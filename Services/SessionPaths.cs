using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Where this run of the application keeps its temporary files: one folder, session_[id] in the temp folder,
/// removed as a whole when the application closes. (Downloads are not in it: they are a cache meant to
/// outlive the session.)
/// </summary>
public static class SessionPaths
{
    private const string SessionPrefix = "session_";

    private static readonly string TempRoot = AppPaths.Temp;

    public static string Root { get; } = Path.Combine(TempRoot, SessionPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>Hover-preview thumbnail sheets.</summary>
    public static string Sprites => Path.Combine(Root, "sprites");

    /// <summary>Clips made by Render Preview.</summary>
    public static string Previews => Path.Combine(Root, "previews");

    /// <summary>Cut and chapter lists belonging to queued jobs.</summary>
    public static string Queue => Path.Combine(Root, "queue");

    /// <summary>Removes this session's folder and everything in it.</summary>
    public static void DeleteSession() => DeleteFolder(Root);

    /// <summary>
    /// Removes folders left by sessions that ended without cleaning up (a crash, a killed process).
    /// Only old ones: a recent folder may belong to another copy of the application that is still running.
    /// </summary>
    public static void DeleteAbandonedSessions()
    {
        try
        {
            if (!Directory.Exists(TempRoot))
                return;

            foreach (var folder in new DirectoryInfo(TempRoot).EnumerateDirectories(SessionPrefix + "*"))
            {
                if (folder.FullName != Root && folder.LastWriteTime < DateTime.Now.AddDays(-1))
                    DeleteFolder(folder.FullName);
            }

            // What earlier versions kept directly in the temp root.
            foreach (var name in new[] { "queue", "sprites", "preview" })
                DeleteFolder(Path.Combine(TempRoot, name));
            foreach (var name in new[] { "cuts.txt", "chapters.txt", "binding-errors.log" })
                File.Delete(Path.Combine(TempRoot, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something in it is still open. It is in the temp folder and will be picked up as abandoned later.
        }
    }
}
