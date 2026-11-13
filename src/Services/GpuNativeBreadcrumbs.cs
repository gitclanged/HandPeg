using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Native GPU breadcrumbs (Pass 5.4.4 crash tracer): before every native Direct3D 11 / DXGI / Media Foundation call that can hang a card
/// (the keyed mutexes, Present, ResizeBuffers, the Source Reader's reads and seeks, the encoder and decoder transforms, the overlay's
/// window region) an ENTER line is on the disk, and a LEAVE line after it returns. A buffered log is lost when Windows itself freezes or
/// resets; these lines are written through the disk's cache before the call is made, so after a hard freeze the file says which call
/// of which process and thread was in flight, on which card.
/// <para>Off unless asked for (each line is a synchronous disk write): the flag file <see cref="FlagFile"/> in <see cref="Folder"/>
/// (local\DesktopSentinel.ps1 makes it) or HANDPEG_GPU_BREADCRUMBS=1, read once at the process's start. Off, a call costs one test of a
/// static field.</para>
/// <para>One file per process (gpu-native-breadcrumbs-&lt;role&gt;-&lt;pid&gt;.log), one fixed 4 KB slot per thread: a header line (the
/// thread, its card, its last state) and a ring of its last 31 lines, each slot written whole with FILE_FLAG_WRITE_THROUGH and
/// FILE_FLAG_NO_BUFFERING. No lock: a thread writes only its own slot. Plain text, padded with spaces.</para>
/// </summary>
internal static unsafe partial class GpuNativeBreadcrumbs
{
    private const int SlotBytes = 4096, LineBytes = 128, RingLines = SlotBytes / LineBytes - 1, SlotCount = 128;
    private const uint GenericWrite = 0x40000000, ShareAll = 7, CreateAlways = 2, AttributeNormal = 0x80;
    private const uint WriteThrough = 0x80000000, NoBuffering = 0x20000000;

    /// <summary>Where the breadcrumbs and the sentinel's log are kept: %LOCALAPPDATA%\HandPeg\CrashReports.</summary>
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandPeg", "CrashReports");

    /// <summary>The file whose presence in <see cref="Folder"/> turns the breadcrumbs on for processes started after it.</summary>
    public const string FlagFile = "gpu-breadcrumbs.on";

    /// <summary>Whether this process leaves breadcrumbs (decided once, at its start).</summary>
    public static readonly bool On = IsAsked();

    /// <summary>The card this process draws on (its LUID), named on every line; set when its device is made.</summary>
    public static long Adapter;

    private static readonly string Role = Environment.GetCommandLineArgs().Any(a => a.Equals("--compositor-worker", StringComparison.OrdinalIgnoreCase)) ? "worker" : "editor";
    private static readonly int ProcessId = Environment.ProcessId;
    private static readonly nint File = On ? OpenFile() : -1;
    private static int _nextSlot = -1;

    [ThreadStatic]
    private static Slot? _slot;

    private sealed class Slot
    {
        public int Index;
        public byte* Buffer;
        public long Events;
        public uint ThreadId;
        public string Name = "";
    }

    /// <summary>A native call in flight: <see cref="Leave"/> writes its LEAVE line (nothing when the breadcrumbs are off).</summary>
    public readonly struct Crumb
    {
        private readonly string? _op;
        private readonly long _started;

        internal Crumb(string op, long started) => (_op, _started) = (op, started);

        public void Leave(int hresult = 0)
        {
            if (_op is null)
                return;
            var micros = (Stopwatch.GetTimestamp() - _started) * 1_000_000 / Stopwatch.Frequency;
            Write("LEAVE", _op, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{micros}us hr=0x{hresult:X8}"));
        }
    }

    /// <summary>Before a native call: its ENTER line, on the disk before this returns.</summary>
    public static Crumb Enter(string op, long detail = 0)
    {
        if (!On)
            return default;
        Write("ENTER", op, detail == 0 ? "" : detail.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new Crumb(op, Stopwatch.GetTimestamp());
    }

    /// <summary>Before a native call, with a few words of what it is about.</summary>
    public static Crumb Enter(string op, string detail)
    {
        if (!On)
            return default;
        Write("ENTER", op, detail);
        return new Crumb(op, Stopwatch.GetTimestamp());
    }

    /// <summary>A line on its own (a state reached, a choice made), written through as the others.</summary>
    public static void Mark(string op, string detail)
    {
        if (On)
            Write("MARK ", op, detail);
    }

    private static bool IsAsked()
    {
        try
        {
            // 5.4.4-Final: a preview build's tracing (PreviewTrace: preview-trace.flag beside HandPeg.exe, or HANDPEG_PREVIEW_TRACE=1) too.
            return Environment.GetEnvironmentVariable("HANDPEG_GPU_BREADCRUMBS") == "1" || System.IO.File.Exists(Path.Combine(Folder, FlagFile)) || PreviewTrace.IsOn;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static nint OpenFile()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var old in Directory.EnumerateFiles(Folder, "gpu-native-breadcrumbs-*.log"))
            {
                if (System.IO.File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-3))
                    TryDelete(old);
            }

            var path = Path.Combine(Folder, $"gpu-native-breadcrumbs-{Role}-{ProcessId}.log");
            var handle = CreateFileW(path, GenericWrite, ShareAll, 0, CreateAlways, AttributeNormal | WriteThrough | NoBuffering, 0);
            if (handle == -1)
                handle = CreateFileW(path, GenericWrite, ShareAll, 0, CreateAlways, AttributeNormal | WriteThrough, 0);
            return handle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Slot? OwnSlot()
    {
        if (_slot is { } slot)
            return slot;
        var index = Interlocked.Increment(ref _nextSlot);
        if (index >= SlotCount)
            return null; // more threads than slots: the newest go unrecorded rather than share one
        slot = new Slot
        {
            Index = index,
            Buffer = (byte*)NativeMemory.AlignedAlloc(SlotBytes, SlotBytes),
            ThreadId = GetCurrentThreadId(),
            Name = Thread.CurrentThread.Name ?? (Thread.CurrentThread.IsThreadPoolThread ? "pool" : "thread"),
        };
        new Span<byte>(slot.Buffer, SlotBytes).Fill((byte)' ');
        for (var line = 1; line <= SlotBytes / LineBytes; line++)
            slot.Buffer[line * LineBytes - 1] = (byte)'\n'; // empty lines read as blank lines, not one long run of spaces
        _slot = slot;
        return slot;
    }

    private static void Write(string kind, string op, string detail)
    {
        if (File == -1)
            return;
        try
        {
            if (OwnSlot() is not { } slot)
                return;
            var now = DateTime.Now.ToString("HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
            var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{kind} {now} {ProcessId}:{slot.ThreadId} {op} luid=0x{Adapter:X} {detail}");
            PutLine(slot, 1 + (int)(slot.Events % RingLines), line);
            slot.Events++;
            PutLine(slot, 0, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"SLOT {slot.Index:000} {Role} {ProcessId}:{slot.ThreadId} \"{slot.Name}\" events={slot.Events} STATE {kind.Trim()} {op} @{now}"));

            var overlapped = default(Overlapped);
            var offset = (long)slot.Index * SlotBytes;
            (overlapped.Offset, overlapped.OffsetHigh) = ((uint)offset, (uint)(offset >> 32));
            uint written;
            _ = WriteFile(File, slot.Buffer, SlotBytes, &written, &overlapped);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A breadcrumb never fails the call it is about.
        }
    }

    // One line of the slot: ASCII, cut or padded with spaces to 127 characters, then a line break.
    private static void PutLine(Slot slot, int line, string text)
    {
        var target = new Span<byte>(slot.Buffer + line * LineBytes, LineBytes);
        target.Fill((byte)' ');
        var length = Math.Min(text.Length, LineBytes - 1);
        for (var i = 0; i < length; i++)
            target[i] = text[i] is var c and >= ' ' and <= '~' ? (byte)c : (byte)'?';
        target[LineBytes - 1] = (byte)'\n';
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public nint Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public nint Event;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(nint file, byte* buffer, uint bytes, uint* written, Overlapped* overlapped);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
