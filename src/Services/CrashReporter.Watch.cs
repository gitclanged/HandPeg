using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace HandPegApp.Services;

// The sentinel's side of the crash reporter: the shared memory it reads after a native fault, the two events through
// which the fault filter asks it to name the module, and the sentinel process itself.
public static partial class CrashReporter
{
    // The shared memory, mapped for the rest of the session once made (never unmapped: a breadcrumb may be on its way
    // into it from another thread). Zero until then; the filter and the breadcrumbs write nothing while it is.
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

    // The cards and the tool versions, learnt once after the first frame; a report written before that has none.
    // The rest of the computer (processor, memory, display, power) is CrashHardware's, learnt at the same moment.
    private static List<string>? _gpus;
    private static List<GpuAdapter> _cards = [];
    private static string? _ffmpegVersion;
    private static string? _mpvVersion;

    /// <summary>
    /// libmpv's own version string, for the report's environment, when the player knows it (the "mpv-version"
    /// property). Until it is set, the version HandPeg installed is used.
    /// </summary>
    public static string? MpvVersion
    {
        get => _mpvVersion;
        set
        {
            _mpvVersion = value;
            SetContext("env.mpv", value);
        }
    }

    private static bool IsSentinelRunning => _sentinelProcess != 0 && WaitForSingleObject(_sentinelProcess, 0) == WaitTimeout;

    /// <summary>Whether a sentinel is running and has the process in hand (it says so in the shared memory): a death now is seen.</summary>
    public static unsafe bool IsWatching => IsSentinelRunning && _shared != 0
        && (CrashSharedMemory.Flags((byte*)_shared) & CrashSharedMemory.FlagSentinelWatching) != 0;

    private const uint WaitTimeout = 0x102;

    // How long a sentinel just started is given to open the process and say it is watching: a .NET program's start,
    // with room for a slow disk. Waited for on a pool thread; the window carries on meanwhile.
    private const int SentinelStartWaitMs = 10_000;

    /// <summary>
    /// Makes the shared memory (once), copies the breadcrumbs and the context left so far into it, starts the sentinel
    /// and waits for it to say it is watching. On a pool thread, after the first frame, only when crash reports are on.
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
                    // A sentinel stopped earlier (crash reports switched off, then on again) said it was watching: the
                    // new one says so for itself.
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

    /// <summary>Stops the sentinel (crash reports switched off). The shared memory stays, unused, for the rest of the session.</summary>
    private static void StopWatching()
    {
        lock (WatchGate)
        {
            var process = _sentinelProcess;
            if (process == 0)
                return;

            _sentinelProcess = 0;
            _ = TerminateProcess(process, 0);
            _ = CloseHandle(process);
            Note("crash", "sentinel stopped");
        }
    }

    /// <summary>
    /// The graphics cards, the computer (<see cref="CrashHardware"/>: native calls, well under a millisecond) and the
    /// engines' versions, learnt once: nothing is probed, and no tool is run.
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
        if (_mpvVersion is null)
            SetContext("env.mpv", _mpvVersion = InstalledVersion(DependencyUpdater.Mpv));
        CrashHardware.Whisper = InstalledVersion(DependencyUpdater.Whisper);
        CrashHardware.YtDlp = InstalledVersion(DependencyUpdater.YtDlp);
        PackEnvironment();
    }

    /// <summary>The computer's facts into their context slots, for the sentinel; again when the theme or Efficiency mode changes.</summary>
    private static void PackEnvironment()
    {
        var (theme, efficiency) = CurrentTheme();
        foreach (var (key, value) in CrashHardware.Pack(_cards, theme, efficiency ?? false))
            SetContext(key, value);
    }

    /// <summary>Tells the crash reporter the theme or Efficiency mode changed (Settings, the Layout selector). Cheap; nothing before the environment is learnt.</summary>
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

    /// <summary>The version HandPeg wrote down when it installed a tool ("N-127957-g935f29eaae-20261023"); "custom" for one of the user's own.</summary>
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

        // Published first, then filled: a breadcrumb left meanwhile goes straight in, and the copy below never puts an
        // older one over it (each slot keeps its number). The shared memory counts from the process's start.
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

    /// <summary>The working set and the managed heap, for a report the sentinel writes: refreshed as breadcrumbs and context arrive.</summary>
    private static unsafe void RefreshVitals(byte* shared, double now, bool force = false)
    {
        if (!force && now - Volatile.Read(ref _vitalsAt) < 1)
            return;

        Volatile.Write(ref _vitalsAt, now);
        CrashSharedMemory.WriteVitals(shared, Environment.WorkingSet, GC.GetTotalMemory(false));
    }

    /// <summary>
    /// Starts the sentinel: this same program with --crash-sentinel, the process to watch, the shared memory's name and
    /// the folder to write into (handed over so it never works out portable or installed paths itself).
    ///
    /// Deliberately not through ProcessPipes and its Job Objects, which every tool HandPeg starts goes through: those
    /// jobs kill what is in them when HandPeg closes, and the sentinel has to outlive HandPeg to do its work. For the
    /// same reason it is started out of any job HandPeg itself was started in, where that job allows it. It inherits no
    /// handles (a pipe of a tool being started at that moment would otherwise be held open by it: Process.Start would
    /// pass them all on), shows no window, and works in the system folder, so it holds no folder of the user's open. It
    /// starts at HandPeg's own priority, so that its start (a few dozen milliseconds of processor time) is not held up
    /// for seconds behind an export using every core, which would leave a crash in that time unseen; once it is
    /// watching it moves itself to the lowest priority class (<see cref="CrashSentinel"/>).
    /// </summary>
    private static unsafe void StartSentinel()
    {
        var program = Environment.ProcessPath ?? throw new InvalidOperationException("The program's own path is not known.");
        var commandLine = string.Create(CultureInfo.InvariantCulture,
            $"\"{program}\" {CrashSentinel.Argument} {Environment.ProcessId} {_sharedName} \"{Path.TrimEndingDirectorySeparator(AppPaths.Crashes)}\"");

        const uint CreateNoWindow = 0x08000000, CreateBreakawayFromJob = 0x01000000;
        const int AccessDenied = 5;

        // STARTF_FORCEOFFFEEDBACK: HandPeg.exe is a windowed program, so without it Windows shows the "working in
        // background" pointer for this child until it opens a window, which it never does: about five seconds of a
        // spinning pointer just after HandPeg's own window had come up (measured in Pass 4.1C).
        const int ForceOffFeedback = 0x00000080;

        var startup = new StartupInfo { Size = sizeof(StartupInfo), Flags = ForceOffFeedback };
        ProcessInformation started;
        fixed (char* command = (commandLine + "\0").ToCharArray())
        fixed (char* directory = Environment.SystemDirectory)
        {
            var flags = CreateNoWindow | CreateBreakawayFromJob;
            var ok = CreateProcessW(null, command, 0, 0, 0, flags, 0, directory, &startup, &started);

            // A job that does not let its processes leave refuses the breakaway: then the sentinel stays in it.
            if (!ok && Marshal.GetLastPInvokeError() == AccessDenied)
                ok = CreateProcessW(null, command, 0, 0, 0, flags & ~CreateBreakawayFromJob, 0, directory, &startup, &started);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        _ = CloseHandle(started.Thread);
        _sentinelProcess = started.Process;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, Width, Height, CountChars, CountCharsY, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint flags, nint environment, char* currentDirectory, StartupInfo* startup, ProcessInformation* information);

    [LibraryImport("kernel32.dll")]
    private static partial int TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
