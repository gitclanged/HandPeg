using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using HandPegApp.Native;

namespace HandPegApp.Services;

/// <summary>
/// The file behind <see cref="GpuNativeBreadcrumbs"/>: one fixed 4 KB slot per thread (a header line with the thread's last state, then a
/// ring of its last 31 lines), plain ASCII padded with spaces. The file is opened with FILE_FLAG_WRITE_THROUGH | FILE_FLAG_NO_BUFFERING
/// (sector-aligned slot buffers) and FILE_FLAG_OVERLAPPED, so writes from different threads are never serialized on the file object.
/// <para>Each slot is written only by its own thread, bracketed by a sequence number (odd while composing). A synchronous line is
/// written from the slot under the slot's gate; a hot line only updates the slot. The flusher copies every slot whose sequence moved
/// since its last write (retrying a copy that raced a writer, so a slot is never torn on disk) and writes the copy under the same gate,
/// so the disk never receives an older slot image after a newer one.</para>
/// </summary>
internal sealed unsafe partial class BreadcrumbFile : IDisposable
{
    internal const int SlotBytes = 4096, LineBytes = 128, RingLines = SlotBytes / LineBytes - 1, SlotCount = 128;

    /// <summary>How often the flusher writes the slots that hot calls changed.</summary>
    internal const int FlushMilliseconds = 15;

    /// <summary>Flush periods with nothing to write after which the flusher stops its timer and sleeps until the next hot line.</summary>
    internal const int IdleAfterQuietPeriods = 4;

    private const uint GenericWrite = 0x40000000, ShareAll = 7, CreateAlways = 2, AttributeNormal = 0x80;
    private const uint WriteThrough = 0x80000000, NoBuffering = 0x20000000, OverlappedIo = 0x40000000;
    private const int ErrorIoPending = 997;

    // The file, owned here; its raw handle for the overlapped writes (closed only after the flusher has stopped).
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _file;
    private readonly nint _handle;
    private readonly string _role;
    private readonly int _processId = Environment.ProcessId;
    private readonly Slot?[] _slots = new Slot?[SlotCount];
    private readonly Lock _flushGate = new();
    private readonly byte* _flushBuffer;
    private readonly nint _flushEvent;

    // Auto-reset: set by a hot line that finds the flusher idle (once per idle period), and by Dispose.
    private readonly nint _wakeEvent;
    private int _nextSlot = -1;
    private int _flusherStarted;

    // 1 while the flusher sleeps on _wakeEvent with its timer stopped.
    private int _flusherIdle;
    private Thread? _flusher;
    private volatile bool _disposed;

    [ThreadStatic]
    private static Slot? t_slot;

    [ThreadStatic]
    private static int t_diskWrites;

    private BreadcrumbFile(Microsoft.Win32.SafeHandles.SafeFileHandle file, string role)
    {
        _file = file;
        _handle = file.DangerousGetHandle();
        _role = role;
        _flushBuffer = (byte*)NativeMemory.AlignedAlloc(SlotBytes, SlotBytes);
        _flushEvent = Native.Kernel32.CreateEventW(0, 1, 0, 0);
        _wakeEvent = Native.Kernel32.CreateEventW(0, 0, 0, 0);
    }

    /// <summary>Disk writes issued by the calling thread (hot calls must leave this unchanged).</summary>
    internal static int DiskWritesOnThisThread => t_diskWrites;

    /// <summary>The process's breadcrumb file in <paramref name="folder"/>, deleting files older than three days; null when it cannot be made.</summary>
    internal static BreadcrumbFile? OpenInFolder(string folder, string role)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder, "gpu-native-breadcrumbs-*.log"))
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-3))
                    TryDelete(old);
            }

            return Open(Path.Combine(folder, $"gpu-native-breadcrumbs-{role}-{Environment.ProcessId}.log"), role);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A breadcrumb file at <paramref name="path"/> (created or truncated); null when it cannot be opened.</summary>
    internal static BreadcrumbFile? Open(string path, string role)
    {
        var file = Native.Kernel32.CreateFile(path, GenericWrite, ShareAll, 0, CreateAlways, AttributeNormal | WriteThrough | NoBuffering | OverlappedIo, 0);
        if (file.IsInvalid)
        {
            file.Dispose();
            file = Native.Kernel32.CreateFile(path, GenericWrite, ShareAll, 0, CreateAlways, AttributeNormal | WriteThrough | OverlappedIo, 0);
        }

        if (!file.IsInvalid)
            return new BreadcrumbFile(file, role);
        file.Dispose();
        return null;
    }

    internal GpuNativeBreadcrumbs.Crumb Enter(string op, long detail, bool hot)
    {
        Span<byte> text = stackalloc byte[24];
        var length = 0;
        if (detail != 0)
            detail.TryFormat(text, out length, default, CultureInfo.InvariantCulture);
        Record("ENTER", op, text[..length], writeNow: !hot);
        if (hot && _flusherStarted == 0)
            StartFlusher();
        return new GpuNativeBreadcrumbs.Crumb(this, op, Stopwatch.GetTimestamp(), hot);
    }

    internal GpuNativeBreadcrumbs.Crumb Enter(string op, string detail)
    {
        Span<byte> text = stackalloc byte[LineBytes];
        Record("ENTER", op, text[..Ascii(detail, text)], writeNow: true);
        return new GpuNativeBreadcrumbs.Crumb(this, op, Stopwatch.GetTimestamp(), hot: false);
    }

    internal void Leave(string op, long started, int hresult, bool hot)
    {
        Span<byte> text = stackalloc byte[48];
        var line = new LineWriter(text);
        line.Number(Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMicrosecond);
        line.Text("us hr=0x");
        line.HResult(hresult);
        Record("LEAVE", op, text[..line.Length], writeNow: !hot);
    }

    internal void Mark(string op, string detail)
    {
        Span<byte> text = stackalloc byte[LineBytes];
        Record("MARK ", op, text[..Ascii(detail, text)], writeNow: true);
    }

    private void Record(string kind, string op, ReadOnlySpan<byte> detail, bool writeNow)
    {
        if (_disposed)
            return;
        try
        {
            if (OwnSlot() is not { } slot)
                return;
            Compose(slot, kind, op, detail);
            if (!writeNow)
            {
                WakeFlusherIfIdle();
                return;
            }

            lock (slot.Gate)
            {
                WriteSlot(slot.Buffer, slot.Index, slot.Event);
                slot.Flushed = slot.Sequence;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A breadcrumb never fails the call it describes.
        }
    }

    private Slot? OwnSlot()
    {
        if (t_slot is { } slot && slot.File == this)
            return slot;
        var index = Interlocked.Increment(ref _nextSlot);
        if (index >= SlotCount)
            return null; // more threads than slots: the newest go unrecorded rather than share one
        slot = new Slot(this, index, (byte*)NativeMemory.AlignedAlloc(SlotBytes, SlotBytes), Native.Kernel32.CreateEventW(0, 1, 0, 0))
        {
            ThreadId = Native.Kernel32.GetCurrentThreadId(),
            Name = Thread.CurrentThread.Name ?? (Thread.CurrentThread.IsThreadPoolThread ? "pool" : "thread"),
        };
        var bytes = new Span<byte>(slot.Buffer, SlotBytes);
        bytes.Fill((byte)' ');
        for (var line = 1; line <= SlotBytes / LineBytes; line++)
            bytes[line * LineBytes - 1] = (byte)'\n'; // empty lines read as blank lines, not one long run of spaces
        Volatile.Write(ref _slots[index], slot);
        t_slot = slot;
        return slot;
    }

    // Formats one ring line and the header into the slot under the slot's sequence (odd while writing). Allocation-free.
    private void Compose(Slot slot, string kind, string op, ReadOnlySpan<byte> detail)
    {
        var sequence = slot.Sequence;
        Interlocked.Exchange(ref slot.Sequence, sequence + 1); // full fence: the odd value is visible before any byte changes
        var now = DateTime.Now;

        var ring = new LineWriter(Line(slot, 1 + (int)(slot.Events % RingLines)));
        ring.Text(kind);
        ring.Text(" ");
        ring.Time(now);
        ring.Text(" ");
        ring.Number(_processId);
        ring.Text(":");
        ring.Number(slot.ThreadId);
        ring.Text(" ");
        ring.Text(op);
        ring.Text(" luid=0x");
        ring.Hex(GpuNativeBreadcrumbs.Adapter);
        ring.Text(" ");
        ring.Bytes(detail);
        ring.Close();
        slot.Events++;

        var header = new LineWriter(Line(slot, 0));
        header.Text("SLOT ");
        header.Number(slot.Index, "000");
        header.Text(" ");
        header.Text(_role);
        header.Text(" ");
        header.Number(_processId);
        header.Text(":");
        header.Number(slot.ThreadId);
        header.Text(" \"");
        header.Text(slot.Name);
        header.Text("\" events=");
        header.Number(slot.Events);
        header.Text(" STATE ");
        header.Text(kind.AsSpan().Trim());
        header.Text(" ");
        header.Text(op);
        header.Text(" @");
        header.Time(now);
        header.Close();

        Volatile.Write(ref slot.Sequence, sequence + 2); // release: every byte above is visible before the even value
    }

    private static Span<byte> Line(Slot slot, int line) => new(slot.Buffer + line * LineBytes, LineBytes);

    private void WriteSlot(byte* buffer, int index, nint completion)
    {
        var overlapped = default(Overlapped);
        var offset = (long)index * SlotBytes;
        (overlapped.Offset, overlapped.OffsetHigh, overlapped.Event) = ((uint)offset, (uint)(offset >> 32), completion);
        if (!WriteFile(_handle, buffer, SlotBytes, null, &overlapped) && Marshal.GetLastPInvokeError() == ErrorIoPending)
            _ = GetOverlappedResult(_handle, &overlapped, out _, true);
        t_diskWrites++;
    }

    private static int Ascii(string text, Span<byte> target)
    {
        var line = new LineWriter(target);
        line.Text(text);
        return line.Length;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Native.Kernel32.SetEvent(_wakeEvent);

        // A flusher that did not stop keeps the handle and buffers it writes from (ThreadStop logs it).
        if (_flusher is { } flusher && !ThreadStop.Join(flusher, 1000, "GPU breadcrumb flusher"))
            return;
        lock (_flushGate)
        {
            _file.Dispose();
            Native.Kernel32.CloseHandle(_flushEvent);
            Native.Kernel32.CloseHandle(_wakeEvent);
            NativeMemory.AlignedFree(_flushBuffer);
        }
    }

    private sealed class Slot(BreadcrumbFile file, int index, byte* buffer, nint completion)
    {
        public readonly BreadcrumbFile File = file;
        public readonly int Index = index;
        public readonly byte* Buffer = buffer;
        public readonly nint Event = completion;
        public readonly Lock Gate = new();
        public long Events;
        public uint ThreadId;
        public string Name = "";

        /// <summary>Odd while the owning thread composes; even values are complete images.</summary>
        public int Sequence;

        /// <summary>The sequence last written to disk (under <see cref="Gate"/>).</summary>
        public int Flushed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public nint Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public nint Event;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(nint file, byte* buffer, uint bytes, uint* written, Overlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOverlappedResult(nint file, Overlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
