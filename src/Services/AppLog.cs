using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Plain-text background log in the logs folder (update checks, migration, tool installs); writes never throw.
/// </summary>
public static class AppLog
{
    private const long MaximumBytes = 512 * 1024;

    /// <summary>The longest single entry: a deep stack with inner exceptions is cut here.</summary>
    public const int MostEntryChars = 4096;

    private static readonly Lock Gate = new();
    private static readonly HashSet<int> SeenFaults = [];
    private static readonly ConcurrentDictionary<string, byte> OnceKeys = new(StringComparer.Ordinal);
    private static Mutex? _fileMutex;

    public static string FilePath { get; } = Path.Combine(AppPaths.Logs, "HandPeg.log");

    public static void Write(string message)
    {
        DevFlightRecorder.Record("log", message);
        try
        {
            lock (Gate)
            {
                var mutex = _fileMutex ??= new Mutex(initiallyOwned: false, MutexName(FilePath));
                var owned = false;
                try
                {
                    try
                    {
                        owned = mutex.WaitOne(250);
                    }
                    catch (AbandonedMutexException)
                    {
                        // The other process ended while it held the mutex: the file is still a valid append-only log.
                        owned = true;
                    }

                    Append(message);
                }
                finally
                {
                    if (owned)
                        mutex.ReleaseMutex();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Logs <paramref name="what"/> with exception type, message, stack and inners; repeats (same type and stack) collapse to one line.
    /// </summary>
    public static void Write(string what, Exception ex) => Write(Describe(what, ex));

    /// <summary>
    /// <see cref="Write(string, Exception)"/> once per <paramref name="key"/> per process, for failures recurring on every refresh.
    /// </summary>
    public static void WriteOnce(string key, string what, Exception ex)
    {
        if (OnceKeys.TryAdd(key, 0))
            Write(what, ex);
    }

    /// <summary>Formats an exception entry: full on first sighting of its type and stack, one line afterwards.</summary>
    internal static string Describe(string what, Exception ex)
    {
        var stack = ex.StackTrace;
        if (stack is null)
            return $"{what}: {ex.GetType().Name}: {ex.Message}";

        var signature = HashCode.Combine(ex.GetType().FullName, stack, ex.InnerException?.GetType().FullName);
        bool first;
        lock (SeenFaults)
            first = SeenFaults.Add(signature);
        if (!first)
            return $"{what}: {ex.GetType().Name}: {ex.Message} (same stack as earlier)";

        var full = $"{what}: {ex}";
        return full.Length <= MostEntryChars ? full : string.Concat(full.AsSpan(0, MostEntryChars), " ... (cut)");
    }

    private static void Append(string message)
    {
        Directory.CreateDirectory(AppPaths.Logs);

        // One older generation is kept, so the log cannot grow without end.
        if (new FileInfo(FilePath) is { Exists: true, Length: > MaximumBytes })
            File.Move(FilePath, FilePath + ".old", overwrite: true);

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.AppendAllText(FilePath, line);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                // Held for a moment by something outside HandPeg (a viewer, an antivirus scan): retried briefly.
                Thread.Sleep(5);
            }
        }
    }

    // One mutex per log file (a portable copy and an installed one log to different files): FNV-1a of the upper-cased path.
    private static string MutexName(string path)
    {
        var hash = 2166136261u;
        foreach (var c in path.ToUpperInvariant())
            hash = (hash ^ c) * 16777619u;
        return $@"Local\HandPeg.Log.{hash:x8}";
    }
}
