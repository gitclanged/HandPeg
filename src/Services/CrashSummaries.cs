using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

/// <summary>
/// What a crash report may say about the work in hand, made from it: the kind of thing and its numbers (container,
/// codecs, sizes, rates, lengths, counts, the names of the filters in a graph), never a path, a title or any of the
/// media's own words. These go to <see cref="CrashReporter.SetContext"/> and <see cref="CrashReporter.Note"/>; the
/// sanitizer still goes over them when a report is written.
/// </summary>
public static partial class CrashSummaries
{
    /// <summary>"mkv (matroska,webm), 1h02m03.4s, h264 1920x1080 29.97 fps yuv420p, 2 audio (aac 2ch 48000 Hz, ac3 6ch 48000 Hz), 1 subtitle (subrip), 3 layers".</summary>
    public static string Media(string path, MediaInfo? info, int layers = 0)
    {
        var text = new StringBuilder(Extension(path));
        if (info is null)
            return text.Append(", not probed").ToString();

        text.Append(" (").Append(Word(info.Container)).Append("), ").Append(Duration(info.DurationSeconds));
        if (info.Video is { } video)
        {
            text.Append(CultureInfo.InvariantCulture, $", {Word(video.Codec)} {video.Width}x{video.Height} {video.FrameRate:0.###} fps");
            if (video.PixelFormat.Length > 0)
                text.Append(' ').Append(Word(video.PixelFormat));
            if (video.IsHdr)
                text.Append(" HDR");
        }

        if (info.Audio.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $", {info.Audio.Count} audio (");
            text.AppendJoin(", ", info.Audio.Take(4).Select(a => string.Create(CultureInfo.InvariantCulture, $"{Word(a.Codec)} {a.Channels}ch {a.SampleRate} Hz")));
            text.Append(info.Audio.Count > 4 ? ", ...)" : ")");
        }

        if (info.Subtitles.Count > 0)
            text.Append(CultureInfo.InvariantCulture, $", {info.Subtitles.Count} subtitle ({string.Join(", ", info.Subtitles.Take(4).Select(s => Word(s.Codec)).Distinct())})");
        if (layers > 0)
            text.Append(CultureInfo.InvariantCulture, $", {layers} layers");
        return text.ToString();
    }

    /// <summary>"format 5, mkv source, mp4 out, 4 tracks, 12 clips, 2 audio streams, 4 markers, subtitles" (the format when it is known).</summary>
    public static string Project(ProjectState state, int? format = null)
    {
        var source = Extension(state.LocalMediaPath.Length > 0 ? state.LocalMediaPath : state.SourcePath);
        var known = format is { } number ? string.Create(CultureInfo.InvariantCulture, $"format {number}, ") : "";
        var subtitles = state.Subtitles is not null ? ", subtitles" : "";
        var timeline = state.Timeline;
        return string.Create(CultureInfo.InvariantCulture,
            $"{known}{source} source, {Word(state.Container)} out, {timeline.Tracks.Count} tracks, {timeline.Tracks.Sum(t => t.Clips.Count)} clips, {state.AudioTracks.Count} audio streams, {timeline.Markers.Count} markers{subtitles}");
    }

    /// <summary>
    /// The files a project uses (its source, the file actually read, its output, the captions' sound, and the file of every
    /// clip, on the timeline and in the recycle bin, and of every track's shared look), for <see cref="CrashReporter.KeepOut(IEnumerable{string?})"/>.
    /// </summary>
    public static IEnumerable<string?> FilesIn(ProjectState state) =>
    [
        state.SourcePath, state.LocalMediaPath, state.DestinationPath, state.CaptionAudioPath,
        .. state.Timeline.Tracks.Select(t => t.TrackLook?.ImagePath),
        .. state.Timeline.Tracks.SelectMany(t => t.Clips).Concat(state.Deleted.Select(d => d.Clip).OfType<TimelineClip>())
            .SelectMany(c => new[] { c.SourcePath, c.State?.ImagePath }),
    ];

    /// <summary>
    /// An FFmpeg command as its kind: the video and audio encoders, the output's container, its frame size when the
    /// command says it, how long the output should be, and the filter graph's size (or that it is read from a script
    /// file). "libx265, aac, to .mp4, 1920x1080, 10m00.0s, graph 12345 chars". Only names and numbers are read.
    /// </summary>
    public static string Command(string command, TimeSpan expected)
    {
        var video = VideoCodec().Match(command) is { Success: true } v ? Word(v.Groups[1].Value) : "default video";
        var audio = AudioCodec().Match(command) is { Success: true } a ? Word(a.Groups[1].Value) : "default audio";
        var output = OutputExtension().Match(command) is { Success: true } o ? "." + o.Groups[1].Value.ToLowerInvariant() : "unknown output";
        var size = FrameSize().Match(command) is { Success: true } s ? ", " + s.Groups["size"].Value : "";
        var graph = FilterComplex().Match(command) is { Success: true } g ? string.Create(CultureInfo.InvariantCulture, $", graph {g.Groups[1].Length} chars")
            : FilterScript().IsMatch(command) ? ", graph from a script file"
            : "";
        return $"{video}, {audio}, to {output}{size}, {Duration(expected.TotalSeconds)}{graph}";
    }

    /// <summary>
    /// A filter graph as its size and the filters in it, most used first: "4120 chars, 37 filters: overlay 6, scale 5,
    /// setpts 5, movie 3, trim 3...". Only names are read: what is in quotes, and every option's value, is skipped.
    /// </summary>
    public static string Graph(string graph)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var name in FilterNames(graph))
        {
            counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
            total++;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{graph.Length} chars, {total} filters");
        var first = true;
        foreach (var (name, count) in counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Take(12))
        {
            text.Append(first ? ": " : ", ").Append(name);
            if (count > 1)
                text.Append(' ').Append(count.ToString(CultureInfo.InvariantCulture));
            first = false;
        }

        if (counts.Count > 12)
            text.Append(", ...");
        return text.ToString();
    }

    /// <summary>
    /// The filter names of a graph, in order. A filter starts the graph, or follows a "," or ";" outside quotes, after
    /// any [pad] labels; its name runs to "@", "=", "[", "," or ";". Quoted text ('...') and escaped characters are
    /// stepped over, so nothing inside an option is ever taken for a name.
    /// </summary>
    public static IEnumerable<string> FilterNames(string graph)
    {
        var expectName = true;
        var name = new StringBuilder();
        for (var i = 0; i < graph.Length; i++)
        {
            var c = graph[i];
            if (c == '\\')
            {
                i++; // An escaped character is part of whatever it is in.
                continue;
            }

            if (c == '\'')
            {
                var close = graph.IndexOf('\'', i + 1);
                i = close < 0 ? graph.Length : close;
                expectName = false;
                continue;
            }

            if (c == '[')
            {
                var close = graph.IndexOf(']', i + 1);
                if (name.Length > 0 && Keep(name.ToString()) is { } labelled)
                    yield return labelled;
                name.Clear();
                i = close < 0 ? graph.Length : close;
                continue;
            }

            if (c is ',' or ';')
            {
                if (name.Length > 0 && Keep(name.ToString()) is { } ended)
                    yield return ended;
                name.Clear();
                expectName = true;
                continue;
            }

            if (!expectName)
                continue;

            if (c is '@' or '=')
            {
                if (Keep(name.ToString()) is { } named)
                    yield return named;
                name.Clear();
                expectName = false;
            }
            else if (!char.IsWhiteSpace(c))
            {
                name.Append(c);
            }
        }

        if (expectName && name.Length > 0 && Keep(name.ToString()) is { } last)
            yield return last;
    }

    // A filter's name is an identifier: anything else (a stray word) is not one, and is not kept.
    private static string? Keep(string name) => FilterName().IsMatch(name) ? name : null;

    /// <summary>"1h02m03.4s", "4m05.0s", "12.3s".</summary>
    public static string Duration(double seconds)
    {
        if (!(seconds > 0) || double.IsInfinity(seconds))
            return "no length";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}h{time.Minutes:00}m{time.Seconds + time.Milliseconds / 1000.0:00.0}s")
            : time.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}m{time.Seconds + time.Milliseconds / 1000.0:00.0}s")
                : string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0}s");
    }

    /// <summary>A file's extension as its kind ("mkv"), or "no extension"; a web address is "web".</summary>
    public static string Extension(string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "web";
        try
        {
            var extension = Path.GetExtension(path.Trim().Trim('"')).TrimStart('.').ToLowerInvariant();
            return Word(extension) is { Length: > 0 } word ? word : "no extension";
        }
        catch (ArgumentException)
        {
            return "no extension";
        }
    }

    // A codec or format name as FFmpeg writes it: letters, digits, "_", ",", "-" and ".". Anything else is not one.
    private static string Word(string value) => CodeWord().IsMatch(value) ? value : value.Length == 0 ? "" : "other";

    [GeneratedRegex(@"^[A-Za-z0-9_.,\-]{1,40}$")]
    private static partial Regex CodeWord();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,40}$")]
    private static partial Regex FilterName();

    [GeneratedRegex(@"(?:^|\s)-(?:c:v|codec:v|vcodec)(?::0)?\s+(\S+)")]
    private static partial Regex VideoCodec();

    [GeneratedRegex(@"(?:^|\s)-(?:c:a|codec:a|acodec)(?::0)?\s+(\S+)")]
    private static partial Regex AudioCodec();

    // The output is the command's last argument: its extension, before a closing quote or the end.
    [GeneratedRegex(@"\.([A-Za-z0-9]{1,6})""?\s*$")]
    private static partial Regex OutputExtension();

    [GeneratedRegex(@"-filter_complex\s+(""[^""]*""|\S+)")]
    private static partial Regex FilterComplex();

    // The graph read from a file: "-/filter_complex file" (FFmpeg 7 and later) or the older "-filter_complex_script file".
    [GeneratedRegex(@"(?:^|\s)-(?:/filter_complex|filter_complex_script)\s")]
    private static partial Regex FilterScript();

    // The output's frame size: "-s 1920x1080", or the sequence canvas a graph is built on ("color=c=black:s=1920x1080").
    [GeneratedRegex(@"(?:(?:^|\s)-s(?::v)?\s+|color=c=[A-Za-z0-9#@.]*:s=)(?<size>\d{2,5}x\d{2,5})(?!\d)")]
    private static partial Regex FrameSize();
}
