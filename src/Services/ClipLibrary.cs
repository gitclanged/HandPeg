using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- Personal Clip Library -----
//
// Cross-project reusable clips (reactions, SFX, highlights) in Documents\HandPeg\Clips\: one file per clip plus an entry in
// clip-ledger.json (title, tags, duration, true In/Out within the file, Ping-to-Mark focal point). "Save Clip to Personal Clip
// Library..." stores the span as a lossless stream copy (VodTrim cut from the preceding key frame, all streams; audio from the
// preceding packet; images copied whole). The Media Browser lists, searches (title, tags), hover-scrubs and drags them; dropped clips
// honour the saved In/Out, not the key-frame start. The ledger is written atomically via ".part"; unreadable ledgers are preserved.
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

        /// <summary>In/Out within the file (s); the copy begins on a key frame at or before In.</summary>
        public double In { get; set; }

        public double Out { get; set; }

        /// <summary>Optional Ping-to-Mark focal point (frame fractions).</summary>
        public double? FocalX { get; set; }

        public double? FocalY { get; set; }

        /// <summary>Source file name (no path) and offset (s), informational only.</summary>
        public string? From { get; set; }

        public double FromSeconds { get; set; }

        public DateTime Saved { get; set; }

        [JsonIgnore]
        public string Path => System.IO.Path.Combine(Folder, File);

        /// <summary>True when every query word appears in the title or a tag (case-insensitive); empty query matches all.</summary>
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

    /// <summary>Clips with existing files, newest first; empty when the ledger is missing or unreadable.</summary>
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
    /// Saves a span (<paramref name="mediaFrom"/> to <paramref name="mediaTo"/>, media seconds) as a stream copy plus ledger metadata.
    /// Null on failure (no FFmpeg, missing file, cut not on a key frame).
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

    // Audio stream copy from the packet at or before start; returns that packet's media time, or null.
    private static double? CutSound(string source, double from, double to, string target, CancellationToken cancel)
    {
        if (!System.IO.File.Exists(DependencyUpdater.FfmpegPath))
            return null;

        var partial = target + ".part" + System.IO.Path.GetExtension(target);
        System.Diagnostics.Process? started;
        try
        {
            started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DependencyUpdater.FfmpegPath, string.Create(CultureInfo.InvariantCulture,
                $"-hide_banner -loglevel error -y -ss {from:0.######} -t {to - from:0.######} -i \"{source}\" -map 0:a -c copy -map_metadata 0 \"{partial}\""))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write("Clip Library: FFmpeg could not be started", ex);
            return null;
        }

        if (started is null)
            return null;
        using (started)
        using (ProcessPipes.Adopt(started.Id))
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
