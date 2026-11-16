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
    /// Machine-specific or security-relevant keys never imported from or exported to backups: tool paths and download sources (a custom
    /// release URL disables pinning), the crash-report endpoint, local folders and files, and selected devices and adapters.
    /// </summary>
    public static IReadOnlyList<string> ProtectedKeys { get; } =
    [
        nameof(AppSettings.FfmpegPath), nameof(AppSettings.FfprobePath), nameof(AppSettings.YtDlpPath), nameof(AppSettings.WhisperPath),
        nameof(AppSettings.FfmpegReleaseUrl), nameof(AppSettings.YtDlpReleaseUrl), nameof(AppSettings.WhisperReleaseUrl),
        nameof(AppSettings.AllowUnpinnedDependencyUpdates), nameof(AppSettings.UseFfmpegForOlderNvidiaDrivers),
        nameof(AppSettings.CrashReportEndpoint), nameof(AppSettings.TrustedProjectCommands),
        nameof(AppSettings.YtDlpCookiesFilePath), nameof(AppSettings.DownloadFolder), nameof(AppSettings.BrowserFolder), nameof(AppSettings.WatchedCaptureFolder),
        nameof(AppSettings.AudioOutputDeviceId), nameof(AppSettings.WhisperGpuFailedOn), nameof(AppSettings.CaptionAccelerator),
        nameof(AppSettings.CompositorGpuAdapter), nameof(AppSettings.DecodeGpuAdapter), nameof(AppSettings.BackgroundGpu), nameof(AppSettings.HardwareDecodeAdapter),
    ];

    /// <summary>
    /// Replaces appsettings.json and presets.json from a settings export, all-or-nothing: both parts are validated and staged as new
    /// files, then swapped in, leaving .bak copies. <see cref="ProtectedKeys"/> keep local values, so imports never change executed tools.
    /// </summary>
    /// <returns>Number of presets imported.</returns>
    /// <exception cref="InvalidDataException">Not a settings export, or a higher major schema version.</exception>
    public static int ImportSettingsAndPresets(string path)
    {
        if (ParseObject(File.ReadAllText(path)) is not { } root
            || root["AppSettings"] is not JsonObject settings || root["Presets"] is not JsonArray presets)
        {
            throw new InvalidDataException("This is not a HandPeg settings export: it has no AppSettings and Presets sections.");
        }

        // The schema: a missing or unreadable version, or a newer major one, is refused before anything is read further.
        if (root[BackupExporter.SchemaVersionKey] is not JsonValue schemaValue || !schemaValue.TryGetValue<string>(out var schema)
            || !Version.TryParse(schema, out var version) || !Version.TryParse(BackupExporter.SchemaVersion, out var supported))
        {
            throw new InvalidDataException("This settings export does not say which version of the format it is.");
        }

        if (version.Major > supported.Major)
            throw new InvalidDataException("This settings export was made by a newer HandPeg.");

        KeepThisPcsValues(settings);

        // Read both the way the application will at its next start, so a file it could not start with is refused here.
        _ = settings.Deserialize<AppSettings>() ?? throw new InvalidDataException("The AppSettings section is empty.");
        var count = (presets.Deserialize<List<EncodingPreset>>() ?? []).Count;

        var newSettings = AppSettings.FilePath + ".new";
        var newPresets = PresetStore.FilePath + ".new";
        AtomicFile.WriteAllText(newSettings, settings.ToJsonString(JsonOptions), durable: true);
        AtomicFile.WriteAllText(newPresets, presets.ToJsonString(JsonOptions), durable: true);

        SwapIn(newSettings, AppSettings.FilePath);
        SwapIn(newPresets, PresetStore.FilePath);

        // The masks a backup carries go back where the layers look for them. One already there is kept.
        if (root["Masks"] is JsonObject masks)
        {
            Directory.CreateDirectory(AppPaths.Masks);
            foreach (var (name, value) in masks)
            {
                var target = Path.Combine(AppPaths.Masks, Path.GetFileName(name));
                if (!File.Exists(target) && value?.GetValue<string>() is { } encoded)
                {
                    try
                    {
                        File.WriteAllBytes(target, Convert.FromBase64String(encoded));
                    }
                    catch (FormatException)
                    {
                        // Not a picture this backup can give back; the rest is still restored.
                    }
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Imports projects into the projects folder, unpacking Export All Projects archives. Name collisions get numbered names; nothing is
    /// replaced. Unsupported legacy formats (1-4) are skipped and counted.
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
                    // Import supported formats; skip legacy ones without aborting the rest.
                    foreach (var (name, project) in bundled)
                    {
                        if (project is JsonObject readable && IsProject(readable) && AddProject(name, readable.ToJsonString(JsonOptions)))
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
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
    /// Projects in an Export All Projects file by name (any format; the caller filters); empty for other files. The version marker is skipped.
    /// </summary>
    private static List<KeyValuePair<string, JsonNode?>> GetBundledProjects(JsonObject root)
    {
        var entries = root.Where(entry => entry.Key != BackupExporter.SchemaVersionKey).ToList();
        return entries.All(entry => entry.Value is JsonObject project && SaysItIsAProject(project)) ? entries : [];
    }

    /// <summary>True when the JSON declares a .hproj format (any version; the earliest projects declared none).</summary>
    private static bool SaysItIsAProject(JsonObject node) =>
        node["format"] is JsonValue format && format.TryGetValue<string>(out var name) && name == ProjectFile.FormatName;

    /// <summary>
    /// True for a readable supported-format project; newer formats are accepted as-is (copied unchanged, prompting on open).
    /// </summary>
    private static bool IsProject(JsonObject node)
    {
        if (!ProjectStore.IsProject(node))
            return false;

        try
        {
            ProjectStore.Deserialize(node.ToJsonString(), allowNewer: true);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
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

        var target = Path.Combine(ProjectStore.Folder, baseName + ProjectStore.Extension);
        for (var number = 2; File.Exists(target); number++)
        {
            // The same project, however it happens to be laid out in the file.
            if (ParseObject(File.ReadAllText(target)) is { } existing && JsonNode.DeepEquals(existing, JsonNode.Parse(json)))
                return false;
            target = Path.Combine(ProjectStore.Folder, $"{baseName} ({number}){ProjectStore.Extension}");
        }

        File.WriteAllText(target, json);
        return true;
    }

    /// <summary>Overwrites the backup's <see cref="ProtectedKeys"/> with local values (settings file, else current settings).</summary>
    private static void KeepThisPcsValues(JsonObject imported)
    {
        var here = File.Exists(AppSettings.FilePath) ? ParseObject(File.ReadAllText(AppSettings.FilePath)) : null;
        here ??= JsonSerializer.SerializeToNode(AppSettings.Current, AppSettingsJsonContext.Default.AppSettings) as JsonObject;
        foreach (var key in ProtectedKeys)
        {
            imported.Remove(key);
            if (here?[key] is { } value)
                imported[key] = value.DeepClone();
        }
    }

    private static void SwapIn(string newFile, string target)
    {
        if (File.Exists(target))
            File.Replace(newFile, target, target + ".bak");
        else
            File.Move(newFile, target);
    }
}
