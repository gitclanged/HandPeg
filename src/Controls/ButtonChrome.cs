using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Single-element button face: fill, 1 px outline and bevel (light top/left edge just inside the outline) with theme corner
/// rounding, plus padded content. Replaces the templates' Grid of three Borders, which added hundreds of elements to measure and
/// render before Frame 1 on a window of a few hundred buttons (~7 ms measured); these lines are drawn with no extra elements.
///
/// Outline and bevel render over the fill and take no layout space, so size = padding + content (<see cref="OutlineTakesRoom"/>
/// reserves the outline pixel, as toggle buttons' Border did). BorderThickness 0 draws neither. Lines snap to device pixels.
///
/// The outline can be lit from the top left (<see cref="LowerBorderBrush"/>): BorderBrush on top/left, the lower brush on
/// bottom/right, for raised (light over dark) or sunken (dark over light) looks, split exactly at the top-right and bottom-left
/// corners for any shape. A gradient brush cannot (its split runs corner to corner, along a thin bar's bottom edge for many pixels),
/// so the lines are drawn here; text boxes, combo boxes, check boxes, lists, cards and wells use this element instead of a Border.
/// </summary>
public sealed class ButtonChrome : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ButtonChrome),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ButtonChrome)d).SurfaceChanged?.Invoke(d, EventArgs.Empty)));

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Outline colour (top and left only when <see cref="LowerBorderBrush"/> is set).</summary>
    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(Thickness), typeof(ButtonChrome),
        new FrameworkPropertyMetadata(new Thickness(1), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Any side above 0 draws the 1 px outline and the bevel; all zero draws neither.</summary>
    public Thickness BorderThickness
    {
        get => (Thickness)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public static readonly DependencyProperty BevelBrushProperty = DependencyProperty.Register(
        nameof(BevelBrush), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bevel colour; transparent or null for a flat button.</summary>
    public Brush? BevelBrush
    {
        get => (Brush?)GetValue(BevelBrushProperty);
        set => SetValue(BevelBrushProperty, value);
    }

    public static readonly DependencyProperty LowerBorderBrushProperty = DependencyProperty.Register(
        nameof(LowerBorderBrush), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Bottom/right outline brush for top-left lighting (theme ...LowerBrush tokens); null for a single-colour outline (flat
    /// themes). Triggers that recolour the outline (hover, focus, selected) clear it.
    /// </summary>
    public Brush? LowerBorderBrush
    {
        get => (Brush?)GetValue(LowerBorderBrushProperty);
        set => SetValue(LowerBorderBrushProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(ButtonChrome), new FrameworkPropertyMetadata(new CornerRadius(), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Corner rounding; only TopLeft is used, for all four corners (themes round them uniformly).</summary>
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

    /// <summary>True to reserve the outline pixel on each side in layout (toggle buttons); false to draw it over the fill (buttons).</summary>
    public bool OutlineTakesRoom
    {
        get => (bool)GetValue(OutlineTakesRoomProperty);
        set => SetValue(OutlineTakesRoomProperty, value);
    }

    public static readonly DependencyProperty DottedOutlineBrushProperty = DependencyProperty.Register(
        nameof(DottedOutlineBrush), typeof(Brush), typeof(ButtonChrome), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Classic 1 px dotted focus rectangle (1 on, 1 off, square corners) drawn over the content. Triggers set it to the theme's
    /// DottedFocusBrush when focused, checked or selected (Reno themes; transparent in flat themes). Drawn regardless of outline.
    /// </summary>
    public Brush? DottedOutlineBrush
    {
        get => (Brush?)GetValue(DottedOutlineBrushProperty);
        set => SetValue(DottedOutlineBrushProperty, value);
    }

    public static readonly DependencyProperty DottedInsetProperty = DependencyProperty.Register(
        nameof(DottedInset), typeof(double), typeof(ButtonChrome), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Dotted rectangle inset from the edge in device pixels (default 3, just inside a button outline).</summary>
    public double DottedInset
    {
        get => (double)GetValue(DottedInsetProperty);
        set => SetValue(DottedInsetProperty, value);
    }

    private bool HasOutline => BorderThickness is { Left: > 0 } or { Top: > 0 } or { Right: > 0 } or { Bottom: > 0 };

    // Content inset: padding plus the outline pixel when OutlineTakesRoom.
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

    // Cached pens, reused while the brush reference is unchanged (theme brushes are frozen, so same reference = same colour).
    private Pen? _outlinePen, _bevelPen, _lowerPen, _dottedPen;

    // Outline pens use square caps (extending half a pixel) so each half of a lit outline fully covers its corner pixels;
    // closed outlines have no ends and are unaffected.
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

        // Centre lines on device pixels: a 1 px line at x.5 covers exactly one pixel column.
        var scale = VisualTreeHelper.GetDpi(this);
        var half = 0.5 / scale.DpiScaleX;
        var guidelines = new GuidelineSet([0, width], [0, height]);
        guidelines.Freeze();
        drawing.PushGuidelineSet(guidelines);

        if (HasOutline)
            DrawOutline(drawing, width, height, radius, scale, half);

        if (dotted is not null)
        {
            // Pixel-aligned and square-cornered: a zero-length dash with square caps is one pixel on, one off.
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

    // Draws the bevel inside the outline, then the outline (two-tone when LowerBorderBrush differs).
    private void DrawOutline(DrawingContext drawing, double width, double height, double radius, DpiScale scale, double half)
    {
        if (BevelBrush is SolidColorBrush { Color.A: > 0 } bevel)
        {
            // Left and top edges one pixel inside the outline, following its top-left corner arc.
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
    /// Draws the outline in two halves: upper pen on left and top, lower pen on right and bottom, meeting at the midpoints of the
    /// top-right and bottom-left corner arcs. With square corners the lower half, drawn last, owns those corner pixels (classic bevel).
    /// </summary>
    private static void DrawLitOutline(DrawingContext drawing, Rect line, double r, Pen upper, Pen lower)
    {
        // Arc midpoint: 45 degrees from either end.
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

    // ----- Adaptive icon contrast (Pass 5.6.5) -----
    // Opt-in per icon (AdaptiveIcon on a glyph TextBlock or a Shape): its colour is kept at UiBrushes.IconContrast (3:1) against the
    // surface behind it, re-checked whenever the nearest ButtonChrome's fill changes (hover, press, check, theme) and whenever its own
    // colour is replaced (a theme's accent). The icon's own colour stays its tint: the adapted one is a current value (SetCurrentValue),
    // so a DynamicResource behind it keeps working. Faces without adaptive icons pay one null check per fill change.

    /// <summary>Raised when <see cref="Background"/> changes (adaptive icons inside re-check their contrast).</summary>
    public event EventHandler? SurfaceChanged;

    public static readonly DependencyProperty AdaptiveIconProperty = DependencyProperty.RegisterAttached(
        "AdaptiveIcon", typeof(bool), typeof(ButtonChrome), new PropertyMetadata(false, OnAdaptiveIconChanged));

    /// <summary>True on an icon (glyph TextBlock or Shape) whose colour keeps 3:1 contrast against the face behind it.</summary>
    public static bool GetAdaptiveIcon(DependencyObject element) => (bool)element.GetValue(AdaptiveIconProperty);

    public static void SetAdaptiveIcon(DependencyObject element, bool value) => element.SetValue(AdaptiveIconProperty, value);

    // Per-icon listeners while adaptive.
    private static readonly DependencyProperty IconStateProperty = DependencyProperty.RegisterAttached(
        "IconState", typeof(AdaptiveIconState), typeof(ButtonChrome), new PropertyMetadata(null));

    /// <summary>
    /// Marks every coloured glyph under <paramref name="root"/> (logical tree, so before it is shown): a TextBlock in an icon font
    /// (Segoe MDL2 / Fluent Icons) whose Foreground is set on it or by its style rather than inherited (accent-tinted tool icons).
    /// </summary>
    public static void AdaptIconsUnder(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock glyph && IsIconFont(glyph.FontFamily)
                && DependencyPropertyHelper.GetValueSource(glyph, TextBlock.ForegroundProperty).BaseValueSource is BaseValueSource.Local or BaseValueSource.Style)
                SetAdaptiveIcon(glyph, true);
            AdaptIconsUnder(child);
        }
    }

    private static bool IsIconFont(FontFamily? family) => family?.Source is { } name
        && (name.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || name.Contains("Fluent", StringComparison.OrdinalIgnoreCase));

    private static void OnAdaptiveIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement icon || IconPropertyOf(icon) is not { } property)
            return;

        if (e.NewValue is true)
        {
            if (icon.GetValue(IconStateProperty) is AdaptiveIconState)
                return;
            var state = new AdaptiveIconState(icon, property);
            icon.SetValue(IconStateProperty, state);
            state.Start();
        }
        else if (icon.GetValue(IconStateProperty) is AdaptiveIconState state)
        {
            state.Stop();
            icon.ClearValue(IconStateProperty);
        }
    }

    // The colour property an icon is drawn with: a glyph's Foreground, a shape's Fill (or Stroke when unfilled).
    private static DependencyProperty? IconPropertyOf(FrameworkElement icon) => icon switch
    {
        TextBlock => TextBlock.ForegroundProperty,
        System.Windows.Shapes.Shape { Fill: not null } => System.Windows.Shapes.Shape.FillProperty,
        System.Windows.Shapes.Shape => System.Windows.Shapes.Shape.StrokeProperty,
        _ => null,
    };

    /// <summary>One adaptive icon's listeners, attached only while it is loaded (so neither the face nor the descriptor keeps it alive).</summary>
    private sealed class AdaptiveIconState(FrameworkElement icon, DependencyProperty property)
    {
        private ButtonChrome? _face;
        private Color? _tint;
        private bool _writing;
        private System.ComponentModel.DependencyPropertyDescriptor? _descriptor;

        public void Start()
        {
            icon.Loaded += Icon_Loaded;
            icon.Unloaded += Icon_Unloaded;
            if (icon.IsLoaded)
                Attach();
        }

        public void Stop()
        {
            icon.Loaded -= Icon_Loaded;
            icon.Unloaded -= Icon_Unloaded;
            Detach();
            if (_tint is not null)
                icon.InvalidateProperty(property);
        }

        private void Icon_Loaded(object sender, RoutedEventArgs e) => Attach();

        private void Icon_Unloaded(object sender, RoutedEventArgs e) => Detach();

        private void Attach()
        {
            Detach();
            for (var at = VisualTreeHelper.GetParent(icon); at is not null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is ButtonChrome face)
                {
                    _face = face;
                    face.SurfaceChanged += Face_SurfaceChanged;
                    break;
                }
            }

            _descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, icon.GetType());
            _descriptor?.AddValueChanged(icon, Icon_ColorChanged);
            Adapt();
        }

        private void Detach()
        {
            if (_face is not null)
                _face.SurfaceChanged -= Face_SurfaceChanged;
            _face = null;
            _descriptor?.RemoveValueChanged(icon, Icon_ColorChanged);
            _descriptor = null;
        }

        private void Face_SurfaceChanged(object? sender, EventArgs e) => Adapt();

        // A colour written by someone else (a theme's accent, a trigger) is the new tint.
        private void Icon_ColorChanged(object? sender, EventArgs e)
        {
            if (_writing)
                return;
            _tint = null;
            Adapt();
        }

        private void Adapt()
        {
            if (_tint is null)
            {
                if (icon.GetValue(property) is not SolidColorBrush own)
                    return;
                _tint = own.Color;
            }

            var shown = UiBrushes.ContrastSafe(_tint.Value, UiBrushes.SurfaceBehind(VisualTreeHelper.GetParent(icon)));
            if (icon.GetValue(property) is SolidColorBrush current && current.Color == shown)
                return;

            _writing = true;
            try
            {
                icon.SetCurrentValue(property, UiBrushes.Frozen(shown));
            }
            finally
            {
                _writing = false;
            }
        }
    }
}
