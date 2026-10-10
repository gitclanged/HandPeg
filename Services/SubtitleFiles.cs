using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Subtitle files, read and written by hand. The text formats (.srt, .vtt) are lists of blocks: a number, a
/// line of two times, and the words. The picture formats are read only for when each picture is shown: a
/// Blu-ray .sup (PGS) is a run of segments each stamped with its time, and a VobSub is an .idx of times beside
/// a .sub of pictures. Their pictures are never decoded here; a track of them is retimed by writing the same
/// bytes out again with other times.
/// </summary>
public static partial class SubtitleFiles
{
    public static readonly string[] TextExtensions = [".srt", ".vtt"];
    public static readonly string[] ImageExtensions = [".sup", ".pgs", ".idx", ".sub", ".vobsub"];

    public const string ImportFilter =
        "Subtitles|*.srt;*.vtt;*.sup;*.pgs;*.idx;*.sub;*.vobsub|Text subtitles (*.srt, *.vtt)|*.srt;*.vtt|Image subtitles (*.sup, *.pgs, *.idx, *.sub)|*.sup;*.pgs;*.idx;*.sub;*.vobsub|All files|*.*";

    /// <summary>What stands for the words of a cue that is a picture.</summary>
    public const string ImageCueText = "[image]";

    // "00:01:02,345 --> 00:01:04,000", with a comma or a point, and the hours left out as .vtt allows.
    [GeneratedRegex(@"^\s*(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"^timestamp:\s*(-?)(\d+):(\d+):(\d+):(\d+)\s*,\s*filepos:\s*([0-9a-fA-F]+)", RegexOptions.IgnoreCase)]
    private static partial Regex IdxLineRegex();

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsTextFile(string path) => TextExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Reads a subtitle file of any kind HandPeg knows into cues. Throws InvalidDataException for one it cannot make sense of.</summary>
    public static List<SubtitleCue> Read(string path) =>
        IsImageFile(path) ? ReadImageTimings(path) : ParseText(File.ReadAllText(path, DetectEncoding(path)));

    // ----- Text: .srt and .vtt -----

    /// <summary>
    /// Parses SubRip or WebVTT text. The two differ in little: .vtt has a header, may leave the hours and the
    /// numbers out, writes a point where .srt writes a comma, and may add settings after the times. A block is
    /// found by its line of times; what follows it, up to the next empty line, is its words.
    /// </summary>
    public static List<SubtitleCue> ParseText(string content)
    {
        var cues = new List<SubtitleCue>();
        var lines = content.ReplaceLineEndings("\n").TrimStart('﻿').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (TimingRegex().Match(lines[i]) is not { Success: true } timing)
                continue;

            var words = new StringBuilder();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                // A line of times straight after words is the next block, in a file with no empty line between.
                if (TimingRegex().IsMatch(lines[i]))
                {
                    i--;
                    break;
                }

                if (words.Length > 0)
                    words.Append('\n');
                words.Append(MarkupRegex().Replace(lines[i], "").Trim());
            }

            var (start, end) = (TimeOf(timing, 1), TimeOf(timing, 5));
            if (end > start && words.Length > 0)
                cues.Add(new SubtitleCue { Start = start, End = end, Text = System.Net.WebUtility.HtmlDecode(words.ToString()) });
        }

        return Numbered(cues);
    }

    private static double TimeOf(Match match, int group)
    {
        static int Part(Group g) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        var fraction = match.Groups[group + 3].Value.PadRight(3, '0');
        return Part(match.Groups[group]) * 3600 + Part(match.Groups[group + 1]) * 60 + Part(match.Groups[group + 2])
               + int.Parse(fraction, CultureInfo.InvariantCulture) / 1000.0;
    }

    /// <summary>The cues in order of time, numbered from 1.</summary>
    public static List<SubtitleCue> Numbered(IEnumerable<SubtitleCue> cues)
    {
        var ordered = cues.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Index = i + 1;
        return ordered;
    }

    /// <summary>The cues as SubRip text, each moved by an offset in seconds; cues moved to before the start are cut off there.</summary>
    public static string ToSrt(IEnumerable<SubtitleCue> cues, double offsetSeconds = 0)
    {
        var srt = new StringBuilder();
        var number = 0;
        foreach (var cue in cues.OrderBy(c => c.Start))
        {
            var (start, end) = (Math.Max(cue.Start + offsetSeconds, 0), cue.End + offsetSeconds);
            if (end - start < 0.01 || string.IsNullOrWhiteSpace(cue.Text))
                continue;

            srt.Append(++number).Append('\n').Append(SrtTime(start)).Append(" --> ").Append(SrtTime(end)).Append('\n')
                .Append(cue.Text.ReplaceLineEndings("\n").Trim()).Append("\n\n");
        }

        return srt.ToString();
    }

    private static string SrtTime(double seconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");
    }

    /// <summary>UTF-8 unless the file says otherwise with a byte order mark; a file that is not valid UTF-8 is read as the system's code page would be, Latin-1.</summary>
    private static Encoding DetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;

        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1;
        }
    }

    // ----- Pictures: when each is shown -----

    private static List<SubtitleCue> ReadImageTimings(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sup" or ".pgs" ? ReadSup(path) : ReadIdx(IdxPathOf(path));

    /// <summary>A VobSub is two files; whichever was picked, the times are in the .idx.</summary>
    public static string IdxPathOf(string path) => Path.GetExtension(path).ToLowerInvariant() == ".idx" ? path : Path.ChangeExtension(path, ".idx");

    private const int SupHeader = 13;
    private const byte PresentationSegment = 0x16;

    /// <summary>The segments of a .sup file: where each begins, how long it is with its header, its kind and its time (90000 to the second).</summary>
    private static List<(int At, int Length, byte Kind, uint Pts)> ReadSupSegments(byte[] bytes)
    {
        var segments = new List<(int, int, byte, uint)>();
        var at = 0;
        while (at + SupHeader <= bytes.Length)
        {
            if (bytes[at] != (byte)'P' || bytes[at + 1] != (byte)'G')
                throw new InvalidDataException("Not a PGS (.sup) subtitle file.");

            var length = SupHeader + BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 11));
            if (at + length > bytes.Length)
                break;

            segments.Add((at, length, bytes[at + 10], BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 2))));
            at += length;
        }

        return segments;
    }

    /// <summary>
    /// The display sets of a .sup: each begins with a presentation segment, which says how many pictures it
    /// puts on screen. One with pictures is a subtitle appearing; the next one with none is it going.
    /// </summary>
    private static List<SubtitleCue> ReadSup(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var cues = new List<SubtitleCue>();
        SubtitleCue? showing = null;
        foreach (var (at, length, kind, pts) in ReadSupSegments(bytes))
        {
            if (kind != PresentationSegment || length < SupHeader + 11)
                continue;

            var time = pts / 90000.0;
            if (showing is not null)
            {
                showing.End = Math.Max(time, showing.Start + 0.05);
                cues.Add(showing);
                showing = null;
            }

            // The count of pictures is the last byte of the presentation segment's fixed part.
            if (bytes[at + SupHeader + 10] > 0)
                showing = new SubtitleCue { Start = time, End = time + 3, Text = ImageCueText };
        }

        if (showing is not null)
            cues.Add(showing);
        return Numbered(cues);
    }

    /// <summary>The .idx of a VobSub: one line per picture with the time it appears. When it goes is in the .sub; here it is taken to stay until the next, four seconds at most.</summary>
    private static List<SubtitleCue> ReadIdx(string idxPath)
    {
        if (!File.Exists(idxPath))
            throw new InvalidDataException($"A VobSub needs its .idx file beside the .sub: {Path.GetFileName(idxPath)} was not found.");

        var starts = new List<double>();
        foreach (var line in File.ReadLines(idxPath))
        {
            if (IdxLineRegex().Match(line.Trim()) is { Success: true } match)
                starts.Add(IdxTime(match));
        }

        var cues = new List<SubtitleCue>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? Math.Min(starts[i + 1], starts[i] + 4) : starts[i] + 4;
            cues.Add(new SubtitleCue { Start = starts[i], End = Math.Max(end, starts[i] + 0.05), Text = ImageCueText });
        }

        return Numbered(cues);
    }

    private static double IdxTime(Match match)
    {
        int Part(int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
        var seconds = Part(2) * 3600 + Part(3) * 60 + Part(4) + Part(5) / 1000.0;
        return match.Groups[1].Value == "-" ? -seconds : seconds;
    }

    /// <summary>
    /// Writes a copy of a picture subtitle file with its pictures at other times: where the cues, which were
    /// read from it in this order, now say, plus an offset. Returns the file a player or FFmpeg is given, or
    /// null when the file no longer matches the cues.
    /// </summary>
    /// <param name="original">The cues as they were read from the file, in the file's order.</param>
    /// <param name="edited">The same cues, by position, with the times wanted.</param>
    public static string? WriteRetimed(string sourcePath, IReadOnlyList<SubtitleCue> original, IReadOnlyList<SubtitleCue> edited, double offsetSeconds, string folder, string name)
    {
        if (original.Count != edited.Count)
            return null;

        Directory.CreateDirectory(folder);
        return Path.GetExtension(sourcePath).ToLowerInvariant() is ".sup" or ".pgs"
            ? WriteSup(sourcePath, original, edited, offsetSeconds, Path.Combine(folder, name + ".sup"))
            : WriteVobSub(sourcePath, edited, offsetSeconds, Path.Combine(folder, name + ".idx"));
    }

    private static string? WriteSup(string sourcePath, IReadOnlyList<SubtitleCue> original, IReadOnlyList<SubtitleCue> edited, double offsetSeconds, string targetPath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var segments = ReadSupSegments(bytes);

        // Each display set is moved as a whole: one that shows a picture by how far its cue's start moved,
        // the one that clears it by how far the cue's end did.
        var (cue, clears, shift) = (-1, false, 0.0);
        foreach (var (at, length, kind, pts) in segments)
        {
            if (kind == PresentationSegment && length >= SupHeader + 11)
            {
                if (bytes[at + SupHeader + 10] > 0)
                {
                    (cue, clears) = (cue + 1, false);
                    if (cue >= edited.Count)
                        return null;
                    shift = edited[cue].Start - original[cue].Start;
                }
                else if (cue >= 0 && !clears)
                {
                    (clears, shift) = (true, edited[cue].End - original[cue].End);
                }
            }

            var ticks = (long)Math.Round((shift + offsetSeconds) * 90000);
            var moved = (uint)Math.Clamp(pts + ticks, 0, uint.MaxValue);
            var decode = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 6));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 2), moved);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 6), decode == 0 ? 0 : (uint)Math.Clamp(decode + ticks, 0, uint.MaxValue));
        }

        File.WriteAllBytes(targetPath, bytes);
        return targetPath;
    }

    private static string? WriteVobSub(string sourcePath, IReadOnlyList<SubtitleCue> edited, double offsetSeconds, string targetIdx)
    {
        var idxPath = IdxPathOf(sourcePath);
        var subPath = Path.ChangeExtension(idxPath, ".sub");
        if (!File.Exists(idxPath) || !File.Exists(subPath))
            return null;

        var lines = File.ReadAllLines(idxPath);
        var cue = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IdxLineRegex().Match(lines[i].Trim()) is not { Success: true } match)
                continue;
            if (cue >= edited.Count)
                return null;

            var time = TimeSpan.FromMilliseconds(Math.Round(Math.Max(edited[cue++].Start + offsetSeconds, 0) * 1000));
            lines[i] = string.Create(CultureInfo.InvariantCulture,
                $"timestamp: {(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}:{time.Milliseconds:000}, filepos: {match.Groups[6].Value}");
        }

        File.WriteAllLines(targetIdx, lines);
        File.Copy(subPath, Path.ChangeExtension(targetIdx, ".sub"), overwrite: true);
        return targetIdx;
    }
}
