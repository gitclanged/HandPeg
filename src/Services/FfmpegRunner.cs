using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using HandPegApp.Native;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp.Services;

/// <summary>
/// FFmpeg command that cannot be started as written (e.g. too long for Windows); encoder fallback is skipped since it would fail identically.
/// </summary>
public sealed class FfmpegCommandException(string message) : InvalidOperationException(message);

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
    /// Runs an FFmpeg command line verbatim. A bare "ffmpeg" resolves to the deps folder copy, else PATH. Arguments pass through
    /// unchanged (hand-typed filter graphs stay on the command line).
    /// </summary>
    public static async Task RunAsync(string commandLine, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        var match = CommandLineRegex().Match(commandLine.ReplaceLineEndings(" ").Trim());
        var executable = ResolveExecutable(match.Groups[1].Value);
        await RunArgumentsAsync(executable, match.Groups[2].Value, progress, cancellationToken);
    }

    /// <summary>
    /// Resolves the executable a command may run: bare <c>ffmpeg</c>/<c>ffmpeg.exe</c> (deps copy, else PATH) or exactly
    /// <see cref="DependencyUpdater.FfmpegPath"/>. Anything else is refused: commands can come from project files or shared bundles, so
    /// an embedded path (network share, bundle folder, batch file) must never choose the program.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command names another program.</exception>
    public static string ResolveExecutable(string token)
    {
        var executable = token.Trim().Trim('"');
        var configured = DependencyUpdater.FfmpegPath;
        if (configured.Length > 0 && executable.Equals(configured, StringComparison.OrdinalIgnoreCase))
            return configured;
        if (executable.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) || executable.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            return File.Exists(configured) ? configured : executable;
        throw new InvalidOperationException("The command has to start with ffmpeg; another FFmpeg can be set in Settings.");
    }

    private static async Task RunArgumentsAsync(string executable, string argumentText, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        var arguments = SplitArguments(argumentText);
        var command = Cli.Wrap(executable).WithArguments(arguments);

        // CliWrap quotes and FFmpeg re-splits; verify the round trip is exact (golden-tested too) since a mangled argument silently
        // corrupts the export.
        if (!SplitArguments(command.Arguments).SequenceEqual(arguments, StringComparer.Ordinal))
        {
            AppLog.Write("The FFmpeg command's arguments did not survive quoting, so the command line is passed on exactly as written.");
            command = Cli.Wrap(executable).WithArguments(argumentText);
        }

        // Windows command-line limit (including the null): report it clearly instead of "The filename or extension is too long".
        var length = executable.Length + 3 + command.Arguments.Length;
        if (length > MaxCommandLineLength)
        {
            throw new FfmpegCommandException(string.Create(CultureInfo.InvariantCulture,
                $"The FFmpeg command is {length:N0} characters long, and Windows starts no program from more than {MaxCommandLineLength:N0}."));
        }

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

        var result = await ProcessPipes.RunAsync(command
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

    // ----- Command line -----
    // Arguments are split exactly as FFmpeg splits them (CommandLineToArgvW) to verify what it receives.

    /// <summary>Windows maximum command-line length in characters (32,767 including the null).</summary>
    public const int MaxCommandLineLength = 32766;

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CommandLineToArgv(string commandLine, out int count);

    /// <summary>
    /// Splits a command line via CommandLineToArgvW, matching FFmpeg's Windows parsing of quotes and backslashes.
    /// </summary>
    public static List<string> SplitArguments(string arguments)
    {
        // The first word is taken for the program's name, which is split by rules of its own: a stand-in goes there.
        var pointer = CommandLineToArgv("ffmpeg " + arguments, out var count);
        if (pointer == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();

        try
        {
            var split = new List<string>(Math.Max(count - 1, 0));
            for (var i = 1; i < count; i++)
                split.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "");
            return split;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    /// <summary>Thumbnails per row and per column of a hover-preview sheet.</summary>
    public const int SpriteGridSize = 10;

    /// <summary>Width of one hover thumbnail in pixels. Its height follows the video's shape.</summary>
    public const int SpriteThumbnailWidth = 160;

    /// <summary>
    /// Writes a thumbnail grid sampled every <paramref name="intervalSeconds"/> s, decoding only the nearest I-frames (fast for long
    /// videos). Background work (WorkScheduler) on the background adapter, CPU fallback.
    /// </summary>
    /// <remarks>
    /// Like the posters and strips below, launched from the thread pool (FFmpeg startup blocked the UI thread for tens of ms).
    /// </remarks>
    public static Task<bool> GenerateSpriteSheetAsync(string videoPath, string imagePath, double intervalSeconds, CancellationToken cancellationToken) =>
        Task.Run(() => WorkScheduler.Run(new WorkSpec($"Hover previews of {Path.GetFileName(videoPath)}", WorkLane.Background) { Key = $"sprite|{videoPath}|{intervalSeconds}" }, async work =>
        {
            if (!File.Exists(DependencyUpdater.FfmpegPath))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            var filter = string.Create(CultureInfo.InvariantCulture,
                $"fps=1/{intervalSeconds:0.###},scale={SpriteThumbnailWidth}:-1,tile={SpriteGridSize}x{SpriteGridSize}");

            foreach (var hardware in GpuAdapters.BackgroundDecodeArguments.Count > 0 ? new[] { true, false } : [false])
            {
                if (hardware)
                    await WorkScheduler.GpuDecodeGate.WaitAsync(work.Token);
                try
                {
                    var result = await ProcessPipes.RunGatedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                        .WithArguments(["-hide_banner", "-loglevel", "error", "-y", .. GpuAdapters.BackgroundThreadArguments,
                            .. hardware ? GpuAdapters.BackgroundDecodeArguments : [], "-skip_frame", "nokey", "-i", videoPath,
                            "-an", "-sn", "-vf", filter, "-frames:v", "1", "-q:v", "4", imagePath])
                        .WithValidation(CommandResultValidation.None),
                        work.Token);

                    if (result.ExitCode == 0 && File.Exists(imagePath))
                        return true;
                }
                finally
                {
                    if (hardware)
                        WorkScheduler.GpuDecodeGate.Release();
                }
            }

            return false;
        }, cancellationToken));

    /// <summary>
    /// Single-frame JPEG poster <paramref name="height"/> px tall for a Media Browser tile (before its strip). Tries the GPU thumbnail
    /// compositor (nearest key frame, GpuFilmstrips), then in-process Media Foundation (Compositor.MfPoster), then FFmpeg.
    /// </summary>
    public static Task<bool> GeneratePosterFrameAsync(string videoPath, string imagePath, double atSeconds, int height, CancellationToken cancellationToken,
        WorkLane lane = WorkLane.Interactive) =>
        Task.Run(() => WorkScheduler.Run(new WorkSpec($"Poster of {Path.GetFileName(videoPath)}", lane) { Key = $"poster|{videoPath}|{height}|{imagePath}" }, async work =>
        {
            if (await GpuFilmstrips.TryStillAsync(videoPath, imagePath, atSeconds, height, onAKeyFrame: true, work.Token))
                return true;
            if (await Compositor.MfPoster.TryWriteAsync(videoPath, imagePath, atSeconds, height, work.Token))
                return true;

            if (!File.Exists(DependencyUpdater.FfmpegPath))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            var result = await ProcessPipes.RunGatedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                .WithArguments(["-hide_banner", "-loglevel", "error", "-y", .. GpuAdapters.BackgroundThreadArguments,
                    "-ss", Math.Max(atSeconds, 0).ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
                    "-an", "-sn", "-frames:v", "1", "-vf", string.Create(CultureInfo.InvariantCulture, $"scale=-2:{height}"), "-q:v", "4", imagePath])
                .WithValidation(CommandResultValidation.None),
                work.Token);

            return result.ExitCode == 0 && File.Exists(imagePath) && new FileInfo(imagePath).Length > 0;
        }, cancellationToken));

    /// <summary>
    /// Horizontal filmstrip of evenly spaced frames for Timeline blocks, the Sequence Overview Scrubber and the Media Browser. Rendered by
    /// the GPU compositor (<see cref="GpuFilmstrips"/>, identical output); FFmpeg when Background GPU is Processor Only or the GPU path
    /// fails, retrying on CPU if hardware decode fails (unsupported format, too many decoders). Background work (Media Browser: Idle).
    /// </summary>
    /// <param name="height">Frame height in px (54 for Timeline blocks).</param>
    public static Task<bool> GenerateFilmstripAsync(string videoPath, string imagePath, int count, double durationSeconds, CancellationToken cancellationToken,
        int height = 54, WorkLane lane = WorkLane.Background) =>
        Task.Run(() => WorkScheduler.Run(new WorkSpec($"Thumbnails of {Path.GetFileName(videoPath)}", lane) { Key = $"strip|{WorkScheduler.PathKey(videoPath)}|{count}|{height}|{imagePath}" }, async work =>
        {
            if (await GpuFilmstrips.TryMakeAsync(videoPath, imagePath, count, durationSeconds, height, work.Token))
                return true;
            if (!File.Exists(DependencyUpdater.FfmpegPath))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            foreach (var hardware in GpuAdapters.BackgroundDecodeArguments.Count > 0 ? new[] { true, false } : [false])
            {
                // A few decoding sessions at a time on the card, whatever asked for them (see WorkScheduler.GpuDecodeGate).
                if (hardware)
                    await WorkScheduler.GpuDecodeGate.WaitAsync(work.Token);
                try
                {
                    var problem = await RunFilmstripAsync(videoPath, imagePath, count, durationSeconds, hardware, height, work.Token);
                    if (problem is null)
                        return true;

                    AppLog.Write($"Thumbnails of {Path.GetFileName(videoPath)} could not be made{(hardware ? " with hardware decoding, and are tried again without" : "")}: {problem}");
                }
                finally
                {
                    if (hardware)
                        WorkScheduler.GpuDecodeGate.Release();
                }
            }

            return false;
        }, cancellationToken));

    /// <summary>Makes the strip once. Null when it was written; otherwise what FFmpeg said.</summary>
    private static async Task<string?> RunFilmstripAsync(string videoPath, string imagePath, int count, double durationSeconds, bool hardware, int height, CancellationToken cancellationToken)
    {
        // The frames' number, height and moments are GpuFilmstrips' (the compositor's strip is this one, frame for frame).
        count = GpuFilmstrips.FrameCount(count, durationSeconds);
        height = GpuFilmstrips.FrameHeight(height);

        // One input per frame, pre-seeked so only that frame decodes (vs. reading the whole file). The last sample stays clear of the
        // end: container duration often exceeds the video's, and an input seeked past the last frame yields nothing, losing the strip.
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var at in GpuFilmstrips.Moments(count, durationSeconds))
        {
            if (hardware)
                arguments.AddRange(GpuAdapters.BackgroundDecodeArguments);

            // One decoder per frame, so keep each light: one thread and the first frame after the seek (the landing I-frame) rather
            // than the exact time; thumbnails do not need frame accuracy.
            arguments.AddRange(["-threads", "1", "-noaccurate_seek", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath]);
        }

        // Every frame the same height and the same pixel format, which is what setting them side by side asks.
        var graph = new StringBuilder();
        for (var i = 0; i < count; i++)
            graph.Append(CultureInfo.InvariantCulture, $"[{i}:v:0]scale=-2:{height},setsar=1,format=yuv420p[f{i}];");
        for (var i = 0; i < count; i++)
            graph.Append(CultureInfo.InvariantCulture, $"[f{i}]");
        graph.Append(count > 1 ? string.Create(CultureInfo.InvariantCulture, $"hstack=inputs={count}") : "null");
        arguments.AddRange(["-filter_complex", graph.ToString(), "-an", "-sn", "-frames:v", "1", "-update", "1", "-q:v", "4", imagePath]);

        var said = new StringBuilder();
        var result = await ProcessPipes.RunGatedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath).WithArguments(arguments)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(said)).WithValidation(CommandResultValidation.None), cancellationToken);
        if (result.ExitCode == 0 && File.Exists(imagePath) && new FileInfo(imagePath).Length > 0)
            return null;

        var reason = said.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return reason ?? $"exit code {result.ExitCode}, and no picture was written";
    }

    /// <summary>
    /// Timestamps, in seconds and ascending, of every I-frame in the first video stream.
    /// Empty when ffprobe is unavailable or the file has no video.
    /// </summary>
    public static async Task<List<double>> GetIFramesAsync(string path, CancellationToken cancellationToken)
    {
        // Known already for this file as it is (listed this session, or taken up with a project or from seekindex.bin).
        if (MediaProbe.KnownIFrames(path) is { } known)
            return [.. known];

        // Parse the container index directly (MP4 stss, MKV Cues): no ffprobe, no decoding.
        if (await Task.Run(() => NativeKeyFrames.Read(path), cancellationToken) is { Length: > 0 } native)
        {
            MediaProbe.NoteIFrames(path, native);
            return [.. native];
        }

        if (!File.Exists(DependencyUpdater.FfprobePath))
            return [];

        // Listed before, for this file as it is: a long recording takes minutes to go through.
        if (PreviewCache.Timeline.ReadBytes(path, "iframes", ".bin") is { Length: > 0 } kept && kept.Length % sizeof(double) == 0)
        {
            List<double> listed = [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(kept)];
            MediaProbe.NoteIFrames(path, listed);
            return listed;
        }

        // Fallback: packet keyframe flags (K) via ffprobe, no decoding; same result much faster (4.5 h 1440p in ~30 s, disk-bound),
        // streamed line by line. Background work (WorkScheduler) deduplicates concurrent requests.
        return await WorkScheduler.Run(new WorkSpec($"I-frames of {Path.GetFileName(path)}", WorkLane.Background) { Key = $"iframes|{WorkScheduler.PathKey(path)}" }, async work =>
        {
            var iFrames = new List<double>();
            var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfprobePath)
                .WithArguments(["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", "-i", path])
                .WithStandardOutputPipe(ProcessPipes.Lines(line =>
                {
                    var comma = line.IndexOf(',');
                    if (comma > 0 && comma + 1 < line.Length && line[comma + 1] == 'K'
                        && double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    {
                        iFrames.Add(seconds);
                    }
                }))
                .WithValidation(CommandResultValidation.None), work.Token);

            iFrames.Sort();
            if (result.ExitCode == 0 && iFrames.Count > 0)
            {
                PreviewCache.Timeline.WriteBytes(path, "iframes", ".bin", System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(iFrames)));
                MediaProbe.NoteIFrames(path, iFrames);
            }

            return iFrames;
        }, cancellationToken);
    }

    private static TimeSpan? ParseTime(Match match)
    {
        if (!match.Success)
            return null;

        return new TimeSpan(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), 0)
               + TimeSpan.FromSeconds(double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }
}
