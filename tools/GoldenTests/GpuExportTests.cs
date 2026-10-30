using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// The GPU export (Pass 5.0-AudioAnd3D, run in 5.0-Verify) against the FFmpeg export of the same timeline.
/// <para>gpu-export-command, with no worker: the command the GPU export runs (FilterGraphBuilder.BuildGpuExportCommand) for
/// hand-written states, what is refused and why, and the frames each pair of rates pipes (GpuExportTiming), checked against
/// FFmpeg's color source and fps filter as they count, frame by frame.</para>
/// <para>gpu-export, with a real worker and HandPeg's FFmpeg: a main video in two clips with a crossfade, a picture in
/// picture with place and opacity keys, and a title, exported both ways (lossless: FFV1 and 16-bit PCM in Matroska) at the
/// timeline's rate, at a lower one and at a higher one. Every frame's PSNR must reach a floor (and their mean another), the
/// frame counts, the sample counts and the durations must be equal, and the sound must be the FFmpeg export's to 1 LSB. The
/// measured numbers go to the console (they differ by card and driver).</para>
/// </summary>
public static partial class GpuExportTests
{
    private const int W = 320, H = 180;
    private const double Rate = 30, Seconds = 4;

    /// <summary>The PSNR (dB) every frame must reach, and the mean over the export.</summary>
    private const double FrameFloor = 22, MeanFloor = 25;

    /// <summary>gpu-export: the timeline exported both ways (a worker and HandPeg's FFmpeg; skipped, with a note, without FFmpeg).</summary>
    public static GraphResult Exports()
    {
        var text = new StringBuilder("# The GPU export (GpuExport) against the FFmpeg export of the same timeline: frames by PSNR, the sound sample for sample.\n");
        var problems = new List<string>();
        try
        {
            Run(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-export", text.ToString(), problems, 0);
    }

    // ----- The command and the frames, without a worker -----

    /// <summary>gpu-export-command: the commands, the refusals and the frame timing (no worker, no FFmpeg).</summary>
    public static GraphResult Commands()
    {
        var text = new StringBuilder("# The GPU export's FFmpeg command (the export's own, its picture piped), what it refuses, and the frames it pipes.\n");
        var problems = new List<string>();
        try
        {
            var files = new Files("D:/media/main.mp4", "D:/media/inset.mp4");

            // The full timeline: two main clips with a crossfade, a picture in picture, a title; fades, auto-captions, a mixed track.
            var full = State(Timeline(files), files, "D:/media/out.mkv") with
            {
                FfmpegExecutable = "ffmpeg",
                VideoCodecArguments = "-c:v libx264 -preset medium -crf 18",
                FadeIn = true,
                FadeOut = true,
                AutoCaptions = true,
                CaptionsFilePath = "D:/media/captions.ass",
                AudioTracks = [Track("aac", "160k")],
            };
            Describe(text, problems, "the whole timeline, fades, auto-captions", full);

            // One clip of the whole file: a hardware encoder's own format, the source's subtitles burned in, chapters at the
            // markers (an input before the pipe's), another rate.
            var single = State(SingleClip(files), files, "D:/media/out.mp4") with
            {
                FfmpegExecutable = "ffmpeg",
                VideoCodecArguments = "-c:v h264_nvenc -preset p5 -rc vbr -cq 21 -b:v 0 -pix_fmt yuv420p",
                EncoderPixelFormat = "yuv420p",
                BurnedSubtitleStreams = [0],
                ChaptersAtMarkers = true,
                ChaptersFilePath = "D:/media/chapters.txt",
                TargetFramerate = "24",
                AudioTracks = [Track("aac", "160k")],
            };
            Describe(text, problems, "one whole clip, NVENC, burned subtitles, chapters, 24 fps", single);

            // What is refused, and the words for it.
            text.Append("\nrefused:\n");
            foreach (var (name, state) in new (string, SequenceExportState)[]
                     {
                         ("Copy", full with { CopyVideoEncoder = true, VideoCodecArguments = "-c:v copy" }),
                         ("GIF", full with { OutputPath = "D:/media/out.gif", IsAnimatedOutput = true }),
                         ("deinterlaced", full with { Deinterlace = true }),
                         ("tone-mapped", full with { TonemapToSdr = true }),
                         ("burned subtitles on a spliced main video", full with { BurnedSubtitleStreams = [0] }),
                         ("no known length", full with { MainMediaSeconds = 0 }),
                         ("channel balance", full with { ColorFilters = ["eq=gamma_r=1.2:gamma_g=1:gamma_b=0.9"] }),
                     })
            {
                var reason = GpuExport.WhyNot(state);
                text.Append(CultureInfo.InvariantCulture, $"  {name}: {reason ?? "(taken: wrong)"}\n");
                if (reason is null)
                    problems.Add($"the GPU export took a state it cannot draw ({name})");
            }

            if (GpuExport.WhyNot(full) is { } refused)
                problems.Add($"the GPU export refused the whole timeline: {refused}");

            // The frames each pair of rates pipes, against FFmpeg's own counting.
            text.Append("\nframes (canvas rate, its length, export rate: canvas frames, export frames, the first export frames' canvas frames):\n");
            foreach (var (canvas, seconds, output) in new (FrameRate, double, string?)[]
                     {
                         (FrameRate.Fps30, 4, null), (FrameRate.Fps30, 4, "24"), (FrameRate.Fps30, 4, "60"), (FrameRate.Fps60, 10.01, "30"),
                         (FrameRate.Ntsc2997, 10, "29.97"), (FrameRate.Ntsc23976, 7.5, "25"), (FrameRate.Fps25, 3.333, "59.94"), (FrameRate.Fps24, 0.02, null),
                     })
            {
                var micros = (long)Math.Round(double.Parse(seconds.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 1_000_000);
                var rate = FilterGraphBuilder.ParseFrameRate(output);
                var timing = GpuExportTiming.Of(micros, canvas, rate);
                var first = string.Join(" ", Enumerable.Range(0, (int)Math.Min(timing.OutputFrames, 12)).Select(k => timing.CanvasFrameOf(k).ToString(CultureInfo.InvariantCulture)));
                text.Append(CultureInfo.InvariantCulture,
                    $"  {canvas.Numerator}/{canvas.Denominator}, {seconds:0.###} s, {(output ?? "the same")}: {timing.CanvasFrames} canvas, {timing.OutputFrames} out ({timing.OutputRate}): {first}\n");
                CheckTiming(timing, micros, $"{canvas.Numerator}/{canvas.Denominator} at {output ?? "the same"}", problems);
            }
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-export-command", text.ToString(), problems, 0);
    }

    // ----- Render Preview on the GPU (5.0-Verify Q5) -----

    /// <summary>gpu-preview-command: Render Preview's command on the GPU (BuildGpuPreviewCommand) and its stretch's frames (GpuExportTiming.Window); no worker.</summary>
    public static GraphResult PreviewCommands()
    {
        var text = new StringBuilder("# Render Preview on the GPU compositor (5.0-Verify Q5): its FFmpeg command (the piped stretch, the sound engine's sound, the preview's finishing) and the frames of its stretch.\n");
        var problems = new List<string>();
        try
        {
            var files = new Files("D:/media/main.mp4", "D:/media/inset.mp4");
            var state = State(Timeline(files), files, "D:/media/out.mp4") with { FfmpegExecutable = "ffmpeg", AudioTracks = [Track("aac", "160k")] };
            const string output = "D:/session/previews/preview.mp4", captions = "D:/session/previews/captions_preview.ass", sound = "D:/session/previews/preview.mp4.sound.f32";
            foreach (var (name, stateOf, from, to, percent, captionsPath, soundPath, first, frames, outputFrames) in new (string, SequenceExportState, double, double, int, string?, string?, long, long, long)[]
                     {
                         ("2 s from 1 s, half size, captions and sound", state, 1, 3, 50, captions, sound, 30, 60, 60),
                         ("from between two frames (1.01 s): the captions' clock set back to the stretch's start", state, 1.01, 3.01, 50, captions, sound, 31, 60, 60),
                         ("at 24 fps, full size, silent", state with { TargetFramerate = "24" }, 0.5, 2.5, 100, null, null, 15, 60, 48),
                         ("past the end (3 to 6 s of a 4 s timeline)", state, 3, 6, 50, null, sound, 90, 30, 30),
                     })
            {
                var command = FilterGraphBuilder.BuildGpuPreviewCommand(stateOf, output, from, to, percent, captionsPath, soundPath, out var why);
                text.Append(CultureInfo.InvariantCulture, $"\n{name}:\n");
                if (command is null)
                {
                    text.Append("  refused: ").Append(why).Append('\n');
                    problems.Add($"{name}: refused ({why})");
                    continue;
                }

                var timing = command.Timing;
                var shown = string.Join(" ", Enumerable.Range(0, (int)Math.Min(timing.OutputFrames, 8)).Select(k => timing.CanvasFrameOf(k).ToString(CultureInfo.InvariantCulture)));
                text.Append(CultureInfo.InvariantCulture,
                    $"  {command.Width} x {command.Height}: canvas frames {timing.FirstCanvasFrame} on ({timing.CanvasFrames}), {timing.OutputFrames} frames at {timing.OutputRate}: {shown}\n");
                text.Append("  ").Append(command.CommandLine).Append('\n');
                if (timing.FirstCanvasFrame != first || timing.CanvasFrames != frames || timing.OutputFrames != outputFrames)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: frames {timing.FirstCanvasFrame} on ({timing.CanvasFrames}, {timing.OutputFrames} out), not {first} on ({frames}, {outputFrames} out)"));
                if (timing.CanvasFrameOf(0) != first || timing.CanvasFrameOf(timing.OutputFrames - 1) != first + frames - 1)
                    problems.Add($"{name}: the stretch's frames are not its canvas's first to last");
                var arguments = command.Arguments.ToList();
                if (arguments.IndexOf("-map") is var map && (map < 0 || arguments[map + 1] != "0:v:0"))
                    problems.Add($"{name}: the picture is not the pipe's");
                if (soundPath is not null != arguments.Contains("1:a:0"))
                    problems.Add($"{name}: the sound is not the rendered file's");
            }

            // What is refused: the export's reasons, and the source's burned subtitles.
            text.Append("\nrefused:\n");
            foreach (var (name, refused) in new (string, SequenceExportState)[]
                     {
                         ("the source's burned subtitles", state with { BurnedSubtitleStreams = [0] }),
                         ("Copy", state with { CopyVideoEncoder = true, VideoCodecArguments = "-c:v copy" }),
                         ("a stretch past the end", state),
                     })
            {
                var (from, to) = name == "a stretch past the end" ? (5.0, 7.0) : (1.0, 3.0);
                var command = FilterGraphBuilder.BuildGpuPreviewCommand(refused, output, from, to, 50, null, null, out var why);
                text.Append(CultureInfo.InvariantCulture, $"  {name}: {(command is null ? why : "(taken: wrong)")}\n");
                if (command is not null)
                    problems.Add($"the GPU preview took a stretch it cannot draw ({name})");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-preview-command", text.ToString(), problems, 0);
    }

    /// <summary>
    /// gpu-preview-window, with a real worker and HandPeg's FFmpeg: a 2 s stretch from 1 s of the gpu-export timeline made by
    /// GpuExport.TryRunPreviewAsync (the view model's way in, on this STA thread: its awaits come back here, as to the UI
    /// thread), at full size. Its frame count, the moment of its first and last frames (each the FFmpeg export's frame it
    /// matches best, within 10), and its sound's length.
    /// </summary>
    public static GraphResult PreviewWindow()
    {
        var text = new StringBuilder("# Render Preview on the GPU compositor (5.0-Verify Q5): a 2 s stretch of the gpu-export timeline, its frames against the FFmpeg export's.\n");
        var problems = new List<string>();
        try
        {
            text.Append(CultureInfo.InvariantCulture, $"from 1 to 3 s at {Rate:0} fps, full size: 60 frames, the first the export's frame 30, the last its 89 (best matched within 10); its sound 2 s long (within 25 ms)\n");
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null)
            {
                Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the GPU preview was not made");
                return new GraphResult("gpu-preview-window", text.ToString(), problems, 0);
            }

            var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.GpuPreview");
            Directory.CreateDirectory(folder);
            var files = MakeMedia(ffmpeg, folder);
            var state = State(Timeline(files), files, Path.Combine(folder, "reference.mkv")) with
            {
                FfmpegExecutable = $"\"{ffmpeg}\"",
                VideoCodecArguments = "-c:v ffv1 -pix_fmt yuv420p",
                AudioTracks = [Track("pcm_s16le", "")],
            };

            var reference = FilterGraphBuilder.BuildExportCommand(state);
            Task.Run(() => FfmpegRunner.RunAsync(reference.CommandLine, new Progress<FfmpegProgress>(), CancellationToken.None, graphInFile: true)).GetAwaiter().GetResult();

            var preview = Path.Combine(folder, "preview.mp4");
            var clock = Stopwatch.StartNew();
            var made = OnThisThread(() => GpuExport.TryRunPreviewAsync(state, preview, 1, 3, 100, null, new Progress<FfmpegProgress>(), CancellationToken.None));
            if (!made)
            {
                problems.Add($"the GPU preview was not made ({GpuExport.WhyNot(state) ?? "its compositor did not start, or could not plan it: the log says"})");
                return new GraphResult("gpu-preview-window", text.ToString(), problems, 0);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        gpu preview: 2 s made in {clock.Elapsed.TotalSeconds:0.00} s"));
            var size = W * H * 3;
            var (ours, theirs) = (Read(ffmpeg, preview, Picture), Read(ffmpeg, state.OutputPath, Picture));
            var (count, referenceCount) = (ours.Length / size, theirs.Length / size);
            if (count != 60)
                problems.Add($"the preview has {count} frames, not 60");

            int Matched(int frame, int near)
            {
                var (best, at) = (double.MinValue, -1);
                for (var j = Math.Max(near - 10, 0); j <= Math.Min(near + 10, referenceCount - 1); j++)
                {
                    var psnr = Psnr(ours.AsSpan(frame * size, size), theirs.AsSpan(j * size, size));
                    if (psnr > best)
                        (best, at) = (psnr, j);
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        gpu preview: frame {frame} is the export's {at} ({best:0.0} dB)"));
                return at;
            }

            if (count > 0 && Matched(0, 30) is var firstFrame && firstFrame != 30)
                problems.Add($"the preview's first frame is the export's {firstFrame}, not 30 (1 s)");
            if (count >= 60 && Matched(59, 89) is var lastFrame && lastFrame != 89)
                problems.Add($"the preview's last frame is the export's {lastFrame}, not 89");

            var heard = Read(ffmpeg, preview, Sound).Length / 4 / 48000.0;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        gpu preview: {heard:0.000} s of sound"));
            if (Math.Abs(heard - 2) > 0.025)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"the preview's sound is {heard:0.000} s long, not 2 s"));
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-preview-window", text.ToString(), problems, 0);
    }

    /// <summary>
    /// Runs an asynchronous piece of the view model's on this STA thread as the UI thread runs it: its awaits come back here
    /// (a dispatcher frame pumped until it ends), so what must be on an STA thread (GpuExport.Prepare: WPF draws the titles) is.
    /// </summary>
    private static T OnThisThread<T>(Func<Task<T>> work)
    {
        var previous = SynchronizationContext.Current;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = work();
            var frame = new System.Windows.Threading.DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void Describe(StringBuilder text, List<string> problems, string name, SequenceExportState state)
    {
        var command = FilterGraphBuilder.BuildGpuExportCommand(state, out var whyNot);
        text.Append(CultureInfo.InvariantCulture, $"\n{name}:\n");
        if (command is null)
        {
            text.Append("  refused: ").Append(whyNot).Append('\n');
            problems.Add($"{name}: refused ({whyNot})");
            return;
        }

        var timing = command.Timing;
        text.Append(CultureInfo.InvariantCulture, $"  {command.Width} x {command.Height}, {timing.OutputFrames} frames at {timing.OutputRate} ({timing.CanvasFrames} on the canvas), chapters file: {command.UsesChaptersFile}\n");
        text.Append("  ").Append(command.CommandLine).Append('\n');

        // The sound is the FFmpeg export's: its whole -filter_complex, every -map but the picture's, every option after them.
        var export = FfmpegRunner.SplitArguments(FilterGraphBuilder.BuildExportCommand(state with { OpenClBlur = false, HardwareDecodeArguments = "" }).CommandLine[(state.FfmpegExecutable.Length + 1)..]);
        var graph = Value(export, "-filter_complex");
        if (graph != Value([.. command.Arguments], "-filter_complex"))
            problems.Add($"{name}: the sound's graph is not the FFmpeg export's");
        var pipe = command.Arguments.Count(a => a == "-i") - 1;
        if (!command.Arguments.Contains(string.Create(CultureInfo.InvariantCulture, $"{pipe}:v:0")) || command.Arguments[^1] != export[^1])
            problems.Add($"{name}: the picture is not mapped from the pipe (input {pipe}), or the output is not the export's");
    }

    private static string? Value(List<string> arguments, string option)
    {
        var at = arguments.IndexOf(option);
        return at >= 0 && at + 1 < arguments.Count ? arguments[at + 1] : null;
    }

    /// <summary>The timing against FFmpeg's counting: the color source's frames (time in microseconds before d) and the fps filter's choice, simulated frame by frame.</summary>
    private static void CheckTiming(GpuExportTiming timing, long micros, string name, List<string> problems)
    {
        long Micros(long n) => (long)Math.Round((decimal)n * timing.CanvasDenominator * 1_000_000 / timing.CanvasNumerator, MidpointRounding.AwayFromZero);
        if ((timing.CanvasFrames > 0 && Micros(timing.CanvasFrames - 1) >= micros) || Micros(timing.CanvasFrames) < micros)
            problems.Add($"{name}: {timing.CanvasFrames} canvas frames is not the color source's count");

        // vf_fps: each canvas frame's time rounded (to the nearest, halves up) to the output's clock; slot k shows the last one at or before it.
        long Slot(long n) => (long)Math.Round((decimal)n * timing.CanvasDenominator * timing.OutputNumerator / ((decimal)timing.CanvasNumerator * timing.OutputDenominator), MidpointRounding.AwayFromZero);
        var expectedFrames = timing.SameRate ? timing.CanvasFrames : Slot(timing.CanvasFrames);
        if (expectedFrames != timing.OutputFrames)
            problems.Add($"{name}: {timing.OutputFrames} export frames, the fps filter makes {expectedFrames}");
        var n = 0L;
        for (var k = 0L; k < Math.Min(timing.OutputFrames, 5000); k++)
        {
            while (n + 1 < timing.CanvasFrames && Slot(n + 1) <= k)
                n++;
            if (timing.CanvasFrameOf(k) != n)
            {
                problems.Add($"{name}: export frame {k} shows canvas frame {timing.CanvasFrameOf(k)}, the fps filter's is {n}");
                break;
            }
        }
    }

    // ----- Both ways, with a worker -----

    private sealed record Variant(string Name, string? Framerate);

    private static void Run(StringBuilder text, List<string> problems)
    {
        Variant[] variants = [new("at the timeline's 30 fps", null), new("at 24 fps (frames the fps filter drops)", "24"), new("at 60 fps (frames it repeats)", "60")];
        text.Append(CultureInfo.InvariantCulture, $"timeline: {W} x {H} at {Rate:0} fps, {Seconds:0} s: the main video in two clips (a 1 s crossfade at 2 s), a picture in picture (place and opacity keys), a title\n");
        foreach (var variant in variants)
        {
            var timing = GpuExportTiming.Of((long)(Seconds * 1_000_000), FrameRate.Fps30, FilterGraphBuilder.ParseFrameRate(variant.Framerate));
            text.Append(CultureInfo.InvariantCulture, $"  {variant.Name}: {timing.OutputFrames} frames, each at least {FrameFloor:0} dB, on average at least {MeanFloor:0} dB; the sound to 1 LSB\n");
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the GPU export was not compared");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.GpuExport");
        Directory.CreateDirectory(folder);
        var files = MakeMedia(ffmpeg, folder);
        foreach (var variant in variants)
        {
            try
            {
                Both(ffmpeg, folder, files, variant, problems);
            }
            catch (Exception ex)
            {
                problems.Add($"{variant.Name}: threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    /// <summary>One variant exported by FFmpeg's graph, then by the GPU compositor with the same command's sound, and the two compared.</summary>
    private static void Both(string ffmpeg, string folder, Files files, Variant variant, List<string> problems)
    {
        var slug = variant.Framerate ?? "same";
        var state = State(Timeline(files), files, Path.Combine(folder, $"ffmpeg-{slug}.mkv")) with
        {
            FfmpegExecutable = $"\"{ffmpeg}\"",
            VideoCodecArguments = "-c:v ffv1 -pix_fmt yuv420p",
            TargetFramerate = variant.Framerate,
            AudioTracks = [Track("pcm_s16le", "")],
        };

        // FFmpeg's graph.
        var command = FilterGraphBuilder.BuildExportCommand(state);
        Task.Run(() => FfmpegRunner.RunAsync(command.CommandLine, new Progress<FfmpegProgress>(), CancellationToken.None, graphInFile: true)).GetAwaiter().GetResult();

        // The GPU compositor's frames, through the same command's sound: the plan made here, on this STA thread, as the UI thread makes it.
        var gpuState = state with { OutputPath = Path.Combine(folder, $"gpu-{slug}.mkv") };
        if (GpuExport.WhyNot(gpuState) is { } refused)
        {
            problems.Add($"{variant.Name}: refused ({refused})");
            return;
        }

        var client = GpuExport.StartCompositor(out var why);
        if (client is null)
        {
            problems.Add($"{variant.Name}: {why}");
            return;
        }

        var export = GpuExport.Prepare(gpuState, client, out why);
        if (export is null)
        {
            client.Dispose();
            problems.Add($"{variant.Name}: not prepared ({why})");
            return;
        }

        var clock = Stopwatch.StartNew();
        using (export)
            Task.Run(() => export.RunAsync(new Progress<FfmpegProgress>(), CancellationToken.None)).GetAwaiter().GetResult();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {variant.Name}: the GPU export took {clock.Elapsed.TotalSeconds:0.00} s, {export.FramesPiped} frames piped"));

        Compare(ffmpeg, variant.Name, state.OutputPath, gpuState.OutputPath, export.Command.Timing.OutputFrames, problems);
    }

    private static void Compare(string ffmpeg, string name, string reference, string gpu, long expectedFrames, List<string> problems)
    {
        var size = W * H * 3;
        var (a, b) = (Read(ffmpeg, reference, Picture), Read(ffmpeg, gpu, Picture));
        if (a.Length % size != 0 || b.Length % size != 0)
            problems.Add($"{name}: the frames read back are not whole ({a.Length}, {b.Length} bytes)");
        var (framesA, framesB) = (a.Length / size, b.Length / size);
        if (framesA != framesB || framesB != expectedFrames)
            problems.Add($"{name}: FFmpeg's export has {framesA} frames, the GPU's {framesB}, its timing {expectedFrames}");

        var (worst, sum, at) = (double.MaxValue, 0.0, -1);
        for (var f = 0; f < Math.Min(framesA, framesB); f++)
        {
            var psnr = Psnr(a.AsSpan(f * size, size), b.AsSpan(f * size, size));
            sum += psnr;
            if (psnr < worst)
                (worst, at) = (psnr, f);
        }

        var mean = framesA > 0 ? sum / Math.Min(framesA, framesB) : 0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {name}: {Math.Min(framesA, framesB)} frames, worst {worst:0.0} dB (frame {at}), mean {mean:0.0} dB"));
        if (worst < FrameFloor)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: frame {at} at {worst:0.0} dB, under {FrameFloor:0} dB"));
        if (mean < MeanFloor)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: the mean is {mean:0.0} dB, under {MeanFloor:0} dB"));

        // The sound: the same graph and encoder, so the same samples (to 1 LSB, should a resampler round differently).
        var (soundA, soundB) = (Read(ffmpeg, reference, Sound), Read(ffmpeg, gpu, Sound));
        if (soundA.Length != soundB.Length)
            problems.Add($"{name}: FFmpeg's export has {soundA.Length / 4} sample frames of sound, the GPU's {soundB.Length / 4}");
        var largest = 0;
        for (var i = 0; i + 1 < Math.Min(soundA.Length, soundB.Length); i += 2)
            largest = Math.Max(largest, Math.Abs(BitConverter.ToInt16(soundA, i) - BitConverter.ToInt16(soundB, i)));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {name}: the sound differs by at most {largest} LSB"));
        if (largest > 1)
            problems.Add($"{name}: the sound differs by up to {largest} LSB");

        if (Duration(ffmpeg, reference) is { } durationA && Duration(ffmpeg, gpu) is { } durationB && Math.Abs(durationA - durationB) > 0.0015)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: FFmpeg's export lasts {durationA:0.000} s, the GPU's {durationB:0.000} s"));
    }

    private const string Picture = "-map 0:v:0 -vf \"scale=in_color_matrix=bt709:in_range=tv,format=rgb24\" -f rawvideo -";
    private const string Sound = "-map 0:a:0 -f s16le -ac 2 -ar 48000 -";

    /// <summary>An export's frames (RGB24) or sound (16-bit stereo) as FFmpeg decodes them.</summary>
    private static byte[] Read(string ffmpeg, string file, string how)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, $"-hide_banner -loglevel error -i \"{file}\" {how}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        using var data = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(data);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg could not read {Path.GetFileName(file)} back: {errors.Result}");
        return data.ToArray();
    }

    /// <summary>A file's duration as ffprobe (beside FFmpeg) gives it; null without ffprobe.</summary>
    private static double? Duration(string ffmpeg, string file)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(ffprobe))
            return null;
        using var process = Process.Start(new ProcessStartInfo(ffprobe, $"-v error -show_entries format=duration -of csv=p=0 \"{file}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        var said = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return double.TryParse(said.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
    }

    private static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }

        var mse = sum / a.Length;
        return mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    // ----- The media and the timeline -----

    private sealed record Files(string Main, string Inset);

    /// <summary>The main clip (5 s: handles round the cut) with a 440 Hz tone, and an inset, both 30 frames a second, BT.709, a key frame a second.</summary>
    private static Files MakeMedia(string ffmpeg, string folder)
    {
        var files = new Files(Path.Combine(folder, "main.mp4"), Path.Combine(folder, "inset.mp4"));
        const string encode = "-c:v libx264 -preset veryfast -crf 12 -g 30 -bf 2 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv";
        const string picture = "-vf \"scale=out_color_matrix=bt709:out_range=tv,format=yuv420p\"";
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"testsrc2=s={W}x{H}:r=30:d=5\" -f lavfi -i \"sine=frequency=440:sample_rate=48000:duration=5\" {picture} {encode} -c:a aac -b:a 192k -ac 2 -y \"{files.Main}\"");
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"smptehdbars=s={W}x{H}:r=30:d=5,drawbox=x='mod(t*120,{W})':y=60:w=40:h=40:c=white:t=fill\" {picture} {encode} -an -y \"{files.Inset}\"");
        return files;
    }

    private static void FfmpegOnce(string ffmpeg, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error " + arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg failed: {errors}");
    }

    private static string? FindFfmpeg()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[] { Path.Combine(local, "HandPeg-Data", "deps", "ffmpeg", "ffmpeg.exe"), Path.Combine(local, "HandPeg", "deps", "ffmpeg", "ffmpeg.exe") })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>The main video in two clips (the second crossfading in over 1 s at 2 s), a picture in picture moving and fading by its keys, a title from 0.5 to 3.5 s.</summary>
    private static TimelineDocument Timeline(Files files)
    {
        var document = new TimelineDocument { Width = W, Height = H, FrameRate = FrameRate.Fps30 };
        var main = Track(document, TimelineTrackKind.Video);
        main.Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 2, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        var later = Clip(files.Main, LayerKind.MainVideo, 2, 4, 2, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main));
        later.TransitionIn = 1;
        main.Clips.Add(later);

        var inset = Look(LayerKind.VideoFile, "inset", 0.55, 0.08, 0.4, files.Inset);
        inset.KeysX = [new Keyframe(0, 0.55, EasingType.Linear), new Keyframe(4, 0.2)];
        inset.KeysOpacity = [new Keyframe(0, 100, EasingType.Linear), new Keyframe(3, 50)];
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Inset, LayerKind.VideoFile, 0, 4, 0, inset));

        var words = new LayerState
        {
            Kind = LayerKind.Text, Name = "Title", TextContent = "GPU EXPORT", FontFamily = "Arial", FontSize = 40, FontColor = "#FFFFFF",
            OutlineColor = "#000000", OutlineThickness = 2, PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.5,
        };
        var layout = TextLayout.Measure(words.TextContent, words.FontFamily, words.FontSize, words.OutlineThickness);
        (words.ImageWidth, words.ImageHeight) = (layout.Width, layout.Height);
        Track(document, TimelineTrackKind.Video).Clips.Add(new TimelineClip { ContentKind = LayerKind.Text, Start = 0.5, End = 3.5, State = words });
        return document;
    }

    /// <summary>The main video in one clip of the whole file.</summary>
    private static TimelineDocument SingleClip(Files files)
    {
        var document = new TimelineDocument { Width = W, Height = H, FrameRate = FrameRate.Fps30 };
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 5, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        return document;
    }

    private static TimelineTrackModel Track(TimelineDocument document, TimelineTrackKind kind)
    {
        var track = new TimelineTrackModel { Kind = kind, TrackNumber = document.Tracks.Count(t => t.Kind == kind) + 1 };
        document.Tracks.Add(track);
        return track;
    }

    private static LayerState Look(LayerKind kind, string name, double x, double y, double width, string file) => new()
    {
        Kind = kind, Name = name, PositionX = x, PositionY = y, SizeWidth = width, SourceWidth = 0.25, SourceHeight = 0.25,
        ImagePath = file, ImageWidth = W, ImageHeight = H, MediaDuration = 5,
    };

    private static TimelineClip Clip(string file, LayerKind kind, double start, double end, double offset, LayerState look) => new()
    {
        ContentKind = kind, SourcePath = file, Start = start, End = end, MediaOffset = offset, SourceDuration = 5, State = look,
    };

    private static ExportAudioTrack Track(string codec, string bitrate) =>
        new(0, "Track 1", codec, bitrate, false, false, false, false, false, false, [], []);

    private static SequenceExportState State(TimelineDocument document, Files files, string output) => new()
    {
        Timeline = document,
        SourceFrameRate = Rate,
        SourceWidth = W,
        SourceHeight = H,
        RequestedSize = (document.Width, document.Height),
        OutputSize = (document.Width, document.Height),
        LocalMediaPath = files.Main,
        HasSource = true,
        MainMediaSeconds = 5,
        CenterRect = (0, 0, W, H),
        FrameEngine = true,
        Container = Path.GetExtension(output).TrimStart('.'),
        OutputPath = output,
        CaptionLayer = Layer.FromState(new LayerState
        {
            Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
            SourceWidth = 0.25, SourceHeight = 0.25,
        }, W, H, document.Width, document.Height),
        MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [files.Main] = 0, [files.Inset] = 0 },
    };
}
