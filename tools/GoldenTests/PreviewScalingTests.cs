using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.4: Dynamic Preview Scaling at high DPI (dynamic-preview-scaling). The pure rules of GpuPreviewEngine.Scaling.cs: where a run
/// starts (1:1 DIP on battery, or on a card that shares the PC's memory with more than one tab open), step 1 at Windows' monitor scale
/// capped at 720 pixels on the picture's short side and at the Sequence's own pixels, the steps down (one that changes nothing passed
/// over, a floor of a quarter), the tumbling count; the worker's DrawScale; the tab count and the shared-memory field the engine reads;
/// then a real worker drawing scenes alternately whole and at half: the frame always the viewport's size, the picture where the letterbox
/// puts it, the same colours, no dark fringe along the far edges, really drawn at half (a checker's cells twice as wide), and no texture
/// made once both sizes were drawn. Every line says what it proved.
/// </summary>
public static class PreviewScalingTests
{
    public static IEnumerable<GraphResult> All()
    {
        var text = new StringBuilder("# Dynamic Preview Scaling at high DPI (5.4.4): 1:1 DIP at Windows' monitor scale, where a run starts, its steps down, whole when paused.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            Rules(Expect);
            Wiring(Expect);
            using var client = new CompositorClient(adapter: -1, keepStandby: false);
            using var reader = new CompositorFrameReader(client);
            Worker(client, reader, Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("dynamic-preview-scaling", text.ToString(), problems, 0);
        yield return Case("dynamic-preview-scaling-blend-copy", "# A blend's copy of the canvas is kept per canvas (5.4.4): drawing whole and smaller by turns makes no texture with a blend on screen.", BlendCopy);
        yield return Case("dynamic-preview-scaling-whole-still", "# A still read while paused is a whole frame (5.4.4); the one drawn smaller while playing or scrubbing only when the whole one is late.", WholeStill);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    /// <summary>The cases that need the WPF Application (run after UiScaleTests made it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
    }

    private static bool Near(double a, double b, double within = 0.002) => Math.Abs(a - b) <= within;

    // ----- The pure rules -----

    private static void Rules(Action<bool, string> expect)
    {
        expect(GpuPreviewEngine.StartStep(onBattery: true, unifiedMemory: false, openTabs: 1) == 1 && GpuPreviewEngine.StartStep(true, true, 3) == 1
               && GpuPreviewEngine.StartStep(false, true, 2) == 1 && GpuPreviewEngine.StartStep(false, true, 1) == 0
               && GpuPreviewEngine.StartStep(false, false, 5) == 0 && GpuPreviewEngine.StartStep(false, false, 1) == 0,
            "StartStep: 1:1 DIP on battery (any card, any tabs), or on a card that shares the PC's memory with more than one tab open; whole otherwise (a big card on AC with many tabs, or one tab)");

        // The XPS 13 at 200 % (Windows' monitor scale 2): the Standard layout's player about 1610 x 757 pixels, the Vertical Creator Split's about 806 x 1665.
        var rows = new (string Name, int ViewWidth, int ViewHeight, int CanvasWidth, int CanvasHeight)[]
        {
            ("Standard, 2560 x 1440", 1610, 757, 2560, 1440),
            ("Standard, 1080 x 1920", 1610, 757, 1080, 1920),
            ("Vertical Creator, 1080 x 1920", 806, 1665, 1080, 1920),
        };
        foreach (var (name, viewWidth, viewHeight, canvasWidth, canvasHeight) in rows)
        {
            var steps = Enumerable.Range(0, 4).Select(s => GpuPreviewEngine.StepScale(s, viewWidth, viewHeight, canvasWidth, canvasHeight, 2.0)).ToArray();
            expect(steps[0] == 1 && Near(steps[1], 0.5) && Near(steps[2], 0.375) && Near(steps[3], 0.25)
                   && GpuPreviewEngine.NextStep(0, viewWidth, viewHeight, canvasWidth, canvasHeight, 2.0) == 1
                   && GpuPreviewEngine.NextStep(1, viewWidth, viewHeight, canvasWidth, canvasHeight, 2.0) == 2
                   && GpuPreviewEngine.NextStep(3, viewWidth, viewHeight, canvasWidth, canvasHeight, 2.0) == 3,
                FormattableString.Invariant($"the XPS 13 at 200 % ({name} in {viewWidth} x {viewHeight}): steps 1, 0.5 (1:1 DIP, a quarter of the pixels), 0.375, 0.25; each step down is the next, the deepest stays"));
        }

        // HandPeg's own content scale (1.5 on the XPS) is not what decides: the monitor's 2.0 is.
        expect(Near(GpuPreviewEngine.DipScale(1610, 757, 2560, 1440, 1.5), 1 / 1.5) && Near(GpuPreviewEngine.DipScale(1610, 757, 2560, 1440, 2.0), 0.5),
            "DipScale is one over the monitor scale it is given (0.5 at 2.0, where the 5.4.3 step stayed whole on this XPS)");

        // A 1440p desktop at 100 %: the picture about 1198 x 674, no cap bites: step 1 is whole, passed over (AC goes 1, 0.75, 0.5).
        expect(GpuPreviewEngine.StepScale(1, 1198, 674, 1920, 1080, 1.0) == 1 && GpuPreviewEngine.NextStep(0, 1198, 674, 1920, 1080, 1.0) == 2
               && Near(GpuPreviewEngine.StepScale(2, 1198, 674, 1920, 1080, 1.0), 0.75) && GpuPreviewEngine.NextStep(2, 1198, 674, 1920, 1080, 1.0) == 3,
            "a 1440p desktop at 100 %: step 1 changes nothing and is passed over (whole, then 0.75, then 0.5)");

        // 4K at 100 %: the picture about 1938 x 1090: step 1 is 720 / 1090, the picture 1280 x 720.
        var fourK = GpuPreviewEngine.StepScale(1, 1938, 1090, 1920, 1080, 1.0);
        expect(Near(fourK, 720.0 / 1090, 0.001) && GpuPreviewEngine.NextStep(0, 1938, 1090, 1920, 1080, 1.0) == 1,
            FormattableString.Invariant($"a 4K desktop at 100 %: step 1 caps the picture's short side at 720 pixels ({fourK:0.000} of 1938 x 1090: about 1280 x 720)"));

        // A 9:16 Sequence in a tall player at 100 %: the cap is on the picture's short side (its width), 720 x 1280.
        expect(Near(GpuPreviewEngine.DipScale(1000, 1800, 1080, 1920, 1.0), 720.0 / 1000, 0.001),
            "a 9:16 picture 1000 pixels wide: step 1 makes it 720 wide (the cap is on the letterboxed picture's short side, not the viewport's)");

        // The Sequence's own pixels: a 640 x 360 Sequence in a 3840 x 2160 player draws at most its own pixels; the floor holds.
        expect(Near(GpuPreviewEngine.DipScale(3840, 2160, 640, 360, 1.0), 1 / 6.0) && GpuPreviewEngine.StepScale(1, 3840, 2160, 640, 360, 1.0) == GpuPreviewEngine.LeastScale
               && GpuPreviewEngine.StepScale(3, 3840, 2160, 640, 360, 1.0) == GpuPreviewEngine.LeastScale && GpuPreviewEngine.NextStep(1, 3840, 2160, 640, 360, 1.0) == 1,
            "a small Sequence in a large player: step 1 is no more than its own pixels (1/6), never under the 0.25 floor, and no step goes below it");
        var atHundred = GpuPreviewEngine.DipScale(1610, 757, 2560, 1440, 1.0);
        expect(Near(atHundred, 720.0 / 757, 0.001) && GpuPreviewEngine.DipScale(1610, 757, 2560, 1440, 0) == atHundred
               && GpuPreviewEngine.DipScale(1610, 757, 2560, 1440, double.NaN) == atHundred
               && GpuPreviewEngine.DipScale(0, 0, 2560, 1440, 2.0) == 1 && GpuPreviewEngine.StepScale(-1, 1610, 757, 2560, 1440, 2.0) == 1,
            "no monitor scale known (a window gone) counts as 100 %: only the 720 cap is left (0.951 of a 757-pixel picture); no viewport, or no step, is whole");

        // The tumbling count: 3 slow of 30 step down once settled; 2 slow do not; not settled, the window runs out and starts again.
        (int Frames, int Slow, bool Step) Run(IEnumerable<bool> slow, bool settled)
        {
            var state = (Frames: 0, Slow: 0, Step: false);
            foreach (var one in slow)
            {
                state = GpuPreviewEngine.Counted(state.Frames, state.Slow, one, settled);
                if (state.Step)
                    return state;
            }

            return state;
        }

        var third = Run([false, true, false, true, false, true], settled: true);
        expect(third.Step && third is { Frames: 0, Slow: 0 }, "Counted: the third slow frame of a window steps the scale down once settled, and the count starts again");
        var two = Run(Enumerable.Range(0, 30).Select(i => i is 3 or 17), settled: true);
        expect(!two.Step && two is { Frames: 0, Slow: 0 }, "two slow frames in a window of 30 do not; the window of 30 then starts again");
        var unsettled = Run(Enumerable.Range(0, 30).Select(i => i < 5), settled: false);
        expect(!unsettled.Step && unsettled is { Frames: 0, Slow: 0 }, "within half a second of the last step no slow frames step it: the window runs out and starts again");
        expect(GpuPreviewEngine.StepWindow == 30 && GpuPreviewEngine.SlowToStep == 3 && GpuPreviewEngine.SlowDrawMs == 12 && GpuPreviewEngine.CrispAfter == 0.080,
            "a window is 30 frames, 3 slow ones step, slow is over 12 ms, and a scrub's picture is whole again 80 ms after its last move");

        // The worker: only a flat letterboxed player frame is drawn smaller.
        float Draw(float scale, int flags, int viewMode = 0, long loupe = 0, bool raw = false)
        {
            var header = new FrameSceneHeader { PreviewScale = scale, Flags = flags, ViewMode = viewMode, LoupeClipKey = loupe };
            return SceneRenderer.DrawScale(header, raw);
        }

        const int box = FrameSceneHeader.FlagLetterbox;
        expect(Draw(0.5f, box) == 0.5f && Draw(0.1f, box) == 0.25f && Draw(0, box) == 1 && Draw(1, box) == 1
               && Draw(0.5f, box, viewMode: 1) == 1 && Draw(0.5f, box, loupe: 7) == 1 && Draw(0.5f, box, raw: true) == 1
               && Draw(0.5f, box | FrameSceneHeader.FlagReadback) == 1 && Draw(0.5f, box | FrameSceneHeader.FlagExactFrames) == 1 && Draw(0.5f, 0) == 1,
            "SceneRenderer.DrawScale: a flat letterboxed frame at its scale (never under 0.25); whole for no scale (every other client), the 3D view, the loupe, raw, a readback, exact frames and an unletterboxed frame (the Live Encoded Preview)");

        // What the engine reads: the worker's shared-memory answer and the tab count.
        expect((int)Marshal.OffsetOf<ControlBlock>(nameof(ControlBlock.UnifiedMemory)) == 3212 && CompositorProtocol.Version == 8,
            "ControlBlock.UnifiedMemory is at 3212 (the worker's answer; the editor never loads DXCore), the protocol still version 8");
        var root = Path.Combine(Environment.GetEnvironmentVariable("HANDPEG_DATA_DIR") ?? Path.GetTempPath(), "preview-scaling-tabs");
        var tabs = new ProjectTabs(root, session: "tabs");
        var one = ProjectTabs.OpenCount;
        var second = tabs.Add("Second");
        var two2 = ProjectTabs.OpenCount;
        tabs.Insert(0, "Third");
        var three = ProjectTabs.OpenCount;
        tabs.Close(second);
        var closed = ProjectTabs.OpenCount;
        var adopted = tabs.Adopt(new ProjectTabHeader { Id = "adopted", Title = "Left behind" });
        expect(one == 1 && two2 == 2 && three == 3 && closed == 2 && adopted && ProjectTabs.OpenCount == 3 && ProjectTabs.OpenCount == tabs.Count,
            "ProjectTabs.OpenCount follows the strip: 1 at start, 2 and 3 as tabs are added and inserted, 2 after a close, 3 after a tab left behind is taken in");
    }

    // ----- The wiring (text: the pump's thread cannot be driven without a player) -----

    private static void Wiring(Action<bool, string> expect)
    {
        var scaling = Read("Services", "GpuPreviewEngine.Scaling.cs");
        var forFrame = Method(scaling, "private float PreviewScaleFor(");
        expect(forFrame.Contains("_viewport.MonitorScale", StringComparison.Ordinal) && forFrame.Contains("StartStep(_onBattery, UnifiedMemory, ProjectTabs.OpenCount)", StringComparison.Ordinal)
               && forFrame.Contains("if (start > _scaleStep)", StringComparison.Ordinal) && forFrame.Contains("StepScale(_scaleStep,", StringComparison.Ordinal)
               && !Regex.IsMatch(scaling, @"_native\b"),
            "the pump asks the monitor's scale, the shared-memory answer and the tab count every frame; a run never sits above its start step (a second tab or the charger pulled mid-run); the 5.4.3 'Sequence's pixels' step is gone");
        var count = Method(scaling, "private void CountFrame(");
        expect(count.Contains("_lateOwed", StringComparison.Ordinal) && count.Contains("NextStep(_scaleStep,", StringComparison.Ordinal) && count.Contains("Counted(", StringComparison.Ordinal),
            "a late frame is counted as it is late and not again as it arrives; a step down goes through NextStep (a step that changes nothing passed over)");
        expect(Method(scaling, "private void EndScaledRun(").Contains("AppLog.Write(", StringComparison.Ordinal) && scaling.Contains("but for the reset Play asks", StringComparison.Ordinal) is false,
            "one log line as a run that played ends (its frames, slow ones, start and deepest step); the stale comment on Play is gone");
        var engine = Read("Services", "GpuPreviewEngine.cs");
        expect(engine.Contains("header.PreviewScale = PreviewScaleFor(playing, width, height,", StringComparison.Ordinal)
               && engine.Contains("CountFrame(late: NoteFrame(!_client.WaitForFrame(sequence,", StringComparison.Ordinal),
            "the frame pump still asks PreviewScaleFor for every scene it writes, and counts a frame as late when it misses its refresh");
        var viewport = Read("Controls", "GpuViewportControl.cs");
        expect(viewport.Contains("public double MonitorScale => GetDpiForWindow(_window) is var dpi and > 0 ? dpi / 96.0 : 1;", StringComparison.Ordinal),
            "GpuViewportControl.MonitorScale is Windows' scale of the monitor its window is on, asked at each call, 1 when the window is gone");
        var renderer = Read("Compositor", "SceneRenderer.cs");
        var release = Method(renderer, "public void ReleaseIdleTextures(bool history)");
        expect(renderer.Contains("ref var slot = ref scaled ? ref _canvasScaled : ref _canvasWhole;", StringComparison.Ordinal) && release.Contains("_canvasScaled?.Dispose();", StringComparison.Ordinal)
               && Method(renderer, "internal (int Textures, int History) IdleTexturesForTests()").Contains("_canvasScaled", StringComparison.Ordinal)
               && renderer.Contains("constants.U1 = (outputWidth - 0.5f) / _canvas.Width;", StringComparison.Ordinal),
            "the worker keeps a canvas for frames drawn smaller beside the whole one (let go by the idle trim, counted for the goldens), and takes the stretched picture's far edges half a texel in");
        var settings = Read("Dialogs", "SettingsWindow.xaml");
        expect(Regex.Matches(settings, @"IsChecked=""\{Binding DynamicPreviewScaling\}""").Count == 1 && !settings.Contains("Render Preview", StringComparison.OrdinalIgnoreCase)
               && settings.Contains("Dynamic Preview Scaling: play at fewer pixels on battery, with several tabs open, or when frames drop", StringComparison.Ordinal),
            "Settings has one Dynamic Preview Scaling checkbox, its words saying when it scales");
    }

    // ----- A real worker -----

    private const int W = 640, H = 360;

    private static void Worker(CompositorClient client, CompositorFrameReader reader, Action<bool, string> expect)
    {
        using (var gone = new GpuViewportControl(client, 0))
            expect(gone.MonitorScale == 1, "MonitorScale of no window is 1 (whole)");

        // (a) A 16:9 Sequence filling the 640 x 360 player, a gradient over it (red across, green down): whole, then at half, five times.
        var whole = Frame(client, reader, 1280, 720, 1, 1, out var problem);
        var half = Frame(client, reader, 1280, 720, 1, 0.5f, out var halfProblem);
        if (whole is null || half is null)
        {
            expect(false, $"a real worker draws a 640 x 360 frame whole and at half ({problem}{halfProblem})");
            return;
        }

        var made = Volatile.Read(ref client.Memory.Control.TexturesMade);
        var sized = true;
        for (var pair = 0; pair < 4; pair++)
            sized &= Frame(client, reader, 1280, 720, 1, 1, out _) is not null & Frame(client, reader, 1280, 720, 1, 0.5f, out _) is not null;
        var after = Volatile.Read(ref client.Memory.Control.TexturesMade);
        expect(sized, "every frame drawn at half is read back at the viewport's 640 x 360 (the output and its ring never change size)");
        expect(after == made, $"after the first frame of each size, four more pairs make no texture ({after - made} made): play and pause, a scrub begun and ended, create none");

        var (meanWhole, meanHalf) = (Mean(whole, 0, 0, W, H), Mean(half, 0, 0, W, H));
        Console.WriteLine(FormattableString.Invariant(
            $"  (the mean colour whole {meanWhole[0]:0.0}, {meanWhole[1]:0.0}, {meanWhole[2]:0.0}; at half {meanHalf[0]:0.0}, {meanHalf[1]:0.0}, {meanHalf[2]:0.0})"));
        expect(Enumerable.Range(0, 3).All(c => Math.Abs(meanWhole[c] - meanHalf[c]) <= 2),
            "at half the picture keeps its colours: the mean of every channel within 2/255 of the whole frame's");
        var (lastColumnWhole, lastColumnHalf) = (Mean(whole, W - 1, 8, W, H - 8)[0], Mean(half, W - 1, 8, W, H - 8)[0]);
        var (lastRowWhole, lastRowHalf) = (Mean(whole, 8, H - 1, W - 8, H)[1], Mean(half, 8, H - 1, W - 8, H)[1]);
        Console.WriteLine(FormattableString.Invariant(
            $"  (the last column's red {lastColumnHalf:0.0} at half, {lastColumnWhole:0.0} whole; the last row's green {lastRowHalf:0.0} at half, {lastRowWhole:0.0} whole)"));
        expect(lastColumnHalf >= lastColumnWhole - 4 && lastRowHalf >= lastRowWhole - 4,
            "no dark fringe where the picture meets the far edges: the last column's red and the last row's green at half within 4/255 of the whole frame's");

        // (b) Really drawn at half: a checker of 32-pixel cells (counted in the texture the layer is drawn into) comes out 64 pixels wide.
        var checkerWhole = Frame(client, reader, 1280, 720, 2, 1, out _);
        var checkerHalf = Frame(client, reader, 1280, 720, 2, 0.5f, out _);
        var (edgesWhole, edgesHalf) = checkerWhole is null || checkerHalf is null ? (0, 0) : (Edges(checkerWhole, H / 2), Edges(checkerHalf, H / 2));
        Console.WriteLine($"  (a checker's cell edges along a row: {edgesWhole} whole, {edgesHalf} at half)");
        expect(edgesWhole >= 18 && edgesHalf > 0 && Math.Abs(edgesWhole - 2 * edgesHalf) <= 2,
            "drawn at half for real: a checker's cells come out twice as wide (half as many edges along a row)");

        // (c) A square Sequence in the wide player: its picture's edges where SceneRenderer.Letterbox puts them, within a pixel, at half too.
        var squareWhole = Frame(client, reader, 400, 400, 1, 1, out _);
        var squareHalf = Frame(client, reader, 400, 400, 1, 0.5f, out _);
        if (squareWhole is null || squareHalf is null)
        {
            expect(false, "a square Sequence drawn whole and at half");
            return;
        }

        var (x, _, scale) = SceneRenderer.Letterbox(new FrameSceneHeader { CanvasWidth = 400, CanvasHeight = 400, ViewScale = 1 }, W, H);
        var (left, right) = ((int)Math.Floor(x), (int)Math.Ceiling(x + 400 * scale) - 1);
        var (leftWhole, rightWhole) = PictureEdges(squareWhole, H / 2, left, right);
        var (leftHalf, rightHalf) = PictureEdges(squareHalf, H / 2, left, right);
        Console.WriteLine($"  (a square Sequence's picture: columns {left} to {right} by the letterbox; {leftWhole} to {rightWhole} whole, {leftHalf} to {rightHalf} at half)");
        expect(Math.Abs(leftWhole - left) <= 1 && Math.Abs(rightWhole - right) <= 1 && Math.Abs(leftHalf - left) <= 1 && Math.Abs(rightHalf - right) <= 1,
            "a square Sequence's picture lies where SceneRenderer.Letterbox puts it, within a pixel each side, whole and at half");
    }

    // ----- dynamic-preview-scaling-blend-copy: a real worker -----

    private static void BlendCopy(Action<bool, string> expect)
    {
        using var client = new CompositorClient(adapter: -1, keepStandby: false);
        using var reader = new CompositorFrameReader(client);
        if (!BlendFrame(client, reader, 1) || !BlendFrame(client, reader, 0.5f))
        {
            expect(false, $"a real worker draws a layer with a blend mode whole and at half ({client.State}: {client.Message})");
            return;
        }

        var made = Volatile.Read(ref client.Memory.Control.TexturesMade);
        var drawn = true;
        for (var pair = 0; pair < 4; pair++)
            drawn &= BlendFrame(client, reader, 1) & BlendFrame(client, reader, 0.5f);
        var after = Volatile.Read(ref client.Memory.Control.TexturesMade);
        Console.WriteLine($"  (a blend on screen, whole and at half by turns: {after - made} textures made after the first pair)");
        expect(drawn && after == made,
            "a layer with a blend mode drawn whole and at half by turns: after the first frame of each size, four more pairs make no texture (each canvas keeps the copy the blend reads)");

        var renderer = Read("Compositor", "SceneRenderer.cs");
        expect(Method(renderer, "public void ReleaseIdleTextures(bool history)").Contains("_canvasCopyScaled?.Dispose();", StringComparison.Ordinal)
               && Regex.Matches(renderer, @"_canvasCopyWhole\?\.Dispose\(\);").Count == 2 && Regex.Matches(renderer, @"_canvasCopyScaled\?\.Dispose\(\);").Count == 2,
            "the idle trim and Dispose let go of both canvases' copies");
    }

    // A 16:9 Sequence in the 640 x 360 player: a pattern, and a smaller one laid on it with a blend mode (the copy of the canvas it reads).
    private static bool BlendFrame(CompositorClient client, CompositorFrameReader reader, float previewScale)
    {
        client.ClearScene();
        ref var scene = ref client.Scene;
        (scene.CanvasWidth, scene.CanvasHeight, scene.Flags) = (1280, 720, FrameSceneHeader.FlagLetterbox);
        (scene.OutputWidth, scene.OutputHeight, scene.ViewScale, scene.PreviewScale) = (W, H, 1, previewScale);
        for (var k = 0; k < 2; k++)
        {
            ref var layer = ref client.Layer(k);
            layer.SourceKind = (int)GpuSourceKind.TestPattern;
            layer.PatternKind = 1 + k;
            (layer.X, layer.Y, layer.Width, layer.Height) = (k * 160, k * 90, 1280 - k * 320, 720 - k * 180);
            (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
            (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
            (layer.CropRight, layer.CropBottom) = (1, 1);
            layer.Flags = GpuLayerDescriptor.FlagVisible;
            if (k == 1)
                layer.Effects[layer.EffectCount++] = new GpuEffectPacket(GpuShaderSlot.BlendMode, 2, 0, 0, 0, 0, 0, 0, 0);
        }

        scene.ActiveLayerCount = 2;
        return client.WaitForFrame(client.Publish(), 10_000) && reader.TryRead(out _, out var width, out var height) && (width, height) == (W, H);
    }

    // ----- dynamic-preview-scaling-whole-still: the rule (pure) and its wiring (the pump's thread cannot be driven without a player) -----

    private static void WholeStill(Action<bool, string> expect)
    {
        expect(!GpuPreviewEngine.StillMayBeRead(playing: false, presentedSequence: 40, scaledSequence: 40) && !GpuPreviewEngine.StillMayBeRead(false, 39, 40)
               && GpuPreviewEngine.StillMayBeRead(false, 41, 40) && GpuPreviewEngine.StillMayBeRead(false, 1, 0) && GpuPreviewEngine.StillMayBeRead(true, 40, 40),
            "StillMayBeRead: paused, not while the frame on screen is the last one drawn smaller (or older), at once when a newer one is (whole: the paused turn's); playing, as it is");
        var snapshot = Read("Services", "GpuPreviewEngine.Snapshot.cs");
        expect(InOrder(Method(snapshot, "private void TakeSnapshot()"), "Volatile.Read(ref _snapshot) is not { } asked", "if (!StillMayBeRead(_playing, _viewport.PresentedSequence, _scaledSequence))",
                   "if (!asked.SoftTried)", "Volatile.Write(ref asked.Soft, ", "return;", "Interlocked.Exchange(ref _snapshot, null)", "request.Frame = StillOnScreen(request.ShortSide);")
               && Method(snapshot, "private (byte[] Bgra, int Width, int Height)? StillOnScreen(int shortSide)").Contains("_viewport.TryReadStill(", StringComparison.Ordinal),
            "the pump reads the still asked for only when StillMayBeRead says so (the request read before the player's state), else reads the frame drawn smaller once as its fallback and leaves the request for a later turn");

        // The UI's side (5.4.4): what a request gets within its bound, the soft frame the pump read meanwhile when the whole one is late or not read.
        var (soft, whole) = ((new byte[4], 1, 1), (new byte[16], 2, 2));
        var late = new GpuPreviewEngine.SnapshotRequest(540) { Soft = new(soft) };
        GpuPreviewEngine.SnapshotRequest? slot = late;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var gotLate = GpuPreviewEngine.AwaitStill(ref slot, late, 30, out var lateSoft);
        var waited = watch.ElapsedMilliseconds;
        var inTime = new GpuPreviewEngine.SnapshotRequest(540) { Soft = new(soft) };
        GpuPreviewEngine.SnapshotRequest? inTimeSlot = inTime;
        _ = Task.Run(() =>
        {
            Thread.Sleep(5);
            inTime.Frame = whole;
            inTime.Done.Set();
        });
        var gotInTime = GpuPreviewEngine.AwaitStill(ref inTimeSlot, inTime, 5000, out var inTimeSoft);
        var unread = new GpuPreviewEngine.SnapshotRequest(540) { Soft = new(soft) };
        unread.Done.Set();
        GpuPreviewEngine.SnapshotRequest? unreadSlot = null;
        var gotUnread = GpuPreviewEngine.AwaitStill(ref unreadSlot, unread, 30, out var unreadSoft);
        var none = new GpuPreviewEngine.SnapshotRequest(540);
        GpuPreviewEngine.SnapshotRequest? noneSlot = none;
        var gotNone = GpuPreviewEngine.AwaitStill(ref noneSlot, none, 5, out var noneSoft);
        expect(gotLate is { } l && ReferenceEquals(l.Bgra, soft.Item1) && slot is null && waited < 1000
               && gotInTime is { } t && ReferenceEquals(t.Bgra, whole.Item1)
               && gotUnread is { } u && ReferenceEquals(u.Bgra, soft.Item1)
               && gotNone is null && noneSlot is null,
            "AwaitStill: the whole frame when it is read in time; the soft frame read meanwhile when it is late (the request withdrawn, the UI held no longer than its bound) or not read; null only when neither is");
        expect(lateSoft && !inTimeSoft && unreadSoft && !noneSoft,
            "and it says which: soft for the frame drawn smaller (late or unread), not for the whole frame nor for nothing");
        var engine = Read("Services", "GpuPreviewEngine.cs");
        var publish = Method(engine, "private long PublishAt(");
        expect(InOrder(publish, "header.PreviewScale = PreviewScaleFor(", "var sequence = _client.Publish();", "if (sequence > 0 && _lastScale < 1)", "_scaledSequence = sequence;"),
            "every scene published drawn smaller is noted as such");
        var pump = Method(engine, "private void Pump()");
        expect(InOrder(pump, "TakeSnapshot();", "if (_playing)", "if (CrispOwed())", "PublishAt(pausedPlan, pausedAt, playing: false);", "_viewport.PresentNewest(vsync: false);")
               && Read("Services", "GpuPreviewEngine.Scaling.cs").Contains("private bool CrispOwed() => !_playing && _lastScale < 1", StringComparison.Ordinal),
            "the paused turn draws a picture drawn smaller whole again and shows it, so the still waiting for it is taken at the next turn's top");
        var still = File.ReadAllText(Path.Combine(Root(), "src", "MainWindow.TabStill.cs"));
        expect(still.Contains("private const int StillOnScreenWaitMs = 40;", StringComparison.Ordinal) && still.Contains("_gpu?.SnapshotPresented(StillShortSide, StillOnScreenWaitMs, out var soft)", StringComparison.Ordinal)
               && snapshot.Contains("return AwaitStill(ref _snapshot, request, timeoutMs, out soft);", StringComparison.Ordinal),
            "the UI thread still waits at most 40 ms for it (the soft frame kept when the whole frame is not read by then)");
        var cache = File.ReadAllText(Path.Combine(Root(), "src", "MainWindow.TabCache.cs"));
        expect(cache.Contains("private readonly record struct StillMark(object? UndoMark, double PositionMs, bool Shown3D, bool Soft = false);", StringComparison.Ordinal)
               && InOrder(Method(still, "private void TakeStillOnScreen()"), "if (_liveStill is not null && (!_liveStillMark.Soft || _tabStill is { Visibility: System.Windows.Visibility.Visible }) && ", "return;", "mark with { Soft = soft }")
               && InOrder(Method(still, "private async Task TakeLiveStillAsync()"), "var mark = new StillMark(", "if (_liveStill is not null && _liveStillMark == mark)"),
            "a soft still is marked so: it covers the player, but the next switch away reads the screen again (not while that still is over the player: the screen is not the tab's yet) and the next quiet moment reads a whole still in its place (its mark never equals now's)");
    }

    /// <summary>Whether every one of <paramref name="parts"/> is in <paramref name="text"/>, each after the one before.</summary>
    private static bool InOrder(string text, params string[] parts)
    {
        var at = 0;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, at, StringComparison.Ordinal);
            if (found < 0)
                return false;
            at = found + part.Length;
        }

        return true;
    }

    // A scene of one test pattern filling the canvas, letterboxed in the 640 x 360 player at a preview scale; null when it is not read back.
    private static byte[]? Frame(CompositorClient client, CompositorFrameReader reader, int canvasWidth, int canvasHeight, int kind, float previewScale, out string problem)
    {
        problem = "";
        client.ClearScene();
        ref var scene = ref client.Scene;
        (scene.CanvasWidth, scene.CanvasHeight, scene.Flags) = (canvasWidth, canvasHeight, FrameSceneHeader.FlagLetterbox);
        (scene.OutputWidth, scene.OutputHeight, scene.ViewScale, scene.PreviewScale) = (W, H, 1, previewScale);
        ref var layer = ref client.Layer(0);
        layer.SourceKind = (int)GpuSourceKind.TestPattern;
        layer.PatternKind = kind;
        (layer.X, layer.Y, layer.Width, layer.Height) = (0, 0, canvasWidth, canvasHeight);
        (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
        (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
        (layer.CropRight, layer.CropBottom) = (1, 1);
        layer.Flags = GpuLayerDescriptor.FlagVisible;
        scene.ActiveLayerCount = 1;

        var sequence = client.Publish();
        if (!client.WaitForFrame(sequence, 10_000))
        {
            problem = $"no frame within 10 s ({client.State}: {client.Message})";
            return null;
        }

        if (!reader.TryRead(out var bgra, out var width, out var height) || (width, height) != (W, H))
        {
            problem = $"read back at {width} x {height}, not {W} x {H}";
            return null;
        }

        return bgra;
    }

    // The mean red, green and blue over a rectangle of a frame (left, top inclusive; right, bottom exclusive).
    private static double[] Mean(byte[] frame, int left, int top, int right, int bottom)
    {
        var sums = new double[3];
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var at = (y * W + x) * 4;
                (sums[0], sums[1], sums[2]) = (sums[0] + frame[at + 2], sums[1] + frame[at + 1], sums[2] + frame[at]);
                count++;
            }
        }

        return [.. sums.Select(s => s / Math.Max(count, 1))];
    }

    private static int Light(byte[] frame, int x, int y) => frame[(y * W + x) * 4] + frame[(y * W + x) * 4 + 1] + frame[(y * W + x) * 4 + 2];

    // How many times a row goes from a checker's dark cell to its light one or back (halfway between the two, so a filtered edge counts once).
    private static int Edges(byte[] frame, int y)
    {
        var (edges, light) = (0, Light(frame, 0, y) > 3 * 128);
        for (var x = 1; x < W; x++)
        {
            var now = Light(frame, x, y) > 3 * 128;
            if (now != light)
                edges++;
            light = now;
        }

        return edges;
    }

    // The first and last column of a row lit to at least half the picture's light just inside each edge (black bars either side).
    private static (int Left, int Right) PictureEdges(byte[] frame, int y, int left, int right)
    {
        var (insideLeft, insideRight) = (Light(frame, left + 6, y), Light(frame, right - 6, y));
        var first = Enumerable.Range(left - 12, 30).FirstOrDefault(x => Light(frame, x, y) * 2 > insideLeft, -1);
        var last = Enumerable.Range(right - 17, 30).LastOrDefault(x => Light(frame, x, y) * 2 > insideRight, -1);
        return (first, last);
    }

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
}
