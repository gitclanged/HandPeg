using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Zero-lag scrubbing -----
// Setting MainViewModel.PositionMs on every raw pointer move during a ruler or Overview Scrubber drag ran all its listeners (readout,
// Effects pane, inspector, key lanes, player boxes) before the playhead redrew, so at 500 Hz input the line lagged the pointer. Each
// move now moves the playhead lines and seeks the engine immediately (GpuPreviewEngine.Seek is lock-free since Pass 5.6.4A), while
// PositionMs is committed once per frame (ScrubCoalescer, at most 60 Hz) and exactly on release. While the drag is held (ScrubGate),
// the heavy PositionMs listeners (player/Canvas box rebuilds and hit masks, the Effects pane's animated values) skip those commits and
// catch up once on release.
//
// Ghost-pause play-scrubbing (Pass 5.6.4A): a drag while playing holds the transport (GpuPreviewEngine.GhostPause: audio clock held,
// whole single frames, no Dynamic Preview Scaling penalty); a pointer rest of GhostDwell (80 ms) plays on from there until the next move;
// release resumes playback after the release's own seek, with the scaled run ended (EndScrubScaling).
public partial class MainWindow
{
    private ScrubCoalescer? _scrub;
    private readonly Shell.ScrubState _scrubState = new();

    private ScrubCoalescer Scrub => _scrub ??= new ScrubCoalescer(FlushScrub);

    /// <summary>True while a playhead drag is held (Overview Scrubber thumb or a ruler).</summary>
    private bool IsScrubHeld => _isScrubbing || _scrub is { IsHeld: true };

    /// <summary>
    /// True for the whole held scrub (any PositionMs change while the gate is held, a dwell segment's playback included): heavy listeners
    /// defer, and ReleaseScrubGate refreshes them once.
    /// </summary>
    private bool ScrubDefersLayout => ScrubGate.IsHeld;

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
        ScrubGate.Hold();
        UnifiedTimeline.Surface.PlayheadSeconds = seconds;
        foreach (var ruler in _rulers)
            PlaceRulerPlayhead(ruler, seconds);
        KeyLanes.MovePlayhead(seconds);

        // As in FollowPlayheadInPlayer: never leave a tab's paused still over a scrub.
        if (!_switchingTab && !_restoringTabView)
            HideTabStill();

        // Playing: hold the transport first, so this seek lands on a paused engine (whole frames, no scaling penalty).
        GhostPauseForScrub();
        if (!PlayerAt((long)positionMs))
            PlayerSeek((long)positionMs);

        Scrub.Move(positionMs);
        if (!_scrubState.FrameHooked)
        {
            _scrubState.FrameHooked = true;
            CompositionTarget.Rendering += OnScrubFrame;
        }
    }

    /// <summary>Playhead drag released or cancelled: commits the last scrub position immediately.</summary>
    private void EndScrub()
    {
        UnhookScrubFrame();
        _scrub?.Release(ScrubNow());
        ReleaseScrubGate();

        // Releasing never leaves playback at a scrub's reduced step (Stage 2); a held play resumes after the caller's release seek.
        _scrubState.GhostDwell?.Stop();
        if (_gpu is { } engine)
        {
            engine.EndScrubScaling();
            if (engine.IsGhostPaused)
                Dispatcher.BeginInvoke(ResumeAfterScrub, DispatcherPriority.Input);
        }
    }

    private void OnScrubFrame(object? sender, EventArgs e)
    {
        Scrub.Tick(ScrubNow());

        // Drag ended without notification and nothing is pending: unhook the frame callback and open the gate.
        if (!Scrub.IsHeld && !Scrub.HasPending)
        {
            UnhookScrubFrame();
            if (!_isScrubbing)
                ReleaseScrubGate();
        }
    }

    private void UnhookScrubFrame()
    {
        if (!_scrubState.FrameHooked)
            return;

        _scrubState.FrameHooked = false;
        CompositionTarget.Rendering -= OnScrubFrame;
    }

    // Commits a scrubbed PositionMs; the player already seeked there (ScrubPlayheadTo), so FollowPlayheadInPlayer skips a second seek.
    private void FlushScrub(double positionMs)
    {
        _scrubState.Flushing = true;
        try
        {
            _viewModel.PositionMs = positionMs;
        }
        finally
        {
            _scrubState.Flushing = false;
        }
    }

    /// <summary>Opens the scrub gate once: listeners catch up (ScrubGate.Released) and the deferred box rebuild runs.</summary>
    private void ReleaseScrubGate()
    {
        if (!ScrubGate.Release() || !_scrubState.RedrawOwed)
            return;

        _scrubState.RedrawOwed = false;
        if (_dragHeld)
            _redrawAfterDrag = true;
        else
            RedrawForClipsShown();
    }

    // ----- Ghost-pause play-scrubbing -----

    /// <summary>A scrub move: a playing engine is ghost-paused; a held one restarts its pointer-rest timer.</summary>
    private void GhostPauseForScrub()
    {
        if (_gpu is not { } engine)
            return;

        if (engine.IsPlaying)
            engine.GhostPause();
        if (!engine.IsGhostPaused)
            return;

        _scrubState.GhostDwell ??= NewGhostDwell();
        _scrubState.GhostDwell.Stop();
        _scrubState.GhostDwell.Start();
    }

    private DispatcherTimer NewGhostDwell()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromSeconds(GpuPreviewEngine.GhostDwell) };
        timer.Tick += (_, _) =>
        {
            // The pointer rested while the drag is still held: play on from the scrubbed moment until the next move.
            timer.Stop();
            if (IsScrubHeld && _gpu is { IsGhostPaused: true } engine)
                engine.GhostResume();
        };
        return timer;
    }

    /// <summary>After a release (and its exact seek): a ghost-paused play resumes, unless Pause or another scrub came first.</summary>
    private void ResumeAfterScrub()
    {
        if (!IsScrubHeld && _gpu is { IsGhostPaused: true } engine)
            engine.GhostResume();
    }
}
