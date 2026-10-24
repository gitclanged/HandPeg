using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HandPegApp.Services;

/// <summary>How a tool HandPeg starts is run: as work the user is waiting for, or as work done behind their back.</summary>
public enum ProcessClass
{
    /// <summary>Exports, Render Preview, the queue, probes, captions: normal priority.</summary>
    Foreground,

    /// <summary>Thumbnails, waveforms, I-frame lists, proxies, indexing: low priority, Efficiency mode.</summary>
    Background,
}

/// <summary>
/// Windows Job Objects for the tools HandPeg starts (FFmpeg, ffprobe, yt-dlp, whisper): one for foreground work,
/// one for background work. A tool put in one keeps its children there too (yt-dlp's own FFmpeg), and they all
/// die with HandPeg, even after a crash (KILL_ON_JOB_CLOSE: the jobs close when the process does). Both keep off
/// the processors reserved for the interface and the player; the background one runs at Below Normal (or Idle)
/// priority, and its tools are put in Efficiency mode (EcoQoS) when that is switched on.
///
/// Where a tool cannot be put in a job (HandPeg itself runs in a job that forbids it), its priority is set on it
/// directly instead, as 2.6 did.
/// </summary>
public static partial class JobObjects
{
    private static readonly object Gate = new();
    private static IntPtr _foreground, _background;
    private static int _reservedCores = -1;
    private static bool _idle;

    /// <summary>
    /// Sets the limits of both jobs: the processors left out (the first <paramref name="reservedCores"/>), and
    /// the background job's priority. Applied to the tools running in them as well as to those started later.
    /// </summary>
    public static void Configure(int reservedCores, bool idlePriority)
    {
        lock (Gate)
        {
            if (reservedCores == _reservedCores && idlePriority == _idle && _foreground != IntPtr.Zero)
                return;

            (_reservedCores, _idle) = (reservedCores, idlePriority);
            _foreground = Apply(_foreground, NormalPriority);
            _background = Apply(_background, idlePriority ? IdlePriority : BelowNormalPriority);
        }
    }

    /// <summary>The processors every tool may use: all but the reserved ones; 0 (no limit) when that would leave fewer than two.</summary>
    public static ulong ToolAffinity
    {
        get
        {
            var count = Environment.ProcessorCount;
            var reserved = Math.Max(_reservedCores, 0);
            if (reserved == 0 || count > 64 || count - reserved < 2)
                return 0;

            var all = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
            return all & ~((1UL << reserved) - 1);
        }
    }

    /// <summary>Puts a tool that has just been started into the job for its class. Returns whether it is in one.</summary>
    public static bool Assign(int processId, ProcessClass processClass, bool efficiencyMode)
    {
        if (processId <= 0)
            return false;

        var process = OpenProcess(ProcessSetQuota | ProcessTerminate | ProcessSetInformation | ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
            return false;

        try
        {
            IntPtr job;
            lock (Gate)
            {
                if (_foreground == IntPtr.Zero)
                    Configure(Math.Max(_reservedCores, 0), _idle);
                job = processClass == ProcessClass.Background ? _background : _foreground;
            }

            var assigned = job != IntPtr.Zero && AssignProcessToJobObject(job, process);
            if (processClass == ProcessClass.Background)
            {
                // Efficiency mode: Windows runs it on the slower cores, at lower clocks, after everything else.
                if (efficiencyMode)
                {
                    var throttle = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeedThrottling, StateMask = ExecutionSpeedThrottling };
                    SetProcessInformation(process, ProcessPowerThrottling, ref throttle, (uint)Marshal.SizeOf<PowerThrottlingState>());
                }

                // Not in the job: low priority at least, set on the process itself.
                if (!assigned)
                    LowerPriority(processId);
            }

            return assigned;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static void LowerPriority(int processId)
    {
        try
        {
            using var running = Process.GetProcessById(processId);
            running.PriorityClass = _idle ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already, or not ours to change.
        }
    }

    /// <summary>Makes the job (when it is not there yet) and sets its limits; the handle, or zero when Windows would not.</summary>
    private static IntPtr Apply(IntPtr job, uint priorityClass)
    {
        if (job == IntPtr.Zero)
            job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;

        var affinity = ToolAffinity;
        var limits = new ExtendedLimitInformation
        {
            Basic = new BasicLimitInformation
            {
                LimitFlags = KillOnJobClose | LimitPriorityClass | (affinity != 0 ? LimitAffinity : 0),
                PriorityClass = priorityClass,
                Affinity = (nuint)affinity,
            },
        };

        if (!SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            AppLog.Write($"The limits of a tool job could not be set (error {Marshal.GetLastPInvokeError()}).");
        return job;
    }

    /// <summary>
    /// Stops a tool where it is (all of its threads), or lets it go on: a proxy being made while the player plays.
    /// Windows has no documented call for a whole process; ntdll's, which every process explorer uses, is used.
    /// </summary>
    public static void Suspend(int processId, bool suspended)
    {
        var process = OpenProcess(ProcessSuspendResume, false, processId);
        if (process == IntPtr.Zero)
            return;

        try
        {
            _ = suspended ? NtSuspendProcess(process) : NtResumeProcess(process);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // ----- The interface thread -----

    /// <summary>
    /// The UI thread prefers the first processor (one of the reserved ones), and runs a step above normal: the tools
    /// are kept off those processors, so the window answers however busy they are. Nothing is pinned to it.
    /// </summary>
    public static void FavourInterfaceThread()
    {
        if (Math.Max(_reservedCores, 0) == 0)
            return;

        SetThreadIdealProcessor(GetCurrentThread(), 0);
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
    }

    // ----- Windows -----

    private const int ExtendedLimitInformationClass = 9, ProcessPowerThrottling = 4;
    private const uint KillOnJobClose = 0x2000, LimitPriorityClass = 0x20, LimitAffinity = 0x10;
    private const uint NormalPriority = 0x20, BelowNormalPriority = 0x4000, IdlePriority = 0x40;
    private const uint ProcessSetQuota = 0x0100, ProcessTerminate = 0x0001, ProcessSetInformation = 0x0200, ProcessQueryLimitedInformation = 0x1000;
    private const uint ExecutionSpeedThrottling = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint length);

    private const uint ProcessSuspendResume = 0x0800;

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr process);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(IntPtr process);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadIdealProcessor(IntPtr thread, uint idealProcessor);
}
