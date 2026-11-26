using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Zero-lag scrubbing -----
// Setting MainViewModel.PositionMs on every raw pointer move during a ruler or Overview Scrubber drag ran all its listeners (readout,
// Effects pane, inspector, key lanes, player boxes) before the playhead redrew, so at 500 Hz input the line lagged the pointer. Each
// move now moves the playhead lines immediately, while PositionMs is committed once per frame (ScrubCoalescer, at most 60 Hz) and
// exactly on release. While the drag is held (ScrubGate), the heavy PositionMs listeners (player/Canvas box rebuilds and hit masks, the
// Effects pane's animated values) skip those commits and catch up once on release.
//
// Frame-edge scrubbing (Pass 5.7.1): the ruler stamps its raw moves and samples the live cursor once per rendered frame
// (TimelineParts.cs), and the engine is sought at most once per rendered frame too (ScrubCoalescer.TakeSeek -> GpuPreviewEngine.ScrubSeek,
// which skips a moment already posted), so neither the dispatcher nor the pump is woken at the pointer's rate. Both happen in one ordered
// frame callback (OnScrubFrame: the held ruler's sample, then the seek), so a frame's seek carries that frame's cursor. The drag's first move
// seeks at once; the last owed seek lands on release. A ruler drag leaves the Overview Scrubber's binding off until release
// (FreezeOverviewSlider, MainWindow.Player.cs): no slider layout at the commit rate.
//
// Ghost-pause play-scrubbing (Pass 5.6.4A): a drag while playing holds the transport (GpuPreviewEngine.GhostPause: audio clock held,
// whole single frames, no Dynamic Preview Scaling penalty); a pointer rest of GhostDwell (80 ms, checked by timestamp every frame since
// Pass 5.7.1) plays on from there until the next move; release resumes playback after the release's own seek, with the scaled run ended
// (EndScrubScaling).
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
    /// Scrub move to <paramref name="positionMs"/>: immediately moves the playhead lines (Timeline, rulers, key lanes); the engine is sought
    /// once per rendered frame (<see cref="OnScrubFrame"/>; the drag's first move at once) and PositionMs is committed on the next frame or
    /// on release (<see cref="EndScrub"/>).
    /// </summary>
    private void ScrubPlayheadTo(double positionMs)
    {
        if (_viewModel.DurationMs <= 0)
            return;

        positionMs = Math.Clamp(positionMs, 0, _viewModel.DurationMs);
        var seconds = positionMs / 1000;
        var starting = !ScrubGate.IsHeld;
        ScrubGate.Hold();

        // A ruler (or full-screen) drag: the Overview Scrubber stays where it is until release (its thumb drags scrub it themselves).
        if (starting && !_isScrubbing)
            FreezeOverviewSlider(true);

        // The pointer-rest clock moves only with the target (a still pointer, or a repeat, keeps resting).
        if (!positionMs.Equals(_scrubState.LastTargetMs))
        {
            _scrubState.LastTargetMs = positionMs;
            _scrubState.LastMoveTicks = Stopwatch.GetTimestamp();
            _scrubState.DwellResumed = false;
        }

        UnifiedTimeline.Surface.PlayheadSeconds = seconds;
        foreach (var ruler in _rulers)
            PlaceRulerPlayhead(ruler, seconds);
        KeyLanes.MovePlayhead(seconds);

        // As in FollowPlayheadInPlayer: never leave a tab's paused still over a scrub.
        if (!_switchingTab && !_restoringTabView)
            HideTabStill();

        // Playing: hold the transport first, so the seek lands on a paused engine (whole frames, no scaling penalty).
        GhostPauseForScrub();
        if (_scrubState.FrameHooked)
        {
            // Later moves: the frame's one engine seek (OnScrubFrame, ScrubCoalescer.TakeSeek).
            Scrub.Move(positionMs);
            return;
        }

        // The drag's first move seeks at once (press latency), then hooks the frame loop.
        if (!PlayerAt((long)positionMs))
            ScrubSeek((long)positionMs);
        Scrub.Move(positionMs, sought: true);
        _scrubState.FrameHooked = true;
        CompositionTarget.Rendering += OnScrubFrame;
    }

    /// <summary>Playhead drag released or cancelled: the last owed seek, then the last scrub position committed immediately.</summary>
    private void EndScrub()
    {
        UnhookScrubFrame();
        SeekOwed();
        _scrub?.Release(ScrubNow());
        ReleaseScrubGate();
        EndSlicePreview();
        (_scrubState.LastTargetMs, _scrubState.LastMoveTicks, _scrubState.DwellResumed) = (double.NaN, 0, false);
        (_scrubState.Velocity, _scrubState.VelocityTicks, _scrubState.VelocityFromMs) = (0, 0, double.NaN);

        // Releasing never leaves playback at a scrub's reduced step (Stage 2); a held play resumes after the caller's release seek.
        if (_gpu is { } engine)
        {
            // Caption pre-roll runs once here, never per raw pointer move (ScrubSeek).
            engine.PrepareAround(engine.Position);
            engine.EndScrubScaling();
            if (engine.IsGhostPaused)
                Dispatcher.BeginInvoke(ResumeAfterScrub, DispatcherPriority.Input);
        }
    }

    // The frame's one engine seek: the newest owed target, if any (ScrubCoalescer.TakeSeek).
    private void SeekOwed()
    {
        if (_scrub is { } scrub && scrub.TakeSeek(out var positionMs))
            ScrubSeek(positionMs);
    }

    /// <summary>
    /// A coalesced scrub seek: only the lock-free GpuPreviewEngine.ScrubSeek (nothing when the paused engine already has that moment).
    /// PrepareAround (UI-thread caption rasterization, a couple of ms) runs once on EndScrub and on a GhostDwell resume, never per frame.
    /// </summary>
    private void ScrubSeek(double positionMs)
    {
        if (_gpu is { } engine)
        {
            if (!engine.IsPlaying)
                _pausedOnUsersMoment = true;
            engine.ScrubSeek(positionMs / 1000.0);
        }
        else if (_pendingMedia is { } pending)
        {
            _pendingMedia = ((long)positionMs, pending.Paused);
        }
    }

    private void OnScrubFrame(object? sender, EventArgs e)
    {
        // One ordered callback: the held ruler's live cursor first, so this frame's one engine seek (SeekOwed) is this frame's pointer.
        // A drag that ends inside the sample (Esc, capture lost) was already released by EndScrub.
        SampleScrubRulers(e);
        if (!_scrubState.FrameHooked)
            return;

        var now = Stopwatch.GetTimestamp();
        SeekOwed();
        Scrub.Tick(now * 1000.0 / Stopwatch.Frequency);
        if (Scrub.IsHeld)
        {
            GhostDwellFrame(now);
            SlicePreviewFrame(now);
        }

        // Drag ended without notification and nothing is pending: unhook the frame callback and open the gate.
        if (!Scrub.IsHeld && !Scrub.HasPending)
        {
            UnhookScrubFrame();
            EndSlicePreview();
            if (!_isScrubbing)
                ReleaseScrubGate();
        }
    }

    // The frame sample of a ruler holding a seek drag (TimelineRuler.SampleScrubFrame; its own hook then skips the frame). With no capture
    // drag held (direct SeekRequested callers, goldens) nothing is sampled.
    private void SampleScrubRulers(EventArgs e)
    {
        foreach (var ruler in _rulers)
        {
            if (ruler.IsSampling)
                ruler.SampleScrubFrame(e);
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

    /// <summary>
    /// Opens the scrub gate once: the Overview Scrubber is bound again, listeners catch up (ScrubGate.Released) and the deferred box
    /// rebuild runs.
    /// </summary>
    private void ReleaseScrubGate()
    {
        FreezeOverviewSlider(false);
        if (!ScrubGate.Release())
            return;

        // Held commits skipped the clip/key-lane playhead moves and the key diamonds' repaint (ViewModel_PropertyChanged): catch up once.
        MoveClipPlayheads();
        KeyLanes.MovePlayhead(_viewModel.PositionMs / 1000);
        if (!_viewModel.IsPlaying)
            KeyLanes.InvalidateVisual();
        if (!_scrubState.RedrawOwed)
            return;

        _scrubState.RedrawOwed = false;
        if (_dragHeld)
            _redrawAfterDrag = true;
        else
            RedrawForClipsShown();
    }

    // ----- Ghost-pause play-scrubbing -----

    /// <summary>A scrub move: a playing engine is ghost-paused (lock-free; nothing when already held). The pointer rest is timed by stamp.</summary>
    private void GhostPauseForScrub()
    {
        if (_gpu is { IsPlaying: true } engine)
            engine.GhostPause();
    }

    /// <summary>
    /// Each rendered frame of a held scrub: once the pointer has rested GhostDwell (80 ms) since the last move to a new target, a
    /// ghost-paused play plays on from the scrubbed moment until the next move (then held again by <see cref="GhostPauseForScrub"/>).
    /// </summary>
    private void GhostDwellFrame(long now)
    {
        if (_scrubState.DwellResumed || _gpu is not { IsGhostPaused: true } engine
            || !Shell.ScrubState.DwellDue(now, _scrubState.LastMoveTicks, Stopwatch.Frequency, GpuPreviewEngine.GhostDwell))
        {
            return;
        }

        _scrubState.DwellResumed = true;
        engine.PrepareAround(engine.Position);
        engine.GhostResume();
    }

    /// <summary>After a release (and its exact seek): a ghost-paused play resumes, unless Pause or another scrub came first.</summary>
    private void ResumeAfterScrub()
    {
        if (!IsScrubHeld && _gpu is { IsGhostPaused: true } engine)
            engine.GhostResume();
    }

    // ----- Slice-Preview ribbon (Pass 5.7.1) -----

    /// <summary>Least time, ms, between two asks for new ribbon frames while no strip has answered the last one.</summary>
    private const double SliceAskEveryMs = 50;

    /// <summary>
    /// Each rendered frame of a held scrub: the playhead's speed feeds the ribbon's gate (dormant above 300 DIP/s with no clip lookup;
    /// shown below 250 DIP/s held 120 ms over a video clip, user decision Q3); while shown it is placed at the playhead, asks the engine
    /// for frames when the centre frame, spacing or slots change, and takes a strip that came back.
    /// </summary>
    private void SlicePreviewFrame(long now)
    {
        var state = _scrubState;
        var surface = UnifiedTimeline.Surface;
        var pixelsPerSecond = surface.PixelsPerSecond;
        var at = state.LastTargetMs;
        if (!double.IsNaN(state.VelocityFromMs) && state.VelocityTicks != 0)
        {
            state.Velocity = Shell.ScrubState.NextVelocity(state.Velocity, (at - state.VelocityFromMs) / 1000 * pixelsPerSecond,
                (now - state.VelocityTicks) / (double)Stopwatch.Frequency);
        }

        (state.VelocityFromMs, state.VelocityTicks) = (at, now);
        var seconds = at / 1000;
        var document = _viewModel.Document;
        var overVideo = state.Velocity <= SlicePreviewRequest.DormantAbove && double.IsFinite(seconds) && _gpu is { IsPlaying: false, IsExploded: false }
            && SlicePreviewPlanner.VideoClipAt(document, seconds) is not null;
        if (state.SliceGate.Update(state.Velocity, now * 1000.0 / Stopwatch.Frequency, overVideo) != SlicePreviewPhase.Shown || _gpu is not { } engine)
        {
            HideSlicePreview();
            return;
        }

        var ribbon = UnifiedTimeline.SlicePreview;
        var aspect = document.NaturalWidth > 0 && document.NaturalHeight > 0 ? document.NaturalWidth / (double)document.NaturalHeight : 16.0 / 9;
        var cellWidth = SlicePreviewPlanner.CellHeight * Math.Clamp(aspect, 0.25, 4);
        var fps = document.FrameRate.IsValid ? document.FrameRate.Fps : 30;
        var spacing = SlicePreviewPlanner.SpacingFor(pixelsPerSecond, fps);
        var slots = SlicePreviewPlanner.SlotCountFor(surface.TimeAreaWidth, cellWidth);
        var shortSide = (int)Math.Round(SlicePreviewPlanner.CellHeight * VisualTreeHelper.GetDpi(ribbon).DpiScaleY);
        ribbon.Present(slots, cellWidth, spacing);
        UnifiedTimeline.PlaceSlicePreview(seconds);
        ribbon.Show(true);

        // New frames when what they show moved (the centre frame, spacing or slots), paced while no strip has answered the last ask.
        if (SlicePreviewPlanner.Plan(document, seconds, spacing, slots, shortSide) is { } request)
        {
            var key = (request.Path, request.Stream, (long)Math.Round(seconds * fps), spacing.Seconds, request.Slots.Count);
            var nowMs = now * 1000.0 / Stopwatch.Frequency;
            if (key != state.SliceKey && (state.SliceAnswered || nowMs - state.SliceAskedAt * 1000.0 / Stopwatch.Frequency >= SliceAskEveryMs))
            {
                (state.SliceKey, state.SliceAskedAt, state.SliceAnswered) = (key, now, false);
                state.SliceTicket = engine.RequestSlicePreview(request, aspect);
            }
        }

        if (engine.TakeSlicePreview() is { } strip && strip.Request.Path == state.SliceKey.Path && strip.Request.Slots.Count > 0)
        {
            state.SliceAnswered |= strip.Ticket == state.SliceTicket;
            ribbon.SetStrip(strip.Pixels, strip.Width, strip.Height, strip.Request.Slots[0].Offset, strip.Request.Slots.Count);
        }
    }

    /// <summary>Fades the ribbon out and lets the engine's burst go (once); the gate stays where it is.</summary>
    private void HideSlicePreview()
    {
        UnifiedTimeline.SlicePreview.Show(false);
        if (_scrubState.SliceTicket == 0)
            return;

        _scrubState.SliceTicket = 0;
        _scrubState.SliceKey = default;
        _gpu?.RequestSlicePreview(null);
    }

    /// <summary>The drag ended: the ribbon sleeps and fades out, its burst withdrawn.</summary>
    private void EndSlicePreview()
    {
        _scrubState.SliceGate.Sleep();
        HideSlicePreview();
    }
}
