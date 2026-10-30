using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// An effect added to a clip from the Effects pane, followed up the real path as far as it runs without a window (Pass
/// 5.0-Verify, the user's bug 2): the app's own view model, hosted with no window and no project file, is given a timeline
/// of two clips of the main video (the anchor, drawn in one piece, and a second one that becomes a rider once its look is
/// its own) and a video from another file over them. Each clip is selected as a press on its block selects it (KeyLayer),
/// and the clip the Effects pane edits (ActiveKeyLayer) is given a card and a value the way the pane's Add and its slider
/// write them (a checkpoint, then the change inside Layer.Echo). The state the GPU engine is pushed (LivePreviewState, which
/// writes the views' looks into their clips) is planned (GpuScenePlan.Create) and evaluated inside each clip: the layer the
/// worker is told to draw for that clip must carry the card's passes. Last, a clip split (as Remove Dead Air splits) keeps
/// its selection on a view that is drawn, and an effect on the new half reaches the scene too.
/// </summary>
public static class GpuSceneClipEffectTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int W = 320, H = 180;

    public static GraphResult Run()
    {
        var text = new StringBuilder("# An effect added from the Effects pane to a selected clip, through the view model (FlushLooks) into the GPU scene (GpuScenePlan).\n");
        var problems = new List<string>();
        try
        {
            Follow(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-scene-clip-effect", text.ToString(), problems, 0);
    }

    private static void Follow(StringBuilder text, List<string> problems)
    {
        // Nothing the view model would start on its own is wanted here (it only would if this thread's queue were run).
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports) = (false, false);

        // Files that are not there yet: the view model's part only names them, and nothing is probed or pictured. They are made
        // (when HandPeg's FFmpeg is installed) once it is done, for a real worker to draw the scenes planned on the way (Draw).
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.ClipEffect", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var (main, inset) = (Path.Combine(folder, "main.mp4"), Path.Combine(folder, "inset.mp4"));

        var vm = new MainViewModel();
        vm.SourceWidth = W;
        vm.SourceHeight = H;
        vm.LocalMediaPath = main;
        // What a probe of the main file would have said (it is never probed here): 8 s of 30 fps video, no sound.
        var probed = new MediaInfo("mov,mp4,m4a,3gp,3g2,mj2", 8, 0, new VideoStreamInfo(W, H, "h264", 30, "16:9", "1:1", "bt709", "bt709", 0), [], []);
        var known = typeof(MainViewModel).GetField("_mediaInfo", Hidden) ?? throw new MissingFieldException(nameof(MainViewModel), "_mediaInfo");
        known.SetValue(vm, probed);

        var document = new TimelineDocument { Width = W, Height = H, FrameRate = FrameRate.Fps30 };
        var mainTrack = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        var insetTrack = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 2 };
        document.Tracks.Add(mainTrack);
        document.Tracks.Add(insetTrack);
        var anchor = Clip(main, LayerKind.MainVideo, 0, 4, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, main));
        var rider = Clip(main, LayerKind.MainVideo, 4, 8, 4, Look(LayerKind.MainVideo, "Main", 0, 0, 1, main));
        var file = Clip(inset, LayerKind.VideoFile, 0, 8, 0, Look(LayerKind.VideoFile, "inset", 0.5, 0.1, 0.4, inset));
        mainTrack.Clips.Add(anchor);
        mainTrack.Clips.Add(rider);
        insetTrack.Clips.Add(file);
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);

        var names = new Dictionary<long, string> { [GpuHash.Of(anchor.Id)] = "anchor", [GpuHash.Of(rider.Id)] = "rider", [GpuHash.Of(file.Id)] = "inset" };
        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);

        Scene(text, problems, vm, broker, names, "nothing added");
        var plain = vm.LivePreviewState();

        // The anchor: Blur, which blurs as soon as it is added (5.0-Release, the user's Q1), its radius then dragged to 30.
        Select(problems, vm, anchor, "the anchor");
        var blur = Add(vm, EffectRegistry.Blur);
        text.Append(CultureInfo.InvariantCulture, $"\nBlur added to the anchor: its radius starts at {blur.Get("radius"):0.##}\n");
        if (blur.Get("radius") < 1)
            problems.Add("an added Blur starts with no blur: Add Effect shows nothing (the user's Q1)");
        Expect(problems, vm, broker, names, 2, "anchor", GpuShaderSlot.BoxBlur);
        Write(vm, blur, "radius", 30);
        Scene(text, problems, vm, broker, names, "Blur (radius 30) on the anchor");
        Expect(problems, vm, broker, names, 2, "anchor", GpuShaderSlot.BoxBlur);
        ExpectNone(problems, vm, broker, names, 6, "rider", GpuShaderSlot.BoxBlur);

        // The second clip of the main video: Color Grade, its contrast 1.5 (its look is its own now: a rider).
        Select(problems, vm, rider, "the second clip of the main video");
        var grade = Add(vm, EffectRegistry.Grade);
        Write(vm, grade, "contrast", 1.5);
        Scene(text, problems, vm, broker, names, "Color Grade (contrast 1.5) on the second clip of the main video");
        Expect(problems, vm, broker, names, 6, "rider", GpuShaderSlot.ColorGrade);
        Expect(problems, vm, broker, names, 2, "anchor", GpuShaderSlot.BoxBlur);

        // Edited again: the scene follows the new value.
        var before = Packet(vm, broker, 6, GpuHash.Of(rider.Id), GpuShaderSlot.ColorGrade);
        Write(vm, grade, "contrast", 0.5);
        Scene(text, problems, vm, broker, names, "the second clip's contrast changed to 0.5");
        var after = Packet(vm, broker, 6, GpuHash.Of(rider.Id), GpuShaderSlot.ColorGrade);
        if (after is null)
            problems.Add("the second clip's Color Grade was gone from the scene after its contrast changed");
        else if (before is { } was && was == after.Value)
            problems.Add("the second clip's contrast changed, but the scene's Color Grade pass did not");

        // The other file's video: Blur and Color Grade.
        Select(problems, vm, file, "the video from another file");
        Write(vm, Add(vm, EffectRegistry.Blur), "radius", 6);
        Write(vm, Add(vm, EffectRegistry.Grade), "saturation", 0.2);
        Scene(text, problems, vm, broker, names, "Blur (6) and Color Grade (saturation 0.2) on the other file's video");
        foreach (var at in new[] { 2.0, 6.0 })
        {
            Expect(problems, vm, broker, names, at, "inset", GpuShaderSlot.BoxBlur);
            Expect(problems, vm, broker, names, at, "inset", GpuShaderSlot.ColorGrade);
        }

        var graded = vm.LivePreviewState();

        // Every card is in its clip's own look once the views' looks are written into the clips.
        foreach (var (clip, type) in new[] { (anchor, EffectRegistry.Blur), (rider, EffectRegistry.Grade), (file, EffectRegistry.Blur), (file, EffectRegistry.Grade) })
        {
            if (vm.Document.FindClip(clip.Id)?.State.Effects?.Any(e => e.Type == type) != true)
                problems.Add($"{names[GpuHash.Of(clip.Id)]}'s clip has no {type} card in its look after the looks were written");
        }

        // Split as Remove Dead Air splits: the selection stays on a view that is drawn, and the new half takes an effect.
        var split = vm.SplitLayer(vm.ViewOf(vm.Document.FindClip(file.Id)!)!, 3);
        text.Append(CultureInfo.InvariantCulture, $"\nthe other file's video split at 3 s: {(split ? "split" : "refused")}\n");
        if (!split)
            problems.Add("the other file's video could not be split at 3 s");
        var target = vm.ActiveKeyLayer;
        if (!vm.Layers.Contains(target) || target.ClipId == Guid.Empty || vm.ClipOf(target) is null)
            problems.Add("after the split the Effects pane's clip is not one on the timeline (an orphan view)");
        var second = vm.Document.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.ContentKind == LayerKind.VideoFile && c.Start > 2.5);
        if (second is not null)
        {
            names[GpuHash.Of(second.Id)] = "inset, second half";
            Select(problems, vm, second, "the second half of the other file's video");
            Write(vm, Add(vm, EffectRegistry.Sharpen), "strength", 1);
            Scene(text, problems, vm, broker, names, "Sharpen on the second half");
            Expect(problems, vm, broker, names, 6, "inset, second half", GpuShaderSlot.Sharpen);
        }
        else
        {
            problems.Add("the split made no second clip of the other file's video");
        }

        // With no clip of the main video at all, what the pane would edit with nothing selected is a stand-in, drawn nowhere.
        var empty = new MainViewModel();
        var alone = new TimelineDocument { Width = W, Height = H, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        track.Clips.Add(Clip(inset, LayerKind.VideoFile, 0, 8, 0, Look(LayerKind.VideoFile, "inset", 0.5, 0.1, 0.4, inset)));
        alone.Tracks.Add(track);
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(empty, [alone]);
        var standIn = empty.ActiveKeyLayer;
        text.Append(CultureInfo.InvariantCulture,
            $"\nno main video, nothing selected: the clip the panes work on is {(empty.Layers.Contains(standIn) ? "a clip on the timeline" : "a stand-in that is no clip")}\n");

        Background(text, problems, main);
        Draw(text, problems, folder, main, inset, plain, graded);
    }

    // ----- The blurred background, the main video's video-only child (5.0-Release) -----

    /// <summary>
    /// The blurred background behind a main video at half size: its continuous radius (7.5 px; 0 is sharp and still drawn),
    /// its zoom (the middle 1 / zoom of the picture covers the frame) and an effect of its own (a Vignette added the way the
    /// Effects pane adds one with the background's row selected), in the GPU scene and in the export's FFmpeg graph.
    /// </summary>
    private static void Background(StringBuilder text, List<string> problems, string main)
    {
        var vm = new MainViewModel { SourceWidth = W, SourceHeight = H, LocalMediaPath = main, FrameEngine = true };
        var probed = new MediaInfo("mov,mp4,m4a,3gp,3g2,mj2", 8, 0, new VideoStreamInfo(W, H, "h264", 30, "16:9", "1:1", "bt709", "bt709", 0), [], []);
        (typeof(MainViewModel).GetField("_mediaInfo", Hidden) ?? throw new MissingFieldException(nameof(MainViewModel), "_mediaInfo")).SetValue(vm, probed);
        var document = new TimelineDocument { Width = W, Height = H, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        track.Clips.Add(Clip(main, LayerKind.MainVideo, 0, 8, 0, Look(LayerKind.MainVideo, "Main", 0, 0, 1, main)));
        document.Tracks.Add(track);
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);
        vm.CenterZoom = 0.5;

        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        void Show(string step)
        {
            var plan = GpuScenePlan.Create(vm.LivePreviewState(), broker);
            var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
            var header = default(FrameSceneHeader);
            var count = plan.Evaluate(2, ref header, layers);
            text.Append(CultureInfo.InvariantCulture, $"\n{step}: {count} layer(s) at 2 s\n");
            var found = false;
            for (var i = 0; i < count; i++)
            {
                ref var d = ref layers[i];
                if ((d.Flags & GpuLayerDescriptor.FlagBlurFillOnly) == 0)
                    continue;
                found = true;
                var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
                    $"    blurred background: radius {d.BlurFillRadius:0.##}, {d.BlurFillPasses} passes, dim {d.BlurFillDim:0.##}, crop ({d.CropLeft:0.###}, {d.CropTop:0.###})-({d.CropRight:0.###}, {d.CropBottom:0.###})"));
                for (var k = 0; k < d.EffectCount; k++)
                    line.Append(CultureInfo.InvariantCulture, $"{(k == 0 ? ": " : ", ")}{d.Effects[k].Slot}({d.Effects[k].P0:0.###} {d.Effects[k].P1:0.###} {d.Effects[k].P2:0.###})");
                text.Append(line).Append('\n');
            }

            if (!found)
                problems.Add($"{step}: no blurred background is drawn behind the main video at half size");
        }

        Show("the blurred background behind the main video at half size");

        // Its row selected; its blur and zoom set as its pinned card sets them; a Vignette added as Add Effect adds one.
        vm.BackgroundSelected = true;
        (vm.BlurRadius, vm.BackgroundZoom) = (7.5, 1.5);
        if (vm.AddEffectCard(vm.BackgroundLayer, EffectRegistry.Find(EffectRegistry.Vignette)!) is null)
            problems.Add("a Vignette could not be added to the blurred background");
        Show("radius 7.5, zoom 1.5 and a Vignette of its own");

        var graph = FilterGraphBuilder.BuildExportCommand(vm.LivePreviewState() with { IsPreview = false, CopyVideoEncoder = false, VideoCodecArguments = "-c:v libx264" }).CommandLine;
        foreach (var (piece, what) in new[] { ("scale=480:270:force_original_aspect_ratio=increase,crop=320:180", "its zoom"), ("boxblur=8:2", "its radius, rounded"), ("vignette=angle=0.44", "its own vignette") })
        {
            var has = graph.Contains(piece, StringComparison.Ordinal);
            text.Append(CultureInfo.InvariantCulture, $"    the export's graph has {what}: {(has ? "yes" : "no")}\n");
            if (!has)
                problems.Add($"the export's graph has no {piece} for the blurred background's {what}");
        }

        vm.BlurRadius = 0;
        Show("radius 0: sharp, and drawn all the same");
    }

    // ----- Drawn by a real worker -----

    /// <summary>
    /// The scenes planned before any effect and with every one of them, drawn by a real compositor worker (this program
    /// started again with --compositor-worker) from clips made here with HandPeg's FFmpeg: the anchor's blur must soften its
    /// picture, the second clip's contrast flatten it, the other file's saturation grey it. Skipped (said on the console)
    /// with no FFmpeg; the golden text is the same either way.
    /// </summary>
    private static void Draw(StringBuilder text, List<string> problems, string folder, string main, string inset, SequenceExportState plain, SequenceExportState graded)
    {
        text.Append("\ndrawn by a real worker: the anchor's blur softens it, the second clip's contrast flattens it, the other file's saturation greys it\n");
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the clip effects were not drawn");
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            const string encode = "-c:v libx264 -preset veryfast -crf 12 -g 30 -bf 2 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -an -y";
            Ffmpeg(ffmpeg, $"-f lavfi -i \"testsrc2=s={W}x{H}:r=30:d=8\" -vf \"scale=out_color_matrix=bt709:out_range=tv,format=yuv420p\" {encode} \"{main}\"");
            Ffmpeg(ffmpeg, $"-f lavfi -i \"smptehdbars=s={W}x{H}:r=30:d=8\" -vf \"scale=out_color_matrix=bt709:out_range=tv,format=yuv420p\" {encode} \"{inset}\"");

            using var client = new CompositorClient(adapter: -1, keepStandby: false);
            using var reader = new CompositorFrameReader(client);
            using var broker = new GpuResourceBroker(client, bakeOnCallingThread: true);
            var (before, after) = (GpuScenePlan.Create(plain, broker), GpuScenePlan.Create(graded, broker));
            before.PrepareAround(0);
            after.PrepareAround(0);

            // The anchor (left of the other file's box) at 2 s, the second clip there at 6 s; the other file's box at 2 s.
            var (anchorArea, insetArea) = ((X: 4, Y: 4, W: 148, H: 172), (X: 170, Y: 26, W: 108, H: 56));
            if (Frame(client, reader, before, 2, problems) is not { } plain2 || Frame(client, reader, after, 2, problems) is not { } graded2
                || Frame(client, reader, before, 6, problems) is not { } plain6 || Frame(client, reader, after, 6, problems) is not { } graded6)
            {
                return;
            }

            var sharpness = Detail(graded2, anchorArea) / Math.Max(Detail(plain2, anchorArea), 1e-6);
            var spread = Spread(graded6, anchorArea) / Math.Max(Spread(plain6, anchorArea), 1e-6);
            var colour = Chroma(graded2, insetArea) / Math.Max(Chroma(plain2, insetArea), 1e-6);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"        drawn: the anchor's detail x{sharpness:0.00}, the second clip's spread x{spread:0.00}, the other file's colour x{colour:0.00}"));
            if (sharpness > 0.7)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"drawn by the worker, the anchor's Blur (30) left {sharpness:0.00} of its detail (the effect is not drawn)"));
            if (spread > 0.8)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"drawn by the worker, the second clip's contrast 0.5 left {spread:0.00} of its spread (the effect is not drawn)"));
            if (colour > 0.6)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"drawn by the worker, the other file's saturation 0.2 left {colour:0.00} of its colour (the effect is not drawn)"));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>A plan's scene at a moment, drawn and read back as RGB24 (twice: the exact frame may follow a stand-in).</summary>
    private static byte[]? Frame(CompositorClient client, CompositorFrameReader reader, GpuScenePlan plan, double seconds, List<string> problems)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            client.ClearScene();
            ref var header = ref client.Scene;
            var layers = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref client.Layer(0), CompositorProtocol.MaxLayers);
            plan.Evaluate(seconds + 0.0005, ref header, layers);
            var sequence = client.Publish();
            if (!client.WaitForFrame(sequence, 10_000))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"drawn at {seconds} s: no frame within 10 s ({client.State}: {client.Message})"));
                return null;
            }

            if (pass == 0)
                Thread.Sleep(150);
        }

        if (!reader.TryRead(out var bgra, out var width, out var height) || width != W || height != H)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"drawn at {seconds} s: the frame could not be read back"));
            return null;
        }

        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
            (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);
        return rgb;
    }

    private static double Luma(byte[] rgb, int x, int y)
    {
        var i = (y * W + x) * 3;
        return 0.2126 * rgb[i] + 0.7152 * rgb[i + 1] + 0.0722 * rgb[i + 2];
    }

    /// <summary>The mean step in brightness from one pixel to the next across an area: what a blur takes away.</summary>
    private static double Detail(byte[] rgb, (int X, int Y, int W, int H) area)
    {
        var (sum, count) = (0.0, 0);
        for (var y = area.Y; y < area.Y + area.H; y++)
        {
            for (var x = area.X; x < area.X + area.W - 1; x++, count++)
                sum += Math.Abs(Luma(rgb, x + 1, y) - Luma(rgb, x, y));
        }

        return sum / Math.Max(count, 1);
    }

    /// <summary>The standard deviation of brightness over an area: what a lower contrast takes away.</summary>
    private static double Spread(byte[] rgb, (int X, int Y, int W, int H) area)
    {
        var (sum, squares, count) = (0.0, 0.0, 0);
        for (var y = area.Y; y < area.Y + area.H; y++)
        {
            for (var x = area.X; x < area.X + area.W; x++, count++)
            {
                var l = Luma(rgb, x, y);
                (sum, squares) = (sum + l, squares + l * l);
            }
        }

        var mean = sum / Math.Max(count, 1);
        return Math.Sqrt(Math.Max(squares / Math.Max(count, 1) - mean * mean, 0));
    }

    /// <summary>The mean of each pixel's strongest channel less its weakest over an area: what a lower saturation takes away.</summary>
    private static double Chroma(byte[] rgb, (int X, int Y, int W, int H) area)
    {
        var (sum, count) = (0.0, 0);
        for (var y = area.Y; y < area.Y + area.H; y++)
        {
            for (var x = area.X; x < area.X + area.W; x++, count++)
            {
                var i = (y * W + x) * 3;
                sum += Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2])) - Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2]));
            }
        }

        return sum / Math.Max(count, 1);
    }

    private static void Ffmpeg(string ffmpeg, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error " + arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
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

    // ----- Doing what the panes do -----

    /// <summary>A press on the clip's block (MainWindow.SelectClip): it is the clip the panes show.</summary>
    private static void Select(List<string> problems, MainViewModel vm, TimelineClip clip, string what)
    {
        var view = vm.ViewOf(vm.Document.FindClip(clip.Id)!);
        vm.KeyLayer = view;
        if (view is null || !ReferenceEquals(vm.ActiveKeyLayer, view))
            problems.Add($"{what} selected, but the Effects pane's clip (ActiveKeyLayer) is another");
    }

    /// <summary>
    /// EffectsPane.Add (and a track's Add Effect menu, 5.0-Release): MainViewModel.AddEffectCard, the card put on the pane's clip
    /// as its own with its visible starting values (the user's Q1: an added Blur blurs at once).
    /// </summary>
    private static EffectInstance Add(MainViewModel vm, string type) =>
        vm.AddEffectCard(vm.ActiveKeyLayer, EffectRegistry.Find(type)!) ?? throw new InvalidOperationException($"{type} could not be added to {vm.ActiveKeyLayer.Name}");

    /// <summary>EffectsPane.WriteValue: a value dragged on the card.</summary>
    private static void Write(MainViewModel vm, EffectInstance card, string key, double value)
    {
        vm.Checkpoint($"change {key}");
        using (Layer.Echo())
            card.Set(key, value);
    }

    // ----- The scene -----

    private static GpuScenePlan Plan(MainViewModel vm, GpuResourceBroker broker) => GpuScenePlan.Create(vm.LivePreviewState(), broker);

    private static void Scene(StringBuilder text, List<string> problems, MainViewModel vm, GpuResourceBroker broker, Dictionary<long, string> names, string step)
    {
        var plan = Plan(vm, broker);
        text.Append(CultureInfo.InvariantCulture, $"\n{step}: {plan.EntryCount} entries\n");
        if (ReferenceEquals(plan, GpuScenePlan.Empty))
            problems.Add($"{step}: the plan could not be made (GpuScenePlan.Create returned Empty; see the log)");
        var (layers, infos) = (new GpuLayerDescriptor[CompositorProtocol.MaxLayers], new GpuLayerInfo[CompositorProtocol.MaxLayers]);
        foreach (var at in new[] { 2.0, 6.0 })
        {
            var header = default(FrameSceneHeader);
            var count = plan.Evaluate(at, ref header, layers);
            var described = plan.LayerInfoAt(at, infos);
            text.Append(CultureInfo.InvariantCulture, $"  at {at:0.###} s: {count} layer(s)\n");
            for (var i = 0; i < count; i++)
                text.Append("    ").Append(Describe(layers[i], i < described ? infos[i] : default, names)).Append('\n');
        }
    }

    private static string Describe(in GpuLayerDescriptor d, GpuLayerInfo info, Dictionary<long, string> names)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{(GpuSourceKind)d.SourceKind} [{names.GetValueOrDefault(d.ClipKey, info.Name)}]");
        line.Append(CultureInfo.InvariantCulture, $" box ({d.X:0.0}, {d.Y:0.0}) {d.Width:0.0} x {d.Height:0.0}");
        for (var k = 0; k < d.EffectCount; k++)
        {
            var p = d.Effects[k];
            line.Append(CultureInfo.InvariantCulture, $"{(k == 0 ? ": " : ", ")}{p.Slot}({p.P0:0.###} {p.P1:0.###} {p.P2:0.###} {p.P3:0.###})");
        }

        return line.ToString();
    }

    /// <summary>The first packet of a slot on the layer drawn for a clip at a moment; null when the clip's layer has none (or is not drawn).</summary>
    private static GpuEffectPacket? Packet(MainViewModel vm, GpuResourceBroker broker, double at, long clipKey, GpuShaderSlot slot)
    {
        var plan = Plan(vm, broker);
        var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var header = default(FrameSceneHeader);
        var count = plan.Evaluate(at, ref header, layers);
        for (var i = 0; i < count; i++)
        {
            if (layers[i].ClipKey != clipKey)
                continue;
            for (var k = 0; k < layers[i].EffectCount; k++)
            {
                if (layers[i].Effects[k].Slot == slot)
                    return layers[i].Effects[k];
            }
        }

        return null;
    }

    private static bool Drawn(MainViewModel vm, GpuResourceBroker broker, double at, long clipKey)
    {
        var plan = Plan(vm, broker);
        var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var header = default(FrameSceneHeader);
        var count = plan.Evaluate(at, ref header, layers);
        for (var i = 0; i < count; i++)
        {
            if (layers[i].ClipKey == clipKey)
                return true;
        }

        return false;
    }

    private static void Expect(List<string> problems, MainViewModel vm, GpuResourceBroker broker, Dictionary<long, string> names, double at, string clip, GpuShaderSlot slot)
    {
        var key = names.First(n => n.Value == clip).Key;
        if (!Drawn(vm, broker, at, key))
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"at {at} s no layer is drawn for {clip}"));
        else if (Packet(vm, broker, at, key, slot) is null)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"at {at} s the layer drawn for {clip} has no {slot} pass"));
    }

    private static void ExpectNone(List<string> problems, MainViewModel vm, GpuResourceBroker broker, Dictionary<long, string> names, double at, string clip, GpuShaderSlot slot)
    {
        var key = names.First(n => n.Value == clip).Key;
        if (Packet(vm, broker, at, key, slot) is not null)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"at {at} s the layer drawn for {clip} has a {slot} pass that is another clip's"));
    }

    // ----- Building the timeline -----

    private static LayerState Look(LayerKind kind, string name, double x, double y, double width, string file) => new()
    {
        Kind = kind, Name = name, PositionX = x, PositionY = y, SizeWidth = width, SourceWidth = 0.25, SourceHeight = 0.25,
        ImagePath = file, ImageWidth = W, ImageHeight = H, MediaDuration = 8,
    };

    private static TimelineClip Clip(string file, LayerKind kind, double start, double end, double offset, LayerState look) => new()
    {
        ContentKind = kind, SourcePath = file, Start = start, End = end, MediaOffset = offset, SourceDuration = 8, State = look,
    };
}
