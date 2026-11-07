using System.Windows;
using System.Windows.Media;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models;

/// <summary>
/// How a clip's picture comes on over its fade in, or goes over its fade out (its sound fades the same way whatever this
/// is). Every clip can fade on its own, over whatever is under it, so a transition between two clips on separate tracks
/// is the upper one's fade over the lower. A direction is the way the edge of a wipe, or the picture of a slide, travels.
/// </summary>
/// <remarks>Saved by name in projects: new styles are added at the end, and none is renamed.</remarks>
public enum TransitionStyle
{
    /// <summary>Its opacity goes from nothing to all (or back): the plain fade, and a cross dissolve over a clip under it.</summary>
    Dissolve,

    WipeLeft,
    WipeRight,
    WipeUp,
    WipeDown,

    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,

    /// <summary>Grows from nothing about its anchor point with a little overshoot (or shrinks away to nothing).</summary>
    Zoom,

    /// <summary>A dissolve that comes in out of a blur (or goes out into one).</summary>
    BlurDissolve,

    /// <summary>The Thanos Snap: the picture breaks into embers on a wind (or gathers itself out of them).</summary>
    ParticleSnap,

    /// <summary>Whips in from the right, smeared the way it goes (or away to the left): a camera whip (5.2.1).</summary>
    WhipPan,

    /// <summary>Punches in from three times its size about its middle, streaked outward (or away into it).</summary>
    ZoomWarp,

    /// <summary>Torn rows and split colours that settle as it comes on, blinking between it and what is under it.</summary>
    RgbGlitch,

    /// <summary>A whole turn about its anchor, blurred along the turn, coming on as it starts (or going as it ends).</summary>
    Spin,
}

/// <summary>What each <see cref="TransitionStyle"/> is called, which way it goes, and the curve its fade follows.</summary>
public static class TransitionStyles
{
    public static IReadOnlyList<TransitionStyle> All { get; } = Enum.GetValues<TransitionStyle>();

    public static string Label(TransitionStyle style) => style switch
    {
        TransitionStyle.WipeLeft => "Wipe Left",
        TransitionStyle.WipeRight => "Wipe Right",
        TransitionStyle.WipeUp => "Wipe Up",
        TransitionStyle.WipeDown => "Wipe Down",
        TransitionStyle.SlideLeft => "Slide Left",
        TransitionStyle.SlideRight => "Slide Right",
        TransitionStyle.SlideUp => "Slide Up",
        TransitionStyle.SlideDown => "Slide Down",
        TransitionStyle.Zoom => "Zoom (Scale Pop)",
        TransitionStyle.BlurDissolve => "Blur Dissolve",
        TransitionStyle.ParticleSnap => "Particle Disintegration (Thanos Snap)",
        TransitionStyle.WhipPan => "Whip Pan",
        TransitionStyle.ZoomWarp => "Zoom Warp",
        TransitionStyle.RgbGlitch => "RGB Glitch",
        TransitionStyle.Spin => "Spin",
        _ => "Dissolve",
    };

    /// <summary>The way it travels, as a step across (X) and down (Y); (0, 0) for a style that goes nowhere.</summary>
    public static (int X, int Y) Direction(TransitionStyle style) => style switch
    {
        TransitionStyle.WipeLeft or TransitionStyle.SlideLeft => (-1, 0),
        TransitionStyle.WipeRight or TransitionStyle.SlideRight => (1, 0),
        TransitionStyle.WipeUp or TransitionStyle.SlideUp => (0, -1),
        TransitionStyle.WipeDown or TransitionStyle.SlideDown => (0, 1),
        _ => (0, 0),
    };

    public static bool IsWipe(TransitionStyle style) => style is TransitionStyle.WipeLeft or TransitionStyle.WipeRight or TransitionStyle.WipeUp or TransitionStyle.WipeDown;

    public static bool IsSlide(TransitionStyle style) => style is TransitionStyle.SlideLeft or TransitionStyle.SlideRight or TransitionStyle.SlideUp or TransitionStyle.SlideDown;

    /// <summary>
    /// The curve a fade of this style follows, as how much of the clip is there (0 to 1) against how far through the fade
    /// the clock is: a dissolve evenly; a wipe easing in and out; a slide arriving gently (and leaving as gently as it
    /// starts); a zoom with a little overshoot; the Thanos Snap evenly; a whip pan and a spin easing in and out, a zoom warp
    /// landing gently, a glitch evenly. The same curve on the timeline and in the graph (EasingCurves).
    /// </summary>
    public static EasingType Easing(TransitionStyle style) => style switch
    {
        TransitionStyle.Dissolve or TransitionStyle.BlurDissolve or TransitionStyle.ParticleSnap or TransitionStyle.RgbGlitch => EasingType.Linear,
        TransitionStyle.Zoom => EasingType.Back,
        TransitionStyle.ZoomWarp => EasingType.EaseOut,
        _ when IsSlide(style) => EasingType.EaseOut,
        _ => EasingType.EaseInOut,
    };

    /// <summary>Its shader in the v5.0 compositor (ARCHITECTURE_CONTRACTS.md): one for each family, the direction in the packet.</summary>
    public static GpuShaderSlot GpuSlot(TransitionStyle style) => style switch
    {
        TransitionStyle.Zoom => GpuShaderSlot.TransitionZoom,
        TransitionStyle.BlurDissolve => GpuShaderSlot.TransitionBlurDissolve,
        TransitionStyle.ParticleSnap => GpuShaderSlot.TransitionParticleSnap,
        TransitionStyle.WhipPan => GpuShaderSlot.TransitionWhipPan,
        TransitionStyle.ZoomWarp => GpuShaderSlot.TransitionZoomWarp,
        TransitionStyle.RgbGlitch => GpuShaderSlot.TransitionRgbGlitch,
        TransitionStyle.Spin => GpuShaderSlot.TransitionSpin,
        _ when IsWipe(style) => GpuShaderSlot.TransitionWipe,
        _ when IsSlide(style) => GpuShaderSlot.TransitionSlide,
        _ => GpuShaderSlot.TransitionDissolve,
    };

    /// <summary>
    /// A fade of this style at a moment, as the v5.0 compositor takes it: P0 how much of the clip is there along the
    /// style's curve (<see cref="Easing"/>, as the graph writes it), P1 and P2 the direction (<see cref="Direction"/>),
    /// P3 how far through the fade the clock is (0 to 1), P4 1 for a fade in and 0 for a fade out.
    /// </summary>
    public static GpuEffectPacket GpuPacket(TransitionStyle style, double progress, bool fadingIn)
    {
        var through = Math.Clamp(progress, 0, 1);
        var shown = EasingCurves.Evaluate(fadingIn ? through : 1 - through, new Keyframe(0, 0, Easing(style)));
        var (x, y) = Direction(style);
        return new(GpuSlot(style), (float)shown, x, y, (float)through, fadingIn ? 1 : 0, 0, 0, 0);
    }

    /// <summary>
    /// The Thanos Snap as a filter graph's <c>geq</c> writes it (the export without the card): 1 where a grain of <paramref name="grain"/>
    /// pixels is still there at <paramref name="shown"/>, in the card's sweep from the side the wind blows to. In place: no embers fly.
    /// </summary>
    public static string SnapGrains(string grain, string shown)
    {
        var hash = $"(sin(floor(X/{grain})*12.9898+floor(Y/{grain})*78.233)*43758.5453)";
        return $"lt(0.58*(1.34-X/W*0.86-(1-Y/H)*0.5)+0.42*({hash}-floor({hash})),{shown})";
    }

    private static readonly Dictionary<TransitionStyle, Geometry> Icons = [];

    /// <summary>
    /// A frozen 16 x 12 picture of a style (4.2B-3), for the fade menus: a dissolve a ramp; a wipe the frame, the half its
    /// edge has uncovered filled and a chevron the way it goes; a slide an arrow the way it goes, with two speed lines; a
    /// zoom four corners around a small square; a blur dissolve soft dots growing across; a whip pan a frame with speed
    /// lines trailing it; a zoom warp lines bursting out of a small square; a glitch the frame torn into offset bars; a spin
    /// a circling arrow. Drawn from coordinates alone (no
    /// picture file), for a Path that strokes and fills with the text colour: only the figures meant solid are filled.
    /// Made once per style, on the UI thread, and kept.
    /// </summary>
    public static Geometry IconGeometry(TransitionStyle style)
    {
        if (Icons.TryGetValue(style, out var kept))
            return kept;

        var icon = new StreamGeometry();
        using (var context = icon.Open())
        {
            void Shape(bool filled, bool closed, params Point[] points)
            {
                context.BeginFigure(points[0], filled, closed);
                context.PolyLineTo(points[1..], true, true);
            }

            // A point a given way along the travel (from the side it starts at) and across it (from the middle), in DIPs.
            var (dx, dy) = Direction(style);
            var (along, half) = dx != 0 ? (15.0, 5.5) : (11.0, 7.5);
            Point P(double a, double c) => (dx, dy) switch
            {
                (1, _) => new(0.5 + a, 6 + c),
                (-1, _) => new(15.5 - a, 6 + c),
                (_, 1) => new(8 + c, 0.5 + a),
                _ => new(8 + c, 11.5 - a),
            };

            if (IsWipe(style))
            {
                Shape(false, true, new(0.5, 0.5), new(15.5, 0.5), new(15.5, 11.5), new(0.5, 11.5));
                Shape(true, true, P(0, -half), P(along / 2, -half), P(along / 2, half), P(0, half));
                var tip = along / 2 + 5;
                Shape(false, false, P(tip - 2.5, -2.5), P(tip, 0), P(tip - 2.5, 2.5));
            }
            else if (IsSlide(style))
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
                // A block breaking up: whole at the left, grains flying off up and to the right.
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
                // A frame flying left, three speed lines trailing it to the right.
                Shape(false, true, new(0.5, 2.5), new(8.5, 2.5), new(8.5, 9.5), new(0.5, 9.5));
                Shape(false, false, new(10, 3.5), new(15.5, 3.5));
                Shape(false, false, new(11, 6), new(15.5, 6));
                Shape(false, false, new(10, 8.5), new(15.5, 8.5));
            }
            else if (style == TransitionStyle.ZoomWarp)
            {
                // A small square with lines bursting out of its corners and sides.
                Shape(true, true, new(6, 4.5), new(10, 4.5), new(10, 7.5), new(6, 7.5));
                foreach (var (x0, y0, x1, y1) in new[] { (5.0, 3.5, 1.5, 0.5), (11.0, 3.5, 14.5, 0.5), (5.0, 8.5, 1.5, 11.5), (11.0, 8.5, 14.5, 11.5), (4.5, 6.0, 0.5, 6.0), (11.5, 6.0, 15.5, 6.0) })
                    Shape(false, false, new(x0, y0), new(x1, y1));
            }
            else if (style == TransitionStyle.RgbGlitch)
            {
                // The frame torn into three bars, each slid its own way.
                Shape(true, true, new(1.5, 0.5), new(15.5, 0.5), new(15.5, 3.5), new(1.5, 3.5));
                Shape(true, true, new(0.5, 4.5), new(12.5, 4.5), new(12.5, 7.5), new(0.5, 7.5));
                Shape(true, true, new(3.5, 8.5), new(15.5, 8.5), new(15.5, 11.5), new(3.5, 11.5));
            }
            else if (style == TransitionStyle.Spin)
            {
                // Three quarters of a circle turning clockwise from its top, an arrowhead at its end pointing up the way it goes.
                context.BeginFigure(new Point(8, 0.5), false, false);
                context.ArcTo(new Point(2.5, 6), new Size(5.5, 5.5), 0, true, SweepDirection.Clockwise, true, true);
                Shape(false, false, new(0.5, 8.5), new(2.5, 6), new(4.5, 8.5));
            }
            else if (style == TransitionStyle.BlurDissolve)
            {
                // Dots in two rows, small at the left and large at the right: a picture coming out of a blur.
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
        Icons[style] = icon;
        return icon;
    }
}
