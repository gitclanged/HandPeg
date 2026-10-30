using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// gpu-export-every-frame (Pass 5.0-Verify, the user's bug 1: a GPU export that only changed at the key frames). Sources
/// made the way recorders make them (60 fps with a key frame every 4 s and B-frames, H.264 in MP4 and full-range HEVC in
/// Matroska; HEVC at 420 fps on Matroska's millisecond clock), each the main video of a one-clip timeline, exported both
/// ways at 60 fps (lossless). Every frame of the GPU export must be the FFmpeg export's same frame (the best match within
/// six frames either side is that one, at 30 dB or better), and wherever FFmpeg's picture moves from one frame to the next
/// the GPU's must move too (no frame held over).
/// <para>HANDPEG_TEST_EXPORT_SOURCES ("path[@start]|..." of real recordings, 6 s from start) adds more: their numbers go
/// to the console only, so the golden text stays the same.</para>
/// </summary>
public static partial class GpuExportTests
{
    // The canvas, the size the frames are compared at, the export's rate and length.
    private const int EveryW = 640, EveryH = 360, CompareW = 320, CompareH = 180;
    private const string EveryRate = "60";
    private const double EverySeconds = 6, EveryFloor = 30, StyleFloor = 20;

    /// <summary>A source to export: made here (its problems fail the test) or a real recording (console only); as one clip, or the user's game style.</summary>
    private sealed record EverySource(string Name, string File, double Start, bool Made, bool GameStyle = false);

    // The game style's main clips (the user's export after Remove Dead Air: four pieces of the recording, each later in it),
    // seconds long and how far after the source's start each begins.
    private static readonly double[] StyleLengths = [2.25, 0.819048, 1.590952, 1.128571], StyleOffsets = [1.3, 5.75, 7.769, 10.081];

    /// <summary>gpu-export-every-frame: sources at recorders' rates and key frame intervals, exported both ways, frame by frame.</summary>
    public static GraphResult EveryFrame()
    {
        var text = new StringBuilder("# The GPU export of recorders' sources (long key frame intervals, B-frames), every frame against FFmpeg's export.\n");
        var problems = new List<string>();
        try
        {
            RunEveryFrame(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-export-every-frame", text.ToString(), problems, 0);
    }

    private static void RunEveryFrame(StringBuilder text, List<string> problems)
    {
        text.Append(CultureInfo.InvariantCulture, $"canvas {EveryW} x {EveryH}, {EverySeconds:0} s exported at {EveryRate} fps; frames compared at {CompareW} x {CompareH}, each the same frame as FFmpeg's at {EveryFloor:0} dB or better (the game style {StyleFloor:0} dB), none held over\n");
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the GPU export's frames were not checked");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.GpuExportFrames");
        Directory.CreateDirectory(folder);
        var sources = new List<EverySource>
        {
            new("H.264, 60 fps, a key frame every 4 s, B-frames (MP4)", Path.Combine(folder, "h264-60.mp4"), 0, true),
            new("HEVC, 60 fps, full range, a key frame every 4 s, B-frames (Matroska)", Path.Combine(folder, "hevc-60.mkv"), 0, true),
            new("HEVC, 420 fps on a millisecond clock, a key frame every 240 frames (Matroska)", Path.Combine(folder, "hevc-420.mkv"), 0, true),
            new("the game style after Remove Dead Air (4 main clips, 3 HUD cut-outs with masks, the blurred background; 1080 x 1920 shape) on the HEVC 60 fps source", Path.Combine(folder, "hevc-60.mkv"), 0, true, true),
            new("the game style after Remove Dead Air on the 420 fps source", Path.Combine(folder, "hevc-420.mkv"), 0, true, true),
        };
        MakeEverySources(ffmpeg, sources);
        foreach (var source in sources)
            text.Append("  ").Append(source.Name).Append('\n');

        // Real recordings, for a look at the user's own media: console only.
        foreach (var entry in (Environment.GetEnvironmentVariable("HANDPEG_TEST_EXPORT_SOURCES") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {

            var style = entry.EndsWith("!style", StringComparison.OrdinalIgnoreCase);
            var spec = style ? entry[..^6] : entry;
            var at = spec.LastIndexOf('@');
            var (file, start) = at > 2 && double.TryParse(spec[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? (spec[..at], s) : (spec, 0.0);
            sources.Add(new EverySource($"{Path.GetFileName(file)} from {start:0.###} s{(style ? ", the game style" : "")}", file, start, false, style));
        }

        // HANDPEG_TEST_EXPORT_ONLY=1: only the real recordings (a quicker look while hunting).
        if (Environment.GetEnvironmentVariable("HANDPEG_TEST_EXPORT_ONLY") == "1")
            sources.RemoveAll(s => s.Made);

        foreach (var source in sources)
        {
            try
            {
                EveryFrameOf(ffmpeg, folder, source, problems);
            }
            catch (Exception ex)
            {
                problems.Add($"{source.Name}: threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    private static void MakeEverySources(string ffmpeg, List<EverySource> sources)
    {
        const string tv = "-vf \"scale=out_color_matrix=bt709:out_range=tv,format=yuv420p\" -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv";
        const string pc = "-vf \"scale=out_color_matrix=bt709:out_range=pc,format=yuvj420p\" -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range pc";
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"testsrc2=s=1280x720:r=60:d=7\" {tv} -c:v libx264 -preset veryfast -crf 16 -g 240 -keyint_min 240 -bf 2 -an -y \"{sources[0].File}\"");
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"testsrc2=s=1280x720:r=60:d=13\" {pc} -c:v libx265 -preset veryfast -crf 16 -x265-params keyint=240:min-keyint=240:bframes=2:scenecut=0:log-level=error -an -y \"{sources[1].File}\"");
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"testsrc2=s=640x360:r=420:d=13\" {pc} -c:v libx265 -preset ultrafast -crf 18 -x265-params keyint=240:min-keyint=240:bframes=2:scenecut=0:log-level=error -an -y \"{sources[2].File}\"");

        // The HUD cut-outs' mask: the whole frame kept but a band down the middle (a mask of the frame, as the game styles' are).
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"color=c=white:s=1280x720:d=1,format=gray,drawbox=x=560:y=0:w=160:h=720:c=black:t=fill\" -frames:v 1 -y \"{StyleMask(Path.GetDirectoryName(sources[0].File)!)}\"");
    }

    private static string StyleMask(string folder) => Path.Combine(folder, "hud-mask.png");

    /// <summary>One source exported by FFmpeg's graph and by the GPU compositor (6 s at 60 fps, FFV1), and their frames compared.</summary>
    private static void EveryFrameOf(string ffmpeg, string folder, EverySource source, List<string> problems)
    {
        var (width, height, fps, seconds) = Probe(ffmpeg, source.File);

        // A real recording is looked at the way the app does (its probe, its I-frame list, which the worker seeks with).
        if (!source.Made)
        {
            Task.Run(async () =>
            {
                await MediaProbe.ProbeAsync(source.File, CancellationToken.None);
                var keys = await FfmpegRunner.GetIFramesAsync(source.File, CancellationToken.None);
                if (keys.Count == 0)
                {
                    // The test's own settings know no ffprobe: the app's own listing (the packets' K flags), made here.
                    keys = ListIFrames(ffmpeg, source.File);
                    MediaProbe.NoteIFrames(source.File, keys);
                }
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {source.Name}: {keys.Count} I-frames listed ({string.Join(" ", keys.Take(5).Select(k => k.ToString("0.###", CultureInfo.InvariantCulture)))} ...), video start {MediaProbe.KnownVideoStart(source.File)}"));
            }).GetAwaiter().GetResult();
        }
        var length = source.GameStyle ? StyleLengths.Sum() : Math.Min(EverySeconds, seconds - source.Start - 0.05);
        if (length < 1 || source.Start + StyleOffsets[^1] + StyleLengths[^1] > seconds && source.GameStyle)
        {
            problems.Add($"{source.Name}: too short ({seconds:0.###} s)");
            return;
        }

        var slug = GpuHashOf(source.File + "@" + source.Start.ToString(CultureInfo.InvariantCulture) + (source.GameStyle ? "!style" : ""));
        var state = EveryState(ffmpeg, folder, source, width, height, fps, seconds, length);
        var command = FilterGraphBuilder.BuildExportCommand(state);
        Task.Run(() => FfmpegRunner.RunAsync(command.CommandLine, new Progress<FfmpegProgress>(), CancellationToken.None, graphInFile: true)).GetAwaiter().GetResult();

        var gpuState = state with { OutputPath = Path.Combine(folder, $"gpu-{slug}.mkv") };
        if (GpuExport.WhyNot(gpuState) is { } refused)
        {
            problems.Add($"{source.Name}: refused ({refused})");
            return;
        }

        var client = GpuExport.StartCompositor(out var why);
        if (client is null)
        {
            problems.Add($"{source.Name}: {why}");
            return;
        }

        var export = GpuExport.Prepare(gpuState, client, out why);
        if (export is null)
        {
            client.Dispose();
            problems.Add($"{source.Name}: not prepared ({why})");
            return;
        }

        var clock = Stopwatch.StartNew();
        (int Opens, int Seeks, int Decoded, int Stretches, int LateLandings, int Missing) counters;
        using (export)
        {
            Task.Run(() => export.RunAsync(new Progress<FfmpegProgress>(), CancellationToken.None)).GetAwaiter().GetResult();
            counters = client.DecodeCounters;
        }

        var took = clock.Elapsed.TotalSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        {source.Name}: {width} x {height} at {fps:0.###} fps; the GPU export took {took:0.00} s for {export.FramesPiped} frames ({export.FramesPiped / Math.Max(took, 1e-3):0.0} a second); decoders opened {counters.Opens}, seeks {counters.Seeks}, frames decoded {counters.Decoded}, stretched {counters.Stretches}, late landings {counters.LateLandings}, missing {counters.Missing}"));
        if (source.Made && counters.Missing > 0)
            problems.Add($"{source.Name}: {counters.Missing} moments had no frame");

        CompareEveryFrame(ffmpeg, source, state.OutputPath, gpuState.OutputPath, export.Command.Timing.OutputFrames, source.GameStyle ? (CompareH, CompareW) : (CompareW, CompareH), problems);
    }

    /// <summary>
    /// A source's export state: as one clip filling the frame, or the user's game style (a vertical frame); FFmpeg's export
    /// to <paramref name="folder"/> (FFV1 at <see cref="EveryRate"/>, named by the source).
    /// </summary>
    private static SequenceExportState EveryState(string ffmpeg, string folder, EverySource source, int width, int height, double fps, double seconds, double length)
    {
        var slug = GpuHashOf(source.File + "@" + source.Start.ToString(CultureInfo.InvariantCulture) + (source.GameStyle ? "!style" : ""));
        var (canvasW, canvasH) = source.GameStyle ? (EveryH, EveryW) : (EveryW, EveryH);
        var (document, center, masks) = source.GameStyle
            ? StyleTimeline(source, width, height, seconds, fps, canvasW, canvasH)
            : (OneClip(source, width, height, seconds, fps, length), (0, 0, EveryW, EveryH), (IReadOnlySet<string>)new HashSet<string>());

        return new SequenceExportState
        {
            Timeline = document,
            SourceFrameRate = fps,
            SourceWidth = width,
            SourceHeight = height,
            RequestedSize = (canvasW, canvasH),
            OutputSize = (canvasW, canvasH),
            LocalMediaPath = source.File,
            HasSource = true,
            MainMediaSeconds = seconds,
            CenterRect = center,
            FrameEngine = true,
            Container = "mkv",
            OutputPath = Path.Combine(folder, $"ffmpeg-{slug}.mkv"),
            FfmpegExecutable = $"\"{ffmpeg}\"",
            VideoCodecArguments = "-c:v ffv1 -pix_fmt yuv420p",
            TargetFramerate = EveryRate,
            AudioTracks = [],
            CaptionLayer = Layer.FromState(new LayerState
            {
                Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
                SourceWidth = 0.25, SourceHeight = 0.25,
            }, canvasW, canvasH, canvasW, canvasH),
            ExistingFiles = masks,
            MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [source.File] = 0 },
        };

    }

    /// <summary>The source as the main video in one clip filling the frame.</summary>
    private static TimelineDocument OneClip(EverySource source, int width, int height, double seconds, double fps, double length)
    {
        var document = new TimelineDocument { Width = EveryW, Height = EveryH, FrameRate = FrameRate.OfSource(fps) };
        var look = new LayerState
        {
            Kind = LayerKind.MainVideo, Name = "Main", PositionX = 0, PositionY = 0, SizeWidth = 1, SourceWidth = 0.25, SourceHeight = 0.25,
            ImagePath = source.File, ImageWidth = width, ImageHeight = height, MediaDuration = seconds,
        };
        Track(document, TimelineTrackKind.Video).Clips.Add(new TimelineClip
        {
            ContentKind = LayerKind.MainVideo, SourcePath = source.File, Start = 0, End = length, MediaOffset = source.Start, SourceDuration = seconds, State = look,
        });
        return document;
    }

    /// <summary>
    /// The user's export (Overwatch's vertical style after Remove Dead Air): the main video zoomed across a vertical frame in
    /// four clips from later and later in the recording, three HUD pieces cut from it (children of each clip, with a mask of
    /// the frame, feathered, the mask and edge effects), and the blurred background behind.
    /// </summary>
    private static (TimelineDocument Document, (int X, int Y, int Width, int Height) Center, IReadOnlySet<string> Masks) StyleTimeline(
        EverySource source, int width, int height, double seconds, double fps, int canvasW, int canvasH)
    {
        var document = new TimelineDocument { Width = canvasW, Height = canvasH, FrameRate = FrameRate.OfSource(fps) };
        var mainTrack = Track(document, TimelineTrackKind.Video);
        var mask = source.Made ? StyleMask(Path.GetDirectoryName(source.File)!) : FindUserMask() ?? StyleMask(Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.GpuExportFrames"));
        (string Name, double X, double Y, double W, double H, double PX, double PY, double Size, int Feather, int Grow)[] pieces =
        [
            ("Overwatch: Killfeed", 0.6564, 0.0109, 0.3378, 0.2608, -0.169, 0.013, 1.1503, 16, 19),
            ("Overwatch: Health & Name", 0.0014, 0.7766, 0.2534, 0.2088, -0.0103, 0.7434, 0.5591, 4, 2),
            ("Overwatch: Abilities & Ammo", 0.7278, 0.7660, 0.2722, 0.2340, 0.4679, 0.7421, 0.5828, 2, 0),
        ];
        var children = pieces.Select(_ => Track(document, TimelineTrackKind.Video)).ToArray();
        foreach (var child in children)
            child.ParentTrackId = mainTrack.Id;

        var at = 0.0;
        for (var i = 0; i < StyleLengths.Length; i++)
        {
            var main = new TimelineClip
            {
                ContentKind = LayerKind.MainVideo, SourcePath = source.File, Start = at, End = at + StyleLengths[i], MediaOffset = source.Start + StyleOffsets[i],
                SourceDuration = seconds,
                State = new LayerState
                {
                    Kind = LayerKind.MainVideo, Name = "Main", PositionX = -0.35, PositionY = 0.23125, SizeWidth = 1.7, SourceWidth = 0.25, SourceHeight = 0.25,
                    ImagePath = source.File, ImageWidth = width, ImageHeight = height, MediaDuration = seconds, ShadowDistance = 14, ShadowSoftness = 2,
                },
            };
            mainTrack.Clips.Add(main);
            for (var k = 0; k < pieces.Length; k++)
            {
                var piece = pieces[k];
                children[k].Clips.Add(new TimelineClip
                {
                    ContentKind = LayerKind.Video, SourcePath = source.File, Start = at, End = at + StyleLengths[i], ParentClipId = main.Id,
                    State = new LayerState
                    {
                        Kind = LayerKind.Video, Name = piece.Name, SourceX = piece.X, SourceY = piece.Y, SourceWidth = piece.W, SourceHeight = piece.H,
                        PositionX = piece.PX, PositionY = piece.PY, SizeWidth = piece.Size, Feather = true, FeatherRadius = piece.Feather,
                        ShadowDistance = 14, ShadowSoftness = 2, CustomMask = true, MaskPath = mask, MaskFrame = true, MaskGrow = piece.Grow,
                        Effects = [Scene.Effect(HandPegApp.Models.Effects.EffectRegistry.Mask, 10 + k), Scene.Effect(HandPegApp.Models.Effects.EffectRegistry.Edges, 20 + k)],
                    },
                });
            }

            at += StyleLengths[i];
        }

        // The Center controls' box: 1.7 of the frame's width, at -0.35 and 0.23125 of it.
        var centerWidth = (int)Math.Round(canvasW * 1.7 / 2) * 2;
        var centerHeight = (int)Math.Round(centerWidth * (double)height / width / 2) * 2;
        return (document, ((int)Math.Round(-0.35 * canvasW), (int)Math.Round(0.23125 * canvasH), centerWidth, centerHeight), new HashSet<string>([mask], StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The user's own Overwatch mask, when it is on this PC (real recordings only).</summary>
    private static string? FindUserMask()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HandPeg", "Masks");
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "owlayermask1*.png").FirstOrDefault() : null;
    }

    private static void CompareEveryFrame(string ffmpeg, EverySource source, string reference, string gpu, long expectedFrames, (int W, int H) compare, List<string> problems)
    {
        var how = $"-map 0:v:0 -vf \"scale={compare.W}:{compare.H}:flags=area:in_color_matrix=bt709:in_range=tv,format=rgb24\" -f rawvideo -";
        var size = compare.W * compare.H * 3;
        var (a, b) = (Read(ffmpeg, reference, how), Read(ffmpeg, gpu, how));
        var (framesA, framesB) = (a.Length / size, b.Length / size);
        void Problem(string what)
        {
            // Real recordings report, the made sources fail.
            if (source.Made)
                problems.Add($"{source.Name}: {what}");
            else
                Console.WriteLine($"        {source.Name}: {what}");
        }

        if (framesA != framesB || framesB != expectedFrames)
            Problem($"FFmpeg's export has {framesA} frames, the GPU's {framesB}, its timing {expectedFrames}");

        var frames = Math.Min(framesA, framesB);
        var (worst, worstAt, sum) = (double.MaxValue, -1, 0.0);
        var (held, wrong, firstHeld, firstWrong, bestOfWrong) = (0, 0, -1, -1, -1);
        for (var k = 0; k < frames; k++)
        {
            var mine = b.AsSpan(k * size, size);
            var psnr = Psnr(a.AsSpan(k * size, size), mine);
            sum += psnr;
            if (psnr < worst)
                (worst, worstAt) = (psnr, k);

            var (best, bestAt) = (double.MaxValue, k);
            for (var j = Math.Max(0, k - 6); j < Math.Min(framesA, k + 7); j++)
            {
                var mse = Mse(a.AsSpan(j * size, size), mine);
                if (mse < best - 1e-9 || (Math.Abs(mse - best) <= 1e-9 && j == k))
                    (best, bestAt) = (mse, j);
            }

            var same = Mse(a.AsSpan(k * size, size), mine);
            if (bestAt != k && same > 1 && best < same * 0.5)
            {
                wrong++;
                if (firstWrong < 0)
                    (firstWrong, bestOfWrong) = (k, bestAt);
            }

            if (k > 0)
            {
                var theirs = Mse(a.AsSpan((k - 1) * size, size), a.AsSpan(k * size, size));
                var ours = Mse(b.AsSpan((k - 1) * size, size), mine);
                // A frame held over is the same picture drawn again (all but bit-identical); FFmpeg's own first frame after
                // a cut can be a picture between the two clips, which the GPU's ordinary next step must not count against.
                if (theirs > 1 && ours < Math.Min(theirs * 0.05, 0.05))
                {
                    held++;
                    if (firstHeld < 0)
                        firstHeld = k;
                }
            }
        }

        var mean = frames > 0 ? sum / frames : 0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        {source.Name}: {frames} frames, worst {worst:0.0} dB (frame {worstAt}), mean {mean:0.0} dB; {held} held over (first {firstHeld}), {wrong} not the same frame (first {firstWrong}, like {bestOfWrong})"));
        if (held > 0)
            Problem($"{held} frames held over where FFmpeg's picture moves (the first at frame {firstHeld})");
        if (wrong > 0)
            Problem($"{wrong} frames are another frame of FFmpeg's (the first, frame {firstWrong}, looks like its frame {bestOfWrong})");
        // The game style's blurred background and its feathered, shadowed pieces are drawn a little differently from FFmpeg's
        // filters (gpu-scene-differential holds them to 20 dB); the frames themselves must still be the same ones.
        var floor = source.GameStyle ? StyleFloor : EveryFloor;
        if (worst < floor)
            Problem(string.Create(CultureInfo.InvariantCulture, $"frame {worstAt} at {worst:0.0} dB, under {floor:0} dB"));
    }

    private static double Mse(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }

        return (double)sum / Math.Max(a.Length, 1);
    }

    /// <summary>A source's first video stream's size and rate, and its length, as ffprobe (beside FFmpeg) gives them.</summary>
    private static (int Width, int Height, double Fps, double Seconds) Probe(string ffmpeg, string file)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        using var process = Process.Start(new ProcessStartInfo(ffprobe, $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate:format=duration -of default=nw=1 \"{file}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        var said = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var values = said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        var rate = values.GetValueOrDefault("avg_frame_rate", "30/1").Split('/');
        var fps = rate.Length == 2 && double.TryParse(rate[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.TryParse(rate[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? n / d : 30;
        return (int.Parse(values["width"], CultureInfo.InvariantCulture), int.Parse(values["height"], CultureInfo.InvariantCulture), fps,
            double.Parse(values["duration"], NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    /// <summary>A file's I-frame times as the app lists them (FfmpegRunner.GetIFramesAsync: the packets flagged K).</summary>
    private static List<double> ListIFrames(string ffmpeg, string file)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        using var process = Process.Start(new ProcessStartInfo(ffprobe, $"-v error -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 -i \"{file}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        var keys = new List<double>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            var comma = line.IndexOf(',');
            if (comma > 0 && comma + 1 < line.Length && line[comma + 1] == 'K' && double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                keys.Add(seconds);
        }

        process.WaitForExit();
        keys.Sort();
        return keys;
    }

    private static string GpuHashOf(string text)
    {
        var hash = 1469598103934665603UL;
        foreach (var c in text)
            hash = (hash ^ c) * 1099511628211UL;
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
