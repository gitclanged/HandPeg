using System.Diagnostics;

namespace HandPegApp.Services;

// ----- Dynamic Preview Scaling (Pass 5.4.3, 5.4.4) -----
// The player draws at the viewport's own pixels. With Settings' Dynamic Preview Scaling on (AppSettings.DynamicPreviewScaling), a frame
// drawn while playing or scrubbing may be drawn smaller and stretched over the viewport by the worker's final pass
// (FrameSceneHeader.PreviewScale, SceneRenderer.DrawScale). The steps (StepScale): 0 whole; 1 "1:1 DIP" (5.4.4), the viewport's pixels
// over Windows' scale of the monitor the player is on (GpuViewportControl.MonitorScale: a quarter of them at 200 %), never more than
// ShortSideCap pixels on the picture's short side nor more than the Sequence's own pixels (DipScale); 2 and 3 three quarters and half of
// step 1. A run starts at step 1 on battery, or on a card that shares the PC's memory while more than one project tab is open, else
// whole (StartStep); a second tab or the charger pulled mid-run takes it there at once. Three slow frames of a tumbling StepWindow step
// it down (Counted, NextStep: a step that would change nothing is passed over); it never steps up while it runs. Paused (at once), and
// CrispAfter after the last scrub move, the frame is drawn whole again and the next run starts afresh. Pump thread only.
public sealed partial class GpuPreviewEngine
{
    /// <summary>A frame that took longer than this to draw (the worker's acquire, draw and flush, ms) counts as one dropped.</summary>
    public const double SlowDrawMs = 12.0;

    /// <summary>How long after the last scrub move the paused picture is drawn whole again (seconds).</summary>
    public const double CrispAfter = 0.080;

    /// <summary>The most pixels step 1 keeps on the picture's short side (5.4.4: a 9:16 Sequence at most 720 x 1280).</summary>
    internal const int ShortSideCap = 720;

    /// <summary>How many frames a window of the count is, how many slow ones in it step the scale down, and the deepest step.</summary>
    internal const int StepWindow = 30, SlowToStep = 3, DeepestStep = 3;

    /// <summary>The smallest a frame is drawn, as a fraction of the viewport's pixels each way.</summary>
    internal const float LeastScale = 0.25f;

    // The least time between two steps (the new size settles), seconds.
    private const double StepSettle = 0.5;

    // The step now, and what decides the next one.
    private int _scaleStep;
    private int _framesCounted, _slowCounted;
    private long _stepTicks, _countedFrames = -1;
    private bool _scaleActive, _lateOwed;
    private float _lastScale = 1;

    // What the run's steps are measured against (the viewport, the Sequence, the monitor's scale), and what the run did, for its log line.
    private (int ViewWidth, int ViewHeight, int CanvasWidth, int CanvasHeight, double MonitorScale) _scaleFor;
    private int _runStart, _runDeepest, _runFrames, _runSlow;
    private bool _runPlayed;

    // The last Seek that followed another within ScrubbingFor (a drag's move, not a click): Stopwatch ticks, written by Seek.
    private long _scrubMovedTicks;

    // On battery, asked again every few seconds (GetSystemPowerStatus is cheap, but not every frame).
    private bool _onBattery;
    private long _batteryTicks;

    /// <summary>The scale for the frame drawn now (1: whole). Pump thread.</summary>
    private float PreviewScaleFor(bool playing, int width, int height, int canvasWidth, int canvasHeight)
    {
        var scrubbing = !playing && Stopwatch.GetElapsedTime(Volatile.Read(ref _scrubMovedTicks)).TotalSeconds < CrispAfter;
        if (!AppSettings.Current.DynamicPreviewScaling || _sourceView || !(playing || scrubbing))
        {
            EndScaledRun();
            return _lastScale = 1;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _batteryTicks > 5 * Stopwatch.Frequency)
            (_onBattery, _batteryTicks) = (PowerStatus.OnBattery, now);

        // Asked every frame (5.4.4): the monitor's scale can change under a fullscreen player, the tabs and the charger mid-run.
        _scaleFor = (width, height, canvasWidth, canvasHeight, _viewport.MonitorScale);
        var start = StartStep(_onBattery, UnifiedMemory, ProjectTabs.OpenCount);
        if (!_scaleActive)
        {
            (_scaleActive, _scaleStep, _framesCounted, _slowCounted, _stepTicks, _lateOwed) = (true, start, 0, 0, now, false);
            (_runStart, _runDeepest, _runFrames, _runSlow, _runPlayed) = (start, start, 0, 0, false);
            _countedFrames = Volatile.Read(ref _client.Memory.Control.FramesRendered);
        }
        else
        {
            // Never above the start step while it runs (a second tab opened, the charger pulled), and never back up.
            if (start > _scaleStep)
                (_scaleStep, _stepTicks, _runDeepest) = (start, now, Math.Max(_runDeepest, start));
            CountFrame(late: false);
        }

        _runPlayed |= playing;
        return _lastScale = StepScale(_scaleStep, width, height, canvasWidth, canvasHeight, _scaleFor.MonitorScale);
    }

    /// <summary>
    /// Counts the worker's newest frame (once): slow when it took more than SlowDrawMs, or <paramref name="late"/> (it did not arrive by
    /// its refresh); enough slow ones in a window step the scale down. A late frame is counted as it is late, not again as it arrives
    /// (5.4.4). Pump thread.
    /// </summary>
    private void CountFrame(bool late)
    {
        if (!_scaleActive)
            return;
        ref var control = ref _client.Memory.Control;
        var frames = Volatile.Read(ref control.FramesRendered);
        if (late)
        {
            (_countedFrames, _lateOwed) = (frames, true);
        }
        else
        {
            if (frames == _countedFrames)
                return;
            _countedFrames = frames;
            if (_lateOwed)
            {
                // The frame that came late, drawn now: counted already.
                _lateOwed = false;
                return;
            }
        }

        var slow = late || control.LastAcquireMs + control.LastDrawMs + control.LastFlushMs > SlowDrawMs;
        _runFrames++;
        if (slow)
            _runSlow++;

        var now = Stopwatch.GetTimestamp();
        (_framesCounted, _slowCounted, var step) = Counted(_framesCounted, _slowCounted, slow, now - _stepTicks > StepSettle * Stopwatch.Frequency);
        if (!step)
            return;
        var (width, height, canvasWidth, canvasHeight, monitorScale) = _scaleFor;
        var next = NextStep(_scaleStep, width, height, canvasWidth, canvasHeight, monitorScale);
        if (next != _scaleStep)
            (_scaleStep, _stepTicks, _runDeepest) = (next, now, Math.Max(_runDeepest, next));
    }

    /// <summary>
    /// A run of scaled frames over (paused, the scrub over, the setting turned off): the next one starts afresh. A run that played logs one
    /// line (5.4.4): its frames, the slow ones, the step it started at and the deepest it reached. Pump thread.
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
            $"Dynamic Preview Scaling: a play of {_runFrames} frames, {_runSlow} slow; step {_runStart} ({StepScale(_runStart, width, height, canvasWidth, canvasHeight, monitorScale):0.00}) at the start, step {_runDeepest} ({StepScale(_runDeepest, width, height, canvasWidth, canvasHeight, monitorScale):0.00}) at the deepest; {width} x {height} pixels, monitor scale {monitorScale:0.##}, {ProjectTabs.OpenCount} tab(s){(UnifiedMemory ? ", shared memory" : "")}{(_onBattery ? ", on battery" : "")}"));
    }

    /// <summary>Whether the paused picture was last drawn smaller and the scrub is over: it is drawn whole again. Pump thread.</summary>
    private bool CrispOwed() => !_playing && _lastScale < 1 && Stopwatch.GetElapsedTime(Volatile.Read(ref _scrubMovedTicks)).TotalSeconds >= CrispAfter;

    // ----- The rules, pure (the golden test dynamic-preview-scaling checks them) -----

    /// <summary>The step a run starts at (5.4.4): 1 ("1:1 DIP") on battery, or on a card that shares the PC's memory with more than one tab open; else 0 (whole).</summary>
    internal static int StartStep(bool onBattery, bool unifiedMemory, int openTabs) => onBattery || (unifiedMemory && openTabs > 1) ? 1 : 0;

    /// <summary>
    /// Step 1 as a fraction of the viewport's pixels each way (5.4.4): the smallest of 1, one over Windows' monitor scale (the viewport's
    /// size in DIPs at that scale), <see cref="ShortSideCap"/> over the short side of the picture as it is letterboxed in the viewport, and
    /// the Sequence's own pixels (a viewport larger than the Sequence never draws more than it has).
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

    /// <summary>A step's scale (0 whole; 1 <see cref="DipScale"/>; 2 three quarters of it; 3 half), never under <see cref="LeastScale"/> nor over 1.</summary>
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

    /// <summary>The next step down from <paramref name="step"/> that draws fewer pixels (a step that would change nothing is passed over); the step itself when none does.</summary>
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
    /// One frame counted in the tumbling window: the frames and slow frames after it, and whether the scale steps down now (<see cref="SlowToStep"/>
    /// slow ones, and <paramref name="settled"/>: the last step long enough ago). A step, or a full window, starts the count again.
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
