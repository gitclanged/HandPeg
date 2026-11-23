namespace HandPegApp.Services;

/// <summary>
/// Pass 5.6.4A: a one-word, lock-free seek mail-slot (latest wins). Producers <see cref="Post"/> from any thread (one Interlocked exchange,
/// no lock); the consumer <see cref="TryTake"/>s the newest target. <see cref="Empty"/> (long.MinValue) is the bit pattern of -0.0, so
/// <see cref="Post"/> normalises -0.0 to +0.0 and never posts a non-finite target.
/// </summary>
internal static class SeekMailSlot
{
    /// <summary>The empty slot's value.</summary>
    public const long Empty = long.MinValue;

    /// <summary>Posts <paramref name="seconds"/>, replacing any target not yet taken. Non-finite values are ignored.</summary>
    public static void Post(ref long slot, double seconds)
    {
        if (double.IsFinite(seconds))
            Interlocked.Exchange(ref slot, BitConverter.DoubleToInt64Bits(seconds + 0.0));
    }

    /// <summary>Takes the newest target and empties the slot; false (NaN) when none was posted.</summary>
    public static bool TryTake(ref long slot, out double seconds)
    {
        var bits = Interlocked.Exchange(ref slot, Empty);
        seconds = bits == Empty ? double.NaN : BitConverter.Int64BitsToDouble(bits);
        return bits != Empty;
    }

    /// <summary>True while a target waits to be taken.</summary>
    public static bool IsPending(ref long slot) => Volatile.Read(ref slot) != Empty;

    /// <summary>
    /// The pump's end-of-Sequence commit point (under the engine's transport lock): moves <paramref name="serial"/> from
    /// <paramref name="measured"/> in one compare-exchange, then requires the slot empty. A Seek posts before it bumps the serial, so one
    /// posted before this point is seen (bumped serial or pending target: false, playback goes on and the drain applies it) and one posted
    /// after it lands on the paused clock. True: the end may pause.
    /// </summary>
    public static bool TryCommitEnd(ref long serial, long measured, ref long slot) =>
        Interlocked.CompareExchange(ref serial, measured + 1, measured) == measured && !IsPending(ref slot);

    /// <summary>
    /// Where Play starts (under the engine's transport lock): a pending target is taken and wins over <paramref name="pausedAt"/> (a drain
    /// may have written an older target there after it was posted); clamped to the Sequence, and from its end a forward play restarts at 0.
    /// </summary>
    public static double ResumeFrom(ref long slot, double pausedAt, double sequenceSeconds, double rate)
    {
        var from = Math.Clamp(TryTake(ref slot, out var pending) ? pending : pausedAt, 0, Math.Max(sequenceSeconds, 0));
        return rate >= 0 && from >= sequenceSeconds - 1e-3 ? 0 : from;
    }

    /// <summary>The waiting target, else <paramref name="fallback"/>; the slot is left as it is.</summary>
    public static double PeekOr(ref long slot, double fallback)
    {
        var bits = Volatile.Read(ref slot);
        return bits == Empty ? fallback : BitConverter.Int64BitsToDouble(bits);
    }
}
