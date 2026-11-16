using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HandPegApp.Native;

/// <summary>STARTUPINFOW. For STARTUPINFOEXW (EXTENDED_STARTUPINFO_PRESENT) a caller passes its extended struct, which begins with this.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    public int Size;
    public nint Reserved, Desktop, Title;
    public int X, Y, Width, Height, CountChars, CountCharsY, FillAttribute, Flags;
    public short ShowWindow, Reserved2;
    public nint Reserved3, StdInput, StdOutput, StdError;
}

/// <summary>PROCESS_INFORMATION: the new process's and thread's handles (the caller owns and closes both) and ids.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint Process, Thread;
    public int ProcessId, ThreadId;
}

/// <summary>SYSTEM_POWER_STATUS.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
    public uint BatteryLifeTime, BatteryFullLifeTime;
}

/// <summary>
/// kernel32's events, waits and high-resolution waitable timers: one declaration each, for the sound engine, the decode lanes'
/// pacing and the Live Encoded Preview (which used to borrow the sound engine's declarations). New Win32 imports belong in this
/// folder (src/Native), one canonical declaration per function. Only stubs and constants: no type initializer, nothing at start-up.
/// </summary>
internal static unsafe partial class Kernel32
{
    /// <summary>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION: a timer that wakes to the half millisecond, not the 15.6 ms tick.</summary>
    public const uint HighResolutionTimer = 0x2, TimerAllAccess = 0x1F0003;

    /// <summary>INFINITE: a wait with no timeout.</summary>
    public const uint Infinite = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll")]
    public static partial nint CreateEventW(nint attributes, int manualReset, int initialState, nint name);

    [LibraryImport("kernel32.dll")]
    public static partial int SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    public static partial int SetWaitableTimer(nint timer, long* dueTime, int period, nint completion, nint argument, int resume);

    [LibraryImport("kernel32.dll")]
    public static partial int CancelWaitableTimer(nint timer);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? module);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProcAddress(nint module, string name);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetProcessHeap();

    /// <summary>Coalesces the heap's free blocks and decommits what it can (returns the largest free block).</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nuint HeapCompact(nint heap, uint flags);

    // ----- Processes and threads -----

    /// <summary>A process by id, owned by the returned handle (invalid when it cannot be opened; Marshal.GetLastPInvokeError says why).</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeWaitHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    /// <summary>The calling process's pseudo-handle (never closed).</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    /// <summary>The calling thread's pseudo-handle (never closed).</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeHandle process, uint exitCode);

    /// <summary>A process's creation, exit, kernel and user times (FILETIME ticks); 0 on failure.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial int GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>A wait on a handle a SafeHandle owns: the handle is kept alive for the wait (the raw form is <see cref="WaitForSingleObject(nint, uint)"/>).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    public static partial uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    /// <summary>
    /// CreateProcessW. The caller owns both handles of <paramref name="information"/>; with EXTENDED_STARTUPINFO_PRESENT it passes a
    /// STARTUPINFOEXW cast to <see cref="StartupInfo"/>*, which is its first member.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcess(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint flags, nint environment, char* currentDirectory, StartupInfo* startup, ProcessInformation* information);

    [LibraryImport("kernel32.dll")]
    public static partial uint SetErrorMode(uint mode);

    // ----- Files, memory and power -----

    /// <summary>CreateFileW, the handle owned by the returned SafeFileHandle (invalid on failure; Marshal.GetLastPInvokeError says why).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
