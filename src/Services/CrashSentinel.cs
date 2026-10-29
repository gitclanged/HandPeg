using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace HandPegApp.Services;

/// <summary>
/// HandPeg.exe --crash-sentinel &lt;pid&gt; &lt;shared memory&gt; &lt;crashes folder&gt;: a second copy of HandPeg that does
/// one thing. It waits, using no processor time, for the HandPeg it was started by to end, and when that HandPeg died
/// of a fault and wrote nothing itself (a native crash in libmpv or a graphics driver leaves nothing standing in the
/// process to write with), it writes the report from the shared memory HandPeg kept for it (<see cref="CrashSharedMemory"/>).
///
/// Handled at the very top of Program.Main, before the updater, the settings, the presets or anything of WPF, and
/// kept that way: this class and what it calls never touch <see cref="AppPaths"/>, <see cref="AppSettings"/> or a
/// window. It leaves without a word when the process it should watch is already gone, when the process id has been
/// reused (the process's creation time is compared with the one in the shared memory), or when the shared memory is not
/// there. It runs in the lowest priority class, with its memory given back while it waits.
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

                var process = OpenProcess(Synchronize | QueryLimitedInformation, 0, pid);
                if (process == 0)
                    return 0;

                try
                {
                    // The same process, not another one that was given its id after it ended.
                    if (!Times(process, out var created, out _) || created != CrashSharedMemory.CreationTime(block))
                        return 0;

                    // HandPeg is told it is seen from now on, and the sentinel gets out of everyone's way until it is
                    // needed: its memory is given back, and it waits, using no processor time, in the lowest priority
                    // class there is (it starts at HandPeg's own, so that a busy computer does not hold its start up for
                    // seconds). Its one thread is made time-critical within that class (15), which costs
                    // nothing while it waits and means that when the fault filter wakes it, it answers at once, even
                    // with every core busy with an export: the filter holds the process for it at most two seconds, and
                    // a sentinel woken at idle priority behind an export could miss them.
                    CrashSharedMemory.SetFlag(block, CrashSharedMemory.FlagSentinelWatching);
                    _ = SetPriorityClass(GetCurrentProcess(), 0x40); // IDLE_PRIORITY_CLASS
                    _ = SetThreadPriority(GetCurrentThread(), 15); // THREAD_PRIORITY_TIME_CRITICAL
                    _ = SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

                    if (!Watch(process, pid, args[2], block))
                    {
                        // A fault was recorded and the process has not ended since: it is stuck on its way out (a
                        // dialog, a lock in a broken process). The report is written now, while it hangs, and only once.
                        var written = TryWrite(Report(block, null, created, 0), folder);
                        _ = WaitForSingleObject(process, Infinite);
                        return written ? 1 : 0;
                    }

                    if (GetExitCodeProcess(process, out var exitCode) == 0)
                        return 0;
                    _ = Times(process, out _, out var exited);

                    var flags = CrashSharedMemory.Flags(block);
                    if ((flags & (CrashSharedMemory.FlagCleanExit | CrashSharedMemory.FlagManagedReportWritten)) != 0 || exitCode == 0)
                        return 0;

                    // Ended while the user was closing it (the save prompt up, then closed from the taskbar or Task
                    // Manager): no fault, so no crash to report.
                    if ((flags & (CrashSharedMemory.FlagUserClosing | CrashSharedMemory.FlagNativeFault)) == CrashSharedMemory.FlagUserClosing)
                        return 0;

                    return TryWrite(Report(block, exitCode, created, exited), folder) ? 1 : 0;
                }
                finally
                {
                    _ = CloseHandle(process);
                }
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    /// <summary>Writes a report into the folder; false when the folder cannot be written to (there is no one left to tell).</summary>
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

    // How long a process that recorded a fault may take to end before it is taken to be stuck: Windows Error Reporting
    // can hold a crashed process for a while (writing a dump), so this is generous.
    private const uint StuckAfterFaultMs = 60_000;

    /// <summary>
    /// Waits for the process to end. If its fault filter signals first, the process is still there, held in the filter:
    /// the module the faulting address is in is looked up in its module list and written into the shared memory, and
    /// the filter is told, so it can let the process go. False when the process did that and then did not end.
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

    /// <summary>Finds the module the faulting address is in, by the live process's module list, and writes its file name (never its path).</summary>
    private static unsafe void NameFaultingModule(int pid, byte* block)
    {
        var address = CrashSharedMemory.FaultAddress(block);
        var process = OpenProcess(QueryInformation | VmRead, 0, pid);
        if (process == 0)
            return;

        try
        {
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
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>
    /// The report of a death HandPeg could not write up itself, from what it left in the shared memory. No exit code: the
    /// process recorded a fault and is stuck on its way out.
    /// </summary>
    private static unsafe CrashReport Report(byte* block, uint? exitCode, long created, long exited)
    {
        // The names of the files HandPeg had in use are kept out of everything below, as HandPeg keeps them out of its own reports.
        foreach (var name in CrashSharedMemory.FileNames(block))
            _ = CrashSanitizer.KeepOutFileName(name);

        // The filter records a CLR exception as well (it sees every fault that reaches the top); that one is a managed
        // exception whose own report could not be written, not a native fault.
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
                Mpv = Env("env.mpv"),
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

        // On a developer's PC, what the flight recorder last wrote (4.4.0).
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
    private static partial nint OpenProcess(uint access, int inheritHandle, int processId);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static partial int GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial int GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial int SetPriorityClass(nint process, uint priorityClass);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    private static partial int SetThreadPriority(nint thread, int priority);

    [LibraryImport("kernel32.dll")]
    private static partial int SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumProcessModulesEx")]
    private static unsafe partial int EnumProcessModulesEx(nint process, nint* modules, uint size, out uint needed, uint filter);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleInformation")]
    private static partial int GetModuleInformation(nint process, nint module, out ModuleInfo info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleBaseNameW")]
    private static unsafe partial uint GetModuleBaseNameW(nint process, nint module, char* name, uint size);
}
