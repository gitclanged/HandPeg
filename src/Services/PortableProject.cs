using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HandPegApp.Models;

namespace HandPegApp.Services;

// ----- Fully portable project saves (AppSettings.FullyPortableProjectSave) -----
// Manual saves write a self-contained folder: Projects\Name\Name.hproj plus all referenced media (video, audio, images, LUTs, masks) in
// Projects\Name\media\, referenced as relative "media\file" so the folder is movable. Same-volume files are hard-linked (instant, no
// space); others (or volumes without hard links: FAT32, exFAT, shares) are copied in the background, the project referencing the
// original until the copy completes. The file carries "portableMedia": true; Resolve maps "media\..." back on open. Autosaves unaffected.
public static partial class PortableProject
{
    public const string MediaFolder = "media";

    // Portable marker property (ignored as unknown by older versions).
    private const string Marker = "portableMedia";

    // In-flight copy targets, so concurrent saves neither duplicate a copy nor reference it before completion.
    private static readonly HashSet<string> Copying = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Portable project path: Projects\Name\Name.hproj (name sanitized).</summary>
    public static string PathFor(string name)
    {
        var fileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (fileName.Length == 0)
            fileName = "Project";
        return Path.Combine(ProjectStore.Folder, fileName, fileName + ProjectStore.Extension);
    }

    /// <summary>True when the project sits in a same-named folder suitable for a media subfolder.</summary>
    public static bool IsPortableFolder(string projectPath) =>
        projectPath.Length > 0 && Path.GetDirectoryName(Path.GetFullPath(projectPath)) is { Length: > 0 } folder
        && string.Equals(Path.GetFileName(folder), Path.GetFileNameWithoutExtension(projectPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Portable save result: files linked or present, files copying in the background, and missing references.</summary>
    public readonly record struct Outcome(string Path, int InPlace, int Copying, int Missing, Task Copies);

    /// <summary>
    /// Saves a portable project at <paramref name="projectPath"/> (creating the folder): existing references become "media\file",
    /// hard-linked when possible, else copied in the background (original path until done, then rewritten). Export destination unchanged.
    /// </summary>
    public static Outcome Save(ProjectState state, string projectPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var media = Path.Combine(folder, MediaFolder);
        var project = JsonNode.Parse(ProjectStore.Serialize(state)) ?? throw new IOException("The project could not be written.");

        // Where the export goes is this PC's folder, never a file to carry: kept out of the rewrite.
        var output = project["output"] as JsonObject;
        var destination = output?["path"]?.DeepClone();
        output?.Remove("path");

        var mapped = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toCopy = new List<(string From, string To)>();
        int inPlace = 0, missing = 0;
        string? Map(string text)
        {
            if (!ProjectBundle.LooksLikeFilePath(text))
                return null;

            string full;
            try
            {
                full = Path.GetFullPath(text);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            if (mapped.TryGetValue(full, out var known))
                return known;
            if (!File.Exists(full))
            {
                missing++;
                return mapped[full] = null;
            }

            // Already in this project's media folder: named as it is there.
            var mediaRoot = media.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(mediaRoot, StringComparison.OrdinalIgnoreCase))
            {
                inPlace++;
                return mapped[full] = Path.Combine(MediaFolder, full[mediaRoot.Length..]);
            }

            var target = TargetFor(full, media, taken);
            var relative = Path.Combine(MediaFolder, Path.GetFileName(target));
            if (SameFile(full, target) || (!Copying.Contains(target) && TryLink(full, target)))
            {
                inPlace++;
                return mapped[full] = relative;
            }

            // Another drive: copied in the background, named where it is until then.
            if (Copying.Add(target))
                toCopy.Add((full, target));
            return mapped[full] = null;
        }

        Directory.CreateDirectory(media);
        lock (Copying)
            RewriteWithHistory(project, Map);
        if (output is not null && destination is not null)
            output["path"] = destination;
        if (project is JsonObject whole)
            whole[Marker] = true;

        WriteRefusingNewer(projectPath, project.ToJsonString());
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Portable project saved: {projectPath} ({inPlace} file(s) in its media folder, {toCopy.Count} being copied there, {missing} named file(s) not there)"));
        var copies = toCopy.Count == 0 ? Task.CompletedTask : Task.Run(() => CopyThenPoint(projectPath, toCopy));
        return new Outcome(projectPath, inPlace, toCopy.Count, missing, copies);
    }

    /// <summary>
    /// On open, maps a portable project's "media\..." references to the adjacent media folder (never outside it); other projects pass through.
    /// </summary>
    public static string Resolve(string json, string projectPath)
    {
        if (!json.Contains("\"" + Marker + "\"", StringComparison.Ordinal) || Path.GetDirectoryName(Path.GetFullPath(projectPath)) is not { Length: > 0 } folder)
            return json;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject project || project[Marker] is not JsonValue mark || !mark.TryGetValue<bool>(out var portable) || !portable)
                return json;

            RewriteWithHistory(project, text => IsMediaName(text) && ProjectBundle.InsideOf(folder, text) is { } file && File.Exists(file) ? file : null);
            return project.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            // Not readable as JSON: the reading that follows says so.
            return json;
        }
    }

    private static bool IsMediaName(string text) =>
        text.Length > MediaFolder.Length + 1 && text.StartsWith(MediaFolder, StringComparison.OrdinalIgnoreCase) && text[MediaFolder.Length] is '\\' or '/';

    // ----- Undo/redo history -----
    // Saved steps are packed timelines (UndoSteps: Brotli JSON, Base64), opaque to project-level string rewriting. Portable save, Resolve
    // and CopyThenPoint unpack, rewrite with the same map and repack each step, so undo works after moving the folder. Steps are first
    // scanned token by token; unchanged ones keep their bytes. Map results are memoized per string (one File.Exists per path).

    // History property names and the maximum unpacked step size (UndoSteps.MaximumBytes).
    private static readonly string[] Histories = ["undoHistory", "redoHistory"];
    private const int MaximumStepBytes = 128 * 1024 * 1024;

    /// <summary>Rewrites mapped strings in the project and in every undo/redo step via <paramref name="map"/>.</summary>
    private static void RewriteWithHistory(JsonNode project, Func<string, string?> map)
    {
        // Each string asked about once: the project and every step share the answers.
        var answers = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Answer(string text) => answers.TryGetValue(text, out var known) ? known : (answers[text] = map(text));

        ProjectBundle.Rewrite(project, Answer);
        if (project is not JsonObject whole)
            return;

        foreach (var name in Histories)
        {
            if (whole[name] is not JsonArray steps)
                continue;

            foreach (var step in steps)
            {
                if (step is JsonObject entry && entry["state"] is JsonValue value && value.TryGetValue<string>(out var packed)
                    && RewriteStep(packed, Answer) is { } rewritten)
                {
                    entry["state"] = rewritten;
                }
            }
        }
    }

    /// <summary>
    /// Rewrites and repacks a Base64 step; null when unchanged or unreadable (left as-is; loading drops unreadable steps, not the project).
    /// </summary>
    internal static string? RewriteStep(string packed, Func<string, string?> map)
    {
        JsonNode? timeline;
        try
        {
            var json = UnpackStep(Convert.FromBase64String(packed));
            if (!AnyChanged(json, map))
                return null;

            timeline = JsonNode.Parse(json);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            return null;
        }

        if (timeline is null)
            return null;

        var changed = false;
        ProjectBundle.Rewrite(timeline, text =>
        {
            var to = map(text);
            changed |= to is not null && !string.Equals(to, text, StringComparison.Ordinal);
            return to;
        });

        return changed ? Convert.ToBase64String(PackStep(System.Text.Encoding.UTF8.GetBytes(timeline.ToJsonString()))) : null;
    }

    // True if any JSON string in the step is remapped (token scan, no DOM, no writes).
    private static bool AnyChanged(ReadOnlySpan<byte> json, Func<string, string?> map)
    {
        var reader = new System.Text.Json.Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.String && reader.GetString() is { } text
                && map(text) is { } to && !string.Equals(to, text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // A step's JSON out of its Brotli stream, never past what any timeline comes to (a damaged step could claim gigabytes).
    private static byte[] UnpackStep(byte[] packed)
    {
        using var text = new MemoryStream();
        using (var brotli = new System.IO.Compression.BrotliStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress))
        {
            var buffer = new byte[65536];
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                text.Write(buffer, 0, read);
                if (text.Length > MaximumStepBytes)
                    throw new InvalidDataException("An undo step is larger than any timeline's.");
            }
        }

        return text.ToArray();
    }

    // A step's JSON packed as Undo packs it (UndoSteps.Pack): Brotli at quality 1, a 4 MB window.
    private static byte[] PackStep(byte[] json)
    {
        var packed = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(json.Length)];
        return System.IO.Compression.BrotliEncoder.TryCompress(json, packed, out var written, quality: 1, window: 22)
            ? packed.AsSpan(0, written).ToArray()
            : throw new InvalidDataException("An undo step could not be packed again.");
    }

    // The project file written whole, never over one a newer HandPeg wrote (as every save refuses that).
    private static void WriteRefusingNewer(string path, string text)
    {
        if (ProjectStore.PeekHeader(path) is { Format: ProjectFile.FormatName, Version: > ProjectFile.CurrentVersion })
            throw new IOException($"{Path.GetFileName(path)} was saved by a newer version of HandPeg, and saving over it would lose what that version put in it.");
        ProjectStore.WriteWhole(path, text, durable: true);
    }

    /// <summary>
    /// Media folder target: same name, reusing an identical existing file (length and write time), else numbered ("clip (2).mkv").
    /// </summary>
    private static string TargetFor(string full, string media, Dictionary<string, string> taken)
    {
        var name = Path.GetFileName(full);
        for (var n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(media, n == 1 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({n.ToString(CultureInfo.InvariantCulture)}){Path.GetExtension(name)}");
            if (taken.TryGetValue(candidate, out var owner))
            {
                if (string.Equals(owner, full, StringComparison.OrdinalIgnoreCase))
                    return candidate;
                continue;
            }

            if (!File.Exists(candidate) || SameFile(full, candidate) || Copying.Contains(candidate))
            {
                taken[candidate] = full;
                return candidate;
            }
        }

        return Path.Combine(media, Guid.NewGuid().ToString("N") + Path.GetExtension(name));
    }

    // Whether a file in the media folder is the file (a link to it, or a whole copy): the same length and last write.
    private static bool SameFile(string full, string target)
    {
        try
        {
            var (from, to) = (new FileInfo(full), new FileInfo(target));
            return to.Exists && from.Length == to.Length && Math.Abs((from.LastWriteTimeUtc - to.LastWriteTimeUtc).TotalSeconds) < 2.5;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A hard link to the file in the media folder: only on the same drive, and only where the drive has them (NTFS, ReFS).
    private static bool TryLink(string full, string target)
    {
        if (!string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            return false;
        if (File.Exists(target))
            return false;
        return CreateHardLinkW(target, full, 0);
    }

    /// <summary>
    /// Copies cross-volume files into media (via ".part", preserving write time for seek indexes), then rewrites the newest project file to
    /// reference completed copies.
    /// </summary>
    private static void CopyThenPoint(string projectPath, List<(string From, string To)> files)
    {
        var done = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in files)
        {
            var partial = to + ".part";
            try
            {
                File.Copy(from, partial, overwrite: true);
                File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(from));
                File.Move(partial, to, overwrite: true);
                done[Path.GetFullPath(from)] = Path.Combine(MediaFolder, Path.GetFileName(to));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Portable project: {from} could not be copied into its media folder", ex);
                try
                {
                    File.Delete(partial);
                }
                catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
                {
                }
            }
            finally
            {
                lock (Copying)
                    Copying.Remove(to);
            }
        }

        if (done.Count == 0)
            return;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(projectPath)) is not JsonObject project)
                return;

            var output = project["output"] as JsonObject;
            var destination = output?["path"]?.DeepClone();
            output?.Remove("path");
            RewriteWithHistory(project, text => ProjectBundle.LooksLikeFilePath(text) && done.TryGetValue(Path.GetFullPath(text), out var copy) ? copy : null);
            if (output is not null && destination is not null)
                output["path"] = destination;
            WriteRefusingNewer(projectPath, project.ToJsonString());
            AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Portable project: {done.Count} file(s) copied into its media folder, and the project points at them"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            AppLog.Write("Portable project: the project could not be pointed at its copied media", ex);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string newFile, string existingFile, nint securityAttributes);
}
