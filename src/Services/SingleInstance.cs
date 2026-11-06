using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// One editor for a project double-clicked while HandPeg is open (5.2.0's re-cut, the user's answer): HandPeg started with a .hproj
/// or .hpb (Explorer's open command, ExplorerThumbnails) hands it to the copy already open for the same user, session and data folder,
/// over a pipe only that user can open; that copy asks about its unsaved changes, opens it and comes to the front, and the new process
/// ends without a window. A start with no project, a copy of another data folder (a portable one beside an installed one), or a hand-off
/// that fails for any reason starts as it always did.
///
/// Law 1: the open copy takes a named mutex in Program.Start (one kernel call) and listens only after its first frame, on a pool thread;
/// a start with no project never looks for another copy.
/// </summary>
public static partial class SingleInstance
{
    // How long a hand-off waits for the open copy (still starting, its pipe not up yet) to take it and say so.
    private static readonly TimeSpan HandOffWait = TimeSpan.FromSeconds(3);

    // The longest line a hand-off reads: a path, however long Windows lets it be.
    private const int LongestPath = 32768;

    private static Mutex? _held;

    /// <summary>The .hproj or .hpb on the command line (<c>HandPeg.exe "x.hproj"</c>: Explorer's open command), made whole; null for none.</summary>
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

    // The same user, session and data folder make the same names; a copy with a data folder of its own (portable) is an editor of its own.
    private static string Key => _key ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Environment.UserDomainName}\\{Environment.UserName}|{SessionId()}|{AppPaths.DataRoot.ToLowerInvariant()}")), 0, 12);

    private static string? _key;

    private static uint SessionId() => ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session : 0;

    private static string MutexName => $@"Local\HandPeg-Editor-{Key}";

    private static string PipeName => $"HandPeg-Open-{Key}";

    /// <summary>
    /// Program.Start, before WPF, for a start with a project: hands it to the copy open, when there is one. True when that copy took
    /// it (this process then ends); false to start as usual.
    /// </summary>
    public static bool HandOff(string path)
    {
        try
        {
            // No copy open: nothing waits (the mutex is the open copy's from its first moment; its pipe comes after its first frame).
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
            AppLog.Write($"The project could not be handed to the HandPeg already open ({ex.GetType().Name}: {ex.Message}); this copy opens it.");
            return false;
        }
    }

    /// <summary>Program.Start: this copy is the one a project double-clicked later is handed to (it listens after its first frame, <see cref="Listen"/>).</summary>
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

    /// <summary>The window is closing: a project double-clicked from now on starts a copy of its own, never waiting on this one.</summary>
    public static void Release()
    {
        _held?.Dispose();
        _held = null;
    }

    /// <summary>
    /// After the first frame: takes projects handed over, one at a time, on a pool thread, until <paramref name="token"/> ends. Each that is a
    /// project file there is answered "ok" and given to <paramref name="open"/> (on the pool thread: the window marshals it to its own).
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
                    // One hand-off that went wrong (the other copy gone mid-way, a line that never came): the next is listened for.
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
