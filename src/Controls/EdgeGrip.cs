using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Thin resize grip along an edge (e.g. the Sequence Overview Scrubber's lower edge): transparent until hovered, then a 1 px accent line
/// across its middle. While dragged it reports the pointer delta since press (DIPs, down positive) in window-content coordinates (the
/// grip itself moves as its target grows, and content DIPs differ from window DIPs under the UI scale: UiScale). Double-click requests
/// the default size. Rendered in OnRender.
/// </summary>
public sealed class EdgeGrip : FrameworkElement
{
    private double? _fromY;

    public EdgeGrip()
    {
        Height = 6;
        Cursor = Cursors.SizeNS;
        Focusable = false;
        AutomationProperties.SetName(this, "Resize");
    }

    /// <summary>Raised when a drag begins.</summary>
    public event Action? DragStarted;

    /// <summary>Raised with the vertical delta since drag start (positive down).</summary>
    public event Action<double>? Dragged;

    /// <summary>Raised on double-click to request the default size.</summary>
    public event Action? ResetRequested;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ResetRequested?.Invoke();
            return;
        }

        if (!CaptureMouse())
            return;
        _fromY = PointerY(e);
        DragStarted?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_fromY is { } from)
            Dragged?.Invoke(PointerY(e) - from);
    }

    /// <summary>
    /// Pointer Y in window-content DIPs (inside the UI scale LayoutTransform). Window DIPs from GetPosition(null) differ by the scale
    /// factor (about a third on a 2880 x 1800 panel at 200 %), which made the edge lag the pointer.
    /// </summary>
    private double PointerY(MouseEventArgs e) => e.GetPosition(Window.GetWindow(this)?.Content as IInputElement).Y;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_fromY is null)
            return;
        e.Handled = true;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _fromY = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        // Transparent fill keeps the whole strip hit-testable.
        var (width, height) = (ActualWidth, ActualHeight);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (!IsMouseOver && _fromY is null)
            return;

        var accent = TryFindResource("AccentBrush") as Brush ?? SystemColors.HighlightBrush;
        drawing.DrawRectangle(accent, null, new Rect(0, Math.Floor((height - 1) / 2), width, 1));
    }
}

/// <summary>
/// Timeline track-height pill in the zoom corner; <see cref="Value"/> is the track height (MainViewModel.LayerTrackHeight, 1 to 50).
/// Drag up to grow (one step per <see cref="PixelsPerStep"/> DIPs), down to shrink; the wheel moves two steps per notch; double-click
/// restores the default. Rendered in OnRender: three horizontal track bars and a vertical double-headed arrow in a rounded well, accent
/// while held (a former chevron pair met at its feet and read as a Keyframe diamond).
/// </summary>
public sealed class TrackHeightPill : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(TrackHeightPill),
        new FrameworkPropertyMetadata(25.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Track height, 1 to 50.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private const double Minimum = 1, Maximum = 50, Usual = 25, PixelsPerStep = 3, StepsPerNotch = 2;

    private (double FromY, double FromValue)? _drag;
    private Pen? _pen;
    private Brush? _penBrush;
    private StreamGeometry? _mark;
    private Size _markSize;

    public TrackHeightPill()
    {
        Width = 18;
        Height = 20;
        Cursor = Cursors.SizeNS;
        Focusable = false;
        AutomationProperties.SetName(this, "Track Height Pill");
    }

    private void Set(double value) => Value = Math.Clamp(Math.Round(value), Minimum, Maximum);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            Set(Usual);
            return;
        }

        if (!CaptureMouse())
            return;
        _drag = (e.GetPosition(null).Y, Value);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is { } drag)
            Set(drag.FromValue + (drag.FromY - e.GetPosition(null).Y) / PixelsPerStep);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is null)
            return;
        e.Handled = true;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = null;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        Set(Value + Math.Sign(e.Delta) * StepsPerNotch * Math.Max(1, Math.Round(Math.Abs(e.Delta) / 120.0)));
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        var well = TryFindResource(IsMouseOver || _drag is not null ? "ControlHoverBrush" : "InsetBrush") as Brush ?? Brushes.Transparent;
        var mark = (_drag is not null ? TryFindResource("AccentBrush") : TryFindResource("MutedTextBrush")) as Brush ?? SystemColors.GrayTextBrush;
        drawing.DrawRoundedRectangle(well, null, new Rect(0, 0, width, height), 3, 3);

        if (_pen is null || !ReferenceEquals(_penBrush, mark))
        {
            _pen = new Pen(mark, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _pen.Freeze();
            _penBrush = mark;
        }

        if (_mark is null || _markSize != new Size(width, height))
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
                foreach (var (from, to) in MarkStrokes(width, height))
                {
                    context.BeginFigure(from, false, false);
                    context.LineTo(to, true, false);
                }

            geometry.Freeze();
            (_mark, _markSize) = (geometry, new Size(width, height));
        }

        drawing.DrawGeometry(null, _pen, _mark);
    }

    /// <summary>
    /// Icon strokes for a well of the given size: three horizontal track bars on the left and a vertical double-headed arrow on the right.
    /// No two slanted strokes share an endpoint, so the mark never reads as a diamond.
    /// </summary>
    internal static (Point From, Point To)[] MarkStrokes(double width, double height)
    {
        var (shaft, head, rise) = (width - 3.5, Math.Min(2.5, width * 0.14), Math.Min(2.5, height * 0.125));
        var (left, right, middle, gap) = (3.0, Math.Max(3.0, shaft - head - 1.5), height / 2, Math.Min(4.0, height * 0.2));
        var (top, bottom) = (Math.Min(4.0, height * 0.2), height - Math.Min(4.0, height * 0.2));
        return
        [
            (new Point(left, middle - gap), new Point(right, middle - gap)),
            (new Point(left, middle), new Point(right, middle)),
            (new Point(left, middle + gap), new Point(right, middle + gap)),
            (new Point(shaft, top), new Point(shaft, bottom)),
            (new Point(shaft - head, top + rise), new Point(shaft, top)),
            (new Point(shaft + head, top + rise), new Point(shaft, top)),
            (new Point(shaft - head, bottom - rise), new Point(shaft, bottom)),
            (new Point(shaft + head, bottom - rise), new Point(shaft, bottom)),
        ];
    }
}
