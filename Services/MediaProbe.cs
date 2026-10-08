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
    IReadOnlyList<SubtitleStreamInfo> Subtitles);

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
}

/// <param name="Index">Position among the audio streams, as used in "0:a:N".</param>
public sealed record AudioStreamInfo(
    int Index, string Codec, int Channels, string ChannelLayout, int SampleRate, long BitRate, string Title, string Language);

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
    public static async Task<MediaInfo?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfprobePath))
            return null;

        var result = await Cli.Wrap(DependencyUpdater.FfprobePath)
            .WithArguments(["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        if (result.ExitCode != 0)
            return null;

        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            return Parse(json.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MediaInfo Parse(JsonElement root)
    {
        VideoStreamInfo? video = null;
        var audio = new List<AudioStreamInfo>();
        var subtitles = new List<SubtitleStreamInfo>();

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
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
                            (long)Number(stream, "bit_rate"));
                        break;

                    case "audio":
                        audio.Add(new AudioStreamInfo(
                            audio.Count, codec,
                            (int)Number(stream, "channels"),
                            Text(stream, "channel_layout"),
                            (int)Number(stream, "sample_rate"),
                            (long)Number(stream, "bit_rate"),
                            Tag(stream, "title"), Tag(stream, "language")));
                        break;

                    case "subtitle":
                        subtitles.Add(new SubtitleStreamInfo(subtitles.Count, codec, Tag(stream, "title"), Tag(stream, "language")));
                        break;
                }
            }
        }

        var hasFormat = root.TryGetProperty("format", out var format);
        return new MediaInfo(
            hasFormat ? Text(format, "format_long_name") : "",
            hasFormat ? Number(format, "duration") : 0,
            hasFormat ? (long)Number(format, "bit_rate") : 0,
            video, audio, subtitles);
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
