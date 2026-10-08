using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>Brings back what <see cref="BackupExporter"/> wrote, and project files from elsewhere.</summary>
public static class BackupImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Replaces appsettings.json and presets.json with the ones in a settings export. Nothing is touched
    /// unless the whole file is sound: both parts are checked and written out as new files first, and only
    /// then swapped in, each leaving the file it replaces behind as a .bak.
    /// </summary>
    /// <returns>How many presets the file held.</returns>
    public static int ImportSettingsAndPresets(string path)
    {
        if (ParseObject(File.ReadAllText(path)) is not { } root
            || root["AppSettings"] is not JsonObject settings || root["Presets"] is not JsonArray presets)
        {
            throw new InvalidDataException("This is not a Handpeg settings export: it has no AppSettings and Presets sections.");
        }

        // Read both the way the application will at its next start, so a file it could not start with is refused here.
        _ = settings.Deserialize<AppSettings>() ?? throw new InvalidDataException("The AppSettings section is empty.");
        var count = (presets.Deserialize<List<EncodingPreset>>() ?? []).Count;

        var newSettings = AppSettings.FilePath + ".new";
        var newPresets = PresetStore.FilePath + ".new";
        File.WriteAllText(newSettings, settings.ToJsonString(JsonOptions));
        File.WriteAllText(newPresets, presets.ToJsonString(JsonOptions));

        SwapIn(newSettings, AppSettings.FilePath);
        SwapIn(newPresets, PresetStore.FilePath);
        return count;
    }

    /// <summary>
    /// Copies project files into the projects folder. A file written by Export All Projects is taken apart
    /// into the projects it holds. A project whose name is already taken is added beside it under a
    /// numbered name; nothing already in the folder is replaced.
    /// </summary>
    public static (int Imported, int Skipped) ImportProjects(IEnumerable<string> paths)
    {
        Directory.CreateDirectory(ProjectStore.Folder);
        var (imported, skipped) = (0, 0);

        foreach (var path in paths)
        {
            try
            {
                var text = File.ReadAllText(path);
                var root = ParseObject(text);

                if (root is not null && IsProject(root))
                {
                    // Chosen from the projects folder itself: it is already where it would go.
                    if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), ProjectStore.Folder, StringComparison.OrdinalIgnoreCase))
                        skipped++;
                    else if (AddProject(Path.GetFileNameWithoutExtension(path), text))
                        imported++;
                    else
                        skipped++;
                }
                else if (root is not null && GetBundledProjects(root) is { Count: > 0 } bundled)
                {
                    foreach (var (name, project) in bundled)
                    {
                        if (AddProject(name, project!.ToJsonString(JsonOptions)))
                            imported++;
                        else
                            skipped++;
                    }
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                skipped++;
            }
        }

        if (imported > 0)
            ProjectStore.NotifyChanged();
        return (imported, skipped);
    }

    private static JsonObject? ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The projects in a file written by Export All Projects, by name; empty when the file is something else.
    /// The version marker beside them is not a project and is passed over.
    /// </summary>
    private static List<KeyValuePair<string, JsonNode?>> GetBundledProjects(JsonObject root)
    {
        var entries = root.Where(entry => entry.Key != BackupExporter.SchemaVersionKey).ToList();
        return entries.All(entry => entry.Value is JsonObject project && IsProject(project)) ? entries : [];
    }

    /// <summary>A project names its source and carries a block of settings.</summary>
    private static bool IsProject(JsonObject node)
    {
        if (node["SourcePath"] is not JsonValue || node["Settings"] is not JsonObject)
            return false;

        try
        {
            return node.Deserialize<ProjectState>() is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Writes one project into the folder. Returns false when the very same project is already there.</summary>
    private static bool AddProject(string name, string json)
    {
        var baseName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (baseName.Length == 0)
            baseName = "Project";

        var target = Path.Combine(ProjectStore.Folder, baseName + ".txt");
        for (var number = 2; File.Exists(target); number++)
        {
            // The same project, however it happens to be laid out in the file.
            if (ParseObject(File.ReadAllText(target)) is { } existing && JsonNode.DeepEquals(existing, JsonNode.Parse(json)))
                return false;
            target = Path.Combine(ProjectStore.Folder, $"{baseName} ({number}).txt");
        }

        File.WriteAllText(target, json);
        return true;
    }

    private static void SwapIn(string newFile, string target)
    {
        if (File.Exists(target))
            File.Replace(newFile, target, target + ".bak");
        else
            File.Move(newFile, target);
    }
}
