
namespace HandPegApp.Models;

/// <summary>
/// Interpolation curve for the segment leaving a keyframe (progress 0-1): closed form where one expression suffices, otherwise
/// piecewise linear (see <see cref="EasingCurves"/>), keeping motion a flat sum of clamped ramps without nested conditionals.
/// </summary>
/// <remarks>Saved by number in projects: new easings are added at the end.</remarks>
public enum EasingType
{
    /// <summary>At one speed all the way.</summary>
    Linear,

    /// <summary>Starts slowly, ends slowly: half a cosine wave.</summary>
    EaseInOut,

    /// <summary>Starts slowly and arrives at full speed.</summary>
    EaseIn,

    /// <summary>Leaves at full speed and settles slowly.</summary>
    EaseOut,

    /// <summary>Starts and ends slowly, a little more gently than Ease In-Out: 3u² - 2u³.</summary>
    Smoothstep,

    /// <summary>Stays at its value and jumps to the next keyframe's when that comes: a step.</summary>
    Hold,

    /// <summary>Runs a little past the next value and comes back to it (overshoot).</summary>
    Back,

    /// <summary>Springs past the next value and back a few times, settling on it.</summary>
    Elastic,

    /// <summary>Drops onto the next value and bounces on it, smaller each time.</summary>
    Bounce,

    /// <summary>A curve of its own: a cubic Bézier through (0, 0) and (1, 1) with the keyframe's two handles.</summary>
    CubicBezier,
}

/// <summary>An easing as the Keyframes pane's drop-down offers it.</summary>
public sealed record EasingChoice(EasingType Type, string Name)
{
    public static IReadOnlyList<EasingChoice> All { get; } =
    [
        new(EasingType.Linear, "Linear"),
        new(EasingType.EaseInOut, "Ease In-Out"),
        new(EasingType.EaseIn, "Ease In"),
        new(EasingType.EaseOut, "Ease Out"),
        new(EasingType.Smoothstep, "Smoothstep"),
        new(EasingType.Hold, "Hold (Step)"),
        new(EasingType.Back, "Overshoot (Back)"),
        new(EasingType.Elastic, "Elastic"),
        new(EasingType.Bounce, "Bounce"),
        new(EasingType.CubicBezier, "Custom Bézier"),
    ];

    public override string ToString() => Name;
}

/// <summary>A cubic Bézier's two handles, as CSS writes them: cubic-bezier(x1, y1, x2, y2).</summary>
public sealed record BezierPreset(string Name, double X1, double Y1, double X2, double Y2)
{
    /// <summary>The curves the Custom Bézier box offers to start from.</summary>
    public static IReadOnlyList<BezierPreset> All { get; } =
    [
        new("Ease", 0.25, 0.1, 0.25, 1),
        new("Ease In", 0.42, 0, 1, 1),
        new("Ease Out", 0, 0, 0.58, 1),
        new("Ease In-Out", 0.42, 0, 0.58, 1),
        new("Snap", 0.7, 0, 0.3, 1),
        new("Anticipate", 0.36, -0.4, 0.6, 1),
        new("Overshoot", 0.3, 0, 0.4, 1.4),
    ];

    public override string ToString() => Name;
}

/// <summary>
/// Single easing implementation for the player overlays, layout pane, Keyframes graph and expression strings; pieced curves are
/// evaluated from the same pieces the expressions use, so preview and output match.
/// </summary>
public static class EasingCurves
{
    /// <summary>Linear piece count for curves without a closed form (Bounce, Custom Bézier).</summary>
    public const int Pieces = 12;

    // Back's overshoot (about 10%), and Elastic's period: the curves easings.net names easeOutBack and easeOutElastic.
    public const double BackOvershoot = 1.70158;
    private const double ElasticPeriod = 2 * Math.PI / 3;

    // Elastic ends a hair off its value (2^-10 of a swing): taken away along the stretch, so it ends exactly on it.
    private static readonly double ElasticRest = Math.Pow(2, -10) * Math.Sin((10 - 0.75) * ElasticPeriod);

    /// <summary>The default handles of a new Custom Bézier: Ease In-Out.</summary>
    public static (double X1, double Y1, double X2, double Y2) DefaultHandles => (0.42, 0, 0.58, 1);

    /// <summary>
    /// Eased value progress for time progress (both nominally 0-1; Back, Elastic and Bézier may overshoot). Easing and handles come from
    /// the departing keyframe.
    /// </summary>
    public static double Evaluate(double progress, in Keyframe from) =>
        from.IsPart ? EvaluatePart(progress, from) : EvaluateWhole(progress, from);

    /// <summary><see cref="Evaluate"/> over the full curve, ignoring any partial range.</summary>
    private static double EvaluateWhole(double progress, in Keyframe from)
    {
        var u = Math.Clamp(progress, 0, 1);
        switch (from.Easing)
        {
            case EasingType.EaseInOut: return (1 - Math.Cos(u * Math.PI)) / 2;
            case EasingType.EaseIn: return 1 - Math.Cos(u * Math.PI / 2);
            case EasingType.EaseOut: return Math.Sin(u * Math.PI / 2);
            case EasingType.Smoothstep: return u * u * (3 - 2 * u);
            case EasingType.Hold: return u >= 1 ? 1 : 0;
            case EasingType.Back: return 1 + (BackOvershoot + 1) * Math.Pow(u - 1, 3) + BackOvershoot * Math.Pow(u - 1, 2);
            case EasingType.Elastic: return 1 + Math.Pow(2, -10 * u) * Math.Sin((10 * u - 0.75) * ElasticPeriod) - u * ElasticRest;
            case EasingType.Bounce:
            case EasingType.CubicBezier:
                // Same points as PiecesOf but stack-allocated (hot path: every keyframed value, every frame).
                Span<(double U, double E)> points = stackalloc (double U, double E)[Pieces + 1];
                WritePieces(from, points);
                for (var i = 1; i < points.Length; i++)
                {
                    if (u <= points[i].U || i == points.Length - 1)
                    {
                        var (a, b) = (points[i - 1], points[i]);
                        return b.U - a.U < 1e-9 ? b.E : a.E + (b.E - a.E) * (u - a.U) / (b.U - a.U);
                    }
                }

                return 1;
            default: return u;
        }
    }

    /// <summary>
    /// Value of ascending keyframes at a time: clamped to the first/last outside, eased between (matches the graph exactly).
    /// </summary>
    public static double ValueAt(IReadOnlyList<Keyframe> keys, double time)
    {
        if (keys.Count == 0)
            return 0;
        if (time <= keys[0].Time)
            return keys[0].Value;

        for (var i = 1; i < keys.Count; i++)
        {
            if (time >= keys[i].Time)
                continue;

            var (a, b) = (keys[i - 1], keys[i]);
            var length = b.Time - a.Time;
            return length < 1e-6 ? b.Value : a.Value + (b.Value - a.Value) * Evaluate((time - a.Time) / length, a);
        }

        return keys[^1].Value;
    }

    /// <summary>True when the curve is piecewise linear (<see cref="PiecesOf"/>) rather than one expression.</summary>
    public static bool IsPieced(EasingType easing) => easing is EasingType.Bounce or EasingType.CubicBezier;

    /// <summary>
    /// Points (U = time, E = value) of a pieced curve from (0, 0) to (1, 1). Bounce splits at its four landings; Bézier samples its own
    /// parameter (denser where it bends) with handle x clamped to 0..1 so time is monotonic.
    /// </summary>
    public static (double U, double E)[] PiecesOf(in Keyframe from)
    {
        var points = new (double U, double E)[Pieces + 1];
        WritePieces(from, points);
        if (!from.IsPart)
            return points;

        // Partial curve: its endpoints and interior points, rescaled to 0..1.
        var (start, end) = from.Part;
        var (first, last) = (EvaluateWhole(start, from), EvaluateWhole(end, from));
        var rise = Math.Abs(last - first) < 1e-9 ? 1 : last - first;
        List<(double U, double E)> part = [(0, 0)];
        foreach (var (u, e) in points)
        {
            if (u > start + 1e-9 && u < end - 1e-9)
                part.Add(((u - start) / (end - start), (e - first) / rise));
        }

        part.Add((1, 1));
        return [.. part];
    }

    // PiecesOf's points (always Pieces + 1 of them) written into room the caller has, so that Evaluate needs no array.
    private static void WritePieces(in Keyframe from, Span<(double U, double E)> points)
    {
        if (from.Easing == EasingType.Bounce)
        {
            // easeOutBounce: four arcs of one parabola, landing at 1/2.75, 2/2.75, 2.5/2.75 and 1; 4 + 4 + 2 + 2 pieces.
            Span<double> cuts = [0, 1 / 2.75, 2 / 2.75, 2.5 / 2.75, 1];
            Span<int> pieces = [4, 4, 2, 2];
            points[0] = (0, 0);
            var next = 1;
            for (var arc = 0; arc < 4; arc++)
            {
                for (var k = 1; k <= pieces[arc]; k++)
                {
                    var u = cuts[arc] + (cuts[arc + 1] - cuts[arc]) * k / pieces[arc];
                    points[next++] = (u, BounceAt(u));
                }
            }

            return;
        }

        var (x1, y1, x2, y2) = Handles(from);
        for (var i = 0; i <= Pieces; i++)
        {
            var s = (double)i / Pieces;
            var (a, b, c) = (3 * (1 - s) * (1 - s) * s, 3 * (1 - s) * s * s, s * s * s);
            points[i] = (a * x1 + b * x2 + c, a * y1 + b * y2 + c);
        }

        points[0] = (0, 0);
        points[Pieces] = (1, 1);
    }

    /// <summary>Bézier handles clamped for monotonic time; defaults when unset.</summary>
    public static (double X1, double Y1, double X2, double Y2) Handles(in Keyframe key) =>
        key.BezierX1 == 0 && key.BezierY1 == 0 && key.BezierX2 == 0 && key.BezierY2 == 0
            ? DefaultHandles
            : (Math.Clamp(key.BezierX1, 0, 1), Math.Clamp(key.BezierY1, -2, 3), Math.Clamp(key.BezierX2, 0, 1), Math.Clamp(key.BezierY2, -2, 3));

    // ----- Partial curves (clip split between keyframes) -----
    // Each half keeps its portion of the original curve (Keyframe.PartStart / PartEnd) with rescaled progress, so motion is unchanged.

    /// <summary>
    /// Splits a segment at <paramref name="progress"/> into two keyframes sharing the easing and handles over their partial ranges
    /// (Linear and Hold split trivially).
    /// </summary>
    public static (Keyframe First, Keyframe Second) Cut(in Keyframe from, double progress)
    {
        if (from.Easing is EasingType.Linear or EasingType.Hold)
            return (from, from);

        var (start, end) = from.Part;
        var at = start + Math.Clamp(progress, 0, 1) * (end - start);
        return (from.WithPart(start, at), from.WithPart(at, end));
    }

    /// <summary>
    /// <see cref="Evaluate"/> for a partial-range keyframe: curve progress over the part, rescaled to 0..1 (flat for a static part).
    /// </summary>
    private static double EvaluatePart(double progress, in Keyframe from)
    {
        var (start, end) = from.Part;
        var (first, last) = (EvaluateWhole(start, from), EvaluateWhole(end, from));
        var rise = last - first;
        var u = Math.Clamp(progress, 0, 1);
        return Math.Abs(rise) < 1e-9 ? u : (EvaluateWhole(start + u * (end - start), from) - first) / rise;
    }

    /// <summary>FFmpeg expression for a keyframe's curve (<see cref="Expression(string, EasingType)"/>), honouring partial ranges.</summary>
    public static string Expression(string p, in Keyframe from)
    {
        if (!from.IsPart)
            return Expression(p, from.Easing);

        var (start, end) = from.Part;
        var (first, last) = (EvaluateWhole(start, from), EvaluateWhole(end, from));
        var rise = last - first;
        if (Math.Abs(rise) < 1e-9)
            return p;
        return $"(({Expression($"({Number(start)}+{p}*{Number(end - start)})", from.Easing)}-{Number(first)})*{Number(1 / rise)})";
    }

    private static double BounceAt(double u)
    {
        const double n = 7.5625, d = 2.75;
        var (middle, rest) = u < 1 / d ? (0.0, 0.0) : u < 2 / d ? (1.5 / d, 0.75) : u < 2.5 / d ? (2.25 / d, 0.9375) : (2.625 / d, 0.984375);
        return n * (u - middle) * (u - middle) + rest;
    }

    /// <summary>
    /// Wraps a 0-1 progress expression in a closed-form FFmpeg easing expression (identity for Linear). Hold and pieced curves are
    /// handled by the caller (<see cref="IsPieced"/>).
    /// </summary>
    public static string Expression(string p, EasingType type) => type switch
    {
        EasingType.EaseInOut => $"((1-cos({p}*PI))/2)",
        EasingType.EaseIn => $"(1-cos({p}*(PI/2)))",
        EasingType.EaseOut => $"sin({p}*(PI/2))",
        EasingType.Smoothstep => $"({p}*{p}*(3-2*{p}))",
        EasingType.Back => $"(1+{Number(BackOvershoot + 1)}*pow({p}-1,3)+{Number(BackOvershoot)}*pow({p}-1,2))",
        EasingType.Elastic => $"(1+pow(2,-10*{p})*sin((10*{p}-0.75)*{Number(ElasticPeriod)})-{p}*{Number(ElasticRest)})",
        _ => p,
    };

    private static string Number(double value) => value.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

    // ----- 3D View camera -----

    /// <summary>
    /// Cubic ease-in-out (4u³ to the midpoint, then 1 - (2 - 2u)³ / 2), softer at both ends than Ease In-Out's half cosine. Used for 3D
    /// View enter, exit and Reset (GpuPreviewEngine.StepPose), not keyframes.
    /// </summary>
    public static double CubicInOut(double progress)
    {
        var u = Math.Clamp(progress, 0, 1);
        return u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(2 - 2 * u, 3) / 2;
    }
}
