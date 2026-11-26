namespace HandPegApp;

/// <summary>
/// Zero-lag scrub coalescer: during a playhead drag (Timeline ruler, Overview Scrubber) the host moves the lines on every move it is
/// given, while this hands out at most one engine seek per rendered frame (<see cref="TakeSeek"/>, the newest target; Pass 5.7.1) and
/// commits MainViewModel.PositionMs (whose listeners redraw the readout, Effects pane, inspector and key lanes) at most once per
/// <see cref="FlushIntervalMs"/>, plus exactly once on release with the last position. Pure: the host supplies time and calls
/// <see cref="Tick"/> from its frame loop; no hooks. Holds a single pending value of each kind, so state never grows.
/// </summary>
public sealed class ScrubCoalescer(Action<double> flush)
{
    /// <summary>Minimum interval (ms) between PositionMs commits during a drag: one per 60 Hz frame regardless of input rate.</summary>
    public const double FlushIntervalMs = 16;

    // Uncommitted latest position (ms; NaN when none), the time of the last commit, the engine seek owed (ms; NaN when none) and the
    // last target handed out as a seek (a still pointer owes nothing).
    private double _pending = double.NaN;
    private double _flushedAt = double.NegativeInfinity;
    private double _seekOwed = double.NaN;
    private double _seeked = double.NaN;

    /// <summary>True while a playhead drag is held (moves arrive via <see cref="Move"/>).</summary>
    public bool IsHeld { get; private set; }

    /// <summary>True when a position is waiting to be committed.</summary>
    public bool HasPending => !double.IsNaN(_pending);

    /// <summary>True when an engine seek is owed (a move to a target not yet handed out by <see cref="TakeSeek"/>).</summary>
    public bool HasSeek => !double.IsNaN(_seekOwed);

    /// <summary>
    /// Records a scrub move to <paramref name="positionMs"/> for the next <see cref="Tick"/> and the next <see cref="TakeSeek"/> (the host
    /// updates the lines itself). A move back to the target last handed out owes no seek; <paramref name="sought"/>: the host already sought
    /// the engine there (a drag's first move), so it owes none either.
    /// </summary>
    public void Move(double positionMs, bool sought = false)
    {
        IsHeld = true;
        _pending = positionMs;
        if (sought)
            _seeked = positionMs;
        _seekOwed = positionMs.Equals(_seeked) ? double.NaN : positionMs;
    }

    /// <summary>
    /// The frame's one engine seek: the newest owed target (true), then nothing until the next move to another target. Called once per
    /// rendered frame and once on release, so the engine is woken at most at the display's rate whatever the pointer's rate.
    /// </summary>
    public bool TakeSeek(out double positionMs)
    {
        positionMs = _seekOwed;
        if (double.IsNaN(positionMs))
            return false;

        (_seekOwed, _seeked) = (double.NaN, positionMs);
        return true;
    }

    /// <summary>Frame tick at <paramref name="nowMs"/>: commits the pending position if the interval has elapsed; returns true when committed.</summary>
    public bool Tick(double nowMs)
    {
        if (!HasPending || nowMs - _flushedAt < FlushIntervalMs)
            return false;

        Flush(nowMs);
        return true;
    }

    /// <summary>
    /// Drag released or cancelled at <paramref name="nowMs"/>: commits the last position immediately. The host takes an owed seek first
    /// (<see cref="TakeSeek"/>); the next drag starts with nothing handed out.
    /// </summary>
    public void Release(double nowMs)
    {
        IsHeld = false;
        if (HasPending)
            Flush(nowMs);
        (_seekOwed, _seeked) = (double.NaN, double.NaN);
    }

    private void Flush(double nowMs)
    {
        var positionMs = _pending;
        (_pending, _flushedAt) = (double.NaN, nowMs);
        flush(positionMs);
    }
}
