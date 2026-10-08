using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// A plain text log in the logs folder, for what happens out of sight: update checks, data migration,
/// tool installs. Writing to it never fails the caller.
/// </summary>
public static class AppLog
{
    private const long MaximumBytes = 512 * 1024;

    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppPaths.Logs, "HandPeg.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);

                // One generation is kept, so the log cannot grow without end.
                if (new FileInfo(FilePath) is { Exists: true, Length: > MaximumBytes })
                    File.Move(FilePath, FilePath + ".old", overwrite: true);

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    public static void Write(string what, Exception ex) => Write($"{what}: {ex.GetType().Name}: {ex.Message}");
}
