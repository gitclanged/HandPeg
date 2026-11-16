namespace HandPegApp.Services;

/// <summary>Grade compare mode: off, Before (ungraded) or split at a divider.</summary>
public enum GradeCompareMode : byte
{
    /// <summary>The graded picture, as the export draws it.</summary>
    Off,

    /// <summary>Bypass all Color Grade cards and colour-slider grading (or only <see cref="GradeCompare.Card"/>).</summary>
    Bypass,

    /// <summary>Left of the divider without the grade, right of it graded.</summary>
    Split,
}

/// <summary>
/// Before/After parameters for GpuScenePlan.Evaluate: mode, split divider (0..1 of canvas width) and bypassed card
/// (<see cref="Guid.Empty"/> = all cards plus slider grading for the player's Before; a card id = that card's input for the Color Studio).
/// </summary>
public readonly record struct GradeCompare(GradeCompareMode Mode, double Wipe = 0.5, Guid Card = default)
{
    /// <summary>The graded picture (what every caller but the player and the input still passes).</summary>
    public static GradeCompare Off => default;
}

// ----- Player grade Before/After -----
// Color Studio compare: player frames (PublishAt) render ungraded or split; export, strips, stills and SeekBench always use
// GradeCompare.Off. Written on the UI thread, read on the pump (SetLoopbackView pattern); changes redraw from the current plan.
public sealed partial class GpuPreviewEngine
{
    private int _gradeCompareMode;
    private double _gradeCompareWipe = 0.5;

    // Color Studio input-still request (CaptureInputThumbnailAsync): task and bypassed card, guarded by _inputStillLock (also taken by the
    // pump while drawing) so fields are always consistent.
    private readonly object _inputStillLock = new();
    private (Task Task, Guid Card)? _inputStill;

    /// <summary>The player's compare now.</summary>
    public GradeCompareMode GradeCompareMode => (GradeCompareMode)Volatile.Read(ref _gradeCompareMode);

    /// <summary>The split's divider now, 0..1 of the picture's width.</summary>
    public double GradeCompareWipe => Volatile.Read(ref _gradeCompareWipe);

    /// <summary>
    /// Sets player compare <paramref name="mode"/> and split divider <paramref name="wipe"/> (0..1 of width, Before on the left). Any
    /// thread; redraws without Invalidate (plan unchanged).
    /// </summary>
    public void SetGradeCompare(GradeCompareMode mode, double wipe)
    {
        var m = mode is GradeCompareMode.Bypass or GradeCompareMode.Split ? (int)mode : (int)GradeCompareMode.Off;
        var w = double.IsFinite(wipe) ? Math.Max(0, Math.Min(wipe, 1)) : 0.5;
        var changed = Interlocked.Exchange(ref _gradeCompareMode, m) != m;
        changed |= Interlocked.Exchange(ref _gradeCompareWipe, w) != w && m == (int)GradeCompareMode.Split;
        if (!changed)
            return;

        Volatile.Write(ref _sceneDirty, 1);
        _dirty.Set();
    }

    /// <summary>
    /// Effective player compare (PublishAt, pump thread). Split degrades to Before in 3D View (per-layer planes) and while the Live
    /// Encoded Preview shows its own split.
    /// </summary>
    private GradeCompare PlayerCompare()
    {
        var mode = (GradeCompareMode)Volatile.Read(ref _gradeCompareMode);
        if (mode == GradeCompareMode.Off)
            return GradeCompare.Off;
        if (mode == GradeCompareMode.Split && (_drawingExploded || Volatile.Read(ref _loopback) is not null))
            mode = GradeCompareMode.Bypass;
        return new GradeCompare(mode, Volatile.Read(ref _gradeCompareWipe));
    }

    /// <summary>
    /// Small playhead frame with <paramref name="card"/> bypassed (that card's input, for the Color Studio curves histogram). Like
    /// <see cref="CaptureThumbnailAsync"/>: null while playing, fades excluded, one request at a time (newer supersedes). UI thread.
    /// </summary>
    public Task<(byte[] Bgra, int Width, int Height)?> CaptureInputThumbnailAsync(Guid card, int shortSide)
    {
        lock (_inputStillLock)
        {
            var task = CaptureThumbnailAsync(shortSide);
            _inputStill = task.IsCompleted ? null : (task, card);
            return task;
        }
    }

    /// <summary>Thumbnail compare (pump thread): the input-still bypass for its own request, else none.</summary>
    private GradeCompare ThumbnailCompare(Task request)
    {
        lock (_inputStillLock)
            return _inputStill is { } still && ReferenceEquals(still.Task, request) ? new GradeCompare(GradeCompareMode.Bypass, 0.5, still.Card) : GradeCompare.Off;
    }
}
