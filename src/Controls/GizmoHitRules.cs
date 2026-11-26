using System.Windows;
using System.Windows.Input;

namespace HandPegApp;

/// <summary>
/// Pass 5.6.4A: the layer gizmo's anchor-dot hit rule (LayerGizmoAdorner). The dot stays 9 x 9 DIP on screen but takes the pointer only
/// within a 6 x 6 square, and not at all on a box under 36 DIP either way, so a press on a small box's body moves the box instead of
/// grabbing its anchor (user decision).
/// <para>Pass 5.6.5: the 8-handle Pro Gizmo's rules. Corners keep the box's shape and Shift frees it (and skips snapping); the four edge
/// handles stretch one axis; Shift turns the rotation handle in 45 degree steps; snapping is magnetic only while the pointer moves slower
/// than <see cref="SnapBelowDipsPerSecond"/> (<see cref="SnapGate"/>, <see cref="LayoutSnap"/>).</para>
/// </summary>
public static class GizmoHitRules
{
    /// <summary>Drawn anchor dot, DIP each way.</summary>
    public const double AnchorDotSize = 9;

    /// <summary>Anchor hit square, DIP each way.</summary>
    public const double AnchorHitSize = 6;

    /// <summary>Boxes narrower or shorter than this (DIP) take no anchor hit.</summary>
    public const double AnchorMinBox = 36;

    /// <summary>Shift turns the rotation handle in steps of this many degrees.</summary>
    public const double TurnStep = 45;

    /// <summary>Pointer speed (DIP per second) below which a drag snaps: a fast drag moves freely, slowing down near a guide snaps to it.</summary>
    public const double SnapBelowDipsPerSecond = 180;

    /// <summary>Snap reach: a guide this close (DIP on screen) pulls the dragged edge, centre or size onto it.</summary>
    public const double SnapReachDips = 6;

    /// <summary>Edge handles show only on a side at least this long (DIP), so they never crowd the corner grips.</summary>
    public const double EdgeHandleMinSide = 30;

    /// <summary>True when a press at <paramref name="pointer"/> grabs the anchor dot at <paramref name="anchor"/> of a box this size.</summary>
    public static bool AnchorTakes(Point pointer, Point anchor, double boxWidth, double boxHeight) =>
        Math.Min(boxWidth, boxHeight) >= AnchorMinBox
        && Math.Abs(pointer.X - anchor.X) <= AnchorHitSize / 2 && Math.Abs(pointer.Y - anchor.Y) <= AnchorHitSize / 2;

    /// <summary>
    /// The rotation handle's angle: in <see cref="TurnStep"/> steps while <paramref name="stepped"/> (Shift), wrapped to -180..180 and rounded
    /// to a tenth of a degree.
    /// </summary>
    public static double TurnAngle(double degrees, bool stepped)
    {
        if (!double.IsFinite(degrees))
            return 0;
        var angle = (stepped ? Math.Round(degrees / TurnStep) * TurnStep : degrees) % 360;
        angle = angle > 180 ? angle - 360 : angle <= -180 ? angle + 360 : angle;
        return Math.Round(angle, 1);
    }

    /// <summary>A corner handle keeps the box's shape unless Shift frees it.</summary>
    public static bool CornerKeepsShape(ModifierKeys keys) => !keys.HasFlag(ModifierKeys.Shift);

    /// <summary>
    /// Whether a gizmo drag snaps now: the Snap Layout Guides setting is on, no key asks for a free drag (Alt; Shift on a corner), and the
    /// pointer moves slower than <see cref="SnapBelowDipsPerSecond"/>.
    /// </summary>
    public static bool Snaps(bool setting, bool freeKey, double dipsPerSecond) => setting && !freeKey && dipsPerSecond < SnapBelowDipsPerSecond;
}

/// <summary>
/// A drag's pointer speed in DIP per second, smoothed over its moves (UI thread), for velocity-gated snapping (Pass 5.6.5). The first move,
/// or one after a pause, counts as slow.
/// </summary>
public sealed class SnapGate
{
    // Longest gap between moves counted (ms): a pause reads as a slow move, never as a burst.
    private const long LongestGapMs = 250;

    private Point? _last;
    private long _lastAt;

    /// <summary>Smoothed pointer speed, DIP per second.</summary>
    public double Speed { get; private set; }

    /// <summary>Forgets the drag (a new press).</summary>
    public void Reset() => (_last, Speed) = (null, 0);

    /// <summary>Notes the pointer at <paramref name="at"/> (DIP) at <paramref name="nowMs"/> (Environment.TickCount64); returns the speed.</summary>
    public double Note(Point at, long nowMs)
    {
        if (_last is { } last)
        {
            var gap = Math.Clamp(nowMs - _lastAt, 1, LongestGapMs);
            Speed = Smoothed(Speed, (at - last).Length * 1000 / gap);
        }

        (_last, _lastAt) = (at, nowMs);
        return Speed;
    }

    /// <summary>Pure smoothing: 60% of the newest speed, 40% of what it was.</summary>
    public static double Smoothed(double previous, double now) => double.IsFinite(now) ? previous * 0.4 + now * 0.6 : previous;
}

/// <summary>A rectangle in output-frame fractions (left, top, width, height), as the snapping engine sees a layer's visible box.</summary>
public readonly record struct SnapRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;
}

/// <summary>A snap guide line in frame fractions: vertical at x = <paramref name="At"/> (or horizontal at y) from <paramref name="From"/> to <paramref name="To"/>.</summary>
public readonly record struct SnapGuide(bool Vertical, double At, double From, double To);

/// <summary>
/// An equal-spacing callout <c>|← px →|</c> in frame fractions: along x (or y when not <paramref name="Horizontal"/>) from
/// <paramref name="From"/> to <paramref name="To"/>, drawn at <paramref name="Across"/> on the other axis, the gap in output pixels.
/// </summary>
public readonly record struct SpacingCallout(bool Horizontal, double From, double To, double Across, int Pixels);

/// <summary>What a snapped drag moved by (frame fractions) and what to draw: guides, equal-spacing callouts and 1:1 equal-size badges.</summary>
public sealed record SnapResult(double DeltaX, double DeltaY, IReadOnlyList<SnapGuide> Guides, IReadOnlyList<SpacingCallout> Spacings, IReadOnlyList<SnapRect> SameSize)
{
    public static SnapResult None { get; } = new(0, 0, [], [], []);
}

/// <summary>
/// Magnetic snapping of a dragged layer box (Pass 5.6.5, pure; golden-tested): its left/centre/right and top/middle/bottom snap to the
/// frame's edges, centre and safe margins (5% action, 10% title), to other layers' edges and centres, and to equal gaps between three or
/// more layers in a row or column (the <c>|← px →|</c> callouts); sizes snap to other layers' widths and heights (1:1 badges).
/// </summary>
public static class LayoutSnap
{
    /// <summary>Frame lines every box snaps to: edges, safe margins and centre.</summary>
    public static readonly double[] FrameLines = [0, 0.05, 0.1, 0.5, 0.9, 0.95, 1];

    // Two sizes within half an output pixel are the same size.
    private const double SamePixels = 0.5;

    /// <summary>
    /// Snaps a moving box within <paramref name="reachX"/>/<paramref name="reachY"/> (frame fractions) on each axis to the nearest frame
    /// line, other box line or equal gap; <paramref name="frameWidth"/> x <paramref name="frameHeight"/> output pixels label the gaps.
    /// </summary>
    public static SnapResult Move(SnapRect moving, IReadOnlyList<SnapRect> others, double reachX, double reachY, double frameWidth, double frameHeight)
    {
        if (!(reachX > 0) || !(reachY > 0) || !IsFinite(moving))
            return SnapResult.None;

        var (dx, spacedX) = Axis(moving, others, reachX, horizontal: true);
        var (dy, spacedY) = Axis(moving, others, reachY, horizontal: false);
        var snapped = moving with { X = moving.X + dx, Y = moving.Y + dy };

        var guides = new List<SnapGuide>();
        var spacings = new List<SpacingCallout>();
        if (dx != 0 || spacedX is not null)
            Lines(snapped, others, horizontal: true, guides);
        if (dy != 0 || spacedY is not null)
            Lines(snapped, others, horizontal: false, guides);
        if (spacedX is { } rowGaps)
            Callouts(snapped, rowGaps, horizontal: true, frameWidth, spacings);
        if (spacedY is { } columnGaps)
            Callouts(snapped, columnGaps, horizontal: false, frameHeight, spacings);

        var same = SameSize(snapped, others, frameWidth, frameHeight);
        return new SnapResult(dx, dy, guides, spacings, same);
    }

    /// <summary>
    /// The nearest of <paramref name="sizes"/> within <paramref name="reach"/> of <paramref name="size"/> (all frame fractions), or null: a
    /// resize snaps its width or height to another layer's (1:1).
    /// </summary>
    public static double? MatchSize(double size, IEnumerable<double> sizes, double reach)
    {
        double? best = null;
        foreach (var other in sizes)
        {
            if (other > 0 && Math.Abs(other - size) <= reach && (best is not { } b || Math.Abs(other - size) < Math.Abs(b - size)))
                best = other;
        }

        return best;
    }

    /// <summary>Other boxes the same size as <paramref name="box"/> (within half an output pixel each way), plus the box itself when any.</summary>
    public static List<SnapRect> SameSize(SnapRect box, IReadOnlyList<SnapRect> others, double frameWidth, double frameHeight)
    {
        var same = new List<SnapRect>();
        foreach (var other in others)
        {
            if (Math.Abs(other.Width - box.Width) * frameWidth <= SamePixels && Math.Abs(other.Height - box.Height) * frameHeight <= SamePixels)
                same.Add(other);
        }

        if (same.Count > 0)
            same.Add(box);
        return same;
    }

    // One axis: the smallest offset within reach to a frame line, another box's line, or an equal gap (with the boxes whose gaps it
    // equals, in order along the axis).
    private static (double Offset, (SnapRect[] Row, int Moving)? Spaced) Axis(SnapRect moving, IReadOnlyList<SnapRect> others, double reach, bool horizontal)
    {
        var (start, size) = horizontal ? (moving.X, moving.Width) : (moving.Y, moving.Height);
        // The best offset and, for an equal-gap snap, its pair's index in the level row and the moving box's place among the three.
        var (best, pair, place) = (double.NaN, -1, 0);

        void Offer(double offset, int at = -1, int where = 0)
        {
            if (double.IsFinite(offset) && Math.Abs(offset) <= reach && (double.IsNaN(best) || Math.Abs(offset) < Math.Abs(best) - 1e-12))
                (best, pair, place) = (offset, at, where);
        }

        foreach (var line in FrameLines)
        {
            for (var part = 0; part <= 2; part++)
                Offer(line - (start + size * part / 2));
        }

        foreach (var other in others)
        {
            var (otherStart, otherSize) = horizontal ? (other.X, other.Width) : (other.Y, other.Height);
            for (var mine = 0; mine <= 2; mine++)
            {
                for (var theirs = 0; theirs <= 2; theirs++)
                    Offer(otherStart + otherSize * theirs / 2 - (start + size * mine / 2));
            }
        }

        // Equal gaps: only boxes level with the moving one on the other axis form a row (or column).
        var level = others.Where(o => Overlaps(o, moving, !horizontal)).OrderBy(o => horizontal ? o.X : o.Y).ToList();
        // Neighbours in order only (never a callout across a box between them): O(n) per move, no allocation until the winner's row.
        for (var i = 0; i + 1 < level.Count; i++)
        {
            var (a, b) = (level[i], level[i + 1]);
            var (aEnd, bStart, bEnd, aStart) = horizontal ? (a.Right, b.X, b.Right, a.X) : (a.Bottom, b.Y, b.Bottom, a.Y);
            var gap = bStart - aEnd;

            // Between a and b, an equal gap each side.
            var between = (aEnd + bStart - size) / 2;
            if (between >= aEnd - 1e-9 && between + size <= bStart + 1e-9)
                Offer(between - start, i, 1);

            // After b, or before a, at the gap a and b keep.
            if (gap > 1e-6)
            {
                Offer(bEnd + gap - start, i, 2);
                Offer(aStart - gap - size - start, i, 0);
            }
        }

        if (double.IsNaN(best))
            return (0, null);
        if (pair < 0)
            return (best, null);
        var (first, second) = (level[pair], level[pair + 1]);
        SnapRect[] row = place switch { 0 => [moving, first, second], 1 => [first, moving, second], _ => [first, second, moving] };
        return (best, (row, place));
    }

    // True when two boxes share some extent across the axis (x when acrossX).
    private static bool Overlaps(SnapRect a, SnapRect b, bool acrossX) =>
        acrossX ? a.X < b.Right && b.X < a.Right : a.Y < b.Bottom && b.Y < a.Bottom;

    // Guides for every frame line and other box line that the snapped box's edges and centre now sit on.
    private static void Lines(SnapRect box, IReadOnlyList<SnapRect> others, bool horizontal, List<SnapGuide> guides)
    {
        var mine = horizontal ? new[] { box.X, box.CenterX, box.Right } : [box.Y, box.CenterY, box.Bottom];
        foreach (var line in FrameLines)
        {
            if (mine.Any(m => Math.Abs(m - line) < 1e-6))
                guides.Add(new SnapGuide(horizontal, line, 0, 1));
        }

        foreach (var other in others)
        {
            var theirs = horizontal ? new[] { other.X, other.CenterX, other.Right } : [other.Y, other.CenterY, other.Bottom];
            foreach (var line in theirs)
            {
                if (!mine.Any(m => Math.Abs(m - line) < 1e-6))
                    continue;
                var (from, to) = horizontal ? (Math.Min(box.Y, other.Y), Math.Max(box.Bottom, other.Bottom)) : (Math.Min(box.X, other.X), Math.Max(box.Right, other.Right));
                guides.Add(new SnapGuide(horizontal, line, from, to));
            }
        }
    }

    // Callouts for each gap of a spaced row (or column), labelled in output pixels; the moving box is drawn where it snapped.
    private static void Callouts(SnapRect snapped, (SnapRect[] Row, int Moving) spaced, bool horizontal, double pixels, List<SpacingCallout> into)
    {
        var row = spaced.Row.ToArray();
        row[spaced.Moving] = snapped;
        for (var i = 1; i < row.Length; i++)
        {
            var (a, b) = (row[i - 1], row[i]);
            var (from, to) = horizontal ? (a.Right, b.X) : (a.Bottom, b.Y);
            var across = horizontal
                ? (Math.Max(a.Y, b.Y) + Math.Min(a.Bottom, b.Bottom)) / 2
                : (Math.Max(a.X, b.X) + Math.Min(a.Right, b.Right)) / 2;
            into.Add(new SpacingCallout(horizontal, from, to, across, (int)Math.Round((to - from) * pixels)));
        }
    }

    private static bool IsFinite(SnapRect r) => double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);
}
