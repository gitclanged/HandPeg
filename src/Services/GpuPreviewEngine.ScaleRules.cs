namespace HandPegApp.Services;

// ----- Dynamic Preview Scaling: recovery and the draw-time gate (Pass 5.6.4A contract) -----
// Pure rules beside the run logic in GpuPreviewEngine.Scaling.cs (golden wave-contract-5-6-4a):
// - SlowByDraw: a playing frame is slow only by the worker's GPU shader draw time (LastDrawMs), never by lateness or acquire time (a video
//   lane seeking); a big discrete card on AC power keeps whole frames unless the draw itself exceeds BigCardDrawMs.
// - Recovered + PrevStep: RecoverAfter on-time frames in a row step a scaled run back up one real step (0.5 -> 0.75 -> 1.0), never above
//   the run's start step, so a momentary hitch never degrades the rest of a play.
public sealed partial class GpuPreviewEngine
{
    /// <summary>On-time frames in a row after which a scaled run steps back up one step.</summary>
    internal const int RecoverAfter = 30;

    /// <summary>Shader draw time (ms) above which a big card (MemoryTier.IsBigCard) on AC power counts a playing frame as slow.</summary>
    internal const double BigCardDrawMs = 8.0;

    /// <summary>True when a playing frame counts as slow: by shader draw time alone, over <see cref="BigCardDrawMs"/> on a big card on AC
    /// power, else over <see cref="SlowDrawMs"/>.</summary>
    internal static bool SlowByDraw(double lastDrawMs, bool bigCardOnAc) =>
        double.IsFinite(lastDrawMs) && lastDrawMs > (bigCardOnAc ? BigCardDrawMs : SlowDrawMs);

    /// <summary>
    /// On-time streak update for a run at <paramref name="step"/> above its <paramref name="start"/>: returns the new streak and whether to
    /// step up (<see cref="RecoverAfter"/> on-time frames). A slow frame, a step up or a run at its start resets the streak.
    /// </summary>
    internal static (int OnTime, bool StepUp) Recovered(int onTime, bool slowFrame, int step, int start)
    {
        if (slowFrame || step <= Math.Max(start, 0))
            return (0, false);
        onTime++;
        return onTime >= RecoverAfter ? (0, true) : (onTime, false);
    }

    /// <summary>Next higher step that actually adds pixels (skipping no-ops), never above <paramref name="start"/>.</summary>
    internal static int PrevStep(int step, int start, int viewWidth, int viewHeight, int canvasWidth, int canvasHeight, double monitorScale)
    {
        var floor = Math.Clamp(start, 0, DeepestStep);
        var scale = StepScale(step, viewWidth, viewHeight, canvasWidth, canvasHeight, monitorScale);
        for (var next = Math.Min(step, DeepestStep) - 1; next > floor; next--)
        {
            if (StepScale(next, viewWidth, viewHeight, canvasWidth, canvasHeight, monitorScale) > scale + 0.001f)
                return next;
        }

        return Math.Min(floor, Math.Max(step, 0));
    }
}
