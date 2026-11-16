using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using HandPegApp.Native;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp.Services;

// Sentinel integration: shared memory read after native faults, the two events the filter uses to request module resolution, and the
// sentinel process.
public static partial class CrashReporter
{
    // Shared memory, mapped for the session once created (never unmapped: another thread may be writing a breadcrumb). Zero until
    // created; the filter and breadcrumbs skip writes meanwhile.
    private static nint _shared;
    private static MemoryMappedFile? _mapping;
    private static MemoryMappedViewAccessor? _view;
    private static string? _sharedName;

    // The fault filter's handshake with the sentinel: "a fault, name its module" and "named". Kept open for the session.
    private static EventWaitHandle? _faultSignal;
    private static EventWaitHandle? _namedSignal;
    private static nint _faultEvent;
    private static nint _namedEvent;

    // The running sentinel, as a process handle (its liveness is all the filter asks of it); zero when none runs.
    private static nint _sentinelProcess;

    private static readonly object WatchGate = new();

    // Adapters and tool versions, captured once after Frame 1 (earlier reports lack them); CPU, memory, display and power come from
    // CrashHardware at the same time.
    private static List<string>? _gpus;
    private static List<GpuAdapter> _cards = [];
    private static string? _ffmpegVersion;

    private static bool IsSentinelRunning => _sentinelProcess != 0 && WaitForSingleObject(_sentinelProcess, 0) == WaitTimeout;

    /// <summary>True when a sentinel has signalled (via shared memory) that it is watching.</summary>
    public static unsafe bool IsWatching => IsSentinelRunning && _shared != 0
        && (CrashSharedMemory.Flags((byte*)_shared) & CrashSharedMemory.FlagSentinelWatching) != 0;

    private const uint WaitTimeout = 0x102;

    // Startup allowance for the sentinel to begin watching (.NET start plus slow-disk margin); awaited on the pool, not the UI.
    private const int SentinelStartWaitMs = 10_000;

    /// <summary>
    /// Creates the shared memory once, copies existing breadcrumbs and context, starts the sentinel and waits until it watches. Pool
    /// thread, after Frame 1, only with crash reports on.
    /// </summary>
    private static void StartWatching()
    {
        lock (WatchGate)
        {
            if (!IsEnabled || IsSentinelRunning)
                return;

            try
            {
                LearnEnvironment();
                if (_shared == 0)
                {
                    MakeSharedMemory();
                }
                else
                {
                    // Clear a previous sentinel's watching flag (reports toggled off/on); the new one sets its own.
                    unsafe
                    {
                        CrashSharedMemory.ClearFlag((byte*)_shared, CrashSharedMemory.FlagSentinelWatching);
                    }
                }

                StartSentinel();

                var clock = Stopwatch.StartNew();
                while (!IsWatching && IsSentinelRunning && clock.ElapsedMilliseconds < SentinelStartWaitMs)
                    Thread.Sleep(20);

                if (IsWatching)
                    Note("crash", string.Create(CultureInfo.InvariantCulture, $"sentinel watching after {clock.ElapsedMilliseconds} ms"));
                else
                    AppLog.Write(IsSentinelRunning ? "The crash sentinel did not start watching in time." : "The crash sentinel left at once.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or WaitHandleCannotBeOpenedException)
            {
                AppLog.Write("The crash sentinel could not be started", ex);
            }
        }
    }

    /// <summary>Stops the sentinel (reports disabled); the shared memory remains mapped but unused.</summary>
    private static void StopWatching()
    {
        lock (WatchGate)
        {
            var process = _sentinelProcess;
            if (process == 0)
                return;

            _sentinelProcess = 0;
            using var owned = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process, ownsHandle: true);
            _ = TerminateProcess(owned, 0);
            Note("crash", "sentinel stopped");
        }
    }

    /// <summary>
    /// Captures adapters, system facts (<see cref="CrashHardware"/>, native calls under 1 ms) and engine versions once; no probing or tool runs.
    /// </summary>
    private static void LearnEnvironment()
    {
        if (_gpus is not null)
            return;

        try
        {
            // DXGI can list one card more than once (once per output it drives): a report names each card once.
            _cards = GpuAdapters.All.DistinctBy(a => (a.VendorId, a.DeviceId, a.Name, a.DriverVersion)).ToList();
            _gpus = _cards
                .Select(a => a.DriverVersion.Length > 0 ? string.Create(CultureInfo.InvariantCulture, $"{a.Name} (driver {a.DriverVersion})") : a.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or DllNotFoundException)
        {
            (_cards, _gpus) = ([], []);
        }

        CrashHardware.Learn();
        _ffmpegVersion = InstalledVersion(DependencyUpdater.Ffmpeg);
        SetContext("env.ffmpeg", _ffmpegVersion);
        CrashHardware.Whisper = InstalledVersion(DependencyUpdater.Whisper);
        CrashHardware.YtDlp = InstalledVersion(DependencyUpdater.YtDlp);
        PackEnvironment();
    }

    /// <summary>Publishes system facts to context slots for the sentinel; repeated on theme or Efficiency mode changes.</summary>
    private static void PackEnvironment()
    {
        var (theme, efficiency) = CurrentTheme();
        foreach (var (key, value) in CrashHardware.Pack(_cards, theme, efficiency ?? false))
            SetContext(key, value);
    }

    /// <summary>Notifies of theme or Efficiency mode changes (Settings, Layout selector). Cheap; no-op before the environment is captured.</summary>
    public static void EnvironmentChanged()
    {
        if (_gpus is not null)
            PackEnvironment();
    }

    private static (string? Theme, bool? Efficiency) CurrentTheme()
    {
        try
        {
            return (ThemeManager.DisplayName(AppSettings.Current.Theme), AppSettings.Current.BackgroundEfficiencyMode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, null);
        }
    }

    /// <summary>Recorded install version of a tool (e.g. an FFmpeg build tag); "custom" for user-supplied binaries.</summary>
    private static string? InstalledVersion(string tool)
    {
        if (DependencyUpdater.CustomPath(tool).Length > 0)
            return "custom";

        try
        {
            var file = Path.Combine(DependencyUpdater.ToolFolder(tool), "version.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() is { Length: > 0 and < 100 } version ? version : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static unsafe void MakeSharedMemory()
    {
        var pid = Environment.ProcessId;
        var name = string.Create(CultureInfo.InvariantCulture, $"HandPeg-Crash-{pid}-{Random.Shared.NextInt64():x16}");
        _faultSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + CrashSentinel.FaultEventSuffix);
        _namedSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + CrashSentinel.NamedEventSuffix);
        _mapping = MemoryMappedFile.CreateNew(name, CrashSharedMemory.TotalBytes, MemoryMappedFileAccess.ReadWrite);
        _view = _mapping.CreateViewAccessor(0, CrashSharedMemory.TotalBytes, MemoryMappedFileAccess.ReadWrite);

        byte* block = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref block);
        var offset = StartOffset();
        CrashSharedMemory.WriteHeader(block, pid, CreationTime(), AppPaths.IsSelfContained, CrashReports.AppVersion, Environment.ProcessorCount);
        RefreshVitals(block, Now, force: true);
        PrepareFilter();

        // Publish before filling so concurrent breadcrumbs go straight in; the backfill never overwrites newer slots (sequence-checked).
        // Times count from process start.
        _faultEvent = _faultSignal.SafeWaitHandle.DangerousGetHandle();
        _namedEvent = _namedSignal.SafeWaitHandle.DangerousGetHandle();
        _sharedName = name;
        Volatile.Write(ref _shared, (nint)block);

        for (var slot = 0; slot < Ring; slot++)
        {
            var sequence = Volatile.Read(ref Sequences[slot]);
            var (at, category, detail) = (Ats[slot], Categories[slot], Details[slot]);
            Interlocked.MemoryBarrier();
            if (sequence > 0 && category is not null && Volatile.Read(ref Sequences[slot]) == sequence)
                CrashSharedMemory.WriteBreadcrumb(block, sequence, at + offset, category, detail);
        }

        for (var slot = 0; slot < ContextKeys.Length; slot++)
            if (Volatile.Read(ref ContextKeys[slot]) is { } key)
                CrashSharedMemory.WriteContext(block, slot, key, Volatile.Read(ref ContextValues[slot]));

        for (var slot = 0; slot < CrashSanitizer.KeptFileNames; slot++)
            if (CrashSanitizer.FileNameAt(slot) is { } fileName)
                CrashSharedMemory.WriteFileName(block, slot, fileName);
    }

    // When the working set and the heap were last written into the header (breadcrumb clock seconds): at most once a second.
    private static double _vitalsAt = double.NegativeInfinity;

    /// <summary>Working set and managed heap for sentinel reports, refreshed with each breadcrumb and context update.</summary>
    private static unsafe void RefreshVitals(byte* shared, double now, bool force = false)
    {
        if (!force && now - Volatile.Read(ref _vitalsAt) < 1)
            return;

        Volatile.Write(ref _vitalsAt, now);
        CrashSharedMemory.WriteVitals(shared, Environment.WorkingSet, GC.GetTotalMemory(false));
    }

    /// <summary>
    /// Launches HandPeg.exe --crash-sentinel with the PID, shared memory name and crash folder (so it never resolves paths itself).
    ///
    /// Bypasses ProcessPipes Job Objects (which kill children on exit; the sentinel must outlive HandPeg) and breaks away from any parent
    /// job when allowed. Inherits no handles (Process.Start would leak concurrent tool pipes), has no window, and uses the system folder
    /// as working directory to avoid locking user folders. Starts at HandPeg's priority so an export cannot delay it for seconds, then
    /// drops to idle once watching (<see cref="CrashSentinel"/>).
    /// </summary>
    private static unsafe void StartSentinel()
    {
        var program = Environment.ProcessPath ?? throw new InvalidOperationException("The program's own path is not known.");
        var commandLine = string.Create(CultureInfo.InvariantCulture,
            $"\"{program}\" {CrashSentinel.Argument} {Environment.ProcessId} {_sharedName} \"{Path.TrimEndingDirectorySeparator(AppPaths.Crashes)}\"");

        const uint CreateNoWindow = 0x08000000, CreateBreakawayFromJob = 0x01000000;
        const int AccessDenied = 5;

        // STARTF_FORCEOFFFEEDBACK: otherwise Windows shows the app-starting cursor for this windowless GUI child (~5 s after launch).
        const int ForceOffFeedback = 0x00000080;

        var startup = new StartupInfo { Size = sizeof(StartupInfo), Flags = ForceOffFeedback };
        ProcessInformation started;
        fixed (char* command = (commandLine + "\0").ToCharArray())
        fixed (char* directory = Environment.SystemDirectory)
        {
            var flags = CreateNoWindow | CreateBreakawayFromJob;
            var ok = CreateProcess(null, command, 0, 0, 0, flags, 0, directory, &startup, &started);

            // A job that does not let its processes leave refuses the breakaway: then the sentinel stays in it.
            if (!ok && Marshal.GetLastPInvokeError() == AccessDenied)
                ok = CreateProcess(null, command, 0, 0, 0, flags & ~CreateBreakawayFromJob, 0, directory, &startup, &started);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        _ = CloseHandle(started.Thread);
        _sentinelProcess = started.Process;
    }

}
