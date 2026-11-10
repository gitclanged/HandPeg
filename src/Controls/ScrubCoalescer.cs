namespace HandPegApp;

/// <summary>
/// Zero-lag scrubbing (5.4.2B): a drag of the playhead (the Timeline's ruler, the Sequence Overview Scrubber) moves its lines and seeks the
/// engine on every raw pointer move, at once; the playhead's own value (MainViewModel.PositionMs, whose listeners redraw the readout, the
/// Effects pane, the inspector and the key lanes) is set from here at most once per <see cref="FlushIntervalMs"/> while the drag lasts, and
/// exactly once more as it is let go, with the last moment the pointer reached. Pure: the host passes the time and calls <see cref="Tick"/>
/// from its frame loop; nothing here hooks one. Holds one number: nothing grows.
/// </summary>
public sealed class ScrubCoalescer(Action<double> flush)
{
    /// <summary>The least time (ms) between two settings of the playhead's value during a drag: once per 60 Hz frame, however fast the pointer reports.</summary>
    public const double FlushIntervalMs = 16;

    // The moment the pointer last moved the playhead to (ms) not yet given to the playhead's value, NaN for none; when it was last given.
    private double _pending = double.NaN;
    private double _flushedAt = double.NegativeInfinity;

    /// <summary>A drag of the playhead is held: moves since its press go through <see cref="Move"/>.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>Whether a moment is waiting for the playhead's value.</summary>
    public bool HasPending => !double.IsNaN(_pending);

    /// <summary>The pointer moved the playhead to <paramref name="positionMs"/>: kept for the next <see cref="Tick"/> (the lines and the engine are the host's, now).</summary>
    public void Move(double positionMs)
    {
        IsHeld = true;
        _pending = positionMs;
    }

    /// <summary>A frame at <paramref name="nowMs"/>: the waiting moment is given to the playhead's value if the last was long enough ago. True when it was.</summary>
    public bool Tick(double nowMs)
    {
        if (!HasPending || nowMs - _flushedAt < FlushIntervalMs)
            return false;

        Flush(nowMs);
        return true;
    }

    /// <summary>The drag let go (or taken away) at <paramref name="nowMs"/>: the last moment reached is given to the playhead's value now.</summary>
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
