using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HandPegApp.Services;

/// <summary>
/// Catches what makes HandPeg fall over, so that it can be looked at later: a trail of the last things that happened
/// (the breadcrumbs), a short summary of what was going on (the context), and the fault itself, written as a .hpcrash
/// file in the crashes folder (<see cref="AppPaths.Crashes"/>). Nothing in a file tells who made it or what they were
/// working on (<see cref="CrashSanitizer"/>), and the files stay on this computer: HandPeg sends nothing anywhere.
///
/// Four kinds of fall are caught. An exception nobody handled, one thrown on the window's thread, and a task's exception
/// nobody looked at are written here, in the process (CrashReporter.Reports.cs). A native fault (an access violation in
/// a graphics driver or a system library) leaves no managed code standing to write anything, so a tiny second copy of HandPeg, the
/// sentinel (<see cref="CrashSentinel"/>), watches from outside: when the process dies badly without having written a
/// report, the sentinel writes one from the shared memory this class keeps up to date (CrashReporter.Watch.cs).
///
/// Off (Settings, Send Crash Reports): nothing is written and no sentinel runs; the breadcrumbs are still kept, in
/// memory only, which costs next to nothing, so a report written after it is switched on has them.
/// </summary>
public static partial class CrashReporter
{
    // ----- The breadcrumb ring -----
    //
    // Four parallel arrays, so that leaving a breadcrumb stores two references and two numbers and allocates nothing.
    // The count only goes up (Interlocked); a breadcrumb's slot is its number modulo the ring's size. Each slot's number
    // is cleared before the slot is written and set after, so a report that reads a slot while it is being written
    // (another thread, a moment before a crash) skips it rather than pairing one breadcrumb's category with another's
    // detail. No lock anywhere: a breadcrumb may be left from the UI thread in the middle of a drag.

    private const int Ring = CrashSharedMemory.BreadcrumbCount;
    private static readonly long[] Sequences = new long[Ring];
    private static readonly double[] Ats = new double[Ring];
    private static readonly string?[] Categories = new string?[Ring];
    private static readonly string?[] Details = new string?[Ring];
    private static long _noted;

    // ----- The context -----
    //
    // A few named summaries of what HandPeg is doing ("mode", "media", "graph", "export"), each in a slot of its own so
    // the sentinel can read it from the shared memory. A key takes the first free slot the first time it is set
    // (claimed with Interlocked, so two threads setting new keys at once each get one); keys beginning "env." are
    // facts about the computer for the report's environment, not the context.

    private static readonly string?[] ContextKeys = new string?[CrashSharedMemory.ContextSlotCount];
    private static readonly string?[] ContextValues = new string?[CrashSharedMemory.ContextSlotCount];

    // ----- Time -----
    //
    // A breadcrumb is stamped with a monotonic clock started with this class (in Main), so a change of the computer's
    // clock never reorders them; reports count from the process's start, which is that clock plus how long the process
    // had been running when it started. That offset is asked of Windows only when it is first wanted (a report, or the
    // shared memory after the first frame), not in Main.

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

    /// <summary>When this process was created (a FILETIME), or 0 when Windows will not say. The sentinel compares it with the process it finds.</summary>
    private static unsafe long CreationTime()
    {
        if (_creationTime == 0)
        {
            long creation, exit, kernel, user;
            _creationTime = GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user) != 0 ? creation : 0;
        }

        return _creationTime;
    }

    /// <summary>
    /// Leaves a breadcrumb: a short note of something that just happened, which a crash report lists in order. Costs a
    /// few dozen nanoseconds (a hundred or so while the sentinel runs, for the copy into the shared memory), never
    /// allocates and never throws, so it can sit anywhere. The detail is sanitized when a report is written, not here,
    /// so it may hold a path as it is passed; still, pass short summaries, not whole graphs or commands.
    /// </summary>
    /// <param name="category">One short word: "app", "mode", "media", "project", "export", "queue", "graph", "seek"...</param>
    public static void Note(string category, string? detail = null)
    {
        var sequence = Interlocked.Increment(ref _noted);
        var slot = (int)((sequence - 1) % Ring);
        var at = Now;

        Volatile.Write(ref Sequences[slot], 0);
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
    /// Sets a context slot: a summary of what HandPeg is doing, by name ("mode", "media", "graph", "export"). Only
    /// summaries belong here: containers, codecs, sizes, rates, lengths, counts, filter names (see <see
    /// cref="CrashSummaries"/>), never a path or any of the media's own words. Null clears it. Lock-free.
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
    /// Keeps the names of files HandPeg has in use (the main video, a layer's file, the project, the output) out of every
    /// report, wherever they turn up without their folders: each file's name without its extension is stripped as the
    /// user's name is (<see cref="CrashSanitizer.KeepOutFileName"/>), by this process and by the sentinel, which is
    /// handed them in the shared memory. Web addresses and empty paths are passed over. Lock-free; never throws.
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
    /// Registers the handlers for managed exceptions and the native fault filter. Cheap enough for the first moments of
    /// Main (no disk, no process, no window, no reflection): the shared memory and the sentinel come after the first
    /// frame (<see cref="FirstFrameShown"/>). The window's thread is watched once there is one (<see cref="WatchDispatcher"/>).
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
    /// Watches a dispatcher (the window's thread) for exceptions that escape its work. What HandPeg did before stays as
    /// it was: nothing here marks one handled, so an exception nobody handles still ends the process.
    /// </summary>
    public static void WatchDispatcher(Dispatcher dispatcher) => dispatcher.UnhandledException += OnDispatcherUnhandledException;

    /// <summary>
    /// The window's first frame is on screen: from now on processes may start and the disk may be read (Cold-Start rule
    /// 3). Once the window is idle, the sentinel is started, and a test crash asked for by HANDPEG_TEST_CRASH happens.
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
    /// Settings were saved: starts the sentinel when crash reports were switched on (after the first frame), or stops it
    /// when they were switched off.
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
    /// HandPeg is closing in the ordinary way (the window closed, an update restarting it, Environment.Exit): the
    /// sentinel, which wakes when the process ends, has nothing to write.
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
    /// The user asked HandPeg to close (true), from before the save prompt is shown, or went back to it (false): ended from
    /// outside meanwhile (the taskbar or Task Manager closing it while the prompt is up), the sentinel writes no report.
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

    // How long the filter waits for the sentinel to name the module the fault is in, while the process can still be
    // looked at from outside: long enough for it to list the modules, short enough that a crash is not held up.
    private const uint ModuleNamingWaitMs = 2000;

    /// <summary>
    /// Puts the filter in place, in front of the runtime's. The filter is called once here, with nothing to record, so
    /// that it is compiled and its way in from native code is made now rather than while the process is falling over.
    /// What it calls only once there is shared memory is made ready with the shared memory (<see cref="PrepareFilter"/>).
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
    /// Makes ready, before the shared memory is published, everything the filter calls once there is some: the Windows
    /// functions found and bound, and the copy of the fault compiled, so nothing is done for the first time in a crash.
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
    /// The top-level filter Windows calls when a fault reaches the top of a thread with nothing to handle it: the way a
    /// crash in a graphics driver or a system library arrives. It copies numbers only (the code, the address, the module's base)
    /// into the shared memory, with no allocation, no lock and nothing that can throw; for a native fault it then wakes
    /// the sentinel and gives it a moment to name the module from outside, while there is still a process to look at.
    /// Then it hands over to the filter that was there before (the runtime's) and returns what that returns.
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

    [LibraryImport("kernel32.dll")]
    private static partial int SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetProcessTimes(nint process, long* creation, long* exit, long* kernel, long* user);
}
