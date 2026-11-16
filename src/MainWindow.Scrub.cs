using System.Diagnostics;
using System.Windows.Media;

namespace HandPegApp;

// ----- Zero-lag scrubbing -----
// Setting MainViewModel.PositionMs on every raw pointer move during a ruler or Overview Scrubber drag ran all its listeners (readout,
// Effects pane, inspector, key lanes, player boxes) before the playhead redrew, so at 500 Hz input the line lagged the pointer. Each
// move now moves the playhead lines and seeks the engine immediately, while PositionMs is committed once per frame (ScrubCoalescer,
// at most 60 Hz) and exactly on release.
public partial class MainWindow
{
    private ScrubCoalescer? _scrub;
    private bool _scrubFrameHooked, _scrubFlushing;

    private ScrubCoalescer Scrub => _scrub ??= new ScrubCoalescer(FlushScrub);

    /// <summary>True while a playhead drag is held (Overview Scrubber thumb or a ruler).</summary>
    private bool IsScrubHeld => _isScrubbing || _scrub is { IsHeld: true };

    private static double ScrubNow() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Scrub move to <paramref name="positionMs"/>: immediately moves the playhead lines (Timeline, rulers, key lanes) and seeks the player;
    /// PositionMs is committed on the next frame (<see cref="OnScrubFrame"/>) or on release (<see cref="EndScrub"/>).
    /// </summary>
    private void ScrubPlayheadTo(double positionMs)
    {
        if (_viewModel.DurationMs <= 0)
            return;

        positionMs = Math.Clamp(positionMs, 0, _viewModel.DurationMs);
        var seconds = positionMs / 1000;
        UnifiedTimeline.Surface.PlayheadSeconds = seconds;
        foreach (var ruler in _rulers)
            PlaceRulerPlayhead(ruler, seconds);
        KeyLanes.MovePlayhead(seconds);

        // As in FollowPlayheadInPlayer: never leave a tab's paused still over a scrub.
        if (!_switchingTab && !_restoringTabView)
            HideTabStill();
        if (!PlayerAt((long)positionMs))
            PlayerSeek((long)positionMs);

        Scrub.Move(positionMs);
        if (!_scrubFrameHooked)
        {
            _scrubFrameHooked = true;
            CompositionTarget.Rendering += OnScrubFrame;
        }
    }

    /// <summary>Playhead drag released or cancelled: commits the last scrub position immediately.</summary>
    private void EndScrub()
    {
        UnhookScrubFrame();
        _scrub?.Release(ScrubNow());
    }

    private void OnScrubFrame(object? sender, EventArgs e)
    {
        Scrub.Tick(ScrubNow());

        // Drag ended without notification and nothing is pending: unhook the frame callback.
        if (!Scrub.IsHeld && !Scrub.HasPending)
            UnhookScrubFrame();
    }

    private void UnhookScrubFrame()
    {
        if (!_scrubFrameHooked)
            return;

        _scrubFrameHooked = false;
        CompositionTarget.Rendering -= OnScrubFrame;
    }

    // Commits a scrubbed PositionMs; the player already seeked there (ScrubPlayheadTo), so FollowPlayheadInPlayer skips a second seek.
    private void FlushScrub(double positionMs)
    {
        _scrubFlushing = true;
        try
        {
            _viewModel.PositionMs = positionMs;
        }
        finally
        {
            _scrubFlushing = false;
        }
    }
}
