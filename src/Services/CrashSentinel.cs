using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using HandPegApp.Native;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp.Services;

/// <summary>
/// HandPeg.exe --crash-sentinel &lt;pid&gt; &lt;shared memory&gt; &lt;crashes folder&gt;: a watcher process that idles (zero CPU) until its
/// parent exits; if the parent died of a fault without reporting (native driver/system crashes leave nothing to write with), it writes
/// the report from <see cref="CrashSharedMemory"/>.
///
/// Dispatched at the top of Program.Main before the updater, settings, presets or WPF; this class never touches <see cref="AppPaths"/>,
/// <see cref="AppSettings"/> or windows. Exits silently if the target is gone, the PID was reused (creation time mismatch) or the
/// shared memory is missing. Runs at the lowest priority class with its working set released while waiting.
/// </summary>
public static partial class CrashSentinel
{
    /// <summary>The first argument that makes HandPeg.exe a sentinel.</summary>
    public const string Argument = "--crash-sentinel";

    // The handshake with the fault filter (CrashReporter.Filter): the shared memory's name with these after it.
    public const string FaultEventSuffix = "-fault";
    public const string NamedEventSuffix = "-named";

    private const uint Synchronize = 0x00100000;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint QueryInformation = 0x0400;
    private const uint VmRead = 0x0010;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;

    /// <summary>Runs the sentinel; returns the process's exit code (0 in every case but a report written, 1).</summary>
    public static unsafe int Run(string[] args)
    {
        // Never a dialog of its own, whatever happens to it.
        _ = SetErrorMode(0x0001 | 0x0002); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX

        if (args.Length < 4
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0
            || args[2].Length == 0 || !Path.IsPathFullyQualified(args[3]))
        {
            return 0;
        }

        var folder = args[3];
        MemoryMappedFile mapping;
        try
        {
            mapping = MemoryMappedFile.OpenExisting(args[2], MemoryMappedFileRights.ReadWrite);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return 0; // The process (and with it the shared memory) is already gone.
        }

        using (mapping)
        using (var view = mapping.CreateViewAccessor(0, CrashSharedMemory.TotalBytes, MemoryMappedFileAccess.ReadWrite))
        {
            byte* block = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref block);
            try
            {
                if (!CrashSharedMemory.IsValid(block) || CrashSharedMemory.WatchedPid(block) != pid)
                    return 0;

                using var owned = OpenProcess(Synchronize | QueryLimitedInformation, false, pid);
                if (owned.IsInvalid)
                    return 0;

                // The raw handle for the waits below, alive while `owned` is (disposed when this returns).
                var process = owned.DangerousGetHandle();

                // The same process, not another one that was given its id after it ended.
                if (!Times(process, out var created, out _) || created != CrashSharedMemory.CreationTime(block))
                    return 0;

                    // Signal "watching", release memory and drop to the idle priority class (startup ran at HandPeg's priority to
                    // avoid multi-second delays on busy systems). The single thread is time-critical (15) within that class: free
                    // while waiting, yet it responds instantly when the fault filter wakes it, even under full export load (the
                    // filter holds the process at most 2 s).
                CrashSharedMemory.SetFlag(block, CrashSharedMemory.FlagSentinelWatching);
                _ = SetPriorityClass(GetCurrentProcess(), 0x40); // IDLE_PRIORITY_CLASS
                _ = SetThreadPriority(GetCurrentThread(), 15); // THREAD_PRIORITY_TIME_CRITICAL
                _ = SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

                if (!Watch(process, pid, args[2], block))
                {
                        // Fault recorded but the process has not exited (stuck on a dialog or lock): write the report once now.
                    var written = TryWrite(Report(block, null, created, 0), folder);
                    _ = WaitForSingleObject(process, Infinite);
                    return written ? 1 : 0;
                }

                if (!GetExitCodeProcess(owned, out var exitCode))
                    return 0;
                _ = Times(process, out _, out var exited);

                var flags = CrashSharedMemory.Flags(block);
                if ((flags & (CrashSharedMemory.FlagCleanExit | CrashSharedMemory.FlagManagedReportWritten)) != 0 || exitCode == 0)
                    return 0;

                    // Terminated during an intentional close (save prompt, then taskbar/Task Manager): not a crash.
                if ((flags & (CrashSharedMemory.FlagUserClosing | CrashSharedMemory.FlagNativeFault)) == CrashSharedMemory.FlagUserClosing)
                    return 0;

                return TryWrite(Report(block, exitCode, created, exited), folder) ? 1 : 0;
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    /// <summary>Writes a report to the folder; false when unwritable (nothing else can be done).</summary>
    private static bool TryWrite(CrashReport report, string folder)
    {
        try
        {
            CrashReports.Write(report, folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // Grace period before a faulted process is considered stuck; generous because WER may hold it while writing a dump.
    private const uint StuckAfterFaultMs = 60_000;

    /// <summary>
    /// Waits for exit. If the fault filter signals first (process held), resolves the faulting module into shared memory and releases
    /// the filter. False when the process then fails to exit.
    /// </summary>
    private static unsafe bool Watch(nint process, int pid, string sharedName, byte* block)
    {
        EventWaitHandle? fault = null, named = null;
        try
        {
            fault = EventWaitHandle.OpenExisting(sharedName + FaultEventSuffix);
            named = EventWaitHandle.OpenExisting(sharedName + NamedEventSuffix);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or IOException or UnauthorizedAccessException)
        {
            // Without the handshake the report still has the code, the address and the module's base.
        }

        using (fault)
        using (named)
        {
            if (fault is null || named is null)
            {
                _ = WaitForSingleObject(process, Infinite);
                return true;
            }

            nint* handles = stackalloc nint[] { process, fault.SafeWaitHandle.DangerousGetHandle() };
            if (WaitForMultipleObjects(2, handles, 0, Infinite) != WaitObject0 + 1)
                return true;

            try
            {
                NameFaultingModule(pid, block);
            }
            finally
            {
                named.Set();
            }

            return WaitForSingleObject(process, StuckAfterFaultMs) == WaitObject0;
        }
    }

    /// <summary>Resolves the faulting module from the live module list and records its file name (never a path).</summary>
    private static unsafe void NameFaultingModule(int pid, byte* block)
    {
        var address = CrashSharedMemory.FaultAddress(block);
        using var owned = OpenProcess(QueryInformation | VmRead, false, pid);
        if (owned.IsInvalid)
            return;

        var process = owned.DangerousGetHandle();

        var modules = new nint[1024];
        fixed (nint* list = modules)
        {
            if (EnumProcessModulesEx(process, list, (uint)(modules.Length * sizeof(nint)), out var needed, 3) == 0) // LIST_MODULES_ALL
                return;

            var count = Math.Min((int)(needed / (uint)sizeof(nint)), modules.Length);
            var name = stackalloc char[260];
            for (var i = 0; i < count; i++)
            {
                if (GetModuleInformation(process, list[i], out var info, (uint)sizeof(ModuleInfo)) == 0)
                    continue;

                var start = (ulong)info.BaseOfDll;
                if (address < start || address >= start + info.SizeOfImage)
                    continue;

                var length = GetModuleBaseNameW(process, list[i], name, 260);
                CrashSharedMemory.WriteModule(block, length > 0 ? new string(name, 0, (int)length) : "", start, address - start);
                return;
            }
        }
    }

    /// <summary>
    /// Builds a report from shared memory for a death HandPeg could not report. No exit code means a faulted process stuck on exit.
    /// </summary>
    private static unsafe CrashReport Report(byte* block, uint? exitCode, long created, long exited)
    {
        // Redact in-use file names throughout, as in-process reports do.
        foreach (var name in CrashSharedMemory.FileNames(block))
            _ = CrashSanitizer.KeepOutFileName(name);

        // The filter also records CLR exceptions; those are unreported managed exceptions, not native faults.
        var native = (CrashSharedMemory.Flags(block) & CrashSharedMemory.FlagNativeFault) != 0 && CrashSharedMemory.ExceptionCode(block) != 0xE0434352;
        var code = native || exitCode is null ? CrashSharedMemory.ExceptionCode(block) : exitCode.Value;
        var type = CrashReports.DescribeCode(code);
        var ending = exitCode is { } ended
            ? $"The process ended with {CrashReports.DescribeCode(ended)}."
            : string.Create(CultureInfo.InvariantCulture, $"The process had not ended {StuckAfterFaultMs / 1000} s later.");

        string? module = null, offset = null;
        string message;
        if (native)
        {
            var address = CrashSharedMemory.FaultAddress(block);
            var name = CrashSharedMemory.ModuleName(block);
            var moduleBase = CrashSharedMemory.ModuleBase(block);
            if (name.Length > 0)
            {
                module = CrashSanitizer.Sanitize(Path.GetFileName(name), 96);
                offset = string.Create(CultureInfo.InvariantCulture, $"0x{CrashSharedMemory.ModuleOffset(block):x}");
            }
            else if (moduleBase != 0)
            {
                offset = string.Create(CultureInfo.InvariantCulture, $"0x{address - moduleBase:x}");
            }

            var where = module is not null ? $"in {module}+{offset}"
                : moduleBase != 0 ? $"in a module not named (base+{offset})"
                : string.Create(CultureInfo.InvariantCulture, $"at 0x{address:x}, in no module");
            message = CrashSharedMemory.Access(block) is { } access && code == 0xC0000005
                ? string.Create(CultureInfo.InvariantCulture, $"Access violation {(access.Kind switch { 0 => "reading", 1 => "writing", 8 => "executing", _ => "at" })} address 0x{access.Address:x}, {where}. {ending}")
                : $"A native fault {where}. {ending}";
        }
        else if (exitCode is null)
        {
            message = $"HandPeg's own report of a managed exception was not written, and the process did not end. {ending}";
        }
        else
        {
            message = $"HandPeg ended with {CrashReports.DescribeCode(exitCode.Value)} without saying it would and without a report of its own (ended from outside, or a fault nothing in it could catch).";
        }

        // When it ended, or for a process stuck on its way out, now.
        var end = exited > 0 ? exited : DateTime.UtcNow.ToFileTimeUtc();
        var context = CrashSharedMemory.Context(block);
        string? Env(string key) => context.TryGetValue(key, out var value) && value.Length > 0 ? CrashSanitizer.Sanitize(value, 160) : null;

        var report = new CrashReport
        {
            Format = CrashReport.FormatName,
            Version = CrashReport.CurrentVersion,
            Signature = CrashSignature.Compute(type, [], module),
            AppVersion = CrashSharedMemory.AppVersion(block) is { Length: > 0 } version ? version : CrashReports.AppVersion,
            CrashedAtUtc = DateTime.FromFileTimeUtc(end),
            Kind = native ? CrashKind.Native : CrashKind.Sentinel,
            Fatal = true,
            ExceptionType = type,
            Message = message,
            NativeModule = module,
            NativeOffset = offset,
            ExitCode = exitCode,
            Environment = new CrashEnvironment
            {
                Os = CrashReports.OsDescription(),
                Runtime = CrashReports.RuntimeDescription(),
                ProcessorCount = CrashSharedMemory.ProcessorCount(block),
                Gpus = context.TryGetValue("env.gpus", out var gpus)
                    ? gpus.Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(g => CrashSanitizer.Sanitize(g, 160)).ToList()
                    : [],
                WorkingSetMb = CrashSharedMemory.WorkingSet(block) / (1024 * 1024),
                ManagedHeapMb = CrashSharedMemory.ManagedHeap(block) / (1024 * 1024),
                UptimeSeconds = end > created && created > 0 ? Math.Round((end - created) / 1e7, 1) : 0,
                Mode = Env("mode") ?? "",
                SelfContained = CrashSharedMemory.SelfContained(block),
                Ffmpeg = Env("env.ffmpeg"),
            },
            Breadcrumbs = CrashSharedMemory.Breadcrumbs(block).Select(c => new CrashBreadcrumb
            {
                At = Math.Round(c.At, 3),
                Category = CrashSanitizer.Sanitize(c.Category, 24),
                Detail = CrashSanitizer.Sanitize(c.Detail, 300),
            }).ToList(),
            Context = context.Where(c => !c.Key.StartsWith("env.", StringComparison.Ordinal) && c.Value.Length > 0)
                .ToDictionary(c => CrashSanitizer.Sanitize(c.Key, 24), c => CrashSanitizer.Sanitize(c.Value, 300)) is { Count: > 0 } kept ? kept : null,
        };

        // The computer, as HandPeg packed it (the cards with it: env.adapters stands for the old env.gpus).
        CrashHardware.Unpack(context, report.Environment);

        // Developer flight recorder output, when enabled.
        report.DevVerbose = DevFlightRecorder.FromDisk();
        return report;
    }

    private static bool Times(nint process, out long created, out long exited)
    {
        var ok = GetProcessTimes(process, out created, out exited, out _, out _) != 0;
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModuleInfo
    {
        public nint BaseOfDll;
        public uint SizeOfImage;
        public nint EntryPoint;
    }

    // ----- Windows -----

    [LibraryImport("kernel32.dll")]
    private static partial int SetPriorityClass(nint process, uint priorityClass);

    [LibraryImport("kernel32.dll")]
    private static partial int SetThreadPriority(nint thread, int priority);

    [LibraryImport("kernel32.dll")]
    private static partial int SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumProcessModulesEx")]
    private static unsafe partial int EnumProcessModulesEx(nint process, nint* modules, uint size, out uint needed, uint filter);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleInformation")]
    private static partial int GetModuleInformation(nint process, nint module, out ModuleInfo info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleBaseNameW")]
    private static unsafe partial uint GetModuleBaseNameW(nint process, nint module, char* name, uint size);
}
