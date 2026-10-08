using System.Globalization;

namespace HandPegApp.Services;

/// <summary>
/// The Video Combinator's command: two videos, each scaled and padded to one frame size and brought to one
/// frame rate and sound format, then joined end to end with a hard splice, a crossfade or a dip to black.
/// </summary>
public static class VideoCombinator
{
    public const string MatchA = "Match Video A";
    public const string MatchB = "Match Video B";

    public static IReadOnlyList<string> Resolutions { get; } = [MatchA, MatchB, "1080p (1920x1080)", "1440p (2560x1440)", "4K (3840x2160)"];

    public const string HardSplice = "Hard Splice";
    public const string Crossfade = "Crossfade (1s)";
    public const string DipToBlack = "Dip to Black (1s)";

    public static IReadOnlyList<string> Transitions { get; } = [HardSplice, Crossfade, DipToBlack];

    /// <summary>How long a crossfade or a dip to black lasts. Both videos have to be longer than this.</summary>
    public const double TransitionSeconds = 1;

    /// <summary>The frame both videos are brought to, with even sides as encoders need.</summary>
    public static (int Width, int Height) GetFrameSize(string resolution, MediaInfo a, MediaInfo b)
    {
        var (width, height) = resolution switch
        {
            MatchA => (a.Video?.Width ?? 1920, a.Video?.Height ?? 1080),
            MatchB => (b.Video?.Width ?? 1920, b.Video?.Height ?? 1080),
            _ when resolution.StartsWith("1440p", StringComparison.Ordinal) => (2560, 1440),
            _ when resolution.StartsWith("4K", StringComparison.Ordinal) => (3840, 2160),
            _ => (1920, 1080),
        };
        return (Math.Max(width / 2 * 2, 2), Math.Max(height / 2 * 2, 2));
    }

    /// <summary>Length of the joined video in seconds: a transition overlaps the two by its own length.</summary>
    public static double GetOutputSeconds(MediaInfo a, MediaInfo b, string transition) =>
        a.DurationSeconds + b.DurationSeconds - (UsesTransition(a, b, transition) ? TransitionSeconds : 0);

    /// <summary>A transition needs something of each video to fade over; with a video too short for that, the two are spliced.</summary>
    public static bool UsesTransition(MediaInfo a, MediaInfo b, string transition) =>
        transition != HardSplice && a.DurationSeconds > TransitionSeconds + 0.1 && b.DurationSeconds > TransitionSeconds + 0.1;

    /// <summary>
    /// The FFmpeg command line. Each input goes through the same steps, so that the two halves agree in size,
    /// pixel shape, frame rate, time base and sound format: the splice (concat) and the transition (xfade,
    /// acrossfade) all refuse inputs that differ. A video without sound gets silence of its own length.
    /// </summary>
    public static string BuildCommand(string pathA, string pathB, MediaInfo a, MediaInfo b, string resolution, string transition, string outputPath)
    {
        var (width, height) = GetFrameSize(resolution, a, b);
        var rate = Number(a.Video?.FrameRate is > 0 and var fps ? Math.Min(fps, 120) : 30);
        var graph = new List<string>();

        void Prepare(int input, MediaInfo info)
        {
            graph.Add($"[{input}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,"
                      + $"setsar=1,fps={rate},format=yuv420p,settb=AVTB[v{input}]");
            graph.Add(info.Audio.Count > 0
                ? $"[{input}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{input}]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={Number(Math.Max(info.DurationSeconds, 0.1))}[a{input}]");
        }

        Prepare(0, a);
        Prepare(1, b);

        if (UsesTransition(a, b, transition))
        {
            // The second video starts coming in one transition's length before the first one ends.
            var effect = transition == DipToBlack ? "fadeblack" : "fade";
            var offset = Number(Math.Max(a.DurationSeconds - TransitionSeconds, 0));
            graph.Add($"[v0][v1]xfade=transition={effect}:duration={Number(TransitionSeconds)}:offset={offset}[v]");
            graph.Add($"[a0][a1]acrossfade=d={Number(TransitionSeconds)}[a]");
        }
        else
        {
            graph.Add("[v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]");
        }

        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? $"\"{DependencyUpdater.FfmpegPath}\"" : "ffmpeg";
        return $"{ffmpeg} -hide_banner -y -i \"{pathA}\" -i \"{pathB}\" -filter_complex \"{string.Join(";", graph)}\" -map \"[v]\" -map \"[a]\" "
               + $"-c:v libx264 -preset fast -crf 18 -c:a aac -b:a 192k -movflags +faststart \"{outputPath}\"";
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
