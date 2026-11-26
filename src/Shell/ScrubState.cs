using System.Windows.Data;

namespace HandPegApp.Shell;

/// <summary>
/// MainWindow's playhead-scrub UI state (MainWindow.Scrub.cs), kept off the shell class (shell-budget golden). UI thread only.
/// </summary>
internal sealed class ScrubState
{
    /// <summary>OnScrubFrame is hooked on CompositionTarget.Rendering.</summary>
    public bool FrameHooked;

    /// <summary>FlushScrub is committing PositionMs (the player already seeked there).</summary>
    public bool Flushing;

    /// <summary>A Player/Canvas box rebuild was skipped during a held scrub (MainWindow.PlayerLayout.cs); run once on release.</summary>
    public bool RedrawOwed;

    // ----- Pointer rest by timestamp (Pass 5.7.1: no DispatcherTimer restarted at the pointer's rate) -----

    /// <summary>The newest scrub target (ms; NaN before the first move of a drag).</summary>
    public double LastTargetMs = double.NaN;

    /// <summary>Stopwatch ticks of the last move to a new target (the pointer-rest clock; 0 before the first).</summary>
    public long LastMoveTicks;

    /// <summary>A held play-scrub already played on from this rest (cleared by the next move).</summary>
    public bool DwellResumed;

    // ----- Playhead speed (the Slice-Preview ribbon's gate) -----

    /// <summary>Smoothed playhead speed along the Timeline, DIP per second, and the frame it was last sampled at (Stopwatch ticks).</summary>
    public double Velocity;

    public long VelocityTicks;

    /// <summary>The target the speed was last sampled from (ms; NaN: none).</summary>
    public double VelocityFromMs = double.NaN;

    /// <summary>The current speed sample's starting target (ms; NaN: none) and time (Stopwatch ticks): <see cref="RawVelocity"/>.</summary>
    public double RawFromMs = double.NaN;

    public long RawTicks;

    /// <summary>Least span, seconds, of the current speed sample: a one-pixel step within one 2 ms frame (500 Hz) is no whip.</summary>
    public const double RawSampleSeconds = 0.024;

    // ----- Slice-Preview ribbon (MainWindow.Scrub.cs) -----

    /// <summary>The ribbon's velocity gate.</summary>
    public readonly SlicePreviewGate SliceGate = new();

    /// <summary>The engine ticket of the ribbon's newest ask (0: none, or withdrawn).</summary>
    public int SliceTicket;

    /// <summary>What the newest ask was for (<see cref="Shell.SliceKey"/>), and when it was made (Stopwatch ticks).</summary>
    public SliceKey SliceKey;

    public long SliceAskedAt;

    /// <summary>A strip arrived since the newest ask (the next ask need not wait out the ask interval).</summary>
    public bool SliceAnswered;

    /// <summary>The newest ask's request (asked again after a failure), how often it was asked again, and whether its strip failed.</summary>
    public Services.SlicePreviewRequest? SliceRequest;

    public int SliceRetries;

    public bool SliceFailed;

    // ----- Overview Scrubber freeze (a ruler drag leaves the slider's layout alone) -----

    /// <summary>The Overview Scrubber's Value binding, taken off while a ruler scrub is held; null when it is bound.</summary>
    public BindingBase? OverviewBinding;

    /// <summary>The slider's binding is being taken off or put back (its ValueChanged echoes are ignored).</summary>
    public bool OverviewSwapping;

    /// <summary>
    /// True once a held play-scrub's pointer has rested for <paramref name="dwellSeconds"/> (GpuPreviewEngine.GhostDwell, 80 ms) since
    /// <paramref name="lastMoveTicks"/>: evaluated each rendered frame by timestamp. Never before a first move. Pure; goldens.
    /// </summary>
    public static bool DwellDue(long nowTicks, long lastMoveTicks, long frequency, double dwellSeconds) =>
        lastMoveTicks != 0 && frequency > 0 && nowTicks - lastMoveTicks >= dwellSeconds * frequency;

    /// <summary>
    /// The playhead's smoothed speed, DIP per second, after it moved <paramref name="movedDip"/> in <paramref name="elapsedSeconds"/>: a
    /// short exponential average (a 120 ms time constant) of the frame's speed, so one jittery frame neither wakes nor sleeps the ribbon;
    /// a frame with no time passed keeps the speed. Pure; goldens.
    /// </summary>
    public static double NextVelocity(double velocity, double movedDip, double elapsedSeconds)
    {
        if (!(elapsedSeconds > 0) || !double.IsFinite(movedDip))
            return velocity;

        var frame = Math.Abs(movedDip) / elapsedSeconds;
        var keep = Math.Exp(-Math.Min(elapsedSeconds, 1) / 0.12);
        return double.IsFinite(velocity) ? keep * velocity + (1 - keep) * frame : frame;
    }

    /// <summary>
    /// The playhead's current (unsmoothed) speed at <paramref name="atMs"/>, DIP per second: its move since the sample began over the time
    /// since, at least <see cref="RawSampleSeconds"/> (<see cref="RawSpeed"/>); the sample starts again once that long has passed. 0 on a
    /// drag's first frame.
    /// </summary>
    public double RawVelocity(double atMs, double pixelsPerSecond, long now, long frequency)
    {
        if (double.IsNaN(RawFromMs) || RawTicks == 0 || frequency <= 0)
        {
            (RawFromMs, RawTicks) = (atMs, now);
            return 0;
        }

        var elapsed = (now - RawTicks) / (double)frequency;
        var speed = RawSpeed((atMs - RawFromMs) / 1000 * pixelsPerSecond, elapsed);
        if (elapsed >= RawSampleSeconds)
            (RawFromMs, RawTicks) = (atMs, now);
        return speed;
    }

    /// <summary>A move of <paramref name="movedDip"/> over <paramref name="elapsedSeconds"/> (floored at <see cref="RawSampleSeconds"/>) in DIP per second; 0 when not finite. Pure; goldens.</summary>
    public static double RawSpeed(double movedDip, double elapsedSeconds) =>
        double.IsFinite(movedDip) && !double.IsNaN(elapsedSeconds) ? Math.Abs(movedDip) / Math.Max(elapsedSeconds, RawSampleSeconds) : 0;
}

/// <summary>
/// What a Slice-Preview ask shows: the clip and the timing its file times follow (start, file offset, speed, ramp keys), its file and video
/// stream, the centre frame, the spacing, the slot count and the thumbnail size. An unchanged key asks nothing (review F26).
/// </summary>
internal readonly record struct SliceKey(Guid Clip, double Start, double MediaOffset, double Speed, int RampKeys, string? Path, int Stream, long Centre,
    double Spacing, int Slots, int ShortSide);
