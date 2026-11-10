using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.1A (Subagent B). player-resize-no-stretch: the player resized never stretches a frame of the old size. The pure rules
/// (GpuViewportControl.CenteredCopy, CompositorWorker.OutputCapacity), a real worker dragged through 40 sizes (a few output rings, not one
/// a size; every frame read back at exactly its size, letterboxed where SceneRenderer.Letterbox puts it), a frame read back staying in a
/// ring of its size, the reader's centred copy into a target of another size (1:1, black round it, nothing scaled), the protocol's
/// content sizes and the source of the swap chain, the viewport and the wake on WM_SIZE. live-encoded-preview-wipe: dragging the Live
/// Encoded Preview's split wipe encodes nothing again (the codec bypassed: the divider where the wipe says, in every frame), a real
/// encoder's paused frame no longer making every wipe scene wait (where this PC has an H.264 encoder), the handle's place against the
/// shader's line at four display scales, and the source of the worker's wait, its poll and the window's drag. Every line says what it proved.
/// </summary>
public static class ViewportResizeTests
{
    public static IEnumerable<GraphResult> All()
    {
        var resize = Start("# The player resized with no stretch (5.4.1A): a frame of another size centred 1:1, a ring that is not made anew for every size.");
        var wipe = Start("# The Live Encoded Preview's split wipe without the encoder's wait (5.4.1A): nothing encoded again, the line where the handle is.");
        try
        {
            Pure(resize.Expect);
            Sources(resize.Expect);
            WipePure(wipe.Expect);
            WipeSources(wipe.Expect);
            using var client = new CompositorClient(adapter: -1, keepStandby: true);
            using var reader = new CompositorFrameReader(client);
            var harness = new Harness(client, reader);
            harness.Drag(resize.Expect);
            harness.ReadbackExact(resize.Expect);
            harness.Centred(resize.Expect);
            harness.WipeBypass(wipe.Expect);
            harness.WipeRealCodec(wipe.Expect);
        }
        catch (Exception ex)
        {
            resize.Problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("player-resize-no-stretch", resize.Text.ToString(), resize.Problems, 0);
        yield return new GraphResult("live-encoded-preview-wipe", wipe.Text.ToString(), wipe.Problems, 0);
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), "src", .. parts]));

    /// <summary>A member's text in a source file: from its signature to the first line that closes it at its own indent.</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    // ----- player-resize-no-stretch: the pure rules -----

    private static void Pure(Action<bool, string> expect)
    {
        expect(GpuViewportControl.CenteredCopy(800, 450, 1000, 450) == (0, 0, 800, 450, 100, 0),
            "CenteredCopy: an 800 x 450 frame in a 1000 x 450 window is all of it, 100 pixels in from the left (margins, never a stretch)");
        expect(GpuViewportControl.CenteredCopy(1000, 600, 800, 600) == (100, 0, 900, 600, 0, 0),
            "a 1000 x 600 frame in an 800 x 600 window: its middle 800 columns (100..900) at the window's left edge (cropped, never squeezed)");
        expect(GpuViewportControl.CenteredCopy(640, 360, 640, 360) == (0, 0, 640, 360, 0, 0), "a frame of the window's size: the whole frame at (0, 0)");
        expect(GpuViewportControl.CenteredCopy(7, 3, 8, 4) == (0, 0, 7, 3, 0, 0) && GpuViewportControl.CenteredCopy(5, 9, 8, 4) == (0, 2, 5, 6, 1, 0),
            "odd sizes in whole pixels: 7 x 3 in 8 x 4 at (0, 0); 5 x 9 in 8 x 4 its rows 2..6 at column 1");
        expect(GpuViewportControl.CenteredCopy(0, 360, 640, 360) == default && GpuViewportControl.CenteredCopy(640, 360, 640, 0) == default,
            "a frame or a window of no size: nothing copied");

        expect(CompositorWorker.OutputCapacity(0, 0, 640, 360, exact: false) == (640, 360), "OutputCapacity: the first ring is the frame's size exactly");
        expect(CompositorWorker.OutputCapacity(640, 360, 700, 400, exact: false) == (768, 512), "a frame that outgrows the ring: a new one rounded up to 128 a side (768 x 512 for 700 x 400)");
        expect(CompositorWorker.OutputCapacity(768, 512, 760, 500, exact: false) == (768, 512)
               && CompositorWorker.OutputCapacity(768, 512, 640, 360, exact: false) == (768, 512),
            "a frame that fits and fills at least half the ring keeps it (760 x 500, and 640 x 360, in 768 x 512)");
        expect(CompositorWorker.OutputCapacity(768, 384, 800, 300, exact: false) == (896, 384), "grown one way only: the other side keeps the ring's (800 x 300 in 768 x 384: 896 x 384)");
        expect(CompositorWorker.OutputCapacity(1920, 1152, 640, 360, exact: false) == (640, 384), "a frame under half the ring's area: a ring re-fitted to it (640 x 384 for 640 x 360 in 1920 x 1152)");
        expect(CompositorWorker.OutputCapacity(768, 512, 640, 360, exact: true) == (640, 360),
            "a frame read back (exact: its readback copies the whole texture) is always in a ring of exactly its size");
        expect(CompositorWorker.OutputCapacity(8000, 8000, 8150, 8190, exact: false) == (8192, 8192), "a grown ring is never more than 8192 a side");
    }

    // ----- player-resize-no-stretch: the source -----

    private static void Sources(Action<bool, string> expect)
    {
        var d3d = Read("Compositor", "Native", "D3D11.cs");
        var viewport = Read("Controls", "GpuViewportControl.cs");
        expect(d3d.Contains("public static nint CreateSwapChain(nint device, nint window, int width, int height, uint scaling)", StringComparison.Ordinal)
               && d3d.Contains("Scaling = scaling,", StringComparison.Ordinal) && !d3d.Contains("Scaling = ScalingStretch", StringComparison.Ordinal),
            "Dxgi.CreateSwapChain takes the swap chain's scaling");
        var player = Method(viewport, "public GpuViewportControl(CompositorClient client, nint window)");
        var mirror = Method(viewport, "public GpuViewportControl(GpuViewportControl player, nint window)");
        expect(player.Contains("_scaling = Dxgi.ScalingNone;", StringComparison.Ordinal) && mirror.Contains("_scaling = Dxgi.ScalingStretch;", StringComparison.Ordinal)
               && viewport.Contains("Dxgi.CreateSwapChain(device.DevicePointer, _window, width, height, _scaling)", StringComparison.Ordinal),
            "the player's swap chain is made with DXGI_SCALING_NONE (the desktop never stretches its buffer across a resized window); a side window's mirror keeps the stretch");
        var present = Method(viewport, "public bool PresentNewest(");
        expect(!present.Contains("(control.OutputWidth, control.OutputHeight) != _buffers", StringComparison.Ordinal)
               && present.Contains("_reader.CopyPresentedCentered(back, _backRtv, width, height, 8)", StringComparison.Ordinal)
               && present.Contains("(width, height) == _buffers", StringComparison.Ordinal)
               && Method(viewport, "public bool HasNewFrame").Contains("(width, height) != _buffers", StringComparison.Ordinal),
            "PresentNewest never refuses a frame for its size (it is centred), and a window of a new size presents again at once, paused too (HasNewFrame)");
        expect(Method(viewport, "private void Resize(").IndexOf("Com.Release(ref _backRtv);", StringComparison.Ordinal) is var release and >= 0
               && release < Method(viewport, "private void Resize(").IndexOf("Dxgi.ResizeBuffers", StringComparison.Ordinal)
               && Method(viewport, "private void DropSwapChain()").Contains("Com.Release(ref _backRtv);", StringComparison.Ordinal),
            "the back buffer's view is let go before every ResizeBuffers and with the swap chain");
        var main = Read("MainWindow.xaml.cs");
        expect(!main.Contains("VideoView.SizeChanged += (_, _) => ScheduleGpuState();", StringComparison.Ordinal),
            "a resize no longer plans the whole Sequence again (the mpv era's VideoView.SizeChanged => ScheduleGpuState is gone)");
        var surface = Read("Controls", "VideoSurface.cs");
        var gpuPreview = Read("MainWindow.GpuPreview.cs");
        expect(Method(surface, "protected override IntPtr WndProc(").Contains("if (msg == WmSize && hwnd == SurfaceHandle)", StringComparison.Ordinal)
               && Method(surface, "protected override IntPtr WndProc(").Contains("SurfaceResized?.Invoke();", StringComparison.Ordinal) && surface.Contains("WmSize = 0x0005", StringComparison.Ordinal)
               && gpuPreview.Contains("VideoView.SurfaceResized += () => _gpu?.InvalidateView();", StringComparison.Ordinal),
            "the engine is woken on the surface window's WM_SIZE, once its client rectangle has the new size (SizeChanged comes before the host moves it)");

        expect(CompositorProtocol.Version == 8, "the compositor's protocol is version 8 (a ring can be larger than its frames)");
        var offsets = new[] { "OutputContentWidth0", "OutputContentHeight0", "OutputContentWidth1", "OutputContentHeight1", "OutputContentWidth2", "OutputContentHeight2" }
            .Select(name => (int)Marshal.OffsetOf<ControlBlock>(name)).ToArray();
        expect(offsets.SequenceEqual([3176, 3180, 3184, 3188, 3192, 3196]) && offsets[^1] + 4 <= (int)Marshal.OffsetOf<ControlBlock>(nameof(ControlBlock.CacheHits)),
            "each output slot's content size is at 3176..3199 of the control block, before the cache's counters (3480)");
        var block = new ControlBlock { OutputWidth = 768, OutputHeight = 512 };
        var unwritten = block.OutputContentSize(1);
        block.SetOutputContentSize(1, 700, 400, 5, readback: false);
        block.SetOutputContentSize(2, 900, 600, 6, readback: true);
        expect(unwritten == (768, 512) && block.OutputContentSize(1) == (700, 400) && block.OutputContentSize(2) == (768, 512) && block.OutputContentSize(0) == (768, 512),
            "a slot's content size: what the worker wrote, never more than the ring, the whole ring where nothing was written (an older worker)");
        var tags = new[] { "OutputContentTag0", "OutputContentTag1", "OutputContentTag2" }.Select(name => (int)Marshal.OffsetOf<ControlBlock>(name)).ToArray();
        expect(tags.SequenceEqual([3200, 3204, 3208]) && block.OutputContentTag(1) == 10 && block.OutputContentTag(2) == 13 && block.OutputContentTag(0) == 0,
            "each slot's tag at 3200..3211: its ring's generation * 2, + 1 for a frame read back (a capture the player never shows)");
        var reader = File.ReadAllText(Path.Combine(Root(), "src", "Services", "CompositorFrameReader.cs"));
        expect(Method(reader, "internal bool CopyPresentedCentered(").Contains("if (ReadbackIn(index))", StringComparison.Ordinal)
               && Method(reader, "internal bool CopyPresentedRegionTo(").Contains("if (ReadbackIn(index))", StringComparison.Ordinal)
               && Method(reader, "internal (int Width, int Height) ContentSize(").Contains("OutputContentTag(index) >> 1 != _generation", StringComparison.Ordinal)
               && !reader.Contains("_ring = (control.OutputWidth, control.OutputHeight);", StringComparison.Ordinal),
            "the player and a mirror never show a capture's frame (read back), a slot drawn into another ring gives no size, and the ring's size comes from its textures");
    }

    // ----- live-encoded-preview-wipe: the pure rules -----

    private static void WipePure(Action<bool, string> expect)
    {
        // The handle's line (WipeLineX over PictureSpan, in the overlay's DIPs) against the shader's (Loopback.hlsl: Letterbox.X + wipe x
        // the picture's width, in the output's pixels, the native window's: the surface's DIPs times the display's scale, rounded). The
        // overlay's window is those pixels exactly, so its DIPs are the pixels over the scale (MainWindow.LoopbackViewSize).
        var worst = 0.0;
        var inverse = 0.0;
        var cases = 0;
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            foreach (var (zoom, panX, panY) in new[] { (1.0, 0.0, 0.0), (2.0, 0.25, 0.0), (3.0, -0.1, 0.2) })
            {
                foreach (var (dipWidth, dipHeight) in new[] { (1000.0, 1000.0), (1601.0, 899.0) })
                {
                    var (pixelWidth, pixelHeight) = ((int)Math.Round(dipWidth * scale), (int)Math.Round(dipHeight * scale));
                    var header = new FrameSceneHeader { CanvasWidth = 1920, CanvasHeight = 1080, ViewScale = (float)zoom, ViewPanX = (float)panX, ViewPanY = (float)panY };
                    var (x, _, s) = SceneRenderer.Letterbox(header, pixelWidth, pixelHeight);
                    var (left, _, width, _) = GpuPreviewEngine.PictureRectOf(1920, 1080, zoom, panX, panY, pixelWidth / scale, pixelHeight / scale);
                    foreach (var w in new[] { 0.0, 0.37, 0.5, 1.0 })
                    {
                        var line = GpuPreviewEngine.WipeLineX(w, (left, width)) * scale;
                        var shader = x + w * 1920 * s;
                        worst = Math.Max(worst, Math.Abs(line - shader));
                        if (width > 1)
                            inverse = Math.Max(inverse, Math.Abs(GpuPreviewEngine.WipeAt(GpuPreviewEngine.WipeLineX(w, (left, width)), (left, width)) - w));
                        cases++;
                    }
                }
            }
        }

        Console.WriteLine(FormattableString.Invariant($"  (the wipe's handle against the shader's line: {cases} cases, worst {worst:0.000} px)"));
        expect(cases == 96 && worst <= 0.5,
            "at display scales 1, 1.25, 1.5 and 2, three zooms and pans and two player sizes, the handle's line lies within half a pixel of the line the worker draws (96 cases)");
        expect(inverse < 1e-9, "WipeAt is the inverse of WipeLineX (the drag puts the wipe where the pointer is)");
        expect(GpuPreviewEngine.WipeAt(-50, (100, 400)) == 0 && GpuPreviewEngine.WipeAt(900, (100, 400)) == 1 && GpuPreviewEngine.WipeAt(300, (100, 400)) == 0.5
               && GpuPreviewEngine.WipeAt(300, (100, 0.5)) == 0.5 && GpuPreviewEngine.WipeAt(double.NaN, (100, 400)) == 0.5,
            "WipeAt keeps the wipe in 0..1 off the picture, and is 0.5 for a picture of no width or a pointer nowhere");
        expect(GpuPreviewEngine.WipeLineX(2, (100, 400)) == 500 && GpuPreviewEngine.WipeLineX(-1, (100, 400)) == 100, "WipeLineX keeps the line on the picture");
    }

    // ----- live-encoded-preview-wipe: the source -----

    private static void WipeSources(Action<bool, string> expect)
    {
        var loopback = Read("Compositor", "ExportLoopbackPreview.cs");
        var collect = Method(loopback, "public void Collect(ref ControlBlock control)");
        expect(collect.Contains("var budget = _playing ? PlayingWaitMs : _submitted ? PausedWaitMs : 0;", StringComparison.Ordinal)
               && Method(loopback, "public void Render(").Contains("_submitted = false;", StringComparison.Ordinal)
               && Method(loopback, "public void Render(").Contains("(_drained, _submitted) = (false, true);", StringComparison.Ordinal),
            "Collect waits for a paused frame only in the scene that handed it to the encoder; a wipe or loupe moved meanwhile takes what is back without waiting");
        expect(Method(loopback, "public bool Poll()").Contains("TryTakeOutput()", StringComparison.Ordinal) && Method(loopback, "public bool Poll()").Contains("if (!_playing && !_drained", StringComparison.Ordinal)
               && Method(loopback, "public void Render(").Contains("decoders.FramesDecoded != _decodedAtRender", StringComparison.Ordinal),
            "Poll takes what the encoder gave out, never waiting (it drains a paused frame the submitting scene found not yet in the encoder); a picture that lacked a video frame is rendered again only once more frames were decoded");
        var worker = Read("Compositor", "CompositorWorker.cs");
        var work = worker[worker.IndexOf("var pending = loopback?.Pending == true", StringComparison.Ordinal)..];
        work = work[..work.IndexOf("catch (GpuException ex) when (ex.IsDeviceLost)", StringComparison.Ordinal)];
        expect(work.Contains("if (pending && loopback!.Poll())", StringComparison.Ordinal) && !Regex.IsMatch(work, @"if \(pending\)\s*redraw = true;")
               && work.Contains("pending ? 2 :", StringComparison.Ordinal),
            "the worker still looks every 2 ms while a paused frame is in the encoder, but draws again only when Poll says the picture changed");
        var window = Read("MainWindow.LoopbackPreview.cs");
        var place = Method(window, "private void PlaceLoopbackWipe()");
        var ensure = Method(window, "private void EnsureLoopbackWipe()");
        expect(place.Contains("GpuPreviewEngine.WipeLineX(", StringComparison.Ordinal) && ensure.Contains("GpuPreviewEngine.WipeAt(", StringComparison.Ordinal)
               && place.Contains("VisualTreeHelper.GetDpi(PlayerOverlay)", StringComparison.Ordinal) && place.Contains("* scale) / scale", StringComparison.Ordinal)
               && place.Contains("LoopbackViewSize()", StringComparison.Ordinal) && ensure.Contains("LoopbackViewSize()", StringComparison.Ordinal)
               && Method(window, "private void LoopbackPointerMoved(").Contains("LoopbackViewSize()", StringComparison.Ordinal),
            "the handle is placed with WipeLineX and dragged with WipeAt, its edge rounded to a device pixel, and the wipe and the Magnifier measure the same element");
        expect(window.Contains("RenderTransform = _loopbackWipeUnzoom", StringComparison.Ordinal) && Method(window, "private void PlaceLoopbackWipe()").Contains("_loopbackWipeUnzoom.Matrix = view;", StringComparison.Ordinal)
               && window.Contains("GpuPreviewEngine.WipeAt(e.GetPosition(_loopbackWipeLayer).X, span)", StringComparison.Ordinal)
               && Read("MainWindow.Keybinds.cs") is var keys && Method(keys, "private void ApplyPlayerView()").Contains("PlaceLoopbackWipe();", StringComparison.Ordinal),
            "the wipe's layer undoes the player's zoom and pan (the overlay's view matrix, inverted), so a zoomed player moves the handle once and never scales it; the drag reads the pointer in that layer; a zoom re-places it at once");
        expect(!window.Contains("GetDisplayedVideoArea", StringComparison.Ordinal) && Method(window, "private void PlaceLoopbackWipe()").Contains("PictureSpan(", StringComparison.Ordinal),
            "the wipe maps through the picture's rectangle (PictureSpan, the worker's letterbox), never the crop canvas's GetDisplayedVideoArea");
    }

    private sealed class Harness(CompositorClient client, CompositorFrameReader reader)
    {
        private const int W = 640, H = 360;

        private ref ControlBlock Control => ref client.Memory.Control;

        private void Begin(int flags, int canvasWidth, int canvasHeight, int outputWidth, int outputHeight)
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.Flags) = (canvasWidth, canvasHeight, flags);
            (scene.OutputWidth, scene.OutputHeight, scene.ViewScale) = (outputWidth, outputHeight, 1);
        }

        private void Pattern(int index, int kind, float x, float y, float width, float height)
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

        // The Live Encoded Preview's fields, as GpuPreviewEngine.WriteLoopback writes them.
        private void Loopback(int mode, float wipe, long stamp, bool bypass, int testFlags = 0)
        {
            ref var scene = ref client.Scene;
            scene.Flags |= FrameSceneHeader.FlagLoopback;
            (scene.LoopbackCodec, scene.LoopbackRateControl, scene.LoopbackBitrateKbps, scene.LoopbackQuality, scene.LoopbackQualityVsSpeed) = (1, 0, 8000, 55, 100);
            (scene.LoopbackMode, scene.LoopbackWipeX, scene.LoopbackFrameRate, scene.LoopbackContentStamp, scene.LoopbackHeatGain) = (mode, wipe, 30, stamp, 10);
            scene.LoopbackTestFlags = (bypass ? 1 : 0) | testFlags;
        }

        private byte[]? Frame(int width, int height, out string problem)
        {
            problem = "";
            var sequence = client.Publish();
            if (!client.WaitForFrame(sequence, 10_000))
            {
                problem = $"no frame within 10 s ({client.State}: {client.Message})";
                return null;
            }

            if (!reader.TryRead(out var bgra, out var readWidth, out var readHeight) || (readWidth, readHeight) != (width, height))
            {
                problem = $"read back at {readWidth} x {readHeight}, not {width} x {height}";
                return null;
            }

            return bgra;
        }

        private static int[] Pixel(byte[] frame, int width, int x, int y) => [frame[(y * width + x) * 4 + 2], frame[(y * width + x) * 4 + 1], frame[(y * width + x) * 4]];

        // ----- player-resize-no-stretch: a dock splitter's drag, through a real worker -----

        public void Drag(Action<bool, string> expect)
        {
            // A square canvas (a gradient over it) in a wide player: bars left and right, where SceneRenderer.Letterbox says.
            var (changes, generation, problems, made) = (0, -1, new List<string>(), 0);
            for (var k = 0; k < 40; k++)
            {
                var (width, height) = (640 + 3 * k, 360 + 3 * k / 2);
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames, 400, 400, width, height);
                Pattern(0, 1, 0, 0, 400, 400);
                var frame = Frame(width, height, out var problem);
                if (frame is null)
                {
                    problems.Add($"{width} x {height}: {problem}");
                    continue;
                }

                var now = Volatile.Read(ref Control.OutputGeneration);
                if (generation >= 0 && now != generation)
                    changes++;
                generation = now;
                if (k == 1)
                    made = Volatile.Read(ref Control.TexturesMade);

                var (x, _, scale) = SceneRenderer.Letterbox(new FrameSceneHeader { CanvasWidth = 400, CanvasHeight = 400, ViewScale = 1 }, width, height);
                var (left, right, middle) = ((int)Math.Floor(x), (int)Math.Ceiling(x + 400 * scale), height / 2);
                var black = Pixel(frame, width, left - 2, middle).Max() <= 2 && Pixel(frame, width, right + 1, middle).Max() <= 2;
                var picture = Pixel(frame, width, left + 2, middle).Sum() > 100 && Pixel(frame, width, right - 3, middle).Sum() > 100;
                var centre = Pixel(frame, width, width / 2, middle).All(c => Math.Abs(c - 128) <= 8);
                if (!black || !picture || !centre)
                    problems.Add($"{width} x {height}: the picture is not where the letterbox puts it (bars {black}, edges {picture}, middle {centre})");
            }

            Console.WriteLine(FormattableString.Invariant(
                $"  (the drag: 40 sizes, {changes} new output ring(s), the ring now {Control.OutputWidth} x {Control.OutputHeight}, {Volatile.Read(ref Control.TexturesMade) - made} texture(s) made after the second frame)"));
            foreach (var problem in problems.Take(5))
                Console.WriteLine("  " + problem);
            expect(problems.Count == 0,
                "40 frames of a drag from 640 x 360 to 757 x 418, 3 pixels a step: each read back at exactly its size, its bars and picture where SceneRenderer.Letterbox puts them");
            expect(changes is >= 1 and <= 3 && Control.OutputWidth >= 757 && Control.OutputHeight >= 418,
                "the worker made at most 3 output rings for the 40 sizes (one a size before 5.4.1A), the last one large enough for the largest frame");
        }

        public void ReadbackExact(Action<bool, string> expect)
        {
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback, 400, 400, W, H);
            Pattern(0, 1, 0, 0, 400, 400);
            client.EnsureReadback((long)W * H * 4);
            var sequence = client.Publish();
            var back = new byte[W * H * 4];
            var readBack = client.TryReadback(sequence, back, out var backWidth, out var backHeight, 10_000) && (backWidth, backHeight) == (W, H);
            var read = reader.TryRead(out var shown, out var width, out var height) && (width, height) == (W, H);
            expect(readBack && read && (Control.OutputWidth, Control.OutputHeight) == (W, H) && shown.AsSpan().SequenceEqual(back),
                "a frame read back after the drag (FlagReadback) is drawn in a ring of exactly its size, 640 x 360: its readback and the reader's copy are the same 640 x 360 pixels");
        }

        // The reader's copy into a texture of another size (the viewport's back buffer while a resize is under way).
        public unsafe void Centred(Action<bool, string> expect)
        {
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames, 400, 400, 700, 400);
            Pattern(0, 1, 0, 0, 400, 400);
            var frame = Frame(700, 400, out var problem);
            if (frame is null || reader.Device is not { } device)
            {
                expect(false, $"the reader's centred copy: a 700 x 400 frame ({problem})");
                return;
            }

            var ring = (Control.OutputWidth, Control.OutputHeight);
            var results = new List<string>();
            foreach (var (targetWidth, targetHeight) in new[] { (760, 420), (600, 380), (700, 400) })
            {
                using var target = device.CreateTexture(targetWidth, targetHeight, DxgiFormat.B8G8R8A8Unorm, renderTarget: true);
                device.Context.ClearRenderTargetView(target.Rtv, 1, 1, 1, 1);
                if (!reader.CopyPresentedCentered(target.Texture, target.Rtv, targetWidth, targetHeight, 1000))
                {
                    results.Add($"{targetWidth} x {targetHeight}: not copied");
                    continue;
                }

                var staging = device.CreateStaging(targetWidth, targetHeight, DxgiFormat.B8G8R8A8Unorm);
                var copied = new byte[targetWidth * targetHeight * 4];
                try
                {
                    device.Context.CopyResource(staging, target.Texture);
                    Com.Check(device.Context.Map(staging, 0, D3D11.MapRead, 0, out var mapped), "Map");
                    for (var y = 0; y < targetHeight; y++)
                        new ReadOnlySpan<byte>(mapped.Data + (long)y * mapped.RowPitch, targetWidth * 4).CopyTo(copied.AsSpan(y * targetWidth * 4));
                    device.Context.Unmap(staging, 0);
                }
                finally
                {
                    Com.Release(ref staging);
                }

                var (srcLeft, srcTop, srcRight, srcBottom, dstX, dstY) = GpuViewportControl.CenteredCopy(700, 400, targetWidth, targetHeight);
                var wrong = 0;
                for (var y = 0; y < targetHeight; y++)
                {
                    for (var x = 0; x < targetWidth; x++)
                    {
                        var (sx, sy) = (x - dstX + srcLeft, y - dstY + srcTop);
                        var inside = sx >= srcLeft && sx < srcRight && sy >= srcTop && sy < srcBottom;
                        var at = (y * targetWidth + x) * 4;
                        for (var c = 0; c < 3; c++)
                        {
                            var want = inside ? frame[(sy * 700 + sx) * 4 + c] : 0;
                            if (copied[at + c] != want)
                            {
                                wrong++;
                                break;
                            }
                        }
                    }
                }

                if (wrong > 0)
                    results.Add($"{targetWidth} x {targetHeight}: {wrong} pixel(s) not the frame 1:1 at ({dstX}, {dstY}) on black");
            }

            foreach (var result in results)
                Console.WriteLine("  " + result);
            expect(ring != (700, 400) && results.Count == 0,
                "the reader copies a 700 x 400 frame from a larger ring into a 760 x 420 window 1:1 in its middle on black, into a 600 x 380 one its middle cropped, into a 700 x 400 one exactly: every pixel, nothing scaled");
        }

        // ----- live-encoded-preview-wipe: the codec bypassed (every machine) -----

        public void WipeBypass(Action<bool, string> expect)
        {
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames, W, H, W, H);
            Pattern(0, 1, 0, 0, W, H);
            Loopback(0, 0.5f, stamp: 41, bypass: true);
            var encoded = Frame(W, H, out var problem);
            var (count, made) = (Control.LoopbackFramesEncoded, Volatile.Read(ref Control.TexturesMade));
            if (encoded is null)
            {
                expect(false, "the wipe (bypassed): the encoded picture first (" + problem + ")");
                return;
            }

            var (misplaced, cpu, draw) = (new List<string>(), 0.0, 0.0);
            for (var k = 0; k < 20; k++)
            {
                var wipe = (float)(0.05 + k * 0.9 / 19);
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames, W, H, W, H);
                Pattern(0, 1, 0, 0, W, H);
                Loopback(1, wipe, stamp: 41, bypass: true);
                var frame = Frame(W, H, out problem);
                if (frame is null)
                {
                    misplaced.Add($"wipe {wipe:0.00}: {problem}");
                    continue;
                }

                (cpu, draw) = (cpu + Control.LastFrameCpuMs, draw + Control.LastDrawMs);

                // The divider: the columns that differ from the encoded picture in every row (a light line on the gradient).
                var columns = new List<int>();
                for (var x = 0; x < W; x++)
                {
                    var rows = 0;
                    for (var y = 0; y < H; y++)
                    {
                        var at = (y * W + x) * 4;
                        if (Math.Max(Math.Abs(frame[at] - encoded[at]), Math.Max(Math.Abs(frame[at + 1] - encoded[at + 1]), Math.Abs(frame[at + 2] - encoded[at + 2]))) > 40)
                            rows++;
                    }

                    if (rows == H)
                        columns.Add(x);
                }

                var expected = (int)Math.Round(wipe * W);
                if (columns.Count == 0 || columns.Any(c => Math.Abs(c - expected) > 1))
                    misplaced.Add(FormattableString.Invariant($"wipe {wipe:0.00}: the divider at [{string.Join(", ", columns)}], not {expected} +-1"));
            }

            Console.WriteLine(FormattableString.Invariant(
                $"  (the wipe dragged, the codec bypassed: the worker's frame {cpu / 20:0.00} ms of processor time, its draw {draw / 20:0.00} ms, on average over 20 wipe-only scenes)"));
            foreach (var problemLine in misplaced.Take(5))
                Console.WriteLine("  " + problemLine);
            expect(Control.LoopbackFramesEncoded == count && Volatile.Read(ref Control.TexturesMade) == made,
                "20 scenes with the same content stamp, only the wipe moved (0.05 to 0.95): nothing is rendered or encoded again and no texture is made");
            expect(misplaced.Count == 0, "in each, the divider is the column at round(wipe x 640) +-1, in every row (the canvas fills the 640 x 360 output)");
        }

        // ----- live-encoded-preview-wipe: a real encoder (where this PC has an H.264 one) -----

        public void WipeRealCodec(Action<bool, string> expect)
        {
            const string Line = "with a real H.264 encoder, the wipe moved right after a new paused frame went to it: no wipe scene waits for the encoder (each back within ExportLoopbackPreview.PausedWaitMs, 120 ms), nothing encoded again; else nothing to wait for";
            const string PollLine = "and the frame still in the encoder is drawn by the worker's poll once it is back (or given up), with no new scene and at most 3 redraws (not one every 2 ms)";
            const string ForcedLine = "a paused frame its scene never waited for (test flag 2) comes back through the worker's Poll alone: drawn with no new scene, in 1 to 3 redraws, before it would be given up";
            void Busy()
            {
                Pattern(0, 2, 0, 0, W, H);
                Pattern(1, 3, W * 0.2f, H * 0.1f, W * 0.6f, H * 0.8f);
            }

            // The encoder made and warm (as export-loopback's RealCodec): the same paused scene until its frame is back or there is none.
            var watch = Stopwatch.StartNew();
            var state = ExportLoopbackState.Off;
            while (watch.ElapsedMilliseconds < 8000)
            {
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames, W, H, W, H);
                Busy();
                Loopback(1, 0.5f, stamp: 70, bypass: false);
                _ = Frame(W, H, out _);
                state = (ExportLoopbackState)Control.LoopbackState;
                if (state is not (ExportLoopbackState.Starting or ExportLoopbackState.Off) && (state != ExportLoopbackState.Running || Control.LoopbackFramesEncoded > 0))
                    break;
                Thread.Sleep(20);
            }

            if (state != ExportLoopbackState.Running)
            {
                Console.WriteLine($"  note  no H.264 loopback on this PC ({state}, 0x{Control.LoopbackHResult:X8}): no encoder to wait for");
                expect(true, Line);
                expect(true, PollLine);
                expect(true, ForcedLine);
                return;
            }

            // A new paused frame handed to the encoder (its scene may wait for it), then the wipe dragged at once.
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames, W, H, W, H);
            Busy();
            Loopback(1, 0.5f, stamp: 71, bypass: false);
            var before = Control.LoopbackFramesEncoded;
            var submit = Stopwatch.StartNew();
            _ = Frame(W, H, out _);
            var submitMs = submit.Elapsed.TotalMilliseconds;
            var (worst, total, cpu) = (0.0, 0.0, 0.0);
            var failed = 0;
            for (var k = 0; k < 12; k++)
            {
                Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames, W, H, W, H);
                Busy();
                Loopback(1, 0.2f + k * 0.05f, stamp: 71, bypass: false);
                var scene = Stopwatch.StartNew();
                var sequence = client.Publish();
                if (!client.WaitForFrame(sequence, 5000))
                    failed++;
                var ms = scene.Elapsed.TotalMilliseconds;
                (worst, total, cpu) = (Math.Max(worst, ms), total + ms, cpu + Control.LastFrameCpuMs);
            }

            var name = CompositorSharedMemory.ReadString(Control.LoopbackEncoderName);
            var backDuring = Control.LoopbackFramesEncoded - before;
            Console.WriteLine(FormattableString.Invariant(
                $"  (real codec {name}: the new paused frame's scene {submitMs:0.0} ms; 12 wipe-only scenes after it {total / 12:0.0} ms on average, {worst:0.0} ms at worst, the worker's frame {cpu / 12:0.00} ms; {backDuring} frame(s) back by then)"));
            expect(failed == 0 && worst < ExportLoopbackPreview.PausedWaitMs && backDuring <= 1, Line);

            // Nothing published from here: the worker alone draws the frame when the encoder gives it back (Poll), or once it is given up.
            var rendered = Volatile.Read(ref Control.FramesRendered);
            var waited = Stopwatch.StartNew();
            while (waited.ElapsedMilliseconds < 2600 && Control.LoopbackFramesEncoded - before < 1)
                Thread.Sleep(10);
            Thread.Sleep(150);
            var (redraws, back) = (Volatile.Read(ref Control.FramesRendered) - rendered, Control.LoopbackFramesEncoded - before);
            Console.WriteLine(FormattableString.Invariant(
                $"  (then, with no scene: the frame {(backDuring > 0 ? "was already back" : back > 0 ? "back" : "given up")} after {waited.ElapsedMilliseconds} ms, {redraws} redraw(s) by the worker)"));
            expect(redraws <= 3 && (backDuring > 0 || redraws >= 1), PollLine);

            // The poll's own path, forced: a new paused frame whose scene does not wait at all (test flag 2), then no scene. Only the
            // worker's Poll (its drain included) can bring the frame back and draw it.
            Begin(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames, W, H, W, H);
            Busy();
            Loopback(1, 0.5f, stamp: 72, bypass: false, testFlags: 2);
            var beforeForced = Control.LoopbackFramesEncoded;
            var renderedForced = Volatile.Read(ref Control.FramesRendered) + 1; // the scene's own render
            var forcedScene = Stopwatch.StartNew();
            _ = Frame(W, H, out _);
            var forcedSceneMs = forcedScene.Elapsed.TotalMilliseconds;
            var backAfter = Stopwatch.StartNew();
            while (backAfter.ElapsedMilliseconds < 2600 && Control.LoopbackFramesEncoded - beforeForced < 1)
                Thread.Sleep(5);
            var backMs = backAfter.ElapsedMilliseconds;
            Thread.Sleep(150);
            var (forcedRedraws, forcedBack) = (Volatile.Read(ref Control.FramesRendered) - renderedForced, Control.LoopbackFramesEncoded - beforeForced);
            Console.WriteLine(FormattableString.Invariant(
                $"  (forced poll: the scene {forcedSceneMs:0.0} ms with no wait; the frame {(forcedBack > 0 ? "back" : "not back")} after {backMs} ms, {forcedRedraws} redraw(s) by the worker)"));
            expect(forcedBack >= 1 && forcedRedraws is >= 1 and <= 3 && backMs < 2000, ForcedLine);
        }
    }
}
