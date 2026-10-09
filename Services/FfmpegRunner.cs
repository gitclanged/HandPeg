using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

/// <param name="Line">The raw line FFmpeg printed.</param>
/// <param name="Position">Output timestamp reached so far, when the line is a progress line.</param>
/// <param name="InputDuration">Duration of the input, when the line announces it.</param>
public sealed record FfmpegProgress(string Line, TimeSpan? Position, TimeSpan? InputDuration);

public static partial class FfmpegRunner
{
    [GeneratedRegex(@"^\s*(""[^""]+""|\S+)\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex CommandLineRegex();

    [GeneratedRegex(@"\btime=(\d+):(\d\d):(\d\d(?:\.\d+)?)")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^\s*Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    /// <summary>
    /// Runs an FFmpeg command line exactly as written. A bare "ffmpeg" resolves to the
    /// local copy in the deps folder when there is one, otherwise to whatever is on PATH.
    /// </summary>
    public static async Task RunAsync(string commandLine, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        var match = CommandLineRegex().Match(commandLine.ReplaceLineEndings(" ").Trim());
        var executable = match.Groups[1].Value.Trim('"');
        var isConfiguredFfmpeg = DependencyUpdater.IsFfmpegOverridden
                                 && executable.Equals(DependencyUpdater.FfmpegPath, StringComparison.OrdinalIgnoreCase);
        if (!isConfiguredFfmpeg && !Path.GetFileNameWithoutExtension(executable).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The command has to start with ffmpeg.");

        var isBareName = Path.GetFileName(executable) == executable;
        if (isBareName && File.Exists(DependencyUpdater.FfmpegPath))
            executable = DependencyUpdater.FfmpegPath;

        var recentLines = new Queue<string>();
        void OnLine(string line)
        {
            // Keep the last few non-progress lines: they explain a failure.
            var position = ParseTime(TimeRegex().Match(line));
            if (position is null && !string.IsNullOrWhiteSpace(line))
            {
                recentLines.Enqueue(line.Trim());
                if (recentLines.Count > 40)
                    recentLines.Dequeue();
            }

            progress.Report(new FfmpegProgress(line.Trim(), position, ParseTime(DurationRegex().Match(line))));
        }

        var result = await ProcessPipes.RunAsync(Cli.Wrap(executable)
            .WithArguments(match.Groups[2].Value)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(ProcessPipes.Lines(OnLine))
            .WithStandardErrorPipe(ProcessPipes.Lines(OnLine)),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            // The lines that name the problem are often followed by end-of-run statistics, so look for them first.
            var problems = recentLines
                .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("could not", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("no such", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("not supported", StringComparison.OrdinalIgnoreCase))
                .TakeLast(3)
                .ToList();
            var detail = problems.Count > 0 ? string.Join(" | ", problems)
                : recentLines.Count > 0 ? string.Join(" | ", recentLines.TakeLast(3))
                : "no output";
            throw new InvalidOperationException($"FFmpeg exited with code {result.ExitCode}: {detail}");
        }
    }

    // How many FFmpeg may be making thumbnails at once, whatever asked for them: the hover-preview sheet of
    // the main video, and the strip of frames of every video on the timeline. Dropping ten videos at once
    // makes ten strips two at a time, not ten at a time with the processor and the memory that takes.
    private static readonly SemaphoreSlim ThumbnailGate = new(2, 2);

    /// <summary>Thumbnails per row and per column of a hover-preview sheet.</summary>
    public const int SpriteGridSize = 10;

    /// <summary>Width of one hover thumbnail in pixels. Its height follows the video's shape.</summary>
    public const int SpriteThumbnailWidth = 160;

    /// <summary>
    /// Writes one image holding a grid of thumbnails taken every <paramref name="intervalSeconds"/> seconds.
    /// Only I-frames are decoded, which makes this quick even for long videos; each thumbnail is the
    /// I-frame nearest its time.
    /// </summary>
    public static async Task<bool> GenerateSpriteSheetAsync(
        string videoPath, string imagePath, double intervalSeconds, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"fps=1/{intervalSeconds:0.###},scale={SpriteThumbnailWidth}:-1,tile={SpriteGridSize}x{SpriteGridSize}");

        await ThumbnailGate.WaitAsync(cancellationToken);
        try
        {
            var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                .WithArguments(["-hide_banner", "-loglevel", "error", "-y", "-skip_frame", "nokey", "-i", videoPath,
                    "-an", "-sn", "-vf", filter, "-frames:v", "1", "-q:v", "4", imagePath])
                .WithValidation(CommandResultValidation.None),
                cancellationToken);

            return result.ExitCode == 0 && File.Exists(imagePath);
        }
        finally
        {
            ThumbnailGate.Release();
        }
    }

    /// <summary>
    /// A strip of frames from a video, evenly spread over its length and set side by side in one picture:
    /// what a video's block on the timeline is drawn with. The graphics card decodes where it can; when that
    /// does not work out (a format the card does not take, a driver that will not open eight decoders at
    /// once) the strip is made again by the processor.
    /// </summary>
    public static async Task<bool> GenerateFilmstripAsync(string videoPath, string imagePath, int count, double durationSeconds, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);

        // Waits its turn: see ThumbnailGate.
        await ThumbnailGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var hardware in new[] { true, false })
            {
                var problem = await RunFilmstripAsync(videoPath, imagePath, count, durationSeconds, hardware, cancellationToken);
                if (problem is null)
                    return true;

                AppLog.Write($"Thumbnails of {Path.GetFileName(videoPath)} could not be made{(hardware ? " with hardware decoding, and are tried again without" : "")}: {problem}");
            }

            return false;
        }
        finally
        {
            ThumbnailGate.Release();
        }
    }

    /// <summary>Makes the strip once. Null when it was written; otherwise what FFmpeg said.</summary>
    private static async Task<string?> RunFilmstripAsync(string videoPath, string imagePath, int count, double durationSeconds, bool hardware, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 25);

        // One frame from each of as many moments, evenly spread: the file is opened once per frame, already
        // wound forward to it, so that only that frame is decoded. Reading a long video from end to end for
        // a dozen pictures is the difference between a moment and a minute.
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (durationSeconds <= 0.2)
            count = 1;

        // The last moment asked for stays clear of the end: a file's length is often a little more than its
        // picture's, and an input wound past its last frame has none to give, which loses the whole strip.
        var last = Math.Max(durationSeconds - 0.5, 0);
        for (var i = 0; i < count; i++)
        {
            var at = durationSeconds > 0.2 ? Math.Min(durationSeconds * (i + 0.5) / count, last) : 0;
            if (hardware)
                arguments.AddRange(["-hwaccel", "auto"]);

            // Each input is a decoder of its own, and there are as many as there are frames in the strip. So
            // each is kept small: one thread, and the first frame it can give (the I-frame the seek lands on)
            // rather than decoding on from there to the exact moment. A thumbnail does not need the exact moment.
            arguments.AddRange(["-threads", "1", "-noaccurate_seek", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath]);
        }

        // Every frame the same height and the same pixel format, which is what setting them side by side asks.
        var graph = new StringBuilder();
        for (var i = 0; i < count; i++)
            graph.Append(CultureInfo.InvariantCulture, $"[{i}:v:0]scale=-2:54,setsar=1,format=yuv420p[f{i}];");
        for (var i = 0; i < count; i++)
            graph.Append(CultureInfo.InvariantCulture, $"[f{i}]");
        graph.Append(count > 1 ? string.Create(CultureInfo.InvariantCulture, $"hstack=inputs={count}") : "null");
        arguments.AddRange(["-filter_complex", graph.ToString(), "-an", "-sn", "-frames:v", "1", "-update", "1", "-q:v", "4", imagePath]);

        var said = new StringBuilder();
        var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfmpegPath).WithArguments(arguments)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(said)).WithValidation(CommandResultValidation.None), cancellationToken);
        if (result.ExitCode == 0 && File.Exists(imagePath) && new FileInfo(imagePath).Length > 0)
            return null;

        var reason = said.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return reason ?? $"exit code {result.ExitCode}, and no picture was written";
    }

    [GeneratedRegex(@"silence_start: (-?\d+(?:\.\d+)?)")]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end: (-?\d+(?:\.\d+)?)")]
    private static partial Regex SilenceEndRegex();

    /// <summary>
    /// The silent stretches of the first audio track, found with the silencedetect filter.
    /// </summary>
    /// <param name="noiseDb">Anything quieter than this counts as silence.</param>
    /// <param name="minimumSeconds">Shorter pauses are ignored.</param>
    /// <param name="duration">Length of the file, to close a silence that runs to the very end.</param>
    /// <param name="trackIndex">Which of the file's audio tracks to listen to, counted from 0.</param>
    public static async Task<List<(double Start, double End)>> DetectSilenceAsync(
        string path, int trackIndex, double noiseDb, double minimumSeconds, double duration, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            throw new InvalidOperationException("FFmpeg is not installed. Install it from Settings first.");

        var filter = string.Create(CultureInfo.InvariantCulture, $"silencedetect=noise={noiseDb}dB:d={minimumSeconds}");
        var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-nostats", "-i", path, "-map", $"0:a:{Math.Max(trackIndex, 0)}", "-af", filter, "-f", "null", "-"])
            .WithValidation(CommandResultValidation.None), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg could not analyse the audio (exit code {result.ExitCode}).");

        // The filter reports on stderr: a start line, then later an end line.
        var silences = new List<(double Start, double End)>();
        double? start = null;
        foreach (var line in result.StandardError.Split('\n'))
        {
            if (SilenceStartRegex().Match(line) is { Success: true } startMatch)
            {
                start = Math.Max(double.Parse(startMatch.Groups[1].Value, CultureInfo.InvariantCulture), 0);
            }
            else if (start is { } from && SilenceEndRegex().Match(line) is { Success: true } endMatch)
            {
                silences.Add((from, double.Parse(endMatch.Groups[1].Value, CultureInfo.InvariantCulture)));
                start = null;
            }
        }

        if (start is { } open && duration > open)
            silences.Add((open, duration));

        return silences;
    }

    /// <summary>
    /// Timestamps, in seconds and ascending, of every I-frame in the first video stream.
    /// Empty when ffprobe is unavailable or the file has no video.
    /// </summary>
    public static async Task<List<double>> GetIFramesAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfprobePath))
            return [];

        var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfprobePath)
            .WithArguments(["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time",
                "-of", "csv=p=0", "-skip_frame", "nokey", "-i", path])
            .WithValidation(CommandResultValidation.None), cancellationToken);

        var iFrames = new List<double>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Frames with side data get extra columns after the timestamp.
            if (double.TryParse(line.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                iFrames.Add(seconds);
        }

        iFrames.Sort();
        return iFrames;
    }

    private static TimeSpan? ParseTime(Match match)
    {
        if (!match.Success)
            return null;

        return new TimeSpan(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), 0)
               + TimeSpan.FromSeconds(double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }
}
