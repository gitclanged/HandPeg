using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Small vector icons for easings and transition styles (Keyframes pane easing drop-down, fade menus). WPF geometry lives on the UI side;
/// models hold only the source formulas (EasingCurves, TransitionStyles).
/// </summary>
internal static class IconGeometry
{
    /// <summary>Icon width and height in DIPs.</summary>
    public const double Width = 16, Height = 12;

    private static readonly Dictionary<EasingType, Geometry> EasingIcons = [];

    /// <summary>
    /// Frozen 16 x 12 easing icon: the curve sampled from <see cref="EasingCurves.Evaluate"/> at <see cref="EasingCurves.Pieces"/> + 1 points
    /// over 0..1 (Bounce uses its own pieces including landings; Hold is a step), time on X and value on Y, scaled so 0..1 plus any
    /// overshoot fills the height. Generated from the formula (no image asset), built once per easing on the UI thread and cached
    /// (Custom Bézier uses its default handles).
    /// </summary>
    public static Geometry Of(EasingType type)
    {
        if (EasingIcons.TryGetValue(type, out var kept))
            return kept;

        var key = new Keyframe(0, 0, type);
        (double U, double E)[] points = type switch
        {
            EasingType.Hold => [(0, 0), (1, 0), (1, 1)],
            EasingType.Bounce => EasingCurves.PiecesOf(key),
            _ => [.. Enumerable.Range(0, EasingCurves.Pieces + 1).Select(i => ((double)i / EasingCurves.Pieces, EasingCurves.Evaluate((double)i / EasingCurves.Pieces, key)))],
        };

        // Inset half a pixel on each side so a 1 px stroke stays inside the bounds.
        var (low, high) = (Math.Min(0, points.Min(p => p.E)), Math.Max(1, points.Max(p => p.E)));
        Point At((double U, double E) p) => new(0.5 + p.U * (Width - 1), Height - 0.5 - (p.E - low) / (high - low) * (Height - 1));

        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(At(points[0]), false, false);
            context.PolyLineTo([.. points.Skip(1).Select(At)], true, true);
        }

        curve.Freeze();
        EasingIcons[type] = curve;
        return curve;
    }

    private static readonly Dictionary<TransitionStyle, Geometry> TransitionIcons = [];

    /// <summary>
    /// Frozen 16 x 12 transition icon for the fade menus: dissolve = ramp; wipe = frame with the revealed half filled and a direction
    /// chevron; slide = direction arrow with two speed lines; zoom = four corner brackets around a small square; particle snap = block
    /// breaking into grains; blur dissolve = dots growing across; whip pan = frame with trailing speed lines; zoom warp = lines bursting from
    /// a small square; glitch = frame torn into offset bars; spin = circling arrow. Pure coordinates (no image asset) for a Path stroked and
    /// filled in the text colour; only figures meant to be solid are filled. Built once per style on the UI thread and cached.
    /// </summary>
    public static Geometry Of(TransitionStyle style)
    {
        if (TransitionIcons.TryGetValue(style, out var kept))
            return kept;

        var icon = new StreamGeometry();
        using (var context = icon.Open())
        {
            void Shape(bool filled, bool closed, params Point[] points)
            {
                context.BeginFigure(points[0], filled, closed);
                context.PolyLineTo(points[1..], true, true);
            }

            // Maps (along travel from the start side, across from the centre line) to icon DIPs for the style's direction.
            var (dx, dy) = TransitionStyles.Direction(style);
            var (along, half) = dx != 0 ? (15.0, 5.5) : (11.0, 7.5);
            Point P(double a, double c) => (dx, dy) switch
            {
                (1, _) => new(0.5 + a, 6 + c),
                (-1, _) => new(15.5 - a, 6 + c),
                (_, 1) => new(8 + c, 0.5 + a),
                _ => new(8 + c, 11.5 - a),
            };

            if (TransitionStyles.IsWipe(style))
            {
                Shape(false, true, new(0.5, 0.5), new(15.5, 0.5), new(15.5, 11.5), new(0.5, 11.5));
                Shape(true, true, P(0, -half), P(along / 2, -half), P(along / 2, half), P(0, half));
                var tip = along / 2 + 5;
                Shape(false, false, P(tip - 2.5, -2.5), P(tip, 0), P(tip - 2.5, 2.5));
            }
            else if (TransitionStyles.IsSlide(style))
            {
                var tip = along - 1;
                Shape(false, false, P(4, 0), P(tip, 0));
                Shape(false, false, P(tip - 3.5, -3.5), P(tip, 0), P(tip - 3.5, 3.5));
                Shape(false, false, P(0.5, -3), P(3, -3));
                Shape(false, false, P(0.5, 3), P(3, 3));
            }
            else if (style == TransitionStyle.Zoom)
            {
                foreach (var (x, y, sx, sy) in new[] { (0.5, 0.5, 1, 1), (15.5, 0.5, -1, 1), (0.5, 11.5, 1, -1), (15.5, 11.5, -1, -1) })
                    Shape(false, false, new(x + 4 * sx, y), new(x, y), new(x, y + 3.5 * sy));
                Shape(true, true, new(6, 4.5), new(10, 4.5), new(10, 7.5), new(6, 7.5));
            }
            else if (style == TransitionStyle.ParticleSnap)
            {
                // Solid block at left with grains scattering up and to the right.
                Shape(true, true, new(0.5, 3), new(6.5, 3), new(6.5, 11.5), new(0.5, 11.5));
                foreach (var (x, y, r) in new[] { (9.0, 8.5, 1.1), (9.5, 4.5, 0.9), (12.0, 6.0, 0.8), (12.5, 2.5, 0.7), (15.0, 1.5, 0.5) })
                {
                    context.BeginFigure(new Point(x - r, y), true, true);
                    context.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                    context.ArcTo(new Point(x - r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                }
            }
            else if (style == TransitionStyle.WhipPan)
            {
                // Frame moving left with three speed lines trailing to the right.
                Shape(false, true, new(0.5, 2.5), new(8.5, 2.5), new(8.5, 9.5), new(0.5, 9.5));
                Shape(false, false, new(10, 3.5), new(15.5, 3.5));
                Shape(false, false, new(11, 6), new(15.5, 6));
                Shape(false, false, new(10, 8.5), new(15.5, 8.5));
            }
            else if (style == TransitionStyle.ZoomWarp)
            {
                // Small square with lines radiating from its corners and sides.
                Shape(true, true, new(6, 4.5), new(10, 4.5), new(10, 7.5), new(6, 7.5));
                foreach (var (x0, y0, x1, y1) in new[] { (5.0, 3.5, 1.5, 0.5), (11.0, 3.5, 14.5, 0.5), (5.0, 8.5, 1.5, 11.5), (11.0, 8.5, 14.5, 11.5), (4.5, 6.0, 0.5, 6.0), (11.5, 6.0, 15.5, 6.0) })
                    Shape(false, false, new(x0, y0), new(x1, y1));
            }
            else if (style == TransitionStyle.RgbGlitch)
            {
                // Frame split into three horizontally offset bars.
                Shape(true, true, new(1.5, 0.5), new(15.5, 0.5), new(15.5, 3.5), new(1.5, 3.5));
                Shape(true, true, new(0.5, 4.5), new(12.5, 4.5), new(12.5, 7.5), new(0.5, 7.5));
                Shape(true, true, new(3.5, 8.5), new(15.5, 8.5), new(15.5, 11.5), new(3.5, 11.5));
            }
            else if (style == TransitionStyle.Spin)
            {
                // Three-quarter clockwise arc from the top, ending in an upward arrowhead.
                context.BeginFigure(new Point(8, 0.5), false, false);
                context.ArcTo(new Point(2.5, 6), new Size(5.5, 5.5), 0, true, SweepDirection.Clockwise, true, true);
                Shape(false, false, new(0.5, 8.5), new(2.5, 6), new(4.5, 8.5));
            }
            else if (style == TransitionStyle.BlurDissolve)
            {
                // Two rows of dots growing from small (left) to large (right): image resolving from blur.
                for (var column = 0; column < 4; column++)
                {
                    var r = 0.6 + 0.35 * column;
                    foreach (var y in new[] { 3.5, 8.5 })
                    {
                        var x = 2 + 4 * column;
                        context.BeginFigure(new Point(x - r, y), true, true);
                        context.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                        context.ArcTo(new Point(x - r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                    }
                }
            }
            else
            {
                Shape(true, true, new(0.5, 11.5), new(15.5, 0.5), new(15.5, 11.5));
            }
        }

        icon.Freeze();
        TransitionIcons[style] = icon;
        return icon;
    }
}

/// <summary>Binding converter from EasingType to its icon (used by the Keyframes pane easing drop-down).</summary>
public sealed class EasingIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is EasingType type ? IconGeometry.Of(type) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
