using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;
using HandPegApp.Views;
using static HandPegGoldenTests.CompositorReference;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6A lane A: the Color Studio. color-grade-packets: the Color Grade card's sixteen lanes in their contract's order, a neutral card the
/// picture exactly, its two packets always side by side in a plan, its last lane's tables flags. color-curves-hermite: the monotone Hermite
/// the curve table is baked from (identity, ends, no overshoot, each channel with the master after it, a bake well under 0.1 ms).
/// color-grade-convert: the old Filters grade turned into Sequence Output cards that draw the same picture within 1/255, one step of Undo; the
/// fold's Reset takes the sliders and the LUT (a kept one too) in one step. grade-compare-plan: the Before/After's bypass leaves out the grade's packets only, the split writes each half with a hard wipe, and nothing
/// but the player's frames and the input still evaluate with it. keybind-grade-compare: Hold \ for the Before/After, Fit Timeline on Shift+\,
/// no preset conflict. dock-color-tab-migration (Live): "Tab:Filters" becomes "Tab:Color" in the main window's, a side window's and a Pane
/// Docker's trees, hidden tabs (version 13), the Default layout still alike. color-studio-lazy (Live): nothing built before shown, no capture
/// while hidden or playing, a wheel drag one step of Undo, the card found again after Undo.
/// </summary>
public static class ColorStudioTests
{
    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("color-grade-packets", "# The Color Grade card (slot 30): its lanes in the contract's order, neutral is the picture exactly, always two packets side by side.", Packets);
        yield return Case("color-curves-hermite", "# The Color Grade's curves: a monotone cubic Hermite (Fritsch-Carlson), identity, ends, no overshoot, each channel with the master after it.", Curves);
        yield return Case("color-grade-convert", "# The old Filters grade as Sequence Output cards: the same picture within 1/255, the legacy fields zeroed, one step of Undo.", Convert);
        yield return Case("grade-compare-plan", "# The grade's Before/After in the plan: the bypass drops the grade's packets only, the split writes two halves, export and stills unaffected.", ComparePlan);
        yield return Case("keybind-grade-compare", "# Hold \\ for the Before/After, Fit Timeline on Shift+\\, no preset with a chord twice.", Keys);
        yield return Case("color-studio-fixes", "# Pass 5.6A's review fixes that need no window: the 3D view keeps graded planes, the export names a lost table, the loupe skips the Before half.", Fixes);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("dock-color-tab-migration", "# The Filters tab is the Color tab: saved arrangements (main, side window, Pane Docker) and hidden tabs follow, the Default layout still alike.", DockMigration);
        yield return Case("color-studio-lazy", "# The Color tab: nothing built before shown, no capture while hidden or playing, a wheel drag one step of Undo, the card found again after Undo.", Lazy);
        yield return Case("color-studio-boxes", "# The Color Studio's value and LUT boxes: only typed text is written, a lone write is shown at once, nothing to grade on a sound clip.", Boxes);
    }

    // ----- Helpers -----

    private static EffectDefinition Studio => EffectRegistry.Find(EffectRegistry.ColorStudio)!;

    /// <summary>The scene of a view model's state at a moment (a plan made fresh: the state's cache let go first).</summary>
    private static (GpuLayerDescriptor[] Layers, int Count) Scene(MainViewModel vm, GpuResourceBroker broker, GradeCompare compare = default)
    {
        typeof(MainViewModel).GetField("_previewState", Hidden)!.SetValue(vm, null);
        var plan = GpuScenePlan.Create(vm.LivePreviewState(), broker);
        var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var header = default(FrameSceneHeader);
        var count = plan.Evaluate(0.5, ref header, layers, compare);
        return (layers, count);
    }

    private static List<GpuEffectPacket> PacketsOf(in GpuLayerDescriptor d)
    {
        var list = new List<GpuEffectPacket>();
        for (var k = 0; k < d.EffectCount; k++)
            list.Add(d.Effects[k]);
        return list;
    }

    private static string Slots(in GpuLayerDescriptor d) => string.Join(",", PacketsOf(d).Select(p => p.Slot));

    private static float[] LanesOf(GpuEffectPacket p) => [p.P0, p.P1, p.P2, p.P3, p.P4, p.P5, p.P6, p.P7];

    private static List<(string What, byte[] State)> Undo(MainViewModel vm) =>
        (List<(string What, byte[] State)>)typeof(MainViewModel).GetField("_undo", Hidden)!.GetValue(vm)!;

    private static List<(string What, byte[] State)> Redo(MainViewModel vm) =>
        (List<(string What, byte[] State)>)typeof(MainViewModel).GetField("_redo", Hidden)!.GetValue(vm)!;

    /// <summary>The view model's state now (its cache let go first), as the export and the plan read it.</summary>
    private static SequenceExportState StateOf(MainViewModel vm)
    {
        typeof(MainViewModel).GetField("_previewState", Hidden)!.SetValue(vm, null);
        return vm.LivePreviewState();
    }

    /// <summary>A scene's whole-frame adjustments (the legacy "Picture" entry, then the Sequence Output's), in their order.</summary>
    private static List<GpuLayerDescriptor> Adjustments((GpuLayerDescriptor[] Layers, int Count) scene) =>
        Enumerable.Range(0, scene.Count).Select(i => scene.Layers[i]).Where(d => (d.Flags & GpuLayerDescriptor.FlagAdjustment) != 0).ToList();

    private static string DescribeEntries(List<GpuLayerDescriptor> entries) => string.Join(" | ", entries.Select(d => Slots(d)));

    /// <summary>Each entry's colour packets run in turn, each with its own LUT table (a descriptor binds one at most).</summary>
    private static RefImage RunAll(RefImage image, List<GpuLayerDescriptor> entries, params (float[] Table, int Size)[] luts)
    {
        for (var i = 0; i < entries.Count; i++)
            image = Run(image, entries[i], luts[i].Table, luts[i].Size);
        return image;
    }

    /// <summary>A few colours (opaque, premultiplied as the compositor keeps them) in a row: greys, primaries, skin, the ends.</summary>
    private static RefImage Swatches()
    {
        Vector3[] colours = [new(0.1f, 0.2f, 0.3f), new(0.5f, 0.5f, 0.5f), new(0.9f, 0.4f, 0.2f), new(0.2f, 0.8f, 0.6f), new(1, 1, 1), new(0, 0, 0), new(0.7f, 0.1f, 0.9f), new(0.85f, 0.62f, 0.5f)];
        var image = new RefImage(colours.Length, 1);
        for (var i = 0; i < colours.Length; i++)
            image.Set(i, 0, new Vector4(colours[i], 1));
        return image.ToHalf();
    }

    /// <summary>A small .cube file of the test's own (5 on a side: a warm, lifted look; <paramref name="cool"/>: a cool one), and its table as the compositor gets it.</summary>
    private static (string Path, float[] Table, int Size) Cube(string name = "handpeg-color-studio-test.cube", bool cool = false)
    {
        const int n = 5;
        var path = Path.Combine(Path.GetTempPath(), name);
        var text = new StringBuilder("TITLE \"test\"\nLUT_3D_SIZE 5\n");
        for (var b = 0; b < n; b++)
            for (var g = 0; g < n; g++)
                for (var r = 0; r < n; r++)
                {
                    var (fr, fg, fb) = (r / (n - 1.0), g / (n - 1.0), b / (n - 1.0));
                    text.Append(cool
                        ? string.Create(CultureInfo.InvariantCulture, $"{fr * fr * 0.8 + 0.03:0.######} {0.02 + fg * 0.93:0.######} {0.08 + fb * 0.9 + 0.05 * fg:0.######}\n")
                        : string.Create(CultureInfo.InvariantCulture, $"{0.05 + fr * 0.92:0.######} {0.03 + fg * 0.9 + 0.04 * fr:0.######} {fb * fb * 0.85 + 0.02:0.######}\n"));
                }

        File.WriteAllText(path, text.ToString());
        var cube = GpuColorTables.ReadCube(path) ?? throw new InvalidOperationException("the test's .cube could not be read");
        var table = new float[cube.Pixels.Length / 4];
        Buffer.BlockCopy(cube.Pixels, 0, table, 0, cube.Pixels.Length);
        return (path, table, cube.Size);
    }

    /// <summary>A descriptor's colour packets run on the CPU reference in order (half floats between, as the GPU keeps them); Sharpen is left to its lanes.</summary>
    private static RefImage Run(RefImage image, in GpuLayerDescriptor d, float[] lut, int size)
    {
        var packets = PacketsOf(d);
        for (var k = 0; k < packets.Count; k++)
        {
            var packet = packets[k];
            switch (packet.Slot)
            {
                case GpuShaderSlot.Lut3D:
                    image = Lut3D(image, [], new RefPass { Params2 = new Vector4(size, 0, 0, 0), Lut = lut });
                    break;
                case GpuShaderSlot.ColorGrade:
                    image = ColorGrade(image, LanesOf(packet), new RefPass());
                    break;
                case GpuShaderSlot.HueSaturation:
                    image = HueSaturation(image, LanesOf(packet), new RefPass());
                    break;
                case GpuShaderSlot.ColorStudio when k + 1 < packets.Count && packets[k + 1].Slot == GpuShaderSlot.ColorStudio:
                    var second = packets[++k];
                    var (hasLut, _, mix) = GpuColorTables.StudioTablesOf(second.P7);
                    image = ColorStudio(image, [.. LanesOf(packet), .. LanesOf(second)], new RefPass { Params2 = new Vector4(hasLut ? size : 0, mix, 0, 0), Lut = lut });
                    break;
                default:
                    continue;
            }

            image = image.ToHalf();
        }

        return image;
    }

    // ----- color-grade-packets -----

    private static void Packets(Action<bool, string> expect)
    {
        var definition = Studio;
        expect(definition is { Name: "Color Grade", Category: "Color", Zone: EffectZone.Look, Once: true, GpuSlot: GpuShaderSlot.ColorStudio, GpuPacketCount: 2, GpuLaneCount: 16, Cost: EffectCost.Amber, GpuOnly: false },
            "builtin.color.studio is the Color Grade: Look, once a clip, slot 30, two packets of sixteen lanes, Amber, drawn by FFmpeg's export too (not GPU-only)");
        expect(EffectRegistry.Find(EffectRegistry.Grade) is { Name: "Brightness & Contrast", GpuSlot: GpuShaderSlot.ColorGrade, GpuPacketCount: 1 },
            "the old Grade (builtin.color.eq, unchanged) is Brightness & Contrast, one packet");
        var studioOnly = definition.Params.Where(p => p.StudioOnly).Select(p => p.Key).ToList();
        expect(studioOnly.SequenceEqual([ColorGradeKeys.LiftR, ColorGradeKeys.LiftG, ColorGradeKeys.LiftB, ColorGradeKeys.GammaR, ColorGradeKeys.GammaG, ColorGradeKeys.GammaB,
                ColorGradeKeys.GainR, ColorGradeKeys.GainG, ColorGradeKeys.GainB, .. ColorGradeKeys.Curves]),
            "the wheels and the curves are edited in the Color Studio only (StudioOnly); the basics and the LUT show on the Effects pane's card");

        // Each lane its own value, in the contract's order.
        (string Key, double Value, float Lane)[] lanes =
        [
            (ColorGradeKeys.Temperature, 12, 12), (ColorGradeKeys.Tint, -21, -21), (ColorGradeKeys.Exposure, 0.5, 0.5f), (ColorGradeKeys.LiftR, 0.01, 0.01f),
            (ColorGradeKeys.LiftG, 0.02, 0.02f), (ColorGradeKeys.LiftB, 0.03, 0.03f), (ColorGradeKeys.GammaR, 0.04, 0.04f), (ColorGradeKeys.Contrast, 1.1, 1.1f),
            (ColorGradeKeys.GammaG, 0.05, 0.05f), (ColorGradeKeys.GammaB, 0.06, 0.06f), (ColorGradeKeys.GainR, 0.07, 0.07f), (ColorGradeKeys.GainG, 0.08, 0.08f),
            (ColorGradeKeys.GainB, 0.09, 0.09f), (ColorGradeKeys.Saturation, 1.2, 1.2f), (ColorGradeKeys.Vibrance, -0.3, -0.3f), (ColorGradeKeys.LutMix, 40, 0.4f),
        ];
        var card = definition.NewEffect();
        foreach (var (key, value, _) in lanes)
            card.Set(key, value);
        var (first, second) = (card.EvaluateGpuPacket(0, null, 0), card.EvaluateGpuPacket(0, null, 1));
        var got = LanesOf(first).Concat(LanesOf(second)).ToArray();
        var wrong = lanes.Select((l, i) => (l.Key, Expected: l.Lane, Got: got[i])).Where(x => Math.Abs(x.Expected - x.Got) > 1e-6).ToList();
        expect(first.Slot == GpuShaderSlot.ColorStudio && second.Slot == GpuShaderSlot.ColorStudio && wrong.Count == 0,
            "P0..P7 temperature, tint, exposure, lift r g b, gamma r, contrast; Q0..Q7 gamma g b, gain r g b, saturation, vibrance, LUT Mix (% x 0.01)"
            + (wrong.Count > 0 ? ": " + string.Join(", ", wrong.Select(w => $"{w.Key} {w.Got} not {w.Expected}")) : ""));

        // A new card changes nothing: its lanes are the neutral ones, the renderer draws no pass, the reference is the picture itself.
        var neutral = definition.NewEffect();
        var (n0, n1) = (neutral.EvaluateGpuPacket(0, null, 0), neutral.EvaluateGpuPacket(0, null, 1));
        expect(ColorGradeValues.From(neutral) == ColorGradeValues.Neutral && ColorGradeValues.Neutral.IsNeutral && !ColorGradeMath.CurvesActive(neutral)
               && HandPegApp.Compositor.SceneRenderer.StudioNeutral(n0, n1) && n1.P7 == 1,
            "a new card is neutral (contrast and saturation 1, LUT Mix 100 %, no curve): the renderer draws no pass for it");
        var swatches = Swatches();
        var neutralLanes = LanesOf(n0).Concat(LanesOf(n1)).ToArray();
        expect(ReferenceEquals(ColorStudio(swatches, neutralLanes, new RefPass()), swatches)
               && Enumerable.Range(0, 8).All(i => ColorGradeMath.Apply(i / 7.0, 1 - i / 7.0, 0.5, ColorGradeValues.Neutral) == (i / 7.0, 1 - i / 7.0, 0.5)),
            "neutral is the picture exactly: ColorGradeMath.Apply gives each colour back as it came, and so does the CPU mirror of PSColorStudio");

        // The shader's float steps against the double reference (Apply is the spec), on the swatches.
        var values = ColorGradeValues.From(card);
        var mirrored = ColorStudio(swatches, got[..15].Append(0f).ToArray(), new RefPass());
        var worst = 0.0;
        for (var i = 0; i < swatches.Width; i++)
        {
            var input = swatches.Get(i, 0);
            var (r, g, b) = ColorGradeMath.Apply(input.X, input.Y, input.Z, values);
            var drawn = mirrored.Get(i, 0);
            worst = Math.Max(worst, Math.Max(Math.Abs(drawn.X - r), Math.Max(Math.Abs(drawn.Y - g), Math.Abs(drawn.Z - b))));
        }

        expect(worst * 255 < 0.5, "a full grade: PSColorStudio's float steps (the CPU mirror) within half a level of ColorGradeMath.Apply on eight colours");

        // In a plan: the card's two packets side by side, a second card's after them; its last lane the mix (no table bound).
        var vm = new MainViewModel();
        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        vm.SequenceEffects.Add(card);
        var (layers, count) = Scene(vm, broker);
        var output = Enumerable.Range(0, count).Select(i => layers[i]).FirstOrDefault(d => PacketsOf(d).Any(p => p.Slot == GpuShaderSlot.ColorStudio));
        var packets = PacketsOf(output);
        expect(packets.Count == 2 && packets.All(p => p.Slot == GpuShaderSlot.ColorStudio) && Math.Abs(packets[1].P7 - 0.4f) < 1e-6 && packets[0].P0 == 12,
            "on the Sequence Output: one descriptor with the card's two packets in order, the last lane its mix alone (no LUT, no curve of its own)");
        var other = definition.NewEffect();
        other.Set(ColorGradeKeys.Exposure, -0.5);
        vm.SequenceEffects.Add(other);
        (layers, count) = Scene(vm, broker);
        output = Enumerable.Range(0, count).Select(i => layers[i]).FirstOrDefault(d => PacketsOf(d).Any(p => p.Slot == GpuShaderSlot.ColorStudio));
        packets = PacketsOf(output);
        expect(packets.Count == 4 && packets.All(p => p.Slot == GpuShaderSlot.ColorStudio) && packets[0].P0 == 12 && packets[2].P2 == -0.5f,
            "two cards side by side: four packets, each card's pair together (the renderer pairs 0 with 1 and 2 with 3)");

        // The last lane: the mix and which tables are the card's, read back as written; never below 0 (no PulseMarker).
        var lanesOk = true;
        foreach (var mix in new[] { 0.0, 0.37, 1.0, 1.7, -0.2 })
            foreach (var curves in new[] { false, true })
                foreach (var lut in new[] { false, true })
                {
                    var lane = GpuColorTables.StudioTablesLane(mix, curves, lut);
                    var back = GpuColorTables.StudioTablesOf(lane);
                    lanesOk &= lane >= 0 && back.Lut == lut && back.Curves == curves && Math.Abs(back.Mix - Math.Clamp(mix, 0, 1)) < 1e-6;
                }

        expect(lanesOk, "the last lane: mix (held to 0..1) + 2 for its curves + 4 for its LUT, read back exactly, never negative");
    }

    // ----- color-curves-hermite -----

    private static void Curves(Action<bool, string> expect)
    {
        expect(GpuColorTables.ColorCurves("", "", "", "") is null && GpuColorTables.ColorCurves("0/0 1/1", "0.5/0.5", null, " ") is null,
            "every curve empty or on the diagonal: no table (the identity)");
        var lone = GpuColorTables.CurvePoints("0.5/0.6");
        expect(lone.SequenceEqual([(0.0, 0.0), (0.5, 0.6), (1.0, 1.0)]), "a lone point bends the diagonal through it: its ends (0, 0) and (1, 1) put in");

        const string rising = "0/0.05 0.3/0.6 0.7/0.62 1/0.95";
        var samples = GpuColorTables.CurveSamples(rising, 1024);
        var monotone = samples.Zip(samples.Skip(1)).All(p => p.Second >= p.First - 1e-12);
        var points = GpuColorTables.CurvePoints(rising);
        var through = points.All(p => Math.Abs(GpuColorTables.CurveAt(rising, p.X) - p.Y) < 1e-9);
        var inBounds = true;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (lo, hi) = (Math.Min(points[i].Y, points[i + 1].Y), Math.Max(points[i].Y, points[i + 1].Y));
            for (var t = 0; t <= 100; t++)
            {
                var y = GpuColorTables.CurveAt(rising, points[i].X + (points[i + 1].X - points[i].X) * t / 100);
                inBounds &= y >= lo - 1e-9 && y <= hi + 1e-9;
            }
        }

        expect(monotone && through && inBounds, "points that rise give a curve that only rises, through each point, never past its neighbours (no overshoot)");
        expect(GpuColorTables.CurveAt(rising, 0) == 0.05 && GpuColorTables.CurveAt(rising, 1) == 0.95, "the ends: the first point's value at 0, the last's at 1");

        const string falling = "0/1 0.5/0.4 1/0";
        var down = GpuColorTables.CurveSamples(falling, 512);
        expect(down.Zip(down.Skip(1)).All(p => p.Second <= p.First + 1e-12), "a falling curve (an inverted channel) only falls");
        expect(GpuColorTables.CurveSamples("0.5/0.5", 64).Select((v, i) => Math.Abs(v - i / 63.0)).Max() < 1e-12, "points on the diagonal: the curve is the diagonal");

        // The table: each channel's curve with the master after it; the master alone in alpha.
        const string master = "0/0 0.5/0.62 1/1", red = "0/0.1 1/0.9", blue = "0.25/0.1 1/1";
        var table = GpuColorTables.ColorCurves(master, red, "", blue)!;
        var floats = new float[256 * 4];
        Buffer.BlockCopy(table, 0, floats, 0, table.Length);
        var worst = 0.0;
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255.0;
            worst = Math.Max(worst, Math.Abs(floats[i * 4] - GpuColorTables.CurveAt(master, GpuColorTables.CurveAt(red, x))));
            worst = Math.Max(worst, Math.Abs(floats[i * 4 + 1] - GpuColorTables.CurveAt(master, x)));
            worst = Math.Max(worst, Math.Abs(floats[i * 4 + 2] - GpuColorTables.CurveAt(master, GpuColorTables.CurveAt(blue, x))));
            worst = Math.Max(worst, Math.Abs(floats[i * 4 + 3] - GpuColorTables.CurveAt(master, x)));
        }

        expect(table.Length == 256 * 16 && worst < 1e-6, "a 256 x 1 RGBA32F table: R, G and B each a channel's curve with the master after it, A the master alone");
        expect(GpuColorTables.CurveSamples(red, 256).Zip(Enumerable.Range(0, 256)).All(p => Math.Abs(p.First - GpuColorTables.CurveAt(red, p.Second / 255.0)) < 1e-12),
            "the Color Studio draws the curve the table is baked from (CurveSamples is the same Hermite)");

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
            _ = GpuColorTables.ColorCurves(master, red, "0/0 0.3/0.35 0.6/0.7 1/1", blue);
        expect(clock.Elapsed.TotalMilliseconds / 200 < 0.1, "a bake of four curves takes well under 0.1 ms (the plan bakes it on the UI thread)");
        expect(ColorCurvesControl.CurveSurface.TextFor([(0, 0), (1, 1)]) == "" && ColorCurvesControl.CurveSurface.TextFor([(0, 0.1), (0.5, 0.5), (1, 1)]) == "0/0.1 0.5/0.5 1/1",
            "the curves' text: empty for the two ends on the diagonal, else each point as x/y");
    }

    // ----- color-grade-convert -----

    private static void Convert(Action<bool, string> expect)
    {
        var (path, lut, size) = Cube();
        var (other, otherLut, otherSize) = Cube("handpeg-color-studio-test-2.cube", cool: true);
        try
        {
            var vm = new MainViewModel();
            using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
            (vm.ColorContrast, vm.ColorBrightness, vm.ColorSaturation, vm.ColorGamma) = (1.25, 0.05, 1.3, 1.1);
            (vm.ColorHue, vm.ColorRed, vm.ColorGreen, vm.ColorBlue, vm.SharpenStrength) = (14, 0.3, -0.1, 0.2, 0.4);
            vm.LutPath = path;
            expect(vm.HasLegacyGrade, "sliders moved and a LUT: the project has a legacy grade (the Color tab shows its fold)");

            var (before, beforeCount) = Scene(vm, broker);
            var legacy = Enumerable.Range(0, beforeCount).Select(i => before[i]).Where(d => (d.Flags & GpuLayerDescriptor.FlagAdjustment) != 0).ToList();
            expect(legacy.Count == 1 && Slots(legacy[0]) == "Lut3D,ColorGrade,HueSaturation,Sharpen", $"before: the whole picture's entry draws the LUT, eq, the hue turn and the sharpening ({(legacy.Count > 0 ? Slots(legacy[0]) : "none")})");

            var steps = Undo(vm).Count;
            var converted = vm.ConvertLegacyGrade();
            expect(converted && Undo(vm).Count == steps + 1 && vm.GetSequenceGrade().IsNeutral && vm.LutPath == "" && !vm.HasLegacyGrade,
                "Convert: one step of Undo, the sliders neutral and the LUT path empty");
            expect(vm.SequenceEffects.Select(c => c.Type).SequenceEqual([EffectRegistry.ColorStudio, EffectRegistry.Grade, EffectRegistry.Hue, EffectRegistry.Sharpen]),
                "the Sequence Output's first cards: Color Grade (the LUT), Brightness & Contrast, Hue, Sharpen, in the order they were drawn");

            var (after, afterCount) = Scene(vm, broker);
            var cards = Enumerable.Range(0, afterCount).Select(i => after[i]).Where(d => (d.Flags & GpuLayerDescriptor.FlagAdjustment) != 0).ToList();
            expect(cards.Count == 1 && Slots(cards[0]) == "ColorStudio,ColorStudio,ColorGrade,HueSaturation,Sharpen",
                $"after: the Sequence Output's entry alone ({(cards.Count > 0 ? Slots(cards[0]) : "none")}); the whole picture's has nothing left to draw");

            if (legacy.Count == 1 && cards.Count == 1)
            {
                var swatches = Swatches();
                var (old, now) = (Run(swatches, legacy[0], lut, size), Run(swatches, cards[0], lut, size));
                expect(old.MaxDifference(now) * 255 <= 1, "the same picture within 1/255 on eight colours (the LUT at 100 %, eq with its channel balance, the hue turn)");
                var (oldSharp, newSharp) = (PacketsOf(legacy[0]).Last(), PacketsOf(cards[0]).Last());
                expect(oldSharp.Slot == GpuShaderSlot.Sharpen && LanesOf(oldSharp).Zip(LanesOf(newSharp)).All(p => Math.Abs(p.First - p.Second) < 1e-6),
                    "the sharpening's lanes as they were (strength, unsharp's radius at the frame's width)");
            }

            vm.Undo();
            expect(!vm.GetSequenceGrade().IsNeutral && vm.SequenceEffects.Count == 0 && vm.LutPath == path,
                "Undo: the sliders back, the cards gone and the LUT path back (the step holds it, the Color tab shown or not)");
            vm.Redo();
            expect(vm.GetSequenceGrade().IsNeutral && vm.SequenceEffects.Count == 4 && vm.LutPath == "", "Redo: the cards back and the LUT path empty again");

            // The card's LUT cleared after a Convert, then an unrelated step undone: nothing puts the legacy LUT back (5.6A review F7).
            var card = vm.FindColorGrade(ColorGradeTarget.ForSequence);
            vm.Checkpoint("clear the LUT");
            card?.SetText(ColorGradeKeys.Lut, "");
            vm.Checkpoint("add a blur");
            vm.SequenceEffects.Add(EffectRegistry.Find(EffectRegistry.Blur)!.NewEffect());
            vm.Undo();
            expect(card is not null && vm.LutPath == "" && vm.FindColorGrade(ColorGradeTarget.ForSequence)?.GetText(ColorGradeKeys.Lut) == "" && !vm.HasLegacyGrade,
                "the card's LUT cleared, then an unrelated step undone: the legacy LUT stays gone (never put back outside Undo)");

            // The look an undo step holds carries the LUT path; a step from before it (none) leaves the path as it is.
            var capture = typeof(MainViewModel).GetMethod("CaptureLook", Hidden)!;
            var apply = typeof(MainViewModel).GetMethod("ApplyLook", Hidden)!;
            vm.LutPath = other;
            var look = (SequenceLook)capture.Invoke(vm, null)!;
            apply.Invoke(vm, [look with { LutPath = null }]);
            var keptByOld = vm.LutPath == other;
            apply.Invoke(vm, [look with { LutPath = "" }]);
            expect(look.LutPath == other && keptByOld && vm.LutPath == "" && JsonSerializer.Deserialize<SequenceLook>("{\"Contrast\":1}")?.LutPath is null,
                "the sequence's look holds the LUT path; an older step's look (no path written: null) leaves it as it is");
            expect(MainViewModel.LegacyGradeCards(SequenceGrade.Neutral, "", 1920).Count == 0, "nothing to convert: no card");

            ConvertIntoCards(expect, broker, (path, lut, size), (other, otherLut, otherSize));
        }
        finally
        {
            File.Delete(path);
            File.Delete(other);
        }
    }

    /// <summary>
    /// Convert into a Sequence Output that has cards of its own (5.6A review F2/F11): never a second Color Grade card nor a second LUT, the
    /// same picture within 1/255 each way: a Color Grade card there takes the LUT as a LUT 3D card; a card holding a LUT keeps the legacy one as it is.
    /// </summary>
    private static void ConvertIntoCards(Action<bool, string> expect, GpuResourceBroker broker, (string Path, float[] Table, int Size) legacy, (string Path, float[] Table, int Size) own)
    {
        var swatches = Swatches();
        MainViewModel Legacy()
        {
            var vm = new MainViewModel();
            (vm.ColorContrast, vm.ColorSaturation, vm.ColorGamma, vm.ColorHue, vm.ColorRed) = (1.2, 1.15, 0.9, -12, 0.2);
            vm.LutPath = legacy.Path;
            return vm;
        }

        bool Same(RefImage a, RefImage b) => a.MaxDifference(b) * 255 <= 1;
        string[] Types(MainViewModel vm) => [.. vm.SequenceEffects.Select(c => c.Type)];
        int Luts(MainViewModel vm) => vm.SequenceEffects.Count(c => c.Type == EffectRegistry.Lut || c.GetText(ColorGradeKeys.Lut).Length > 0);

        // A Color Grade card of the user's (no LUT): the LUT goes in first as a LUT 3D card; the Studio still grades the user's card.
        var graded = Legacy();
        var mine = Studio.NewEffect();
        mine.Set(ColorGradeKeys.Exposure, 0.3);
        mine.Set(ColorGradeKeys.Temperature, 18);
        graded.SequenceEffects.Add(mine);
        var before = Adjustments(Scene(graded, broker));
        var converted = graded.ConvertLegacyGrade();
        var after = Adjustments(Scene(graded, broker));
        expect(converted && Types(graded).SequenceEqual([EffectRegistry.Lut, EffectRegistry.Grade, EffectRegistry.Hue, EffectRegistry.ColorStudio])
               && ReferenceEquals(graded.FindColorGrade(ColorGradeTarget.ForSequence), mine) && graded.LutPath == "" && Luts(graded) == 1,
            "a Color Grade card already there: the LUT as a LUT 3D card first, no second Color Grade, the Color Studio still on the user's card");
        expect(DescribeEntries(before) == "Lut3D,ColorGrade,HueSaturation | ColorStudio,ColorStudio" && DescribeEntries(after) == "Lut3D,ColorGrade,HueSaturation,ColorStudio,ColorStudio"
               && Same(RunAll(swatches, before, (legacy.Table, legacy.Size), (own.Table, own.Size)), RunAll(swatches, after, (legacy.Table, legacy.Size))),
            $"...the same picture within 1/255 (before {DescribeEntries(before)}; after {DescribeEntries(after)})");
        expect(GpuExport.LeftOut(StateOf(graded)).Count == 0, "...and nothing left out of an export");

        // A LUT 3D card of the user's (another LUT): the sliders alone move, the legacy LUT kept as it is (drawn first, by its own entry).
        var tabled = Legacy();
        var table = EffectRegistry.Find(EffectRegistry.Lut)!.NewEffect();
        table.SetText("file", own.Path);
        tabled.SequenceEffects.Add(table);
        before = Adjustments(Scene(tabled, broker));
        converted = tabled.ConvertLegacyGrade();
        after = Adjustments(Scene(tabled, broker));
        expect(converted && Types(tabled).SequenceEqual([EffectRegistry.Grade, EffectRegistry.Hue, EffectRegistry.Lut]) && tabled.LutPath == legacy.Path
               && tabled.HasLegacyGrade && tabled.SequenceOutputHoldsLut && Luts(tabled) == 1,
            "a LUT 3D card already there: the sliders alone become cards, the legacy LUT path kept (the fold stays for it), one LUT on the stack");
        expect(DescribeEntries(before) == "Lut3D,ColorGrade,HueSaturation | Lut3D" && DescribeEntries(after) == "Lut3D | ColorGrade,HueSaturation,Lut3D"
               && Same(RunAll(swatches, before, (legacy.Table, legacy.Size), (own.Table, own.Size)), RunAll(swatches, after, (legacy.Table, legacy.Size), (own.Table, own.Size))),
            $"...the same picture within 1/255, both LUTs drawn (before {DescribeEntries(before)}; after {DescribeEntries(after)})");
        expect(GpuExport.LeftOut(StateOf(tabled)).Count == 0 && !tabled.ConvertLegacyGrade() && tabled.LutPath == legacy.Path,
            "...nothing left out of an export; Convert again: nothing to move (the kept LUT stays)");

        // The fold's Reset (5.6A fix round 2, G1): the kept LUT can still be taken away, with the sliders, as one step of Undo.
        var kept = string.Join(",", Types(tabled));
        var steps = Undo(tabled).Count;
        expect(tabled.ResetLegacyGrade() && Undo(tabled).Count == steps + 1 && tabled.LutPath == "" && tabled.GetSequenceGrade().IsNeutral && !tabled.HasLegacyGrade
               && string.Join(",", Types(tabled)) == kept && DescribeEntries(Adjustments(Scene(tabled, broker))) == "ColorGrade,HueSaturation,Lut3D",
            $"Reset with the LUT kept (Convert off): the legacy LUT path empty, the fold gone, one step of Undo, the cards as they were, the user's LUT alone drawn ({DescribeEntries(Adjustments(Scene(tabled, broker)))})");
        tabled.Undo();
        expect(tabled.LutPath == legacy.Path && tabled.HasLegacyGrade && string.Join(",", Types(tabled)) == kept, "...Undo: the kept LUT back, the cards as they were");

        // Reset with the sliders moved and the LUT kept (not converted): the sliders and the LUT, one step; Undo puts both back, Redo takes both.
        var moved = Legacy();
        var ownTable = EffectRegistry.Find(EffectRegistry.Lut)!.NewEffect();
        ownTable.SetText("file", own.Path);
        moved.SequenceEffects.Add(ownTable);
        double[] Sliders(MainViewModel vm) => [vm.ColorContrast, vm.ColorBrightness, vm.ColorSaturation, vm.ColorGamma, vm.ColorHue, vm.ColorRed, vm.ColorGreen, vm.ColorBlue, vm.SharpenStrength];
        var original = Sliders(moved);
        steps = Undo(moved).Count;
        expect(moved.ResetLegacyGrade() && Undo(moved).Count == steps + 1 && moved.LutPath == "" && moved.GetSequenceGrade().IsNeutral && !moved.HasLegacyGrade && Types(moved).SequenceEqual([EffectRegistry.Lut]),
            "Reset with the sliders moved and the LUT kept: the sliders neutral and the LUT path empty in one step of Undo, the user's card left alone");
        moved.Undo();
        expect(moved.LutPath == legacy.Path && Sliders(moved).SequenceEqual(original), "...Undo: the LUT path and every slider back as they were");
        moved.Redo();
        expect(moved.LutPath == "" && moved.GetSequenceGrade().IsNeutral, "...Redo: both reset again");
        steps = Undo(moved).Count;
        expect(!moved.ResetLegacyGrade() && Undo(moved).Count == steps, "...Reset again: nothing to reset, no step taken");

        // A Color Grade card holding a LUT (at 60 %): the same; the card keeps its own LUT.
        var held = Legacy();
        var holder = Studio.NewEffect();
        holder.SetText(ColorGradeKeys.Lut, own.Path);
        holder.Set(ColorGradeKeys.LutMix, 60);
        holder.Set(ColorGradeKeys.Exposure, -0.2);
        held.SequenceEffects.Add(holder);
        before = Adjustments(Scene(held, broker));
        converted = held.ConvertLegacyGrade();
        after = Adjustments(Scene(held, broker));
        expect(converted && Types(held).SequenceEqual([EffectRegistry.Grade, EffectRegistry.Hue, EffectRegistry.ColorStudio]) && held.LutPath == legacy.Path
               && ReferenceEquals(held.FindColorGrade(ColorGradeTarget.ForSequence), holder) && holder.GetText(ColorGradeKeys.Lut) == own.Path,
            "a Color Grade card holding a LUT: the sliders alone become cards, the legacy LUT path kept, the card's LUT its own");
        expect(DescribeEntries(before) == "Lut3D,ColorGrade,HueSaturation | ColorStudio,ColorStudio" && DescribeEntries(after) == "Lut3D | ColorGrade,HueSaturation,ColorStudio,ColorStudio"
               && Same(RunAll(swatches, before, (legacy.Table, legacy.Size), (own.Table, own.Size)), RunAll(swatches, after, (legacy.Table, legacy.Size), (own.Table, own.Size)))
               && GpuExport.LeftOut(StateOf(held)).Count == 0,
            $"...the same picture within 1/255 (the card's LUT at 60 %), nothing left out (before {DescribeEntries(before)}; after {DescribeEntries(after)})");
    }

    // ----- grade-compare-plan -----

    private static void ComparePlan(Action<bool, string> expect)
    {
        var vm = new MainViewModel();
        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        vm.ColorContrast = 1.3;
        var blur = EffectRegistry.Find(EffectRegistry.Blur)!.NewEffect();
        var grade = Studio.NewEffect();
        grade.Set(ColorGradeKeys.Exposure, 0.5);
        vm.SequenceEffects.Add(blur);
        vm.SequenceEffects.Add(grade);

        string Describe((GpuLayerDescriptor[] Layers, int Count) scene) =>
            string.Join(" | ", Enumerable.Range(0, scene.Count).Select(i => Slots(scene.Layers[i])));

        var off = Scene(vm, broker);
        expect(Describe(off) == "ColorGrade | BoxBlur,ColorStudio,ColorStudio", $"off: the colour sliders' entry, then the Sequence Output's blur and Color Grade ({Describe(off)})");
        var bypass = Scene(vm, broker, new GradeCompare(GradeCompareMode.Bypass));
        expect(Describe(bypass) == "BoxBlur", $"Before (Hold \\): every Color Grade card and the sliders' grade left out, the blur kept ({Describe(bypass)})");
        var input = Scene(vm, broker, new GradeCompare(GradeCompareMode.Bypass, 0.5, grade.Id));
        expect(Describe(input) == "ColorGrade | BoxBlur", $"the Color Studio's input still: that card alone left out ({Describe(input)})");

        var split = Scene(vm, broker, new GradeCompare(GradeCompareMode.Split, 0.5));
        var wipes = Enumerable.Range(0, split.Count).Select(i => PacketsOf(split.Layers[i]).LastOrDefault()).ToList();
        expect(Describe(split) == "ColorGrade,TransitionWipe | BoxBlur,TransitionWipe | BoxBlur,ColorStudio,ColorStudio,TransitionWipe",
            $"split at the middle: the sliders' entry right of the divider alone (nothing to draw without its grade), the Sequence Output's twice: its blur left, blur and grade right ({Describe(split)})");
        expect(wipes.Count == 3 && wipes.All(w => w is { Slot: GpuShaderSlot.TransitionWipe, P0: 0.5f, P1: 1, P2: 0, P5: GpuScenePlan.CompareEdgePixels })
               && wipes[0].P4 == 0 && wipes[1].P4 == 1 && wipes[2].P4 == 0,
            "each half's wipe: at 0.5 of the box, across, a 1-pixel hard edge (lane 5), the Before kept left (fade-in lane 1), the After kept right");
        expect(Describe(Scene(vm, broker, new GradeCompare(GradeCompareMode.Split, 0))) == Describe(off)
               && Describe(Scene(vm, broker, new GradeCompare(GradeCompareMode.Split, 1))) == Describe(bypass),
            "the divider at the left edge: all graded, no extra layer; at the right edge: all Before");

        // Nothing else evaluates with a compare: the plan's default is off, and only the player's frames and the input still pass one.
        var plain = Scene(vm, broker, GradeCompare.Off);
        expect(Describe(plain) == Describe(off), "Evaluate without a compare is the graded picture (the export's, the strips', every still's)");
        var engine = SourceProbe.Repo("src", "Services", "GpuPreviewEngine.cs");
        var evaluates = System.Text.RegularExpressions.Regex.Matches(engine, @"\.Evaluate\(([^;]*)\);").Select(m => m.Groups[1].Value).ToList();
        expect(evaluates.Count == 3 && evaluates.Count(e => e.EndsWith("PlayerCompare()", StringComparison.Ordinal)) == 1
               && evaluates.Count(e => e.EndsWith("ThumbnailCompare(request.Task)", StringComparison.Ordinal)) == 1
               && evaluates.Count(e => e == "at, ref header, layers") == 1,
            "GpuPreviewEngine: the player's frame (PublishAt) passes the player's compare, the project still the input still's (its own request alone), Export Frame none");
        string[] others = [SourceProbe.Repo("src", "Services", "GpuExport.cs"), SourceProbe.Repo("src", "Services", "GpuProgramStrip.cs"), SourceProbe.Repo("tools", "SeekBench", "ProjectPlayback.cs")];
        expect(others.All(s => !s.Contains("GradeCompare", StringComparison.Ordinal) && !s.Contains("PlayerCompare", StringComparison.Ordinal)), "the export, the program strip and SeekBench evaluate plans with no compare");
    }

    // ----- keybind-grade-compare -----

    private static void Keys(Action<bool, string> expect)
    {
        var compare = KeybindManager.Action("view.compareGrade");
        var fit = KeybindManager.Action("view.fit");
        expect(compare is { Label: "Before/After (Hold)", Category: "View" } && compare.Has(KeyActionFlags.Hold) && compare.Defaults.SequenceEqual(["\\"]),
            "view.compareGrade: Before/After (Hold), a Hold action on \\");
        expect(fit is not null && fit.Defaults.SequenceEqual(["Shift+\\"]) && KeybindManager.Parse("Shift+\\") is { } chord && KeybindManager.Validate(chord, "view.fit") is null
               && KeybindManager.Format(chord) == "Shift+\\", "view.fit (Fit Timeline) on Shift+\\, a legal chord for it");
        expect(KeybindManager.Parse("\\") is { } held && KeybindManager.Validate(held, "view.compareGrade") is null
               && KeybindManager.Parse("Shift+\\") is { } shifted && KeybindManager.Validate(shifted, "view.compareGrade") is not null,
            "a Hold action takes \\ alone, never with a modifier");
        var problems = KeybindManager.CheckPresets();
        expect(problems.Count == 0, "KeybindManager.CheckPresets(): no preset gives a chord to two actions" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""));
        expect(KeybindManager.PresetNames.All(p => KeybindManager.PresetChords(p) is var chords && chords["view.compareGrade"].Select(KeybindManager.Format).SequenceEqual(["\\"])
               && chords["view.fit"].Select(KeybindManager.Format).SequenceEqual(["Shift+\\"])), "every preset: \\ the Before/After, Shift+\\ Fit Timeline");

        var keybinds = SourceProbe.Repo("src", "MainWindow.Keybinds.cs");
        expect(keybinds.Contains("[\"view.compareGrade\"] = (() => _gpu is not null, ToggleGradeBypass)", StringComparison.Ordinal),
            "the dial and the hotbar run it as an on and off switch (MakeKeyActions)");
        expect(keybinds.Contains("if (id == \"view.compareGrade\")", StringComparison.Ordinal) && keybinds.Contains("BeginGradeHold();", StringComparison.Ordinal)
               && keybinds.Contains("if (_holdAction == \"view.compareGrade\")", StringComparison.Ordinal),
            "held: on at once as the key goes down (no 250 ms wait), off as it comes up; X-Ray keeps its tap or hold");
        var drop = keybinds[keybinds.IndexOf("private void DropTransientInput()", StringComparison.Ordinal)..];
        expect(drop[..drop.IndexOf("\n    }", StringComparison.Ordinal)].Contains("EndGradeHold();", StringComparison.Ordinal), "the window losing the keyboard ends a held Before/After (DropTransientInput)");
    }

    // ----- color-studio-fixes (Pass 5.6A review, lane FA) -----

    private static GpuLayerDescriptor Descriptor(params GpuEffectPacket[] packets)
    {
        var layer = new GpuLayerDescriptor { EffectCount = packets.Length };
        for (var k = 0; k < packets.Length; k++)
            layer.Effects[k] = packets[k];
        return layer;
    }

    private static GpuEffectPacket Packet(GpuShaderSlot slot) => new(slot, 0, 0, 0, 0, 0, 0, 0, 0);

    private static void Fixes(Action<bool, string> expect)
    {
        // F1: the Color Grade's shader reads no clock, so a graded plane is kept in the 3D view as an ungraded one is.
        var studio = Descriptor(Packet(GpuShaderSlot.ColorStudio), Packet(GpuShaderSlot.ColorStudio));
        expect(!HandPegApp.Compositor.SceneRenderer.VariesWithClock(studio) && !HandPegApp.Compositor.SceneRenderer.VariesWithClock(Descriptor(Packet(GpuShaderSlot.ColorGrade), Packet(GpuShaderSlot.ToneCurve)))
               && HandPegApp.Compositor.SceneRenderer.VariesWithClock(Descriptor(Packet(GpuShaderSlot.ColorStudio), Packet(GpuShaderSlot.ColorStudio), Packet(GpuShaderSlot.FilmGrain))),
            "3D view: a Color Grade (slot 30) does not vary with the clock (its plane kept while playing); Film Grain still does");

        // F4: a Color Grade card that loses its LUT must not hide a Curves card dropped after it; the card named for the table it loses.
        var vm = new MainViewModel();
        var (path, _, _) = Cube("handpeg-color-studio-fixes.cube");
        try
        {
            var table = EffectRegistry.Find(EffectRegistry.Lut)!.NewEffect();
            table.SetText("file", path);
            var grade = Studio.NewEffect();
            grade.SetText(ColorGradeKeys.Lut, path);
            grade.SetText(ColorGradeKeys.CurveMaster, "0/0 0.5/0.7 1/1");
            var curves = EffectRegistry.Find(EffectRegistry.Curves)!.NewEffect();
            vm.SequenceEffects.Add(table);
            vm.SequenceEffects.Add(grade);
            vm.SequenceEffects.Add(curves);
            var names = GpuExport.LeftOut(StateOf(vm));
            expect(ColorGradeMath.CurvesActive(grade) && names.SequenceEqual(["the Sequence Output's Color Grade LUT", "the Sequence Output's second Curves"]),
                $"export: [LUT 3D, Color Grade with a LUT and a curve, Curves]: the Color Grade's lost LUT and the dropped Curves both named ({string.Join("; ", names)})");
        }
        finally
        {
            File.Delete(path);
        }

        // F5: the edge loupe skips the grade compare's Before half (the ungraded copy), so the graded half is the one magnified.
        var before = Descriptor(Packet(GpuShaderSlot.CornerFeather), GpuScenePlan.CompareWipe(0.5f, keepLeft: true));
        var after = Descriptor(Packet(GpuShaderSlot.CornerFeather), Packet(GpuShaderSlot.ColorStudio), Packet(GpuShaderSlot.ColorStudio), GpuScenePlan.CompareWipe(0.5f, keepLeft: false));
        var realWipe = Descriptor(new GpuEffectPacket(GpuShaderSlot.TransitionWipe, 0.5f, 1, 0, 0, 1, 0, 0, 0));
        var loupe = SourceProbe.Repo("src", "Compositor", "SceneRenderer.Loupe.cs");
        expect(HandPegApp.Compositor.SceneRenderer.IsCompareBefore(before) && !HandPegApp.Compositor.SceneRenderer.IsCompareBefore(after)
               && !HandPegApp.Compositor.SceneRenderer.IsCompareBefore(realWipe) && !HandPegApp.Compositor.SceneRenderer.IsCompareBefore(studio)
               && loupe.Contains("== 0 && !IsCompareBefore(candidate))", StringComparison.Ordinal),
            "loupe: the compare's Before half (its hard-edged wipe keeping the left) is passed over; the After half, a real wipe and an unsplit layer are not");

        // F3/F12: a started-again engine is told the grade's Before/After right after its view.
        var preview = SourceProbe.Repo("src", "MainWindow.GpuPreview.cs");
        var apply = SourceProbe.Member(SourceProbe.Src("Services", "GpuPreviewEngine.cs"), "public void Apply(PlayerInputs inputs)");
        expect(SourceProbe.Member(preview, "private async Task StartGpuAsync(").Contains("engine.Apply(PlayerInputs());", StringComparison.Ordinal)
               && preview.Contains("_playerZoom, _playerPanX, _playerPanY, GradeCompareNow, _gradeWipe);", StringComparison.Ordinal)
               && SourceProbe.InOrder(apply, "SetView(inputs.ZoomLog2, inputs.PanX, inputs.PanY);", "SetGradeCompare(inputs.GradeCompare, inputs.GradeWipe);"),
            "Reload Player or a restart: the new engine gets the window's compare (StartGpuAsync's PlayerInputs, after SetView)");

        // F7: the LUT a Convert moves lives in the undo step (SequenceLook): the Color Studio keeps nothing of its own about it.
        var pane = SourceProbe.Repo("src", "Views", "ColorStudioPane.xaml.cs") + SourceProbe.Repo("src", "Views", "ColorStudioPane.Folds.cs") + SourceProbe.Repo("src", "Views", "ColorStudioPane.Writes.cs");
        expect(!pane.Contains("_convertedLut", StringComparison.Ordinal) && !pane.Contains("TimelineRestored", StringComparison.Ordinal),
            "the Color Studio keeps no moved LUT and puts nothing back on a restore (no project or tab can get another's LUT)");

        // F10 (fix round 2, G2): a focused box the user has not edited follows an Undo; typed text is kept. Both refresh sites use the helper.
        expect(ColorStudioPane.BoxFollows(false, "x", "y") && ColorStudioPane.BoxFollows(true, "1.50 EV", "1.50 EV") && !ColorStudioPane.BoxFollows(true, "1.6", "1.50 EV"),
            "boxes: an unfocused box follows; a focused one follows while unedited (an Undo after Enter), never over typed text");
        expect(pane.Contains("if (BoxFollows(row.Box.IsKeyboardFocusWithin, row.Box.Text, row.Shown))", StringComparison.Ordinal)
               && pane.Contains("if (BoxFollows(_lutBox.IsKeyboardFocusWithin, _lutBox.Text, _lutShown))", StringComparison.Ordinal),
            "...the value boxes and the LUT box both refresh through it (RefreshControls)");

        // G1 (fix round 2): the Legacy Grade fold's Reset clears the sliders and the LUT (MainViewModel.ResetLegacyGrade), its tooltip says so.
        expect(pane.Contains("reset.Click += (_, _) => ResetLegacy();", StringComparison.Ordinal) && pane.Contains("vm.ResetLegacyGrade();", StringComparison.Ordinal)
               && pane.Contains("ToolTip = \"Reset the legacy sliders and LUT\"", StringComparison.Ordinal) && !pane.Contains("ResetColorCommand", StringComparison.Ordinal),
            "the Legacy Grade fold's Reset takes the sliders and the LUT, one step of Undo (Reset the legacy sliders and LUT)");

        // F14: the Color tab's Tabs and Panes line within 12 words; the clip Filters dialog's intro one true sentence.
        var about = System.Text.RegularExpressions.Regex.Match(SourceProbe.Repo("src", "MainWindow.Export.cs"), "ColorTabId => \"([^\"]*)\"").Groups[1].Value;
        var dialog = SourceProbe.Repo("src", "Dialogs", "LayerToolDialog.xaml.cs");
        expect(about.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is > 0 and <= 12
               && dialog.Contains("IntroText.Text = \"Filters for this clip alone; grade its color on the Color tab.\";", StringComparison.Ordinal),
            $"microcopy: the Color tab's tooltip in {about.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words; the Filters dialog says where a clip's color is graded");
    }

    // ----- dock-color-tab-migration -----

    private static readonly string[] OldTabs =["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];
    private static readonly string[] NewTabs = ["Tab:Color", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];
    private static readonly string[] PaneIds = ["Player", "Playback", "Timeline", "UnifiedTimeline", "Layout", "Header", "Media", "Keyframes", "Effects", "Meter", "Presets"];

    private static string Json(DockLayoutState state) => JsonSerializer.Serialize(state, AppSettingsJsonContext.Default.DockLayoutState);

    private static DockManager NewDock()
    {
        var dock = new DockManager(new Window(), new System.Windows.Controls.ContentControl(), () => MainWindow.StandardLayout(NewTabs))
        {
            Replaced = MainWindow.ReplacedPanes,
        };
        foreach (var id in PaneIds.Concat(NewTabs))
        {
            var content = new System.Windows.Controls.Border();
            dock.Register(new DockPane
            {
                Id = id, Title = id, Content = () => content, Handle = id.StartsWith("Tab:", StringComparison.Ordinal) ? PaneHandle.Tab : PaneHandle.TitleSlot,
                MinWidth = () => 160, MinHeight = 60,
            });
        }

        return dock;
    }

    private static void DockMigration(Action<bool, string> expect)
    {
        expect(MainWindow.ReplacedPanes.TryGetValue("Tab:Filters", out var color) && color == "Tab:Color" && MainWindow.ReplacedPanes.GetValueOrDefault("Source") == "Media",
            "MainWindow.ReplacedPanes: Tab:Filters is Tab:Color (and Source still Media)");

        // A Standard Editing saved before 5.6A, the Filters tab the one shown in its group.
        var old = MainWindow.StandardLayout(OldTabs);
        var group = old.Main.GroupOf("Tab:Filters")!;
        group.Active = "Tab:Filters";
        var dock = NewDock();
        dock.Load(Json(old));
        var main = DockLayoutState.Read(dock.Save())!;
        var tabs = main.Main.GroupOf("Tab:Color");
        expect(tabs is not null && tabs.Active == "Tab:Color" && !main.Main.AllPanes().Contains("Tab:Filters") && tabs.Panes.IndexOf("Tab:Color") == group.Panes.IndexOf("Tab:Filters"),
            "the main window's tree: Tab:Filters is Tab:Color in its place, still the one shown in its group");

        // A Pane Docker holding the Filters tab, and the closed side arrangement of before 5.4C holding it.
        var docker = new SideWindowState { Id = "side-2", Mode = SideWindowState.PanesMode, Tree = DockNode.Split(false, (DockNode.Group("Tab:Filters"), 1), (DockNode.Group("Keyframes"), 1)) };
        var standard = MainWindow.StandardLayout(OldTabs);
        foreach (var pane in new[] { "Tab:Filters", "Keyframes" })
            standard.Main = DockTree.Remove(standard.Main, pane)!;
        dock = NewDock();
        dock.Load(Json(standard), sideWindows: SideWindowStates.Write([docker]));
        var sides = SideWindowStates.Read(dock.SaveSideWindows());
        var all = DockLayoutState.Read(dock.Save())!.Main.AllPanes().Concat(sides.SelectMany(s => s.Tree?.AllPanes() ?? [])).ToList();
        expect(sides.Count == 1 && sides[0].Tree?.AllPanes().Contains("Tab:Color") == true && !all.Contains("Tab:Filters") && all.Count(p => p == "Tab:Color") == 1,
            "a Pane Docker's tree: its Filters tab is the Color tab, held there once (not put back in the main window)");

        var legacy = MainWindow.StandardLayout(OldTabs);
        legacy.Main = DockTree.Remove(legacy.Main, "Tab:Filters")!;
        legacy.Side = DockNode.Group("Tab:Filters");
        legacy.SideOpen = true;
        dock = NewDock();
        dock.Load(Json(legacy));
        sides = SideWindowStates.Read(dock.SaveSideWindows());
        expect(sides.Count == 1 && sides[0].Tree?.AllPanes().SequenceEqual(["Tab:Color"]) == true, "the side window of before 5.4C: its Filters tab is the Color tab");

        // The Default layout's shape check (StartupLayoutJson: LoadsAlike) still matches an arrangement saved with Tab:Filters.
        expect(NewDock().LoadsAlike(Json(MainWindow.StandardLayout(OldTabs)), MainWindow.StandardLayout(NewTabs), null),
            "an old Standard Editing loads alike the Standard Editing of now (Frame 1 keeps the user's saved arrangement)");

        // Hidden tabs: a hidden Filters tab stays hidden as the Color tab (AppSettings version 13), on the test's own settings file.
        var file = AppSettings.FilePath;
        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var saved = new AppSettings { ViewDefaultsVersion = 12, FirstRunComplete = true, HiddenTabs = ["Tab:Filters", "Tab:Command Preview"] };
            File.WriteAllText(file, JsonSerializer.Serialize(saved, AppSettingsJsonContext.Default.AppSettings));
            var read = AppSettings.Load();
            expect(read is { ViewDefaultsVersion: >= 13 } && read.HiddenTabs?.SequenceEqual(["Tab:Color", "Tab:Command Preview"]) == true,
                "AppSettings version 13: a hidden Filters tab is a hidden Color tab, the other hidden tabs kept");
        }
        finally
        {
            if (original is null)
                File.Delete(file);
            else
                File.WriteAllBytes(file, original);
        }

        // The window's own: the Color tab a lazy template holding the Color Studio, its Tabs and Panes line.
        var xaml = XDocument.Load(Path.Combine(SourceProbe.Root, "src", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tab = xaml.Descendants().FirstOrDefault(e => e.Name.LocalName == "TabItem" && (string?)e.Attribute(x + "Name") == "ColorTab");
        var studio = tab?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ColorStudioPane");
        expect(tab is not null && (string?)tab.Attribute("Header") == "Color" && studio is not null && (string?)studio.Attribute(x + "Name") == "ColorStudio"
               && studio.Ancestors().Any(a => a.Name.LocalName == "DataTemplate"),
            "MainWindow.xaml: <TabItem Header=\"Color\" x:Name=\"ColorTab\">, the Color Studio in its Tag's template (made when first shown or at idle)");
        var export = SourceProbe.Repo("src", "MainWindow.Export.cs");
        expect(export.Contains("ColorTabId => \"The Color tab (the Color Studio)", StringComparison.Ordinal), "Tabs and Panes describes the Color tab");
    }

    // ----- color-studio-lazy -----

    /// <summary>Runs the dispatcher for a while: its timers, its queued work and the windows' layout.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Lazy(Action<bool, string> expect)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var vm = new MainViewModel();
        var captures = new List<Guid>();
        var grey = new byte[64 * 36 * 4];
        Array.Fill(grey, (byte)128);
        var pane = new ColorStudioPane();
        pane.Attach(vm, card =>
        {
            captures.Add(card);
            return Task.FromResult<(byte[] Bgra, int Width, int Height)?>((grey, 64, 36));
        }, null);
        pane.SetTargetForTests(ColorGradeTarget.ForSequence);
        var window = new Window { Left = -20000, Top = -20000, Width = 560, Height = 1400, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = pane };
        try
        {
            expect(!pane.IsBuilt && captures.Count == 0, "made, attached and given its target: nothing built, nothing captured (Law 1)");
            window.Show();
            Pump(ColorStudioPane.ThrottleMs * 3);
            expect(pane.IsBuilt && captures.Count == 1 && captures[0] == Guid.Empty, "shown and paused: built, one capture of the input (no card yet: the picture as shown)");

            vm.IsPlaying = true;
            vm.PositionMs = 2000;
            Pump(ColorStudioPane.ThrottleMs * 3);
            expect(captures.Count == 1, "while playing: no capture (Law 3)");
            vm.IsPlaying = false;
            Pump(ColorStudioPane.ThrottleMs * 3);
            expect(captures.Count == 2, "paused again: captured once more");

            // A wheel drag (point methods, never synthetic input): the card put on, one step of Undo for the whole drag.
            var wheel = pane.Wheel(0);
            var centre = wheel.DiscCentre;
            var steps = Undo(vm).Count;
            var began = wheel.BeginDrag(centre);
            for (var i = 1; i <= 6; i++)
            {
                wheel.DragTo(new Point(centre.X + i * 4, centre.Y - i), lockAxis: false);
                Pump(20);
            }

            wheel.EndDrag();
            var card = vm.FindColorGrade(ColorGradeTarget.ForSequence);
            var values = card is null ? default : ColorGradeValues.From(card);
            expect(began && card is not null && ReferenceEquals(pane.Card, card) && Undo(vm).Count == steps + 1 && values.LiftActive
                   && Math.Abs(values.LiftR - wheel.Value.R) < 1e-3 && Math.Abs(values.LiftB - wheel.Value.B) < 1e-3,
                "a Lift drag on a target without a card: the card put on, the drag's values written, one step of Undo for it all");
            wheel.BeginDrag(centre);
            wheel.DragTo(new Point(centre.X - 10, centre.Y + 6));
            wheel.DragTo(new Point(centre.X - 14, centre.Y + 9));
            wheel.EndDrag();
            expect(Undo(vm).Count == steps + 2, "a second drag: one more step");

            // A curve: a point added and dragged, one step; Undo takes it back and the pane finds the card Undo put in.
            var surface = pane.Curves.Surface;
            var at = new Point(surface.ActualWidth * 0.5, surface.ActualHeight * 0.5);
            surface.BeginDrag(at);
            surface.DragTo(new Point(at.X, at.Y - 20));
            surface.EndDrag();
            var curve = card?.GetText(ColorGradeKeys.CurveMaster) ?? "";
            expect(Undo(vm).Count == steps + 3 && curve.Length > 0 && ColorGradeMath.CurvesActive(card!), "a point added to the Master curve and dragged: one step, the curve written");
            vm.Undo();
            Pump(40);
            var after = vm.FindColorGrade(ColorGradeTarget.ForSequence);
            expect(after is not null && !ReferenceEquals(after, card) && ReferenceEquals(pane.Card, after) && after.GetText(ColorGradeKeys.CurveMaster) == "",
                "Undo puts new cards in: the pane follows the new card, its curve back to none");

            window.Content = null;
            Pump(40);
            var seen = captures.Count;
            vm.PositionMs = 3000;
            Pump(ColorStudioPane.ThrottleMs * 3);
            expect(captures.Count == seen, "out of the window (a dock rebuild, the tab behind another): nothing is captured");
        }
        finally
        {
            vm.IsPlaying = false;
            window.Close();
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    // ----- color-studio-boxes (Pass 5.6A review, lane FA: F6, F9, F10, F13 and a target with nothing to grade) -----

    private static T Part<T>(ColorStudioPane pane, string field) => (T)typeof(ColorStudioPane).GetField(field, Hidden)!.GetValue(pane)!;

    /// <summary>The first element under <paramref name="root"/> (its logical tree) of that type with that automation name.</summary>
    private static T? Named<T>(DependencyObject root, string name) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T found && System.Windows.Automation.AutomationProperties.GetName(found) == name)
                return found;
            if (Named<T>(child, name) is { } deeper)
                return deeper;
        }

        return null;
    }

    private static void Boxes(Action<bool, string> expect)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var vm = new MainViewModel();
        var grey = new byte[64 * 36 * 4];
        Array.Fill(grey, (byte)128);
        var pane = new ColorStudioPane();
        pane.Attach(vm, _ => Task.FromResult<(byte[] Bgra, int Width, int Height)?>((grey, 64, 36)), null);
        pane.SetTargetForTests(ColorGradeTarget.ForSequence);
        var window = new Window { Left = -20000, Top = -20000, Width = 560, Height = 1400, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = pane };
        var (path, _, _) = Cube("handpeg-color-studio-boxes.cube");
        try
        {
            window.Show();
            Pump(80);
            var rows = Part<System.Collections.IDictionary>(pane, "_rows");
            var (temperature, exposure) = (rows[ColorGradeKeys.Temperature]!, rows[ColorGradeKeys.Exposure]!);
            var rowType = temperature.GetType();
            System.Windows.Controls.TextBox BoxOf(object row) => (System.Windows.Controls.TextBox)rowType.GetProperty("Box")!.GetValue(row)!;
            double SliderOf(object row) => ((System.Windows.Controls.Slider)rowType.GetProperty("Slider")!.GetValue(row)!).Value;
            string ShownOf(object row) => (string)rowType.GetProperty("Shown")!.GetValue(row)!;
            var commitBox = typeof(ColorStudioPane).GetMethod("CommitBox", Hidden)!;
            var commitLut = typeof(ColorStudioPane).GetMethod("CommitLut", Hidden)!;
            bool Commit(object row)
            {
                try
                {
                    commitBox.Invoke(pane, [row]);
                    return true;
                }
                catch (TargetInvocationException)
                {
                    return false;
                }
            }

            // F10 (a): boxes left untouched (tabbed through, clicked in and out) write nothing.
            var steps = Undo(vm).Count;
            var left = Commit(temperature) && Commit(exposure);
            expect(left && vm.FindColorGrade(ColorGradeTarget.ForSequence) is null && Undo(vm).Count == steps,
                "Temperature and Exposure left untouched: nothing written (no card put on, no step of Undo)");

            // F6: a box whose parameter has no unit, typed into and committed.
            BoxOf(temperature).Text = "12";
            var committed = Commit(temperature);
            var card = vm.FindColorGrade(ColorGradeTarget.ForSequence);
            expect(committed && card is not null && card.Get(ColorGradeKeys.Temperature) == 12 && Undo(vm).Count == steps + 1,
                "Temperature (no unit) typed and committed: no crash, the card put on holding 12, one step of Undo");

            // F9: a lone write is shown (the slider follows the typed value; the card's first write no longer leaves the defaults showing).
            Pump(40);
            expect(Math.Abs(SliderOf(temperature) - 12) < 1e-9 && BoxOf(temperature).Text == "12" && ShownOf(temperature) == "12",
                "...the slider at 12 and the box showing 12 at once");
            BoxOf(exposure).Text = "9 EV";
            Commit(exposure);
            Pump(40);
            expect(card?.Get(ColorGradeKeys.Exposure) == 3 && BoxOf(exposure).Text == "3.00 EV" && Math.Abs(SliderOf(exposure) - 3) < 1e-9,
                "Exposure typed past its range (9 EV): held to 3, the box and the slider show 3.00 EV");

            // F10 (d): an Undo, then the boxes left: what Undo took back stays taken back, Redo kept.
            vm.Undo();
            Pump(40);
            (double, double) Values() => vm.FindColorGrade(ColorGradeTarget.ForSequence) is { } now
                ? (now.Get(ColorGradeKeys.Temperature), now.Get(ColorGradeKeys.Exposure)) : (0, 0);
            var (redo, undone) = (Redo(vm).Count, Values());
            var shownAfterUndo = BoxOf(exposure).Text;
            Commit(temperature);
            Commit(exposure);
            expect(redo > 0 && undone != (12, 3) && Values() == undone && Redo(vm).Count == redo && shownAfterUndo != "3.00 EV" && BoxOf(exposure).Text == shownAfterUndo,
                "Undo, then the boxes left: nothing written back, the Undo stays undone and Redo kept; the boxes show the values after Undo");

            // F9 + F10 for the LUT box: a typed path is written once; Clear empties the box at once and leaving it writes nothing back.
            vm.Redo();
            Pump(40);
            var lutBox = Part<System.Windows.Controls.TextBox>(pane, "_lutBox");
            lutBox.Text = "\"" + path + "\"";
            commitLut.Invoke(pane, null);
            Pump(40);
            var redone = vm.FindColorGrade(ColorGradeTarget.ForSequence);
            expect(redone?.GetText(ColorGradeKeys.Lut) == path && lutBox.Text == path, "a LUT path typed (quoted): written without its quotes, the box shows it as written");
            var clear = Named<System.Windows.Controls.Button>(pane, "Clear LUT");
            clear?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(40);
            commitLut.Invoke(pane, null);
            expect(clear is not null && redone?.GetText(ColorGradeKeys.Lut) == "" && lutBox.Text == "",
                "Clear: the card's LUT gone and the box emptied with it (no stale path written back when the box is left)");

            // F13: the Compare eye names no shortcut of another action.
            var eye = Part<System.Windows.Controls.Primitives.ToggleButton>(pane, "_compareEye");
            expect(eye.ToolTip as string == "Split Before and After on the player", "the Compare eye's tooltip: no bracket (Hold \\ is the Bypass, not the eye)");

            // A target with nothing to grade (a sound clip can be the Effects pane's): no card ever put on it or written, Reset off.
            var sound = HandPegApp.Models.Layer.FromState(new LayerState { Kind = LayerKind.Audio, Name = "Voice" }, 1920, 1080, 1920, 1080);
            steps = Undo(vm).Count;
            var captured = pane.CaptureCount;
            pane.SetTargetForTests(ColorGradeTarget.For(sound));
            Pump(ColorStudioPane.ThrottleMs * 3);
            BoxOf(temperature).Text = "30";
            Commit(temperature);
            var (empty, body, reset) = (Part<System.Windows.Controls.TextBlock>(pane, "_empty"), Part<System.Windows.Controls.StackPanel>(pane, "_body"), Part<System.Windows.Controls.Button>(pane, "_resetButton"));
            expect(!vm.CanColorGrade(ColorGradeTarget.For(sound)) && empty is { Visibility: Visibility.Visible, Text: "Nothing to grade" } && body.Visibility == Visibility.Collapsed
                   && !reset.IsEnabled && pane.Card is null && sound.Effects.Count == 0 && Undo(vm).Count == steps && pane.CaptureCount == captured,
                "a sound clip as the target: Nothing to grade, the controls hidden, Reset off; a typed value puts no card on it, no step, no still captured");
            var stray = Studio.NewEffect();
            sound.Effects.Add(stray);
            Pump(40);
            BoxOf(temperature).Text = "40";
            Commit(temperature);
            typeof(ColorStudioPane).GetMethod("ResetGrade", Hidden)!.Invoke(pane, null);
            stray.Set(ColorGradeKeys.Exposure, 1);
            typeof(ColorStudioPane).GetMethod("ResetGrade", Hidden)!.Invoke(pane, null);
            expect(pane.Card is null && stray.Get(ColorGradeKeys.Temperature) == 0 && stray.Get(ColorGradeKeys.Exposure) == 1 && Undo(vm).Count == steps,
                "...a Color Grade card it holds anyway is never the pane's: neither a typed value nor Reset writes it");
        }
        finally
        {
            window.Close();
            SynchronizationContext.SetSynchronizationContext(context);
            File.Delete(path);
        }
    }
}
