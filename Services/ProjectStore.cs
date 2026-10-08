using System.IO;
using System.Text.Json;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>A saved project as listed in the project manager.</summary>
/// <param name="FilePath">The project file itself.</param>
/// <param name="SourceName">File name of the video the project was made from.</param>
/// <param name="SourcePath">Full path (or URL) of that video.</param>
public sealed record ProjectEntry(string FilePath, string Name, string SourceName, string SourcePath, DateTime SavedAt)
{
    public string SavedText => $"Saved {SavedAt:g}";
}

/// <summary>
/// Project files live in a "projects" folder next to the application: JSON inside, .txt outside.
/// </summary>
public static class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "projects");

    /// <summary>Raised when projects were added to the folder from outside the project manager, so an open list can refresh.</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>Saves the state under its name, replacing a project of the same name.</summary>
    public static string Save(ProjectState state)
    {
        Directory.CreateDirectory(Folder);

        var fileName = string.Concat(state.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (fileName.Length == 0)
            fileName = "Project";

        var path = Path.Combine(Folder, fileName + ".txt");
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        return path;
    }

    public static ProjectState Load(string filePath) =>
        JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(filePath))
        ?? throw new InvalidDataException("The project file is empty.");

    /// <summary>Saved projects, most recently saved first. Files that cannot be read are left out.</summary>
    public static List<ProjectEntry> ListRecent()
    {
        var entries = new List<ProjectEntry>();
        if (!Directory.Exists(Folder))
            return entries;

        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.txt").OrderByDescending(f => f.LastWriteTime))
        {
            try
            {
                var state = Load(file.FullName);
                var isUrl = Uri.TryCreate(state.SourcePath, UriKind.Absolute, out var uri) && !uri.IsFile;
                entries.Add(new ProjectEntry(
                    file.FullName,
                    Path.GetFileNameWithoutExtension(file.Name),
                    isUrl ? Path.GetFileName(state.LocalMediaPath) : Path.GetFileName(state.SourcePath),
                    state.SourcePath,
                    file.LastWriteTime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
            }
        }

        return entries;
    }
}
