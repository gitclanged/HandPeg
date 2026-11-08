using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>The tools the Studio draws with: the polygon pen, and the helpers that lay down a ready outline by a drag.</summary>
public enum CutoutTool { Pen, Rectangle, Ellipse, Slant }

public sealed partial class MaskDrawWindow
{
    /// <summary>
    /// The shapes over the still, and what the pointer does to them. Drawn in <see cref="OnRender"/>, in the source's pixels; lines
    /// and handles are sized by how far the view has scaled it, so they stay thin at 64x. Each shape belongs to a named piece.
    /// Nothing here cares how far the view is zoomed or panned: that happens around it, at any moment, even with a shape half drawn.
    /// </summary>
    private sealed class ShapeSurface : FrameworkElement
    {
        // An ellipse helper's outline: enough points for a smooth curve, few enough to drag by hand.
        private const int EllipsePoints = 32;

        // How far a slant helper leans: a bar's lean, as a share of its width.
        private const double SlantLean = 0.16;

        private readonly double _width, _height;
        private readonly List<(Rect Area, string Name, bool IsThis)> _regions;
        private readonly List<List<Point>> _shapes;
        private readonly List<int> _owners;
        private List<Point>? _drawing;
        private Point? _pointer;
        private (List<Point> Points, int Index)? _dragging;
        private (Point From, Point To)? _box;
        private (double Step, Rect Seen)? _grid;

        private static readonly Brush[] PieceFills = Frozen(0x55), PieceLines = Frozen(0xFF);
        private static readonly Pen[] PiecePens = PieceLines.Select(line => (Pen)new Pen(line, 1.5).GetAsFrozen()).ToArray();
        private static readonly Brush DrawingLine = Frozen(Color.FromRgb(0xFF, 0x8A, 0x3D));
        private static readonly Brush RegionLine = Frozen(Color.FromArgb(0xC0, 0xFF, 0xD0, 0x40));
        private static readonly Brush LabelBack = Frozen(Color.FromArgb(0xB0, 0, 0, 0));
        private static readonly Brush GridLine = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        private static readonly Brush FirstPointGlow = MakeGlow();

        public event Action? Changed;

        public ShapeSurface(double width, double height, List<(Rect, string, bool)> regions, List<(int Owner, List<Point> Points)> shapes)
        {
            (_width, _height, _regions) = (width, height, regions);
            (_shapes, _owners) = (shapes.Select(s => s.Points).ToList(), shapes.Select(s => s.Owner).ToList());
            (Width, Height) = (width, height);
            Cursor = Cursors.Cross;
            Focusable = true;
        }

        public CutoutTool Tool { get; set; } = CutoutTool.Pen;

        /// <summary>The piece new shapes go to.</summary>
        public int CurrentPiece { get; set; }

        public bool IsDrawing => _drawing is { Count: > 0 };
        public int PointsInShape => _drawing?.Count ?? 0;
        public int ShapeCount => _shapes.Count;
        public IReadOnlyList<List<Point>> Finished => _shapes;
        public IReadOnlyList<int> Owners => _owners;

        /// <summary>The colour a piece is drawn in.</summary>
        public static Brush LineOf(int piece) => PieceLines[Math.Abs(piece) % PieceLines.Length];

        /// <summary>A line in a piece's colour, for the Source Navigator's small outlines.</summary>
        public static Pen PenOf(int piece) => PiecePens[Math.Abs(piece) % PiecePens.Length];

        /// <summary>
        /// A view changed: the grid of source pixels to draw (pixels that step wide, over what is seen; null for none), and vertices at
        /// that zoom land on pixel corners. The pointer is where it is on the picture now: a live line follows it however the view moved.
        /// </summary>
        public void ViewChanged(double? pixelGrid, Rect seen)
        {
            _grid = pixelGrid is { } step ? (step, seen) : null;
            if (IsMouseOver || IsMouseCaptured)
                _pointer = Pointer();
            InvalidateVisual();
        }

        // ----- Undo (4.3E) -----
        // Before each change, the shapes as they were (the finished ones and the one being drawn): a point put down, a shape
        // closed or dropped, a point taken back, a point or shape removed, Clear All, one step each; a point dragged, one step
        // for the drag. The window is modal and works on a copy, kept only when it is confirmed: these steps are its own.
        private const int UndoDepth = 100;
        private readonly List<Drawn> _undo = [], _redo = [];
        private bool _dragKept;

        private sealed record Drawn(List<List<Point>> Shapes, List<int> Owners, List<Point>? Drawing);

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        private Drawn Capture() => new(_shapes.Select(s => s.ToList()).ToList(), [.. _owners], _drawing?.ToList());

        private void Remember()
        {
            _undo.Add(Capture());
            if (_undo.Count > UndoDepth)
                _undo.RemoveAt(0);
            _redo.Clear();
        }

        public void Undo() => Step(_undo, _redo);

        public void Redo() => Step(_redo, _undo);

        private void Step(List<Drawn> from, List<Drawn> to)
        {
            // Not while a point is held: the drag would go on moving a point of shapes that are gone.
            if (from.Count == 0 || _dragging is not null || _box is not null)
                return;

            to.Add(Capture());
            var state = from[^1];
            from.RemoveAt(from.Count - 1);
            _shapes.Clear();
            _shapes.AddRange(state.Shapes);
            _owners.Clear();
            _owners.AddRange(state.Owners);
            _drawing = state.Drawing;
            Redraw();
        }

        // How many of the source's pixels one pixel on screen is.
        private double OnePixel
        {
            get
            {
                var scale = Window.GetWindow(this) is { } window && window.IsAncestorOf(this)
                    ? TransformToAncestor(window).TransformBounds(new Rect(0, 0, 1, 1)).Width
                    : 1;
                return scale > 0 ? 1 / scale : 1;
            }
        }

        public void CloseShape() => CloseShape(remember: true);

        private void CloseShape(bool remember)
        {
            if (remember && _drawing is not null)
                Remember();
            if (_drawing is { Count: >= 3 })
            {
                _shapes.Add(_drawing);
                _owners.Add(CurrentPiece);
            }

            _drawing = null;
            Redraw();
        }

        public void DropShape()
        {
            if (_drawing is not null)
                Remember();
            _drawing = null;
            Redraw();
        }

        public void TakeBackPoint()
        {
            if (_drawing is { Count: > 0 })
            {
                Remember();
                _drawing.RemoveAt(_drawing.Count - 1);
                if (_drawing.Count == 0)
                    _drawing = null;
            }

            Redraw();
        }

        public void RemoveLastShape()
        {
            if (IsDrawing || _shapes.Count > 0)
                Remember();
            if (IsDrawing)
            {
                _drawing = null;
            }
            else if (_shapes.Count > 0)
            {
                _shapes.RemoveAt(_shapes.Count - 1);
                _owners.RemoveAt(_owners.Count - 1);
            }

            Redraw();
        }

        public void Clear()
        {
            if (_drawing is not null || _shapes.Count > 0)
                Remember();
            (_drawing, _dragging) = (null, null);
            _shapes.Clear();
            _owners.Clear();
            Redraw();
        }

        /// <summary>Takes a piece's shapes away (one step of Undo, which brings them back to it).</summary>
        public void RemovePiece(int piece)
        {
            if (!_owners.Contains(piece))
                return;

            Remember();
            for (var i = _shapes.Count - 1; i >= 0; i--)
            {
                if (_owners[i] == piece)
                {
                    _shapes.RemoveAt(i);
                    _owners.RemoveAt(i);
                }
            }

            Redraw();
        }

        private void Redraw()
        {
            InvalidateVisual();
            Changed?.Invoke();
        }

        private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, _width), Math.Clamp(point.Y, 0, _height));

        // Where the pointer is on the picture: with the pixel grid shown, on the nearest corner of a pixel.
        private Point Pointer() => Snapped(Mouse.GetPosition(this));

        private Point Snapped(Point at)
        {
            at = Clamp(at);
            return _grid is not null ? new Point(Math.Round(at.X), Math.Round(at.Y)) : at;
        }

        /// <summary>Whether a press here (in the picture's pixels) lands on an edge, where Alt + a press puts a point in (5.3.1: elsewhere it pans).</summary>
        public bool HasEdgeAt(Point at) => EdgeAt(Snapped(at)) is not null;

        // With Shift, the next point lies along 0, 45 or 90 degrees from the last one.
        private Point Constrained(Point at)
        {
            if (_drawing is not { Count: > 0 } drawing || !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                return at;

            var from = drawing[^1];
            var away = at - from;
            var angle = Math.Round(Math.Atan2(away.Y, away.X) / (Math.PI / 4)) * (Math.PI / 4);
            var along = away.X * Math.Cos(angle) + away.Y * Math.Sin(angle);
            return Clamp(new Point(from.X + along * Math.Cos(angle), from.Y + along * Math.Sin(angle)));
        }

        // The point under the pointer, within a few pixels on screen: of the shape being drawn first.
        private (List<Point> Points, int Index)? PointAt(Point at)
        {
            var reach = 7 * OnePixel;
            for (var p = 0; _drawing is not null && p < _drawing.Count; p++)
            {
                if ((_drawing[p] - at).Length <= reach)
                    return (_drawing, p);
            }

            for (var s = _shapes.Count - 1; s >= 0; s--)
            {
                for (var p = 0; p < _shapes[s].Count; p++)
                {
                    if ((_shapes[s][p] - at).Length <= reach)
                        return (_shapes[s], p);
                }
            }

            return null;
        }

        // The edge under the pointer: the outline and the index a new point goes in at.
        private (List<Point> Points, int Index)? EdgeAt(Point at)
        {
            var reach = 6 * OnePixel;

            static double Away(Point p, Point a, Point b)
            {
                var along = b - a;
                var t = along.LengthSquared > 0 ? Math.Clamp((p - a) * along / along.LengthSquared, 0, 1) : 0;
                return (p - (a + along * t)).Length;
            }

            for (var s = _shapes.Count - 1; s >= 0; s--)
            {
                var shape = _shapes[s];
                for (var p = 0; p < shape.Count; p++)
                {
                    if (Away(at, shape[p], shape[(p + 1) % shape.Count]) <= reach)
                        return (shape, p + 1);
                }
            }

            for (var p = 0; _drawing is not null && p + 1 < _drawing.Count; p++)
            {
                if (Away(at, _drawing[p], _drawing[p + 1]) <= reach)
                    return (_drawing, p + 1);
            }

            return null;
        }

        private int ShapeAt(Point at)
        {
            for (var s = _shapes.Count - 1; s >= 0; s--)
            {
                if (Outline(_shapes[s], true).FillContains(at))
                    return s;
            }

            return -1;
        }

        // A point put in on an edge, and held: the same press carries on as its drag.
        private void InsertAndHold((List<Point> Points, int Index) edge, Point at)
        {
            Remember();
            edge.Points.Insert(edge.Index, at);
            (_dragging, _dragKept) = (edge, true);
            CaptureMouse();
            Redraw();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            var at = Pointer();
            e.Handled = true;

            // Alt + click on an edge puts a point in it, at any moment.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && EdgeAt(at) is { } altEdge)
            {
                InsertAndHold(altEdge, at);
                return;
            }

            // A double-click closes the shape: its first click has already put a point down, which is kept.
            if (e.ClickCount == 2 && _drawing is { Count: >= 3 })
            {
                Remember();
                if (_drawing.Count >= 4 && (_drawing[^1] - at).Length <= 7 * OnePixel)
                    _drawing.RemoveAt(_drawing.Count - 1);
                CloseShape(remember: false);
                return;
            }

            // A double-click on a finished shape's edge puts a point in it, as Alt + click does.
            if (e.ClickCount == 2 && _drawing is null && EdgeAt(at) is { } edge)
            {
                InsertAndHold(edge, at);
                return;
            }

            // The glowing first point closes the shape; any other point is dragged, the shape being drawn or not.
            if (_drawing is { Count: >= 3 } && (_drawing[0] - at).Length <= 9 * OnePixel)
            {
                CloseShape();
                return;
            }

            if (PointAt(at) is { } point)
            {
                (_dragging, _dragKept) = (point, false);
                CaptureMouse();
                return;
            }

            if (Tool != CutoutTool.Pen && _drawing is null)
            {
                _box = (at, at);
                CaptureMouse();
                return;
            }

            Remember();
            (_drawing ??= []).Add(Constrained(at));
            Redraw();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var at = Pointer();
            _pointer = at;
            if (_box is { } box && e.LeftButton == MouseButtonState.Pressed)
            {
                _box = (box.From, at);
            }
            else if (_dragging is { } dragging && e.LeftButton == MouseButtonState.Pressed && dragging.Points[dragging.Index] != at)
            {
                // The drag's one step, as the point first moves.
                if (!_dragKept)
                {
                    _dragKept = true;
                    Remember();
                }

                dragging.Points[dragging.Index] = at;
            }

            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_box is { } box)
            {
                _box = null;
                ReleaseMouseCapture();
                var area = new Rect(box.From, box.To);
                if (area.Width >= 1 && area.Height >= 1)
                {
                    Remember();
                    _shapes.Add(Helper(Tool, area));
                    _owners.Add(CurrentPiece);
                }

                Redraw();
                return;
            }

            if (_dragging is not null)
            {
                _dragging = null;
                ReleaseMouseCapture();
                Redraw();
            }
        }

        // A helper's outline: ordinary points, to be dragged as any others.
        private static List<Point> Helper(CutoutTool tool, Rect area)
        {
            switch (tool)
            {
                case CutoutTool.Ellipse:
                    var round = new List<Point>(EllipsePoints);
                    for (var i = 0; i < EllipsePoints; i++)
                    {
                        var angle = 2 * Math.PI * i / EllipsePoints;
                        round.Add(new Point(area.X + area.Width / 2 * (1 + Math.Cos(angle)), area.Y + area.Height / 2 * (1 + Math.Sin(angle))));
                    }

                    return round;
                case CutoutTool.Slant:
                    var lean = area.Width * SlantLean;
                    return [new(area.Left + lean, area.Top), new(area.Right, area.Top), new(area.Right - lean, area.Bottom), new(area.Left, area.Bottom)];
                default:
                    return [area.TopLeft, area.TopRight, area.BottomRight, area.BottomLeft];
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _pointer = null;
            InvalidateVisual();
        }

        /// <summary>
        /// A right-click at a place on the picture (in its pixels). The window decides it was one when the button comes up without the
        /// pointer having moved (5.3.1): a right-drag pans the view instead.
        /// </summary>
        public void RightClick(Point at)
        {
            at = Snapped(at);

            // A point goes, of the shape being drawn or a finished one; a finished shape left with fewer than three goes with it.
            if (PointAt(at) is { } point)
            {
                Remember();
                point.Points.RemoveAt(point.Index);
                if (ReferenceEquals(point.Points, _drawing))
                {
                    if (_drawing.Count == 0)
                        _drawing = null;
                }
                else if (point.Points.Count < 3)
                {
                    var shape = _shapes.IndexOf(point.Points);
                    _shapes.RemoveAt(shape);
                    _owners.RemoveAt(shape);
                }
            }
            else if (IsDrawing)
            {
                TakeBackPoint();
                return;
            }
            else if (ShapeAt(at) is var inside and >= 0)
            {
                Remember();
                _shapes.RemoveAt(inside);
                _owners.RemoveAt(inside);
            }

            Redraw();
        }

        private static StreamGeometry Outline(IReadOnlyList<Point> points, bool closed)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], closed, closed);
                context.PolyLineTo(points.Skip(1).ToList(), true, true);
            }

            geometry.Freeze();
            return geometry;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var px = OnePixel;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, _width, _height));

            // One source pixel to a cell (at 16x and up): only over what is seen, so it costs the same at any zoom.
            if (_grid is { } grid && grid.Step > 0)
            {
                var pen = new Pen(GridLine, px);
                pen.Freeze();
                var seen = Rect.Intersect(grid.Seen, new Rect(0, 0, _width, _height));
                if (!seen.IsEmpty)
                {
                    for (var x = Math.Floor(seen.Left); x <= seen.Right; x += grid.Step)
                        dc.DrawLine(pen, new Point(x, seen.Top), new Point(x, seen.Bottom));
                    for (var y = Math.Floor(seen.Top); y <= seen.Bottom; y += grid.Step)
                        dc.DrawLine(pen, new Point(seen.Left, y), new Point(seen.Right, y));
                }
            }

            // The areas the layers cut out of the video: this layer's solid, the others dashed.
            foreach (var (area, name, isThis) in _regions)
            {
                var pen = new Pen(RegionLine, (isThis ? 2.5 : 1.5) * px) { DashStyle = isThis ? DashStyles.Solid : DashStyles.Dash };
                dc.DrawRectangle(null, pen, area);
                var label = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11 * px, Brushes.White, 1);
                var where = new Point(area.X + 3 * px, Math.Max(area.Y - label.Height - 2 * px, 0));
                dc.DrawRectangle(LabelBack, null, new Rect(where.X - 2 * px, where.Y, label.Width + 4 * px, label.Height));
                dc.DrawText(label, where);
            }

            var handle = 3.5 * px;
            for (var s = 0; s < _shapes.Count; s++)
            {
                var (fill, stroke) = (PieceFills[Math.Abs(_owners[s]) % PieceFills.Length], LineOf(_owners[s]));
                dc.DrawGeometry(_owners[s] == CurrentPiece ? fill : null, new Pen(stroke, 1.5 * px), Outline(_shapes[s], true));
                foreach (var point in _shapes[s])
                    dc.DrawRectangle(stroke, null, new Rect(point.X - handle, point.Y - handle, handle * 2, handle * 2));
            }

            if (_box is { } box)
            {
                var pen = new Pen(DrawingLine, 1.5 * px) { DashStyle = DashStyles.Dash };
                dc.DrawGeometry(null, pen, Outline(Helper(Tool, new Rect(box.From, box.To)), true));
            }

            if (_drawing is { Count: > 0 } drawing)
            {
                var drawPen = new Pen(DrawingLine, 2 * px);
                if (drawing.Count > 1)
                    dc.DrawGeometry(null, drawPen, Outline(drawing, false));
                if (_pointer is { } pointer)
                    dc.DrawLine(new Pen(DrawingLine, 1.2 * px) { DashStyle = DashStyles.Dash }, drawing[^1], Constrained(pointer));

                foreach (var point in drawing)
                    dc.DrawRectangle(DrawingLine, null, new Rect(point.X - handle, point.Y - handle, handle * 2, handle * 2));

                // The first point is where the shape closes: it glows, and fills while the pointer is near enough to close it.
                var near = drawing.Count >= 3 && _pointer is { } p && (drawing[0] - p).Length <= 9 * px;
                if (drawing.Count >= 3)
                    dc.DrawEllipse(FirstPointGlow, null, drawing[0], 16 * px, 16 * px);
                dc.DrawEllipse(near ? DrawingLine : null, new Pen(DrawingLine, 2 * px), drawing[0], 6 * px, 6 * px);
            }
        }

        private static Brush[] Frozen(byte alpha)
        {
            Color[] colors = [Color.FromRgb(0x3D, 0xD6, 0xF5), Color.FromRgb(0x7C, 0xE0, 0x6A), Color.FromRgb(0xF5, 0xC2, 0x3D), Color.FromRgb(0xF5, 0x6F, 0xA8), Color.FromRgb(0xB0, 0x8C, 0xFF), Color.FromRgb(0xFF, 0x8A, 0x3D)];
            return colors.Select(c => Frozen(Color.FromArgb(alpha, c.R, c.G, c.B))).ToArray();
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static RadialGradientBrush MakeGlow()
        {
            var glow = new RadialGradientBrush(Color.FromArgb(0xC0, 0xFF, 0x8A, 0x3D), Color.FromArgb(0, 0xFF, 0x8A, 0x3D));
            glow.Freeze();
            return glow;
        }
    }
}
