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
    /// Pipe target that splits on both CR and LF, so progress lines that a tool
    /// rewrites in place with a bare carriage return still arrive one at a time.
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
    /// Runs a tool so that cancelling it, or closing the application, takes down the whole process tree.
    /// That matters for yt-dlp, which starts ffmpeg itself: killing only the parent would leave the child running.
    /// </summary>
    public static async Task<CommandResult> RunAsync(Command command, CancellationToken cancellationToken)
    {
        // Disposed when this method is left, whichever way: nothing of the run outlives it.
        using var execution = command.ExecuteAsync(cancellationToken);
        var processId = execution.ProcessId;
        RunningProcesses[processId] = 0;

        // CliWrap kills on cancellation as well; this makes sure it is the tree, and happens straight away.
        await using var killOnCancel = cancellationToken.Register(() => KillTree(processId));
        try
        {
            return await execution;
        }
        finally
        {
            RunningProcesses.TryRemove(processId, out _);
        }
    }

    /// <summary>
    /// Runs a tool to its end and returns what it wrote, with the run disposed of as soon as it is over.
    /// Every short run whose output is read (ffprobe, the encoder tests) goes through here.
    /// </summary>
    public static async Task<CliWrap.Buffered.BufferedCommandResult> RunBufferedAsync(Command command, CancellationToken cancellationToken, Encoding? encoding = null)
    {
        using var execution = CliWrap.Buffered.BufferedCommandExtensions.ExecuteBufferedAsync(command, encoding ?? Console.OutputEncoding, cancellationToken);
        return await execution;
    }

    /// <summary>Adds a process started some other way to those that are stopped when the application closes.</summary>
    public static void Track(int processId) => RunningProcesses[processId] = 0;

    public static void Untrack(int processId) => RunningProcesses.TryRemove(processId, out _);

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
