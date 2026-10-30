using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegGoldenTests;

/// <summary>
/// The GPU scene against the FFmpeg graph (Pass 5.0-AudioAnd3D, run in 5.0-Verify): timelines written by hand, each
/// exported by FilterGraphBuilder's own command (FFmpeg, lossless, from real clips made here with HandPeg's FFmpeg) and
/// drawn by a real compositor worker from <see cref="GpuScenePlan"/> (this program started again with
/// --compositor-worker), compared frame for frame at sampled moments by PSNR. The cases: a plain main video, a rider with
/// keys of its own, a game style (a HUD cut-out with a frame mask over the blurred background), a picture in picture from
/// another file (cropped, turned, its opacity keyed), a transition, a text clip, a masked layer and an adjustment layer.
/// Each case's threshold is in the golden file; the measured numbers go to the console (they differ by card and driver).
/// <para>Before it, with no worker at all (gpu-scene-plan): every scene the plan writes for the same timelines at the same
/// moments, layer by layer, as text (what is drawn, from where, where, how, with which passes), and the proof that
/// evaluating a plan allocates nothing (a thousand evaluations of each moment, measured by the collector's own count).</para>
/// </summary>
public static class GpuSceneDifferentialTests
{
    private const int W = 320, H = 180;
    private const double Rate = 30;

    /// <summary>One timeline, its moments, and the PSNR (dB) every one of them must reach.</summary>
    private sealed record Case(string Name, SequenceExportState State, double[] Moments, double Threshold);

    public static IEnumerable<GraphResult> All()
    {
        yield return Plans();

        var text = new StringBuilder("# The GPU scene (GpuScenePlan) against the FFmpeg export graph of the same timeline, at sampled moments (PSNR, dB).\n");
        var problems = new List<string>();
        try
        {
            Run(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("gpu-scene-differential", text.ToString(), problems, 0);
    }

    // ----- The plans, without a worker -----

    private static GraphResult Plans()
    {
        var text = new StringBuilder("# GpuScenePlan's scenes, layer by layer (no worker): what the GPU engine is told to draw at sampled moments; Evaluate allocates nothing.\n");
        var problems = new List<string>();
        try
        {
            using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
            var files = new Files("D:/media/main.mp4", "D:/media/inset.mp4", "D:/media/mask.png");
            var (layers, infos) = (new GpuLayerDescriptor[CompositorProtocol.MaxLayers], new GpuLayerInfo[CompositorProtocol.MaxLayers]);
            foreach (var c in Cases(files))
            {
                var plan = GpuScenePlan.Create(c.State, broker);
                text.Append(string.Create(CultureInfo.InvariantCulture, $"\n{c.Name}: {plan.CanvasWidth} x {plan.CanvasHeight}, {plan.SequenceSeconds:0.###} s, {plan.EntryCount} entries\n"));
                var places = new Dictionary<long, float>();
                var depths = new HashSet<int>();
                foreach (var moment in c.Moments)
                {
                    var header = default(FrameSceneHeader);
                    var count = plan.Evaluate(moment, ref header, layers);
                    var described = plan.LayerInfoAt(moment, infos);
                    text.Append(string.Create(CultureInfo.InvariantCulture, $"  at {moment:0.###} s: {count} layer(s)\n"));
                    for (var i = 0; i < count; i++)
                        text.Append("    ").Append(Describe(layers[i], i < described ? infos[i] : default)).Append('\n');
                    if (described != count || header.ActiveLayerCount != count || header.CanvasWidth != plan.CanvasWidth)
                        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{c.Name} at {moment} s: {count} layers written, {described} described, the header says {header.ActiveLayerCount}"));

                    // The 3D view's places (5.0-Release): each clip at one place whatever else is on, in the order drawn, the
                    // stack as deep at every moment.
                    var depth = plan.PlaceLayers(layers, count);
                    depths.Add(depth);
                    for (var i = 0; i < count; i++)
                    {
                        var (key, place) = (layers[i].ClipKey, layers[i].ZDepth);
                        if (places.TryGetValue(key, out var known) && known != place || i > 0 && place < layers[i - 1].ZDepth || place >= depth)
                            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{c.Name} at {moment} s: layer {i} at place {place} (before {known}, depth {depth})"));
                        places[key] = place;
                    }

                    var placing = GC.GetAllocatedBytesForCurrentThread();
                    for (var k = 0; k < 100; k++)
                        plan.PlaceLayers(layers, count);
                    if (GC.GetAllocatedBytesForCurrentThread() - placing is > 0 and var placed)
                        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{c.Name} at {moment} s: PlaceLayers allocated {placed} bytes in 100 calls"));

                    // Allocation-free, as the frame pump needs it: a thousand evaluations of the moment.
                    plan.Evaluate(moment, ref header, layers);
                    // The clock first: its own 40 bytes are not the plan's.
                    var clock = Stopwatch.StartNew();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var k = 0; k < 1000; k++)
                        plan.Evaluate(moment + k * 1e-6, ref header, layers);
                    var (allocated, micros) = (GC.GetAllocatedBytesForCurrentThread() - before, clock.Elapsed.TotalMilliseconds);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {c.Name} at {moment:0.###} s: {micros:0.0} us an evaluation, {allocated} bytes in 1000"));
                    if (allocated > 0)
                        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{c.Name} at {moment} s: Evaluate allocated {allocated} bytes in 1000 calls"));
                }

                text.Append(string.Create(CultureInfo.InvariantCulture, $"  3D places: {places.Values.Distinct().Count()} used of a stack {string.Join(" / ", depths)} deep\n"));
                if (depths.Count != 1)
                    problems.Add($"{c.Name}: the 3D stack's depth changed from moment to moment ({string.Join(", ", depths)})");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-scene-plan", text.ToString(), problems, 0);
    }

    /// <summary>A layer as the worker is told it (no clip key: clips' ids are new for every run).</summary>
    private static string Describe(in GpuLayerDescriptor d, GpuLayerInfo info)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{(GpuSourceKind)d.SourceKind}");
        if (d.SourceKind == (int)GpuSourceKind.Media)
            line.Append(CultureInfo.InvariantCulture, $" source {d.SourceId} at {d.MediaSeconds:0.000}");
        if (d.StillResourceId >= 0)
            line.Append(CultureInfo.InvariantCulture, $" still {d.StillResourceId}");
        line.Append(CultureInfo.InvariantCulture, $" box ({d.X:0.0}, {d.Y:0.0}) {d.Width:0.0} x {d.Height:0.0}, crop ({d.CropLeft:0.000}, {d.CropTop:0.000})-({d.CropRight:0.000}, {d.CropBottom:0.000})");
        if (d.Rotation != 0)
            line.Append(CultureInfo.InvariantCulture, $", turned {d.Rotation:0.0} about ({d.AnchorX:0.00}, {d.AnchorY:0.00})");
        line.Append(CultureInfo.InvariantCulture, $", opacity {d.Opacity:0.000}");
        if (d.BlendMode != 0)
            line.Append(CultureInfo.InvariantCulture, $", blend {d.BlendMode}");
        if ((d.Flags & ~GpuLayerDescriptor.FlagVisible) != 0)
            line.Append(CultureInfo.InvariantCulture, $", flags {d.Flags}");
        if (d.BlurFillRadius > 0)
            line.Append(CultureInfo.InvariantCulture, $", blur {d.BlurFillRadius:0}/{d.BlurFillPasses}/{d.BlurFillDim:0.00}");
        if (d.MaskResourceId >= 0)
            line.Append(CultureInfo.InvariantCulture, $", mask {d.MaskResourceId}");
        for (var k = 0; k < d.EffectCount; k++)
        {
            var p = d.Effects[k];
            line.Append(CultureInfo.InvariantCulture, $"{(k == 0 ? ": " : ", ")}{p.Slot}({p.P0:0.###} {p.P1:0.###} {p.P2:0.###} {p.P3:0.###})");
        }

        if (info.Name is { Length: > 0 })
            line.Append(CultureInfo.InvariantCulture, $"  [{info.Name}, track {info.TrackIndex}{(info.HasMask ? ", masked" : "")}]");
        return line.ToString();
    }

    // ----- The plans against the export -----

    private static void Run(StringBuilder text, List<string> problems)
    {
        var ffmpeg = FindFfmpeg();
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.GpuScene");
        Directory.CreateDirectory(folder);
        var media = ffmpeg is null ? null : MakeMedia(ffmpeg, folder);
        var cases = Cases(media ?? new Files(Path.Combine(folder, "main.mp4"), Path.Combine(folder, "inset.mp4"), Path.Combine(folder, "mask.png")));

        foreach (var c in cases)
            text.Append(string.Create(CultureInfo.InvariantCulture, $"  {c.Name}: {c.Moments.Length} moments, each at least {c.Threshold:0} dB of luma (2 x 2 blocks)\n"));
        if (ffmpeg is null || media is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the GPU scene was not compared");
            return;
        }

        using var client = new CompositorClient(adapter: -1, keepStandby: false);
        using var reader = new CompositorFrameReader(client);
        using var broker = new GpuResourceBroker(client, bakeOnCallingThread: true);
        foreach (var c in cases)
        {
            var exported = Export(ffmpeg, c, folder);
            var plan = GpuScenePlan.Create(c.State, broker);
            plan.PrepareAround(0);
            var size = c.State.Timeline.Width * c.State.Timeline.Height * 3;
            foreach (var moment in c.Moments)
            {
                var frame = (int)Math.Round(moment * Rate);
                if (exported is null || (frame + 1) * size > exported.Length)
                {
                    problems.Add($"{c.Name}: the export has no frame {frame}");
                    continue;
                }

                if (Draw(client, reader, plan, frame / Rate, problems, c.Name) is not { } drawn)
                    continue;

                var psnr = Psnr(exported.AsSpan(frame * size, size), drawn);
                var luma = LumaPsnr(exported.AsSpan(frame * size, size), drawn, c.State.Timeline.Width);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {c.Name} at {moment:0.###} s: luma {luma:0.0} dB (every channel, every pixel: {psnr:0.0} dB)"));
                if (luma < c.Threshold)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{c.Name} at {moment:0.###} s: luma {luma:0.0} dB, under {c.Threshold:0} dB"));

                    // Both pictures beside the media (PPM), to be looked at.
                    var stem = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{c.Name.Replace(' ', '-')}-{moment:0.###}"));
                    WritePpm(stem + "-ffmpeg.ppm", exported.AsSpan(frame * size, size), c.State.Timeline.Width, c.State.Timeline.Height);
                    WritePpm(stem + "-gpu.ppm", drawn, c.State.Timeline.Width, c.State.Timeline.Height);
                }
            }
        }
    }

    // ----- The two pictures -----

    /// <summary>The case exported by FilterGraphBuilder's own command, lossless, then read back as RGB24 frames (BT.709, limited range).</summary>
    private static byte[]? Export(string ffmpeg, Case c, string folder)
    {
        var output = Path.Combine(folder, c.Name.Replace(' ', '-') + ".mkv");
        var state = c.State with
        {
            FfmpegExecutable = $"\"{ffmpeg}\"",
            VideoCodecArguments = "-c:v ffv1 -pix_fmt yuv420p",
            OutputPath = output,
            AudioTracks = [],
        };

        var command = FilterGraphBuilder.BuildExportCommand(state);
        Task.Run(() => FfmpegRunner.RunAsync(command.CommandLine, new Progress<FfmpegProgress>(), CancellationToken.None, graphInFile: true)).GetAwaiter().GetResult();

        using var process = Process.Start(new ProcessStartInfo(ffmpeg,
            $"-hide_banner -loglevel error -i \"{output}\" -vf \"scale=in_color_matrix=bt709:in_range=tv,format=rgb24\" -f rawvideo -")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        using var frames = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(frames);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg could not read the export back: {errors.Result}");
        return frames.ToArray();
    }

    /// <summary>The plan's scene at a moment, drawn by the worker and read back as RGB24 (its canvas's size, the black background).</summary>
    private static byte[]? Draw(CompositorClient client, CompositorFrameReader reader, GpuScenePlan plan, double seconds, List<string> problems, string name)
    {
        // Twice: the exact frame may come a moment after a stand-in (Instant Seek), so the decoder is let settle and the
        // same moment drawn again. (A hair into the frame, so the decoder never takes the frame before it.)
        for (var pass = 0; pass < 2; pass++)
        {
            client.ClearScene();
            ref var header = ref client.Scene;
            var layers = MemoryMarshal.CreateSpan(ref client.Layer(0), CompositorProtocol.MaxLayers);
            plan.Evaluate(seconds + 0.0005, ref header, layers);
            var sequence = client.Publish();
            if (!client.WaitForFrame(sequence, 10_000))
            {
                problems.Add($"{name}: no frame within 10 s ({client.State}: {client.Message})");
                return null;
            }

            if (pass == 0)
                Thread.Sleep(150);
        }

        if (!reader.TryRead(out var bgra, out var width, out var height) || width != plan.CanvasWidth || height != plan.CanvasHeight)
        {
            problems.Add($"{name}: the frame could not be read back ({width} x {height})");
            return null;
        }

        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
            (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);
        return rgb;
    }

    private static void WritePpm(string path, ReadOnlySpan<byte> rgb, int width, int height)
    {
        using var file = File.Create(path);
        file.Write(System.Text.Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"P6\n{width} {height}\n255\n")));
        file.Write(rgb);
    }

    /// <summary>
    /// The PSNR of the two pictures' BT.709 luma (RGB24 of <paramref name="width"/> pixels a row), each 2 x 2 block averaged
    /// first. What the threshold holds the plan to: the chroma (FFmpeg's read-back repeats each 4:2:0 chroma sample, the
    /// worker interpolates them) and the last half pixel of a box's place (FFmpeg's overlay and scale round to whole and even
    /// pixels, the worker places boxes exactly) are told apart from a wrong picture, place, size or moment.
    /// </summary>
    private static double LumaPsnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width)
    {
        double Y(ReadOnlySpan<byte> rgb, int x, int y)
        {
            var i = (y * width + x) * 3;
            return 0.2126 * rgb[i] + 0.7152 * rgb[i + 1] + 0.0722 * rgb[i + 2];
        }

        var height = a.Length / 3 / width;
        var (sum, count) = (0.0, 0);
        for (var y = 0; y + 1 < height; y += 2)
        {
            for (var x = 0; x + 1 < width; x += 2)
            {
                var d = (Y(a, x, y) + Y(a, x + 1, y) + Y(a, x, y + 1) + Y(a, x + 1, y + 1) - Y(b, x, y) - Y(b, x + 1, y) - Y(b, x, y + 1) - Y(b, x + 1, y + 1)) / 4;
                sum += d * d;
                count++;
            }
        }

        var mse = sum / Math.Max(count, 1);
        return mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
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

    // ----- The media -----

    private sealed record Files(string Main, string Inset, string Mask);

    /// <summary>Two 4 s clips at 30 frames a second (moving test pictures, BT.709, a key frame a second), and a round mask.</summary>
    private static Files MakeMedia(string ffmpeg, string folder)
    {
        var files = new Files(Path.Combine(folder, "main.mp4"), Path.Combine(folder, "inset.mp4"), Path.Combine(folder, "mask.png"));
        const string encode = "-c:v libx264 -preset veryfast -crf 12 -g 30 -bf 2 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -an -y";
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"testsrc2=s={W}x{H}:r=30:d=4\" -vf \"scale=out_color_matrix=bt709:out_range=tv,format=yuv420p\" {encode} \"{files.Main}\"");
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"smptehdbars=s={W}x{H}:r=30:d=4,drawbox=x='mod(t*120,{W})':y=60:w=40:h=40:c=white:t=fill\" -vf \"scale=out_color_matrix=bt709:out_range=tv,format=yuv420p\" {encode} \"{files.Inset}\"");
        FfmpegOnce(ffmpeg, $"-f lavfi -i \"color=c=black:s={W}x{H}:d=1,format=gray,geq=lum='255*lt(hypot(X-{W / 2},Y-{H / 2}),70)'\" -frames:v 1 -y \"{files.Mask}\"");
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

    // ----- The cases -----

    private static IReadOnlyList<Case> Cases(Files files) =>
    [
        PlainMain(files),
        Rider(files),
        GameStyle(files),
        PictureInPicture(files),
        Transition(files),
        Words(files),
        Masked(files),
        Adjusted(files),
        BackgroundChild(files),
        AdvancedValues(files),
    ];

    private static Case PlainMain(Files files)
    {
        var document = Document(W, H);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        return new Case("plain main video", State(document, files, (0, 0, W, H)), [0.5, 1.5, 3.2], 42);
    }

    /// <summary>The main video split at 2 s, its second clip with keys of its own: a rider, cut whole from the same stream in its own motion.</summary>
    private static Case Rider(Files files)
    {
        var document = Document(W, H);
        var track = Track(document, TimelineTrackKind.Video);
        track.Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 2, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        var keyed = Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main);
        keyed.KeysScale = [new Keyframe(0, 1, EasingType.EaseInOut), new Keyframe(2, 0.6)];
        keyed.KeysX = [new Keyframe(0, 0, EasingType.Linear), new Keyframe(2, 0.3)];
        track.Clips.Add(Clip(files.Main, LayerKind.MainVideo, 2, 4, 2, keyed));
        return new Case("rider with its own keys", State(document, files, (0, 0, W, H)), [1.0, 2.5, 3.5], 22);
    }

    /// <summary>A vertical game style: the main video cropped to its middle across the frame, a HUD piece cut from it with a mask of the whole frame, the blurred background.</summary>
    private static Case GameStyle(Files files)
    {
        const int width = 180, height = 320;
        var document = Document(width, height);
        var (centerWidth, centerHeight) = (width, (int)Math.Round(width * 180.0 / 192 / 2) * 2);
        var centerY = (height - centerHeight) / 2;
        var main = Look(LayerKind.MainVideo, "Main", 0, (double)centerY / height, 1, files.Main);
        (main.CropLeft, main.CropRight) = (0.2, 0.2);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, main));

        var piece = Look(LayerKind.Video, "HUD", 0.05, 0.03, 0.6);
        (piece.SourceX, piece.SourceY, piece.SourceWidth, piece.SourceHeight) = (0.0, 0.0, 0.35, 0.3);
        (piece.CustomMask, piece.MaskFrame, piece.MaskPath, piece.MaskGrow) = (true, true, files.Mask, 2);
        Track(document, TimelineTrackKind.Video).Clips.Add(new TimelineClip { ContentKind = LayerKind.Video, SourcePath = files.Main, Start = 0, End = 4, State = piece });

        var state = State(document, files, (0, centerY, centerWidth, centerHeight)) with { HasCrop = true, CropFractions = (0.2, 0, 0.2, 0) };
        return new Case("game style (HUD cut-out, blurred background)", state, [0.7, 2.2], 27);
    }

    /// <summary>Another file's video over the main one: cropped, turned 15 degrees, its opacity keyed from 100 to 40.</summary>
    private static Case PictureInPicture(Files files)
    {
        var document = Document(W, H);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        var inset = Look(LayerKind.VideoFile, "inset", 0.5, 0.1, 0.4, files.Inset);
        (inset.CropLeft, inset.CropBottom, inset.Rotation) = (0.1, 0.2, 15);
        inset.KeysOpacity = [new Keyframe(0, 100, EasingType.Linear), new Keyframe(3, 40)];
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Inset, LayerKind.VideoFile, 0, 4, 0, inset));
        return new Case("picture in picture (crop, turn, opacity keys)", State(document, files, (0, 0, W, H)), [1.0, 2.5], 26);
    }

    /// <summary>A picture in picture wiping in from the left over its first second and dissolving out over its last.</summary>
    private static Case Transition(Files files)
    {
        var document = Document(W, H);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        var inset = Clip(files.Inset, LayerKind.VideoFile, 0, 4, 0, Look(LayerKind.VideoFile, "inset", 0.3, 0.2, 0.5, files.Inset));
        (inset.FadeInSeconds, inset.FadeInStyle, inset.FadeOutSeconds, inset.FadeOutStyle) = (1, TransitionStyle.WipeRight, 1, TransitionStyle.Dissolve);
        Track(document, TimelineTrackKind.Video).Clips.Add(inset);
        return new Case("transition (wipe in, dissolve out)", State(document, files, (0, 0, W, H)), [0.5, 2.0, 3.5], 39);
    }

    private static Case Words(Files files)
    {
        var document = Document(W, H);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        var words = new LayerState
        {
            Kind = LayerKind.Text, Name = "Title", TextContent = "GPU 5.0", FontFamily = "Arial", FontSize = 48, FontColor = "#FFFFFF",
            OutlineColor = "#000000", OutlineThickness = 2, PositionX = 0.1, PositionY = 0.1, SizeWidth = 0.5,
        };
        var layout = TextLayout.Measure(words.TextContent, words.FontFamily, words.FontSize, words.OutlineThickness);
        (words.ImageWidth, words.ImageHeight) = (layout.Width, layout.Height);
        Track(document, TimelineTrackKind.Video).Clips.Add(new TimelineClip { ContentKind = LayerKind.Text, Start = 0, End = 4, State = words });
        return new Case("text clip", State(document, files, (0, 0, W, H)), [1.0], 24);
    }

    /// <summary>A picture in picture with a round mask of its own picture, grown 2 pixels, its outline softened.</summary>
    private static Case Masked(Files files)
    {
        var document = Document(W, H);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        var inset = Look(LayerKind.VideoFile, "masked", 0.2, 0.15, 0.6, files.Inset);
        (inset.CustomMask, inset.MaskPath, inset.MaskGrow, inset.Feather, inset.FeatherRadius) = (true, files.Mask, 2, true, 8);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Inset, LayerKind.VideoFile, 0, 4, 0, inset));
        return new Case("masked layer (grown, soft edge)", State(document, files, (0, 0, W, H)), [1.5], 28);
    }

    /// <summary>An adjustment clip over the whole frame from 1 to 3 s: more contrast, half the saturation.</summary>
    private static Case Adjusted(Files files)
    {
        var document = Document(W, H);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main)));
        var look = new LayerState
        {
            Kind = LayerKind.Adjustment, Name = "Grade", PositionX = 0, PositionY = 0, SizeWidth = 1, SizeHeight = 1, LockAspectRatio = false,
            SourceWidth = 0.25, SourceHeight = 0.25,
            Effects = [Scene.Effect(EffectRegistry.Grade, 1, new Dictionary<string, object> { ["contrast"] = 1.3, ["saturation"] = 0.5 })],
        };
        Track(document, TimelineTrackKind.Adjustment).Clips.Add(new TimelineClip { ContentKind = LayerKind.Adjustment, Start = 1, End = 3, State = look });
        return new Case("adjustment layer", State(document, files, (0, 0, W, H)), [0.5, 2.0], 33);
    }

    /// <summary>
    /// The blurred background as the main video's video-only child (5.0-Release): behind the main video at half size, its
    /// radius 9 px (a fraction of a box on the GPU, rounded for boxblur), zoomed 1.5 times, and a Vignette of its own.
    /// </summary>
    private static Case BackgroundChild(Files files)
    {
        var document = Document(W, H);
        var (width, height) = (W / 2, H / 2);
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, Look(LayerKind.MainVideo, "Main", 0.25, 0.25, 0.5, files.Main)));
        var state = State(document, files, ((W - width) / 2, (H - height) / 2, width, height)) with
        {
            BlurRadius = 9, BackgroundZoom = 1.5,
            BackgroundEffects = [Scene.Effect(EffectRegistry.Vignette, 1, new Dictionary<string, object> { ["angle"] = 0.6 })],
        };
        return new Case("blurred background child (radius 9, zoom 1.5, its own vignette)", state, [0.7, 2.2], 25);
    }

    /// <summary>
    /// Effect cards' Advanced values (5.0-Release) on the main video: a Blur of 3 passes, twice as wide across as down and dimmed
    /// by 10 %, then a Vignette following the frame's shape (roundness 0) and feathered to its outer 60 % (geq in the export).
    /// </summary>
    private static Case AdvancedValues(Files files)
    {
        var document = Document(W, H);
        var look = Look(LayerKind.MainVideo, "Main", 0, 0, 1, files.Main);
        look.Effects =
        [
            Scene.Effect(EffectRegistry.Blur, 1, new Dictionary<string, object> { ["radius"] = 8.0, ["passes"] = 3.0, ["ratio"] = 2.0, ["dim"] = 10.0 }),
            Scene.Effect(EffectRegistry.Vignette, 2, new Dictionary<string, object> { ["angle"] = 0.7, ["round"] = 0.0, ["feather"] = 60.0 }),
        ];
        Track(document, TimelineTrackKind.Video).Clips.Add(Clip(files.Main, LayerKind.MainVideo, 0, 4, 0, look));
        return new Case("advanced values (a 3-pass blur wider across, dimmed; a frame-shaped feathered vignette)", State(document, files, (0, 0, W, H)), [1.0, 3.0], 30);
    }

    // ----- Building documents -----

    private static TimelineDocument Document(int width, int height) => new() { Width = width, Height = height, FrameRate = FrameRate.Fps30 };

    private static TimelineTrackModel Track(TimelineDocument document, TimelineTrackKind kind)
    {
        var track = new TimelineTrackModel { Kind = kind, TrackNumber = document.Tracks.Count(t => t.Kind == kind) + 1 };
        document.Tracks.Add(track);
        return track;
    }

    private static LayerState Look(LayerKind kind, string name, double x, double y, double width, string? file = null) => new()
    {
        Kind = kind, Name = name, PositionX = x, PositionY = y, SizeWidth = width, SourceWidth = 0.25, SourceHeight = 0.25,
        ImagePath = file ?? "", ImageWidth = file is null ? 0 : W, ImageHeight = file is null ? 0 : H, MediaDuration = file is null ? 0 : 4,
    };

    private static TimelineClip Clip(string file, LayerKind kind, double start, double end, double offset, LayerState look) => new()
    {
        ContentKind = kind, SourcePath = file, Start = start, End = end, MediaOffset = offset, SourceDuration = 4, State = look,
    };

    private static SequenceExportState State(TimelineDocument document, Files files, (int X, int Y, int Width, int Height) center) => new()
    {
        Timeline = document,
        SourceFrameRate = Rate,
        SourceWidth = W,
        SourceHeight = H,
        RequestedSize = (document.Width, document.Height),
        OutputSize = (document.Width, document.Height),
        LocalMediaPath = files.Main,
        HasSource = true,
        MainMediaSeconds = 4,
        CenterRect = center,
        FrameEngine = true,
        CaptionLayer = Layer.FromState(new LayerState
        {
            Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
            SourceWidth = 0.25, SourceHeight = 0.25,
        }, W, H, document.Width, document.Height),
        ExistingFiles = new HashSet<string>([files.Mask], StringComparer.OrdinalIgnoreCase),
        MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [files.Main] = 0, [files.Inset] = 0 },
    };
}
