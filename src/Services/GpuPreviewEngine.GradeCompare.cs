namespace HandPegApp.Services;

/// <summary>How the player compares the grade (5.6.0): off, the picture without its grade (Before), or split at a divider.</summary>
public enum GradeCompareMode : byte
{
    /// <summary>The graded picture, as the export draws it.</summary>
    Off,

    /// <summary>Every Color Grade card and the colour sliders' grade left out (or only <see cref="GradeCompare.Card"/>'s).</summary>
    Bypass,

    /// <summary>Left of the divider without the grade, right of it graded.</summary>
    Split,
}

/// <summary>
/// The grade's Before/After as a scene is evaluated with it (5.6.0, GpuScenePlan.Evaluate): its mode, the split's divider (0..1 of the
/// canvas's width) and, for a bypass, the one Color Grade card left out (<see cref="Guid.Empty"/>: every one, and the colour sliders' grade:
/// the player's Before; a card's id: the Color Studio's input still, the picture that card is given).
/// </summary>
public readonly record struct GradeCompare(GradeCompareMode Mode, double Wipe = 0.5, Guid Card = default)
{
    /// <summary>The graded picture (what every caller but the player and the input still passes).</summary>
    public static GradeCompare Off => default;
}

// ----- The grade's Before/After on the player (5.6.0) -----
// The Color Studio's compare: the player's frames (PublishAt) are evaluated without the grade, or split at a divider; nothing else
// sees it (the export, the strips, the project's stills and SeekBench evaluate plans with GradeCompare.Off). Written by the UI thread,
// read by the pump's (the SetLoopbackView pattern): a change redraws the frame from the plan the player has, no new plan.
public sealed partial class GpuPreviewEngine
{
    private int _gradeCompareMode;
    private double _gradeCompareWipe = 0.5;

    // The Color Studio's input still (CaptureInputThumbnailAsync): the request's task and the card it leaves out. Under _inputStillLock, which the
    // pump takes too as it draws a still, so a still it has taken is never told apart from it by a field not yet written.
    private readonly object _inputStillLock = new();
    private (Task Task, Guid Card)? _inputStill;

    /// <summary>The player's compare now.</summary>
    public GradeCompareMode GradeCompareMode => (GradeCompareMode)Volatile.Read(ref _gradeCompareMode);

    /// <summary>The split's divider now, 0..1 of the picture's width.</summary>
    public double GradeCompareWipe => Volatile.Read(ref _gradeCompareWipe);

    /// <summary>
    /// The player's Before/After of the grade: <paramref name="mode"/>, and the split's divider at <paramref name="wipe"/> (0..1 of the picture's
    /// width, the Before on the left). Any thread; a change redraws the frame (not Invalidate: nothing of the plan changed).
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
    /// The compare the player's frame is evaluated with (PublishAt; the pump's thread). A split falls back to the plain Before where a
    /// divider cannot be drawn: in the 3D view (each layer one plane) and while the Live Encoded Preview compares a split of its own.
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
    /// The frame at the playhead without one Color Grade card (<paramref name="card"/>'s id), drawn small: the picture that card is given
    /// (the Color Studio's curves draw its histogram). As <see cref="CaptureThumbnailAsync"/>: null at once while playing, the plan
    /// without the fades, one request at a time (a newer one gives the older none). UI thread.
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

    /// <summary>The compare a thumbnail is drawn with (the pump's thread): the input still's bypass for its own request, else none.</summary>
    private GradeCompare ThumbnailCompare(Task request)
    {
        lock (_inputStillLock)
            return _inputStill is { } still && ReferenceEquals(still.Task, request) ? new GradeCompare(GradeCompareMode.Bypass, 0.5, still.Card) : GradeCompare.Off;
    }
}
