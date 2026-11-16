using System.Globalization;

namespace HandPegApp.Services;

/// <summary>The background writer for hot breadcrumbs, the seqlock slot copy it uses, and the allocation-free line formatter.</summary>
internal sealed unsafe partial class BreadcrumbFile
{
    /// <summary>Writes every slot changed since its last write; returns how many were written. The flusher thread calls this every
    /// <see cref="FlushMilliseconds"/> ms while hot lines come; tests call it directly.</summary>
    internal int FlushChanged()
    {
        var written = 0;
        lock (_flushGate)
        {
            if (_disposed)
                return 0;
            var count = Math.Min(Volatile.Read(ref _nextSlot) + 1, SlotCount);
            for (var i = 0; i < count; i++)
            {
                if (Volatile.Read(ref _slots[i]) is not { } slot || Volatile.Read(ref slot.Sequence) == slot.Flushed)
                    continue;
                lock (slot.Gate)
                {
                    if (!TryCopy(slot, _flushBuffer, out var sequence) || sequence == slot.Flushed)
                        continue;
                    WriteSlot(_flushBuffer, slot.Index, _flushEvent);
                    slot.Flushed = sequence;
                    written++;
                }
            }
        }

        return written;
    }

    /// <summary>A whole slot image as of one even sequence: false when the owning thread kept writing through every attempt (the
    /// next flush takes it).</summary>
    private static bool TryCopy(Slot slot, byte* target, out int sequence)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            sequence = Volatile.Read(ref slot.Sequence);
            if ((sequence & 1) != 0)
            {
                Thread.SpinWait(16);
                continue;
            }

            Buffer.MemoryCopy(slot.Buffer, target, SlotBytes, SlotBytes);
            Interlocked.MemoryBarrier(); // the copy's loads complete before the sequence is read again
            if (Volatile.Read(ref slot.Sequence) == sequence)
                return true;
        }

        sequence = 0;
        return false;
    }

    /// <summary>The slot image the flusher would write now, for tests that check a copy is never torn.</summary>
    internal bool TryCopyOwnSlot(int index, Span<byte> target)
    {
        if (target.Length < SlotBytes || Volatile.Read(ref _slots[index]) is not { } slot)
            return false;
        fixed (byte* copy = target)
            return TryCopy(slot, copy, out _);
    }

    private void StartFlusher()
    {
        if (Interlocked.Exchange(ref _flusherStarted, 1) != 0)
            return;
        _flusher = new Thread(FlushLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "GPU breadcrumb flusher" };
        _flusher.Start();
    }

    /// <summary>
    /// Wakes a sleeping flusher (a hot line just changed its slot). The full fence orders the slot's sequence store before the read of
    /// <see cref="_flusherIdle"/>; the flusher stores that flag, fences, then looks for a changed slot, so one of the two always sees
    /// the other and no hot line is left unflushed. While the flusher is awake this costs the fence and one read.
    /// </summary>
    private void WakeFlusherIfIdle()
    {
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _flusherIdle) != 0 && Interlocked.Exchange(ref _flusherIdle, 0) != 0)
            Native.Kernel32.SetEvent(_wakeEvent);
    }

    /// <summary>Whether any slot changed since its last write (the flusher's check before it sleeps).</summary>
    private bool AnyChanged()
    {
        var count = Math.Min(Volatile.Read(ref _nextSlot) + 1, SlotCount);
        for (var i = 0; i < count; i++)
        {
            if (Volatile.Read(ref _slots[i]) is { } slot && Volatile.Read(ref slot.Sequence) != slot.Flushed)
                return true;
        }

        return false;
    }

    /// <summary>Whether the flusher is asleep with its timer stopped (no hot line for <see cref="IdleAfterQuietPeriods"/> periods): tests.</summary>
    internal bool FlusherIdle => Volatile.Read(ref _flusherIdle) != 0;

    /// <summary>How many periods the flusher has woken for (tests: none while it is idle).</summary>
    internal long FlushPasses => Interlocked.Read(ref _flushPasses);

    private long _flushPasses;

    private void FlushLoop()
    {
        // A high-resolution periodic timer keeps the period at 15 ms rather than the 15.6 ms scheduler tick (or twice it). It runs only
        // while hot lines come: after IdleAfterQuietPeriods periods with nothing to write it is stopped and the thread sleeps on
        // _wakeEvent until the next hot line (no wake-ups while paused or idle).
        var timer = Native.Kernel32.CreateWaitableTimerExW(0, 0, Native.Kernel32.HighResolutionTimer, Native.Kernel32.TimerAllAccess);
        if (timer != 0 && !Arm(timer))
        {
            Native.Kernel32.CloseHandle(timer);
            timer = 0;
        }

        try
        {
            var quiet = 0;
            while (!_disposed)
            {
                if (timer != 0)
                    Native.Kernel32.WaitForSingleObject(timer, 100);
                else
                    Thread.Sleep(FlushMilliseconds);

                var written = 0;
                Interlocked.Increment(ref _flushPasses);
                try
                {
                    written = FlushChanged();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The next period retries; a breadcrumb failure never reaches the traced code.
                }

                quiet = written > 0 ? 0 : quiet + 1;
                if (quiet < IdleAfterQuietPeriods)
                    continue;

                // Asleep until a hot line: the flag first, then a last look (see WakeFlusherIfIdle).
                if (timer != 0)
                    Native.Kernel32.CancelWaitableTimer(timer);
                Volatile.Write(ref _flusherIdle, 1);
                Interlocked.MemoryBarrier();
                if (!AnyChanged() && !_disposed)
                    Native.Kernel32.WaitForSingleObject(_wakeEvent, Native.Kernel32.Infinite);
                Interlocked.Exchange(ref _flusherIdle, 0);
                quiet = 0;
                if (timer != 0 && !Arm(timer))
                {
                    Native.Kernel32.CloseHandle(timer);
                    timer = 0;
                }
            }
        }
        finally
        {
            if (timer != 0)
                Native.Kernel32.CloseHandle(timer);
        }

        static bool Arm(nint timer)
        {
            var due = -FlushMilliseconds * TimeSpan.TicksPerMillisecond;
            return Native.Kernel32.SetWaitableTimer(timer, &due, FlushMilliseconds, 0, 0, 0) != 0;
        }
    }

    /// <summary>Formats ASCII into one fixed 128-byte line: printable characters only (others become '?'), cut at 127 bytes, padded
    /// with spaces and ended with a line break by <see cref="Close"/>. Without <see cref="Close"/> it formats into any span.</summary>
    private ref struct LineWriter(Span<byte> target)
    {
        private readonly Span<byte> _target = target;
        private int _length;

        public readonly int Length => _length;

        private readonly int Room => Math.Max(0, Math.Min(_target.Length, LineBytes - 1) - _length);

        public void Text(scoped ReadOnlySpan<char> text)
        {
            var count = Math.Min(text.Length, Room);
            for (var i = 0; i < count; i++)
                _target[_length + i] = text[i] is var c and >= ' ' and <= '~' ? (byte)c : (byte)'?';
            _length += count;
        }

        public void Bytes(scoped ReadOnlySpan<byte> text)
        {
            var count = Math.Min(text.Length, Room);
            text[..count].CopyTo(_target[_length..]);
            _length += count;
        }

        public void Number(long value, ReadOnlySpan<char> format = default)
        {
            Span<byte> digits = stackalloc byte[24];
            if (value.TryFormat(digits, out var count, format, CultureInfo.InvariantCulture))
                Bytes(digits[..count]);
        }

        /// <summary>Upper-case hexadecimal: a LUID in as many digits as it needs.</summary>
        public void Hex(long value)
        {
            Span<byte> digits = stackalloc byte[24];
            if (value.TryFormat(digits, out var count, "X", CultureInfo.InvariantCulture))
                Bytes(digits[..count]);
        }

        /// <summary>An HRESULT's two's complement as 8 hex digits (8876017C).</summary>
        public void HResult(int value)
        {
            Span<byte> digits = stackalloc byte[16];
            if (((uint)value).TryFormat(digits, out var count, "X8", CultureInfo.InvariantCulture))
                Bytes(digits[..count]);
        }

        public void Time(DateTime when)
        {
            Span<byte> text = stackalloc byte[24];
            if (when.TryFormat(text, out var count, "HH:mm:ss.ffffff", CultureInfo.InvariantCulture))
                Bytes(text[..count]);
        }

        /// <summary>Pads the rest of the line with spaces and ends it with a line break.</summary>
        public void Close()
        {
            _target[_length..(LineBytes - 1)].Fill((byte)' ');
            _target[LineBytes - 1] = (byte)'\n';
        }
    }
}
