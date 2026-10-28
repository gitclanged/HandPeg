using System.Text;
using System.Text.Unicode;

namespace HandPegApp.Services;

/// <summary>
/// The small block of shared memory HandPeg keeps for its crash sentinel: a header (what the process is, how it is
/// doing, and the native fault when there is one), the 64 breadcrumbs, and a handful of named context slots, all as
/// fixed-size fields, so that the sentinel can read them from another process after this one has died, and so that the
/// unhandled-exception filter can write the fault into it without allocating or taking a lock.
///
/// The breadcrumbs and the context are kept here as the caller passed them: they are sanitized (<see
/// cref="CrashSanitizer"/>) when a report is written, by whichever process writes it, so nothing that identifies the
/// computer or the media reaches a file. The block only ever lives in memory, shared between HandPeg and its own
/// sentinel; it is never written to disk.
///
/// Layout (format 1, little-endian, 20 KB):
///   0       header (256 bytes)
///   256     64 breadcrumbs of 192 bytes: sequence (8), seconds since start (8), category (24), detail (152)
///   12544   12 context slots of 256 bytes: key (24), value (232)
///   15616   24 file names of 160 bytes (the files in use, kept out of the report: see CrashSanitizer.KeepOutFileName)
/// Text fields are UTF-8, cut at a character boundary and ended by a zero byte.
/// </summary>
public static unsafe class CrashSharedMemory
{
    /// <summary>"HPC1" as a little-endian number: the sentinel refuses a block that does not begin with it.</summary>
    public const uint Magic = 0x31435048;

    public const int Version = 1;

    /// <summary>The whole block's size: a little more than the fields need, rounded up to whole pages.</summary>
    public const int TotalBytes = 20480;

    public const int BreadcrumbCount = 64;

    public const int ContextSlotCount = 12;

    public const int FileNameCount = 24;

    // ----- Flags, set by HandPeg so the sentinel knows whether it still has anything to do -----

    /// <summary>HandPeg is closing in the ordinary way: nothing to report, whatever the exit code.</summary>
    public const uint FlagCleanExit = 1;

    /// <summary>HandPeg wrote a report of its own for the fault that is ending it: the sentinel must not write a second.</summary>
    public const uint FlagManagedReportWritten = 2;

    /// <summary>The unhandled-exception filter recorded a fault (code, address, module base) in the header.</summary>
    public const uint FlagNativeFault = 4;

    /// <summary>Set by the sentinel once it has the process open and has made sure it is the right one: from then on a death is seen.</summary>
    public const uint FlagSentinelWatching = 8;

    /// <summary>
    /// The user asked HandPeg to close and it is on its way out (the save prompt may be up): ended from outside now (the
    /// taskbar, Task Manager), it is the user's doing, not a crash. A native fault recorded meanwhile is still reported.
    /// </summary>
    public const uint FlagUserClosing = 16;

    // ----- The header -----

    private const int OffMagic = 0;
    private const int OffVersion = 4;
    private const int OffFlags = 8;
    private const int OffWatchedPid = 12;
    private const int OffCreationTime = 16;     // The watched process's creation time (FILETIME), to tell a reused process id apart.
    private const int OffProcessorCount = 24;
    private const int OffSelfContained = 28;
    private const int OffWorkingSet = 32;
    private const int OffManagedHeap = 40;
    private const int OffExceptionCode = 48;
    private const int OffParameterCount = 52;
    private const int OffFaultAddress = 56;
    private const int OffModuleBase = 64;
    private const int OffAccessKind = 72;       // EXCEPTION_RECORD.ExceptionInformation[0]: 0 read, 1 write, 8 execute.
    private const int OffAccessAddress = 80;    // ExceptionInformation[1]: the address that could not be reached.
    private const int OffModuleOffset = 88;     // Filled in by the sentinel, from the live process's module list.
    private const int OffAppVersion = 96;
    private const int AppVersionBytes = 32;
    private const int OffModuleName = 128;
    private const int ModuleNameBytes = 96;

    private const int HeaderBytes = 256;

    // ----- The breadcrumb ring -----

    private const int CrumbSequence = 0;
    private const int CrumbAt = 8;
    private const int CrumbCategory = 16;
    private const int CrumbCategoryBytes = 24;
    private const int CrumbDetail = 40;
    private const int CrumbDetailBytes = 152;
    private const int CrumbBytes = 192;

    private static int Crumb(int slot) => HeaderBytes + slot * CrumbBytes;

    // ----- The context slots -----

    private const int ContextBase = HeaderBytes + BreadcrumbCount * CrumbBytes;
    private const int ContextKeyBytes = 24;
    private const int ContextValue = 24;
    private const int ContextValueBytes = 232;
    private const int ContextSlotBytes = 256;

    private static int ContextSlot(int slot) => ContextBase + slot * ContextSlotBytes;

    // ----- The file names -----

    private const int FileNameBase = ContextBase + ContextSlotCount * ContextSlotBytes;
    private const int FileNameBytes = 160;

    // The layout must fit the block, and the header its 256 bytes: a negative constant cannot be a uint, so a field
    // added later that runs off the end stops the build instead of the sentinel. (No static constructor: the filter
    // calls into this class, and must not meet a check for one.)
    private const uint SpareBytes = TotalBytes - (FileNameBase + FileNameCount * FileNameBytes);
    private const uint SpareHeaderBytes = HeaderBytes - (OffModuleName + ModuleNameBytes);

    // ----- Writing the header (HandPeg) -----

    public static void WriteHeader(byte* p, int watchedPid, long creationTime, bool selfContained, string appVersion, int processorCount)
    {
        new Span<byte>(p, HeaderBytes).Clear();
        Write32(p, OffVersion, Version);
        Write32(p, OffWatchedPid, (uint)watchedPid);
        Write64(p, OffCreationTime, creationTime);
        Write32(p, OffProcessorCount, (uint)processorCount);
        Write32(p, OffSelfContained, selfContained ? 1u : 0u);
        WriteUtf8(p, OffAppVersion, AppVersionBytes, appVersion);

        // The magic last: a sentinel that opens the block a moment too early sees no header rather than half of one.
        Volatile.Write(ref *(uint*)(p + OffMagic), Magic);
    }

    /// <summary>Sets a flag without disturbing the others, from any thread.</summary>
    public static void SetFlag(byte* p, uint flag) => Interlocked.Or(ref *(int*)(p + OffFlags), (int)flag);

    /// <summary>Clears a flag without disturbing the others, from any thread.</summary>
    public static void ClearFlag(byte* p, uint flag) => Interlocked.And(ref *(int*)(p + OffFlags), ~(int)flag);

    public static void WriteVitals(byte* p, long workingSetBytes, long managedHeapBytes)
    {
        Write64(p, OffWorkingSet, workingSetBytes);
        Write64(p, OffManagedHeap, managedHeapBytes);
    }

    // ----- The native fault (the unhandled-exception filter: no allocation, no lock) -----

    /// <summary>
    /// Records a fault from the EXCEPTION_RECORD Windows hands the filter (x64 layout: code at 0, address at 16, the
    /// parameter count at 24, the parameters from 32). Only numbers are copied; the flag goes up last.
    /// </summary>
    public static void WriteFault(byte* p, byte* record, IntPtr moduleBase)
    {
        Write32(p, OffExceptionCode, *(uint*)record);
        Write64(p, OffFaultAddress, *(long*)(record + 16));
        Write64(p, OffModuleBase, (long)moduleBase);
        var parameters = *(uint*)(record + 24);
        Write32(p, OffParameterCount, parameters);
        if (parameters >= 2)
        {
            Write64(p, OffAccessKind, *(long*)(record + 32));
            Write64(p, OffAccessAddress, *(long*)(record + 40));
        }

        SetFlag(p, FlagNativeFault);
    }

    /// <summary>The sentinel's answer to a fault: the module the address is in, by file name, and the address's offset in it.</summary>
    public static void WriteModule(byte* p, string name, ulong moduleBase, ulong offset)
    {
        WriteUtf8(p, OffModuleName, ModuleNameBytes, name);
        if (ReadU64(p, OffModuleBase) == 0)
            Write64(p, OffModuleBase, (long)moduleBase);
        Write64(p, OffModuleOffset, (long)offset);
    }

    // ----- Writing a breadcrumb (HandPeg) -----

    /// <summary>
    /// Puts breadcrumb number <paramref name="sequence"/> (1, 2, 3...) into its slot. The slot's sequence is cleared
    /// first and set last, so a breadcrumb caught half-written by a crash is skipped rather than read scrambled.
    /// </summary>
    public static void WriteBreadcrumb(byte* p, long sequence, double at, string category, string? detail)
    {
        var crumb = p + Crumb((int)((sequence - 1) % BreadcrumbCount));

        // A slower writer of an older breadcrumb must not put it over a newer one.
        if (Volatile.Read(ref *(long*)(crumb + CrumbSequence)) > sequence)
            return;

        Volatile.Write(ref *(long*)(crumb + CrumbSequence), 0);
        *(double*)(crumb + CrumbAt) = at;
        WriteUtf8(crumb, CrumbCategory, CrumbCategoryBytes, category);
        WriteUtf8(crumb, CrumbDetail, CrumbDetailBytes, detail ?? "");
        Volatile.Write(ref *(long*)(crumb + CrumbSequence), sequence);
    }

    // ----- Writing a context slot (HandPeg) -----

    public static void WriteContext(byte* p, int slot, string key, string? value)
    {
        if (slot < 0 || slot >= ContextSlotCount)
            return;
        var at = ContextSlot(slot);
        WriteUtf8(p, at + ContextValue, ContextValueBytes, value ?? "");
        WriteUtf8(p, at, ContextKeyBytes, key);
    }

    // ----- Writing a file name (HandPeg) -----

    public static void WriteFileName(byte* p, int slot, string name)
    {
        if (slot >= 0 && slot < FileNameCount)
            WriteUtf8(p, FileNameBase + slot * FileNameBytes, FileNameBytes, name);
    }

    // ----- Reading (the sentinel) -----

    public static bool IsValid(byte* p) => ReadU32(p, OffMagic) == Magic && ReadU32(p, OffVersion) == Version;

    public static uint Flags(byte* p) => Volatile.Read(ref *(uint*)(p + OffFlags));
    public static int WatchedPid(byte* p) => (int)ReadU32(p, OffWatchedPid);
    public static long CreationTime(byte* p) => (long)ReadU64(p, OffCreationTime);
    public static int ProcessorCount(byte* p) => (int)ReadU32(p, OffProcessorCount);
    public static bool SelfContained(byte* p) => ReadU32(p, OffSelfContained) != 0;
    public static long WorkingSet(byte* p) => (long)ReadU64(p, OffWorkingSet);
    public static long ManagedHeap(byte* p) => (long)ReadU64(p, OffManagedHeap);
    public static uint ExceptionCode(byte* p) => ReadU32(p, OffExceptionCode);
    public static ulong FaultAddress(byte* p) => ReadU64(p, OffFaultAddress);
    public static ulong ModuleBase(byte* p) => ReadU64(p, OffModuleBase);
    public static ulong ModuleOffset(byte* p) => ReadU64(p, OffModuleOffset);
    public static string ModuleName(byte* p) => ReadUtf8(p, OffModuleName, ModuleNameBytes);
    public static string AppVersion(byte* p) => ReadUtf8(p, OffAppVersion, AppVersionBytes);

    /// <summary>For an access violation: whether it was a read (0), a write (1) or an execution (8), and of what address; null otherwise.</summary>
    public static (ulong Kind, ulong Address)? Access(byte* p) =>
        ReadU32(p, OffParameterCount) >= 2 ? (ReadU64(p, OffAccessKind), ReadU64(p, OffAccessAddress)) : null;

    /// <summary>The breadcrumbs, oldest first, as they were written (not sanitized: the caller does that).</summary>
    public static List<(long Sequence, double At, string Category, string Detail)> Breadcrumbs(byte* p)
    {
        var crumbs = new List<(long, double, string, string)>(BreadcrumbCount);
        for (var slot = 0; slot < BreadcrumbCount; slot++)
        {
            var crumb = p + Crumb(slot);
            var sequence = *(long*)(crumb + CrumbSequence);
            if (sequence <= 0)
                continue;
            crumbs.Add((sequence, *(double*)(crumb + CrumbAt), ReadUtf8(crumb, CrumbCategory, CrumbCategoryBytes), ReadUtf8(crumb, CrumbDetail, CrumbDetailBytes)));
        }

        crumbs.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return crumbs;
    }

    /// <summary>The context slots that were set, by key, as they were written (not sanitized).</summary>
    public static Dictionary<string, string> Context(byte* p)
    {
        var context = new Dictionary<string, string>();
        for (var slot = 0; slot < ContextSlotCount; slot++)
        {
            var at = ContextSlot(slot);
            var key = ReadUtf8(p, at, ContextKeyBytes);
            if (key.Length > 0)
                context[key] = ReadUtf8(p, at + ContextValue, ContextValueBytes);
        }

        return context;
    }

    /// <summary>The names of the files in use that HandPeg handed over, for the sentinel to keep out of its report.</summary>
    public static List<string> FileNames(byte* p)
    {
        var names = new List<string>(FileNameCount);
        for (var slot = 0; slot < FileNameCount; slot++)
        {
            if (ReadUtf8(p, FileNameBase + slot * FileNameBytes, FileNameBytes) is { Length: > 0 } name)
                names.Add(name);
        }

        return names;
    }

    // ----- Fixed fields -----

    private static void Write32(byte* p, int offset, uint value) => *(uint*)(p + offset) = value;
    private static void Write64(byte* p, int offset, long value) => *(long*)(p + offset) = value;
    private static uint ReadU32(byte* p, int offset) => *(uint*)(p + offset);
    private static ulong ReadU64(byte* p, int offset) => *(ulong*)(p + offset);

    /// <summary>
    /// Encodes a string into a fixed field as UTF-8, as much of it as fits in whole characters with room for the zero
    /// that ends it. One pass, no allocation: a breadcrumb costs a copy, however long its detail.
    /// </summary>
    public static void WriteUtf8(byte* p, int offset, int maxBytes, string value)
    {
        var field = new Span<byte>(p + offset, maxBytes);
        Utf8.FromUtf16(value, field[..^1], out _, out var written, replaceInvalidSequences: true, isFinalBlock: true);
        field[written..].Clear();
    }

    public static string ReadUtf8(byte* p, int offset, int maxBytes)
    {
        var field = new ReadOnlySpan<byte>(p + offset, maxBytes);
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? field[..end] : field);
    }
}
