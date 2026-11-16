using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;
using HandPegApp.Controls;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.ViewModels;

namespace HandPegApp.Shell;

/// <summary>
/// Makes ready what the pointer is about to open while it rests near it. Over the editor's header: the Layout Selector's cards (made
/// by the window at idle) and, once a session, the lists other windows read (Styles, recent projects, audio devices), read on the pool
/// so their files and drivers are warm. Over the Timeline, after <see cref="TimelineQuietMs"/> with nothing playing, scrubbed or
/// dragged: its tools made, and one picture of the still the Cutout Studio would open on, decoded on the pool.
/// </summary>
/// <remarks>
/// Made after Frame 1 (Law 1) and used on the window's thread only. The warm picture is one slot (Media Foundation, about 8 MB at most):
/// a newer target replaces it and a decode that finishes late for an older target is dropped.
/// </remarks>
internal sealed class HeatLampController : IDisposable
{
    /// <summary>How long the pointer rests over the Timeline, nothing moving, before its next windows are made ready, ms.</summary>
    internal const int TimelineQuietMs = 250;

    /// <summary>The tallest the Cutout Studio's warm picture is decoded (its own height when smaller).</summary>
    internal const int WarmStillMostHeight = 1080;

    private readonly MainViewModel _viewModel;
    private readonly UIElement _header, _timeline;
    private readonly Action _warmHeaderCards;
    private readonly Func<bool> _timelineHeld;
    private readonly Action _realizeTimelineTools;
    private readonly Func<Layer?> _cutoutClip;
    private readonly TimerScope _timers = new();
    private readonly Debounce _timelineQuiet;
    private bool _sessionWarmed;

    // The one warm picture for the Cutout Studio, and the file and moment being decoded now (null: none).
    private MaskDrawWindow.WarmStill? _warmCutoutStill;
    private (string Source, double Seconds)? _warmingCutout;

    /// <param name="viewModel">The window's view model (what is open, playing, the source's height).</param>
    /// <param name="header">The editor's header: resting on it warms the selector's cards and the session's lists.</param>
    /// <param name="timeline">The Timeline's panel: resting on it, quiet, warms its tools and the Cutout Studio's picture.</param>
    /// <param name="warmHeaderCards">Makes the Layout Selector's cards; called at idle while the pointer is still on the header.</param>
    /// <param name="timelineHeld">Whether the Timeline is scrubbed or a block dragged now.</param>
    /// <param name="realizeTimelineTools">Makes the Timeline's tools.</param>
    /// <param name="cutoutClip">The clip the Cutout Studio would open for (null: the video at the playhead).</param>
    public HeatLampController(MainViewModel viewModel, UIElement header, UIElement timeline, Action warmHeaderCards, Func<bool> timelineHeld,
        Action realizeTimelineTools, Func<Layer?> cutoutClip)
    {
        (_viewModel, _header, _timeline, _warmHeaderCards, _timelineHeld, _realizeTimelineTools, _cutoutClip) =
            (viewModel, header, timeline, warmHeaderCards, timelineHeld, realizeTimelineTools, cutoutClip);
        _timelineQuiet = new Debounce(TimeSpan.FromMilliseconds(TimelineQuietMs), OnTimelineQuiet, DispatcherPriority.Background, _timers);
        header.MouseEnter += Header_MouseEnter;
        timeline.MouseEnter += Timeline_PointerMoved;
        timeline.MouseMove += Timeline_PointerMoved;
        timeline.MouseLeave += Timeline_MouseLeave;
    }

    /// <summary>The warm picture for a Cutout Studio opening now (it uses it only when it is of its own still); the slot is emptied.</summary>
    public MaskDrawWindow.WarmStill? TakeWarmCutoutStill()
    {
        var warm = _warmCutoutStill;
        _warmCutoutStill = null;
        return warm;
    }

    public void Dispose()
    {
        _header.MouseEnter -= Header_MouseEnter;
        _timeline.MouseEnter -= Timeline_PointerMoved;
        _timeline.MouseMove -= Timeline_PointerMoved;
        _timeline.MouseLeave -= Timeline_MouseLeave;
        _timers.Dispose();
    }

    private void Header_MouseEnter(object sender, MouseEventArgs e)
    {
        WarmSessionOnce();

        // At idle, never in the pointer's own event (the cards' key serializes every layout).
        _header.Dispatcher.BeginInvoke(() =>
        {
            if (_header.IsMouseOver)
                _warmHeaderCards();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void Timeline_PointerMoved(object sender, MouseEventArgs e) => _timelineQuiet.Poke();

    private void Timeline_MouseLeave(object sender, MouseEventArgs e) => _timelineQuiet.Cancel();

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

    private void OnTimelineQuiet()
    {
        if (_timeline.IsMouseOver && !_viewModel.IsPlaying && !_timelineHeld() && Mouse.LeftButton == MouseButtonState.Released)
            WarmTimeline();
    }

    /// <summary>The Timeline quiet under the pointer: its tools made, and the Cutout Studio's picture for the selected clip (or the one at the playhead) decoded ahead.</summary>
    private void WarmTimeline()
    {
        _realizeTimelineTools();
        if (!_viewModel.HasSource || MaskDrawWindow.StillTarget(_viewModel, _cutoutClip()) is not { } target || !System.IO.File.Exists(target.Source))
            return;
        if (_warmCutoutStill is { } warm && SameStill(warm.Source, warm.Seconds, target) || _warmingCutout is { } busy && SameStill(busy.Source, busy.Seconds, target))
            return;

        _warmingCutout = target;
        var height = Math.Min(_viewModel.SourceHeight > 0 ? (int)_viewModel.SourceHeight : WarmStillMostHeight, WarmStillMostHeight);
        var dispatcher = _timeline.Dispatcher;
        _ = Task.Run(() =>
        {
            System.Windows.Media.Imaging.BitmapSource? picture = null;
            try
            {
                picture = MfPoster.Decode(target.Source, target.Seconds, Math.Max(height, 16));
            }
            catch (Exception ex) when (ex is GpuException or InvalidOperationException or System.IO.IOException or ArgumentException)
            {
                AppLog.Write("Heat lamp: the Cutout Studio's picture could not be decoded ahead", ex);
            }

            dispatcher.BeginInvoke(() =>
            {
                // Only the latest target is kept (one slot); a decode for an older one is dropped.
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
}
