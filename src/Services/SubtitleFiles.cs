using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Hand-written subtitle readers/writers. Text formats (.srt, .vtt): blocks of index, time line and text. Bitmap formats are parsed for
/// timing only: Blu-ray .sup (PGS, timestamped segments) and VobSub (.idx times beside .sub images). Images are never decoded;
/// retiming rewrites the same bytes with new times.
/// </summary>
public static partial class SubtitleFiles
{
    public static readonly string[] ImageExtensions = [".sup", ".pgs", ".idx", ".sub", ".vobsub"];

    public const string ImportFilter =
        "Subtitles|*.srt;*.vtt;*.sup;*.pgs;*.idx;*.sub;*.vobsub|Text subtitles (*.srt, *.vtt)|*.srt;*.vtt|Image subtitles (*.sup, *.pgs, *.idx, *.sub)|*.sup;*.pgs;*.idx;*.sub;*.vobsub|All files|*.*";

    /// <summary>What stands for the words of a cue that is a picture.</summary>
    public const string ImageCueText = "[image]";

    // "00:01:02,345 --> 00:01:04,000", with a comma or a point, and the hours left out as .vtt allows.
    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"^timestamp:\s*(-?)(\d+):(\d+):(\d+):(\d+)\s*,\s*filepos:\s*([0-9a-fA-F]+)", RegexOptions.IgnoreCase)]
    private static partial Regex IdxLineRegex();

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Reads any supported subtitle file into cues; throws InvalidDataException when unparseable.</summary>
    public static List<SubtitleCue> Read(string path) =>
        IsImageFile(path) ? ReadImageTimings(path) : ParseText(File.ReadAllText(path, DetectEncoding(path)));

    // ----- Text: .srt and .vtt -----

    /// <summary>
    /// Parses SubRip or WebVTT (.vtt adds a header, optional hours/indices, '.' decimals and cue settings). Blocks are located by their
    /// time line; text runs to the next blank line.
    /// </summary>
    public static List<SubtitleCue> ParseText(string content)
    {
        // Parse in place: no normalized copy, no per-line strings, one shared StringBuilder for cue text.
        var cues = new List<SubtitleCue>();
        var words = new StringBuilder();
        var (open, start, end) = (false, 0.0, 0.0);
        foreach (var line in content.AsSpan().TrimStart('\uFEFF').EnumerateLines())
        {
            // A line of times begins a block, and ends the one before it in a file with no empty line between.
            if (TryReadTiming(line, out var from, out var to))
            {
                AddCue(cues, words, open, start, end);
                (open, start, end) = (true, from, to);
                continue;
            }

            if (!open)
                continue;

            if (line.Trim().IsEmpty)
            {
                AddCue(cues, words, open, start, end);
                open = false;
                continue;
            }

            if (words.Length > 0)
                words.Append('\n');
            AppendWithoutMarkup(words, line);
        }

        AddCue(cues, words, open, start, end);
        return Numbered(cues);
    }

    private static void AddCue(List<SubtitleCue> cues, StringBuilder words, bool open, double start, double end)
    {
        if (open && end > start && words.Length > 0)
            cues.Add(new SubtitleCue { Start = start, End = end, Text = System.Net.WebUtility.HtmlDecode(words.ToString()) });
        words.Clear();
    }

    /// <summary>Adds a line without its tags (&lt;i&gt;, {\an8}) and without the space at its ends.</summary>
    private static void AppendWithoutMarkup(StringBuilder words, ReadOnlySpan<char> line)
    {
        var (first, at) = (words.Length, 0);
        foreach (var tag in MarkupRegex().EnumerateMatches(line))
        {
            words.Append(line[at..tag.Index]);
            at = tag.Index + tag.Length;
        }

        words.Append(line[at..]);

        var last = words.Length;
        while (last > first && char.IsWhiteSpace(words[last - 1]))
            last--;
        words.Length = last;

        var lead = first;
        while (lead < words.Length && char.IsWhiteSpace(words[lead]))
            lead++;
        if (lead > first)
            words.Remove(first, lead - first);
    }

    /// <summary>
    /// Parses a time line ("00:01:02,500 --&gt; 00:01:04,000"; .vtt may use '.' and omit hours); trailing cue settings are ignored.
    /// </summary>
    private static bool TryReadTiming(ReadOnlySpan<char> line, out double start, out double end)
    {
        end = 0;
        var rest = line.TrimStart();
        if (!TryReadTime(ref rest, out start))
            return false;

        rest = rest.TrimStart();
        if (!rest.StartsWith("-->"))
            return false;

        rest = rest[3..].TrimStart();
        return TryReadTime(ref rest, out end);
    }

    /// <summary>[hours:]minutes:seconds and up to three digits of a fraction, taken off the front of the text.</summary>
    private static bool TryReadTime(ref ReadOnlySpan<char> text, out double seconds)
    {
        seconds = 0;
        var a = ReadDigits(ref text, int.MaxValue, out var lengthA);
        if (lengthA == 0 || text.IsEmpty || text[0] != ':')
            return false;

        text = text[1..];
        var b = ReadDigits(ref text, int.MaxValue, out var lengthB);
        if (lengthB == 0)
            return false;

        int hours, minutes, whole;
        if (!text.IsEmpty && text[0] == ':')
        {
            text = text[1..];
            var c = ReadDigits(ref text, int.MaxValue, out var lengthC);
            if (lengthB > 2 || lengthC != 2)
                return false;

            (hours, minutes, whole) = (a, b, c);
        }
        else
        {
            if (lengthA > 2 || lengthB != 2)
                return false;

            (hours, minutes, whole) = (0, a, b);
        }

        if (text.IsEmpty || text[0] is not ('.' or ','))
            return false;

        text = text[1..];
        var fraction = ReadDigits(ref text, 3, out var places);
        if (places == 0)
            return false;

        for (; places < 3; places++)
            fraction *= 10;

        seconds = hours * 3600 + minutes * 60 + whole + fraction / 1000.0;
        return true;
    }

    /// <summary>The number at the front of the text, of at most so many digits, taken off it.</summary>
    private static int ReadDigits(ref ReadOnlySpan<char> text, int most, out int length)
    {
        var (value, read) = (0L, 0);
        while (read < text.Length && read < most && char.IsAsciiDigit(text[read]))
        {
            value = Math.Min(value * 10 + (text[read] - '0'), int.MaxValue / 3600);
            read++;
        }

        text = text[read..];
        length = read;
        return (int)value;
    }

    /// <summary>The cues in order of time, numbered from 1.</summary>
    public static List<SubtitleCue> Numbered(IEnumerable<SubtitleCue> cues)
    {
        var ordered = cues.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Index = i + 1;
        return ordered;
    }

    private static string SrtTime(double seconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");
    }

    /// <summary>Detects encoding: BOM, else UTF-8, falling back to Latin-1 for invalid UTF-8.</summary>
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

    /// <summary>Parses .sup segments: offset, length including header, type and 90 kHz timestamp.</summary>
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
    /// Groups .sup display sets by presentation segment: a set with objects starts a subtitle; the next empty set ends it.
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

    /// <summary>Parses VobSub .idx start times; end times (stored in .sub) are approximated as the next start, capped at 4 s.</summary>
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
}
