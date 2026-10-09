using System.Globalization;
using System.IO;
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

    /// <summary>Thumbnails per row and per column of a hover-preview sheet.</summary>
    public const int SpriteGridSize = 10;

    /// <summary>Width of one hover thumbnail in pixels. Its height follows the video's shape.</summary>
    public const int SpriteThumbnailWidth = 160;

    /// <summary>
    /// Writes one image holding a grid of thumbnails taken every <paramref name="intervalSeconds"/> seconds.
    /// Only keyframes are decoded, which makes this quick even for long videos; each thumbnail is the
    /// keyframe nearest its time.
    /// </summary>
    public static async Task<bool> GenerateSpriteSheetAsync(
        string videoPath, string imagePath, double intervalSeconds, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"fps=1/{intervalSeconds:0.###},scale={SpriteThumbnailWidth}:-1,tile={SpriteGridSize}x{SpriteGridSize}");

        var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-loglevel", "error", "-y", "-skip_frame", "nokey", "-i", videoPath,
                "-an", "-sn", "-vf", filter, "-frames:v", "1", "-q:v", "4", imagePath])
            .WithValidation(CommandResultValidation.None),
            cancellationToken);

        return result.ExitCode == 0 && File.Exists(imagePath);
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
        var result = await Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-nostats", "-i", path, "-map", $"0:a:{Math.Max(trackIndex, 0)}", "-af", filter, "-f", "null", "-"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
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
    /// Timestamps, in seconds and ascending, of every keyframe in the first video stream.
    /// Empty when ffprobe is unavailable or the file has no video.
    /// </summary>
    public static async Task<List<double>> GetKeyframesAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfprobePath))
            return [];

        var result = await Cli.Wrap(DependencyUpdater.FfprobePath)
            .WithArguments(["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time",
                "-of", "csv=p=0", "-skip_frame", "nokey", "-i", path])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        var keyframes = new List<double>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Frames with side data get extra columns after the timestamp.
            if (double.TryParse(line.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                keyframes.Add(seconds);
        }

        keyframes.Sort();
        return keyframes;
    }

    private static TimeSpan? ParseTime(Match match)
    {
        if (!match.Success)
            return null;

        return new TimeSpan(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), 0)
               + TimeSpan.FromSeconds(double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }
}
