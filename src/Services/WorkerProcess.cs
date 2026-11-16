using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HandPegApp.Native;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp.Services;

/// <summary>
/// Launches HandPeg.exe as a worker (GPU compositor, hardware benchmark), like the crash sentinel but inside HandPeg's job:
/// <list type="bullet">
/// <item>STARTF_FORCEOFFFEEDBACK: avoids ~5 s of busy cursor for a windowless GUI child.</item>
/// <item>Created suspended and assigned to the kill-on-close foreground job (JobObjects) before resuming, so it never runs outside the
/// job and dies with HandPeg.</item>
/// <item>Inherits exactly one handle, its stdout pipe when read (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so concurrent tool pipes are
/// never held open.</item>
/// </list>
/// </summary>
internal sealed partial class WorkerProcess : IDisposable
{
    private readonly SafeWaitHandle _process;
    private readonly FileStream? _output;

    private WorkerProcess(SafeWaitHandle process, int processId, FileStream? output)
    {
        _process = process;
        ProcessId = processId;
        _output = output;
    }

    public int ProcessId { get; }

    /// <summary>Executable to launch: HandPeg.exe, or another worker host (e.g. golden tests).</summary>
    public static string ProgramPath { get; set; } = Environment.ProcessPath ?? "";

    /// <summary>A handle that is signalled when the process ends (for WaitHandle.WaitAny beside other events).</summary>
    public WaitHandle ExitedHandle => new ProcessExitWait(_process);

    public bool HasExited => WaitForSingleObject(_process, 0) == 0;

    public int ExitCode => GetExitCodeProcess(_process, out var code) ? (int)code : -1;

    /// <summary>
    /// Starts with pre-quoted arguments; with <paramref name="readOutput"/>, stdout/stderr go to a pipe read by <see cref="ReadLinesAsync"/>.
    /// </summary>
    public static unsafe WorkerProcess Start(string arguments, bool readOutput, ProcessClass jobClass = ProcessClass.Foreground)
    {
        var program = ProgramPath;
        if (program.Length == 0)
            throw new InvalidOperationException("The program's own path is not known.");

        var commandLine = string.Create(CultureInfo.InvariantCulture, $"\"{program}\" {arguments}") + "\0";
        nint readEnd = 0, writeEnd = 0;
        var attributeList = (nint)0;
        try
        {
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = sizeof(StartupInfoEx), Flags = ForceOffFeedback } };
            var inherit = 0;
            if (readOutput)
            {
                var security = new SecurityAttributes { Length = sizeof(SecurityAttributes), InheritHandle = 1 };
                if (!CreatePipe(out readEnd, out writeEnd, &security, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                _ = SetHandleInformation(readEnd, HandleFlagInherit, 0);

                // Only the pipe's write end goes to the child.
                nuint size = 0;
                _ = InitializeProcThreadAttributeList(0, 1, 0, &size);
                attributeList = Marshal.AllocHGlobal((nint)size);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, &size))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                var handles = stackalloc nint[1];
                handles[0] = writeEnd;
                if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, handles, (nuint)sizeof(nint), 0, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());

                startup.AttributeList = attributeList;
                startup.StartupInfo.Flags |= UseStdHandles;
                startup.StartupInfo.StdOutput = writeEnd;
                startup.StartupInfo.StdError = writeEnd;
                inherit = 1;
            }

            ProcessInformation started;
            fixed (char* command = commandLine)
            fixed (char* directory = Environment.SystemDirectory)
            {
                var flags = CreateNoWindow | CreateSuspended | (readOutput ? ExtendedStartupInfoPresent : 0u);
                if (!CreateProcess(null, command, 0, 0, inherit, flags, 0, directory, (StartupInfo*)&startup, &started))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // Assign to the job before resuming. A foreground worker (compositor) outside the job could outlive a crash holding a GPU
            // device, so assignment failure terminates it and fails the start; background workers (hardware test) also watch HandPeg,
            // so failure is only logged.
            if (!JobObjects.Assign(started.ProcessId, jobClass, efficiencyMode: false))
            {
                AppLog.Write($"Worker process: {started.ProcessId} could not be put in its job ({jobClass})");
                if (jobClass == ProcessClass.Foreground)
                {
                    using (var unjobbed = new SafeWaitHandle(started.Process, ownsHandle: true))
                        _ = TerminateProcess(unjobbed, 1);
                    _ = CloseHandle(started.Thread);
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The worker could not be put in HandPeg's job.");
                }
            }

            _ = ResumeThread(started.Thread);
            _ = CloseHandle(started.Thread);

            FileStream? output = null;
            if (readOutput)
            {
                // The child holds its own copy of the write end now: this one closed, the pipe ends when the child does.
                _ = CloseHandle(writeEnd);
                writeEnd = 0;
                output = new FileStream(new SafeFileHandle(readEnd, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
                readEnd = 0;
            }

            return new WorkerProcess(new SafeWaitHandle(started.Process, ownsHandle: true), started.ProcessId, output);
        }
        finally
        {
            if (attributeList != 0)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (writeEnd != 0)
                _ = CloseHandle(writeEnd);
            if (readEnd != 0)
                _ = CloseHandle(readEnd);
        }
    }

    /// <summary>Reads its output line by line (on a pool thread) until it closes; completes with the exit code.</summary>
    public Task<int> ReadLinesAsync(Action<string> onLine, CancellationToken cancellationToken)
    {
        if (_output is null)
            throw new InvalidOperationException("The worker was started without reading its output.");

        return Task.Run(() =>
        {
            using var registration = cancellationToken.Register(Kill);
            using var reader = new StreamReader(_output, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                    onLine(line);
            }

            _ = WaitForSingleObject(_process, Infinite);
            cancellationToken.ThrowIfCancellationRequested();
            return ExitCode;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Runs to completion, streaming every output line, and returns the exit code; cancel kills it. Foreground job unless
    /// <paramref name="jobClass"/> overrides (e.g. the first-run quick hardware check uses background).
    /// </summary>
    public static async Task<int> RunLinesAsync(string arguments, Action<string> onLine, CancellationToken cancellationToken, ProcessClass jobClass = ProcessClass.Foreground)
    {
        using var worker = Start(arguments, readOutput: true, jobClass);
        return await worker.ReadLinesAsync(onLine, cancellationToken).ConfigureAwait(false);
    }

    public bool WaitForExit(int milliseconds) => WaitForSingleObject(_process, milliseconds < 0 ? Infinite : (uint)milliseconds) == 0;

    public void Kill()
    {
        if (!HasExited)
            _ = TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        _output?.Dispose();
        _process.Dispose();
    }

    /// <summary>The process handle as a WaitHandle that does not own it.</summary>
    private sealed class ProcessExitWait : WaitHandle
    {
        public ProcessExitWait(SafeWaitHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    // ----- Win32 -----

    private const uint CreateNoWindow = 0x08000000, CreateSuspended = 0x00000004, ExtendedStartupInfoPresent = 0x00080000, Infinite = 0xFFFFFFFF;
    private const int ForceOffFeedback = 0x00000080, UseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 1;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreatePipe(out nint readPipe, out nint writePipe, SecurityAttributes* attributes, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool InitializeProcThreadAttributeList(nint list, int count, uint flags, nuint* size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll")]
    private static partial uint ResumeThread(nint thread);

}
