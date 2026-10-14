using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Draw Custom Shape: a still of the video at the playhead, to trace HUD elements on by hand. Each click puts
/// down a point and the lines join up; clicking the first point again (or double-clicking, or Enter) closes the
/// shape, and the next click starts another. The shapes become one mask of the whole frame, white inside them,
/// for the layer the window was opened from or for every layer cut from the video. The areas those layers cut
/// out are outlined on the still, so it is plain which shape each will get. Built in code: a still, the
/// shapes drawn over it, a line of help and a row of buttons.
/// </summary>
public sealed class MaskDrawWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Layer _layer;

    // The still and the shapes are laid out in the source's own pixels, and the whole is scaled to the window.
    private readonly double _width, _height;
    private readonly Image _still = new() { Stretch = Stretch.Fill };
    private readonly ShapeSurface _surface;
    private readonly Viewbox _view = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly string _stillPath = Path.Combine(SessionPaths.Previews, $"mask_still_{Guid.NewGuid():N}.png");
    private readonly CancellationTokenSource _closing = new();

    /// <summary>The finished shapes as a mask's text (see <see cref="MaskStore"/>), once the window was confirmed.</summary>
    public string Shapes { get; private set; } = "";

    /// <summary>Whether the shapes are for every layer cut from the video, not only the one the window was opened from.</summary>
    public bool ForAllLayers { get; private set; }

    public MaskDrawWindow(MainViewModel viewModel, Layer layer)
    {
        (_viewModel, _layer) = (viewModel, layer);
        (_width, _height) = viewModel.SourceWidth > 0 && viewModel.SourceHeight > 0 ? (viewModel.SourceWidth, viewModel.SourceHeight) : (1920.0, 1080.0);

        Title = $"Draw Custom Shape: {layer.Name}";
        Width = 1180;
        Height = 820;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        // Start from the layer's own drawn shapes, or failing that from those of another piece of the same video:
        // they are all one mask of the frame.
        var start = layer.UsesFrameMask && layer.MaskShapes.Length > 0 ? layer.MaskShapes
            : viewModel.Layers.FirstOrDefault(l => l.UsesFrameMask && l.CustomMask && l.MaskShapes.Length > 0)?.MaskShapes ?? "";
        var regions = viewModel.Layers.Where(l => l.IsVideo && !l.IsDeleted)
            .Select(l => (new Rect(l.SourceX * _width, l.SourceY * _height, l.SourceWidth * _width, l.SourceHeight * _height), l.Name, ReferenceEquals(l, layer)))
            .ToList();
        _surface = new ShapeSurface(_width, _height, regions, MaskStore.ParseShapes(start).Select(s => s.Select(p => new Point(p.X * _width, p.Y * _height)).ToList()).ToList());
        _surface.Changed += ShowHelp;

        var picture = new Grid { Width = _width, Height = _height, Background = Brushes.Black };
        picture.Children.Add(_still);
        picture.Children.Add(_surface);
        _view.Child = picture;

        var help = new TextBlock
        {
            Text = "Click around a HUD element to put down its outline, point by point. Click the first point again (or double-click, or press Enter) to close the shape, "
                   + "then start the next. Drag a point to move it. Right-click takes back the last point, or removes the point or shape under the pointer. Backspace takes back a point, Escape drops the shape being drawn.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var undoShape = new Button { Content = "Remove Last Shape", Padding = new Thickness(12, 4, 12, 4) };
        undoShape.Click += (_, _) => _surface.RemoveLastShape();
        var clear = new Button { Content = "Clear All", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => _surface.Clear();
        var applyOne = new Button { Content = "Apply to This Layer", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        applyOne.Click += (_, _) => Finish(forAll: false);
        var applyAll = new Button
        {
            Content = "Apply to All Cut-out Layers",
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Every layer cut from the video gets this mask of the whole frame, and takes the part of it under its own area.",
        };
        applyAll.Click += (_, _) => Finish(forAll: true);
        var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(applyOne);
        if (regions.Count > 1)
            right.Children.Add(applyAll);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(undoShape);
        left.Children.Add(clear);
        DockPanel.SetDock(left, Dock.Left);
        _status.Margin = new Thickness(12, 0, 12, 0);
        buttons.Children.Add(right);
        buttons.Children.Add(left);
        buttons.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(help, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(help);
        root.Children.Add(buttons);
        root.Children.Add(new Border { Child = _view, Background = Brushes.Black });
        Content = root;

        PreviewKeyDown += Window_PreviewKeyDown;
        SizeChanged += (_, _) => _surface.InvalidateVisual();
        Loaded += async (_, _) => await LoadStillAsync();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            SourceStill.Delete(_stillPath);
        };
        ShowHelp();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _surface.IsDrawing:
                _surface.CloseShape();
                e.Handled = true;
                break;
            case Key.Back:
                _surface.TakeBackPoint();
                e.Handled = true;
                break;
            case Key.Escape when _surface.IsDrawing:
                _surface.DropShape();
                e.Handled = true;
                break;
        }
    }

    private void ShowHelp() => _status.Text = _surface.IsDrawing
        ? $"Drawing: {_surface.PointsInShape} point{(_surface.PointsInShape == 1 ? "" : "s")}. Click the first point to close the shape."
        : $"{_surface.ShapeCount} shape{(_surface.ShapeCount == 1 ? "" : "s")}.";

    /// <summary>The source's frame at the playhead, as it is in the file (the areas layers cut out are measured on it).</summary>
    private async Task LoadStillAsync()
    {
        var source = _viewModel.LocalMediaPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            _status.Text = "There is no video file to take a still from: shapes can still be drawn on the blank frame.";
            return;
        }

        _status.Text = "Taking a still of the video at the playhead...";
        try
        {
            _still.Source = await SourceStill.TakeAsync(source, _viewModel.PositionMs / 1000, _stillPath, _closing.Token);
            ShowHelp();
        }
        catch (OperationCanceledException)
        {
            // Closed before the still came.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Draw Custom Shape: the still could not be taken", ex);
            _status.Text = $"The still could not be taken ({ex.Message}): shapes can still be drawn on the blank frame.";
        }
    }

    private void Finish(bool forAll)
    {
        _surface.CloseShape();
        if (_surface.ShapeCount == 0)
        {
            _status.Text = "Draw at least one shape first, or press Cancel.";
            return;
        }

        Shapes = MaskStore.FormatShapes(_surface.Finished.Select(s => (IReadOnlyList<Point>)s.Select(p => new Point(p.X / _width, p.Y / _height)).ToList()));
        ForAllLayers = forAll;
        DialogResult = true;
    }

    /// <summary>
    /// The shapes over the still, and what the pointer does to them. Drawn in <see cref="OnRender"/>, in the
    /// source's pixels; lines and handles are sized by how far the window has scaled it, so they stay thin.
    /// </summary>
    private sealed class ShapeSurface : FrameworkElement
    {
        private readonly double _width, _height;
        private readonly List<(Rect Area, string Name, bool IsThis)> _regions;
        private readonly List<List<Point>> _shapes;
        private List<Point>? _drawing;
        private Point? _pointer;
        private (int Shape, int Point)? _dragging;

        private static readonly Brush ShapeFill = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        private static readonly Brush ShapeLine = Frozen(Color.FromRgb(0x3D, 0xD6, 0xF5));
        private static readonly Brush DrawingLine = Frozen(Color.FromRgb(0xFF, 0x8A, 0x3D));
        private static readonly Brush RegionLine = Frozen(Color.FromArgb(0xC0, 0xFF, 0xD0, 0x40));
        private static readonly Brush LabelBack = Frozen(Color.FromArgb(0xB0, 0, 0, 0));

        public event Action? Changed;

        public ShapeSurface(double width, double height, List<(Rect, string, bool)> regions, List<List<Point>> shapes)
        {
            (_width, _height, _regions, _shapes) = (width, height, regions, shapes);
            (Width, Height) = (width, height);
            Cursor = Cursors.Cross;
            Focusable = true;
        }

        public bool IsDrawing => _drawing is { Count: > 0 };
        public int PointsInShape => _drawing?.Count ?? 0;
        public int ShapeCount => _shapes.Count;
        public IReadOnlyList<List<Point>> Finished => _shapes;

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

        public void CloseShape()
        {
            if (_drawing is { Count: >= 3 })
                _shapes.Add(_drawing);
            _drawing = null;
            Redraw();
        }

        public void DropShape()
        {
            _drawing = null;
            Redraw();
        }

        public void TakeBackPoint()
        {
            if (_drawing is { Count: > 0 })
            {
                _drawing.RemoveAt(_drawing.Count - 1);
                if (_drawing.Count == 0)
                    _drawing = null;
            }

            Redraw();
        }

        public void RemoveLastShape()
        {
            if (IsDrawing)
                _drawing = null;
            else if (_shapes.Count > 0)
                _shapes.RemoveAt(_shapes.Count - 1);
            Redraw();
        }

        public void Clear()
        {
            (_drawing, _dragging) = (null, null);
            _shapes.Clear();
            Redraw();
        }

        private void Redraw()
        {
            InvalidateVisual();
            Changed?.Invoke();
        }

        private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, _width), Math.Clamp(point.Y, 0, _height));

        // The point of a finished shape under the pointer, within a few pixels on screen.
        private (int Shape, int Point)? PointAt(Point at)
        {
            var reach = 7 * OnePixel;
            for (var s = _shapes.Count - 1; s >= 0; s--)
            {
                for (var p = 0; p < _shapes[s].Count; p++)
                {
                    if ((_shapes[s][p] - at).Length <= reach)
                        return (s, p);
                }
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

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            var at = Clamp(e.GetPosition(this));
            e.Handled = true;

            // A double-click closes the shape: its first click has already put a point down, which is kept.
            if (e.ClickCount == 2 && _drawing is { Count: >= 3 })
            {
                if (_drawing.Count >= 4 && (_drawing[^1] - at).Length <= 7 * OnePixel)
                    _drawing.RemoveAt(_drawing.Count - 1);
                CloseShape();
                return;
            }

            if (_drawing is null && PointAt(at) is { } point)
            {
                _dragging = point;
                CaptureMouse();
                return;
            }

            if (_drawing is { Count: >= 3 } && (_drawing[0] - at).Length <= 9 * OnePixel)
            {
                CloseShape();
                return;
            }

            (_drawing ??= []).Add(at);
            Redraw();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var at = Clamp(e.GetPosition(this));
            _pointer = at;
            if (_dragging is { } dragging && e.LeftButton == MouseButtonState.Pressed)
                _shapes[dragging.Shape][dragging.Point] = at;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_dragging is not null)
            {
                _dragging = null;
                ReleaseMouseCapture();
                Redraw();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _pointer = null;
            InvalidateVisual();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            var at = Clamp(e.GetPosition(this));
            e.Handled = true;
            if (IsDrawing)
            {
                TakeBackPoint();
                return;
            }

            // A point of a finished shape goes; a shape left with fewer than three goes with it. Elsewhere inside a shape, the shape goes.
            if (PointAt(at) is { } point)
            {
                _shapes[point.Shape].RemoveAt(point.Point);
                if (_shapes[point.Shape].Count < 3)
                    _shapes.RemoveAt(point.Shape);
            }
            else if (ShapeAt(at) is var shape and >= 0)
            {
                _shapes.RemoveAt(shape);
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

            var line = new Pen(ShapeLine, 1.5 * px);
            var handle = 3.5 * px;
            foreach (var shape in _shapes)
            {
                dc.DrawGeometry(ShapeFill, line, Outline(shape, true));
                foreach (var point in shape)
                    dc.DrawRectangle(ShapeLine, null, new Rect(point.X - handle, point.Y - handle, handle * 2, handle * 2));
            }

            if (_drawing is { Count: > 0 } drawing)
            {
                var drawPen = new Pen(DrawingLine, 2 * px);
                if (drawing.Count > 1)
                    dc.DrawGeometry(null, drawPen, Outline(drawing, false));
                if (_pointer is { } pointer)
                    dc.DrawLine(new Pen(DrawingLine, 1.2 * px) { DashStyle = DashStyles.Dash }, drawing[^1], pointer);

                foreach (var point in drawing)
                    dc.DrawRectangle(DrawingLine, null, new Rect(point.X - handle, point.Y - handle, handle * 2, handle * 2));

                // The first point is where the shape closes: larger, and ringed while the pointer is near enough to close it.
                var near = drawing.Count >= 3 && _pointer is { } p && (drawing[0] - p).Length <= 9 * px;
                dc.DrawEllipse(near ? DrawingLine : null, new Pen(DrawingLine, 2 * px), drawing[0], 6 * px, 6 * px);
            }
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
