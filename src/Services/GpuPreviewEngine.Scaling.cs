using System.Diagnostics;

namespace HandPegApp.Services;

// ----- Dynamic Preview Scaling -----
// The player renders at viewport pixels. With AppSettings.DynamicPreviewScaling, play/scrub frames may render smaller and be upscaled
// by the worker's final pass (FrameSceneHeader.PreviewScale, SceneRenderer.DrawScale). Steps (StepScale): 0 full; 1 "1:1 DIP" (viewport
// pixels / GpuViewport.MonitorScale, capped by ShortSideCap and Sequence pixels: DipScale); 2 and 3 are 3/4 and 1/2 of step 1. Runs
// start at step 1 on battery or unified memory with multiple tabs, else full (StartStep), switching immediately if that changes
// mid-run. Pass 5.6.4A: a frame is slow only by the worker's shader draw time (SlowByDraw: LastDrawMs over SlowDrawMs, or over
// BigCardDrawMs on a big card on AC power), never by acquire time or lateness (a decoder lane seeking); a late frame is judged by its draw
// time as it arrives. Three slow frames in a tumbling StepWindow step down (Counted, NextStep, skipping no-op steps); RecoverAfter on-time
// frames in a row step back up (Recovered, PrevStep: 0.5 -> 0.75 -> 1.0, never above the start step). Pausing (immediately), a scrub's
// release or a ghost pause (_scaleResetOwed, at the pump's next turn) or CrispAfter past the last scrub restores full resolution and resets
// the run. Pump thread only.
public sealed partial class GpuPreviewEngine
{
    /// <summary>Worker draw time (acquire, draw, flush; ms) above which a frame counts as slow.</summary>
    public const double SlowDrawMs = 12.0;

    /// <summary>How long after the last scrub move the paused picture is drawn whole again (seconds).</summary>
    public const double CrispAfter = 0.080;

    /// <summary>Step 1 short-side pixel cap (e.g. a 9:16 Sequence at most 720 x 1280).</summary>
    internal const int ShortSideCap = 720;

    /// <summary>Window length, slow-frame threshold per window, and deepest step.</summary>
    internal const int StepWindow = 30, SlowToStep = 3, DeepestStep = 3;

    /// <summary>The smallest a frame is drawn, as a fraction of the viewport's pixels each way.</summary>
    internal const float LeastScale = 0.25f;

    // The least time between two steps (the new size settles), seconds.
    private const double StepSettle = 0.5;

    // The step now, and what decides the next one: the window's frame and slow counts (down), the on-time streak (back up) and the run's
    // start step now (the highest it may recover to).
    private int _scaleStep, _startStep;
    private int _framesCounted, _slowCounted, _onTimeCounted;
    private long _stepTicks, _countedFrames = -1;
    private bool _scaleActive, _lateOwed;
    private float _lastScale = 1;

    // Run reference sizes (viewport, Sequence, monitor scale) and run statistics for the log line (late: missed a refresh, judged by draw
    // time; recovered: steps back up).
    private (int ViewWidth, int ViewHeight, int CanvasWidth, int CanvasHeight, double MonitorScale) _scaleFor;
    private int _runStart, _runDeepest, _runFrames, _runSlow, _runLate, _runRecovered;
    private bool _runPlayed;

    // Stopwatch ticks of the last Seek within ScrubbingFor of the previous one (a drag, not a click); written by Seek.
    private long _scrubMovedTicks;

    // On battery, asked again every few seconds (GetSystemPowerStatus is cheap, but not every frame); with it, whether the compositor's
    // card is big (MemoryTier.IsBigCard) while on AC power: its frames are slow only past BigCardDrawMs of shader draw (SlowByDraw).
    private bool _onBattery, _bigCardOnAc;
    private long _batteryTicks;

    /// <summary>The scale for the frame drawn now (1: whole). Pump thread.</summary>
    private float PreviewScaleFor(bool playing, int width, int height, int canvasWidth, int canvasHeight)
    {
        // Paused frames, scrubs included, are always whole: a scrub's slow frames are decoder seeks, not GPU load.
        if (!AppSettings.Current.DynamicPreviewScaling || _sourceView || !playing)
        {
            EndScaledRun();
            return _lastScale = 1;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _batteryTicks > 5 * Stopwatch.Frequency)
        {
            (_onBattery, _batteryTicks) = (PowerStatus.OnBattery, now);
            _bigCardOnAc = !_onBattery && CompositorCardIsBig();
        }

        // Re-evaluated every frame: monitor scale, tab count and power source can change mid-run.
        _scaleFor = (width, height, canvasWidth, canvasHeight, _viewport.MonitorScale);
        var start = StartStep(_onBattery, UnifiedMemory, ProjectTabs.OpenCount);
        _startStep = start;
        if (!_scaleActive)
        {
            (_scaleActive, _scaleStep, _framesCounted, _slowCounted, _onTimeCounted, _stepTicks, _lateOwed) = (true, start, 0, 0, 0, now, false);
            (_runStart, _runDeepest, _runFrames, _runSlow, _runLate, _runRecovered, _runPlayed) = (start, start, 0, 0, 0, 0, false);
            _countedFrames = Volatile.Read(ref _client.Memory.Control.FramesRendered);
        }
        else
        {
            // Never above the start step while it runs (a second tab opened, the charger pulled); recovery (CountFrame) climbs back to
            // it at most.
            if (start > _scaleStep)
                (_scaleStep, _stepTicks, _runDeepest, _onTimeCounted) = (start, now, Math.Max(_runDeepest, start), 0);
            CountFrame(late: false);
        }

        _runPlayed |= playing;
        return _lastScale = StepScale(_scaleStep, width, height, canvasWidth, canvasHeight, _scaleFor.MonitorScale);
    }

    /// <summary>
    /// Counts the newest worker frame once, by its shader draw time alone (<see cref="SlowByDraw"/>), never by acquire time or lateness: a
    /// frame that missed its refresh (<paramref name="late"/>, often a decoder lane seeking) is noted and judged by its draw time as it
    /// arrives, once. Enough slow frames per window step down (NextStep); <see cref="RecoverAfter"/> on-time frames in a row step back up
    /// (PrevStep, never above the start step). Pump thread.
    /// </summary>
    private void CountFrame(bool late)
    {
        if (!_scaleActive)
            return;
        ref var control = ref _client.Memory.Control;
        var frames = Volatile.Read(ref control.FramesRendered);
        if (late)
        {
            // Not slow by itself: the frame is judged by its own draw time when it arrives (the newest drawn one is not counted again).
            (_countedFrames, _lateOwed) = (frames, true);
            return;
        }

        if (frames == _countedFrames)
            return;
        _countedFrames = frames;
        if (_lateOwed)
        {
            _lateOwed = false;
            _runLate++;
        }

        var slow = SlowByDraw(control.LastDrawMs, _bigCardOnAc);
        _runFrames++;
        if (slow)
            _runSlow++;

        var now = Stopwatch.GetTimestamp();
        var settled = now - _stepTicks > StepSettle * Stopwatch.Frequency;
        var (width, height, canvasWidth, canvasHeight, monitorScale) = _scaleFor;

        // Back up after a streak of on-time frames: a momentary hitch never degrades the rest of the play.
        (_onTimeCounted, var up) = Recovered(_onTimeCounted, slow, _scaleStep, _startStep);
        if (up)
        {
            // The streak itself is the settle for recovery (StepSettle only paces steps down): at 120 fps, 30 frames are 0.25 s.
            var higher = PrevStep(_scaleStep, _startStep, width, height, canvasWidth, canvasHeight, monitorScale);
            if (higher != _scaleStep)
                (_scaleStep, _stepTicks, _framesCounted, _slowCounted, _runRecovered) = (higher, now, 0, 0, _runRecovered + 1);
            return;
        }

        (_framesCounted, _slowCounted, var step) = Counted(_framesCounted, _slowCounted, slow, settled);
        if (!step)
            return;
        var next = NextStep(_scaleStep, width, height, canvasWidth, canvasHeight, monitorScale);
        if (next != _scaleStep)
            (_scaleStep, _stepTicks, _runDeepest, _onTimeCounted) = (next, now, Math.Max(_runDeepest, next), 0);
    }

    /// <summary>
    /// True when the compositor's card is big (MemoryTier.IsBigCard: not WARP, not unified memory, over 4 GB of its own). Read from the
    /// adapter list only once it exists (listing loads drivers); false while unknown. Pump thread, with the battery check.
    /// </summary>
    private bool CompositorCardIsBig() =>
        GpuAdapters.IsListed && GpuAdapters.CompositorAdapter is { } card
        && HandPegApp.Compositor.MemoryTier.IsBigCard(Volatile.Read(ref _client.Memory.Control.IsSoftwareDevice) != 0, UnifiedMemory, card.DedicatedVideoMemoryMb * 1024 * 1024);

    /// <summary>
    /// Ends a scaling run (pause, scrub end, setting off) and resets state; played runs log frames, slow frames, start and deepest step.
    /// Pump thread.
    /// </summary>
    private void EndScaledRun()
    {
        if (!_scaleActive)
            return;
        _scaleActive = false;
        if (!_runPlayed || _runFrames == 0)
            return;

        var (width, height, canvasWidth, canvasHeight, monitorScale) = _scaleFor;
        AppLog.Write(FormattableString.Invariant(
            $"Dynamic Preview Scaling: a play of {_runFrames} frames, {_runSlow} slow by draw time{(_bigCardOnAc ? $" (big card: over {BigCardDrawMs:0.#} ms)" : "")}, {_runLate} late (not counted), {_runRecovered} step(s) back up; step {_runStart} ({StepScale(_runStart, width, height, canvasWidth, canvasHeight, monitorScale):0.00}) at the start, step {_runDeepest} ({StepScale(_runDeepest, width, height, canvasWidth, canvasHeight, monitorScale):0.00}) at the deepest; {width} x {height} pixels, monitor scale {monitorScale:0.##}, {ProjectTabs.OpenCount} tab(s){(UnifiedMemory ? ", shared memory" : "")}{(_onBattery ? ", on battery" : "")}"));
    }

    /// <summary>True when a paused frame was drawn scaled and the scrub ended, so it must be redrawn full. Pump thread.</summary>
    private bool CrispOwed() => !_playing && _lastScale < 1 && Stopwatch.GetElapsedTime(Volatile.Read(ref _scrubMovedTicks)).TotalSeconds >= CrispAfter;

    // ----- Pure rules (golden dynamic-preview-scaling) -----

    /// <summary>Starting step: 1 ("1:1 DIP") on battery or unified memory with multiple tabs, else 0 (full).</summary>
    internal static int StartStep(bool onBattery, bool unifiedMemory, int openTabs) => onBattery || (unifiedMemory && openTabs > 1) ? 1 : 0;

    /// <summary>
    /// Step 1 per-axis fraction of viewport pixels: min of 1, 1 / monitor scale, <see cref="ShortSideCap"/> / letterboxed short side, and
    /// Sequence pixels / viewport (never exceeding the Sequence resolution).
    /// </summary>
    internal static double DipScale(int viewWidth, int viewHeight, int canvasWidth, int canvasHeight, double monitorScale)
    {
        if (viewWidth <= 0 || viewHeight <= 0)
            return 1;
        var (sequenceWidth, sequenceHeight) = (Math.Max(canvasWidth, 1), Math.Max(canvasHeight, 1));
        var fit = Math.Min((double)viewWidth / sequenceWidth, (double)viewHeight / sequenceHeight);
        var shortSide = Math.Min(sequenceWidth, sequenceHeight) * fit;
        var dip = monitorScale > 1 && double.IsFinite(monitorScale) ? 1 / monitorScale : 1;
        var capped = shortSide > ShortSideCap ? ShortSideCap / shortSide : 1;
        var own = fit > 1 ? 1 / fit : 1;
        return Math.Min(Math.Min(dip, capped), own);
    }

    /// <summary>Step scale (0 full; 1 <see cref="DipScale"/>; 2 = 3/4 of it; 3 = 1/2), clamped to [<see cref="LeastScale"/>, 1].</summary>
    internal static float StepScale(int step, int viewWidth, int viewHeight, int canvasWidth, int canvasHeight, double monitorScale)
    {
        if (step <= 0)
            return 1;
        var dip = DipScale(viewWidth, viewHeight, canvasWidth, canvasHeight, monitorScale);
        var scale = step switch
        {
            1 => dip,
            2 => dip * 0.75,
            _ => dip * 0.5,
        };
        return (float)Math.Max(LeastScale, Math.Min(scale, 1));
    }

    /// <summary>Next lower step that actually reduces pixels (skipping no-ops); <paramref name="step"/> itself if none.</summary>
    internal static int NextStep(int step, int viewWidth, int viewHeight, int canvasWidth, int canvasHeight, double monitorScale)
    {
        var scale = StepScale(step, viewWidth, viewHeight, canvasWidth, canvasHeight, monitorScale);
        for (var next = Math.Max(step, 0) + 1; next <= DeepestStep; next++)
        {
            if (StepScale(next, viewWidth, viewHeight, canvasWidth, canvasHeight, monitorScale) < scale - 0.001f)
                return next;
        }

        return step;
    }

    /// <summary>
    /// Tumbling-window update: returns new frame and slow counts and whether to step down (<see cref="SlowToStep"/> slow frames and
    /// <paramref name="settled"/>). A step or a full window resets the counts.
    /// </summary>
    internal static (int Frames, int Slow, bool Step) Counted(int frames, int slow, bool slowFrame, bool settled)
    {
        frames++;
        if (slowFrame)
            slow++;
        if (slow >= SlowToStep && settled)
            return (0, 0, true);
        return frames >= StepWindow ? (0, 0, false) : (frames, slow, false);
    }
}
