using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- The Personal Clip Library (Pass 5.2C) -----
//
// The user's own clips kept across projects (reactions, sound effects, highlights): Documents\HandPeg\Clips\, each clip a file of its own
// and a line of clip-ledger.json beside them (its title, tags, length, where in the file it really starts and ends, and the focal point a
// Ping-to-Mark highlight gave it). "Save Clip to Personal Clip Library..." on a clip's menu puts its span there as a lossless stream copy
// (VodTrim's cut: from the key frame at or before its start, every stream; a sound file's from the packet at or before it; a picture is
// copied whole): nothing is encoded again. The media browser's Clip Library lists them, searched by title and tags, scrubbed by
// hovering, dragged into any project; a clip dropped from there starts and ends where it was saved (In / Out), not on the key frame
// the copy had to begin with. The ledger is written whole through a ".part" file; one it cannot read is kept aside, never written over.
public static class ClipLibrary
{
    public const string LedgerName = "clip-ledger.json", FormatName = "handpeg-clip-ledger";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();

    /// <summary>Where the library is: Documents\HandPeg\Clips (the golden tests point it at a scratch folder).</summary>
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HandPeg", "Clips");

    public static string LedgerPath => Path.Combine(Folder, LedgerName);

    /// <summary>One clip of the library.</summary>
    public sealed class Entry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Its file's name in the library's folder.</summary>
        public string File { get; set; } = "";

        public string Title { get; set; } = "";
        public List<string> Tags { get; set; } = [];

        /// <summary>How long the clip is (seconds: Out - In).</summary>
        public double Duration { get; set; }

        /// <summary>Where the clip starts and ends in its file (seconds; the copy begins on a key frame at or before In).</summary>
        public double In { get; set; }

        public double Out { get; set; }

        /// <summary>Where on the picture a Ping-to-Mark highlight in its span was pinged (fractions of the frame), when one was.</summary>
        public double? FocalX { get; set; }

        public double? FocalY { get; set; }

        /// <summary>The file it was cut from (its name only) and where in it (seconds), for the user's own reference.</summary>
        public string? From { get; set; }

        public double FromSeconds { get; set; }

        public DateTime Saved { get; set; }

        [JsonIgnore]
        public string Path => System.IO.Path.Combine(Folder, File);

        /// <summary>Whether the entry answers a search: every word of it in the title or a tag (case aside); everything for none.</summary>
        public bool Matches(string search)
        {
            foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Title.Contains(word, StringComparison.OrdinalIgnoreCase) && !Tags.Exists(t => t.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            return true;
        }
    }

    private sealed class Ledger
    {
        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = CurrentVersion;
        public List<Entry> Clips { get; set; } = [];
    }

    /// <summary>The library's clips whose files are there, newest first; none when there is no ledger (or one this version cannot read).</summary>
    public static IReadOnlyList<Entry> Load()
    {
        lock (Gate)
            return [.. Read().Clips.Where(e => e.File.Length > 0 && System.IO.File.Exists(e.Path)).OrderByDescending(e => e.Saved)];
    }

    /// <summary>The library's entry for a file of its folder, or null for any other file.</summary>
    public static Entry? Find(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            if (!string.Equals(System.IO.Path.GetDirectoryName(full), System.IO.Path.GetFullPath(Folder).TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return null;
            var name = System.IO.Path.GetFileName(full);
            lock (Gate)
                return Read().Clips.FirstOrDefault(e => string.Equals(e.File, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static Ledger Read()
    {
        if (!System.IO.File.Exists(LedgerPath))
            return new Ledger();
        try
        {
            var ledger = JsonSerializer.Deserialize<Ledger>(System.IO.File.ReadAllText(LedgerPath), Options);
            if (ledger is { Format: FormatName, Version: <= CurrentVersion })
            {
                ledger.Clips = [.. (ledger.Clips ?? []).Where(e => e is not null)];
                return ledger;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Clip Library: the ledger could not be read", ex);
        }

        // Not ours to write over (another version's, or broken): kept aside, a ledger of its own begun.
        try
        {
            System.IO.File.Move(LedgerPath, LedgerPath + string.Create(CultureInfo.InvariantCulture, $".unread-{DateTime.Now:yyyyMMdd-HHmmss}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new Ledger();
    }

    private static void Write(Ledger ledger)
    {
        Directory.CreateDirectory(Folder);
        var partial = LedgerPath + ".part";
        System.IO.File.WriteAllText(partial, JsonSerializer.Serialize(ledger, Options));
        System.IO.File.Move(partial, LedgerPath, overwrite: true);
    }

    /// <summary>
    /// Puts a clip's span into the library (see the top of this file): a stream copy of its file from <paramref name="mediaFrom"/> to
    /// <paramref name="mediaTo"/> (its media seconds), its title, tags and focal point in the ledger. Null when it could not be cut
    /// (no FFmpeg, a file that is not there, a cut that does not land on a key frame).
    /// </summary>
    public static Entry? Save(string source, bool hasPicture, bool isStill, double mediaFrom, double mediaTo, string title, IEnumerable<string> tags,
        double? focalX, double? focalY, CancellationToken cancel = default)
    {
        if (!System.IO.File.Exists(source))
            return null;

        Directory.CreateDirectory(Folder);
        var name = FileNameFor(title, System.IO.Path.GetExtension(source));
        var target = System.IO.Path.Combine(Folder, name);
        double start, end;
        if (isStill)
        {
            // A picture: the file itself.
            System.IO.File.Copy(source, target, overwrite: false);
            (start, end) = (0, Math.Max(mediaTo - mediaFrom, 0));
        }
        else if (hasPicture)
        {
            if (VodTrim.CutOne(source, new VodTrim.Stretch(Math.Max(mediaFrom, 0), mediaTo), target, cancel) is not { } piece)
                return null;
            (start, end) = (Math.Max(mediaFrom, 0) - piece.From, Math.Min(mediaTo - piece.From, piece.Length));
        }
        else if (CutSound(source, Math.Max(mediaFrom, 0), mediaTo, target, cancel) is { } soundFrom)
        {
            (start, end) = (Math.Max(mediaFrom, 0) - soundFrom, mediaTo - soundFrom);
        }
        else
        {
            return null;
        }

        var entry = new Entry
        {
            File = name, Title = title.Trim().Length > 0 ? title.Trim() : System.IO.Path.GetFileNameWithoutExtension(source),
            Tags = [.. tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            In = Math.Max(Math.Round(start, 3), 0), Out = Math.Round(end, 3), Duration = Math.Round(Math.Max(end - Math.Max(start, 0), 0), 3),
            FocalX = focalX, FocalY = focalY, From = System.IO.Path.GetFileName(source), FromSeconds = Math.Round(mediaFrom, 3), Saved = DateTime.Now,
        };

        lock (Gate)
        {
            var ledger = Read();
            ledger.Clips.Add(entry);
            Write(ledger);
        }

        AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Clip Library: \"{entry.Title}\" saved ({entry.Duration:0.###} s, {entry.Tags.Count} tag(s))"));
        return entry;
    }

    /// <summary>Takes a clip out of the library: its line, and its file.</summary>
    public static bool Remove(Entry entry)
    {
        lock (Gate)
        {
            var ledger = Read();
            if (ledger.Clips.RemoveAll(e => e.Id == entry.Id) == 0)
                return false;
            Write(ledger);
        }

        try
        {
            System.IO.File.Delete(entry.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Clip Library: a clip's file could not be deleted", ex);
        }

        return true;
    }

    // A file name of the title (made fit for one), numbered when the folder has it already.
    private static string FileNameFor(string title, string extension)
    {
        var stem = string.Concat(title.Trim().Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (stem.Length == 0)
            stem = "Clip";
        if (stem.Length > 80)
            stem = stem[..80];
        var name = stem + extension;
        for (var n = 2; System.IO.File.Exists(System.IO.Path.Combine(Folder, name)); n++)
            name = string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){extension}");
        return name;
    }

    // A sound file's stretch as a stream copy from the packet at or before its start: where that packet is (media seconds), or null.
    private static double? CutSound(string source, double from, double to, string target, CancellationToken cancel)
    {
        if (!System.IO.File.Exists(DependencyUpdater.FfmpegPath))
            return null;

        var partial = target + ".part" + System.IO.Path.GetExtension(target);
        var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DependencyUpdater.FfmpegPath, string.Create(CultureInfo.InvariantCulture,
            $"-hide_banner -loglevel error -y -ss {from:0.######} -t {to - from:0.######} -i \"{source}\" -map 0:a -c copy -map_metadata 0 \"{partial}\""))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        });
        if (started is null)
            return null;
        using (started)
        {
            using var _ = cancel.Register(() =>
            {
                try
                {
                    started.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            });
            started.StandardError.ReadToEnd();
            started.WaitForExit();
            cancel.ThrowIfCancellationRequested();
            if (started.ExitCode != 0 || !System.IO.File.Exists(partial))
            {
                try
                {
                    System.IO.File.Delete(partial);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                return null;
            }
        }

        System.IO.File.Move(partial, target, overwrite: false);
        return from;
    }
}
