using System.Diagnostics;
using System.Windows.Media;

namespace HandPegApp;

// ----- Zero-lag scrubbing (Pass 5.4.2B) -----
// A drag of the playhead along the Timeline's ruler or the Sequence Overview Scrubber used to set MainViewModel.PositionMs on every raw
// pointer move, and every listener of it (the readout, the Effects pane, the inspector, the key lanes, the player's boxes) ran before the
// playhead line was drawn again: at a 500 Hz mouse the line trailed the pointer. Now each move slides the lines and seeks the engine at
// once, and the playhead's value follows once per frame (ScrubCoalescer, at most 60 times a second) and exactly as the drag is let go.
public partial class MainWindow
{
    private ScrubCoalescer? _scrub;
    private bool _scrubFrameHooked, _scrubFlushing;

    private ScrubCoalescer Scrub => _scrub ??= new ScrubCoalescer(FlushScrub);

    /// <summary>Whether a drag of the playhead is held: the overview scrubber's (its thumb) or a ruler's.</summary>
    private bool IsScrubHeld => _isScrubbing || _scrub is { IsHeld: true };

    private static double ScrubNow() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// The pointer dragging the playhead reached <paramref name="positionMs"/>: the lines (the Timeline's, the rulers', the key lanes') and the
    /// player go there now; the playhead's value at the next frame (<see cref="OnScrubFrame"/>) or as the drag is let go (<see cref="EndScrub"/>).
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

        // As FollowPlayheadInPlayer: a tab's paused still never stands over a scrub.
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

    /// <summary>The drag of the playhead let go (or taken away): its value is the last moment the pointer reached, now.</summary>
    private void EndScrub()
    {
        UnhookScrubFrame();
        _scrub?.Release(ScrubNow());
    }

    private void OnScrubFrame(object? sender, EventArgs e)
    {
        Scrub.Tick(ScrubNow());

        // A drag that ended without being told (nothing waits): the frame loop is let go.
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

    // The playhead's value from a scrub: the player is there already (ScrubPlayheadTo sent it), so FollowPlayheadInPlayer does not seek again.
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
