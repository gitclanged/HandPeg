using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using CliWrap;

namespace HandPegApp.Services;

internal static class ProcessPipes
{
    // Every long-running tool this application has started and not yet seen finish.
    private static readonly ConcurrentDictionary<int, byte> RunningProcesses = new();

    /// <summary>
    /// Pipe target splitting on CR and LF so in-place progress lines (bare CR) arrive individually.
    /// </summary>
    public static PipeTarget Lines(Action<string> onLine) => PipeTarget.Create(async (stream, cancellationToken) =>
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        var buffer = new char[1024];
        var line = new StringBuilder();

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is not ('\r' or '\n'))
                {
                    line.Append(c);
                }
                else if (line.Length > 0)
                {
                    onLine(line.ToString());
                    line.Clear();
                }
            }
        }

        if (line.Length > 0)
            onLine(line.ToString());
    });

    /// <summary>
    /// Runs a tool so cancel or app exit kills the whole process tree (yt-dlp spawns ffmpeg, which would otherwise survive).
    /// </summary>
    public static async Task<CommandResult> RunAsync(Command command, CancellationToken cancellationToken)
    {
        // Disposed when this method is left, whichever way: nothing of the run outlives it.
        using var execution = command.ExecuteAsync(cancellationToken);
        var processId = execution.ProcessId;
        RunningProcesses[processId] = 0;
        IntoJob(processId);
        WorkScheduler.NoteProcess(processId, running: true);

        // CliWrap kills on cancellation as well; this makes sure it is the tree, and happens straight away.
        await using var killOnCancel = cancellationToken.Register(() => KillTree(processId));
        try
        {
            return await execution;
        }
        finally
        {
            RunningProcesses.TryRemove(processId, out _);
            WorkScheduler.NoteProcess(processId, running: false);
        }
    }

    /// <summary>
    /// <see cref="RunAsync"/> for background FFmpeg decodes, gated by <see cref="WorkScheduler.ToolGate"/> (max two concurrent).
    /// </summary>
    public static async Task<CommandResult> RunGatedAsync(Command command, CancellationToken cancellationToken)
    {
        await WorkScheduler.ToolGate.WaitAsync(cancellationToken);
        try
        {
            return await RunAsync(command, cancellationToken);
        }
        finally
        {
            WorkScheduler.ToolGate.Release();
        }
    }

    /// <summary>
    /// Runs a tool to completion and returns buffered output, disposing immediately (ffprobe, encoder probes).
    /// </summary>
    public static async Task<CliWrap.Buffered.BufferedCommandResult> RunBufferedAsync(Command command, CancellationToken cancellationToken, Encoding? encoding = null)
    {
        using var execution = CliWrap.Buffered.BufferedCommandExtensions.ExecuteBufferedAsync(command, encoding ?? Console.OutputEncoding, cancellationToken);
        IntoJob(execution.ProcessId);
        return await execution;
    }

    /// <summary>
    /// Assigns a started tool to its work lane's job (WorkScheduler, JobObjects): avoids reserved cores, uses the lane priority, and dies
    /// with HandPeg however it exits.
    /// </summary>
    private static void IntoJob(int processId) =>
        JobObjects.Assign(processId, WorkScheduler.CurrentProcessClass, AppSettings.Current.BackgroundEfficiencyMode);

    /// <summary>
    /// Registers an externally started process (voiceover recorder) for shutdown, in the foreground job (normal priority, dies with HandPeg).
    /// </summary>
    public static void Track(int processId)
    {
        RunningProcesses[processId] = 0;
        JobObjects.Assign(processId, ProcessClass.Foreground, efficiencyMode: false);
    }

    public static void Untrack(int processId) => RunningProcesses.TryRemove(processId, out _);

    /// <summary>
    /// Adopts a tool started via <see cref="Process.Start(ProcessStartInfo)"/> in synchronous code (bundle VOD trims, Clip Library audio
    /// cuts): assigns its lane job (affinity, priority, dies with HandPeg), registers it for <see cref="KillAll"/> and with the scheduler.
    /// Dispose after exit to release it.
    /// </summary>
    public static IDisposable Adopt(int processId)
    {
        IntoJob(processId);
        RunningProcesses[processId] = 0;
        WorkScheduler.NoteProcess(processId, true);
        return new Adopted(processId);
    }

    private sealed class Adopted(int processId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            RunningProcesses.TryRemove(processId, out _);
            WorkScheduler.NoteProcess(processId, false);
        }
    }

    /// <summary>Kills every tool still running. Called as the application closes.</summary>
    public static void KillAll()
    {
        foreach (var processId in RunningProcesses.Keys)
            KillTree(processId);
        RunningProcesses.Clear();
    }

    private static void KillTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the aim.
        }
    }
}
