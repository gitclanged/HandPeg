using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>Clip box corner, in the same order as <see cref="LayerGizmoAdorner.TiltedCorners"/>.</summary>
internal enum GripCorner
{
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
}

/// <summary>
/// Clip box handles on the Canvas pane and the player: a bottom-right resize grip, a rotation handle on a stalk above the top edge,
/// and the anchor dot (rotation and scale pivot). As an adorner they track the box's position and rotation and render above all
/// boxes. If the stalk would leave the surface (box at the frame top), it folds down into the box so the handle stays reachable.
/// During rotation, a ring around the anchor and an angle readout are shown. Tilted clips (<see cref="TiltedCorners"/>) get grips on
/// all four drawn corners and a stalk off the drawn top edge; grip drags are reported as the equivalent flat-box corner delta
/// (<see cref="FlatDelta"/>). Flat clips keep the single bottom-right grip.
/// </summary>
internal sealed class LayerGizmoAdorner : Adorner
{
    private const double Stalk = 22;

    private readonly VisualCollection _visuals;
    private readonly Thumb _turn, _anchor;

    // One grip per corner in GripCorner order; flat clips show only the bottom-right one.
    private readonly Thumb[] _grips = new Thumb[4];
    private readonly Line _stalk;
    private readonly Ellipse _ring;
    private readonly Border _readout;
    private readonly TextBlock _readoutText;
    private readonly Func<Point> _anchorAt;
    private bool _turning;

    /// <summary>Corner grip drag: the corner and the flat-box corner delta (box-local frame, screen units).</summary>
    public event Action<GripCorner, Vector>? ResizeDelta;

    public event Action<GripCorner>? ResizeStarted;

    /// <summary>Rotation handle drag started; the host reads the pointer relative to the anchor point.</summary>
    public event Action? TurnStarted;

    public event Action? TurnDelta;

    /// <summary>Anchor dot drag started; the host reads the pointer within the box.</summary>
    public event Action? AnchorStarted;

    public event Action? AnchorDelta;

    /// <param name="anchorAt">Anchor point as fractions (0..1) of the box width and height.</param>
    public LayerGizmoAdorner(FrameworkElement box, Style gripStyle, string name, Func<Point> anchorAt) : base(box)
    {
        _anchorAt = anchorAt;
        _ring = new Ellipse { Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5, StrokeDashArray = [4, 3], IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _readoutText = new TextBlock { Foreground = Brushes.White, FontSize = 11 };
        _readout = new Border
        {
            Background = ReadoutBack, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 2), Child = _readoutText,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };

        // Bottom-right is the flat-clip grip (legacy name and tooltip kept); the other three appear only for tilted clips.
        for (var k = 0; k < _grips.Length; k++)
        {
            var corner = (GripCorner)k;
            var words = CornerWords(corner);
            var grip = _grips[k] = new Thumb
            {
                Style = gripStyle,
                Cursor = corner is GripCorner.TopLeft or GripCorner.BottomRight ? Cursors.SizeNWSE : Cursors.SizeNESW,
                ToolTip = corner == GripCorner.BottomRight
                    ? "Drag to resize about the anchor point. Ctrl: the opposite corner stays where it is."
                    : $"Drag the {words.ToLowerInvariant()} corner, where the tilted picture is drawn, to resize about the anchor point. Ctrl: the opposite corner stays where it is.",
                Visibility = corner == GripCorner.BottomRight ? Visibility.Visible : Visibility.Collapsed,
            };
            System.Windows.Automation.AutomationProperties.SetName(grip, corner == GripCorner.BottomRight ? $"Resize {name}" : $"Resize {name} from Its {words} Corner");
            grip.DragDelta += (_, e) => ResizeDelta?.Invoke(corner, Flattened(e, corner));
            grip.DragStarted += (_, _) => ResizeStarted?.Invoke(corner);
        }

        _turn = new Thumb { Width = 12, Height = 12, Cursor = Cursors.Hand, Template = RoundTemplate(Brushes.White, Brushes.DodgerBlue), ToolTip = "Drag to turn the clip about its anchor point. Shift: in steps of 15 degrees." };
        System.Windows.Automation.AutomationProperties.SetName(_turn, $"Rotate {name}");
        _turn.DragStarted += (_, _) =>
        {
            _turning = true;
            TurnStarted?.Invoke();
            InvalidateArrange();
        };
        _turn.DragDelta += (_, _) => TurnDelta?.Invoke();
        _turn.DragCompleted += (_, _) =>
        {
            _turning = false;
            (_ring.Visibility, _readout.Visibility) = (Visibility.Collapsed, Visibility.Collapsed);
        };

        _anchor = new Thumb { Width = 11, Height = 11, Cursor = Cursors.Cross, Template = RoundTemplate(Brushes.Gold, Brushes.Black), ToolTip = "The anchor point: the clip turns and grows about it. Drag it to move it; the clip stays where it is." };
        System.Windows.Automation.AutomationProperties.SetName(_anchor, $"Anchor Point of {name}");
        _anchor.DragStarted += (_, _) => AnchorStarted?.Invoke();
        _anchor.DragDelta += (_, _) => AnchorDelta?.Invoke();

        _stalk = new Line { Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false, Opacity = 0.8 };
        _visuals = new VisualCollection(this) { _ring, _stalk };
        foreach (var grip in _grips)
            _visuals.Add(grip);
        _visuals.Add(_turn);
        _visuals.Add(_anchor);
        _visuals.Add(_readout);
    }

    private static string CornerWords(GripCorner corner) => corner switch
    {
        GripCorner.TopLeft => "Top Left",
        GripCorner.TopRight => "Top Right",
        GripCorner.BottomRight => "Bottom Right",
        _ => "Bottom Left",
    };

    /// <summary>Box sides a corner lies on: (0, 0) top-left through (1, 1) bottom-right.</summary>
    internal static (int X, int Y) SidesOf(GripCorner corner) => corner switch
    {
        GripCorner.TopLeft => (0, 0),
        GripCorner.TopRight => (1, 0),
        GripCorner.BottomRight => (1, 1),
        _ => (0, 1),
    };

    private static readonly SolidColorBrush ReadoutBack = FrozenBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x14));

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Provider of a tilted clip's four drawn corners (TL, TR, BR, BL) in surface coordinates (MainWindow.TiltedCorners); null provider
    /// or null result means a flat clip with handles on its box.
    /// </summary>
    public Func<PointCollection?>? TiltedCorners { get; set; }

    // Tilted corners in the box's local (unrotated) frame at its current placement; null for a flat clip.
    private Point[]? LocalCorners(Size size)
    {
        if (TiltedCorners?.Invoke() is not { Count: 4 } corners)
            return null;
        var (left, top) = (Canvas.GetLeft(AdornedElement), Canvas.GetTop(AdornedElement));
        if (double.IsNaN(left) || double.IsNaN(top))
            return null;

        // Inverse of HandleInside's mapping: subtract box placement, then undo rotation about the render origin.
        var origin = new Point(AdornedElement.RenderTransformOrigin.X * size.Width, AdornedElement.RenderTransformOrigin.Y * size.Height);
        var back = AdornedElement.RenderTransform.Inverse;
        var local = new Point[4];
        for (var k = 0; k < 4; k++)
        {
            var relative = new Point(corners[k].X - left - origin.X, corners[k].Y - top - origin.Y);
            var turned = back?.Transform(relative) ?? relative;
            local[k] = new Point(turned.X + origin.X, turned.Y + origin.Y);
        }

        return local;
    }

    private Vector Flattened(DragDeltaEventArgs e, GripCorner corner)
    {
        var moved = new Vector(e.HorizontalChange, e.VerticalChange);
        return LocalCorners(AdornedElement.RenderSize) is { } corners ? FlatDelta(corners, AdornedElement.RenderSize, moved, corner) : moved;
    }

    /// <summary>
    /// Converts a drawn-corner move <paramref name="moved"/> on a tilted clip into the flat-box corner delta (box-local frame) for
    /// <paramref name="corner"/> (default bottom-right). The two drawn edges meeting at the corner, per box pixel, form a local linear basis
    /// (perspective is near-linear at the corner); solving in that basis yields the flat delta. Flat boxes map 1:1; near-degenerate
    /// (edge-on) bases return the move unchanged.
    /// </summary>
    internal static Vector FlatDelta(Point[] corners, Size size, Vector moved, GripCorner corner = GripCorner.BottomRight)
    {
        var (right, bottom) = SidesOf(corner);
        var across = (bottom == 1 ? corners[2] - corners[3] : corners[1] - corners[0]) / Math.Max(size.Width, 1);
        var down = (right == 1 ? corners[2] - corners[1] : corners[3] - corners[0]) / Math.Max(size.Height, 1);
        var determinant = across.X * down.Y - across.Y * down.X;
        if (Math.Abs(determinant) < 0.05)
            return moved;
        return new Vector((moved.X * down.Y - moved.Y * down.X) / determinant, (across.X * moved.Y - across.Y * moved.X) / determinant);
    }

    /// <summary>Shows the rotation angle beside the handle while it is dragged.</summary>
    public void ShowAngle(double degrees)
    {
        _readoutText.Text = degrees.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "\u00B0";
        _readout.Visibility = _ring.Visibility = _turning ? Visibility.Visible : Visibility.Collapsed;
        InvalidateArrange();
    }

    /// <summary>
    /// True when the rotation handle must fold into the box because <paramref name="tip"/> would fall outside the parent surface.
    /// Uses the box's current Canvas.Left/Top and RenderTransform rather than last layout, since placement is set just before this
    /// adorner re-arranges.
    /// </summary>
    private bool HandleInside(Size size, Point tip)
    {
        if (VisualTreeHelper.GetParent(AdornedElement) is not FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } surface)
            return false;

        var (left, top) = (Canvas.GetLeft(AdornedElement), Canvas.GetTop(AdornedElement));
        if (double.IsNaN(left) || double.IsNaN(top))
            return false;

        var origin = new Point(AdornedElement.RenderTransformOrigin.X * size.Width, AdornedElement.RenderTransformOrigin.Y * size.Height);
        var turned = AdornedElement.RenderTransform.Transform(new Point(tip.X - origin.X, tip.Y - origin.Y));
        var (x, y) = (left + origin.X + turned.X, top + origin.Y + turned.Y);
        return x < 0 || y < 0 || x > surface.ActualWidth || y > surface.ActualHeight;
    }

    private static ControlTemplate RoundTemplate(Brush fill, Brush stroke)
    {
        var ellipse = new FrameworkElementFactory(typeof(Ellipse));
        ellipse.SetValue(Shape.FillProperty, fill);
        ellipse.SetValue(Shape.StrokeProperty, stroke);
        ellipse.SetValue(Shape.StrokeThicknessProperty, 1.5);
        var template = new ControlTemplate(typeof(Thumb)) { VisualTree = ellipse };
        template.Seal();
        return template;
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        foreach (var grip in _grips)
            grip.Measure(constraint);
        _turn.Measure(constraint);
        _anchor.Measure(constraint);
        _stalk.Measure(constraint);
        _ring.Measure(constraint);
        _readout.Measure(constraint);
        return base.MeasureOverride(constraint);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = AdornedElement.RenderSize;

        // Grip corners and stalk edge: the box itself (bottom-right grip only), or the tilted clip's drawn corners (four grips).
        var corners = LocalCorners(size);
        var (edgeFrom, edgeTo) = corners is null ? (new Point(0, 0), new Point(size.Width, 0)) : (corners[0], corners[1]);
        for (var k = 0; k < _grips.Length; k++)
        {
            var grip = _grips[k];
            var shown = corners is not null || (GripCorner)k == GripCorner.BottomRight;
            grip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (!shown)
                continue;

            var at = corners?[k] ?? new Point(size.Width, size.Height);
            grip.Arrange(new Rect(at.X - grip.Width / 2, at.Y - grip.Height / 2, grip.Width, grip.Height));
        }

        // Stalk from the top-edge midpoint along the outward normal (pointing away from the box centre); without room, fold inward by
        // at most a quarter of the height so it clears a centred anchor.
        var middle = new Point((edgeFrom.X + edgeTo.X) / 2, (edgeFrom.Y + edgeTo.Y) / 2);
        var edge = edgeTo - edgeFrom;
        var outward = edge.Length > 1e-6 ? new Vector(edge.Y, -edge.X) / edge.Length : new Vector(0, -1);
        var centre = corners is null ? new Point(size.Width / 2, size.Height / 2) : new Point(corners.Average(c => c.X), corners.Average(c => c.Y));
        if (Vector.Multiply(middle - centre, outward) < 0)
            outward = -outward;
        var reach = HandleInside(size, middle + outward * (Stalk + 7)) ? -Math.Min(Stalk, Math.Max(size.Height * 0.25, 9)) : Stalk;
        var handle = middle + outward * reach;
        _turn.Arrange(new Rect(handle.X - _turn.Width / 2, handle.Y - _turn.Height / 2, _turn.Width, _turn.Height));
        var stalkEnd = handle - outward * Math.Sign(reach) * _turn.Height / 2;
        (_stalk.X1, _stalk.Y1, _stalk.X2, _stalk.Y2) = (middle.X, middle.Y, stalkEnd.X, stalkEnd.Y);
        _stalk.Arrange(new Rect(0, 0, Math.Max(size.Width, 1), Math.Max(size.Height, 1)));
        var anchor = _anchorAt();
        var anchorPoint = new Point(anchor.X * size.Width, anchor.Y * size.Height);
        _anchor.Arrange(new Rect(anchorPoint.X - _anchor.Width / 2, anchorPoint.Y - _anchor.Height / 2, _anchor.Width, _anchor.Height));

        // Rotation feedback: ring around the anchor through the handle, and the angle readout beside the handle.
        var radius = Math.Max((handle - anchorPoint).Length, 8);
        _ring.Arrange(new Rect(anchorPoint.X - radius, anchorPoint.Y - radius, 2 * radius, 2 * radius));
        _readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _readout.Arrange(new Rect(new Point(handle.X + _turn.Width, handle.Y - _readout.DesiredSize.Height / 2), _readout.DesiredSize));
        return finalSize;
    }
}

/// <summary>
/// Canvas pane overlay drawn in OnRender: snap guides for a dragged box (frame centre, action-safe and title-safe margins) and the
/// selected clip's motion path (anchor trajectory sampled with export easing) with a dot per position keyframe. Only dots are
/// hit-testable; other presses pass through to the boxes beneath.
/// </summary>
internal sealed class LayoutGuidesView : FrameworkElement
{
    private const double DotRadius = 5;

    private static readonly Pen GuidePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x81))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen PathPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(220, 0xFF, 0xD5, 0x4F))), 1.5));
    private static readonly Pen DotPen = Frozen(new Pen(Brushes.Black, 1));

    private IReadOnlyList<Point> _path = [];
    private IReadOnlyList<(double Time, Point At)> _dots = [];
    private IReadOnlyList<(Point From, Point To)> _guides = [];

    // Active dot drag: keyframe time, press point and accumulated offset.
    private (double Time, Point From, Vector Moved)? _drag;

    /// <summary>Dot dropped at a new spot: keyframe time (clip-local seconds) and offset in canvas pixels.</summary>
    public event Action<double, Vector>? DotMoved;

    /// <summary>Dot clicked without a drag: the host seeks the playhead to that keyframe.</summary>
    public event Action<double>? DotClicked;

    // UI Automation exposes one named element; drawn guides and dots have no child peers.
    protected override AutomationPeer OnCreateAutomationPeer() => new GuidesPeer(this);

    private sealed class GuidesPeer(LayoutGuidesView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(LayoutGuidesView);

        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : "Canvas guides and motion path";

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }

    public LayoutGuidesView() => Cursor = Cursors.SizeAll;

    /// <summary>Sets the motion path (canvas points) and keyframe dots; empty lists clear them.</summary>
    public void ShowPath(IReadOnlyList<Point> path, IReadOnlyList<(double Time, Point At)> dots)
    {
        (_path, _dots) = (path, dots);
        InvalidateVisual();
    }

    /// <summary>Sets the snap guide lines for a dragged box; empty when nothing is snapped.</summary>
    public void ShowGuides(IReadOnlyList<(Point From, Point To)> guides)
    {
        if (guides.Count == 0 && _guides.Count == 0)
            return;

        _guides = guides;
        InvalidateVisual();
    }

    private int DotAt(Point point)
    {
        for (var i = _dots.Count - 1; i >= 0; i--)
        {
            if ((_dots[i].At - point).Length <= DotRadius + 2)
                return i;
        }

        return -1;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        _drag is not null || DotAt(hitTestParameters.HitPoint) >= 0 ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;

    protected override void OnRender(DrawingContext drawing)
    {
        foreach (var (from, to) in _guides)
            drawing.DrawLine(GuidePen, from, to);

        if (_path.Count > 1)
        {
            var line = new StreamGeometry();
            using (var path = line.Open())
            {
                path.BeginFigure(_path[0], false, false);
                for (var i = 1; i < _path.Count; i++)
                    path.LineTo(_path[i], true, false);
            }

            line.Freeze();
            drawing.DrawGeometry(null, PathPen, line);
        }

        foreach (var (time, at) in _dots)
        {
            var moved = _drag is { } drag && Math.Abs(drag.Time - time) < 1e-6 ? drag.Moved : default;
            drawing.DrawEllipse(Brushes.Gold, DotPen, at + moved, DotRadius, DotRadius);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (DotAt(point) is >= 0 and var index && CaptureMouse())
        {
            _drag = (_dots[index].Time, point, default);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is not { } drag)
            return;

        _drag = drag with { Moved = e.GetPosition(this) - drag.From };
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is not { } drag)
            return;

        _drag = null;
        ReleaseMouseCapture();
        if (drag.Moved.Length > 1.5)
            DotMoved?.Invoke(drag.Time, drag.Moved);
        else
            DotClicked?.Invoke(drag.Time);
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is null)
            return;

        _drag = null;
        InvalidateVisual();
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
