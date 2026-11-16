using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Services.Gpu;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// Color Studio curves: Master, Red, Green and Blue, selected by pills above a square graph. Each curve is the monotone cubic Hermite the
/// GPU table is baked from (GpuColorTables.CurveSamples, so the drawing matches the bake exactly). Click adds a point and drags it; drag
/// moves a point (end points vertically only; Shift locks an axis); dragging off the graph or double-clicking removes it. The pre-grade
/// luma histogram renders underneath (<see cref="SetHistogram"/>). A gesture spans <see cref="EditStarted"/> .. <see cref="EditEnded"/>;
/// each point change raises <see cref="CurveChanged"/> with the curve text ("x/y x/y ...", empty for identity). Pointer logic lives in
/// point methods driven by goldens (<see cref="CurveSurface"/>).
/// </summary>
public sealed class ColorCurvesControl : Grid
{
    /// <summary>Pill labels in curve order (matches ColorGradeKeys.Curves: Master, Red, Green, Blue).</summary>
    internal static readonly string[] ChannelNames = ["Master", "Red", "Green", "Blue"];

    private readonly ToggleButton[] _pills = new ToggleButton[4];

    public ColorCurvesControl()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Surface = new CurveSurface();

        var pills = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        for (var i = 0; i < 4; i++)
        {
            var channel = i;
            var pill = _pills[i] = new ToggleButton
            {
                Content = ChannelNames[i], Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), IsChecked = i == 0,
                ToolTip = $"{ChannelNames[i]} curve",
            };
            AutomationProperties.SetName(pill, ChannelNames[i] + " Curve");
            pill.Click += (_, _) => Channel = channel;
            pills.Children.Add(pill);
        }

        var reset = new Button { Content = "Reset", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Reset this curve" };
        AutomationProperties.SetName(reset, "Reset Curve");
        reset.Click += (_, _) => Surface.ResetChannel();
        pills.Children.Add(reset);
        Children.Add(pills);

        SetRow(Surface, 1);
        Children.Add(Surface);
        Surface.CurveChanged += (channel, text) => CurveChanged?.Invoke(channel, text);
        Surface.EditStarted += () => EditStarted?.Invoke();
        Surface.EditEnded += () => EditEnded?.Invoke();
    }

    /// <summary>The graph surface (exposes point methods for goldens).</summary>
    internal CurveSurface Surface { get; }

    /// <summary>Active curve: 0 Master, 1 Red, 2 Green, 3 Blue.</summary>
    public int Channel
    {
        get => Surface.Channel;
        set
        {
            Surface.Channel = Math.Max(0, Math.Min(value, 3));
            for (var i = 0; i < 4; i++)
                _pills[i].IsChecked = i == Surface.Channel;
        }
    }

    /// <summary>True during a gesture (the pane does not overwrite the curves meanwhile).</summary>
    public bool IsDragging => Surface.IsDragging;

    /// <summary>Raised when a curve's points change (channel index, curve text).</summary>
    public event Action<int, string>? CurveChanged;

    /// <summary>Raised when a gesture begins; changes until EditEnded form one step.</summary>
    public event Action? EditStarted;

    /// <summary>Raised when the gesture ends.</summary>
    public event Action? EditEnded;

    /// <summary>Sets all four curve texts from the card (Master, Red, Green, Blue); redraws without raising events.</summary>
    public void SetCurves(string master, string red, string green, string blue) => Surface.SetCurves([master, red, green, blue]);

    /// <summary>Sets the pre-grade luma histogram (256 bins) drawn beneath the curves; null clears it.</summary>
    public void SetHistogram(int[]? luma) => Surface.SetHistogram(luma);

    /// <summary>Curve graph: curves, control points and histogram rendered in a single OnRender.</summary>
    internal sealed class CurveSurface : FrameworkElement
    {
        private const double Pad = 6, PointRadius = 4, PointReach = 9, RemoveBeyond = 24, LeastGap = 0.01, AxisLockDip = 3;

        private static readonly Brush[] ChannelBrushes = [Solid(255, 0xE8, 0xE8, 0xE8), Solid(255, 0xFF, 0x5A, 0x5A), Solid(255, 0x5A, 0xE0, 0x6A), Solid(255, 0x5A, 0x9A, 0xFF)];
        private static readonly Pen[] ChannelPens = [.. ChannelBrushes.Select(b => Frozen(new Pen(b, 1.6) { LineJoin = PenLineJoin.Round }))];
        private static readonly Pen[] FaintPens = [.. ChannelBrushes.Select(b => Frozen(new Pen(Faded(b, 90), 1)))];
        private static readonly Pen GridPen = Frozen(new Pen(Solid(40, 0x90, 0x90, 0x90), 1)), DiagonalPen = Frozen(new Pen(Solid(70, 0x90, 0x90, 0x90), 1) { DashStyle = DashStyles.Dash });
        private static readonly Pen PointEdge = Frozen(new Pen(Solid(255, 0x10, 0x10, 0x12), 1));
        private static readonly Brush HistogramFill = Solid(46, 0xC8, 0xC8, 0xC8), HeldFill = Solid(255, 0xFF, 0xC8, 0x40);

        private readonly string[] _texts = ["", "", "", ""];
        private Brush _inset = Solid(255, 0x16, 0x16, 0x18);
        private Pen _edge = Frozen(new Pen(Solid(255, 0x2A, 0x2A, 0x30), 1));
        private int[]? _histogram;
        private StreamGeometry? _histogramShape;
        private Size _histogramSize;

        // Gesture state: working point list (ends included), held index, press point and origin, axis lock, off-graph flag.
        private List<(double X, double Y)>? _points;
        private int _held = -1, _axis;
        private Point _from;
        private (double X, double Y) _heldFrom;
        private bool _off;
        private TouchDevice? _touch;
        private int _channel;

        public CurveSurface()
        {
            Focusable = false;
            SnapsToDevicePixels = true;
            ToolTip = "Click to add a point; drag it off to remove";
            AutomationProperties.SetName(this, "Curves");
            Loaded += (_, _) =>
            {
                Services.ThemeManager.Applied += ReadLook;
                ReadLook();
            };
            Unloaded += (_, _) =>
            {
                Services.ThemeManager.Applied -= ReadLook;
                EndDrag();
            };
        }

        public int Channel
        {
            get => _channel;
            set
            {
                if (_channel == value)
                    return;
                EndDrag();
                _channel = value;
                InvalidateVisual();
            }
        }

        public bool IsDragging => _held >= 0;

        public event Action<int, string>? CurveChanged;

        public event Action? EditStarted;

        public event Action? EditEnded;

        /// <summary>Current text of the curve at <paramref name="channel"/>.</summary>
        public string TextOf(int channel) => _texts[channel];

        public void SetCurves(string[] texts)
        {
            for (var i = 0; i < 4; i++)
            {
                // The curve being dragged keeps its gesture state (the card catches up as writes land).
                if (i == _channel && IsDragging)
                    continue;
                _texts[i] = texts[i] ?? "";
            }

            InvalidateVisual();
        }

        public void SetHistogram(int[]? luma)
        {
            _histogram = luma is { Length: 256 } ? luma : null;
            _histogramShape = null;
            InvalidateVisual();
        }

        // ----- Geometry -----

        private Rect Graph() => new(Pad, Pad, Math.Max(ActualWidth - 2 * Pad, 8), Math.Max(ActualHeight - 2 * Pad, 8));

        private Point At((double X, double Y) point)
        {
            var graph = Graph();
            return new Point(graph.X + point.X * graph.Width, graph.Y + (1 - point.Y) * graph.Height);
        }

        private (double X, double Y) ValueAt(Point p)
        {
            var graph = Graph();
            return (Clamp01((p.X - graph.X) / graph.Width), Clamp01(1 - (p.Y - graph.Y) / graph.Height));
        }

        private List<(double X, double Y)> PointsOf(int channel)
        {
            var points = GpuColorTables.CurvePoints(_texts[channel]);
            return points.Count >= 2 ? points : [(0, 0), (1, 1)];
        }

        private int Nearest(List<(double X, double Y)> points, Point p)
        {
            var (best, bestDistance) = (-1, PointReach);
            for (var i = 0; i < points.Count; i++)
            {
                var distance = (At(points[i]) - p).Length;
                if (distance <= bestDistance)
                    (best, bestDistance) = (i, distance);
            }

            return best;
        }

        // ----- Gesture (point methods shared by mouse, pen, touch and goldens) -----

        /// <summary>Press at <paramref name="p"/>: grabs the nearby point or inserts and grabs a new one; false off the graph or too close.</summary>
        public bool BeginDrag(Point p)
        {
            var graph = Graph();
            graph.Inflate(PointReach, PointReach);
            if (!graph.Contains(p))
                return false;

            EndDrag();
            var points = PointsOf(_channel);
            var held = Nearest(points, p);
            var added = false;
            if (held < 0)
            {
                var value = ValueAt(p);
                var at = points.FindIndex(q => q.X > value.X);
                if (at <= 0 || value.X - points[at - 1].X < LeastGap || points[at].X - value.X < LeastGap)
                    return false;
                points.Insert(at, value);
                (held, added) = (at, true);
            }

            (_points, _held, _from, _heldFrom, _axis, _off) = (points, held, p, points[held], 0, false);
            EditStarted?.Invoke();
            if (added)
                Changed();
            return true;
        }

        /// <summary>Moves the held point with the pointer (end points vertical only, inner points between neighbours; optional axis lock).</summary>
        public void DragTo(Point p, bool lockAxis = false)
        {
            if (_points is not { } points || _held < 0)
                return;

            var (dx, dy) = (p.X - _from.X, p.Y - _from.Y);
            if (lockAxis)
            {
                if (_axis == 0 && Math.Max(Math.Abs(dx), Math.Abs(dy)) >= AxisLockDip)
                    _axis = Math.Abs(dx) >= Math.Abs(dy) ? 1 : 2;
                (dx, dy) = _axis == 1 ? (dx, 0.0) : _axis == 2 ? (0.0, dy) : (0.0, 0.0);
            }
            else
            {
                _axis = 0;
            }

            var graph = Graph();
            var end = _held == 0 || _held == points.Count - 1;
            var target = new Point(At(_heldFrom).X + dx, At(_heldFrom).Y + dy);

            // Inner points dragged beyond RemoveBeyond preview as removed (deleted on release there); dragging back restores them.
            var outside = target.X < graph.Left - RemoveBeyond || target.X > graph.Right + RemoveBeyond || target.Y < graph.Top - RemoveBeyond || target.Y > graph.Bottom + RemoveBeyond;
            var off = !end && outside;
            var (x, y) = ValueAt(target);
            if (end)
                x = points[_held].X;
            else
                x = Math.Max(points[_held - 1].X + LeastGap, Math.Min(x, points[_held + 1].X - LeastGap));

            if (off == _off && points[_held] == (x, y))
                return;
            _off = off;
            points[_held] = (x, y);
            Changed();
        }

        /// <summary>Ends the gesture; a point released off the graph is removed.</summary>
        public void EndDrag()
        {
            if (_points is not { } points || _held < 0)
                return;

            if (_off)
            {
                points.RemoveAt(_held);
                _off = false;
                _held = -1;
                Changed();
            }

            (_points, _held, _axis) = (null, -1, 0);
            EditEnded?.Invoke();
            InvalidateVisual();
        }

        /// <summary>Double-click at <paramref name="p"/>: removes the inner point there as one gesture; false when none.</summary>
        public bool RemoveAt(Point p)
        {
            var points = PointsOf(_channel);
            var at = Nearest(points, p);
            if (at <= 0 || at >= points.Count - 1)
                return false;

            EndDrag();
            EditStarted?.Invoke();
            points.RemoveAt(at);
            _texts[_channel] = TextFor(points);
            CurveChanged?.Invoke(_channel, _texts[_channel]);
            EditEnded?.Invoke();
            InvalidateVisual();
            return true;
        }

        /// <summary>Resets the active curve to identity as one gesture.</summary>
        public void ResetChannel()
        {
            if (_texts[_channel].Length == 0)
                return;
            EndDrag();
            EditStarted?.Invoke();
            _texts[_channel] = "";
            CurveChanged?.Invoke(_channel, "");
            EditEnded?.Invoke();
            InvalidateVisual();
        }

        // Recomputes the active curve text from the working points (excluding an off-graph point) and raises CurveChanged.
        private void Changed()
        {
            if (_points is not { } points)
                return;
            var kept = _off && _held >= 0 ? points.Where((_, i) => i != _held).ToList() : points;
            _texts[_channel] = TextFor(kept);
            InvalidateVisual();
            CurveChanged?.Invoke(_channel, _texts[_channel]);
        }

        /// <summary>Curve text: empty for identity (only the two diagonal end points), else the formatted points.</summary>
        internal static string TextFor(IReadOnlyList<(double X, double Y)> points) =>
            points.Count == 2 && points[0] == (0, 0) && points[1] == (1, 1) ? "" : GpuColorTables.FormatCurve(points);

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var p = e.GetPosition(this);
            if (e.ClickCount == 2)
            {
                RemoveAt(p);
                e.Handled = true;
                return;
            }

            if (BeginDrag(p))
            {
                CaptureMouse();
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured && _touch is null && e.LeftButton == MouseButtonState.Pressed)
                DragTo(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsMouseCaptured)
                return;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_touch is null)
                EndDrag();
        }

        protected override void OnTouchDown(TouchEventArgs e)
        {
            base.OnTouchDown(e);
            if (_touch is not null || !BeginDrag(e.GetTouchPoint(this).Position))
                return;
            _touch = e.TouchDevice;
            CaptureTouch(e.TouchDevice);
            e.Handled = true;
        }

        protected override void OnTouchMove(TouchEventArgs e)
        {
            base.OnTouchMove(e);
            if (!ReferenceEquals(e.TouchDevice, _touch))
                return;
            DragTo(e.GetTouchPoint(this).Position, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }

        protected override void OnTouchUp(TouchEventArgs e)
        {
            base.OnTouchUp(e);
            if (!ReferenceEquals(e.TouchDevice, _touch))
                return;
            ReleaseTouchCapture(e.TouchDevice);
            _touch = null;
            EndDrag();
            e.Handled = true;
        }

        protected override void OnLostTouchCapture(TouchEventArgs e)
        {
            base.OnLostTouchCapture(e);
            if (!ReferenceEquals(e.TouchDevice, _touch))
                return;
            _touch = null;
            EndDrag();
        }

        // ----- Rendering -----

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) ? Math.Max(availableSize.Width, 120) : 240;
            return new Size(width, Math.Max(Math.Min(width, 260), 120));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _histogramShape = null;
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var graph = Graph();
            drawing.DrawRectangle(_inset, _edge, graph);
            if (Histogram(graph) is { } shape)
                drawing.DrawGeometry(HistogramFill, null, shape);
            for (var i = 1; i < 4; i++)
            {
                drawing.DrawLine(GridPen, new Point(graph.X + graph.Width * i / 4, graph.Top), new Point(graph.X + graph.Width * i / 4, graph.Bottom));
                drawing.DrawLine(GridPen, new Point(graph.Left, graph.Y + graph.Height * i / 4), new Point(graph.Right, graph.Y + graph.Height * i / 4));
            }

            drawing.DrawLine(DiagonalPen, graph.BottomLeft, graph.TopRight);
            for (var i = 0; i < 4; i++)
            {
                if (i != _channel && _texts[i].Length > 0)
                    drawing.DrawGeometry(null, FaintPens[i], Curve(_texts[i], graph));
            }

            drawing.DrawGeometry(null, ChannelPens[_channel], Curve(_texts[_channel], graph));
            var points = _points ?? PointsOf(_channel);
            for (var i = 0; i < points.Count; i++)
            {
                if (i == _held && _off)
                    continue;
                drawing.DrawEllipse(i == _held ? HeldFill : ChannelBrushes[_channel], PointEdge, At(points[i]), PointRadius, PointRadius);
            }
        }

        // Frozen polyline of 128 samples from the same Hermite the GPU table uses (GpuColorTables.CurveSamples).
        private static StreamGeometry Curve(string text, Rect graph)
        {
            var samples = GpuColorTables.CurveSamples(text, 128);
            var shape = new StreamGeometry();
            using (var context = shape.Open())
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var point = new Point(graph.X + graph.Width * i / (samples.Length - 1), graph.Y + (1 - samples[i]) * graph.Height);
                    if (i == 0)
                        context.BeginFigure(point, false, false);
                    else
                        context.LineTo(point, true, false);
                }
            }

            shape.Freeze();
            return shape;
        }

        // Filled histogram across the graph (sqrt-scaled heights so sparse shadow bins stay visible); cached until data or size changes.
        private StreamGeometry? Histogram(Rect graph)
        {
            if (_histogram is not { } counts)
                return null;
            if (_histogramShape is { } kept && _histogramSize == graph.Size)
                return kept;

            var most = Math.Max(counts.Max(), 1);
            var shape = new StreamGeometry();
            using (var context = shape.Open())
            {
                context.BeginFigure(graph.BottomLeft, true, true);
                for (var i = 0; i < 256; i++)
                    context.LineTo(new Point(graph.X + graph.Width * (i + 0.5) / 256, graph.Bottom - Math.Sqrt(counts[i] / (double)most) * graph.Height * 0.9), false, false);
                context.LineTo(graph.BottomRight, false, false);
            }

            shape.Freeze();
            (_histogramShape, _histogramSize) = (shape, graph.Size);
            return shape;
        }

        private void ReadLook()
        {
            if (TryFindResource("InsetBrush") is Brush inset)
                _inset = (Brush)inset.GetCurrentValueAsFrozen();
            if (TryFindResource("InspectorCardBorderBrush") is Brush edge)
                _edge = Frozen(new Pen((Brush)edge.GetCurrentValueAsFrozen(), 1));
            InvalidateVisual();
        }

        private static double Clamp01(double v) => double.IsFinite(v) ? Math.Max(0, Math.Min(v, 1)) : 0;

        private static Brush Faded(Brush brush, byte alpha) => brush is SolidColorBrush solid ? Solid(alpha, solid.Color.R, solid.Color.G, solid.Color.B) : brush;
    }

    private static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

}
