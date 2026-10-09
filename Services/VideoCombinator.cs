using System.Globalization;

namespace HandPegApp.Services;

/// <summary>One of the videos the Video Combinator joins: the file, and what ffprobe said about it.</summary>
public sealed record CombinatorInput(string Path, MediaInfo Info);

/// <summary>
/// The Video Combinator's command: up to five videos, each scaled and padded to one frame size and brought to
/// one frame rate and sound format, then joined end to end with hard splices, crossfades or dips to black.
/// Every audio track is kept: the result has as many as the video with the most, and a video that has fewer
/// is given silence for the ones it lacks, so each track stays in step from the first video to the last.
/// </summary>
public static class VideoCombinator
{
    /// <summary>The most videos that can be joined at once.</summary>
    public const int MaxInputs = 5;

    public const string MatchFirst = "Match First Video";
    public const string MatchLargest = "Match Largest Video";

    public static IReadOnlyList<string> Resolutions { get; } = [MatchFirst, MatchLargest, "1080p (1920x1080)", "1440p (2560x1440)", "4K (3840x2160)"];

    public const string HardSplice = "Hard Splice";
    public const string Crossfade = "Crossfade (1s)";
    public const string DipToBlack = "Dip to Black (1s)";

    public static IReadOnlyList<string> Transitions { get; } = [HardSplice, Crossfade, DipToBlack];

    /// <summary>How long a crossfade or a dip to black lasts. Every video has to be longer than this.</summary>
    public const double TransitionSeconds = 1;

    /// <summary>The frame every video is brought to, with even sides as encoders need.</summary>
    public static (int Width, int Height) GetFrameSize(string resolution, IReadOnlyList<CombinatorInput> inputs)
    {
        var sizes = inputs.Where(i => i.Info.Video is not null).Select(i => (i.Info.Video!.Width, i.Info.Video.Height)).ToList();
        var (width, height) = resolution switch
        {
            _ when resolution.StartsWith("1080p", StringComparison.Ordinal) => (1920, 1080),
            _ when resolution.StartsWith("1440p", StringComparison.Ordinal) => (2560, 1440),
            _ when resolution.StartsWith("4K", StringComparison.Ordinal) => (3840, 2160),
            _ when sizes.Count == 0 => (1920, 1080),
            MatchLargest => sizes.MaxBy(s => (long)s.Width * s.Height),
            _ => sizes[0],
        };
        return (Math.Max(width / 2 * 2, 2), Math.Max(height / 2 * 2, 2));
    }

    /// <summary>How many audio tracks the result has: as many as the video with the most.</summary>
    public static int GetAudioTrackCount(IReadOnlyList<CombinatorInput> inputs) => inputs.Count == 0 ? 0 : inputs.Max(i => i.Info.Audio.Count);

    /// <summary>Length of the joined video in seconds: each transition overlaps two videos by its own length.</summary>
    public static double GetOutputSeconds(IReadOnlyList<CombinatorInput> inputs, string transition) =>
        inputs.Sum(i => i.Info.DurationSeconds) - (UsesTransition(inputs, transition) ? TransitionSeconds * (inputs.Count - 1) : 0);

    /// <summary>A transition needs something of each video to fade over; with a video too short for that, they are all spliced.</summary>
    public static bool UsesTransition(IReadOnlyList<CombinatorInput> inputs, string transition) =>
        transition != HardSplice && inputs.Count > 1 && inputs.All(i => i.Info.DurationSeconds > TransitionSeconds * 2 + 0.1);

    /// <summary>
    /// The FFmpeg command line. Each input goes through the same steps, so that all of them agree in size,
    /// pixel shape, frame rate, time base and sound format: the splice (concat) and the transitions (xfade,
    /// acrossfade) all refuse inputs that differ.
    /// </summary>
    public static string BuildCommand(IReadOnlyList<CombinatorInput> inputs, string resolution, string transition, string outputPath)
    {
        var (width, height) = GetFrameSize(resolution, inputs);
        var rate = Number(inputs[0].Info.Video?.FrameRate is > 0 and var fps ? Math.Min(fps, 120) : 30);
        var tracks = GetAudioTrackCount(inputs);
        var graph = new List<string>();

        for (var i = 0; i < inputs.Count; i++)
        {
            var info = inputs[i].Info;
            graph.Add($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,"
                      + $"setsar=1,fps={rate},format=yuv420p,settb=AVTB[v{i}]");

            // Track by track: the video's own where it has one, silence of the video's length where it has not.
            // A track is rarely exactly as long as its picture; it is padded or cut to the video's length, so
            // that a track that ends early does not pull everything after it forward.
            var length = Number(Math.Max(info.DurationSeconds, 0.1));
            for (var t = 0; t < tracks; t++)
            {
                graph.Add(t < info.Audio.Count
                    ? $"[{i}:a:{t}]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad=whole_dur={length},atrim=duration={length}[a{i}_{t}]"
                    : $"anullsrc=r=48000:cl=stereo,atrim=duration={length}[a{i}_{t}]");
            }
        }

        if (UsesTransition(inputs, transition))
        {
            // A chain: what has been joined so far, faded into the next video. Each video starts coming in
            // one transition's length before the end of everything before it.
            var effect = transition == DipToBlack ? "fadeblack" : "fade";
            var (current, reached) = ("[v0]", inputs[0].Info.DurationSeconds);
            for (var i = 1; i < inputs.Count; i++)
            {
                var joined = i == inputs.Count - 1 ? "[v]" : $"[vx{i}]";
                graph.Add($"{current}[v{i}]xfade=transition={effect}:duration={Number(TransitionSeconds)}:offset={Number(Math.Max(reached - TransitionSeconds, 0))}{joined}");
                (current, reached) = (joined, reached + inputs[i].Info.DurationSeconds - TransitionSeconds);
            }

            for (var t = 0; t < tracks; t++)
            {
                current = $"[a0_{t}]";
                for (var i = 1; i < inputs.Count; i++)
                {
                    var joined = i == inputs.Count - 1 ? $"[ao{t}]" : $"[ax{i}_{t}]";
                    graph.Add($"{current}[a{i}_{t}]acrossfade=d={Number(TransitionSeconds)}{joined}");
                    current = joined;
                }
            }
        }
        else
        {
            // One concat for everything, so the picture and every track are cut at the same moments.
            var pieces = string.Concat(Enumerable.Range(0, inputs.Count).Select(i => $"[v{i}]" + string.Concat(Enumerable.Range(0, tracks).Select(t => $"[a{i}_{t}]"))));
            graph.Add($"{pieces}concat=n={inputs.Count}:v=1:a={tracks}[v]{string.Concat(Enumerable.Range(0, tracks).Select(t => $"[ao{t}]"))}");
        }

        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? $"\"{DependencyUpdater.FfmpegPath}\"" : "ffmpeg";
        var args = new List<string> { ffmpeg, "-hide_banner", "-y" };
        args.AddRange(inputs.Select(i => $"-i \"{i.Path}\""));
        args.Add($"-filter_complex \"{string.Join(";", graph)}\"");
        args.Add("-map \"[v]\"");
        for (var t = 0; t < tracks; t++)
        {
            args.Add($"-map \"[ao{t}]\"");

            // A track keeps the name it has in the first video that names it.
            var title = inputs.Select(i => t < i.Info.Audio.Count ? i.Info.Audio[t].Title : "").FirstOrDefault(name => name.Length > 0);
            if (title is not null)
                args.Add($"-metadata:s:a:{t} title=\"{title.Replace('"', '\'')}\"");
        }

        args.Add("-c:v libx264 -preset fast -crf 18");
        if (tracks > 0)
            args.Add("-c:a aac -b:a 192k");
        args.Add($"-movflags +faststart \"{outputPath}\"");
        return string.Join(" ", args);
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
