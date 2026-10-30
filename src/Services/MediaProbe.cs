using System.Globalization;
using System.IO;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

public sealed record MediaInfo(
    string Container,
    double DurationSeconds,
    long BitRate,
    VideoStreamInfo? Video,
    IReadOnlyList<AudioStreamInfo> Audio,
    IReadOnlyList<SubtitleStreamInfo> Subtitles)
{
    /// <summary>
    /// The time stamp of the file's first video frame, in seconds: 0 for most files, more for a transport stream
    /// or a recording cut from a longer one. A filter graph that seeks in the file and cuts on its own time
    /// stamps counts from here, as a graph that counts frames from the first one does.
    /// </summary>
    public double VideoStartSeconds { get; init; }

    /// <summary>Where the container's clock starts, in seconds: the earliest of its streams. A seek point (movie='s sp=) is counted from here.</summary>
    public double ContainerStartSeconds { get; init; }

    /// <summary>The file's chapters (4.2B-2), in seconds from its start: a download's own, and the SponsorBlock ones yt-dlp marked.</summary>
    public IReadOnlyList<MediaChapter> Chapters { get; init; } = [];
}

/// <summary>A chapter of a file: where it starts and ends, in seconds from the file's start, and its title (may be empty).</summary>
public sealed record MediaChapter(double Start, double End, string Title);

/// <summary>
/// Everything known about seeking in one video file, as it was when learnt (its size and last write, so that a changed
/// file is never taken for it): kept in the Timeline cache (seekindex.bin) and in the projects that use the file, so a
/// file opened again is ready to be seeked in before anything is probed.
/// </summary>
public sealed record SeekFacts(string Path, long Length, long WrittenTicks, double ContainerStart, double VideoStart, double Duration, double[] IFrames);

/// <summary>
/// What a graph needs to open a file exactly at an I-frame: where the container's clock starts, and the time stamps of
/// the video's I-frames on that clock, ascending (from the packets' flags).
/// </summary>
public sealed record SeekIndex(double ContainerStart, double[] IFrames)
{
    /// <summary>The I-frame at or before a moment of the file's own clock, and the one after it (infinity for none); null before the first.</summary>
    public (double At, double Next)? Around(double moment)
    {
        var i = Array.BinarySearch(IFrames, moment);
        if (i < 0)
            i = ~i - 1;
        return i < 0 ? null : (IFrames[i], i + 1 < IFrames.Length ? IFrames[i + 1] : double.PositiveInfinity);
    }
}

public sealed record VideoStreamInfo(
    int Width,
    int Height,
    string Codec,
    double FrameRate,
    string DisplayAspectRatio,
    string SampleAspectRatio,
    string ColorTransfer,
    string ColorPrimaries,
    long BitRate)
{
    /// <summary>PQ (HDR10, Dolby Vision) and HLG are the two HDR transfer functions.</summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>FFmpeg's name for how its pixels are stored: yuv420p, yuv420p10le...</summary>
    public string PixelFormat { get; init; } = "";
}

/// <param name="Index">Position among the audio streams, as used in "0:a:N".</param>
/// <param name="HandlerName">
/// The name an MP4 or MOV recorder gives the track instead of a title (4.3E: read by the voice track scanner alone, since
/// most are a tool's generic "SoundHandler").
/// </param>
public sealed record AudioStreamInfo(
    int Index, string Codec, int Channels, string ChannelLayout, int SampleRate, long BitRate, string Title, string Language, string HandlerName = "");

/// <param name="Index">Position among the subtitle streams, as used in "0:s:N".</param>
public sealed record SubtitleStreamInfo(int Index, string Codec, string Title, string Language)
{
    /// <summary>Only text subtitles can be drawn by the subtitles filter; bitmap formats cannot.</summary>
    public bool IsText => Codec is not ("hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub");
}

public static class MediaProbe
{
    /// <summary>
    /// Inspects a file with ffprobe. Returns null when ffprobe is unavailable or cannot read the file.
    /// </summary>
    /// <param name="lane">Who is waiting for it (see WorkScheduler): a file being loaded, or the media browser's tiles (Idle).</param>
    /// <param name="urgent">
    /// A user's action waits on it this moment (4.3E: a file just dropped): it starts at once, never queued behind other
    /// work of its lane (the media browser's posters and strips), and is never held (see WorkSpec.Urgent).
    /// </param>
    /// <remarks>
    /// A file already inspected this session, and not changed since, is answered from memory (4.3E): a file the media
    /// browser has described is not inspected again when it is dropped. The tool is started on the thread pool, never on
    /// the thread that asked (the interface's, for most), and what it wrote is read there too.
    /// </remarks>
    public static async Task<MediaInfo?> ProbeAsync(string path, CancellationToken cancellationToken, WorkLane lane = WorkLane.Interactive, bool urgent = false)
    {
        // The file as it is now, looked at afresh (on the thread pool: a share or a sleeping drive may be slow to answer), before
        // the tool reads it: what is kept of it is only good for exactly this.
        var before = await Task.Run(() => Stamp(path), cancellationToken).ConfigureAwait(false);
        if (before is { } stamp && Key(path) is { } known && Probed.TryGetValue(known, out var kept)
            && stamp.Length == kept.Length && stamp.WrittenTicks == kept.WrittenTicks)
            return kept.Info;

        if (!File.Exists(DependencyUpdater.FfprobePath))
            return null;

        var result = await WorkScheduler.Run(new WorkSpec($"Inspecting {Path.GetFileName(path)}", lane) { Visible = false, Urgent = urgent },
            work => Task.Run(() => ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfprobePath)
                .WithArguments(["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path])
                .WithValidation(CommandResultValidation.None), work.Token), work.Token), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            return null;

        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            var info = Parse(json.RootElement);
            if (info.Video is not null && Key(path) is { } key)
            {
                VideoStarts[key] = info.VideoStartSeconds;
                ContainerStarts[key] = info.ContainerStartSeconds;
                Durations[key] = info.DurationSeconds;
                KeepSeekFacts(path);
            }

            KeepInfo(path, info, before);
            return info;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What each file was found to be this session, with the size and last write it had then (a file changed since is
    // inspected again), so that a file is inspected once however many ask (the media browser, then a drop). The most kept:
    // past it, they are all let go and kept again as they are asked for.
    private const int ProbesKept = 256;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (MediaInfo Info, long Length, long WrittenTicks)> Probed = new(StringComparer.OrdinalIgnoreCase);

    // Kept with the file as it was before the tool read it, and only when it is still so (a file being written as it was
    // read is inspected again next time).
    private static void KeepInfo(string path, MediaInfo info, (long Length, long WrittenTicks)? before)
    {
        if (before is not { } stamp || Key(path) is not { } key || Stamp(path) != stamp)
            return;

        if (Probed.Count >= ProbesKept)
            Probed.Clear();
        Probed[key] = (info, stamp.Length, stamp.WrittenTicks);
    }

    /// <summary>A file's size and last write, looked at now; null when it is not there or can not be looked at.</summary>
    private static (long Length, long WrittenTicks)? Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Where each file's picture starts, for every video file inspected this session: see MediaInfo.VideoStartSeconds.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> VideoStarts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a file's picture starts, when the file has been inspected this session; null when it has not.</summary>
    public static double? KnownVideoStart(string path) => Key(path) is { } key && VideoStarts.TryGetValue(key, out var start) ? start : null;

    /// <summary>Where a probed file's container clock starts (seconds), even before its I-frames are listed; null when not probed.</summary>
    public static double? KnownContainerStart(string path) => Key(path) is { } key && ContainerStarts.TryGetValue(key, out var start) ? start : null;

    // Where each file's container clock starts (the earliest of its streams), how long it is, and the time stamps of its
    // video's I-frames (with the size and last write of the file they were listed from): what a seek in the file lands
    // on. Learnt once per file and session, or taken up from what was kept of it (see Recall and Adopt).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> ContainerStarts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> Durations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double[] Times, long Length, long WrittenTicks)> IFrames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a graph needs to seek in a file to an I-frame exactly (see <see cref="SeekIndex"/>), when the file has been
    /// inspected and its I-frames listed this session, or both were taken up from what was kept; null otherwise.
    /// </summary>
    public static SeekIndex? KnownSeekIndex(string path) =>
        Key(path) is { } key && IFrames.TryGetValue(key, out var listed) && ContainerStarts.TryGetValue(key, out var start) ? new SeekIndex(start, listed.Times) : null;

    /// <summary>
    /// What a file was found to be this session, when it was inspected and has not changed since (as FileState sees it now);
    /// null otherwise. Never inspects: for a question asked on the interface's thread (a clip's right-click menu).
    /// </summary>
    public static MediaInfo? KnownInfo(string path) =>
        Key(path) is { } key && Probed.TryGetValue(key, out var kept) && FileState.Get(path) is { Exists: true } file
            && file.Length == kept.Length && file.WrittenUtc.Ticks == kept.WrittenTicks ? kept.Info : null;

    /// <summary>A file's I-frame times when they are known for the file as it is now (not as it was before it changed); null otherwise.</summary>
    public static double[]? KnownIFrames(string path) =>
        Key(path) is { } key && IFrames.TryGetValue(key, out var listed) && FileState.Get(path) is { Exists: true } file
            && file.Length == listed.Length && file.WrittenUtc.Ticks == listed.WrittenTicks ? listed.Times : null;

    /// <summary>Keeps a file's I-frame times (ascending, on the file's own clock), as FfmpegRunner.GetIFramesAsync lists them.</summary>
    public static void NoteIFrames(string path, IReadOnlyList<double> times)
    {
        if (times.Count == 0 || Key(path) is not { } key)
            return;

        var file = FileState.Get(path);
        IFrames[key] = ([.. times], file.Length, file.WrittenUtc.Ticks);
        KeepSeekFacts(path);
    }

    // ----- Seek facts kept between sessions: the Timeline cache's seekindex.bin, and the project file -----

    private const string SeekKind = "seekindex";
    private const uint SeekMagic = 0x49535048; // "HPSI"
    private const int SeekVersion = 1;

    // Files whose facts are in the Timeline cache, this session (written, or read from it): each is written once.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> SeekKept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Everything known about seeking in a file as it is now (its start, length and I-frames, with its size and last
    /// write), to be kept with a project; null while any of it is not known, or the file has changed since.
    /// </summary>
    public static SeekFacts? KnownFacts(string path)
    {
        if (Key(path) is not { } key || !VideoStarts.TryGetValue(key, out var videoStart) || !ContainerStarts.TryGetValue(key, out var containerStart)
            || KnownIFrames(path) is not { } times)
            return null;

        var file = FileState.Get(path);
        return new SeekFacts(key, file.Length, file.WrittenUtc.Ticks, containerStart, videoStart, Durations.GetValueOrDefault(key), times);
    }

    /// <summary>
    /// Takes up facts kept elsewhere (a project file) for a file that is still exactly as it was then (same size, same
    /// last write); false, and nothing taken, for a file that is gone or has changed.
    /// </summary>
    public static bool Adopt(SeekFacts facts)
    {
        if (facts.IFrames.Length == 0 || Key(facts.Path) is not { } key || FileState.Get(facts.Path) is not { Exists: true } file
            || file.Length != facts.Length || file.WrittenUtc.Ticks != facts.WrittenTicks)
            return false;

        VideoStarts[key] = facts.VideoStart;
        ContainerStarts[key] = facts.ContainerStart;
        Durations[key] = facts.Duration;
        IFrames[key] = (facts.IFrames, facts.Length, facts.WrittenTicks);
        KeepSeekFacts(facts.Path);
        return true;
    }

    /// <summary>
    /// Makes a video file ready to be seeked in from what the Timeline cache kept of it (seekindex.bin, for the file as it
    /// is now): a read of a few kilobytes, with no tool run. True when it is ready (now, or already was).
    /// </summary>
    public static bool Recall(string path)
    {
        if (KnownFacts(path) is not null)
            return true;
        if (PreviewCache.Timeline.ReadBytes(path, SeekKind, ".bin") is not { } bytes || Decode(bytes) is not { } kept || Key(path) is not { } key)
            return false;

        var (containerStart, videoStart, duration, times) = kept;
        var file = FileState.Get(path);
        VideoStarts[key] = videoStart;
        ContainerStarts[key] = containerStart;
        Durations[key] = duration;
        IFrames[key] = (times, file.Length, file.WrittenUtc.Ticks);
        SeekKept[key] = true;
        return true;
    }

    /// <summary>Writes a file's facts into the Timeline cache once they are all known (once per file and session, off the caller's thread).</summary>
    private static void KeepSeekFacts(string path)
    {
        if (KnownFacts(path) is not { } facts || !SeekKept.TryAdd(facts.Path, true))
            return;

        _ = Task.Run(() =>
        {
            if (PreviewCache.Timeline.Find(path, SeekKind, ".bin") is null)
                PreviewCache.Timeline.WriteBytes(path, SeekKind, ".bin", Encode(facts));
        });
    }

    // seekindex.bin: "HPSI", the version, the container's start, the picture's start, the length (doubles), the number of
    // I-frames, then their times (doubles); little-endian.
    private static byte[] Encode(SeekFacts facts)
    {
        var bytes = new byte[4 + 4 + 3 * 8 + 4 + facts.IFrames.Length * 8];
        var span = bytes.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span, SeekMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[4..], SeekVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(span[8..], facts.ContainerStart);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(span[16..], facts.VideoStart);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(span[24..], facts.Duration);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[32..], facts.IFrames.Length);
        for (var i = 0; i < facts.IFrames.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(span[(36 + i * 8)..], facts.IFrames[i]);
        return bytes;
    }

    private static (double ContainerStart, double VideoStart, double Duration, double[] IFrames)? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 36 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) != SeekMagic
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != SeekVersion)
            return null;

        var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        if (count <= 0 || bytes.Length != 36 + count * 8L)
            return null;

        var times = new double[count];
        for (var i = 0; i < count; i++)
            times[i] = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes[(36 + i * 8)..]);
        return (System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes[8..]), System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes[16..]),
            System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes[24..]), times);
    }

    /// <summary>A file's I-frame times written as the project file carries them: Base64 of the doubles, little-endian.</summary>
    public static string PackTimes(double[] times)
    {
        var bytes = new byte[times.Length * 8];
        for (var i = 0; i < times.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), times[i]);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The I-frame times a project file carries; empty when they cannot be read.</summary>
    public static double[] UnpackTimes(string packed)
    {
        try
        {
            var bytes = Convert.FromBase64String(packed);
            if (bytes.Length % 8 != 0)
                return [];
            var times = new double[bytes.Length / 8];
            for (var i = 0; i < times.Length; i++)
                times[i] = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i * 8));
            return times;
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string? Key(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // A large file is also touched at points spread across it: how large, how many points, how much at each.
    private const long SpreadAbove = 200L * 1024 * 1024;
    private const int SpreadPoints = 16, SpreadBytes = 16 * 1024;

    /// <summary>
    /// Reads the start and the end of a file (2 MB of each) into the system's file cache, where containers keep their
    /// seek tables (an MP4's moov, a Matroska file's Cues): the first seek a graph makes in the file then finds them
    /// in memory instead of waiting on the disk. A file over 200 MB is also read 16 KB at a time at 16 points spread
    /// evenly across it, so the drive and the file cache have pages from all along the timeline at hand. Nothing is
    /// kept; a file that cannot be read is left alone.
    /// </summary>
    public static void WarmSeekTables(string path, CancellationToken cancellationToken)
    {
        const int Reach = 2 * 1024 * 1024;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                void Read(long from, long count)
                {
                    file.Position = from;
                    for (var left = count; left > 0 && !cancellationToken.IsCancellationRequested;)
                    {
                        var got = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (got <= 0)
                            break;
                        left -= got;
                    }
                }

                Read(0, Math.Min(Reach, file.Length));
                if (file.Length > Reach)
                    Read(Math.Max(file.Length - Reach, Reach), Math.Min(Reach, file.Length - Reach));

                // Positioned reads (they move nothing shared), each its own small request.
                if (file.Length > SpreadAbove)
                {
                    for (var point = 1; point <= SpreadPoints && !cancellationToken.IsCancellationRequested; point++)
                        RandomAccess.Read(file.SafeFileHandle, buffer.AsSpan(0, SpreadBytes), file.Length / (SpreadPoints + 1) * point);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static MediaInfo Parse(JsonElement root)
    {
        VideoStreamInfo? video = null;
        var videoStart = 0.0;
        var streamSeconds = 0.0;
        var audio = new List<AudioStreamInfo>();
        var subtitles = new List<SubtitleStreamInfo>();

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                streamSeconds = Math.Max(streamSeconds, StreamSeconds(stream));
                var codec = Text(stream, "codec_name");
                switch (Text(stream, "codec_type"))
                {
                    case "video" when video is null:
                        var width = (int)Number(stream, "width");
                        var height = (int)Number(stream, "height");
                        video = new VideoStreamInfo(
                            width, height, codec,
                            Ratio(Text(stream, "avg_frame_rate")) is > 0 and var average ? average : Ratio(Text(stream, "r_frame_rate")),
                            Text(stream, "display_aspect_ratio") is { Length: > 0 } dar ? dar : Reduce(width, height),
                            Text(stream, "sample_aspect_ratio") is { Length: > 0 } sar ? sar : "1:1",
                            Text(stream, "color_transfer"),
                            Text(stream, "color_primaries"),
                            (long)Number(stream, "bit_rate"))
                        {
                            PixelFormat = Text(stream, "pix_fmt"),
                        };
                        videoStart = Number(stream, "start_time");
                        break;

                    case "audio":
                        audio.Add(new AudioStreamInfo(
                            audio.Count, codec,
                            (int)Number(stream, "channels"),
                            Text(stream, "channel_layout"),
                            (int)Number(stream, "sample_rate"),
                            (long)Number(stream, "bit_rate"),
                            Tag(stream, "title"), Tag(stream, "language"), Tag(stream, "handler_name")));
                        break;

                    case "subtitle":
                        subtitles.Add(new SubtitleStreamInfo(subtitles.Count, codec, Tag(stream, "title"), Tag(stream, "language")));
                        break;
                }
            }
        }

        var hasFormat = root.TryGetProperty("format", out var format);
        var containerStart = hasFormat ? Number(format, "start_time") : 0;
        if (!double.IsFinite(containerStart))
            containerStart = 0;

        // Chapters count from the container's clock: from the file's start here.
        var chapters = new List<MediaChapter>();
        if (root.TryGetProperty("chapters", out var chapterList) && chapterList.ValueKind == JsonValueKind.Array)
        {
            foreach (var chapter in chapterList.EnumerateArray())
            {
                var (start, end) = (Number(chapter, "start_time") - containerStart, Number(chapter, "end_time") - containerStart);
                if (double.IsFinite(start) && double.IsFinite(end) && end > start)
                    chapters.Add(new MediaChapter(Math.Max(start, 0), end, Tag(chapter, "title").Trim()));
            }
        }

        // A container that gives no length (some Matroska and MPEG-TS files): its longest stream's.
        var duration = hasFormat ? Number(format, "duration") : 0;
        if (!(duration > 0) || !double.IsFinite(duration))
            duration = streamSeconds;

        return new MediaInfo(
            hasFormat ? Text(format, "format_long_name") : "",
            duration,
            hasFormat ? (long)Number(format, "bit_rate") : 0,
            video, audio, subtitles)
        {
            VideoStartSeconds = double.IsFinite(videoStart) ? videoStart : 0,
            ContainerStartSeconds = containerStart,
            Chapters = chapters,
        };
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // ffprobe writes some numbers as JSON numbers and others (duration, bit_rate, sample_rate) as strings.
    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static string Tag(JsonElement stream, string name) =>
        stream.TryGetProperty("tags", out var tags) ? Text(tags, name) : "";

    // A stream's length: its duration, or Matroska's DURATION tag ("00:04:04.223000000"); 0 when it gives neither.
    private static double StreamSeconds(JsonElement stream)
    {
        if (Number(stream, "duration") is > 0 and var seconds && double.IsFinite(seconds))
            return seconds;
        var tag = Tag(stream, "DURATION");
        if (tag.Length == 0)
            return 0;

        // TimeSpan takes at most seven decimals.
        var dot = tag.IndexOf('.');
        if (dot >= 0 && tag.Length - dot - 1 > 7)
            tag = tag[..(dot + 8)];
        return TimeSpan.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var span) ? span.TotalSeconds : 0;
    }

    /// <summary>Evaluates a rate such as "30000/1001".</summary>
    private static double Ratio(string text)
    {
        var parts = text.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
        {
            return numerator / denominator;
        }

        return 0;
    }

    private static string Reduce(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return "";

        int a = width, b = height;
        while (b != 0)
            (a, b) = (b, a % b);
        return $"{width / a}:{height / a}";
    }
}
