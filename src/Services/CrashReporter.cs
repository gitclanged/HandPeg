using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using HandPegApp.Native;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp.Services;

/// <summary>
/// Crash capture: breadcrumb ring, context summaries and the fault, written as sanitized (<see cref="CrashSanitizer"/>) .hpcrash files in
/// <see cref="AppPaths.Crashes"/>. Reports stay local unless an upload endpoint is configured.
///
/// Managed faults (unhandled, dispatcher, unobserved task exceptions) are written in-process (CrashReporter.Reports.cs). Native faults
/// (access violations in drivers or system DLLs) are reported by an external sentinel process (<see cref="CrashSentinel"/>) from shared
/// memory this class keeps current (CrashReporter.Watch.cs).
///
/// When disabled, nothing is written and no sentinel runs; breadcrumbs are still kept in memory (negligible cost) for later reports.
/// </summary>
public static partial class CrashReporter
{
    // ----- The breadcrumb ring -----
    //
    // Four parallel arrays: a breadcrumb stores two references and two numbers with no allocation. Monotonic Interlocked counter;
    // slot = number mod size. Each slot's number is cleared before writing and set after, so concurrent readers skip torn slots.
    // Lock-free (safe from the UI thread mid-drag).

    private const int Ring = CrashSharedMemory.BreadcrumbCount;
    private static readonly long[] Sequences = new long[Ring];
    private static readonly double[] Ats = new double[Ring];
    private static readonly string?[] Categories = new string?[Ring];
    private static readonly string?[] Details = new string?[Ring];
    private static long _noted;

    // ----- The context -----
    //
    // Named summaries ("mode", "media", "graph", "export"), one slot each so the sentinel can read them from shared memory. A new key
    // claims the first free slot via Interlocked. "env." keys describe the machine environment rather than context.

    private static readonly string?[] ContextKeys = new string?[CrashSharedMemory.ContextSlotCount];
    private static readonly string?[] ContextValues = new string?[CrashSharedMemory.ContextSlotCount];

    // ----- Time -----
    //
    // Breadcrumbs use a monotonic clock started in Main (immune to wall-clock changes). Reports are relative to process start: clock plus
    // the process age at class init, queried lazily (first report or shared-memory publish after Frame 1), not in Main.

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _startOffset = double.NaN;
    private static long _creationTime;

    /// <summary>Seconds on the breadcrumb clock: since this class was set up, early in Main.</summary>
    private static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>How long the process had been running when the breadcrumb clock started, in seconds.</summary>
    private static double StartOffset()
    {
        var offset = _startOffset;
        if (!double.IsNaN(offset))
            return offset;

        var elapsed = Now;
        var creation = CreationTime();
        offset = creation > 0 ? Math.Max(0, (DateTime.UtcNow.ToFileTimeUtc() - creation) / 1e7 - elapsed) : 0;
        _startOffset = offset;
        return offset;
    }

    /// <summary>Process creation FILETIME, or 0 if unavailable; the sentinel uses it to verify process identity.</summary>
    private static unsafe long CreationTime()
    {
        if (_creationTime == 0)
        {
            _creationTime = GetProcessTimes(GetCurrentProcess(), out var creation, out _, out _, out _) != 0 ? creation : 0;
        }

        return _creationTime;
    }

    /// <summary>
    /// Records a breadcrumb (tens of ns; ~100 ns with the sentinel's shared-memory copy). Allocation-free and non-throwing. Detail is
    /// sanitized at report time, but keep it a short summary, not a full command.
    /// </summary>
    /// <param name="category">Short tag: "app", "mode", "media", "project", "export", "queue", "graph", "seek", ...</param>
    public static void Note(string category, string? detail = null)
    {
        var sequence = Interlocked.Increment(ref _noted);
        var slot = (int)((sequence - 1) % Ring);
        var at = Now;

        // Full fence (not a release store) so the following plain stores cannot become visible before the slot is marked in-progress.
        Interlocked.Exchange(ref Sequences[slot], 0);
        Ats[slot] = at;
        Categories[slot] = category;
        Details[slot] = detail;
        Volatile.Write(ref Sequences[slot], sequence);
        DevFlightRecorder.Record(category, detail);

        unsafe
        {
            var shared = (byte*)Volatile.Read(ref _shared);
            if (shared != null)
            {
                // The shared memory is made after the start offset is known, so this never asks Windows for it.
                CrashSharedMemory.WriteBreadcrumb(shared, sequence, at + _startOffset, category, detail);
                RefreshVitals(shared, at);
            }
        }
    }

    /// <summary>
    /// Sets a named context slot ("mode", "media", "graph", "export") to a summary (containers, codecs, sizes, rates, counts; see
    /// <see cref="CrashSummaries"/>), never paths or media text. Null clears. Lock-free.
    /// </summary>
    public static void SetContext(string key, string? value)
    {
        var slot = ContextSlotFor(key);
        if (slot < 0)
            return;

        Volatile.Write(ref ContextValues[slot], value);
        unsafe
        {
            var shared = (byte*)Volatile.Read(ref _shared);
            if (shared != null)
            {
                CrashSharedMemory.WriteContext(shared, slot, key, value);
                RefreshVitals(shared, Now);
            }
        }
    }

    /// <summary>
    /// Registers open file base names (sources, project, output) for redaction like the user name (<see cref="CrashSanitizer.KeepOutFileName"/>),
    /// in-process and in the sentinel via shared memory. URLs and empty paths are ignored. Lock-free; never throws.
    /// </summary>
    public static void KeepOut(params string?[] paths) => KeepOut((IEnumerable<string?>)paths);

    /// <inheritdoc cref="KeepOut(string?[])"/>
    public static void KeepOut(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
        {
            var file = path?.Trim().Trim('"');
            if (string.IsNullOrEmpty(file) || file.Contains("://", StringComparison.Ordinal))
                continue;

            var slot = CrashSanitizer.KeepOutFileName(System.IO.Path.GetFileNameWithoutExtension(file));
            if (slot < 0)
                continue;

            unsafe
            {
                var shared = (byte*)Volatile.Read(ref _shared);
                if (shared != null && CrashSanitizer.FileNameAt(slot) is { } name)
                    CrashSharedMemory.WriteFileName(shared, slot, name);
            }
        }
    }

    /// <summary>The slot a key has, or the first free one for a key not seen before; -1 when every slot is taken.</summary>
    private static int ContextSlotFor(string key)
    {
        for (var slot = 0; slot < ContextKeys.Length; slot++)
        {
            var held = Volatile.Read(ref ContextKeys[slot]) ?? Interlocked.CompareExchange(ref ContextKeys[slot], key, null) ?? key;
            if (string.Equals(held, key, StringComparison.Ordinal))
                return slot;
        }

        return -1;
    }

    // ----- Installing the handlers (Program.Main: cheap, before the first frame) -----

    private static int _installed;

    /// <summary>
    /// Installs managed exception handlers and the native fault filter. Safe for early Main (no disk, processes, windows or reflection);
    /// shared memory and the sentinel start after Frame 1 (<see cref="FirstFrameShown"/>); the dispatcher is hooked via <see cref="WatchDispatcher"/>.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Main returning and Environment.Exit both come through here: every ordinary way out says so to the sentinel.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => MarkCleanExit();

        InstallFilter();
    }

    /// <summary>
    /// Hooks dispatcher unhandled exceptions for reporting without marking them handled (they still terminate the process).
    /// </summary>
    public static void WatchDispatcher(Dispatcher dispatcher) => dispatcher.UnhandledException += OnDispatcherUnhandledException;

    /// <summary>
    /// Called after Frame 1 (processes and disk I/O now allowed). At idle, starts the sentinel and triggers HANDPEG_TEST_CRASH if set.
    /// </summary>
    public static void FirstFrameShown(Dispatcher dispatcher)
    {
        Note("app", "first frame");
        SetContext("mode", AppSettings.EditorMode);
        _firstFrameShown = true;
        dispatcher.BeginInvoke(() =>
        {
            // Not the Idle work lane: that waits while the player plays, and the player is what most needs watching.
            _ = Task.Run(StartWatching);
            _ = Task.Run(DevFlightRecorder.Start);
            TriggerTestCrashIfRequested(dispatcher);
        }, DispatcherPriority.ApplicationIdle);
    }

    private static volatile bool _firstFrameShown;

    /// <summary>
    /// On settings save: starts the sentinel when reports were enabled (after Frame 1) or stops it when disabled.
    /// </summary>
    public static void FollowSetting()
    {
        if (!IsEnabled)
            StopWatching();
        else if (_firstFrameShown && !IsSentinelRunning)
            _ = Task.Run(StartWatching);
    }

    /// <summary>Whether reports are to be written: the setting, read so that a crash handler never fails on it.</summary>
    private static bool IsEnabled
    {
        get
        {
            try
            {
                return AppSettings.Current.SendCrashReports;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Settings that cannot be read stand at their default, which is on.
                return true;
            }
        }
    }

    /// <summary>
    /// Marks a normal shutdown (window close, update restart, Environment.Exit) so the sentinel writes nothing on exit.
    /// </summary>
    public static void MarkCleanExit()
    {
        unsafe
        {
            var shared = (byte*)Volatile.Read(ref _shared);
            if (shared != null)
                CrashSharedMemory.SetFlag(shared, CrashSharedMemory.FlagCleanExit);
        }
    }

    /// <summary>
    /// Close requested (true, set before the save prompt) or cancelled (false); an external kill while prompting produces no report.
    /// </summary>
    public static void MarkUserClosing(bool closing)
    {
        Note("app", closing ? "closing" : "close cancelled");
        unsafe
        {
            var shared = (byte*)Volatile.Read(ref _shared);
            if (shared == null)
                return;

            if (closing)
                CrashSharedMemory.SetFlag(shared, CrashSharedMemory.FlagUserClosing);
            else
                CrashSharedMemory.ClearFlag(shared, CrashSharedMemory.FlagUserClosing);
        }
    }

    // ----- The native fault filter -----

    // The filter that was in place before ours (the runtime's), called after ours. Set once, at install.
    private static nint _previousFilter;

    // Set by the filter: the first native fault, recorded. A second thread falling at the same moment changes nothing.
    private static int _faulted;

    private const uint ClrExceptionCode = 0xE0434352;

    // Filter wait for the sentinel to resolve the faulting module while the process is still inspectable; bounded so crashes are not delayed.
    private const uint ModuleNamingWaitMs = 2000;

    /// <summary>
    /// Installs the filter ahead of the runtime's, invoking it once with no data so it is JIT-compiled and its reverse-P/Invoke thunk exists
    /// before a crash. Shared-memory-dependent paths are prepared later (<see cref="PrepareFilter"/>).
    /// </summary>
    private static unsafe void InstallFilter()
    {
        try
        {
            var filter = (delegate* unmanaged<nint, int>)&Filter;
            _ = filter(0);
            _previousFilter = SetUnhandledExceptionFilter((nint)filter);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or NotSupportedException)
        {
            // Without the filter, a native fault is still seen by the sentinel, from the process's exit code alone.
        }
    }

    /// <summary>
    /// Pre-binds Win32 functions and JIT-compiles the fault copy before shared memory is published, so nothing runs for the first time in a crash.
    /// </summary>
    private static unsafe void PrepareFilter()
    {
        nint ignored;
        _ = RtlPcToFileHeader(0, &ignored);
        _ = SetEvent(0);
        _ = WaitForMultipleObjects(0, null, 0, 0);
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(typeof(CrashSharedMemory).GetMethod(nameof(CrashSharedMemory.WriteFault))!.MethodHandle);
    }

    /// <summary>
    /// Unhandled exception filter (native faults from drivers/system DLLs): copies only numbers (code, address, module base) into shared
    /// memory without allocation, locks or throwing; for native faults wakes the sentinel and briefly waits for it to resolve the module,
    /// then chains to the previous (runtime) filter and returns its result.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe int Filter(nint pointers)
    {
        var shared = (byte*)_shared;
        if (shared != null && pointers != 0 && Interlocked.CompareExchange(ref _faulted, 1, 0) == 0)
        {
            // EXCEPTION_POINTERS: the EXCEPTION_RECORD first. In the record (x64): the code at 0, the address at 16.
            var record = *(byte**)pointers;
            if (record != null)
            {
                nint moduleBase;
                if (RtlPcToFileHeader(*(nint*)(record + 16), &moduleBase) == 0)
                    moduleBase = 0;
                CrashSharedMemory.WriteFault(shared, record, moduleBase);

                // A CLR exception is the managed handlers' to report. Anything else is named by the sentinel.
                var sentinel = _sentinelProcess;
                if (*(uint*)record != ClrExceptionCode && _faultEvent != 0 && sentinel != 0 && SetEvent(_faultEvent) != 0)
                {
                    var handles = stackalloc nint[2] { _namedEvent, sentinel };
                    _ = WaitForMultipleObjects(2, handles, 0, ModuleNamingWaitMs);
                }
            }
        }

        var previous = _previousFilter;
        return previous != 0 ? ((delegate* unmanaged<nint, int>)previous)(pointers) : 0; // 0: EXCEPTION_CONTINUE_SEARCH.
    }

    // ----- Windows -----

    [LibraryImport("kernel32.dll")]
    private static partial nint SetUnhandledExceptionFilter(nint filter);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial nint RtlPcToFileHeader(nint pc, nint* baseOfImage);

}
