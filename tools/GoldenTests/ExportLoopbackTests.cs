using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Compositor;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4B, the compositor's half of the Export Quality Preview and of HDR passthrough. export-loopback: the export's settings as
/// the loopback takes them (ExportLoopbackSettings, pure), the playback bar's option (5.4D: a default, after the 3D view), the letterbox the compare pass
/// shares with the player, and a real worker: the off path allocating nothing and saying Off, the compare pass with the codec bypassed
/// (any machine, WARP too: the encoded view equals the source's NV12, the heatmap is black, the wipe is the two halves and a divider),
/// a real hardware encode and decode where this PC has an H.264 encoder (its PSNR, round trip and encoder name to the console; with
/// none, the plain picture and a NoEncoder/NoDecoder state, the same golden lines), the 3D view refused, and the loopback let go when
/// the flag goes; 5.4C: the loupe over the compare (laid on after it, its view the compare picture itself magnified, point sampled, the
/// rest of the picture as it was). export-hdr-readback: FlagReadbackP010 frames against BT.2020 non-constant luminance in limited range,
/// ten bits in the top of each sixteen, a gradient to the code, and the NV12 and BGRA readbacks unchanged after it. Every line says what it proved.
/// </summary>
public static class ExportLoopbackTests
{
    public static IEnumerable<GraphResult> All()
    {
        var loopback = Start("# The Export Quality Preview's encode loopback (5.4B): settings, the off path, the compare pass, a real hardware round trip, its lifetime.");
        var hdr = Start("# An HDR export's frames read back as P010 (5.4B): BT.2020 non-constant luminance, limited range, 10 bits in the top of 16; NV12 unchanged.");
        try
        {
            Settings(loopback.Expect);
            using var client = new CompositorClient(adapter: -1, keepStandby: true);
            using var reader = new CompositorFrameReader(client);
            var harness = new Harness(client, reader, loopback.Expect);
            harness.OffPath();
            harness.Bypass();
            harness.Loupe();
            harness.RealCodec();
            harness.Lifetime();
            new Harness(client, reader, hdr.Expect).HdrReadback();
        }
        catch (Exception ex)
        {
            loopback.Problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("export-loopback", loopback.Text.ToString(), loopback.Problems, 0);
        yield return new GraphResult("export-hdr-readback", hdr.Text.ToString(), hdr.Problems, 0);
    }

    private sealed record Case(StringBuilder Text, List<string> Problems)
    {
        public void Expect(bool holds, string what)
        {
            Text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                Problems.Add(what);
        }
    }

    private static Case Start(string title) => new(new StringBuilder(title).Append('\n'), []);

    // ----- Pure: the settings, the bar's option, the letterbox -----

    private static void Settings(Action<bool, string> expect)
    {
        var nvenc = ExportLoopbackSettings.From("h264_nvenc", constantQuality: false, constantBitrate: true, 8000, 22, "p7", 59.94, out var why1);
        expect(nvenc == new ExportLoopbackSettings(1, 0, 8000, 57, 100, 59.94) && why1 is null,
            "h264_nvenc, CBR 8000 kbps, p7, 59.94 fps: H.264, rate control 0 (CBR), 8000 kbps, quality-vs-speed 100, the export's rate");
        var x264 = ExportLoopbackSettings.From("libx264", constantQuality: true, constantBitrate: false, 5000, 23, "veryslow", 60, out _);
        expect(x264 is { Codec: 1, RateControl: 3, Quality: 55, QualityVsSpeed: 100 },
            "libx264, constant quality CRF 23, veryslow: H.264 in Media Foundation's quality mode at 55 (MediaFoundationEncoder.QualityFromCrf), quality-vs-speed 100");
        var amf = ExportLoopbackSettings.From("hevc_amf", constantQuality: false, constantBitrate: false, 12000, 22, "balanced", 30, out _);
        expect(amf is { Codec: 2, RateControl: 2, BitrateKbps: 12000, QualityVsSpeed: 50 },
            "hevc_amf, VBR (the average bitrate mode) 12000 kbps, balanced: HEVC, rate control 2 (unconstrained VBR), quality-vs-speed 50");
        var av1 = ExportLoopbackSettings.From("av1_qsv", false, false, 6000, 22, "fast", 0, out _);
        expect(av1 is { Codec: 3, QualityVsSpeed: 33, FrameRate: 30 }, "av1_qsv, fast, an unknown rate: AV1, quality-vs-speed 33, 30 fps assumed");
        expect(ExportLoopbackSettings.From("libsvtav1", false, false, 50, 22, "ultrafast", 24, out _) is { Codec: 3, BitrateKbps: 100, QualityVsSpeed: 0 },
            "libsvtav1 at 50 kbps, ultrafast: AV1, the bitrate kept at least 100 kbps, quality-vs-speed 0");
        var vp9 = ExportLoopbackSettings.From("libvpx-vp9", false, false, 5000, 22, "medium", 30, out var whyVp9);
        var copy = ExportLoopbackSettings.From("copy", false, false, 5000, 22, "medium", 30, out var whyCopy);
        var prores = ExportLoopbackSettings.From("prores_ks", false, false, 5000, 22, "medium", 30, out var whyProres);
        expect(vp9 is null && copy is null && prores is null && whyVp9!.Contains("VP9") && whyCopy!.Contains("stream copy") && whyProres!.Contains("ProRes"),
            "VP9, a stream copy and ProRes have no loopback, and each says why in words");
        expect(ExportLoopbackSettings.CodecOf("h264_mf") == 1 && ExportLoopbackSettings.CodecOf("hevc_qsv") == 2 && ExportLoopbackSettings.CodecOf("libx265") == 2
               && ExportLoopbackSettings.CodecOf("gif") == 0 && ExportLoopbackSettings.QualityVsSpeedOf("slow") == 83 && ExportLoopbackSettings.QualityVsSpeedOf("odd") == 50,
            "CodecOf reads the FFmpeg encoder names (h264_mf, hevc_qsv, libx265; a GIF none); QualityVsSpeedOf: slow 83, an unknown preset 50");

        // 5.4D: shown by default, right after the 3D view, as the Live Encoded Preview; list version 3 shows it once on older bars.
        var bar = new PlaybackControlSet();
        bar.Load(null);
        var option = bar.Options.FirstOrDefault(o => o.Id == "ExportQualityPreview");
        var ids = bar.Options.Select(o => o.Id).ToList();
        expect(option is { Group: "Viewer", IsShown: true, Label: "Live Encoded Preview" } && ids.IndexOf("ExportQualityPreview") == ids.IndexOf("Exploded3D") + 1
               && bar.Save().Contains("Exploded3D,ExportQualityPreview,", StringComparison.Ordinal) && bar.Save().EndsWith(",@3", StringComparison.Ordinal),
            "the playback bar offers the Live Encoded Preview under Viewer, shown by default right after the 3D Exploded View; the defaults' list is saved at version 3");
        bar.Load("Volume,@2");
        var older = bar["ExportQualityPreview"] && bar.Save() == "ExportQualityPreview,Volume,@3";
        bar.Load("Volume,@3");
        expect(older && !bar["ExportQualityPreview"] && bar.Save() == "Volume,@3",
            "a bar saved before 5.4D (@2) shows it once, saved at @3 with it; one saved at @3 without it keeps it hidden (the user's choice)");
        bar.Load("ExportQualityPreview,Volume,@3");
        expect(bar["ExportQualityPreview"] && bar.Save() == "ExportQualityPreview,Volume,@3", "chosen, it is shown and saved by its name");
        expect(bar.Options.All(o => !o.Id.Contains("LivePreview", StringComparison.Ordinal)), "no option is named LivePreview (golden live-preview-gone)");

        var header = new FrameSceneHeader { CanvasWidth = 1920, CanvasHeight = 1080, ViewScale = 1 };
        var (x, y, scale) = SceneRenderer.Letterbox(header, 1000, 1000);
        header.ViewScale = 2;
        header.ViewPanX = 0.25f;
        var (zx, zy, zscale) = SceneRenderer.Letterbox(header, 1000, 1000);
        expect(Math.Abs(x) < 1e-3 && Math.Abs(y - 218.75) < 1e-3 && Math.Abs(scale - 1000 / 1920.0) < 1e-6
               && Math.Abs(zscale - 2 * scale) < 1e-6 && Math.Abs(zx - (500 + 0.25 * 1000 * 2 - 1000)) < 1e-3 && Math.Abs(zy - (500 - 562.5)) < 1e-3,
            "the letterbox the compare pass shares with the player: a 1920 x 1080 canvas fitted into 1000 x 1000 at (0, 218.75), zoomed 2x and panned a quarter");

        // The loupe over the compare (5.4C): its view is the whole picture scaled about the point, the point in the view's middle.
        var view = ExportLoopbackPreview.LoupePicture(130, 25.6f, 14.4f, 4, 640, 360);
        expect(Math.Abs(view.X - (65 - 102.4)) < 1e-3 && Math.Abs(view.Y - (65 - 57.6)) < 1e-3 && Math.Abs(view.W - 2560) < 1e-3 && Math.Abs(view.H - 1440) < 1e-3,
            "the loupe's view over the compare: a 640 x 360 picture at 4x about the point (25.6, 14.4) lies at (-37.4, 7.4), 2560 x 1440, the point in the middle of a 130-pixel view");
        var bounds = SceneRenderer.LoupeBounds((0, 0, 640, 360), 640, 360, 130);
        expect(bounds == (12, 498, 12, 218), "and it stands 12 pixels in from the picture's edges: its corner between (12, 12) and (498, 218) on a 640 x 360 picture");
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Compositor", "Shaders", "Loopback.hlsl"));
        var worker = File.ReadAllText(Path.Combine(Root(), "src", "Compositor", "CompositorWorker.cs"));
        var draw = worker[worker.IndexOf("private static bool DrawLoopback", StringComparison.Ordinal)..];
        draw = draw[..draw.IndexOf("\n    }", StringComparison.Ordinal)];
        var acquire = draw.IndexOf("Dxgi.AcquireSync", StringComparison.Ordinal);
        var release = draw.IndexOf("Dxgi.ReleaseSync", StringComparison.Ordinal);
        var prepare = draw.IndexOf("PrepareLoupe(", StringComparison.Ordinal);
        var loupeView = draw.IndexOf("DrawLoupeView(", StringComparison.Ordinal);
        var composite = draw.IndexOf("CompositeLoupe(", StringComparison.Ordinal);
        expect(source.Contains("if (Params.w < 0)", StringComparison.Ordinal) && source.Contains("LumaPlane.SampleLevel(PointClamp", StringComparison.Ordinal)
               && prepare > 0 && prepare < acquire && loupeView > prepare && loupeView < acquire && composite > draw.IndexOf("loopback.Draw(", StringComparison.Ordinal) && composite < release,
            "the worker finds the loupe's point and draws its view before it takes the output, and lays it on after the compare under the keyed mutex; the view's planes are point sampled (a negative Params.w)");
    }

    // The repository's folder (where HandPegApp.csproj is), found upwards from the built program.
    private static string Root()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }

    private sealed class Harness(CompositorClient client, CompositorFrameReader reader, Action<bool, string> expect)
    {
        private const int W = 640, H = 360;

        // ----- Scenes -----

        private void Begin(int flags, int width = W, int height = H)
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.Flags) = (width, height, flags);
            (scene.OutputWidth, scene.OutputHeight, scene.ViewScale) = (width, height, 1);
        }

        private void Pattern(int index, int kind, float x = 0, float y = 0, float width = W, float height = H)
        {
            ref var layer = ref client.Layer(index);
            layer.SourceKind = (int)GpuSourceKind.TestPattern;
            layer.PatternKind = kind;
            (layer.X, layer.Y, layer.Width, layer.Height) = (x, y, width, height);
            (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
            (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
            (layer.CropRight, layer.CropBottom) = (1, 1);
            layer.Flags = GpuLayerDescriptor.FlagVisible;
            client.Scene.ActiveLayerCount = Math.Max(client.Scene.ActiveLayerCount, index + 1);
        }

        // The Export Quality Preview's fields, as GpuPreviewEngine.WriteLoopback writes them.
        private void Loopback(int mode, float wipe, long stamp, bool bypass, int codec = 1, int rateControl = 0, int kbps = 8000)
        {
            ref var scene = ref client.Scene;
            scene.Flags |= FrameSceneHeader.FlagLoopback;
            (scene.LoopbackCodec, scene.LoopbackRateControl, scene.LoopbackBitrateKbps, scene.LoopbackQuality, scene.LoopbackQualityVsSpeed) = (codec, rateControl, kbps, 55, 100);
            (scene.LoopbackMode, scene.LoopbackWipeX, scene.LoopbackFrameRate, scene.LoopbackContentStamp, scene.LoopbackHeatGain) = (mode, wipe, 30, stamp, 10);
            scene.LoopbackTestFlags = bypass ? 1 : 0;
        }

        // The checkerboard with the radial target over it: edges and colour for an encoder to lose.
        private void Busy()
        {
            Pattern(0, 2);
            Pattern(1, 3, W * 0.2f, H * 0.1f, W * 0.6f, H * 0.8f);
        }

        private byte[]? Frame(string what, out long sequence)
        {
            sequence = client.Publish();
            if (!client.WaitForFrame(sequence, 10_000))
            {
                expect(false, $"{what}: a frame within 10 s ({client.State}: {client.Message})");
                return null;
            }

            if (!reader.TryRead(out var bgra, out var width, out var height) || (width, height) != (W, H))
            {
                expect(false, $"{what}: the frame read back at {W} x {H}");
                return null;
            }

            return bgra;
        }

        private byte[]? Frame(string what) => Frame(what, out _);

        private ref ControlBlock Control => ref client.Memory.Control;

        private static int Worst(byte[] a, byte[] b, Func<int, bool>? column = null)
        {
            var worst = 0;
            for (var i = 0; i < a.Length; i += 4)
            {
                if (column is not null && !column(i / 4 % W))
                    continue;
                for (var c = 0; c < 3; c++)
                    worst = Math.Max(worst, Math.Abs(a[i + c] - b[i + c]));
            }

            return worst;
        }

        private static double LumaPsnr(byte[] a, byte[] b)
        {
            double sum = 0;
            var n = a.Length / 4;
            for (var i = 0; i < a.Length; i += 4)
            {
                var la = 0.2126 * a[i + 2] + 0.7152 * a[i + 1] + 0.0722 * a[i];
                var lb = 0.2126 * b[i + 2] + 0.7152 * b[i + 1] + 0.0722 * b[i];
                sum += (la - lb) * (la - lb);
            }

            var mse = sum / n;
            return mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
        }

        private static double Mean(byte[] bgra)
        {
            double sum = 0;
            for (var i = 0; i < bgra.Length; i += 4)
                sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
            return sum / (bgra.Length / 4 * 3);
        }

        // ----- The off path -----

        public void OffPath()
        {
            int made2 = 0, made3 = 0;
            byte[]? frame = null;
            for (var k = 1; k <= 3; k++)
            {
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
                Pattern(0, 1);
                frame = Frame("off path");
                if (k == 2)
                    made2 = Volatile.Read(ref Control.TexturesMade);
                if (k == 3)
                    made3 = Volatile.Read(ref Control.TexturesMade);
            }

            expect(frame is not null && Control.LoopbackState == (int)ExportLoopbackState.Off && made2 == made3 && made2 > 0,
                "three letterboxed frames without the flag: the loopback says Off and no texture is made between the second and the third (nothing of it exists)");
        }

        // ----- The compare pass, the codec bypassed (every machine) -----

        public void Bypass()
        {
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
            Pattern(0, 1);
            var plain = Frame("bypass: the plain frame");

            byte[]? View(int mode, float wipe, string what)
            {
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
                Pattern(0, 1);
                Loopback(mode, wipe, stamp: 1, bypass: true);
                return Frame(what);
            }

            var encoded = View(0, 0.5f, "bypass: encoded");
            var heat = View(2, 0.5f, "bypass: heatmap");
            var wipe = View(1, 0.5f, "bypass: wipe");
            if (plain is null || encoded is null || heat is null || wipe is null)
                return;

            var nv12 = Worst(plain, encoded);
            Console.WriteLine(FormattableString.Invariant($"  (the bypassed encoded view against the plain frame: worst {nv12}/255, the NV12 round trip of a gradient)"));
            expect(nv12 <= 4 && Control.LoopbackState == (int)ExportLoopbackState.Running,
                "the encoded view with the codec bypassed is the plain frame within 4/255 (its own 4:2:0 conversion), and the state is Running");
            var heatWorst = 0;
            for (var i = 0; i < heat.Length; i += 4)
                heatWorst = Math.Max(heatWorst, Math.Max(heat[i], Math.Max(heat[i + 1], heat[i + 2])));
            expect(heatWorst <= 1, "the heatmap of a picture against itself is black (every pixel within 1/255)");

            var halves = Worst(wipe, encoded, x => x < 318 || x > 321);
            var divider = 0;
            for (var y = 0; y < H; y++)
            {
                var at = (y * W + 319) * 4;
                if (Math.Abs(wipe[at] - encoded[at]) > 40 && Math.Abs(wipe[at + 4] - encoded[at + 4]) > 40)
                    divider++;
            }

            expect(halves <= 1 && divider == H,
                "the split wipe at 0.5: both halves are the encoded view (the source and the bypassed encoding agree) and columns 319-320 are the divider, in every row");
        }

        // ----- The loupe over the compare (5.4C), the codec bypassed (every machine) -----

        public void Loupe()
        {
            // A gradient over the whole canvas, its clip's loupe asked for: no soft edge, so the point is its box's top left corner
            // (+4 %: 25.6, 14.4) and the loupe stands in the far corner, at (498, 218), 130 pixels a side (a third of 360).
            byte[]? View(int mode, bool loupe, long stamp, string what)
            {
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
                Pattern(0, 1);
                client.Layer(0).ClipKey = 77;
                if (loupe)
                    (client.Scene.LoupeClipKey, client.Scene.LoupeZoom) = (77, 4);
                Loopback(mode, 0.5f, stamp, bypass: true);
                return Frame(what);
            }

            var plain = View(0, false, 31, "loupe: the compare without it");
            var encoded = View(0, true, 31, "loupe: over the encoded picture");
            var heat = View(2, true, 32, "loupe: over the heatmap");
            if (plain is null || encoded is null || heat is null)
                return;

            const int left = 498, top = 218, side = 130;
            bool Inside(int px, int py) => px >= left && px < left + side && py >= top && py < top + side;
            var (outsideWorst, insideChanged) = (0, 0);
            for (var py = 0; py < H; py++)
            {
                for (var px = 0; px < W; px++)
                {
                    var at = (py * W + px) * 4;
                    var away = Math.Max(Math.Abs(plain[at] - encoded[at]), Math.Max(Math.Abs(plain[at + 1] - encoded[at + 1]), Math.Abs(plain[at + 2] - encoded[at + 2])));
                    if (Inside(px, py))
                        insideChanged += away > 8 ? 1 : 0;
                    else
                        outsideWorst = Math.Max(outsideWorst, away);
                }
            }

            int[] Pixel(byte[] frame, int px, int py) => [frame[(py * W + px) * 4 + 2], frame[(py * W + px) * 4 + 1], frame[(py * W + px) * 4]];
            var middle = Pixel(encoded, left + side / 2, top + side / 2);
            var looked = Pixel(plain, 25, 14);
            var centre = middle.Zip(looked, (a, b) => Math.Abs(a - b)).Max();
            Console.WriteLine(FormattableString.Invariant($"  (loupe over the compare: {insideChanged} of {side * side} pixels of its box changed, the rest within {outsideWorst}/255; its middle {centre}/255 from the picture at the point)"));
            expect(outsideWorst <= 1 && insideChanged > side * side / 4,
                "the loupe is laid over the compare in the far corner (498, 218, 130 a side): the picture round it is the compare as it was, within 1/255");
            expect(centre <= 3, "its middle shows the compare picture at the point it looks at (canvas pixel 25, 14), magnified");

            // Over the heatmap of a picture against itself the loupe's view is black: it shows the compare picture, not the layer drawn again.
            var frame = Pixel(heat, left, top + side / 2);
            var (blackWorst, outsideHeat) = (0, 0);
            for (var py = top + 8; py < top + side - 8; py++)
            {
                for (var px = left + 8; px < left + side - 8; px++)
                {
                    var fromMiddle = (Math.Abs(px + 0.5 - (left + side / 2.0)), Math.Abs(py + 0.5 - (top + side / 2.0)));
                    if (fromMiddle.Item1 < 2 || fromMiddle.Item2 < 2)
                        continue; // the cross hairs
                    blackWorst = Math.Max(blackWorst, Pixel(heat, px, py).Max());
                }
            }

            for (var px = 0; px < left - 1; px++)
                outsideHeat = Math.Max(outsideHeat, Pixel(heat, px, 100).Max());
            expect(blackWorst <= 1 && outsideHeat <= 1 && frame[0] > 200 && frame[1] is > 140 and < 220 && frame[2] < 90,
                "over the heatmap (black: the codec bypassed) the loupe's view is black too, framed in amber: it magnifies the compare picture, not the layer drawn again");
        }

        // ----- A real hardware encode and decode (where this PC has an H.264 encoder) -----

        public void RealCodec()
        {
            // The source as the compare shows it (the codec bypassed), then the same scene through the encoder.
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
            Busy();
            Loopback(0, 0.5f, stamp: 7, bypass: true);
            var source = Frame("real codec: the source");

            var watch = Stopwatch.StartNew();
            byte[]? encoded = null;
            var state = ExportLoopbackState.Off;
            while (watch.ElapsedMilliseconds < 8000)
            {
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
                Busy();
                Loopback(0, 0.5f, stamp: 7, bypass: false);
                encoded = Frame("real codec: encoded");
                state = (ExportLoopbackState)Control.LoopbackState;
                if (state is not (ExportLoopbackState.Starting or ExportLoopbackState.Off) && (state != ExportLoopbackState.Running || Control.LoopbackFramesEncoded > 0))
                    break;
                Thread.Sleep(20);
            }

            var name = CompositorSharedMemory.ReadString(Control.LoopbackEncoderName);
            if (state == ExportLoopbackState.Running && source is not null && encoded is not null)
            {
                // Once more: the paused frame is not encoded again (same content stamp), and the decoded pair is drawn.
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
                Busy();
                Loopback(0, 0.5f, stamp: 7, bypass: false);
                encoded = Frame("real codec: encoded again") ?? encoded;
                var encodedCount = Control.LoopbackFramesEncoded;
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
                Busy();
                Loopback(2, 0.5f, stamp: 7, bypass: false);
                var heat = Frame("real codec: heatmap");
                var psnr = LumaPsnr(source, encoded);
                var mean = heat is null ? -1 : Mean(heat);
                Console.WriteLine(FormattableString.Invariant(
                    $"  (real codec: {name}, luma PSNR {psnr:0.00} dB at 8000 kbps CBR, round trip {Control.LoopbackRoundTripMs:0.0} ms, heatmap mean {mean:0.00}/255, {encodedCount} frame(s) encoded, {watch.ElapsedMilliseconds} ms to the first)"));
                expect(psnr is >= 28 and <= 60 && mean > 0 && mean < 64 && Control.LoopbackFramesEncoded == encodedCount && name.Length > 0,
                    "a real H.264 hardware encode and decode where this PC has one (PSNR 28-60 dB at 8000 kbps CBR, a heatmap neither black nor bright, the paused frame encoded once), else the plain picture with NoEncoder or NoDecoder");

                // Playing: 60 frames, the target moving, each with the playing wait (numbers to the console only: they are this PC's).
                var (before, lateBefore) = (Control.LoopbackFramesEncoded, Control.LoopbackLateFrames);
                var (trips, worstTrip, cpu) = (0.0, 0.0, 0.0);
                for (var k = 0; k < 60; k++)
                {
                    Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
                    client.Scene.IsPlaying = 1;
                    Pattern(0, 2);
                    Pattern(1, 3, W * 0.2f + k * 3, H * 0.1f, W * 0.6f, H * 0.8f);
                    Loopback(1, 0.5f, stamp: 100 + k, bypass: false);
                    _ = Frame("real codec: playing");
                    trips += Control.LoopbackRoundTripMs;
                    worstTrip = Math.Max(worstTrip, Control.LoopbackRoundTripMs);
                    cpu += Control.LastFrameCpuMs;
                }

                var (decoded, late) = (Control.LoopbackFramesEncoded - before, Control.LoopbackLateFrames - lateBefore);
                Console.WriteLine(FormattableString.Invariant(
                    $"  (real codec playing: {decoded} of 60 frames back, {late} late for their turn, round trip {trips / 60:0.0} ms on average, {worstTrip:0.0} ms at worst, the worker's frame {cpu / 60:0.0} ms)"));
                // A paused key frame again, with the encoder and decoder warm (the console only).
                var pausedBefore = Control.LoopbackFramesEncoded;
                var paused = Stopwatch.StartNew();
                while (paused.ElapsedMilliseconds < 3000 && Control.LoopbackFramesEncoded == pausedBefore)
                {
                    Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
                    Busy();
                    Loopback(0, 0.5f, stamp: 500, bypass: false);
                    _ = Frame("real codec: paused again");
                }

                Console.WriteLine(FormattableString.Invariant($"  (real codec paused, warm: a key frame back in {Control.LoopbackRoundTripMs:0.0} ms, seen after {paused.ElapsedMilliseconds} ms)"));
                expect(decoded >= 15 && Control.LoopbackState == (int)ExportLoopbackState.Running,
                    "playing, the encoder keeps up well enough to show its pictures: at least 15 of 60 frames come back and it stays Running (this PC's numbers on the console)");
            }
            else
            {
                Console.WriteLine($"  note  no H.264 loopback on this PC ({state}, 0x{Control.LoopbackHResult:X8}{(name.Length > 0 ? ", " + name : "")}): the plain picture is shown");
                var plainShown = source is not null && encoded is not null && Worst(source, encoded) <= 1;
                expect(state is ExportLoopbackState.NoEncoder or ExportLoopbackState.NoDecoder && plainShown,
                    "a real H.264 hardware encode and decode where this PC has one (PSNR 28-60 dB at 8000 kbps CBR, a heatmap neither black nor bright, the paused frame encoded once), else the plain picture with NoEncoder or NoDecoder");
                expect(true, "playing, the encoder keeps up well enough to show its pictures: at least 15 of 60 frames come back and it stays Running (this PC's numbers on the console)");
            }
        }

        // ----- Lifetime -----

        public void Lifetime()
        {
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
            Pattern(0, 1);
            Loopback(1, 0.5f, stamp: 9, bypass: true);
            _ = Frame("lifetime: on");
            var on = Control.LoopbackState;

            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
            Pattern(0, 1);
            Loopback(1, 0.5f, stamp: 9, bypass: true);
            client.Scene.ViewMode = 1;
            _ = Frame("lifetime: the 3D view");
            var exploded = Control.LoopbackState;

            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
            Pattern(0, 1);
            _ = Frame("lifetime: off");
            var off = Control.LoopbackState;
            var made = Control.TexturesMade;
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
            Pattern(0, 1);
            _ = Frame("lifetime: off again");
            expect(on == (int)ExportLoopbackState.Running && exploded == (int)ExportLoopbackState.Unavailable && off == (int)ExportLoopbackState.Off && Control.TexturesMade == made,
                "the flag on: Running; in the 3D view: Unavailable (the flat picture only); the flag gone: Off on the next frame, and nothing made after");
        }

        // ----- P010 readback (HDR passthrough) -----

        public void HdrReadback()
        {
            const int width = 64, height = 36;
            client.EnsureReadback(width * height * 4L);
            var pixels = new byte[CompositorProtocol.P010Bytes(width, height)];
            (float R, float G, float B, string Name)[] colours =
                [(1, 1, 1, "white"), (0, 0, 0, "black"), (1, 0, 0, "red"), (0, 1, 0, "green"), (0, 0, 1, "blue"), (0.5f, 0.25f, 0.75f, "violet"), (0.58f, 0.58f, 0.58f, "PQ 203-nit grey")];
            var (worst, lowBits, rows) = (0.0, 0, true);
            foreach (var (r, g, b, name) in colours)
            {
                Begin(FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback | FrameSceneHeader.FlagReadbackP010, width, height);
                (client.Scene.BackgroundR, client.Scene.BackgroundG, client.Scene.BackgroundB) = (r, g, b);
                if (!client.TryReadback(client.Publish(), pixels, out var gotWidth, out var gotHeight, 5000) || (gotWidth, gotHeight) != (width, height))
                {
                    expect(false, $"p010: {name} came back");
                    continue;
                }

                rows &= Control.ReadbackRowBytes == width * 2;
                var luma = 0.2627 * r + 0.6780 * g + 0.0593 * b;
                var (y, u, v) = (64 + 876 * luma, 512 + 896 * (b - luma) / 1.8814, 512 + 896 * (r - luma) / 1.4746);
                int Sample(long at) => pixels[at] | (pixels[at + 1] << 8);
                var yAt = 2L * (height / 2 * width + width / 2);
                var uvAt = 2L * width * height + 4L * (height / 4 * (width / 2) + width / 4);
                var (ys, us, vs) = (Sample(yAt), Sample(uvAt), Sample(uvAt + 2));
                lowBits |= (ys | us | vs) & 0x3F;
                var away = Math.Max(Math.Abs((ys >> 6) - y), Math.Max(Math.Abs((us >> 6) - u), Math.Abs((vs >> 6) - v)));
                worst = Math.Max(worst, away);
                if (away > 1)
                    expect(false, FormattableString.Invariant($"p010: {name} is {ys >> 6}, {us >> 6}, {vs >> 6}, not {y:0.0}, {u:0.0}, {v:0.0}"));
            }

            Console.WriteLine(FormattableString.Invariant($"  (P010's worst difference from the book: {worst:0.00} codes)"));
            expect(worst <= 1 && lowBits == 0 && rows,
                $"{colours.Length} solid colours read back as P010 within 1 code of BT.2020 non-constant luminance in limited range (Y 64-940, C 64-960), the low 6 bits clear, rows of twice the width");

            // A gradient across 1024 pixels: every luma code where the book puts it (an 8-bit picture would miss by up to 2 codes).
            const int gw = 1024, gh = 16;
            client.EnsureReadback(gw * gh * 4L);
            var gradient = new byte[CompositorProtocol.P010Bytes(gw, gh)];
            Begin(FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback | FrameSceneHeader.FlagReadbackP010, gw, gh);
            Pattern(0, 1, 0, 0, gw, gh);
            var (gradientWorst, codes) = (0.0, new HashSet<int>());
            if (client.TryReadback(client.Publish(), gradient, out var gotGw, out var gotGh, 5000) && (gotGw, gotGh) == (gw, gh))
            {
                const int row = 8;
                for (var x = 0; x < gw; x++)
                {
                    var (u, v) = ((x + 0.5) / gw, (row + 0.5) / gh);
                    var luma = 0.2627 * u + 0.6780 * v + 0.0593 * (1 - u * 0.5 - v * 0.5);
                    var code = (gradient[2 * (row * gw + x)] | (gradient[2 * (row * gw + x) + 1] << 8)) >> 6;
                    codes.Add(code);
                    gradientWorst = Math.Max(gradientWorst, Math.Abs(code - (64 + 876 * luma)));
                }
            }
            else
            {
                expect(false, "p010: the gradient came back");
            }

            Console.WriteLine(FormattableString.Invariant($"  (P010 gradient: worst {gradientWorst:0.00} codes, {codes.Count} distinct luma codes across {gw} pixels)"));
            expect(gradientWorst <= 1 && codes.Count > 180,
                "a gradient read back as P010: every luma code within 1 of the book and more than 180 distinct codes across 1024 pixels (ten bits kept, not eight)");

            // The NV12 and BGRA readbacks after it, as they were.
            client.EnsureReadback(width * height * 4L);
            var nv12 = new byte[CompositorProtocol.Nv12Bytes(width, height)];
            Begin(FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback | FrameSceneHeader.FlagReadbackNv12, width, height);
            (client.Scene.BackgroundR, client.Scene.BackgroundG, client.Scene.BackgroundB) = (0.5f, 0.25f, 0.75f);
            var nv12Ok = client.TryReadback(client.Publish(), nv12, out _, out _, 5000) && Control.ReadbackRowBytes == width;
            var l709 = 0.2126 * 0.5 + 0.7152 * 0.25 + 0.0722 * 0.75;
            nv12Ok &= Math.Abs(nv12[height / 2 * width + width / 2] - (16 + 219 * l709)) <= 1.5;
            var bgra = new byte[width * height * 4];
            Begin(FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback, width, height);
            (client.Scene.BackgroundR, client.Scene.BackgroundG, client.Scene.BackgroundB) = (1, 0, 0);
            var bgraOk = client.TryReadback(client.Publish(), bgra, out _, out _, 5000) && Control.ReadbackRowBytes == width * 4 && bgra[2] == 255 && bgra[1] == 0 && bgra[0] == 0;
            expect(nv12Ok && bgraOk, "after P010, an NV12 readback is BT.709 limited range as before (rows of its width) and a BGRA one is its colour (rows of four bytes a pixel)");
        }
    }
}
