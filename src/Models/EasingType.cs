using System.Windows;
using System.Windows.Media;

namespace HandPegApp.Models;

/// <summary>
/// How a clip gets from one keyframe to the next: the shape of the stretch that leaves the keyframe it is
/// set on. Each is a curve of how far through the stretch the clip is (0 to 1): closed form where FFmpeg can
/// write it in one expression, and otherwise a few straight pieces (see <see cref="EasingCurves"/>), so that the
/// whole motion stays one flat sum of clipped ramps with no nested if().
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
    /// <summary>The curve's 16 x 12 picture beside its name (4.2B-3, <see cref="EasingCurves.IconGeometry"/>).</summary>
    public Geometry Icon => EasingCurves.IconGeometry(Type);

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
/// The easing curves, once, for everything that needs them: the player's boxes and the layout pane (C#), the Keyframes
/// pane's graph, and the FFmpeg expressions of the export and Live Preview. What is seen is what is encoded: the curves
/// FFmpeg is given as straight pieces are evaluated here from the same pieces.
/// </summary>
public static class EasingCurves
{
    /// <summary>How many straight pieces a curve FFmpeg cannot write in closed form is cut into (Bounce, Custom Bézier).</summary>
    public const int Pieces = 12;

    // Back's overshoot (about 10%), and Elastic's period: the curves easings.net names easeOutBack and easeOutElastic.
    public const double BackOvershoot = 1.70158;
    private const double ElasticPeriod = 2 * Math.PI / 3;

    // Elastic ends a hair off its value (2^-10 of a swing): taken away along the stretch, so it ends exactly on it.
    private static readonly double ElasticRest = Math.Pow(2, -10) * Math.Sin((10 - 0.75) * ElasticPeriod);

    /// <summary>The default handles of a new Custom Bézier: Ease In-Out.</summary>
    public static (double X1, double Y1, double X2, double Y2) DefaultHandles => (0.42, 0, 0.58, 1);

    /// <summary>
    /// How far a stretch has gone in value, given how far through its time it is (both 0 to 1, though Back, Elastic and
    /// a Bézier may run past 1 or below 0 on the way). The keyframe the stretch leaves gives the easing and the handles.
    /// </summary>
    public static double Evaluate(double progress, in Keyframe from)
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
                var points = PiecesOf(from);
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
    /// The value a list of keyframes (ascending) gives at a moment: the first before the first, the last after the last,
    /// and between two the stretch's easing. As the graph works it out, frame for frame.
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

    /// <summary>Whether FFmpeg is given the curve as straight pieces (see <see cref="PiecesOf"/>) rather than one expression.</summary>
    public static bool IsPieced(EasingType easing) => easing is EasingType.Bounce or EasingType.CubicBezier;

    /// <summary>
    /// The points a pieced curve is drawn through, from (0, 0) to (1, 1): how far through the time (U) and how far in
    /// value (E). Bounce is cut at its four landings, so each arc is pieces of its own; a Bézier is sampled along its own
    /// parameter, which puts more points where it bends most. Its handles' x are held to 0..1, so time runs forwards.
    /// </summary>
    public static (double U, double E)[] PiecesOf(in Keyframe from)
    {
        if (from.Easing == EasingType.Bounce)
        {
            // easeOutBounce: four arcs of one parabola, landing at 1/2.75, 2/2.75, 2.5/2.75 and 1; 4 + 4 + 2 + 2 pieces.
            Span<double> cuts = [0, 1 / 2.75, 2 / 2.75, 2.5 / 2.75, 1];
            Span<int> pieces = [4, 4, 2, 2];
            var points = new List<(double, double)>(Pieces + 1) { (0, 0) };
            for (var arc = 0; arc < 4; arc++)
            {
                for (var k = 1; k <= pieces[arc]; k++)
                {
                    var u = cuts[arc] + (cuts[arc + 1] - cuts[arc]) * k / pieces[arc];
                    points.Add((u, BounceAt(u)));
                }
            }

            return [.. points];
        }

        var (x1, y1, x2, y2) = Handles(from);
        var sampled = new (double U, double E)[Pieces + 1];
        for (var i = 0; i <= Pieces; i++)
        {
            var s = (double)i / Pieces;
            var (a, b, c) = (3 * (1 - s) * (1 - s) * s, 3 * (1 - s) * s * s, s * s * s);
            sampled[i] = (a * x1 + b * x2 + c, a * y1 + b * y2 + c);
        }

        sampled[0] = (0, 0);
        sampled[Pieces] = (1, 1);
        return sampled;
    }

    /// <summary>A Bézier keyframe's handles, held to what makes a curve that runs forwards in time; the default ones when it has none.</summary>
    public static (double X1, double Y1, double X2, double Y2) Handles(in Keyframe key) =>
        key.BezierX1 == 0 && key.BezierY1 == 0 && key.BezierX2 == 0 && key.BezierY2 == 0
            ? DefaultHandles
            : (Math.Clamp(key.BezierX1, 0, 1), Math.Clamp(key.BezierY1, -2, 3), Math.Clamp(key.BezierX2, 0, 1), Math.Clamp(key.BezierY2, -2, 3));

    private static double BounceAt(double u)
    {
        const double n = 7.5625, d = 2.75;
        var (middle, rest) = u < 1 / d ? (0.0, 0.0) : u < 2 / d ? (1.5 / d, 0.75) : u < 2.5 / d ? (2.25 / d, 0.9375) : (2.625 / d, 0.984375);
        return n * (u - middle) * (u - middle) + rest;
    }

    /// <summary>
    /// A stretch's progress (an expression that runs from 0 to 1) bent into a closed-form easing curve, for FFmpeg; the
    /// progress itself for Linear. Hold and the pieced curves are written by the caller instead (see <see cref="IsPieced"/>).
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

    // ----- The curves' pictures -----

    /// <summary>How wide and how tall a curve's picture is, in DIPs.</summary>
    public const double IconWidth = 16, IconHeight = 12;

    private static readonly Dictionary<EasingType, Geometry> Icons = [];

    /// <summary>
    /// A frozen 16 x 12 picture of an easing (4.2B-3): its curve sampled from <see cref="Evaluate"/> at <see cref="Pieces"/>
    /// + 1 points from 0 to 1 (Bounce at its own pieces, landings included; Hold as the step it is), time across and value
    /// up, scaled so that 0 to 1 and any overshoot fill the height. Drawn from the formula alone, no picture file; made once
    /// per easing, on the UI thread, and kept (a Custom Bézier shows its default handles).
    /// </summary>
    public static Geometry IconGeometry(EasingType type)
    {
        if (Icons.TryGetValue(type, out var kept))
            return kept;

        var key = new Keyframe(0, 0, type);
        (double U, double E)[] points = type switch
        {
            EasingType.Hold => [(0, 0), (1, 0), (1, 1)],
            EasingType.Bounce => PiecesOf(key),
            _ => [.. Enumerable.Range(0, Pieces + 1).Select(i => ((double)i / Pieces, Evaluate((double)i / Pieces, key)))],
        };

        // Half a pixel in from each side, so a 1 px stroke stays inside the picture.
        var (low, high) = (Math.Min(0, points.Min(p => p.E)), Math.Max(1, points.Max(p => p.E)));
        Point At((double U, double E) p) => new(0.5 + p.U * (IconWidth - 1), IconHeight - 0.5 - (p.E - low) / (high - low) * (IconHeight - 1));

        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(At(points[0]), false, false);
            context.PolyLineTo([.. points.Skip(1).Select(At)], true, true);
        }

        curve.Freeze();
        Icons[type] = curve;
        return curve;
    }
}
