using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

/// <summary>
/// An FFmpeg command that could not be started as it was written (too long for Windows, say): the same with another
/// encoder would fail the same way, so the hardware fallback leaves it alone.
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
    /// Runs an FFmpeg command line exactly as written. A bare "ffmpeg" resolves to the
    /// local copy in the deps folder when there is one, otherwise to whatever is on PATH.
    /// FFmpeg is given the very arguments the line spells out (5.2-Arm64AndNpu: HandPeg writes no filter graph for an export,
    /// so none is handed over in a file any more; a graph typed into a command by hand reaches FFmpeg on its command line).
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

        await RunArgumentsAsync(executable, match.Groups[2].Value, progress, cancellationToken);
    }

    private static async Task RunArgumentsAsync(string executable, string argumentText, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        var arguments = SplitArguments(argumentText);
        var command = Cli.Wrap(executable).WithArguments(arguments);

        // CliWrap quotes each argument for the command line, and FFmpeg splits that line again: what it gets back
        // has to be what was meant, to the character. Proven by the golden tests; checked here as well, because an
        // argument that arrived changed would be an export that is quietly wrong.
        if (!SplitArguments(command.Arguments).SequenceEqual(arguments, StringComparer.Ordinal))
        {
            AppLog.Write("The FFmpeg command's arguments did not survive quoting, so the command line is passed on exactly as written.");
            command = Cli.Wrap(executable).WithArguments(argumentText);
        }

        // Windows starts nothing from a command line longer than this (the terminating null counts): say so plainly,
        // rather than as the system's "The filename or extension is too long".
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

    // ----- The command line -----
    // The arguments are split exactly as FFmpeg splits them (CommandLineToArgvW), so that what it is handed is checked to be
    // what was meant. (5.2-Arm64AndNpu: the filter graphs HandPeg once wrote, and handed over in files when they grew past what
    // Windows starts a program with, are gone with the graph builder; so is asking each FFmpeg how it takes a graph's file.)

    /// <summary>The longest command line Windows starts a program from, in characters (32,767 with its terminating null).</summary>
    public const int MaxCommandLineLength = 32766;

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CommandLineToArgv(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// The arguments a command line gives a program, split exactly as FFmpeg splits its own: its Windows build reads
    /// them with CommandLineToArgvW, as this does, so quotes and the backslashes before them mean here what they mean to it.
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
    /// Writes one image holding a grid of thumbnails taken every <paramref name="intervalSeconds"/> seconds.
    /// Only I-frames are decoded, which makes this quick even for long videos; each thumbnail is the
    /// I-frame nearest its time. Background work (see WorkScheduler), decoded on the background card where there
    /// is one; on the processor when that does not work out.
    /// </summary>
    /// <remarks>
    /// Like the posters and strips below, asked for from the thread pool whoever asks (4.3E): work asked for on the
    /// interface's thread is started there, and FFmpeg's start took it for tens of milliseconds.
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
    /// One frame of a video as a JPEG <paramref name="height"/> pixels tall: a media browser tile's poster, before its strip
    /// is made (4.2D). The thumbnail compositor's (5.1-CleanupStudioAndFfmpegPurge: decoded on the card, the key frame nearest the
    /// moment, GpuFilmstrips); else Media Foundation's in this process (5.4.2A, Compositor.MfPoster: the same key frame, no tool
    /// started); FFmpeg only for what neither can decode.
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
    /// A strip of frames from a video, evenly spread over its length and set side by side in one picture:
    /// what a video's block on the timeline (and the Sequence Overview Scrubber) is drawn with, and the media browser's frames.
    /// A compositor draws it (5.0D, <see cref="GpuFilmstrips"/>: one decode per frame on the card, read back), the same
    /// picture frame for frame; with Background GPU set to Processor Only, and whenever that does not work out, FFmpeg
    /// makes it. The background graphics card decodes where it can; when that does not work out (a format the card
    /// does not take, a driver that will not open eight decoders at once) the strip is made again by the processor.
    /// Background work (the media browser's: Idle).
    /// </summary>
    /// <param name="height">How tall each frame is made, in pixels: 54 for a block on the timeline.</param>
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

        // One frame from each of as many moments, evenly spread: the file is opened once per frame, already
        // wound forward to it, so that only that frame is decoded. Reading a long video from end to end for
        // a dozen pictures is the difference between a moment and a minute. The last moment stays clear of the
        // end: a file's length is often a little more than its picture's, and an input wound past its last frame
        // has none to give, which loses the whole strip.
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var at in GpuFilmstrips.Moments(count, durationSeconds))
        {
            if (hardware)
                arguments.AddRange(GpuAdapters.BackgroundDecodeArguments);

            // Each input is a decoder of its own, and there are as many as there are frames in the strip. So
            // each is kept small: one thread, and the first frame it can give (the I-frame the seek lands on)
            // rather than decoding on from there to the exact moment. A thumbnail does not need the exact moment.
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

        // Read from the file's own index in C# (5.4.1-M4: an MP4's stss, an MKV's Cues), no ffprobe and nothing decoded.
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

        // Read from the packets' flags, with nothing decoded: the demuxer marks every packet that starts an I-frame
        // (K). The same list as decoding the I-frames gives, in a fraction of the time (a 4.5-hour 1440p recording
        // in about half a minute, bound by the disk), and read line by line as it comes, not held whole. Background
        // work (see WorkScheduler): asked for twice, it is read once.
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
