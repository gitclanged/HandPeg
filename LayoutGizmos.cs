using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HandPegApp;

/// <summary>
/// The handles on a layer's box in the layout pane: the resize grip on its bottom-right corner, the rotation handle on a
/// stalk above its top edge, and its anchor point (the dot it turns and grows about). Living in the adorner layer, they
/// follow the box wherever it is moved and however it is turned, and lie above every box.
/// </summary>
internal sealed class LayerGizmoAdorner : Adorner
{
    private const double Stalk = 22;

    private readonly VisualCollection _visuals;
    private readonly Thumb _grip, _turn, _anchor;
    private readonly Line _stalk;
    private readonly Func<Point> _anchorAt;

    /// <summary>The corner grip dragged, with the distance moved in screen units.</summary>
    public event DragDeltaEventHandler? ResizeDelta;

    public event DragStartedEventHandler? ResizeStarted;

    /// <summary>The rotation handle dragged: the pointer is read by the window, against the anchor point.</summary>
    public event Action? TurnStarted;

    public event Action? TurnDelta;

    /// <summary>The anchor dot dragged: the pointer is read by the window, inside the box.</summary>
    public event Action? AnchorStarted;

    public event Action? AnchorDelta;

    /// <param name="anchorAt">Where the anchor point is, as fractions of the box's width and height.</param>
    public LayerGizmoAdorner(FrameworkElement box, Style gripStyle, string name, Func<Point> anchorAt) : base(box)
    {
        _anchorAt = anchorAt;
        _grip = new Thumb { Style = gripStyle, Cursor = Cursors.SizeNWSE, ToolTip = "Drag to resize about the anchor point. Ctrl: the opposite corner stays where it is." };
        System.Windows.Automation.AutomationProperties.SetName(_grip, $"Resize {name}");
        _grip.DragDelta += (_, e) => ResizeDelta?.Invoke(this, e);
        _grip.DragStarted += (_, e) => ResizeStarted?.Invoke(this, e);

        _turn = new Thumb { Width = 12, Height = 12, Cursor = Cursors.Hand, Template = RoundTemplate(Brushes.White, Brushes.DodgerBlue), ToolTip = "Drag to turn the layer about its anchor point. Shift: in steps of 15 degrees." };
        System.Windows.Automation.AutomationProperties.SetName(_turn, $"Rotate {name}");
        _turn.DragStarted += (_, _) => TurnStarted?.Invoke();
        _turn.DragDelta += (_, _) => TurnDelta?.Invoke();

        _anchor = new Thumb { Width = 11, Height = 11, Cursor = Cursors.Cross, Template = RoundTemplate(Brushes.Gold, Brushes.Black), ToolTip = "The anchor point: the layer turns and grows about it. Drag it to move it; the layer stays where it is." };
        System.Windows.Automation.AutomationProperties.SetName(_anchor, $"Anchor Point of {name}");
        _anchor.DragStarted += (_, _) => AnchorStarted?.Invoke();
        _anchor.DragDelta += (_, _) => AnchorDelta?.Invoke();

        _stalk = new Line { Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false, Opacity = 0.8 };
        _visuals = new VisualCollection(this) { _stalk, _grip, _turn, _anchor };
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
        foreach (var thumb in new[] { _grip, _turn, _anchor })
            thumb.Measure(constraint);
        _stalk.Measure(constraint);
        return base.MeasureOverride(constraint);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = AdornedElement.RenderSize;
        _grip.Arrange(new Rect(size.Width - _grip.Width / 2, size.Height - _grip.Height / 2, _grip.Width, _grip.Height));
        _turn.Arrange(new Rect(size.Width / 2 - _turn.Width / 2, -Stalk - _turn.Height / 2, _turn.Width, _turn.Height));
        (_stalk.X1, _stalk.Y1, _stalk.X2, _stalk.Y2) = (size.Width / 2, 0, size.Width / 2, -Stalk + _turn.Height / 2);
        _stalk.Arrange(new Rect(0, 0, Math.Max(size.Width, 1), Math.Max(size.Height, 1)));
        var anchor = _anchorAt();
        _anchor.Arrange(new Rect(anchor.X * size.Width - _anchor.Width / 2, anchor.Y * size.Height - _anchor.Height / 2, _anchor.Width, _anchor.Height));
        return finalSize;
    }
}

/// <summary>
/// What the layout pane draws over its boxes: the guides a dragged box has snapped to (the frame's middle, the action-safe
/// and title-safe margins), and the selected clip's motion path (where its anchor point goes over time, sampled as the
/// export eases it) with a dot at each of its place keyframes. Only the dots answer the pointer: a press anywhere else
/// goes through to the boxes under it. Drawn in OnRender.
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

    // A dot being dragged: its keyframe's moment, where the pointer took it, and how far it has gone.
    private (double Time, Point From, Vector Moved)? _drag;

    /// <summary>A dot let go elsewhere: its keyframe's moment (the clip's own seconds) and how far it moved, in pixels of the canvas.</summary>
    public event Action<double, Vector>? DotMoved;

    /// <summary>A dot clicked without moving it: the playhead goes to its keyframe.</summary>
    public event Action<double>? DotClicked;

    public LayoutGuidesView() => Cursor = Cursors.SizeAll;

    /// <summary>The motion path (points on the canvas) and its keyframes' dots; none to draw none.</summary>
    public void ShowPath(IReadOnlyList<Point> path, IReadOnlyList<(double Time, Point At)> dots)
    {
        (_path, _dots) = (path, dots);
        InvalidateVisual();
    }

    /// <summary>The guides a dragged box has snapped to, as lines across the canvas; none while nothing is snapped.</summary>
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
