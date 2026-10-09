using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HandPegApp.Services;

/// <summary>Gathers the application's own files into single files that can be kept as a backup.</summary>
public static class BackupExporter
{
    /// <summary>Written into every exported file, so that a later version can tell how to read it.</summary>
    public const string SchemaVersionKey = "schema_version";
    public const string SchemaVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes one JSON object holding appsettings.json and presets.json. A file that does not exist yet
    /// (nothing has been saved) is represented by what the application is running with.
    /// </summary>
    public static void ExportSettingsAndPresets(string path)
    {
        var export = new JsonObject
        {
            [SchemaVersionKey] = SchemaVersion,
            ["ExportedAt"] = DateTime.Now.ToString("s"),
            ["AppSettings"] = ReadFile(AppSettings.FilePath) ?? JsonSerializer.SerializeToNode(AppSettings.Current),
            ["Presets"] = ReadFile(PresetStore.FilePath) ?? JsonSerializer.SerializeToNode(PresetStore.Load()),

            // The mask pictures the layers refer to, by file name, so a backup brings them along.
            ["Masks"] = ReadMasks(),
        };
        File.WriteAllText(path, export.ToJsonString(JsonOptions));
    }

    /// <summary>
    /// Writes one JSON object with an entry per project file, named after the file. Files that are not
    /// valid JSON are left out and counted.
    /// </summary>
    public static (int Exported, int Skipped) ExportProjects(string path)
    {
        var export = new JsonObject { [SchemaVersionKey] = SchemaVersion };
        var skipped = 0;

        if (Directory.Exists(ProjectStore.Folder))
        {
            foreach (var file in Directory.EnumerateFiles(ProjectStore.Folder, "*.txt").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    export[Path.GetFileNameWithoutExtension(file)] = JsonNode.Parse(File.ReadAllText(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    skipped++;
                }
            }
        }

        File.WriteAllText(path, export.ToJsonString(JsonOptions));

        // Every entry but the version marker is a project.
        return (export.Count - 1, skipped);
    }

    private static JsonObject ReadMasks()
    {
        var masks = new JsonObject();
        if (!Directory.Exists(AppPaths.Masks))
            return masks;

        foreach (var file in Directory.EnumerateFiles(AppPaths.Masks).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                masks[Path.GetFileName(file)] = Convert.ToBase64String(File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One mask that cannot be read does not stop the backup of everything else.
            }
        }

        return masks;
    }

    private static JsonNode? ReadFile(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
}
