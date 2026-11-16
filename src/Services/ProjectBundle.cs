using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;
using HandPegApp.Models.Effects;

namespace HandPegApp.Services;

// ----- Project bundles (.hpb) -----
// A self-contained ZIP of a project and all referenced media (video, audio, images, LUTs) for transfer or archival:
//   bundle.json    format/version, writer version, and per-media entry name, length and last-write time (never source paths);
//   project.hproj  the project (format 6) with media references rewritten to entry names ("media/clip.mkv");
//   thumbnail.jpg  optional stored thumbnail for Explorer previews (found via the central directory); not extracted;
//   media/...      media stored uncompressed (video does not compress; copies run at disk speed).
// Opening extracts beside the bundle (folder named after it), rewrites paths to the extracted files and opens that ordinary project.
// Reopening reuses its folder as left; a changed bundle extracts to a new folder, never over existing work.
// Long recordings mostly unused by the Sequence are stored as used ranges (VodTrim: lossless stream copies with 5 s handles,
// AppSettings.TrimLongVodsOnBundle); only the bundled project references them, the working project is unchanged.
public static class ProjectBundle
{
    public const string Extension = ".hpb";
    public const string FormatName = "handpeg-bundle";
    public const int CurrentVersion = 1;

    private const string ManifestEntry = "bundle.json", ProjectEntry = "project.hproj", MediaFolder = "media/";

    // Inside an unpacked folder: which bundle it came from (its length and last-write time), so it is found again.
    private const string MarkerName = ".hpb-unpacked";

    private const int CopyBuffer = 4 << 20;

    // ZIP timestamp range, and write-time tolerance for matching the manifest (FAT32 has 2 s resolution, exFAT 10 ms; USB sticks).
    private static readonly DateTimeOffset ZipEarliest = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero), ZipLatest = new(2107, 12, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly long SameTimeTicks = TimeSpan.FromSeconds(2.5).Ticks;

    private sealed class Manifest
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string? WrittenBy { get; set; }
        public string Project { get; set; } = ProjectEntry;

        /// <summary>Thumbnail entry name ("thumbnail.jpg"); omitted when absent.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Thumbnail { get; set; }

        public List<MediaEntry> Media { get; set; } = [];
    }

    private sealed class MediaEntry
    {
        public string Entry { get; set; } = "";
        public long Length { get; set; }

        /// <summary>Last-write time (UTC ticks), restored on extraction so embedded seek indexes still match.</summary>
        public long Written { get; set; }
    }

    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    // ----- Packing -----

    /// <summary>
    /// Writes a bundle atomically (via ".part") with the project and all existing referenced files, reporting byte progress 0..1.
    /// Returns counts of files added, missing references (left unchanged) and recordings trimmed (<paramref name="trimLongVods"/>:
    /// VodTrim pieces cut beside the bundle, deleted afterwards).
    /// </summary>
    public static (int Files, int Missing, int Trimmed) Pack(ProjectState state, string bundlePath, IProgress<double>? progress = null, CancellationToken cancel = default,
        bool trimLongVods = false)
    {
        var trimFolder = bundlePath + ".trim";
        Dictionary<string, IReadOnlyList<VodTrim.Piece>>? pieces = null;
        try
        {
            if (trimLongVods && VodTrim.Plan(state) is { Count: > 0 } plan)
            {
                if (VodTrim.Cut(plan, trimFolder, cancel) is { Count: > 0 } cut)
                {
                    (state, pieces) = VodTrim.Remap(state, cut);
                    state.Timeline.MediaPool = PoolWithPieces(state.Timeline.MediaPool, pieces);
                }
            }

            var (files, missing) = PackWhole(state, bundlePath, progress, cancel, pieces);
            return (files, missing, pieces?.Count ?? 0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(trimFolder))
                    Directory.Delete(trimFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Project bundle: the trimmed stretches could not all be deleted", ex);
            }
        }
    }

    /// <summary>
    /// Project Media with each trimmed recording replaced in place by its pieces (each once), so the bundled bin covers every played
    /// piece. Bin-only recordings are never trimmed (no clip reads them) and are bundled whole.
    /// </summary>
    internal static List<string> PoolWithPieces(IReadOnlyList<string> pool, IReadOnlyDictionary<string, IReadOnlyList<VodTrim.Piece>> trimmed)
    {
        var kept = new List<string>(pool.Count);
        void Keep(string path)
        {
            if (!kept.Contains(path, StringComparer.OrdinalIgnoreCase))
                kept.Add(path);
        }

        foreach (var path in pool)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Keep(path);
                continue;
            }

            if (trimmed.TryGetValue(full, out var cut) && cut.Count > 0)
            {
                foreach (var piece in cut)
                    Keep(piece.Path);
            }
            else
            {
                Keep(path);
            }
        }

        return kept;
    }

    /// <param name="trimmed">Pieces per trimmed recording (VodTrim.Cut); other references to a trimmed recording map to its first piece so
    /// the full recording is never bundled alongside them.</param>
    private static (int Files, int Missing) PackWhole(ProjectState state, string bundlePath, IProgress<double>? progress, CancellationToken cancel,
        IReadOnlyDictionary<string, IReadOnlyList<VodTrim.Piece>>? trimmed)
    {
        var project = JsonNode.Parse(ProjectStore.Serialize(state)) ?? throw new InvalidDataException("The project could not be written.");

        // Each existing referenced file gets a unique entry name (its file name, numbered on collision).
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? EntryFor(string path)
        {
            if (!LooksLikeFilePath(path))
                return null;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            if (entries.TryGetValue(full, out var known))
                return known;
            if (trimmed is not null && trimmed.TryGetValue(full, out var cut) && cut.Count > 0)
                return EntryFor(cut[0].Path);
            if (!File.Exists(full))
            {
                missing.Add(full);
                return null;
            }

            var name = Path.GetFileName(full);
            var entry = MediaFolder + name;
            for (var n = 2; !taken.Add(entry); n++)
                entry = MediaFolder + $"{Path.GetFileNameWithoutExtension(name)} ({n.ToString(CultureInfo.InvariantCulture)}){Path.GetExtension(name)}";
            return entries[full] = entry;
        }

        // Export destination is machine-specific and never bundled: reduce it to a bare file name beside the extracted project.
        var output = project["output"] as JsonObject;
        var exportName = output?["path"] is JsonValue destination && destination.TryGetValue<string>(out var to) && to.Length > 0 ? Path.GetFileName(to) : null;
        output?.Remove("path");

        // Omit undo history (packed snapshots with local paths the rewrite cannot reach) and hand-edited commands (regenerated).
        if (project is JsonObject whole)
        {
            foreach (var key in (string[])["undoFormat", "undoHistory", "redoHistory"])
                whole.Remove(key);
        }

        output?.Remove("manualCommand");

        Rewrite(project, EntryFor);
        if (output is not null && exportName is { Length: > 0 })
            output["path"] = exportName;

        var thumbnail = state.ThumbnailJpeg is { Length: > 0 } picture ? picture : null;
        var manifest = new Manifest
        {
            Format = FormatName, Version = CurrentVersion, WrittenBy = ProjectStore.WriterName, Thumbnail = thumbnail is null ? null : ProjectThumbnail.BundleEntry,
        };
        var files = entries.Select(e => (Path: e.Key, Entry: e.Value, Info: new FileInfo(e.Key))).ToList();
        foreach (var (_, entry, info) in files)
            manifest.Media.Add(new MediaEntry { Entry = entry, Length = info.Length, Written = info.LastWriteTimeUtc.Ticks });

        var total = Math.Max(1, files.Sum(f => f.Info.Length));
        long done = 0;
        var partial = bundlePath + ".part";
        if (Path.GetDirectoryName(Path.GetFullPath(bundlePath)) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBuffer))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteText(archive, ManifestEntry, JsonSerializer.Serialize(manifest, ManifestOptions));
                WriteText(archive, ProjectEntry, project.ToJsonString());

                // Thumbnail stored (uncompressed JPEG) right after the project for Explorer's preview.
                if (thumbnail is not null)
                {
                    using var written = archive.CreateEntry(ProjectThumbnail.BundleEntry, CompressionLevel.NoCompression).Open();
                    written.Write(thumbnail);
                }

                var buffer = new byte[CopyBuffer];
                foreach (var (path, entry, info) in files)
                {
                    cancel.ThrowIfCancellationRequested();
                    var zipped = archive.CreateEntry(entry, CompressionLevel.NoCompression);
                    // ZIP timestamps span 1980-2107 (the manifest holds the exact time, restored on extraction).
                    zipped.LastWriteTime = info.LastWriteTime < ZipEarliest ? ZipEarliest : info.LastWriteTime > ZipLatest ? ZipLatest : info.LastWriteTime;
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBuffer, FileOptions.SequentialScan);
                    using var into = zipped.Open();
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        into.Write(buffer, 0, read);
                        done += read;
                        progress?.Report((double)done / total);
                    }
                }
            }

            File.Move(partial, bundlePath, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        progress?.Report(1);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Project bundle written: {files.Count} file(s), {total / 1048576.0:0.0} MB, {missing.Count} named file(s) not there"));
        return (files.Count, missing.Count);
    }

    private static void WriteText(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    // ----- Unpacking -----

    /// <summary>
    /// Extracts a bundle (beside it, else the projects folder) and returns the project path. A folder previously extracted from the same
    /// bundle is reused as left: the project is not overwritten and only missing or incomplete files are copied. Reports progress 0..1.
    /// </summary>
    public static string Unpack(string bundlePath, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var bundle = new FileInfo(bundlePath);
        var stamp = string.Create(CultureInfo.InvariantCulture, $"{bundle.Length} {bundle.LastWriteTimeUtc.Ticks}");
        using var archive = ZipFile.OpenRead(bundlePath);

        var manifest = (archive.GetEntry(ManifestEntry) is { } manifestEntry ? ReadJson<Manifest>(manifestEntry) : null)
                       ?? throw new InvalidDataException("This is not a HandPeg project bundle: it has no bundle.json.");
        if (manifest.Format != FormatName)
            throw new InvalidDataException("This is not a HandPeg project bundle.");
        if (manifest.Version > CurrentVersion)
            throw new InvalidDataException($"This bundle was made by {manifest.WrittenBy ?? "a newer HandPeg"} (bundle format {manifest.Version}); this version reads format {CurrentVersion}.");

        // A manifest of another shape than this version writes (no list, an entry with no name) is read as far as it goes.
        manifest.Media = [.. (manifest.Media ?? []).Where(m => m is { Entry.Length: > 0 } && m.Entry.IndexOf('\0') < 0)];
        var projectEntry = (manifest.Project is { Length: > 0 } named ? archive.GetEntry(named) : null) ?? throw new InvalidDataException("The bundle holds no project.");

        var name = Path.GetFileNameWithoutExtension(bundlePath);
        var folder = FolderFor(bundle, name, stamp);
        var projectPath = Path.Combine(folder, name + ProjectStore.Extension);

        var total = Math.Max(1, manifest.Media.Sum(m => Math.Max(0, m.Length)));
        long done = 0;
        var buffer = new byte[CopyBuffer];
        foreach (var media in manifest.Media)
        {
            cancel.ThrowIfCancellationRequested();
            if (archive.GetEntry(media.Entry) is not { } entry || InsideOf(folder, media.Entry) is not { } target)
                continue;

            var there = new FileInfo(target);
            if (there.Exists && there.Length == entry.Length && (media.Written == 0 || Math.Abs(there.LastWriteTimeUtc.Ticks - media.Written) <= SameTimeTicks))
            {
                done += entry.Length;
                progress?.Report((double)done / total);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var partial = target + ".part";
            try
            {
                using (var from = entry.Open())
                using (var into = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, CopyBuffer))
                {
                    int read;
                    while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        into.Write(buffer, 0, read);
                        done += read;
                        progress?.Report((double)done / total);
                    }
                }

                File.Move(partial, target, overwrite: true);
                if (media.Written > 0)
                    File.SetLastWriteTimeUtc(target, new DateTime(media.Written, DateTimeKind.Utc));
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }

        // Rewritten project written once; never overwrites later saves.
        if (!File.Exists(projectPath))
        {
            using var reader = new StreamReader(projectEntry.Open(), Encoding.UTF8);
            var project = JsonNode.Parse(reader.ReadToEnd()) ?? throw new InvalidDataException("The bundle's project is empty.");

            // Bundle content is untrusted: hand-edited commands are always dropped (they would run on Export), and only strings exactly
            // matching a manifest entry are rewritten (never by "media/" prefix).
            (project["output"] as JsonObject)?.Remove("manualCommand");
            var entries = manifest.Media.Select(m => m.Entry).ToHashSet(StringComparer.Ordinal);
            Rewrite(project, value => entries.Contains(value) ? InsideOf(folder, value) : null);
            if (project["output"] is JsonObject output && output["path"] is JsonValue destination && destination.TryGetValue<string>(out var to)
                && to.Length > 0 && !Path.IsPathRooted(to) && InsideOf(folder, to) is { } beside)
                output["path"] = beside;
            ProjectStore.WriteWhole(projectPath, project.ToJsonString(), durable: true);
        }

        File.WriteAllText(Path.Combine(folder, MarkerName), stamp);
        progress?.Report(1);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Project bundle unpacked: {manifest.Media.Count} file(s), {total / 1048576.0:0.0} MB"));
        return projectPath;
    }

    /// <summary>
    /// Extraction folder: the previous one (identified by its marker), else the first free "Name", "Name (2)", ... beside the bundle,
    /// else (unwritable location) the projects folder.
    /// </summary>
    private static string FolderFor(FileInfo bundle, string name, string stamp)
    {
        foreach (var root in new[] { bundle.DirectoryName, Path.Combine(ProjectStore.Folder, "Bundles") })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            for (var n = 1; n < 100; n++)
            {
                var folder = Path.Combine(root, n == 1 ? name : $"{name} ({n.ToString(CultureInfo.InvariantCulture)})");
                var marker = Path.Combine(folder, MarkerName);
                if (File.Exists(marker))
                {
                    if (ReadMarker(marker) == stamp)
                        return folder;
                    continue;
                }

                if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                    continue;
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(marker, stamp);
                    return folder;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }
        }

        throw new IOException("There is no folder the bundle can be unpacked into.");
    }

    private static string? ReadMarker(string marker)
    {
        try
        {
            return File.ReadAllText(marker).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Resolves an entry inside the folder; null for escaping names ("..", rooted). Also used for portable project media.</summary>
    internal static string? InsideOf(string folder, string entryName)
    {
        if (entryName.Length == 0 || Path.IsPathRooted(entryName))
            return null;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ----- The project's paths -----

    /// <summary>
    /// Free-text properties (title text, track/layer/marker names, captions, speech prompt, Extra options) that never name files.
    /// <see cref="Rewrite"/> skips them so path-like text ("C:\Users\me\notes.txt") is neither bundled nor altered.
    /// </summary>
    internal static readonly IReadOnlySet<string> FreeTextKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "textContent", "name", "text", "title", "words", "whisperPrompt", "extraVideoArguments", "fontFamily", "fontName", "label", "writtenBy",
    };

    /// <summary>
    /// Recursively replaces JSON strings mapped by <paramref name="map"/> (null keeps), skipping <see cref="FreeTextKeys"/> and effect
    /// text parameters (<see cref="ParamKind.Text"/>).
    /// </summary>
    internal static void Rewrite(JsonNode node, Func<string, string?> map)
    {
        switch (node)
        {
            case JsonObject json:
                // An effect card ({ "type":..., "values": {... } }): its text parameters are words, never paths.
                var textParams = json["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) && json["values"] is JsonObject
                    ? EffectRegistry.Find(typeName)?.Params.Where(p => p.Kind == ParamKind.Text).Select(p => p.Key).ToHashSet(StringComparer.Ordinal)
                    : null;
                foreach (var (key, child) in json.ToList())
                {
                    if (FreeTextKeys.Contains(key))
                        continue;
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (map(text) is { } changed)
                            json[key] = changed;
                    }
                    else if (key == "values" && child is JsonObject values && textParams is { Count: > 0 })
                    {
                        foreach (var (param, setting) in values.ToList())
                        {
                            if (textParams.Contains(param))
                                continue;
                            if (setting is JsonValue v && v.TryGetValue<string>(out var s))
                            {
                                if (map(s) is { } changed)
                                    values[param] = changed;
                            }
                            else if (setting is not null)
                                Rewrite(setting, map);
                        }
                    }
                    else if (child is not null)
                        Rewrite(child, map);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (map(text) is { } changed)
                            array[i] = changed;
                    }
                    else if (array[i] is { } child)
                        Rewrite(child, map);
                }

                break;
        }
    }

    /// <summary>True for an absolute drive or UNC path ("D:\clip.mkv", "\\pc\share\a.wav").</summary>
    internal static bool LooksLikeFilePath(string text) =>
        text.Length is > 3 and < 1024 && !text.Contains("://", StringComparison.Ordinal) && text.IndexOfAny(['\n', '\r', '"', '<', '>', '|']) < 0
        && ((char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/') || text.StartsWith(@"\\", StringComparison.Ordinal));

    private static T? ReadJson<T>(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        try
        {
            return JsonSerializer.Deserialize<T>(stream, ManifestOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
