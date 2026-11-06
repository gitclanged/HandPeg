using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

/// <summary>
/// The export (Pass 5.0-AudioAnd3D, run in 5.0-Verify; since 5.2-Audit the only one: the compositor's frames, the sound engine's
/// tracks, FFmpeg an encoder and a muxer alone).
/// <para>gpu-export-command, with no worker: the command the export runs (EncoderCommand) for hand-written states, that no
/// filter graph is in it, what FFmpeg's graph used to make that is now taken, the sound's tracks and buses, and the frames each
/// pair of rates pipes (GpuExportTiming), checked against FFmpeg's color source and fps filter as they count, frame by frame.</para>
/// <para>gpu-export, with a real worker and HandPeg's FFmpeg: a main video in two clips with a crossfade, a picture in picture
/// with place and opacity keys, and a title, exported losslessly (FFV1 and 16-bit PCM in Matroska) at the timeline's rate: every
/// frame against the scene the preview draws at its moment, the sound against the engine's render to 1 LSB; then at a lower and a
/// higher rate, each frame the 30 fps export's frame its slot shows. The measured numbers go to the console.</para>
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

    /// <summary>gpu-export-command: the commands, what is left to the compositor, and the frame timing (no worker, no FFmpeg).</summary>
    public static GraphResult Commands()
    {
        var text = new StringBuilder("# The export's FFmpeg command (5.2-Audit: an encoder and a muxer, the picture piped, the sound the engine's tracks), and the frames it pipes.\n");
        var problems = new List<string>();
        try
        {
            var files = new Files("D:/media/main.mp4", "D:/media/inset.mp4");

            // The full timeline: two main clips with a crossfade, a picture in picture, a title; fades, auto-captions, a mixed track.
            // (A fresh document for each set of streams: the conversion puts them on Sub-Lanes of the document it is given.)
            SequenceExportState Full(params LegacyStream[] streams) => State(Timeline(files), files, "D:/media/out.mkv", streams) with
            {
                FfmpegExecutable = "ffmpeg",
                VideoCodecArguments = "-c:v libx264 -preset medium -crf 18",
                FadeIn = true,
                FadeOut = true,
                AutoCaptions = true,
                CaptionsFilePath = "D:/media/captions.ass",
            };
            var full = Full(Stream("aac", "160k"));
            Describe(text, problems, "the whole timeline, fades, auto-captions", full);

            // One clip of the whole file: a hardware encoder's own format, the source's subtitles burned in, chapters at the
            // markers (an input after the pipe's), another rate.
            var single = State(SingleClip(files), files, "D:/media/out.mp4", Stream("aac", "160k")) with
            {
                FfmpegExecutable = "ffmpeg",
                VideoCodecArguments = "-c:v h264_nvenc -preset p5 -rc vbr -cq 21 -b:v 0 -pix_fmt yuv420p",
                EncoderPixelFormat = "yuv420p",
                BurnedSubtitleStreams = [0],
                ChaptersAtMarkers = true,
                ChaptersFilePath = "D:/media/chapters.txt",
                TargetFramerate = "24",
            };
            Describe(text, problems, "one whole clip, NVENC, burned subtitles, chapters, 24 fps", single);

            // What FFmpeg's graph used to make, the compositor and the sound engine make now: each of these is taken.
            text.Append("\nonce FFmpeg's, now taken:\n");
            foreach (var (name, state) in new (string, SequenceExportState)[]
                     {
                         ("deinterlaced", full with { Deinterlace = true }),
                         ("tone-mapped", full with { TonemapToSdr = true }),
                         ("burned subtitles on a spliced main video", full with { BurnedSubtitleStreams = [0] }),
                         ("no known length", full with { Timeline = Unmeasured(full.Timeline) }),
                         ("WebP", full with { OutputPath = "D:/media/out.webp", IsAnimatedOutput = true }),
                     })
            {
                var reason = GpuExport.WhyNot(state);
                var command = GpuExport.CommandFor(state, out _);
                text.Append(CultureInfo.InvariantCulture, $"  {name}: {reason ?? "taken"}{(command is null ? "" : $", {command.PipeFormat} piped")}\n");
                if (reason is not null)
                    problems.Add($"the export refused what it draws now ({name}: {reason})");
                if (command is not null && command.Arguments.Any(a => a is "-vf" or "-af" or "-filter_complex" or "-filter:a" or "-filter:v"))
                    problems.Add($"{name}: the command writes a filter graph");
            }

            // A GIF is HandPeg's own: no FFmpeg at all.
            var gif = GpuExport.CommandFor(full with { OutputPath = "D:/media/out.gif", IsAnimatedOutput = true }, out _);
            text.Append("  GIF: ").Append(gif?.CommandLine ?? "refused").Append('\n');
            if (gif is not { Encoder: ExportEncoder.Gif, PipeFormat: EncoderCommand.PipeBgra })
                problems.Add("a GIF is not HandPeg's own encoder's");

            // What is still refused: a frame the compositor cannot render.
            text.Append("\nrefused:\n");
            var huge = full with { Timeline = Resized(full.Timeline, 9000, 5000) };
            var refused = GpuExport.WhyNot(huge);
            text.Append("  a 9000 x 5000 frame: ").Append(refused ?? "(taken: wrong)").Append('\n');
            if (refused is null)
                problems.Add("the export took a frame the compositor cannot render");

            // The sound: the engine's tracks, mixed or kept apart, copied where they are kept as they are, normalized here.
            text.Append("\nthe sound (each track the engine's render, named by its token until it is made):\n");
            foreach (var (name, state) in new (string, SequenceExportState)[]
                     {
                         ("as it stands", full),
                         ("normalized", full with { NormalizeAudio = true }),
                         ("no sound", Full()),
                         ("two tracks kept apart", Full(Stream("aac", "160k"), Stream("libopus", "96k") with { Index = 1, Title = "Mic" }) with { MergeAudioTracks = false }),
                         ("two tracks merged", Full(Stream("aac", "160k"), Stream("aac", "160k") with { Index = 1 }) with { MergeAudioTracks = true }),
                         ("a 5.1 track as the source has it", Full(Stream("aac", "320k") with { Channels = 6 }) with { AudioChannels = AudioChannelChoice.Source }),
                         ("a copied track of the whole file", State(SingleClip(files), files, "D:/media/out.mkv", Stream("aac", "160k") with { Passthrough = true }) with { FfmpegExecutable = "ffmpeg" }),
                     })
            {
                var command = GpuExport.CommandFor(state, out var why);
                text.Append(CultureInfo.InvariantCulture, $"  {name}: ");
                if (command is null)
                {
                    text.Append("refused: ").Append(why).Append('\n');
                    problems.Add($"{name}: refused ({why})");
                    continue;
                }

                var tracks = command.Sound;
                text.Append(tracks.Count == 0 ? "silent" : string.Join("; ", tracks.Select(t => t.IsCopy
                    ? string.Create(CultureInfo.InvariantCulture, $"a:{t.CopyStream} copied")
                    : string.Create(CultureInfo.InvariantCulture, $"{t.Codec} {t.Layout} on bus{(t.Buses.Length > 1 ? "es" : "")} {string.Join(",", t.Buses)}"))));
                var plan = ExportSoundPlan.For(state, false);
                text.Append(plan.Normalize ? ", normalized to -16 LUFS" : "").Append('\n');
                var maps = SoundMaps(command.Arguments);
                text.Append("    ").Append(maps).Append('\n');
            }

            // The frames each pair of rates pipes, against FFmpeg's own counting.
            text.Append("\nframes (canvas rate, its length, export rate: canvas frames, export frames, the first export frames' canvas frames):\n");
            foreach (var (canvas, seconds, output) in new (FrameRate, double, string?)[]
                     {
                         (FrameRate.Fps30, 4, null), (FrameRate.Fps30, 4, "24"), (FrameRate.Fps30, 4, "60"), (FrameRate.Fps60, 10.01, "30"),
                         (FrameRate.Ntsc2997, 10, "29.97"), (FrameRate.Ntsc23976, 7.5, "25"), (FrameRate.Fps25, 3.333, "59.94"), (FrameRate.Fps24, 0.02, null),
                     })
            {
                var micros = (long)Math.Round(double.Parse(seconds.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 1_000_000);
                var rate = EncoderCommand.ParseFrameRate(output);
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

    // The sound inputs and maps of a command, as one line.
    private static string SoundMaps(IReadOnlyList<string> arguments)
    {
        var parts = new List<string>();
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if ((arguments[i] == "-i" && arguments[i + 1].Contains(EncoderCommand.SoundToken, StringComparison.Ordinal))
                || (arguments[i] == "-map" && arguments[i + 1].Contains(":a:", StringComparison.Ordinal))
                || arguments[i].StartsWith("-c:a", StringComparison.Ordinal) || arguments[i].StartsWith("-b:a", StringComparison.Ordinal) || arguments[i] == "-ch_layout")
            {
                parts.Add(arguments[i] + " " + arguments[i + 1]);
            }
        }

        return parts.Count == 0 ? "(no sound in the command)" : string.Join(" ", parts);
    }

    private static TimelineDocument Resized(TimelineDocument document, int width, int height)
    {
        var copy = document.Clone();
        (copy.Width, copy.Height) = (width, height);
        return copy;
    }

    // The same timeline with no file's length known (5.2-AuditB: the main video's was the state's; now each clip's own).
    private static TimelineDocument Unmeasured(TimelineDocument document)
    {
        var copy = document.Clone(deep: true);
        foreach (var clip in copy.Tracks.SelectMany(t => t.Clips))
            (clip.SourceDuration, clip.State.MediaDuration) = (0, 0);
        return copy;
    }

    // ----- A Copy export that cannot cut (5.2-Arm64AndNpu, the user's answer 5) -----

    /// <summary>
    /// copy-export-refused: a Copy export that cannot cut is refused before it starts, with the reason (EncoderCommand.WhyCopyCannotCut,
    /// which the view model asks before an export or a queued job starts): a Sequence with no clip of a file's picture (nothing to copy),
    /// one whose file's clips all play at another speed (a stream copy cannot cut them), and an empty one; refused by neither, a file cut
    /// into clips at speed 1 (a cuts list) and a file whole. No worker, no FFmpeg. Every line says what it proved.
    /// </summary>
    public static GraphResult CopyRefusals()
    {
        var text = new StringBuilder("# A Copy export that cannot cut is refused before it starts, with the reason (EncoderCommand.WhyCopyCannotCut).\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            static TimelineDocument Sequence(params TimelineClip[] clips)
            {
                var document = new TimelineDocument { FrameRate = FrameRate.Fps30 };
                var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
                track.Clips.AddRange(clips);
                document.Tracks.Add(track);
                return document;
            }

            static TimelineClip Clip(LayerKind kind, double start, double end, double offset, double speed = 1) => new()
            {
                ContentKind = kind, SourcePath = kind == LayerKind.VideoFile ? "D:/media/match.mkv" : "D:/media/logo.png",
                Start = start, End = end, MediaOffset = offset, Speed = speed, SourceDuration = kind == LayerKind.VideoFile ? 60 : 0,
                State = new LayerState { Kind = kind, Name = kind == LayerKind.VideoFile ? "match.mkv" : "logo.png" },
            };

            var nothing = EncoderCommand.WhyCopyCannotCut(Sequence(Clip(LayerKind.Image, 0, 5, 0)));
            text.Append("a picture alone: ").Append(nothing ?? "(made)").Append('\n');
            Expect(nothing is not null, "a Sequence with no clip of a file's picture (a picture alone) is refused: there is nothing to copy");

            var speeds = EncoderCommand.WhyCopyCannotCut(Sequence(Clip(LayerKind.VideoFile, 0, 5, 0, 2), Clip(LayerKind.VideoFile, 5, 8, 20, 0.5)));
            text.Append("the file at other speeds: ").Append(speeds ?? "(made)").Append('\n');
            Expect(speeds is not null, "a Sequence whose file's clips all play at another speed is refused: a stream copy cannot cut them");

            var empty = EncoderCommand.WhyCopyCannotCut(new TimelineDocument());
            Expect(empty is not null && empty == nothing, "an empty Sequence is refused as having nothing to copy");

            Expect(EncoderCommand.WhyCopyCannotCut(Sequence(Clip(LayerKind.VideoFile, 0, 5, 10), Clip(LayerKind.VideoFile, 5, 9, 30))) is null,
                "a file cut into clips at speed 1 is not refused: it is copied through its cuts list");
            Expect(EncoderCommand.WhyCopyCannotCut(Sequence(Clip(LayerKind.VideoFile, 0, 60, 0))) is null, "a file played whole is not refused: it is copied as it is");
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("copy-export-refused", text.ToString(), problems, 0);
    }

    // ----- Render Preview on the GPU (5.0-Verify Q5) -----

    /// <summary>gpu-preview-command: Render Preview's command (EncoderCommand.BuildPreview) and its stretch's frames (GpuExportTiming.Window); no worker.</summary>
    public static GraphResult PreviewCommands()
    {
        var text = new StringBuilder("# Render Preview on the GPU compositor (5.0-Verify Q5): its FFmpeg command (the piped stretch at the preview's size, the sound engine's sound) and the frames of its stretch.\n");
        var problems = new List<string>();
        try
        {
            var files = new Files("D:/media/main.mp4", "D:/media/inset.mp4");
            var state = State(Timeline(files), files, "D:/media/out.mp4", Stream("aac", "160k")) with { FfmpegExecutable = "ffmpeg" };
            const string output = "D:/session/previews/preview.mp4";
            foreach (var (name, stateOf, from, to, percent, sound, first, frames, outputFrames) in new (string, SequenceExportState, double, double, int, bool, long, long, long)[]
                     {
                         ("2 s from 1 s, half size, with sound", state, 1, 3, 50, true, 30, 60, 60),
                         ("from between two frames (1.01 s)", state, 1.01, 3.01, 50, true, 31, 60, 60),
                         ("at 24 fps, full size, silent", state with { TargetFramerate = "24" }, 0.5, 2.5, 100, false, 15, 60, 48),
                         ("past the end (3 to 6 s of a 4 s timeline)", state, 3, 6, 50, true, 90, 30, 30),
                     })
            {
                var length = TimelineLowering.Lowering(stateOf).SequenceSeconds;
                var timing = GpuExportTiming.Window(from, Math.Min(to, length), stateOf.Timeline.FrameRate, EncoderCommand.ParseFrameRate(stateOf.TargetFramerate));
                var command = EncoderCommand.BuildPreview(stateOf, output, timing, percent, sound, captionsOnGpu: false);
                text.Append(CultureInfo.InvariantCulture, $"\n{name}:\n");
                var shown = string.Join(" ", Enumerable.Range(0, (int)Math.Min(timing.OutputFrames, 8)).Select(k => timing.CanvasFrameOf(k).ToString(CultureInfo.InvariantCulture)));
                text.Append(CultureInfo.InvariantCulture,
                    $"  {command.Width} x {command.Height} (the {command.CanvasWidth} x {command.CanvasHeight} canvas drawn into it): canvas frames {timing.FirstCanvasFrame} on ({timing.CanvasFrames}), {timing.OutputFrames} frames at {timing.OutputRate}: {shown}\n");
                text.Append("  ").Append(command.CommandLine).Append('\n');
                if (timing.FirstCanvasFrame != first || timing.CanvasFrames != frames || timing.OutputFrames != outputFrames)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: frames {timing.FirstCanvasFrame} on ({timing.CanvasFrames}, {timing.OutputFrames} out), not {first} on ({frames}, {outputFrames} out)"));
                if (timing.CanvasFrameOf(0) != first || timing.CanvasFrameOf(timing.OutputFrames - 1) != first + frames - 1)
                    problems.Add($"{name}: the stretch's frames are not its canvas's first to last");
                var arguments = command.Arguments.ToList();
                if (arguments.IndexOf("-map") is var map && (map < 0 || arguments[map + 1] != "0:v:0"))
                    problems.Add($"{name}: the picture is not the pipe's");
                if (sound != arguments.Contains("1:a:0"))
                    problems.Add($"{name}: the sound is not the rendered file's");
                if (arguments.Any(a => a is "-vf" or "-af" or "-filter_complex"))
                    problems.Add($"{name}: the preview writes a filter graph");
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
    /// GpuExport.RunPreviewAsync (the view model's way in, on this STA thread: its awaits come back here, as to the UI thread), at
    /// full size, against the export of the whole timeline (5.2-Audit: the export is the GPU's too). Its frame count, the moment of
    /// its first and last frames (each the export's frame it matches best, within 10), and its sound's length.
    /// </summary>
    public static GraphResult PreviewWindow()
    {
        var text = new StringBuilder("# Render Preview on the GPU compositor (5.0-Verify Q5): a 2 s stretch of the gpu-export timeline, its frames against the export's.\n");
        var problems = new List<string>();
        try
        {
            text.Append(CultureInfo.InvariantCulture, $"from 1 to 3 s at {Rate:0} fps, full size: 60 frames, the first the export's frame 30, the last its 89 (best matched within 10); its sound 2 s long (within 25 ms)\n");
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null)
            {
                Console.WriteLine("  note  no FFmpeg found (HANDPEG_FFMPEG, or %LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg or ffmpeg-arm64): the GPU preview was not made");
                return new GraphResult("gpu-preview-window", text.ToString(), problems, 0);
            }

            var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.GpuPreview");
            Directory.CreateDirectory(folder);
            var files = MakeMedia(ffmpeg, folder);
            var state = State(Timeline(files), files, Path.Combine(folder, "reference.mkv"), Stream("pcm_s16le", "")) with
            {
                FfmpegExecutable = $"\"{ffmpeg}\"",
                VideoCodecArguments = "-c:v ffv1 -pix_fmt yuv420p",
            };

            Export(state);
            var preview = Path.Combine(folder, "preview.mp4");
            var clock = Stopwatch.StartNew();
            OnThisThread(async () =>
            {
                await GpuExport.RunPreviewAsync(state, preview, 1, 3, 100, null, new Progress<FfmpegProgress>(), CancellationToken.None);
                return true;
            });

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
    internal static T OnThisThread<T>(Func<Task<T>> work)
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

    /// <summary>The export of a state as the view model makes it (GpuExport.RunAsync on this STA thread), into its OutputPath.</summary>
    internal static void Export(SequenceExportState state) =>
        OnThisThread(async () =>
        {
            await GpuExport.RunAsync(state, null, new Progress<FfmpegProgress>(), CancellationToken.None);
            return true;
        });

    private static void Describe(StringBuilder text, List<string> problems, string name, SequenceExportState state)
    {
        var command = GpuExport.CommandFor(state, out var whyNot);
        text.Append(CultureInfo.InvariantCulture, $"\n{name}:\n");
        if (command is null)
        {
            text.Append("  refused: ").Append(whyNot).Append('\n');
            problems.Add($"{name}: refused ({whyNot})");
            return;
        }

        var timing = command.Timing;
        text.Append(CultureInfo.InvariantCulture, $"  {command.Width} x {command.Height}, {timing.OutputFrames} frames at {timing.OutputRate} ({timing.CanvasFrames} on the canvas), {command.PipeFormat} piped, chapters file: {command.UsesChaptersFile}\n");
        text.Append("  ").Append(command.CommandLine).Append('\n');
        if (command.Arguments.Any(a => a is "-vf" or "-af" or "-filter_complex" or "-filter:a" or "-filter:v"))
            problems.Add($"{name}: the command writes a filter graph");
        var arguments = command.Arguments.ToList();
        if (arguments.IndexOf("pipe:0") is var pipe && (pipe < 0 || arguments.IndexOf("0:v:0") < 0) || arguments[^1] != state.OutputPath)
            problems.Add($"{name}: the picture is not mapped from the pipe (input 0), or the output is not the export's");
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

    // ----- The export, with a worker -----

    private sealed record Variant(string Name, string? Framerate);

    private static void Run(StringBuilder text, List<string> problems)
    {
        Variant[] variants = [new("at 24 fps (frames the timeline's rate drops)", "24"), new("at 60 fps (frames it repeats)", "60")];
        text.Append(CultureInfo.InvariantCulture, $"timeline: {W} x {H} at {Rate:0} fps, {Seconds:0} s: the main video in two clips (a 1 s crossfade at 2 s), a picture in picture (place and opacity keys), a title\n");
        text.Append(CultureInfo.InvariantCulture, $"  at the timeline's 30 fps: every frame the scene the preview draws at its moment (at least {FrameFloor:0} dB, on average {MeanFloor:0} dB); the sound the engine's render to 1 LSB\n");
        foreach (var variant in variants)
        {
            var timing = GpuExportTiming.Of((long)(Seconds * 1_000_000), FrameRate.Fps30, EncoderCommand.ParseFrameRate(variant.Framerate));
            text.Append(CultureInfo.InvariantCulture, $"  {variant.Name}: {timing.OutputFrames} frames, each the 30 fps export's frame its slot shows (at least {SameFrameFloor:0} dB); the sound the same\n");
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (HANDPEG_FFMPEG, or %LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg or ffmpeg-arm64): the GPU export was not made");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.GpuExport");
        Directory.CreateDirectory(folder);
        var files = MakeMedia(ffmpeg, folder);
        var reference = StateAt(files, folder, null);
        var clock = Stopwatch.StartNew();
        Export(reference);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        the 30 fps export took {clock.Elapsed.TotalSeconds:0.00} s"));
        AgainstThePreview(ffmpeg, reference, problems);
        AgainstTheEngine(ffmpeg, reference, problems);
        foreach (var variant in variants)
        {
            try
            {
                var state = StateAt(files, folder, variant.Framerate);
                Export(state);
                AgainstTheReference(ffmpeg, variant.Name, reference.OutputPath, state.OutputPath,
                    GpuExportTiming.Of((long)(Seconds * 1_000_000), FrameRate.Fps30, EncoderCommand.ParseFrameRate(variant.Framerate)), problems);
            }
            catch (Exception ex)
            {
                problems.Add($"{variant.Name}: threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    /// <summary>The PSNR (dB) an export at another rate must reach against the frame of the 30 fps export it shows: the same frame, encoded losslessly.</summary>
    private const double SameFrameFloor = 45;

    private static SequenceExportState StateAt(Files files, string folder, string? framerate) =>
        State(Timeline(files), files, Path.Combine(folder, $"gpu-{framerate ?? "same"}.mkv"), Stream("pcm_s16le", "")) with
        {
            FfmpegExecutable = $"\"{FindFfmpeg()}\"",
            VideoCodecArguments = "-c:v ffv1 -pix_fmt yuv420p",
            TargetFramerate = framerate,
        };

    /// <summary>Every frame of the 30 fps export against the scene the preview's plan draws at its moment (a compositor of its own, exact frames).</summary>
    private static void AgainstThePreview(string ffmpeg, SequenceExportState state, List<string> problems)
    {
        var size = W * H * 3;
        var exported = Read(ffmpeg, state.OutputPath, Picture);
        var frames = exported.Length / size;
        var timing = EncoderCommand.Timing(state)!.Value;
        if (frames != timing.OutputFrames)
            problems.Add($"the 30 fps export has {frames} frames, its timing {timing.OutputFrames}");

        using var client = new CompositorClient(adapter: -1, keepStandby: false);
        using var reader = new CompositorFrameReader(client);
        using var broker = new GpuResourceBroker(client, bakeOnCallingThread: true);
        var plan = GpuScenePlan.Create(state, broker);
        plan.PrepareAround(0);
        var (worst, sum, at, compared) = (double.MaxValue, 0.0, -1, 0);
        for (var f = 0; f < frames; f += 3)
        {
            if (GpuSceneDifferentialTests.Draw(client, reader, plan, timing.CanvasSeconds(timing.CanvasFrameOf(f)), problems, "the preview's scene") is not { } drawn)
                continue;
            var psnr = Psnr(exported.AsSpan(f * size, size), drawn);
            (sum, compared) = (sum + psnr, compared + 1);
            if (psnr < worst)
                (worst, at) = (psnr, f);
        }

        var mean = compared > 0 ? sum / compared : 0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        the export against the preview's scene: {compared} frames, worst {worst:0.0} dB (frame {at}), mean {mean:0.0} dB"));
        if (worst < FrameFloor)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"export frame {at} is {worst:0.0} dB from the preview's scene, under {FrameFloor:0} dB"));
        if (mean < MeanFloor)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the export is on average {mean:0.0} dB from the preview's scene, under {MeanFloor:0} dB"));
    }

    /// <summary>The export's sound against the engine's render of the same program (16-bit, so to 1 LSB), and as long as the picture.</summary>
    private static void AgainstTheEngine(string ffmpeg, SequenceExportState state, List<string> problems)
    {
        var timing = EncoderCommand.Timing(state)!.Value;
        var seconds = timing.OutputSeconds(timing.OutputFrames);
        var engine = new float[(int)Math.Round(seconds * 48000) * 2];
        AudioMixer.Render(AudioProgram.Build(state), 0, engine, 48000);
        var heard = Read(ffmpeg, state.OutputPath, Sound);
        if (Math.Abs(heard.Length / 4 - engine.Length / 2) > 1)
            problems.Add($"the export has {heard.Length / 4} sample frames of sound, the engine renders {engine.Length / 2}");
        var largest = 0;
        for (var i = 0; i < Math.Min(heard.Length / 2, engine.Length); i++)
        {
            var expected = (int)Math.Clamp(Math.Round(engine[i] * 32768.0), short.MinValue, short.MaxValue);
            largest = Math.Max(largest, Math.Abs(BitConverter.ToInt16(heard, 2 * i) - expected));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        the export's sound against the engine's render: at most {largest} LSB apart"));
        if (largest > 1)
            problems.Add($"the export's sound differs from the engine's render by up to {largest} LSB");
        if (Duration(ffmpeg, state.OutputPath) is { } duration && Math.Abs(duration - seconds) > 0.0015)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the export lasts {duration:0.000} s, its frames {seconds:0.000} s"));
    }

    /// <summary>An export at another rate: each of its frames the 30 fps export's frame its slot shows, its sound as long as its picture.</summary>
    private static void AgainstTheReference(string ffmpeg, string name, string reference, string export, GpuExportTiming timing, List<string> problems)
    {
        var size = W * H * 3;
        var (a, b) = (Read(ffmpeg, reference, Picture), Read(ffmpeg, export, Picture));
        var frames = b.Length / size;
        if (b.Length % size != 0 || frames != timing.OutputFrames)
            problems.Add($"{name}: {frames} frames, its timing {timing.OutputFrames}");

        var (worst, at) = (double.MaxValue, -1);
        for (var k = 0; k < frames; k++)
        {
            var shown = (int)timing.CanvasFrameOf(k);
            if ((shown + 1) * size > a.Length)
                break;
            var psnr = Psnr(a.AsSpan(shown * size, size), b.AsSpan(k * size, size));
            if (psnr < worst)
                (worst, at) = (psnr, k);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {name}: {frames} frames, the worst {worst:0.0} dB from the frame it shows (frame {at})"));
        if (worst < SameFrameFloor)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: frame {at} is {worst:0.0} dB from the 30 fps export's frame it shows, under {SameFrameFloor:0} dB"));

        var heard = Read(ffmpeg, export, Sound).Length / 4 / 48000.0;
        if (Math.Abs(heard - timing.OutputSeconds(timing.OutputFrames)) > 0.002)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {heard:0.000} s of sound for {timing.OutputSeconds(timing.OutputFrames):0.000} s of picture"));
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

    internal static string? FindFfmpeg()
    {
        foreach (var candidate in FfmpegCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Where the tests look for an FFmpeg, in order: HANDPEG_FFMPEG when it is set (a portable copy's
    /// portable\deps\ffmpeg-arm64\ffmpeg.exe, say), then HandPeg's own tool folders: the ARM64 build's ffmpeg-arm64 first
    /// on an ARM64 PC (DependencyUpdater.ToolFolder), the x64 build's ffmpeg after it.
    /// </summary>
    internal static IEnumerable<string> FfmpegCandidates()
    {
        if (Environment.GetEnvironmentVariable("HANDPEG_FFMPEG") is { Length: > 0 } named)
            yield return named;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folders = DependencyUpdater.IsArm64 ? new[] { "ffmpeg-" + DependencyUpdater.Arm64Variant, "ffmpeg" } : ["ffmpeg"];
        foreach (var data in new[] { "HandPeg-Data", "HandPeg" })
        {
            foreach (var folder in folders)
                yield return Path.Combine(local, data, "deps", folder, "ffmpeg.exe");
        }
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
        ContentKind = kind, SourcePath = file, AudioStreamIndex = kind == LayerKind.MainVideo ? 0 : -1, Start = start, End = end, MediaOffset = offset, SourceDuration = 5, State = look,
    };

    /// <summary>The main file's first audio stream as the Audio tab kept it, encoded as asked.</summary>
    private static LegacyStream Stream(string codec, string bitrate) => new(0, "Track 1", codec, bitrate);

    /// <summary>
    /// The state of a document of the 5.1 kinds (its main video's clips) as a project opened now compiles it (5.2-AuditB,
    /// LegacyFixtures.Opened): the main video's clips made clips of its file in the Center box (the whole frame), its streams on
    /// Sub-Lanes and the outputs they leave; with no stream, nothing of the main video is heard.
    /// </summary>
    private static SequenceExportState State(TimelineDocument document, Files files, string output, params LegacyStream[] streams) => LegacyFixtures.Opened(new SequenceExportState
    {
        Timeline = document,
        SourceFrameRate = Rate,
        RequestedSize = (document.Width, document.Height),
        OutputSize = (document.Width, document.Height),
        Container = Path.GetExtension(output).TrimStart('.'),
        OutputPath = output,
        CaptionLayer = Layer.FromState(new LayerState
        {
            Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
            SourceWidth = 0.25, SourceHeight = 0.25,
        }, W, H, document.Width, document.Height),
        MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [files.Main] = 0, [files.Inset] = 0 },
    }, new LegacyMain(files.Main) { SourceWidth = W, SourceHeight = H, MediaSeconds = 5, CenterRect = (0, 0, W, H), Streams = streams });
}
