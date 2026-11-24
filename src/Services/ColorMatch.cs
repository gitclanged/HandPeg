using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>Ottosson OKLab colour: perceptual lightness L (0-1) and opponent axes a, b.</summary>
public readonly record struct OkLab(double L, double A, double B);

/// <summary>
/// Color Match result: new card values, residual OKLab dE to the reference, the ungraded target colour, whether dE is within
/// <see cref="ColorMatch.Tolerance"/>, and iteration count.
/// </summary>
public readonly record struct ColorMatchSolution(ColorGradeValues Values, double DeltaE, (double R, double G, double B) Ungraded, bool Converged, int Iterations);

/// <summary>
/// Pure Color Match math: OKLab conversion, robust bilateral patch sampling (outlier- and edge-resistant), and a solver fitting Color
/// Grade values that map the target to the reference using the card's own model (<see cref="ColorGradeMath.Apply"/>, identical to the
/// shader). Colours are display-referred sRGB 0..1 (HDR sources are their SDR-mapped 8-bit frames).
/// Stages: ungrade the target (numerically invert the current grade), solve exposure for lightness, then exposure + temperature + tint
/// for colour, then assign the remainder to the target's tone-zone wheel (Lift/Gamma/Gain). Each stage is Levenberg-Marquardt with at
/// most <see cref="MaxIterations"/> steps, verified with the forward model.
/// </summary>
public static class ColorMatch
{
    /// <summary>Patch size ((2r + 1)^2 px), bilateral spatial/range sigmas, and the edge-rejection threshold (OKLab dE).</summary>
    public const int PatchRadius = 7;

    public const double SpatialSigma = 4, RangeSigma = 0.05, EdgeReject = 0.12;

    /// <summary>The distance (OKLab dE) a match is within; and the one a stage stops at, early.</summary>
    public const double Tolerance = 0.01, StopAt = 0.002;

    /// <summary>The most steps each stage of the solver takes.</summary>
    public const int MaxIterations = 20;

    // The wheels that take a remainder beyond the tone-zone wheel, in order: Gamma, Lift, Gain.
    private static readonly int[] WheelOrder = [1, 0, 2];

    // Lazy sRGB-to-linear LUT for 256 levels (the screen eyedropper samples at 60 Hz).
    private static double[]? _linear;

    private static double[] Linear => _linear ??= [.. Enumerable.Range(0, 256).Select(i => ColorGradeMath.SrgbToLinear(i / 255.0))];

    /// <summary>An sRGB colour (0..1) in OKLab.</summary>
    public static OkLab ToOkLab(double r, double g, double b) =>
        LinearToOkLab(ColorGradeMath.SrgbToLinear(r), ColorGradeMath.SrgbToLinear(g), ColorGradeMath.SrgbToLinear(b));

    /// <summary>An sRGB colour as a tuple, in OKLab.</summary>
    public static OkLab ToOkLab((double R, double G, double B) c) => ToOkLab(c.R, c.G, c.B);

    /// <summary>Linear-light sRGB in OKLab (Ottosson's matrices).</summary>
    public static OkLab LinearToOkLab(double r, double g, double b)
    {
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new OkLab(
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>An OKLab colour in sRGB (0..1, kept within it).</summary>
    public static (double R, double G, double B) FromOkLab(OkLab lab)
    {
        var l = Cube(lab.L + 0.3963377774 * lab.A + 0.2158037573 * lab.B);
        var m = Cube(lab.L - 0.1055613458 * lab.A - 0.0638541728 * lab.B);
        var s = Cube(lab.L - 0.0894841775 * lab.A - 1.2914855480 * lab.B);
        var r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        return (Clamp01(ColorGradeMath.LinearToSrgb(Math.Max(r, 0))), Clamp01(ColorGradeMath.LinearToSrgb(Math.Max(g, 0))), Clamp01(ColorGradeMath.LinearToSrgb(Math.Max(b, 0))));

        static double Cube(double x) => x * x * x;
    }

    /// <summary>The distance between two colours in OKLab (dE_OK: about 0.02 is a just-noticeable difference).</summary>
    public static double DeltaE(OkLab a, OkLab b)
    {
        var (dl, da, db) = (a.L - b.L, a.A - b.A, a.B - b.B);
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    // A BGRA8 pixel in OKLab (its alpha never read).
    private static OkLab PixelLab(ReadOnlySpan<byte> bgra, int at)
    {
        var table = Linear;
        return LinearToOkLab(table[bgra[at + 2]], table[bgra[at + 1]], table[bgra[at]]);
    }

    /// <summary>
    /// Robust colour pick from a BGRA8 frame (alpha ignored): OKLab bilateral mean of the patch, weighted by spatial distance and by colour
    /// distance from the 3 x 3 median; pixels beyond <see cref="EdgeReject"/> are excluded. Patch clipped to frame bounds. Null for an
    /// empty frame or out-of-bounds point. Allocation-free.
    /// </summary>
    public static (double R, double G, double B)? BilateralMean(ReadOnlySpan<byte> bgra, int width, int height, int x, int y, int radius = PatchRadius)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4 || x < 0 || y < 0 || x >= width || y >= height)
            return null;

        // Reference colour: per-channel 3 x 3 median (rejects a single stray pixel).
        Span<double> ls = stackalloc double[9], as_ = stackalloc double[9], bs = stackalloc double[9];
        var n = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var (px, py) = (x + dx, y + dy);
                if (px < 0 || py < 0 || px >= width || py >= height)
                    continue;
                var lab = PixelLab(bgra, (py * width + px) * 4);
                (ls[n], as_[n], bs[n]) = (lab.L, lab.A, lab.B);
                n++;
            }
        }

        var centre = new OkLab(Median(ls[..n]), Median(as_[..n]), Median(bs[..n]));
        double sumL = 0, sumA = 0, sumB = 0, weights = 0;
        var (x0, x1, y0, y1) = (Math.Max(x - radius, 0), Math.Min(x + radius, width - 1), Math.Max(y - radius, 0), Math.Min(y + radius, height - 1));
        for (var py = y0; py <= y1; py++)
        {
            for (var px = x0; px <= x1; px++)
            {
                var lab = PixelLab(bgra, (py * width + px) * 4);
                var distance = DeltaE(lab, centre);
                if (distance > EdgeReject)
                    continue;
                var (dx, dy) = (px - x, py - y);
                var weight = Math.Exp(-(dx * dx + dy * dy) / (2 * SpatialSigma * SpatialSigma)) * Math.Exp(-distance * distance / (2 * RangeSigma * RangeSigma));
                (sumL, sumA, sumB, weights) = (sumL + weight * lab.L, sumA + weight * lab.A, sumB + weight * lab.B, weights + weight);
            }
        }

        return weights > 0 ? FromOkLab(new OkLab(sumL / weights, sumA / weights, sumB / weights)) : FromOkLab(centre);
    }

    private static double Median(Span<double> values)
    {
        values.Sort();
        return values.Length == 0 ? 0 : values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }

    /// <summary>
    /// Forward model: parametric grade (<see cref="ColorGradeMath.Apply"/>) then curves (<see cref="ColorCurveStage"/>), as PSColorStudio.
    /// LUTs are not modelled (Color Match refuses them).
    /// </summary>
    public static (double R, double G, double B) Graded((double R, double G, double B) ungraded, in ColorGradeValues values, ColorCurveStage? curves = null)
    {
        var graded = ColorGradeMath.Apply(ungraded.R, ungraded.G, ungraded.B, values);
        return curves is null ? graded : curves.Apply(graded);
    }

    /// <summary>
    /// Numerically inverts <see cref="Graded"/> (grade plus optional <paramref name="curves"/>) by Gauss-Newton from the graded colour (max
    /// <see cref="MaxIterations"/>, clamped 0..1). Clipped values return one valid preimage; a neutral grade returns the input.
    /// </summary>
    public static (double R, double G, double B) Ungrade((double R, double G, double B) graded, in ColorGradeValues current, ColorCurveStage? curves = null) =>
        Ungrade(graded, current, curves, out _);

    private static (double R, double G, double B) Ungrade((double R, double G, double B) graded, ColorGradeValues current, ColorCurveStage? curves, out int iterations)
    {
        iterations = 0;
        if (current.IsNeutral && curves is null)
            return graded;

        Span<double> u = [graded.R, graded.G, graded.B];
        Span<double> low = [0, 0, 0], high = [1, 1, 1];
        (double, double, double) Residual(ReadOnlySpan<double> q)
        {
            var (r, g, b) = Graded((q[0], q[1], q[2]), current, curves);
            return (r - graded.R, g - graded.G, b - graded.B);
        }

        iterations = Fit(u, low, high, Residual, 1e-12);
        return (u[0], u[1], u[2]);
    }

    /// <summary>
    /// Solves card values mapping the sampled <paramref name="target"/> (under <paramref name="current"/> and <paramref name="curves"/>) to
    /// <paramref name="reference"/>: <see cref="Ungrade"/> then <see cref="SolveFrom"/>.
    /// </summary>
    public static ColorMatchSolution Solve((double R, double G, double B) target, (double R, double G, double B) reference, in ColorGradeValues current, ColorCurveStage? curves = null)
    {
        var ungraded = Ungrade(target, current, curves, out var iterations);
        var solution = SolveFrom(ungraded, reference, current, curves);
        return solution with { Iterations = solution.Iterations + iterations };
    }

    /// <summary>
    /// Solves from the <paramref name="ungraded"/> target to <paramref name="reference"/> starting at <paramref name="current"/> (curves kept):
    /// exposure, then exposure + temperature + tint, then the tone-zone wheel; other values unchanged. A white balance pinned at its limit is
    /// compared against a wheel-only solution after exposure and replaced when that matches. Pass 5.6.4B: the solution keeps to natural
    /// bounds (<see cref="ColorMatchBounds"/>: Temperature and Tint within 0.75 of their reach, each wheel's push within 0.75 of the radius).
    /// A free fit inside them is kept exactly as found; one beyond them is fitted again within them (the white balance bounded, every wheel's
    /// push projected into its disc, the other wheels taking a remainder beyond <see cref="Tolerance"/>) and regularized
    /// (<see cref="ColorMatchBounds.Regularize"/>), so a match never paints a cast past what a colourist would. Verified with the full forward
    /// model (<see cref="ColorMatchSolution.DeltaE"/>).
    /// </summary>
    public static ColorMatchSolution SolveFrom((double R, double G, double B) ungraded, (double R, double G, double B) reference, in ColorGradeValues current, ColorCurveStage? curves = null)
    {
        var free = SolveWithin(ungraded, reference, current, curves, bounded: false);
        if (ColorMatchBounds.Within(free.Values))
            return free;
        var held = SolveWithin(ungraded, reference, current, curves, bounded: true);
        return held with { Iterations = free.Iterations + held.Iterations };
    }

    // The staged fit, free (each value within its own range) or held within the natural bounds.
    private static ColorMatchSolution SolveWithin((double R, double G, double B) ungraded, (double R, double G, double B) reference, in ColorGradeValues current,
        ColorCurveStage? curves, bool bounded)
    {
        // White balance scaled to -1..1 per 100: its whole range free, 0.75 of it held.
        var balanceBound = bounded ? ColorMatchBounds.WheelLimit : 1;
        var iterations = 0;
        var goal = ToOkLab(reference);
        double Distance(in ColorGradeValues v) => DeltaE(ToOkLab(Graded(ungraded, v, curves)), goal);

        (double, double, double) LabResidual(in ColorGradeValues v)
        {
            var lab = ToOkLab(Graded(ungraded, v, curves));
            return (lab.L - goal.L, lab.A - goal.A, lab.B - goal.B);
        }

        // 1. Exposure alone, for the lightness (EV, -3..3).
        ColorGradeValues Exposure(ColorGradeValues start)
        {
            if (Distance(start) <= StopAt)
                return start;
            double[] q = [start.Exposure];
            iterations += Fit(q, [-3], [3], p =>
            {
                var (dl, _, _) = LabResidual(start with { Exposure = p[0] });
                return (dl, 0, 0);
            }, StopAt * StopAt);
            return start with { Exposure = q[0] };
        }

        // 2. Joint exposure/temperature/tint fit (temperature and tint scaled to -1..1 for comparable step sizes, held within the bound).
        ColorGradeValues Balance(ColorGradeValues start)
        {
            if (Distance(start) <= StopAt)
                return start;
            double[] q = [start.Exposure, start.Temperature / 100, start.Tint / 100];
            iterations += Fit(q, [-3, -balanceBound, -balanceBound], [3, balanceBound, balanceBound],
                p => LabResidual(start with { Exposure = p[0], Temperature = p[1] * 100, Tint = p[2] * 100 }), StopAt * StopAt);
            return start with { Exposure = q[0], Temperature = q[1] * 100, Tint = q[2] * 100 };
        }

        // 3. Residual to a wheel (Lift 0, Gamma 1, Gain 2), its RGB fitted; held, the push is projected into the bound's disc (the puck never
        // past 0.75 of the rim, its master kept; an identity inside it). Kept only if it reduces dE.
        ColorGradeValues Wheel(ColorGradeValues start, int zone)
        {
            if (Distance(start) <= StopAt)
                return start;
            double[] q = zone switch
            {
                0 => [start.LiftR, start.LiftG, start.LiftB],
                2 => [start.GainR, start.GainG, start.GainB],
                _ => [start.GammaR, start.GammaG, start.GammaB],
            };
            ColorGradeValues With(ReadOnlySpan<double> p)
            {
                var (r, g, b) = bounded ? ColorMatchBounds.Wheel((p[0], p[1], p[2])) : (p[0], p[1], p[2]);
                return zone switch
                {
                    0 => start with { LiftR = r, LiftG = g, LiftB = b },
                    2 => start with { GainR = r, GainG = g, GainB = b },
                    _ => start with { GammaR = r, GammaG = g, GammaB = b },
                };
            }

            iterations += Fit(q, [-1, -1, -1], [1, 1, 1], p => LabResidual(With(p)), StopAt * StopAt);
            var trial = With(q);
            return Distance(trial) < Distance(start) ? trial : start;
        }

        // The tone zone by ungraded luma: shadows to Lift, midtones to Gamma, highlights to Gain.
        var luma = ColorGradeMath.LumaR * ungraded.R + ColorGradeMath.LumaG * ungraded.G + ColorGradeMath.LumaB * ungraded.B;
        var zone = luma < 1 / 3.0 ? 0 : luma > 2 / 3.0 ? 2 : 1;
        // Held, the fit starts from the start itself regularized: no stage stops early on a grade the final clamp would then move.
        var exposed = Exposure(bounded ? ColorMatchBounds.Regularize(current) : current);
        var balanced = Balance(exposed);
        var values = Wheel(balanced, zone);

        // A temperature or tint at its limit (or bound): the wheel alone after the exposure, kept when it matches (or comes closer).
        var pinned = balanceBound * 100 - 0.5;
        if (Math.Abs(balanced.Temperature) >= pinned || Math.Abs(balanced.Tint) >= pinned)
        {
            var wheel = Wheel(exposed, zone);
            if (Distance(wheel) <= Tolerance || Distance(wheel) < Distance(values))
                values = wheel;
        }

        if (bounded)
        {
            // Still beyond the tolerance with the tone-zone wheel at its bound: the other wheels take the remainder, each within its own.
            foreach (var other in WheelOrder)
            {
                if (other != zone && Distance(values) > Tolerance)
                    values = Wheel(values, other);
            }

            // The bounds hold whatever the start (a card set past them by hand included); the distance reported is the regularized grade's.
            values = ColorMatchBounds.Regularize(values);
        }

        var left = Distance(values);
        return new ColorMatchSolution(values, left, ungraded, left <= Tolerance, iterations);
    }

    // ----- Refusal rules (pure, golden-tested) -----

    /// <summary>
    /// Reason the card cannot be matched (null if it can): a mixed-in LUT (not modelled) or a keyframed value (a match would write static
    /// values hidden by the keys).
    /// </summary>
    public static string? CardRefusal(EffectInstance? card) =>
        card is null ? null : ColorGradeMath.LutMixedIn(card) ? "LUT blocks match" : ColorGradeMath.IsKeyframed(card) ? "Grade is keyframed" : null;

    /// <summary>
    /// Reason the grade is not rendered (null if it is): card disabled or stack bypassed (clip or Sequence Output Bypass All), matching the
    /// plan's Look/LookCards skips; picking would ungrade with wrong values and matching would write into an inactive card. Allocation-free
    /// (called on every pick move).
    /// </summary>
    public static string? DrawnRefusal(EffectInstance? card, bool bypassed) =>
        bypassed || card is { Enabled: false } ? "Grade is off" : null;

    /// <summary>
    /// True when the grade's curves are rendered: a layer has one curve table, assigned by LookCards to the first requesting card, so an
    /// earlier active non-identity Curves card takes it (PSColorStudio then skips the grade's curves). Identity, disabled or later Curves cards do not.
    /// </summary>
    public static bool OwnsCurveTable(IList<EffectInstance> stack, EffectInstance card)
    {
        for (var i = 0; i < stack.Count; i++)
        {
            var earlier = stack[i];
            if (ReferenceEquals(earlier, card))
                return true;
            if (earlier.Enabled && earlier.Definition is { Zone: EffectZone.Look, GpuSlot: GpuShaderSlot.ToneCurve }
                && GpuColorTables.ToneCurve(earlier.GetText("preset"), earlier.GetText("master")) is not null)
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when a clip (or blur fill, always) is visible at a Sequence time: shown, not binned, within [start, end) (Duration 0 runs to the end).
    /// </summary>
    public static bool ShownAt(Layer layer, double seconds) =>
        layer.IsBackground || !layer.IsHidden && !layer.IsDeleted && seconds >= layer.StartTime - 0.0005 && (layer.Duration <= 0 || seconds < layer.StartTime + layer.Duration);

    private delegate (double, double, double) ResidualOf(ReadOnlySpan<double> parameters);

    /// <summary>
    /// Bounded Levenberg-Marquardt for up to three parameters against a 3-component residual: finite-difference Jacobian, max
    /// <see cref="MaxIterations"/> steps, stops when squared residual &lt; <paramref name="stop"/>. Returns steps taken; parameters hold the best fit.
    /// </summary>
    private static int Fit(Span<double> q, ReadOnlySpan<double> low, ReadOnlySpan<double> high, ResidualOf residual, double stop)
    {
        var n = q.Length;
        Span<double> jacobian = stackalloc double[9], trial = stackalloc double[3], step = stackalloc double[3];
        Span<double> normal = stackalloc double[9], gradient = stackalloc double[3];
        var (r0, r1, r2) = residual(q);
        var cost = r0 * r0 + r1 * r1 + r2 * r2;
        var damping = 1e-3;
        var steps = 0;
        while (steps < MaxIterations && cost > stop)
        {
            steps++;

            // The Jacobian, column by column, a small step inside the bounds (backwards at the upper one).
            for (var j = 0; j < n; j++)
            {
                q.CopyTo(trial);
                var h = 1e-5 * Math.Max(1, Math.Abs(q[j]));
                if (trial[j] + h > high[j])
                    h = -h;
                trial[j] += h;
                var (a, b, c) = residual(trial[..n]);
                (jacobian[j], jacobian[3 + j], jacobian[6 + j]) = ((a - r0) / h, (b - r1) / h, (c - r2) / h);
            }

            // The normal equations, J'J (damped on its diagonal) and J'r.
            for (var i = 0; i < n; i++)
            {
                gradient[i] = jacobian[i] * r0 + jacobian[3 + i] * r1 + jacobian[6 + i] * r2;
                for (var j = 0; j < n; j++)
                    normal[i * 3 + j] = jacobian[i] * jacobian[j] + jacobian[3 + i] * jacobian[3 + j] + jacobian[6 + i] * jacobian[6 + j];
            }

            var improved = false;
            for (var attempt = 0; attempt < 8 && !improved; attempt++)
            {
                if (!SolveDamped(normal, gradient, damping, n, step))
                {
                    damping *= 10;
                    continue;
                }

                for (var i = 0; i < n; i++)
                    trial[i] = Math.Max(low[i], Math.Min(q[i] - step[i], high[i]));
                var (a, b, c) = residual(trial[..n]);
                var trialCost = a * a + b * b + c * c;
                if (trialCost < cost)
                {
                    trial[..n].CopyTo(q);
                    (r0, r1, r2, cost) = (a, b, c, trialCost);
                    damping = Math.Max(damping / 3, 1e-9);
                    improved = true;
                }
                else
                {
                    damping *= 4;
                }
            }

            // No step down from here (a bound or a flat spot): the best found stands.
            if (!improved)
                break;
        }

        return steps;
    }

    // (J'J + damping diag) step = J'r, by Gaussian elimination with partial pivoting; false when it is singular.
    private static bool SolveDamped(ReadOnlySpan<double> normal, ReadOnlySpan<double> gradient, double damping, int n, Span<double> step)
    {
        Span<double> m = stackalloc double[12];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
                m[i * 4 + j] = normal[i * 3 + j] + (i == j ? damping * Math.Max(normal[i * 3 + i], 1e-9) : 0);
            m[i * 4 + 3] = gradient[i];
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
            {
                if (Math.Abs(m[row * 4 + col]) > Math.Abs(m[pivot * 4 + col]))
                    pivot = row;
            }

            if (Math.Abs(m[pivot * 4 + col]) < 1e-15)
                return false;
            if (pivot != col)
            {
                for (var k = 0; k < 4; k++)
                    (m[col * 4 + k], m[pivot * 4 + k]) = (m[pivot * 4 + k], m[col * 4 + k]);
            }

            for (var row = 0; row < n; row++)
            {
                if (row == col)
                    continue;
                var factor = m[row * 4 + col] / m[col * 4 + col];
                for (var k = col; k < 4; k++)
                    m[row * 4 + k] -= factor * m[col * 4 + k];
            }
        }

        for (var i = 0; i < n; i++)
            step[i] = m[i * 4 + 3] / m[i * 4 + i];
        return true;
    }

    private static double Clamp01(double c) => c < 0 ? 0 : c > 1 ? 1 : c;
}

/// <summary>
/// Curve stage model matching PSColorStudio after the parametric grade: per-channel curve then master, sampled from the baked
/// <see cref="GpuColorTables.ColorCurves"/> table (256 entries, linear interpolation). Built once per match; allocation-free lookups.
/// </summary>
public sealed class ColorCurveStage
{
    private const int Entries = 256;

    // R, G, B, A (the master alone) per entry, as the GPU's table.
    private readonly float[] _table;

    private ColorCurveStage(float[] table) => _table = table;

    /// <summary>The stage of four curves ("x/y x/y..."); null when every one is the identity (nothing to model).</summary>
    public static ColorCurveStage? Of(string? master, string? red, string? green, string? blue)
    {
        if (GpuColorTables.ColorCurves(master, red, green, blue) is not { } bytes)
            return null;

        var table = new float[Entries * 4];
        Buffer.BlockCopy(bytes, 0, table, 0, Math.Min(bytes.Length, table.Length * sizeof(float)));
        return new ColorCurveStage(table);
    }

    /// <summary>The curves of a Color Grade card; null when it has none that change the picture.</summary>
    public static ColorCurveStage? Of(EffectInstance card) =>
        Of(card.GetText(ColorGradeKeys.CurveMaster), card.GetText(ColorGradeKeys.CurveRed), card.GetText(ColorGradeKeys.CurveGreen), card.GetText(ColorGradeKeys.CurveBlue));

    /// <summary>Applies the curves like the shader (input clamped, output clamped to 0..1).</summary>
    public (double R, double G, double B) Apply((double R, double G, double B) c) => (At(c.R, 0), At(c.G, 1), At(c.B, 2));

    // The shader's lookup: texel coordinate v * 255, linear between the two entries about it (a clamped sampler).
    private double At(double v, int channel)
    {
        var x = Math.Max(0, Math.Min(v, 1)) * (Entries - 1);
        var below = Math.Min((int)x, Entries - 2);
        var share = x - below;
        var value = _table[below * 4 + channel] * (1 - share) + _table[(below + 1) * 4 + channel] * share;
        return Math.Max(0, Math.Min(value, 1));
    }
}
