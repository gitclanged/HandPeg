using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegApp;

// ----- The heat lamp (Pass 5.4.3): what the pointer is about to open, made ready while it rests near it -----
// Hooked after Frame 1 (InitializeHeatLamp, at idle after ContentRendered), never before (Law 1). Over the editor's header: the Layout
// Selector's cards made ahead (kept until what they show changes), and once a session the lists other windows read (Styles, recent
// projects, audio devices) read on the pool so their files and drivers are warm. Over the Timeline, once it has been quiet for
// TimelineQuietMs (nothing playing, scrubbed or dragged): its tools made, and one picture of the still the Cutout Studio would open on,
// decoded on the pool (Media Foundation, about 8 MB at most: one slot, replaced when the clip or moment changes).
public partial class MainWindow
{
    /// <summary>How long the pointer rests over the Timeline, nothing moving, before its next windows are made ready, ms.</summary>
    private const int TimelineQuietMs = 250;

    /// <summary>The tallest the Cutout Studio's warm picture is decoded (its own height when smaller).</summary>
    private const int WarmStillMostHeight = 1080;

    private DispatcherTimer? _timelineQuiet;
    private bool _heatLampOn, _sessionWarmed;

    // The one warm picture for the Cutout Studio, and the file and moment being decoded now (null: none).
    private MaskDrawWindow.WarmStill? _warmCutoutStill;
    private (string Source, double Seconds)? _warmingCutout;

    /// <summary>The heat lamp's hooks (once, after Frame 1).</summary>
    private void InitializeHeatLamp()
    {
        if (_heatLampOn)
            return;
        _heatLampOn = true;
        EditorHeader.MouseEnter += (_, _) => WarmEditorHeader();
        UnifiedTimelinePanel.MouseEnter += (_, _) => RestartTimelineQuiet();
        UnifiedTimelinePanel.MouseMove += (_, _) => RestartTimelineQuiet();
        UnifiedTimelinePanel.MouseLeave += (_, _) => _timelineQuiet?.Stop();
    }

    /// <summary>The pointer came over the editor's header: the Layout Selector's cards made now (none while it is open), and the session's lists warmed once.</summary>
    private void WarmEditorHeader()
    {
        WarmSessionOnce();

        // At idle, never in the pointer's own event (the cards' key alone serializes every layout).
        Dispatcher.BeginInvoke(() =>
        {
            if (LayoutSelector.IsOpen || !EditorHeader.IsMouseOver)
                return;

            // As the selector will take them as it opens: the arrangement now is the one its cards mark as in use.
            _layoutBeforePreview = _dock.Save();
            MakeLayoutCards();
            MakeThemeCards();
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Styles, recent projects and audio devices read once on the pool (their files and drivers warm for the windows that list them).</summary>
    private void WarmSessionOnce()
    {
        if (_sessionWarmed)
            return;
        _sessionWarmed = true;
        _ = Task.Run(() =>
        {
            try
            {
                _ = StyleFile.All();
                _ = ProjectStore.ListRecent();
                _ = AudioDevices.List();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("Heat lamp: the session's lists could not be read ahead", ex);
            }
        });
    }

    private void RestartTimelineQuiet()
    {
        if (_timelineQuiet is null)
        {
            _timelineQuiet = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(TimelineQuietMs) };
            _timelineQuiet.Tick += (_, _) =>
            {
                _timelineQuiet.Stop();
                if (UnifiedTimelinePanel.IsMouseOver && !_viewModel.IsPlaying && !IsScrubHeld && !TimelineDragHeld && Mouse.LeftButton == MouseButtonState.Released)
                    WarmTimeline();
            };
        }

        _timelineQuiet.Stop();
        _timelineQuiet.Start();
    }

    /// <summary>The Timeline quiet under the pointer: its tools made, and the Cutout Studio's picture for the selected clip (or the one at the playhead) decoded ahead.</summary>
    private void WarmTimeline()
    {
        UnifiedTimeline.RealizeTools();
        if (!_viewModel.HasSource || MaskDrawWindow.StillTarget(_viewModel, CutoutTarget()) is not { } target || !System.IO.File.Exists(target.Source))
            return;
        if (_warmCutoutStill is { } warm && SameStill(warm.Source, warm.Seconds, target) || _warmingCutout is { } busy && SameStill(busy.Source, busy.Seconds, target))
            return;

        _warmingCutout = target;
        var height = Math.Min(_viewModel.SourceHeight > 0 ? (int)_viewModel.SourceHeight : WarmStillMostHeight, WarmStillMostHeight);
        _ = Task.Run(() =>
        {
            System.Windows.Media.Imaging.BitmapSource? picture = null;
            try
            {
                picture = MfPoster.Decode(target.Source, target.Seconds, Math.Max(height, 16));
            }
            catch (Exception ex) when (ex is GpuException or InvalidOperationException or System.IO.IOException or ArgumentException)
            {
                AppLog.Write("Heat lamp: the Cutout Studio's picture could not be decoded ahead: " + ex.Message);
            }

            Dispatcher.BeginInvoke(() =>
            {
                // Only the latest asked for is kept (one slot); an older one that came late is let go.
                if (_warmingCutout is not { } asked || !SameStill(asked.Source, asked.Seconds, target))
                    return;
                _warmingCutout = null;
                if (picture is not null)
                    _warmCutoutStill = new MaskDrawWindow.WarmStill(target.Source, target.Seconds, picture);
            }, DispatcherPriority.Background);
        });

        static bool SameStill(string source, double seconds, (string Source, double Seconds) other) =>
            string.Equals(source, other.Source, StringComparison.OrdinalIgnoreCase) && Math.Abs(seconds - other.Seconds) < 0.001;
    }

    /// <summary>The clip the Cutout Studio would open for from the Timeline: the selected video clip, else (null) the video at the playhead.</summary>
    private Layer? CutoutTarget() => _selectedClip is Layer { IsVideoFile: true } clip ? clip : null;

    /// <summary>The warm picture handed to a Cutout Studio opening now (it uses it only when it is of its own still), and let go here.</summary>
    private MaskDrawWindow.WarmStill? TakeWarmCutoutStill()
    {
        var warm = _warmCutoutStill;
        _warmCutoutStill = null;
        return warm;
    }
}
