using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;

namespace HandPegApp.Services;

// ----- Project bundles (.hpb, 5.0-Release) -----
// One file that holds a project and every file it reads (its videos, sounds, pictures, LUTs), to carry it to another PC or
// keep it as it is. A ZIP archive, so any archive tool opens it:
//   bundle.json    what the bundle is: its format and version, the HandPeg that wrote it, and each media file's entry name,
//                  length and last-write time (never where it came from: a bundle is meant to be given away);
//   project.hproj  the project (format 6), each file it reads written as its entry's name ("media/clip.mkv");
//   media/...      the files, stored as they are (no compression: video does not shrink, and the copy runs at disk speed).
// Opening a bundle unpacks it into a folder beside it (named after it), points the project at the unpacked files and opens
// that project, which is then an ordinary one: saving writes it there. A bundle opened again finds its folder and opens the
// project in it as it was left; a changed bundle unpacks into a folder of its own, never over the user's work.
public static class ProjectBundle
{
    public const string Extension = ".hpb";
    public const string FormatName = "handpeg-bundle";
    public const int CurrentVersion = 1;

    private const string ManifestEntry = "bundle.json", ProjectEntry = "project.hproj", MediaFolder = "media/";

    // Inside an unpacked folder: which bundle it came from (its length and last-write time), so it is found again.
    private const string MarkerName = ".hpb-unpacked";

    private const int CopyBuffer = 4 << 20;

    // The times a ZIP entry can carry; and how far a file's write time may be from the manifest's and still be that file
    // (FAT32 keeps 2 s, exFAT 10 ms: a bundle carried on a USB stick unpacks there).
    private static readonly DateTimeOffset ZipEarliest = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero), ZipLatest = new(2107, 12, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly long SameTimeTicks = TimeSpan.FromSeconds(2.5).Ticks;

    /// <summary>Whether a file is one to open as a bundle (typed in, dropped, chosen): a .hpb that is there.</summary>
    public static bool IsBundleFile(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private sealed class Manifest
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string? WrittenBy { get; set; }
        public string Project { get; set; } = ProjectEntry;
        public List<MediaEntry> Media { get; set; } = [];
    }

    private sealed class MediaEntry
    {
        public string Entry { get; set; } = "";
        public long Length { get; set; }

        /// <summary>The file's last-write time in UTC ticks, put back on unpacking (the seek indexes the project carries match on it).</summary>
        public long Written { get; set; }
    }

    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    // ----- Packing -----

    /// <summary>
    /// Writes the state as a bundle (through a ".part" file, moved into place when whole): the project and every file it reads
    /// that is there. Reports the share of bytes written (0 to 1). Returns how many files went in and how many named files were
    /// not there (left as they were in the project).
    /// </summary>
    public static (int Files, int Missing) Pack(ProjectState state, string bundlePath, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var project = JsonNode.Parse(ProjectStore.Serialize(state)) ?? throw new InvalidDataException("The project could not be written.");

        // Every file the project names that is there, under one entry name each (the file's own name, numbered when two share one).
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

        // Where the export goes is this PC's folder (and never a file to carry, though an earlier export may be there): in the
        // bundle it is the file's name alone, beside the unpacked project.
        var output = project["output"] as JsonObject;
        var exportName = output?["path"] is JsonValue destination && destination.TryGetValue<string>(out var to) && to.Length > 0 ? Path.GetFileName(to) : null;
        output?.Remove("path");

        // The undo history is left behind: its steps are packed snapshots naming this PC's files, which no rewrite reaches.
        // So is a hand-edited command (a whole command line naming this PC's files): the project makes its own again.
        if (project is JsonObject whole)
        {
            foreach (var key in (string[])["undoFormat", "undoHistory", "redoHistory"])
                whole.Remove(key);
        }

        output?.Remove("manualCommand");

        Rewrite(project, EntryFor);
        if (output is not null && exportName is { Length: > 0 })
            output["path"] = exportName;

        var manifest = new Manifest { Format = FormatName, Version = CurrentVersion, WrittenBy = ProjectStore.WriterName };
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

                var buffer = new byte[CopyBuffer];
                foreach (var (path, entry, info) in files)
                {
                    cancel.ThrowIfCancellationRequested();
                    var zipped = archive.CreateEntry(entry, CompressionLevel.NoCompression);
                    // A ZIP keeps 1980 to 2107 (the manifest keeps the file's own time, put back on unpacking).
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
    /// Unpacks a bundle into its folder (beside it, else in the projects folder) and returns the project file to open there.
    /// A folder this very bundle was unpacked into before is used again as it was left: its project is not written over,
    /// and only the files that are not there whole are copied. Reports the share of bytes copied (0 to 1).
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

        // The project, pointed at the unpacked files: written once, never over what the user saved there since.
        if (!File.Exists(projectPath))
        {
            using var reader = new StreamReader(projectEntry.Open(), Encoding.UTF8);
            var project = JsonNode.Parse(reader.ReadToEnd()) ?? throw new InvalidDataException("The bundle's project is empty.");
            Rewrite(project, value => value.StartsWith(MediaFolder, StringComparison.Ordinal) ? InsideOf(folder, value) : null);
            if (project["output"] is JsonObject output && output["path"] is JsonValue destination && destination.TryGetValue<string>(out var to)
                && to.Length > 0 && !Path.IsPathRooted(to) && InsideOf(folder, to) is { } beside)
                output["path"] = beside;
            ProjectStore.WriteWhole(projectPath, project.ToJsonString());
        }

        File.WriteAllText(Path.Combine(folder, MarkerName), stamp);
        progress?.Report(1);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Project bundle unpacked: {manifest.Media.Count} file(s), {total / 1048576.0:0.0} MB"));
        return projectPath;
    }

    /// <summary>
    /// The folder a bundle unpacks into: the one it was unpacked into before (its marker names it), else the first free one
    /// of "Name", "Name (2)"... beside it, else (a folder that cannot be written, a read-only drive) in the projects folder.
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

    /// <summary>An entry's file inside the folder, or null for a name that would leave it ("..", a rooted name).</summary>
    private static string? InsideOf(string folder, string entryName)
    {
        if (entryName.Length == 0 || Path.IsPathRooted(entryName))
            return null;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ----- The project's paths -----

    /// <summary>Replaces every string of the JSON that <paramref name="map"/> has another value for (null: kept), at any depth.</summary>
    private static void Rewrite(JsonNode node, Func<string, string?> map)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var (key, child) in json.ToList())
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (map(text) is { } changed)
                            json[key] = changed;
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

    /// <summary>A string that names a file on a drive or a share ("D:\clip.mkv", "\\pc\share\a.wav"): what the project reads.</summary>
    private static bool LooksLikeFilePath(string text) =>
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
