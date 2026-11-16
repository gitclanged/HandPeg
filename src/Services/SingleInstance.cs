using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Single-instance project hand-off: launching with a .hproj/.hpb (Explorer open command) forwards it over a user-only pipe to the
/// running instance with the same user, session and data folder; that instance prompts for unsaved changes, opens it and activates,
/// and the new process exits windowless. No project, a different data folder (portable vs. installed) or any hand-off failure starts
/// normally.
///
/// Law 1: the running instance takes a named mutex in Program.Start (one kernel call) and listens only after Frame 1 on the pool;
/// project-less starts never probe for another instance.
/// </summary>
public static partial class SingleInstance
{
    // How long a hand-off waits for the open copy (still starting, its pipe not up yet) to take it and say so.
    private static readonly TimeSpan HandOffWait = TimeSpan.FromSeconds(3);

    // The longest line a hand-off reads: a path, however long Windows lets it be.
    private const int LongestPath = 32768;

    private static Mutex? _held;

    /// <summary>Full path of the .hproj/.hpb command-line argument (<c>HandPeg.exe "x.hproj"</c>); null when absent.</summary>
    public static string? LaunchFile()
    {
        var file = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(arg => !arg.StartsWith('-') && IsProjectPath(arg));
        try
        {
            return file is null ? null : Path.GetFullPath(file.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsProjectPath(string path) =>
        path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase);

    // Names derive from user, session and data folder, so instances with different data folders (portable) stay independent.
    private static string Key => _key ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Environment.UserDomainName}\\{Environment.UserName}|{SessionId()}|{AppPaths.DataRoot.ToLowerInvariant()}")), 0, 12);

    private static string? _key;

    private static uint SessionId() => ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session : 0;

    private static string MutexName => $@"Local\HandPeg-Editor-{Key}";

    private static string PipeName => $"HandPeg-Open-{Key}";

    /// <summary>
    /// Called from Program.Start before WPF for project launches: forwards to a running instance. True when accepted (this process exits);
    /// false to start normally.
    /// </summary>
    public static bool HandOff(string path)
    {
        try
        {
            // No running instance: no wait (the mutex exists from process start; the pipe opens after Frame 1).
            if (!Mutex.TryOpenExisting(MutexName, out var other))
                return false;
            other.Dispose();

            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            pipe.Connect((int)HandOffWait.TotalMilliseconds);

            // Explorer gave this process the right to come to the front; it goes to the copy that will.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var server))
                _ = AllowSetForegroundWindow(server);

            using var timeout = new CancellationTokenSource(HandOffWait);
            var line = Encoding.UTF8.GetBytes(path + "\n");
            pipe.WriteAsync(line, timeout.Token).AsTask().GetAwaiter().GetResult();
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, leaveOpen: true);
            return reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() == "ok";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Write("The project could not be handed to the HandPeg already open; this copy opens it", ex);
            return false;
        }
    }

    /// <summary>Program.Start: claims the primary-instance mutex (listening starts after Frame 1 via <see cref="Listen"/>).</summary>
    public static void Claim()
    {
        try
        {
            _held ??= new Mutex(false, MutexName, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Another user's object of the name, or none can be made: projects double-clicked start copies of their own.
        }
    }

    /// <summary>On window close: releases the mutex so later launches start their own instance.</summary>
    public static void Release()
    {
        _held?.Dispose();
        _held = null;
    }

    /// <summary>
    /// Post-Frame-1 pool listener accepting hand-offs serially until <paramref name="token"/> cancels; valid project paths get "ok" and go
    /// to <paramref name="open"/> (on the pool; the window marshals to the UI thread).
    /// </summary>
    public static void Listen(Action<string> open, CancellationToken token)
    {
        if (_held is null)
            return;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(token);

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(HandOffWait);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var path = (await reader.ReadLineAsync(timeout.Token))?.Trim() ?? "";
                    var taken = path.Length is > 0 and <= LongestPath && IsProjectPath(path) && File.Exists(path);
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(taken ? "ok\n" : "no\n"), timeout.Token);
                    await pipe.FlushAsync(timeout.Token);
                    if (taken)
                    {
                        AppLog.Write($"A project double-clicked while HandPeg was open was handed to it: {Path.GetFileName(path)}.");
                        open(path);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
                {
                    // Failed hand-off (peer exited, no line received): continue listening.
                    AppLog.Write("A project handed over by another HandPeg could not be taken", ex);
                    await Task.Delay(250, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
