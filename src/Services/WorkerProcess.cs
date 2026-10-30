using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HandPegApp.Services;

/// <summary>
/// HandPeg.exe started again as one of its own workers (the GPU compositor, the hardware benchmark: Pass 5.0-Core), the
/// way the crash sentinel is started (CrashReporter.Watch.StartSentinel) but inside HandPeg's job:
/// <list type="bullet">
/// <item>STARTF_FORCEOFFFEEDBACK: HandPeg.exe is a windowed program that never opens a window as a worker, so without it
/// Windows would show the busy pointer for five seconds (Section 2 of CLAUDE.md).</item>
/// <item>Started suspended, put into the kill-on-close foreground job (JobObjects) before its first instruction, then let go:
/// it dies with HandPeg whatever way HandPeg ends, and never runs a moment outside the job.</item>
/// <item>It inherits exactly one handle, its standard output pipe when it is read (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), and
/// no other: a tool being started at the same moment never has its pipes held open by it.</item>
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

    /// <summary>The program started: HandPeg.exe itself (or whatever program hosts the worker entry points, such as the golden tests).</summary>
    public static string ProgramPath { get; set; } = Environment.ProcessPath ?? "";

    /// <summary>A handle that is signalled when the process ends (for WaitHandle.WaitAny beside other events).</summary>
    public WaitHandle ExitedHandle => new ProcessExitWait(_process);

    public bool HasExited => WaitForSingleObject(_process, 0) == 0;

    public int ExitCode => GetExitCodeProcess(_process, out var code) ? code : -1;

    /// <summary>
    /// Starts the program with these arguments (already quoted where needed). With <paramref name="readOutput"/>, its
    /// standard output (and error) is a pipe read by <see cref="ReadLinesAsync"/>.
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
                if (!CreateProcessW(null, command, 0, 0, inherit, flags, 0, directory, &startup, &started))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // Into the job before it runs a single instruction, then on its way.
            JobObjects.Assign(started.ProcessId, jobClass, efficiencyMode: false);
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

    /// <summary>Starts it, reads every line it writes, and returns its exit code; cancelling kills it.</summary>
    public static async Task<int> RunLinesAsync(string arguments, Action<string> onLine, CancellationToken cancellationToken)
    {
        using var worker = Start(arguments, readOutput: true);
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
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, Width, Height, CountChars, CountCharsY, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint flags, nint environment, char* currentDirectory, StartupInfoEx* startup, ProcessInformation* information);

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

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeWaitHandle process, out int exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeWaitHandle process, uint exitCode);
}
