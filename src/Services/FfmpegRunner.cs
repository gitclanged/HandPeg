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
    /// FFmpeg is given the very arguments the line spells out, except that a filter graph may reach it in a file
    /// rather than on the command line (see <see cref="WithGraphFiles"/>): always when <paramref name="graphInFile"/>
    /// is set (exports, queue jobs, Render Preview), and otherwise when the graph is longer than
    /// <see cref="GraphFileThreshold"/>. The line itself, as the user sees and copies it, keeps its graphs inline.
    /// </summary>
    public static async Task RunAsync(string commandLine, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken, bool graphInFile = false)
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

        // The files the graphs are written to belong to this run, and go with it however it ends.
        var graphFiles = new List<string>();
        try
        {
            await RunArgumentsAsync(executable, match.Groups[2].Value, graphInFile, graphFiles, progress, cancellationToken);
        }
        finally
        {
            foreach (var file in graphFiles)
                DeleteQuietly(file);
        }
    }

    private static async Task RunArgumentsAsync(
        string executable, string argumentText, bool graphInFile, List<string> graphFiles, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        var original = SplitArguments(argumentText);
        var arguments = original;
        if (NeedsGraphFile(original, graphInFile))
        {
            var form = await GraphFileFormAsync(executable, cancellationToken);
            arguments = WithGraphFiles(original, graphInFile, form, graph =>
            {
                Directory.CreateDirectory(SessionPaths.Graphs);
                var path = Path.Combine(SessionPaths.Graphs, $"graph_{Guid.NewGuid():N}.txt");
                File.WriteAllText(path, graph, Utf8WithoutMark);
                graphFiles.Add(path);
                if (graph.Length > GraphFileThreshold)
                    AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"A filter graph of {graph.Length:N0} characters was given to FFmpeg in a file ({form})."));
                return path;
            });
        }

        var command = Cli.Wrap(executable).WithArguments(arguments);

        // CliWrap quotes each argument for the command line, and FFmpeg splits that line again: what it gets back
        // has to be what was meant, to the character. Proven by the golden tests; checked here as well, because a
        // graph that arrived changed would be an export that is quietly wrong.
        if (!SplitArguments(command.Arguments).SequenceEqual(arguments, StringComparer.Ordinal))
        {
            if (graphFiles.Count > 0)
                throw new FfmpegCommandException("The FFmpeg command could not be passed on unchanged, so it was not run.");
            AppLog.Write("The FFmpeg command's arguments did not survive quoting, so the command line is passed on exactly as written.");
            command = Cli.Wrap(executable).WithArguments(argumentText);
        }

        // Windows starts nothing from a command line longer than this (the terminating null counts): say so plainly,
        // rather than as the system's "The filename or extension is too long". What is left that long is the list of
        // inputs (some hundreds of files), which has no other way to FFmpeg.
        var length = executable.Length + 3 + command.Arguments.Length;
        if (length > MaxCommandLineLength)
        {
            throw new FfmpegCommandException(string.Create(CultureInfo.InvariantCulture,
                $"The FFmpeg command is {length:N0} characters long{(graphFiles.Count > 0 ? " even with its filter graph in a file" : "")}, and Windows starts no program from more than {MaxCommandLineLength:N0}."));
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

    // ----- Filter graphs in files -----
    // Windows starts a program with at most 32,767 characters of command line, and a timeline of many clips, cuts or
    // keyframes writes a graph longer than that. So the graph is written to a file in the session folder and FFmpeg is
    // pointed at it: "-/filter_complex file" (FFmpeg 7 and later, which load any option's value from a file that way;
    // FFmpeg 9 has dropped the older options), or "-filter_complex_script file" for an older FFmpeg of the user's own.
    // Which of the two an FFmpeg takes is asked of it once, by running it on a tiny graph. The arguments are split
    // exactly as FFmpeg splits them, so the file holds the very text the command line would have given it.

    /// <summary>A graph longer than this goes to FFmpeg in a file even for a run that does not always send its graphs so.</summary>
    public const int GraphFileThreshold = 4000;

    /// <summary>The longest command line Windows starts a program from, in characters (32,767 with its terminating null).</summary>
    public const int MaxCommandLineLength = 32766;

    private static readonly UTF8Encoding Utf8WithoutMark = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How an FFmpeg is pointed at a graph in a file.</summary>
    public enum GraphFileForm
    {
        /// <summary>Neither way works: the graph stays on the command line.</summary>
        None,

        /// <summary>"-/filter_complex file": FFmpeg 7 and later.</summary>
        LoadOption,

        /// <summary>"-filter_complex_script file", "-filter_script:v file": FFmpeg before 9.</summary>
        ScriptOption,
    }

    // The options whose value is a filter graph: the complex one, and the simple ones of the streams.
    [GeneratedRegex(@"^-(?:filter_complex|lavfi|vf|af|filter(?::\S+)?)$")]
    private static partial Regex GraphOptionRegex();

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

    /// <summary>Whether any graph among the arguments goes to FFmpeg in a file.</summary>
    public static bool NeedsGraphFile(IReadOnlyList<string> arguments, bool always)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (GraphOptionRegex().IsMatch(arguments[i]) && (always || arguments[i + 1].Length > GraphFileThreshold))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The arguments with their filter graphs given in files: every graph when <paramref name="always"/>, otherwise
    /// those longer than <see cref="GraphFileThreshold"/>. <paramref name="writeGraph"/> writes a graph and returns its
    /// file. With <see cref="GraphFileForm.None"/> the arguments are returned as they are.
    /// </summary>
    public static List<string> WithGraphFiles(IReadOnlyList<string> arguments, bool always, GraphFileForm form, Func<string, string> writeGraph)
    {
        var result = new List<string>(arguments);
        if (form == GraphFileForm.None)
            return result;

        for (var i = 0; i + 1 < result.Count; i++)
        {
            var option = result[i];
            if (!GraphOptionRegex().IsMatch(option))
                continue;

            i++;
            if (!always && result[i].Length <= GraphFileThreshold)
                continue;

            result[i - 1] = form == GraphFileForm.LoadOption ? "-/" + option[1..] : ScriptOptionFor(option);
            result[i] = writeGraph(result[i]);
        }

        return result;
    }

    /// <summary>The older option that reads a graph option's value from a file: "-filter_complex_script", "-filter_script:v".</summary>
    private static string ScriptOptionFor(string option) => option switch
    {
        "-filter_complex" or "-lavfi" => "-filter_complex_script",
        "-vf" => "-filter_script:v",
        "-af" => "-filter_script:a",
        _ => "-filter_script" + option["-filter".Length..],
    };

    // What each FFmpeg takes, by its path, with the size and time of the file it was asked of.
    private static readonly Dictionary<string, (long Length, DateTime Written, GraphFileForm Form)> GraphFileForms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How this FFmpeg takes a graph from a file, asked of it once (a run on a one-frame graph, some 40 ms) and
    /// remembered while the program file stays the same.
    /// </summary>
    public static async Task<GraphFileForm> GraphFileFormAsync(string executable, CancellationToken cancellationToken)
    {
        var (length, written) = (-1L, DateTime.MinValue);
        try
        {
            var info = new FileInfo(executable);
            if (info.Exists)
                (length, written) = (info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        lock (GraphFileForms)
        {
            if (GraphFileForms.TryGetValue(executable, out var known) && known.Length == length && known.Written == written)
                return known.Form;
        }

        var form = await ProbeGraphFileFormAsync(executable, cancellationToken);
        AppLog.Write($"FFmpeg takes filter graphs from files as {form}: {Path.GetFileName(executable)}");
        lock (GraphFileForms)
            GraphFileForms[executable] = (length, written, form);
        return form;
    }

    private static async Task<GraphFileForm> ProbeGraphFileFormAsync(string executable, CancellationToken cancellationToken)
    {
        var probe = Path.Combine(SessionPaths.Graphs, $"probe_{Guid.NewGuid():N}.txt");
        try
        {
            Directory.CreateDirectory(SessionPaths.Graphs);
            File.WriteAllText(probe, "nullsrc=s=16x16:d=0.04", Utf8WithoutMark);
            foreach (var (form, option) in new[] { (GraphFileForm.LoadOption, "-/filter_complex"), (GraphFileForm.ScriptOption, "-filter_complex_script") })
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(executable)
                        .WithArguments(["-hide_banner", "-nostdin", "-loglevel", "error", option, probe, "-f", "null", "-"])
                        .WithValidation(CommandResultValidation.None), timeout.Token);
                    if (result.ExitCode == 0)
                        return form;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // It did not answer in time: taken as not knowing this way.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Could not ask FFmpeg how it takes a filter graph from a file", ex);
        }
        finally
        {
            DeleteQuietly(probe);
        }

        return GraphFileForm.None;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere: it is in the session folder, which goes when the program closes.
        }
    }

    /// <summary>
    /// Makes the player's stand-in clock (see MainViewModel.PlayerSource): black, 64 by 36, at the given frame
    /// rate, with a silent track for each channel layout given, I-frames a second apart so that it seeks at
    /// once. Written beside its place and moved there when whole. Returns whether it was made.
    /// </summary>
    public static async Task<bool> MakeClockFileAsync(string path, double frameRate, IReadOnlyList<string> audioLayouts, double seconds, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return false;

        var rate = frameRate.ToString("0.###", CultureInfo.InvariantCulture);
        var part = path + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c=black:s=64x36:r={rate}" };
            foreach (var layout in audioLayouts)
                arguments.AddRange(["-f", "lavfi", "-i", $"anullsrc=r=48000:cl={layout}"]);
            arguments.AddRange(["-map", "0:v"]);
            for (var i = 0; i < audioLayouts.Count; i++)
                arguments.AddRange(["-map", $"{i + 1}:a"]);
            arguments.AddRange(["-t", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "stillimage", "-pix_fmt", "yuv420p",
                "-g", Math.Max((int)Math.Round(frameRate), 1).ToString(CultureInfo.InvariantCulture)]);
            if (audioLayouts.Count > 0)
                arguments.AddRange(["-c:a", "aac", "-b:a", "32k"]);
            arguments.AddRange(["-f", "matroska", part]);

            var said = new StringBuilder();
            var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfmpegPath).WithArguments(arguments)
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(said)).WithValidation(CommandResultValidation.None), cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(part))
            {
                AppLog.Write($"The player's stand-in clock could not be made: {said.ToString().Trim()}");
                return false;
            }

            File.Move(part, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if (ex is not OperationCanceledException)
                AppLog.Write("The player's stand-in clock could not be made", ex);
            return false;
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
    public static Task<bool> GenerateSpriteSheetAsync(string videoPath, string imagePath, double intervalSeconds, CancellationToken cancellationToken) =>
        WorkScheduler.Run(new WorkSpec($"Hover previews of {Path.GetFileName(videoPath)}", WorkLane.Background) { Key = $"sprite|{videoPath}|{intervalSeconds}" }, async work =>
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
                    var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
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
        }, cancellationToken);

    /// <summary>
    /// A strip of frames from a video, evenly spread over its length and set side by side in one picture:
    /// what a video's block on the timeline is drawn with. The background graphics card decodes where it can;
    /// when that does not work out (a format the card does not take, a driver that will not open eight decoders
    /// at once) the strip is made again by the processor. Background work (the media browser's: Idle).
    /// </summary>
    /// <param name="height">How tall each frame is made, in pixels: 54 for a block on the timeline.</param>
    public static Task<bool> GenerateFilmstripAsync(string videoPath, string imagePath, int count, double durationSeconds, CancellationToken cancellationToken,
        int height = 54, WorkLane lane = WorkLane.Background) =>
        WorkScheduler.Run(new WorkSpec($"Thumbnails of {Path.GetFileName(videoPath)}", lane) { Key = $"strip|{videoPath}|{count}|{height}|{imagePath}" }, async work =>
        {
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
        }, cancellationToken);

    /// <summary>Makes the strip once. Null when it was written; otherwise what FFmpeg said.</summary>
    private static async Task<string?> RunFilmstripAsync(string videoPath, string imagePath, int count, double durationSeconds, bool hardware, int height, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 40);
        height = Math.Clamp(height / 2 * 2, 16, 1080);

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

        // Known already for this file as it is (listed this session, or taken up with a project or from seekindex.bin).
        if (MediaProbe.KnownIFrames(path) is { } known)
            return [.. known];

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
        return await WorkScheduler.Run(new WorkSpec($"I-frames of {Path.GetFileName(path)}", WorkLane.Background) { Key = $"iframes|{path}" }, async work =>
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
