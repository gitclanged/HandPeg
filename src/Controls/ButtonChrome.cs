using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// The face of a button, drawn as one element (Pass 4.1C): its fill, a 1 px outline and the bevel (a light top and left
/// edge just inside the outline), all with the theme's corner rounding, and its content inside the padding. It stands
/// for the Grid of three Borders the button templates had: on a window of a few hundred buttons that was a few hundred
/// elements more to make, measure and draw before the first frame (about 7 ms, measured), for lines a drawing makes
/// with no elements at all.
///
/// The outline and the bevel are drawn over the fill and take no room, so a button is as large as its padding and its
/// content (<see cref="OutlineTakesRoom"/> gives the outline its pixel, as the toggle buttons' Border always did). A
/// BorderThickness of 0 draws neither. Lines fall on whole device pixels.
///
/// Since Pass 4.1C.2 the outline may be lit from the top left (<see cref="LowerBorderBrush"/>): BorderBrush along the top
/// and left, the lower brush along the bottom and right, the classic raised control (light over dark) or sunken well
/// (dark over light), split exactly at the top-right and bottom-left corners whatever the shape. A brush cannot do that
/// (a gradient's split runs from corner to corner, along a thin bar's bottom edge for many pixels), so the lines are
/// drawn here; and that is why the text boxes, combo boxes, check boxes, lists, cards and wells are faced by this element
/// too, in place of the one Border each had.
/// </summary>
public sealed class ButtonChrome : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The outline's colour.</summary>
    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(Thickness), typeof(ButtonChrome),
        new FrameworkPropertyMetadata(new Thickness(1), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Any thickness above 0 draws the outline (1 px) and the bevel; 0 draws neither.</summary>
    public Thickness BorderThickness
    {
        get => (Thickness)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public static readonly DependencyProperty BevelBrushProperty = DependencyProperty.Register(
        nameof(BevelBrush), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The bevel's colour: transparent (or null) for a flat button.</summary>
    public Brush? BevelBrush
    {
        get => (Brush?)GetValue(BevelBrushProperty);
        set => SetValue(BevelBrushProperty, value);
    }

    public static readonly DependencyProperty LowerBorderBrushProperty = DependencyProperty.Register(
        nameof(LowerBorderBrush), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The outline's bottom and right sides, for an edge lit from the top left (the theme's ...LowerBrush tokens); null,
    /// as in the flat themes, for an outline of one colour. Triggers that colour the outline (hover, focus, chosen) clear it.
    /// </summary>
    public Brush? LowerBorderBrush
    {
        get => (Brush?)GetValue(LowerBorderBrushProperty);
        set => SetValue(LowerBorderBrushProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(ButtonChrome), new FrameworkPropertyMetadata(new CornerRadius(), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The corners' rounding (its top left is used for all four, as the themes round them alike).</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ButtonChrome), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public static readonly DependencyProperty OutlineTakesRoomProperty = DependencyProperty.Register(
        nameof(OutlineTakesRoom), typeof(bool), typeof(ButtonChrome), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Whether the outline adds its pixel on each side to the size (a toggle button), or is drawn over the fill (a button).</summary>
    public bool OutlineTakesRoom
    {
        get => (bool)GetValue(OutlineTakesRoomProperty);
        set => SetValue(OutlineTakesRoomProperty, value);
    }

    public static readonly DependencyProperty DottedOutlineBrushProperty = DependencyProperty.Register(
        nameof(DottedOutlineBrush), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The classic 1 px dotted rectangle inside the face (4.2B-3), one pixel on and one off, square-cornered, drawn over the
    /// content: the theme's DottedFocusBrush, set by triggers on what is focused, checked or chosen (Reno, Reno Dark;
    /// transparent, so not drawn, in the flat themes). Drawn whether or not there is an outline.
    /// </summary>
    public Brush? DottedOutlineBrush
    {
        get => (Brush?)GetValue(DottedOutlineBrushProperty);
        set => SetValue(DottedOutlineBrushProperty, value);
    }

    public static readonly DependencyProperty DottedInsetProperty = DependencyProperty.Register(
        nameof(DottedInset), typeof(double), typeof(ButtonChrome), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How far inside the edge the dotted rectangle runs, in device pixels (3: just inside a button's outline).</summary>
    public double DottedInset
    {
        get => (double)GetValue(DottedInsetProperty);
        set => SetValue(DottedInsetProperty, value);
    }

    private bool HasOutline => BorderThickness is { Left: > 0 } or { Top: > 0 } or { Right: > 0 } or { Bottom: > 0 };

    // The room around the content: the padding, and the outline's pixel when it takes room.
    private Thickness Inset()
    {
        var (padding, edge) = (Padding, OutlineTakesRoom && HasOutline ? 1.0 : 0.0);
        return new Thickness(padding.Left + edge, padding.Top + edge, padding.Right + edge, padding.Bottom + edge);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var inset = Inset();
        var (across, down) = (inset.Left + inset.Right, inset.Top + inset.Bottom);
        if (Child is not { } child)
            return new Size(across, down);

        child.Measure(new Size(Math.Max(0, constraint.Width - across), Math.Max(0, constraint.Height - down)));
        return new Size(child.DesiredSize.Width + across, child.DesiredSize.Height + down);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var inset = Inset();
        Child?.Arrange(new Rect(inset.Left, inset.Top,
            Math.Max(0, arrangeSize.Width - inset.Left - inset.Right), Math.Max(0, arrangeSize.Height - inset.Top - inset.Bottom)));
        return arrangeSize;
    }

    // Pens kept while their brush stays the same (the theme's brushes are frozen, so the same brush is the same colour).
    private Pen? _outlinePen, _bevelPen, _lowerPen, _dottedPen;

    // The outline's pens end square (half a pixel past their ends), so each half of a lit outline covers its corner
    // pixels whole; a closed outline has no ends, so it is unchanged by that.
    private static Pen PenFor(ref Pen? kept, Brush brush, bool squareEnds = false)
    {
        if (kept is null || !ReferenceEquals(kept.Brush, brush))
        {
            kept = new Pen(brush, 1);
            if (squareEnds)
                kept.StartLineCap = kept.EndLineCap = PenLineCap.Square;
            kept.Freeze();
        }

        return kept;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var (width, height) = (RenderSize.Width, RenderSize.Height);
        if (width <= 0 || height <= 0)
            return;

        var radius = Math.Max(0, Math.Min(CornerRadius.TopLeft, Math.Min(width, height) / 2));
        if (Background is { } fill)
            drawing.DrawRoundedRectangle(fill, null, new Rect(0, 0, width, height), radius, radius);

        var dotted = DottedOutlineBrush is SolidColorBrush { Color.A: > 0 } dots ? dots : null;
        if ((!HasOutline && dotted is null) || width < 2 || height < 2)
            return;

        // Lines on the middle of whole device pixels: a 1 px line at x.5 covers exactly one pixel column.
        var scale = VisualTreeHelper.GetDpi(this);
        var half = 0.5 / scale.DpiScaleX;
        var guidelines = new GuidelineSet([0, width], [0, height]);
        guidelines.Freeze();
        drawing.PushGuidelineSet(guidelines);

        if (HasOutline)
            DrawOutline(drawing, width, height, radius, scale, half);

        if (dotted is not null)
        {
            // Square-cornered and on whole pixels: a zero-length dash with square caps is one pixel, its gap another.
            var inset = DottedInset / scale.DpiScaleX + half;
            var box = new Rect(inset, inset, width - 2 * inset, height - 2 * inset);
            if (box.Width >= 4 / scale.DpiScaleX && box.Height >= 4 / scale.DpiScaleX)
            {
                if (_dottedPen is null || !ReferenceEquals(_dottedPen.Brush, dotted) || _dottedPen.Thickness != 1 / scale.DpiScaleX)
                {
                    _dottedPen = new Pen(dotted, 1 / scale.DpiScaleX) { DashStyle = DashStyles.Dot, DashCap = PenLineCap.Square };
                    _dottedPen.Freeze();
                }

                drawing.DrawRectangle(null, _dottedPen, box);
            }
        }

        drawing.Pop();
    }

    // The bevel just inside the outline, then the outline itself (lit from the top left when the lower brush differs).
    private void DrawOutline(DrawingContext drawing, double width, double height, double radius, DpiScale scale, double half)
    {
        if (BevelBrush is SolidColorBrush { Color.A: > 0 } bevel)
        {
            // The top and left edges just inside the outline, turning round the top-left corner as the outline does.
            var (inner, r) = (1 / scale.DpiScaleX + half, Math.Max(0, radius - 1));
            var edge = new StreamGeometry();
            using (var context = edge.Open())
            {
                context.BeginFigure(new Point(inner, height - inner - r), false, false);
                context.LineTo(new Point(inner, inner + r), true, false);
                if (r > 0)
                    context.ArcTo(new Point(inner + r, inner), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(width - inner - r, inner), true, false);
            }

            edge.Freeze();
            drawing.DrawGeometry(null, PenFor(ref _bevelPen, bevel), edge);
        }

        if (BorderBrush is { } outline)
        {
            var (r, line) = (Math.Max(0, radius - half), new Rect(half, half, width - 2 * half, height - 2 * half));
            if (LowerBorderBrush is { } lower && !SameColour(lower, outline))
                DrawLitOutline(drawing, line, r, PenFor(ref _outlinePen, outline, true), PenFor(ref _lowerPen, lower, true));
            else
                drawing.DrawRoundedRectangle(null, PenFor(ref _outlinePen, outline, true), line, r, r);
        }
    }

    private static bool SameColour(Brush a, Brush b) =>
        ReferenceEquals(a, b) || (a is SolidColorBrush first && b is SolidColorBrush second && first.Color == second.Color);

    /// <summary>
    /// The outline in two halves along its centre line: the upper pen up the left side and along the top, the lower pen
    /// down the right side and along the bottom, meeting halfway round the top-right and bottom-left corners (at the
    /// corner pixels themselves when square, where the lower half, drawn last, has them, as classic bevels did).
    /// </summary>
    private static void DrawLitOutline(DrawingContext drawing, Rect line, double r, Pen upper, Pen lower)
    {
        // Halfway round a corner's arc: 45 degrees from either of its ends.
        var diagonal = r * (1 - Math.Sqrt(0.5));
        var topRight = new Point(line.Right - diagonal, line.Top + diagonal);
        var bottomLeft = new Point(line.Left + diagonal, line.Bottom - diagonal);
        var arc = new Size(r, r);

        var lit = new StreamGeometry();
        using (var path = lit.Open())
        {
            path.BeginFigure(bottomLeft, false, false);
            if (r > 0)
                path.ArcTo(new Point(line.Left, line.Bottom - r), arc, 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(line.Left, line.Top + r), true, false);
            if (r > 0)
                path.ArcTo(new Point(line.Left + r, line.Top), arc, 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(line.Right - r, line.Top), true, false);
            if (r > 0)
                path.ArcTo(topRight, arc, 0, false, SweepDirection.Clockwise, true, false);
        }

        var shade = new StreamGeometry();
        using (var path = shade.Open())
        {
            path.BeginFigure(topRight, false, false);
            if (r > 0)
                path.ArcTo(new Point(line.Right, line.Top + r), arc, 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(line.Right, line.Bottom - r), true, false);
            if (r > 0)
                path.ArcTo(new Point(line.Right - r, line.Bottom), arc, 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(line.Left + r, line.Bottom), true, false);
            if (r > 0)
                path.ArcTo(bottomLeft, arc, 0, false, SweepDirection.Clockwise, true, false);
        }

        lit.Freeze();
        shade.Freeze();
        drawing.DrawGeometry(null, upper, lit);
        drawing.DrawGeometry(null, lower, shade);
    }
}
