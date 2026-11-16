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
/// Hover-intent prewarming. Header hover: realizes Layout Selector cards (at idle) and once per session reads Styles, recent projects
/// and audio devices on the pool to warm files and drivers. Timeline hover, after <see cref="TimelineQuietMs"/> without playback,
/// scrub or drag: realizes timeline tools and decodes the Cutout Studio's opening still on the pool.
/// </summary>
/// <remarks>
/// Created after Frame 1 (Law 1); UI thread only. The warm still is a single slot (Media Foundation, ~8 MB max) holding the decode
/// task, running or done, so a Studio opened mid-decode awaits it rather than starting over; a newer target replaces the slot.
/// </remarks>
internal sealed class HeatLampController : IDisposable
{
    /// <summary>Quiet hover time over the Timeline before prewarming (ms).</summary>
    internal const int TimelineQuietMs = 250;

    /// <summary>Max decode height of the warm Cutout Studio still (source height if smaller).</summary>
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

    // Single warm still slot: the decode for the latest target (file, time), running or done; null result when it failed.
    private (string Source, double Seconds, Task<MaskDrawWindow.WarmStill?> Still)? _warmCutout;

    /// <param name="viewModel">Window VM (open state, playback, source height).</param>
    /// <param name="header">Editor header; hover warms selector cards and session lists.</param>
    /// <param name="timeline">Timeline panel; quiet hover warms its tools and the Cutout Studio still.</param>
    /// <param name="warmHeaderCards">Realizes Layout Selector cards; invoked at idle while still hovering the header.</param>
    /// <param name="timelineHeld">Whether a scrub or block drag is in progress.</param>
    /// <param name="realizeTimelineTools">Realizes Timeline tools.</param>
    /// <param name="cutoutClip">Selected clip the Cutout Studio would resolve its base clip from (null = video at playhead).</param>
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

    /// <summary>Takes the warm still (decoded or decoding) for an opening Cutout Studio, used only if it matches, and empties the slot.</summary>
    public Task<MaskDrawWindow.WarmStill?>? TakeWarmCutoutStill()
    {
        var warm = _warmCutout?.Still;
        _warmCutout = null;
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

        // Defer to idle, never inside the pointer event (card keys serialize every layout).
        _header.Dispatcher.BeginInvoke(() =>
        {
            if (_header.IsMouseOver)
                _warmHeaderCards();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void Timeline_PointerMoved(object sender, MouseEventArgs e) => _timelineQuiet.Poke();

    private void Timeline_MouseLeave(object sender, MouseEventArgs e) => _timelineQuiet.Cancel();

    /// <summary>Reads Styles, recent projects and audio devices once on the pool to warm files and drivers.</summary>
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

    /// <summary>Quiet Timeline hover: realizes tools and pre-decodes the Cutout Studio still for the selected (or playhead) clip.</summary>
    private void WarmTimeline()
    {
        _realizeTimelineTools();
        if (!_viewModel.HasSource || MaskDrawWindow.StillTarget(_viewModel, _cutoutClip()) is not { } target || !System.IO.File.Exists(target.Source))
            return;
        // Same target already held (a failed decode is tried again).
        if (_warmCutout is { } held && SameStill(held.Source, held.Seconds, target) && !(held.Still.IsCompletedSuccessfully && held.Still.Result is null))
            return;

        var height = Math.Min(_viewModel.SourceHeight > 0 ? (int)_viewModel.SourceHeight : WarmStillMostHeight, WarmStillMostHeight);
        _warmCutout = (target.Source, target.Seconds, Task.Run(() =>
        {
            try
            {
                // Frozen by the decoder, so the UI thread can show it.
                return MfPoster.Decode(target.Source, target.Seconds, Math.Max(height, 16)) is { } picture
                    ? new MaskDrawWindow.WarmStill(target.Source, target.Seconds, picture)
                    : null;
            }
            catch (Exception ex) when (ex is GpuException or InvalidOperationException or System.IO.IOException or ArgumentException)
            {
                AppLog.Write("Heat lamp: the Cutout Studio's picture could not be decoded ahead", ex);
                return null;
            }
        }));

        static bool SameStill(string source, double seconds, (string Source, double Seconds) other) =>
            string.Equals(source, other.Source, StringComparison.OrdinalIgnoreCase) && Math.Abs(seconds - other.Seconds) < 0.001;
    }
}
