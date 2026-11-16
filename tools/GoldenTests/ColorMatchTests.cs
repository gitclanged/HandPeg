using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6A lane B: Color Match. color-match-math (pure): OKLab both ways, the bilateral patch (a stray pixel and the far side of an
/// edge never pull it; cut at the frame's edges), the inverse of a card's grade, and the solver converging on the Color Grade card's own
/// model (ColorGradeMath.Apply) within OKLab dE 0.01 for exposure, white-balance and wheel cases, from a neutral card and from a graded one.
/// </summary>
public static class ColorMatchTests
{
    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("color-match-math", "# Color Match (5.6A): OKLab, the bilateral patch, the inverse of a grade, the solver against the Color Grade card's own model.", MatchMath);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("color-match-bar", "# Color Match's bar (5.6A review): a click on Reference cancels, the target on screen, the pick ended by an edit or a view, Esc first for a drag, a square loupe.", MatchBar);
    }

    private static string C((double R, double G, double B) c) => string.Create(CultureInfo.InvariantCulture, $"({c.R:0.000}, {c.G:0.000}, {c.B:0.000})");

    private static double Distance((double R, double G, double B) a, (double R, double G, double B) b) => ColorMatch.DeltaE(ColorMatch.ToOkLab(a), ColorMatch.ToOkLab(b));

    // A BGRA8 frame of a colour per pixel.
    private static byte[] Frame(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = colour(x, y);
                var at = (y * width + x) * 4;
                (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = (b, g, r, 255);
            }
        }

        return bgra;
    }

    private static void MatchMath(Action<bool, string> expect)
    {
        // ----- OKLab -----
        var white = ColorMatch.ToOkLab(1, 1, 1);
        var black = ColorMatch.ToOkLab(0, 0, 0);
        expect(Math.Abs(white.L - 1) < 1e-4 && Math.Abs(white.A) < 1e-4 && Math.Abs(white.B) < 1e-4 && Math.Abs(black.L) < 1e-9,
            string.Create(CultureInfo.InvariantCulture, $"OKLab of white ({white.L:0.0000}, {white.A:0.0000}, {white.B:0.0000}) and black (L {black.L:0.0000})"));
        var worst = 0.0;
        for (var r = 0.0; r <= 1; r += 0.125)
            for (var g = 0.0; g <= 1; g += 0.125)
                for (var b = 0.0; b <= 1; b += 0.125)
                {
                    var back = ColorMatch.FromOkLab(ColorMatch.ToOkLab(r, g, b));
                    worst = Math.Max(worst, Math.Max(Math.Abs(back.R - r), Math.Max(Math.Abs(back.G - g), Math.Abs(back.B - b))));
                }
        expect(worst < 1e-6, string.Create(CultureInfo.InvariantCulture, $"sRGB to OKLab and back over a 9 x 9 x 9 cube: worst {worst:0.0e+0}"));
        var red = ColorMatch.ToOkLab(1, 0, 0);
        expect(Math.Abs(red.L - 0.628) < 0.001 && Math.Abs(red.A - 0.2249) < 0.001 && Math.Abs(red.B - 0.1258) < 0.001,
            string.Create(CultureInfo.InvariantCulture, $"OKLab of pure red ({red.L:0.000}, {red.A:0.000}, {red.B:0.000}), as Ottosson gives it (0.628, 0.225, 0.126)"));

        // ----- The bilateral patch -----
        // Left of x = 15 red-orange, from x = 15 blue: the point one pixel left of the edge.
        var edge = Frame(31, 31, (x, _) => x < 15 ? ((byte)200, (byte)90, (byte)60) : ((byte)40, (byte)70, (byte)210));
        var picked = ColorMatch.BilateralMean(edge, 31, 31, 14, 15)!.Value;
        var plain = (R: 0.0, G: 0.0, B: 0.0);
        for (var y = 8; y <= 22; y++)
            for (var x = 7; x <= 21; x++)
            {
                var at = (y * 31 + x) * 4;
                plain = (plain.R + edge[at + 2] / 255.0 / 225, plain.G + edge[at + 1] / 255.0 / 225, plain.B + edge[at] / 255.0 / 225);
            }
        expect(Distance(picked, (200 / 255.0, 90 / 255.0, 60 / 255.0)) < 0.002 && Distance(plain, (200 / 255.0, 90 / 255.0, 60 / 255.0)) > 0.1,
            $"a point beside an edge (half its patch the other colour): the bilateral mean {C(picked)} is the side it is on; a plain mean would be {C(plain)}");

        // Noise and a stray pixel at the point itself.
        var random = new Random(56);
        var noisy = Frame(31, 31, (x, y) => x == 15 && y == 15 ? ((byte)255, (byte)255, (byte)255) : ((byte)(120 + random.Next(-6, 7)), (byte)(140 + random.Next(-6, 7)), (byte)(100 + random.Next(-6, 7))));
        var calm = ColorMatch.BilateralMean(noisy, 31, 31, 15, 15)!.Value;
        expect(Distance(calm, (120 / 255.0, 140 / 255.0, 100 / 255.0)) < 0.01,
            $"a noisy patch with a white stray pixel at the point: {C(calm)}, the colour under the noise (the point's colour is its 3 x 3 median)");
        var corner = ColorMatch.BilateralMean(edge, 31, 31, 0, 0);
        expect(corner is { } c && Distance(c, (200 / 255.0, 90 / 255.0, 60 / 255.0)) < 0.002 && ColorMatch.BilateralMean(edge, 31, 31, 31, 0) is null
               && ColorMatch.BilateralMean([], 0, 0, 0, 0) is null,
            "at the frame's corner the patch is cut there; a point off the frame, or no frame: nothing");

        // ----- The inverse of a grade -----
        var graded = new ColorGradeValues(25, -10, -0.4, 0.05, 0, -0.03, 0.1, 0.2, -0.1, 0.1, 0, 0.05, 1.15, 0.9, 0.2, 100);
        var worstInverse = 0.0;
        foreach (var colour in new[] { (0.30, 0.45, 0.25), (0.62, 0.48, 0.40), (0.15, 0.20, 0.35), (0.55, 0.55, 0.55) })
        {
            var shown = ColorGradeMath.Apply(colour.Item1, colour.Item2, colour.Item3, graded);
            var back = ColorMatch.Ungrade(shown, graded);
            var again = ColorGradeMath.Apply(back.R, back.G, back.B, graded);
            worstInverse = Math.Max(worstInverse, Math.Max(Math.Abs(again.R - shown.R), Math.Max(Math.Abs(again.G - shown.G), Math.Abs(again.B - shown.B))));
            worstInverse = Math.Max(worstInverse, Math.Max(Math.Abs(back.R - colour.Item1), Math.Max(Math.Abs(back.G - colour.Item2), Math.Abs(back.B - colour.Item3))));
        }
        expect(worstInverse < 1e-3 && ColorMatch.Ungrade((0.3, 0.4, 0.5), ColorGradeValues.Neutral) == (0.3, 0.4, 0.5),
            string.Create(CultureInfo.InvariantCulture, $"a graded colour ungraded (white balance, exposure, wheels, contrast, saturation, vibrance) and graded again: worst {worstInverse:0.0e+0}; a neutral card gives it back as it is"));

        // ----- The solver, on the card's own model -----
        var neutral = ColorGradeValues.Neutral;
        (string What, (double, double, double) Ungraded, ColorGradeValues Truth, ColorGradeValues Current)[] cases =
        [
            ("exposure +0.7 EV on a midtone", (0.35, 0.40, 0.30), neutral with { Exposure = 0.7 }, neutral),
            ("exposure -1.2 EV on a highlight", (0.80, 0.78, 0.74), neutral with { Exposure = -1.2 }, neutral),
            ("warmer (+40) and greener (-25)", (0.50, 0.50, 0.50), neutral with { Temperature = 40, Tint = -25 }, neutral),
            ("cooler (-60), darker (-0.3)", (0.62, 0.55, 0.48), neutral with { Temperature = -60, Exposure = -0.3 }, neutral),
            ("Gain red +0.2 on a highlight", (0.75, 0.72, 0.70), neutral with { GainR = 0.2 }, neutral),
            ("Lift blue +0.15 in a shadow", (0.08, 0.09, 0.10), neutral with { LiftB = 0.15 }, neutral),
            ("Gamma green -0.3 on a midtone", (0.45, 0.50, 0.40), neutral with { GammaG = -0.3 }, neutral),
            ("a skin tone to a warmer reference, from a graded card", (0.62, 0.45, 0.36), graded with { Temperature = 55, Exposure = -0.1 }, graded),
            ("a teal sky from a graded card, cooler and brighter", (0.35, 0.52, 0.66), graded with { Temperature = -20, Exposure = 0.2, Tint = 10 }, graded),
        ];
        foreach (var (what, ungraded, truth, current) in cases)
        {
            var (ur, ug, ub) = ungraded;
            var target = ColorGradeMath.Apply(ur, ug, ub, current);
            var reference = ColorGradeMath.Apply(ur, ug, ub, truth);
            var solution = ColorMatch.Solve(target, reference, current);
            var made = ColorGradeMath.Apply(ur, ug, ub, solution.Values);
            var checkedDistance = Distance(made, reference);
            var kept = solution.Values with { Exposure = current.Exposure, Temperature = current.Temperature, Tint = current.Tint, LiftR = current.LiftR, LiftG = current.LiftG, LiftB = current.LiftB,
                GammaR = current.GammaR, GammaG = current.GammaG, GammaB = current.GammaB, GainR = current.GainR, GainG = current.GainG, GainB = current.GainB };
            expect(solution.Converged && checkedDistance <= ColorMatch.Tolerance && Math.Abs(checkedDistance - solution.DeltaE) < 1e-4 && kept == current
                   && Distance(solution.Ungraded, ungraded) < 0.002 && solution.Iterations <= 5 * ColorMatch.MaxIterations,
                string.Create(CultureInfo.InvariantCulture,
                    $"{what}: target {C(target)} to {C(reference)}: dE {checkedDistance:0.000}, within {ColorMatch.Tolerance}; the target ungraded first; contrast, saturation, vibrance and LUT mix kept"));
        }

        var same = ColorMatch.Solve((0.4, 0.5, 0.6), (0.4, 0.5, 0.6), neutral);
        expect(same.Values == neutral && same.DeltaE < 1e-9 && same.Iterations == 0, "the target already the reference: the card is left as it is, no step taken");
        var farHue = ColorMatch.Solve((0.8, 0.1, 0.1), (0.1, 0.2, 0.9), neutral);
        expect(farHue.Converged == (farHue.DeltaE <= ColorMatch.Tolerance) && farHue.DeltaE < Distance((0.8, 0.1, 0.1), (0.1, 0.2, 0.9)),
            string.Create(CultureInfo.InvariantCulture, $"a red target to a blue reference (far beyond white balance): closer than it was, dE {farHue.DeltaE:0.000}, converged {farHue.Converged}"));

        ReviewMath(expect);
    }

    // ----- 5.6A review (F20, F22, F24): the target ungraded once, the curves in the model, the cards refused -----

    private static double Worst((double R, double G, double B) a, (double R, double G, double B) b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static void ReviewMath(Action<bool, string> expect)
    {
        var neutral = ColorGradeValues.Neutral;

        // F20: the target's colour before its card, kept: a match after an Undo (the card back to neutral) solves from it, where the old
        // flow (the target rewritten as the first match graded it, then ungraded with the card as it is now) lands far from the reference.
        var t0 = (0.40, 0.30, 0.20);
        var (r1, r2) = ((0.55, 0.38, 0.22), (0.30, 0.32, 0.36));
        var first = ColorMatch.SolveFrom(t0, r1, neutral);
        var second = ColorMatch.SolveFrom(t0, r2, neutral);
        var stale = ColorMatch.Solve(ColorGradeMath.Apply(t0.Item1, t0.Item2, t0.Item3, first.Values), r2, neutral);
        var staleOnScreen = ColorGradeMath.Apply(t0.Item1, t0.Item2, t0.Item3, stale.Values);
        expect(first.Converged && second.Converged && Distance(ColorMatch.Graded(t0, second.Values), r2) <= ColorMatch.Tolerance && Distance(staleOnScreen, r2) > 0.05,
            string.Create(CultureInfo.InvariantCulture, $"F20: matched, undone, matched again to a new reference from the target kept ungraded: dE {Distance(ColorMatch.Graded(t0, second.Values), r2):0.000}; the old stale target would leave dE {Distance(staleOnScreen, r2):0.000} while saying Matched ({stale.Converged})"));
        var graded = new ColorGradeValues(25, -10, -0.4, 0.05, 0, -0.03, 0.1, 0.2, -0.1, 0.1, 0, 0.05, 1.15, 0.9, 0.2, 100);
        var shown = ColorGradeMath.Apply(0.62, 0.45, 0.36, graded);
        var whole = ColorMatch.Solve(shown, (0.70, 0.45, 0.30), graded);
        var split = ColorMatch.SolveFrom(ColorMatch.Ungrade(shown, graded), (0.70, 0.45, 0.30), graded);
        expect(whole.Values == split.Values && whole.DeltaE == split.DeltaE && whole.Iterations >= split.Iterations,
            "Solve is Ungrade then SolveFrom: the same values and distance (its iterations count the ungrading too)");

        // F22: the card's curves (each channel's, then the master) are part of the model, looked up as the shader looks up the baked table.
        const string master = "0.25/0.4 0.75/0.85", red = "0.5/0.56", blue = "0.4/0.36";
        var curves = ColorCurveStage.Of(master, red, "", blue);
        var worstTable = 0.0;
        for (var i = 0; i <= 64; i++)
        {
            var x = i / 64.0;
            var (cr, cg, cb) = curves!.Apply((x, x, x));
            var m = (Func<double, double>)(v => HandPegApp.Services.Gpu.GpuColorTables.CurveAt(master, v));
            worstTable = Math.Max(worstTable, Worst((cr, cg, cb), (m(HandPegApp.Services.Gpu.GpuColorTables.CurveAt(red, x)), m(x), m(HandPegApp.Services.Gpu.GpuColorTables.CurveAt(blue, x)))));
        }
        expect(curves is not null && worstTable < 2e-3 && ColorCurveStage.Of("", null, "0/0 1/1", "0.5/0.5") is null,
            string.Create(CultureInfo.InvariantCulture, $"F22: the curves stage is the GPU's table (each channel's curve, then the master; 256 entries, linear between): within {worstTable:0.0e+0} of the curves themselves; identity curves: no stage"));
        var u = (0.42, 0.50, 0.33);
        var curved = ColorMatch.Graded(u, graded, curves);
        var back = ColorMatch.Ungrade(curved, graded, curves);
        var curvesOnly = ColorMatch.Graded(u, neutral, curves);
        expect(Worst(ColorMatch.Graded(back, graded, curves), curved) < 1e-3 && Worst(back, u) < 2e-3
               && Worst(ColorMatch.Ungrade(curvesOnly, neutral, curves), u) < 2e-3 && ColorMatch.Ungrade(curvesOnly, neutral) == curvesOnly,
            string.Create(CultureInfo.InvariantCulture, $"F22: a colour graded and curved is ungraded through both (worst {Worst(back, u):0.0e+0}); a card with only curves too (without them it would be taken as ungraded)"));
        var target = ColorMatch.Graded(u, neutral, curves);
        var reference = ColorMatch.Graded(u, neutral with { Exposure = 0.5, Temperature = 30 }, curves);
        var withCurves = ColorMatch.Solve(target, reference, neutral, curves);
        var blind = ColorMatch.Solve(target, reference, neutral);
        var blindOnScreen = ColorMatch.Graded(ColorMatch.Ungrade(target, neutral, curves), blind.Values, curves);
        expect(withCurves.Converged && Distance(ColorMatch.Graded(u, withCurves.Values, curves), reference) <= ColorMatch.Tolerance && Math.Abs(withCurves.DeltaE - Distance(ColorMatch.Graded(u, withCurves.Values, curves), reference)) < 2e-3
               && Distance(blindOnScreen, reference) > ColorMatch.Tolerance,
            string.Create(CultureInfo.InvariantCulture, $"F22: solved through the curves the picture matches (dE {Distance(ColorMatch.Graded(u, withCurves.Values, curves), reference):0.000}); a model without them would leave dE {Distance(blindOnScreen, reference):0.000} on screen while saying Matched ({blind.Converged})"));

        // F22 and F24: a LUT mixed in, or a keyframed number, refuses the match (short statuses).
        var studio = EffectRegistry.Find(EffectRegistry.ColorStudio)!;
        var plain = studio.NewEffect();
        var lut = studio.NewEffect();
        lut.SetText(ColorGradeKeys.Lut, @"C:\looks\film.cube");
        var lutOff = studio.NewEffect();
        lutOff.SetText(ColorGradeKeys.Lut, @"C:\looks\film.cube");
        lutOff.Set(ColorGradeKeys.LutMix, 0);
        var keyed = studio.NewEffect();
        keyed.SetKeys(ColorGradeKeys.GammaG, [new Keyframe(0, 0), new Keyframe(1, 0.2)]);
        var curvedCard = studio.NewEffect();
        curvedCard.SetText(ColorGradeKeys.CurveMaster, master);
        expect(ColorMatch.CardRefusal(null) is null && ColorMatch.CardRefusal(plain) is null && ColorMatch.CardRefusal(curvedCard) is null && ColorCurveStage.Of(curvedCard) is not null
               && ColorMatch.CardRefusal(lut) == "LUT blocks match" && ColorMatch.CardRefusal(lutOff) is null && ColorMatch.CardRefusal(keyed) == "Grade is keyframed"
               && ColorGradeMath.NumberKeys.Length == 16 && ColorGradeMath.NumberKeys.Distinct().Count() == 16 && ColorGradeMath.NumberKeys.All(k => studio.Param(k) is not null),
            "F22, F24: refused: a LUT mixed in (\"LUT blocks match\"; at 0 % it is not), any of the 16 numbers keyframed (\"Grade is keyframed\"); curves are matched through");

        // G3: Color Match models what the plan draws. The layer's one curve table goes to the first card that asks for it (LookCards):
        // an earlier enabled Curves card that changes the picture takes it, and the grade's curves are left out of the model.
        var tone = EffectRegistry.Find(EffectRegistry.Curves)!;
        var contrast = tone.NewEffect();
        var identity = tone.NewEffect();
        identity.SetText("preset", "none");
        var toneOff = tone.NewEffect();
        toneOff.Enabled = false;
        var pointsOnly = tone.NewEffect();
        pointsOnly.SetText("preset", "none");
        pointsOnly.SetText("master", "0/0 0.5/0.62 1/1");
        expect(!ColorMatch.OwnsCurveTable([contrast, curvedCard], curvedCard) && !ColorMatch.OwnsCurveTable([EffectRegistry.Find(EffectRegistry.Temperature)!.NewEffect(), pointsOnly, curvedCard], curvedCard)
               && ColorMatch.OwnsCurveTable([identity, curvedCard], curvedCard) && ColorMatch.OwnsCurveTable([toneOff, curvedCard], curvedCard)
               && ColorMatch.OwnsCurveTable([curvedCard, contrast], curvedCard) && ColorMatch.OwnsCurveTable([curvedCard], curvedCard),
            "G3: the grade's curves are modelled only while it holds the curve table: not under an enabled Curves card that changes the picture (a preset or its own points); yes under an identity or switched-off one, above one, or alone");
        var off = studio.NewEffect();
        off.Enabled = false;
        expect(ColorMatch.DrawnRefusal(off, false) == "Grade is off" && ColorMatch.DrawnRefusal(plain, true) == "Grade is off" && ColorMatch.DrawnRefusal(null, true) == "Grade is off"
               && ColorMatch.DrawnRefusal(plain, false) is null && ColorMatch.DrawnRefusal(null, false) is null,
            "G3: a grade not drawn refuses (\"Grade is off\"): its card switched off, or its stack bypassed (with or without a card); an enabled card, or none, on a live stack may match");
    }

    // ----- color-match-bar (5.6A review: F20-F27) -----

    private static void MatchBar(Action<bool, string> expect)
    {
        // F21: a release on the Reference button itself (a plain click) cancels; one off it samples.
        var size = new Size(96, 26);
        expect(ScreenEyedropper.ReleasedOnItself(size, new Point(40, 12)) && ScreenEyedropper.ReleasedOnItself(size, new Point(0, 0))
               && !ScreenEyedropper.ReleasedOnItself(size, new Point(-1, 12)) && !ScreenEyedropper.ReleasedOnItself(size, new Point(40, 30)) && !ScreenEyedropper.ReleasedOnItself(size, new Point(300, -200)),
            "F21: a release on the Reference button (a click) cancels, with no match; released anywhere off it, the colour there is the reference");
        var dropper = new ScreenEyedropper();
        var raised = 0;
        dropper.DraggingChanged += _ => raised++;
        dropper.Cancel();
        var eyedropper = SourceProbe.Src("Controls", "ScreenEyedropper.cs");
        expect(raised == 0 && !dropper.IsDragging && SourceProbe.Member(eyedropper, "protected override void OnMouseLeftButtonUp(").Contains("if (ReleasedOnItself(RenderSize, e.GetPosition(this)))", StringComparison.Ordinal),
            "F21, F26: the release checks the button's own bounds first; Cancel with no drag under way does nothing");

        // F23: the target clip on screen at the playhead (its span, shown); the blur fill always; a Duration of 0 to the end.
        var clip = new Layer { StartTime = 2, Duration = 3 };
        var hidden = new Layer { StartTime = 0, Duration = 10, IsHidden = true };
        var open = new Layer { StartTime = 1, Duration = 0 };
        var fill = new Layer { Kind = LayerKind.Background, IsHidden = true };
        expect(!ColorMatch.ShownAt(clip, 1.9) && ColorMatch.ShownAt(clip, 2) && ColorMatch.ShownAt(clip, 4.99) && !ColorMatch.ShownAt(clip, 5)
               && !ColorMatch.ShownAt(hidden, 5) && ColorMatch.ShownAt(open, 600) && !ColorMatch.ShownAt(open, 0.5) && ColorMatch.ShownAt(fill, 0),
            "F23: a clip is on screen from its start to its end (not hidden; a Duration of 0 runs to the end), the blur fill always");

        var bar = SourceProbe.Src("MainWindow.ColorMatch.cs");
        var refusal = SourceProbe.Member(bar, "private string? Refusal()");
        expect(refusal.Contains("if (target.Layer is { } layer && !ColorMatch.ShownAt(layer, viewModel.PositionMs / 1000))", StringComparison.Ordinal)
               && refusal.Contains("return \"Target not shown\";", StringComparison.Ordinal) && refusal.Contains("return ColorMatch.DrawnRefusal(card, Bypassed(target)) ?? ColorMatch.CardRefusal(card);", StringComparison.Ordinal),
            "F23, F22, F24, G3: Pick Target refuses a target off screen at the playhead (\"Target not shown\"), a grade not drawn, a card with a LUT mixed in or a keyframed value");
        expect(bar.Contains("private bool Bypassed(ColorGradeTarget target) => target.Layer?.EffectsBypassed ?? _window._viewModel.SequenceEffectsBypassed;", StringComparison.Ordinal),
            "G3: a stack is bypassed by its clip's (or the blur fill's) Bypass All, the Sequence Output's by the Sequence's");

        // F20: the grade the frame was read under, kept with it; the click ungrades the colour once under it; Match solves from it.
        var pick = SourceProbe.Member(bar, "private async Task PickTargetAsync()");
        var press = SourceProbe.Member(bar, "private void Overlay_PreviewMouseLeftButtonDown(");
        var match = SourceProbe.Member(bar, "private void Match()");
        expect(pick.IndexOf("var shot = ShotOf(_target());", StringComparison.Ordinal) is > 0 and var shotAt && shotAt < pick.IndexOf("CaptureFrameAsync", StringComparison.Ordinal)
               && pick.Contains("if (ShotOf(_target()) != shot)", StringComparison.Ordinal) && pick.Contains("BeginPick(read, shot);", StringComparison.Ordinal)
               && press.Contains("var ungraded = ColorMatch.Ungrade(picked, shot.Values, shot.Curves);", StringComparison.Ordinal) && press.Contains("if (ShotOf(_target()) != shot)", StringComparison.Ordinal)
               && match.Contains("ColorMatch.SolveFrom(ungraded, reference, current, curves);", StringComparison.Ordinal) && !match.Contains("ColorMatch.Solve(", StringComparison.Ordinal)
               && !bar.Contains("_targetColor", StringComparison.Ordinal),
            "F20: the grade is noted before the frame is read (and again after, and at the click); the target's colour is ungraded once under it; Match solves from that colour, never a stale graded one");
        expect(match.Contains("if ((ColorMatch.DrawnRefusal(card, Bypassed(target)) ?? ColorMatch.CardRefusal(card)) is { } refused)", StringComparison.Ordinal)
               && match.Contains("var curves = card is not null && viewModel.ColorGradeStack(target) is { } stack && ColorMatch.OwnsCurveTable(stack, card) ? ColorCurveStage.Of(card) : null;", StringComparison.Ordinal)
               && match.IndexOf("CardRefusal(card)", StringComparison.Ordinal) < match.IndexOf("ApplyColorGrade(", StringComparison.Ordinal),
            "F22, F24, G3: Match checks the card again before it writes (a grade not drawn, a LUT or keys refuse) and solves through its curves while it holds the curve table");
        var shotOf = SourceProbe.Member(bar, "private GradeShot ShotOf(");
        expect(shotOf.Contains("if (viewModel.ColorGradeStack(target) is { } stack && !ColorMatch.OwnsCurveTable(stack, card))", StringComparison.Ordinal)
               && shotOf.Contains("return new(target, ColorGradeValues.From(card), \"\", \"\", \"\", \"\");", StringComparison.Ordinal),
            "G3: the grade the frame is read under leaves out the curves a Curves card above took (the table changing hands is another shot)");
        var plan = SourceProbe.Src("Services", "Gpu", "GpuScenePlan.Create.cs");
        var lookCards = SourceProbe.Member(plan, "private EffectInstance[] LookCards(");
        var toneAt = lookCards.IndexOf("definition.GpuSlot == GpuShaderSlot.ToneCurve && !TakeTable(ref entry.Curve, () => resources.Curve(card.GetText(\"preset\"), card.GetText(\"master\"))))", StringComparison.Ordinal);
        expect(lookCards.Contains("if (!card.Enabled || card.Definition is not { Zone: EffectZone.Look } definition", StringComparison.Ordinal) && toneAt > 0
               && toneAt < lookCards.IndexOf("TakeTable(ref entry.Curve, () => resources.ColorCurves(", StringComparison.Ordinal)
               && SourceProbe.Member(SourceProbe.Src("Services", "Gpu", "GpuResourceBroker.cs"), "public GpuResourceRef Curve(").Contains("if (GpuColorTables.ToneCurve(preset, master) is not { } table)", StringComparison.Ordinal),
            "G3: OwnsCurveTable follows the plan: cards in order, a switched-off one skipped, a Curves card takes the table only when GpuColorTables.ToneCurve has one");

        // F25: the pick ends on an edit, Undo or a grade moved (PreviewStateInvalidated), on Interactive Crop or a Draw target, and a press or a
        // move in a view that refuses (the 3D view) ends it and is left to that view.
        var begin = SourceProbe.Member(bar, "private void BeginPick(");
        var end = SourceProbe.Member(bar, "private void EndPick()");
        var changed = SourceProbe.Member(bar, "private void ViewModel_PropertyChanged(");
        var move = SourceProbe.Member(bar, "private void Overlay_PreviewMouseMove(");
        expect(begin.Contains("_window._viewModel.PreviewStateInvalidated += Picture_Invalidated;", StringComparison.Ordinal) && end.Contains("_window._viewModel.PreviewStateInvalidated -= Picture_Invalidated;", StringComparison.Ordinal)
               && SourceProbe.Member(bar, "private void Picture_Invalidated()").Contains("Status(\"Pick the target again\");", StringComparison.Ordinal)
               && changed.Contains("nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetLayer) => true", StringComparison.Ordinal),
            "F25: an edit, Undo, a grade moved (the preview state invalidated), Interactive Crop or a Draw target end the pick (\"Pick the target again\")");
        expect(press.IndexOf("if (EndedByRefusal())", StringComparison.Ordinal) is > 0 and var refusedAt && refusedAt < press.IndexOf("e.Handled = true;", StringComparison.Ordinal)
               && move.Contains("if (_frame is not null && EndedByRefusal())", StringComparison.Ordinal)
               && SourceProbe.Member(bar, "private bool EndedByRefusal()").Contains("EndPick();", StringComparison.Ordinal),
            "F25: a press or a move asks Refusal first: the 3D view or a source view ends the pick, the press left unhandled for that view");

        // F26: Esc during a Reference drag is the window's first Esc (before the dial, the 3D view and Interactive Crop).
        var keys = SourceProbe.Member(SourceProbe.Src("MainWindow.Keybinds.cs"), "private bool TakeOverlayKey(");
        var escape = keys.IndexOf("if (key == Key.Escape)", StringComparison.Ordinal);
        var dropperAt = keys.IndexOf("if (Mouse.Captured is ScreenEyedropper { IsDragging: true } dropper)", StringComparison.Ordinal);
        expect(escape > 0 && dropperAt > escape && dropperAt < keys.IndexOf("EscapeTakerFor(", StringComparison.Ordinal) && keys.Contains("dropper.Cancel();", StringComparison.Ordinal),
            "F26: TakeOverlayKey's Esc cancels a Reference drag first, before EscapeTakerFor (the 3D view's and Interactive Crop's Esc come later)");

        // F27: the loupe keeps its size (UiScale scales its content only) and is as much taller than wide as its swatch row in real pixels.
        var loupe = SourceProbe.Src("Controls", "ScreenEyedropper.Loupe.cs");
        var place = SourceProbe.Member(loupe, "private void Place(int x, int y)");
        expect(loupe.Contains("UiScale.SetKeepsItsSize(this, true);", StringComparison.Ordinal) && place.Contains("var content = VisualTreeHelper.GetDpi(this).DpiScaleX;", StringComparison.Ordinal)
               && place.Contains("width + (int)Math.Round(SwatchHeight * (content > 0 ? content : scale))", StringComparison.Ordinal),
            "F27: the screen loupe keeps its size and squares its picture row in physical pixels (the swatch row at the content's pixels per DIP)");
    }
}
