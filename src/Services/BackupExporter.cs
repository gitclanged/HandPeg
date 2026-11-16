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
    /// Exports appsettings.json and presets.json as one JSON object; missing files are represented by current in-memory values.
    /// </summary>
    public static void ExportSettingsAndPresets(string path)
    {
        // Exclude machine-specific keys (BackupImporter.ProtectedKeys) so shared backups reveal no local paths and imports never apply them.
        var settings = ReadFile(AppSettings.FilePath) ?? JsonSerializer.SerializeToNode(AppSettings.Current);
        if (settings is JsonObject fields)
        {
            foreach (var key in BackupImporter.ProtectedKeys)
                fields.Remove(key);
        }

        var export = new JsonObject
        {
            [SchemaVersionKey] = SchemaVersion,
            ["ExportedAt"] = DateTime.Now.ToString("s"),
            ["AppSettings"] = settings,
            ["Presets"] = ReadFile(PresetStore.FilePath) ?? JsonSerializer.SerializeToNode(PresetStore.Load()),

            // The mask pictures the layers refer to, by file name, so a backup brings them along.
            ["Masks"] = ReadMasks(),
        };
        File.WriteAllText(path, export.ToJsonString(JsonOptions));
    }

    /// <summary>
    /// Exports all projects as one JSON object keyed by file name; invalid JSON files are skipped and counted.
    /// </summary>
    public static (int Exported, int Skipped) ExportProjects(string path)
    {
        var export = new JsonObject { [SchemaVersionKey] = SchemaVersion };
        var skipped = 0;

        if (Directory.Exists(ProjectStore.Folder))
        {
            foreach (var file in Directory.EnumerateFiles(ProjectStore.Folder, "*" + ProjectStore.Extension).Order(StringComparer.OrdinalIgnoreCase))
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
