namespace HandPegApp;

/// <summary>
/// Zero-lag scrub coalescer: during a playhead drag (Timeline ruler, Overview Scrubber) the host moves the lines and seeks the engine on
/// every raw move, while this commits MainViewModel.PositionMs (whose listeners redraw the readout, Effects pane, inspector and key lanes)
/// at most once per <see cref="FlushIntervalMs"/>, plus exactly once on release with the last position. Pure: the host supplies time and
/// calls <see cref="Tick"/> from its frame loop; no hooks. Holds a single pending value, so state never grows.
/// </summary>
public sealed class ScrubCoalescer(Action<double> flush)
{
    /// <summary>Minimum interval (ms) between PositionMs commits during a drag: one per 60 Hz frame regardless of input rate.</summary>
    public const double FlushIntervalMs = 16;

    // Uncommitted latest position (ms; NaN when none) and the time of the last commit.
    private double _pending = double.NaN;
    private double _flushedAt = double.NegativeInfinity;

    /// <summary>True while a playhead drag is held (moves arrive via <see cref="Move"/>).</summary>
    public bool IsHeld { get; private set; }

    /// <summary>True when a position is waiting to be committed.</summary>
    public bool HasPending => !double.IsNaN(_pending);

    /// <summary>Records a scrub move to <paramref name="positionMs"/> for the next <see cref="Tick"/> (the host updates lines and engine itself).</summary>
    public void Move(double positionMs)
    {
        IsHeld = true;
        _pending = positionMs;
    }

    /// <summary>Frame tick at <paramref name="nowMs"/>: commits the pending position if the interval has elapsed; returns true when committed.</summary>
    public bool Tick(double nowMs)
    {
        if (!HasPending || nowMs - _flushedAt < FlushIntervalMs)
            return false;

        Flush(nowMs);
        return true;
    }

    /// <summary>Drag released or cancelled at <paramref name="nowMs"/>: commits the last position immediately.</summary>
    public void Release(double nowMs)
    {
        IsHeld = false;
        if (HasPending)
            Flush(nowMs);
    }

    private void Flush(double nowMs)
    {
        var positionMs = _pending;
        (_pending, _flushedAt) = (double.NaN, nowMs);
        flush(positionMs);
    }
}
