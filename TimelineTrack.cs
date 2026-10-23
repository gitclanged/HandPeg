using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// A bar of a timeline that draws itself. Everything on it (blocks, frames, waveforms, keyframe diamonds) is
/// drawn in OnRender as shapes; nothing on it is an element of its own, so a bar with a thousand blocks costs
/// what a bar with one does to lay out. The playhead is the one thing that moves while a video plays, and is
/// a visual of its own that is slid along: the bar is not drawn again for it.
/// </summary>
public abstract class TimelineBar : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen KeyOutline = Frozen(new Pen(Brushes.Black, 0.7));

    private readonly DrawingVisual _playhead = new();
    private double _playheadX = double.NaN;
    private double _playheadHeight;

    protected TimelineBar()
    {
        ClipToBounds = true;
        AddVisualChild(_playhead);
    }

    /// <summary>What is behind the blocks. Also what makes the whole bar answer the mouse, so it is never left out: none is drawn as transparent.</summary>
    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Where the playhead is, in pixels from the left; NaN for none.</summary>
    public double PlayheadX
    {
        get => _playheadX;
        set
        {
            _playheadX = value;
            PlacePlayhead();
        }
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _playhead;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        PlacePlayhead();
    }

    private void PlacePlayhead()
    {
        var height = Math.Max(ActualHeight, 4);
        var shown = !double.IsNaN(_playheadX);
        if (height != _playheadHeight || !shown)
        {
            _playheadHeight = shown ? height : 0;
            using var drawing = _playhead.RenderOpen();
            if (shown)
                drawing.DrawRectangle(Brushes.OrangeRed, null, new Rect(0, 0, 1.5, height));
        }

        if (shown)
            _playhead.Offset = new Vector(_playheadX, 0);
    }

    protected void DrawBackground(DrawingContext drawing) =>
        drawing.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

    /// <summary>A keyframe's diamond, centred on a point.</summary>
    protected static void DrawDiamond(DrawingContext drawing, double x, double y, double radius)
    {
        var diamond = new StreamGeometry();
        using (var path = diamond.Open())
        {
            path.BeginFigure(new Point(x, y - radius), isFilled: true, isClosed: true);
            path.LineTo(new Point(x + radius, y), true, false);
            path.LineTo(new Point(x, y + radius), true, false);
            path.LineTo(new Point(x - radius, y), true, false);
        }

        diamond.Freeze();
        drawing.DrawGeometry(Brushes.Gold, KeyOutline, diamond);
    }

    protected static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    protected static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// The stretch of a sound that a block shows: the whole sound laid out along the timeline would be
/// <paramref name="Span"/> pixels wide, and the block begins <paramref name="Offset"/> pixels into it. A span
/// of nothing means the whole sound, fitted to the block.
/// </summary>
public readonly record struct WaveSpan(HandPegApp.Services.WaveformData Data, double Offset, double Span);

/// <summary>One block of a <see cref="TimelineTrack"/>: a clip, a cut segment, a part of an audio track, a subtitle.</summary>
public sealed class TimelineBlock
{
    public double Left { get; set; }
    public double Top { get; init; }
    public double Width { get; set; }
    public double Height { get; init; }
    public Brush Fill { get; init; } = Brushes.Gray;
    public string Name { get; init; } = "";

    /// <summary>What the block stands for: what is selected when it is.</summary>
    public object? Clip { get; init; }

    /// <summary>Frames of the block's video, side by side from its left edge: each a picture, where it begins and how wide it is.</summary>
    public IReadOnlyList<(ImageSource Picture, double Left, double Width)>? Frames { get; init; }

    /// <summary>A picture of the block's sound, drawn over the frames.</summary>
    public Brush? Wave { get; init; }

    /// <summary>The block's sound as peaks, drawn over the frames at whatever size the block is: used in place of <see cref="Wave"/> when given.</summary>
    public WaveSpan? Peaks { get; init; }

    /// <summary>The block's keyframes, in pixels from its left edge.</summary>
    public IReadOnlyList<double>? Keys { get; init; }

    /// <summary>Whether the block can be moved right now; null for one that never can.</summary>
    public Func<bool>? CanDrag { get; init; }

    /// <summary>Whether its right edge can be dragged to change its length.</summary>
    public bool Resizable { get; init; }

    /// <summary>What letting go of a moved block does, given its new left edge and width in pixels.</summary>
    public Action<double, double>? Commit { get; init; }

    /// <summary>What to say when it cannot be dragged.</summary>
    public string CannotDrag { get; init; } = "";

    /// <summary>May be dragged past the left end of the bar: a sound that was slipped.</summary>
    public bool MayStartBeforeZero { get; init; }

    internal FormattedText? Label;
}

/// <summary>
/// A time bar with blocks on it: a layer's clips, the main video's clips and cut segments, an audio track's
/// parts, the subtitles. The window says what the blocks are; the bar draws them, finds the one under the
/// pointer by arithmetic, and moves or stretches it as it is dragged.
/// </summary>
public sealed class TimelineTrack : TimelineBar
{
    /// <summary>How close to a block's right edge the pointer has to be to take hold of its length.</summary>
    public const double EdgePixels = 7;

    private static readonly Pen SelectedOutline = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Brush LabelShade = FrozenBrush(Color.FromArgb(150, 0, 0, 0));

    /// <summary>The room after the end of the sequence: darker, and where a clip dropped or dragged makes it longer.</summary>
    internal static readonly Brush PastEndShade = FrozenBrush(Color.FromArgb(90, 0, 0, 0));
    internal static readonly Pen EndLine = Frozen(new Pen(FrozenBrush(Color.FromArgb(150, 255, 255, 255)), 1));

    /// <summary>How close, in pixels, a dragged edge has to come to something to snap to it.</summary>
    public const double SnapPixels = 8;
    private static readonly Typeface LabelFace = new("Segoe UI");

    private (TimelineBlock Block, double PointerX, double Left, double Width, bool Resizing)? _drag;

    public TimelineTrack()
    {
        // The frames in a block are thumbnails, drawn smaller than they are and many at a time: the cheapest
        // scaling there is, not the smooth one, which costs the most exactly while a timeline is scrolled.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
    }

    public List<TimelineBlock> Blocks { get; } = [];

    /// <summary>Where the sequence ends, in the bar's laid-out pixels (as the blocks are); what lies after it is shaded. NaN for no end drawn.</summary>
    public double EndX { get; set; } = double.NaN;

    /// <summary>How many of the bar's pixels a second of the sequence takes: what snapping measures in.</summary>
    public double PixelsPerSecond { get; set; }

    /// <summary>
    /// The moments a dragged block's edges snap to, in seconds (the edges of the other clips, the playhead); null
    /// for none. Asked once, when a drag begins. Holding Alt while dragging leaves them alone.
    /// </summary>
    public Func<IReadOnlyList<double>>? SnapTimes { get; set; }

    private IReadOnlyList<double> _snapTimes = [];

    /// <summary>What is selected: the block that stands for it is outlined.</summary>
    public Func<object?, bool>? IsSelected { get; set; }

    /// <summary>A block was pressed, with either button.</summary>
    public event Action<TimelineBlock, MouseButtonEventArgs>? BlockPressed;

    /// <summary>A block that cannot be dragged right now was pressed: its reason.</summary>
    public event Action<string>? DragRefused;

    /// <summary>A drag ended, whether or not it moved anything.</summary>
    public event Action? DragEnded;

    public bool IsDragging => _drag is not null;

    /// <summary>
    /// How far the bar has been moved along, in pixels: its blocks are laid out as if it were as wide as the
    /// zoom makes it, and this is how much of that lies to the left of what is seen.
    /// </summary>
    public double ViewOffsetX { get; set; }

    /// <summary>Ctrl + wheel over the bar: zoom by a factor, about the pointer (pixels from the bar's left).</summary>
    public event Action<TimelineTrack, double, double>? ZoomRequested;

    /// <summary>Shift + wheel, or a drag with the middle button: move along by so many pixels.</summary>
    public event Action<TimelineTrack, double>? PanRequested;

    private double? _panFrom;

    // Where the pointer is on the bar as it is laid out, zoomed: what the blocks are measured in.
    private Point Virtual(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return new Point(point.X + ViewOffsetX, point.Y);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomRequested?.Invoke(this, Math.Pow(1.25, e.Delta / 120.0), e.GetPosition(this).X);
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            PanRequested?.Invoke(this, -e.Delta);
        else
        {
            // A plain wheel is the list's: it scrolls the rows.
            base.OnMouseWheel(e);
            return;
        }

        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && CaptureMouse())
        {
            _panFrom = e.GetPosition(this).X;
            Cursor = Cursors.ScrollWE;
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panFrom is not null)
        {
            _panFrom = null;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <summary>The block under a point, the topmost (the last drawn) first.</summary>
    public TimelineBlock? BlockAt(Point point)
    {
        for (var i = Blocks.Count - 1; i >= 0; i--)
        {
            var block = Blocks[i];
            if (point.X >= block.Left && point.X <= block.Left + block.Width && point.Y >= block.Top && point.Y <= block.Top + block.Height)
                return block;
        }

        return null;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        DrawBackground(drawing);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // After the end of the sequence: the room a clip can be dropped or dragged into.
        if (!double.IsNaN(EndX) && EndX - ViewOffsetX < ActualWidth)
        {
            var end = Math.Max(EndX - ViewOffsetX, 0);
            drawing.DrawRectangle(PastEndShade, null, new Rect(end, 0, ActualWidth - end, ActualHeight));
            if (EndX - ViewOffsetX >= 0)
                drawing.DrawLine(EndLine, new Point(end + 0.5, 0), new Point(end + 0.5, ActualHeight));
        }

        // The blocks are where the zoom puts them; the part of the bar that is seen is slid into view.
        drawing.PushTransform(new TranslateTransform(-ViewOffsetX, 0));
        foreach (var block in Blocks)
        {
            // What is off the bar is not drawn at all.
            if (block.Left - ViewOffsetX > ActualWidth || block.Left + block.Width - ViewOffsetX < 0)
                continue;

            var rect = new Rect(block.Left, block.Top, Math.Max(block.Width, 3), Math.Max(block.Height, 1));
            var shape = new RectangleGeometry(rect, 3, 3);
            shape.Freeze();
            drawing.DrawGeometry(block.Fill, null, shape);
            drawing.PushClip(shape);

            if (block.Frames is { } frames)
            {
                drawing.PushOpacity(0.9);
                foreach (var (picture, left, width) in frames)
                {
                    var at = rect.Left + left;
                    if (at - ViewOffsetX <= ActualWidth && at + width - ViewOffsetX >= 0)
                        drawing.DrawImage(picture, new Rect(at, rect.Top, width, rect.Height));
                }

                drawing.Pop();
            }

            if (block.Peaks is { } peaks)
            {
                drawing.PushOpacity(block.Frames is null ? 0.6 : 0.85);
                DrawPeaks(drawing, peaks, rect);
                drawing.Pop();
            }
            else if (block.Wave is { } wave)
            {
                // The brush is laid out for a block that begins at 0: it is moved to where the block is.
                drawing.PushTransform(new TranslateTransform(rect.Left, rect.Top));
                drawing.PushOpacity(block.Frames is null ? 0.6 : 0.85);
                drawing.DrawRectangle(wave, null, new Rect(0, 0, rect.Width, rect.Height));
                drawing.Pop();
                drawing.Pop();
            }

            if (block.Keys is { } keys)
            {
                foreach (var at in keys)
                {
                    if (at >= -4 && at <= rect.Width + 4)
                        DrawDiamond(drawing, rect.Left + at, rect.Bottom - 6, 4.5);
                }
            }

            if (block.Name.Length > 0 && rect.Width > 14)
            {
                var label = block.Label ??= new FormattedText(block.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, dpi)
                {
                    MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                };
                label.MaxTextWidth = Math.Max(rect.Width - 10, 4);
                var origin = new Point(Math.Max(rect.Left, ViewOffsetX) + 5, rect.Top + (rect.Height - label.Height) / 2);

                // Over frames, the words get a shade of their own to be read against.
                if (block.Frames is not null)
                    drawing.DrawRoundedRectangle(LabelShade, null, new Rect(origin.X - 2, origin.Y, label.Width + 4, label.Height), 2, 2);
                drawing.DrawText(label, origin);
            }

            drawing.Pop();
            if (IsSelected?.Invoke(block.Clip) == true)
                drawing.DrawRoundedRectangle(null, SelectedOutline, new Rect(rect.Left + 0.75, rect.Top + 0.75, Math.Max(rect.Width - 1.5, 1), Math.Max(rect.Height - 1.5, 1)), 3, 3);
        }

        drawing.Pop();

        // Where a file being dragged over the bar would land.
        if (!double.IsNaN(_dropX) && _dropX >= -2 && _dropX <= ActualWidth + 2)
            drawing.DrawRectangle(DropMarkBrush, null, new Rect(_dropX - 1.5, 0, 3, ActualHeight));
    }

    private static readonly Brush DropMarkBrush = FrozenBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private double _dropX = double.NaN;

    /// <summary>Where a file being dragged over the bar would land, in pixels from its left as it is seen; NaN for nowhere.</summary>
    public double DropX
    {
        get => _dropX;
        set
        {
            if (_dropX.Equals(value))
                return;

            _dropX = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Draws a block's sound from its peaks, as one shape. Only the part of the block that is on the bar is
    /// drawn, and with no more columns than it has pixels: zoomed out, all the peaks that fall into a pixel
    /// are merged into the one column, so an hour of sound across a narrow block is a few hundred points and
    /// not seventy thousand; zoomed in, where a peak is wider than a pixel, there is a column for each peak.
    /// </summary>
    private void DrawPeaks(DrawingContext drawing, WaveSpan wave, Rect rect)
    {
        var (from, to) = (Math.Max(rect.Left, ViewOffsetX), Math.Min(rect.Right, ViewOffsetX + ActualWidth));
        var span = wave.Span > 0 ? wave.Span : rect.Width;
        if (to - from < 1 || span <= 0 || wave.Data.Count == 0)
            return;

        // Where in the sound the visible part of the block begins and ends, as fractions of its length.
        var (first, last) = ((from - rect.Left + wave.Offset) / span, (to - rect.Left + wave.Offset) / span);
        var columns = (int)Math.Clamp(Math.Ceiling(Math.Min(to - from, (last - first) * wave.Data.Count)), 1, 4096);
        var (step, slice, middle) = ((to - from) / columns, (last - first) / columns, rect.Top + rect.Height / 2);
        var half = rect.Height / 2;

        Span<double> tops = columns <= 512 ? stackalloc double[columns] : new double[columns];
        Span<double> bottoms = columns <= 512 ? stackalloc double[columns] : new double[columns];
        for (var c = 0; c < columns; c++)
        {
            var (low, high) = wave.Data.Range(first + c * slice, first + (c + 1) * slice);
            tops[c] = middle - Math.Max(high * half, 0.4);
            bottoms[c] = middle - Math.Min(low * half, -0.4);
        }

        var outline = new StreamGeometry();
        using (var path = outline.Open())
        {
            path.BeginFigure(new Point(from, tops[0]), isFilled: true, isClosed: true);
            for (var c = 0; c < columns; c++)
                path.LineTo(new Point(from + (c + 0.5) * step, tops[c]), false, false);
            path.LineTo(new Point(to, tops[columns - 1]), false, false);
            path.LineTo(new Point(to, bottoms[columns - 1]), false, false);
            for (var c = columns - 1; c >= 0; c--)
                path.LineTo(new Point(from + (c + 0.5) * step, bottoms[c]), false, false);
            path.LineTo(new Point(from, bottoms[0]), false, false);
        }

        outline.Freeze();
        drawing.DrawGeometry(wave.Data.Fill, null, outline);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // A right-click selects the block as well, so that the menu that opens is about it.
        if (BlockAt(Virtual(e)) is { } block)
            BlockPressed?.Invoke(block, e);
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = Virtual(e);
        if (BlockAt(point) is not { } block)
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        BlockPressed?.Invoke(block, e);
        if (block is { CanDrag: { } canDrag, Commit: not null })
        {
            if (!canDrag())
            {
                DragRefused?.Invoke(block.CannotDrag);
            }
            else if (CaptureMouse())
            {
                _drag = (block, point.X, block.Left, block.Width, block.Resizable && point.X > block.Left + block.Width - EdgePixels);
                _snapTimes = SnapTimes?.Invoke() ?? [];
            }
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panFrom is { } from)
        {
            var at = e.GetPosition(this).X;
            PanRequested?.Invoke(this, from - at);
            _panFrom = at;
            return;
        }

        var point = Virtual(e);
        if (_drag is not { } drag)
        {
            // The right edge is the handle for the length.
            Cursor = BlockAt(point) switch
            {
                { Resizable: true, CanDrag: not null } over when point.X > over.Left + over.Width - EdgePixels => Cursors.SizeWE,
                { CanDrag: not null } => Cursors.SizeAll,
                _ => Cursors.Hand,
            };
            return;
        }

        // A clip may be dragged, or stretched, past the right-hand end of the bar: the sequence is as long as
        // its clips reach, and the bars are drawn to the new length when it is let go.
        var moved = point.X - drag.PointerX;
        if (drag.Resizing)
        {
            var right = Snap(drag.Left + drag.Width + moved);
            drag.Block.Width = Math.Max(right - drag.Left, 4);
        }
        else
        {
            // Either edge of a moved block snaps, whichever is the nearer to something.
            var left = drag.Left + moved;
            var (toLeft, toRight) = (Snap(left) - left, Snap(left + drag.Width) - (left + drag.Width));
            left += Math.Abs(toLeft) <= Math.Abs(toRight) ? toLeft : toRight;
            drag.Block.Left = drag.Block.MayStartBeforeZero ? left : Math.Max(left, 0);
        }

        InvalidateVisual();
    }

    /// <summary>A point along the bar moved onto the nearest moment to snap to, when one is close enough; as it was otherwise, or while Alt is held.</summary>
    private double Snap(double x)
    {
        if (PixelsPerSecond <= 0 || _snapTimes.Count == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return x;

        var best = x;
        var nearest = SnapPixels;
        foreach (var time in _snapTimes)
        {
            var at = time * PixelsPerSecond;
            if (Math.Abs(at - x) < nearest)
                (best, nearest) = (at, Math.Abs(at - x));
        }

        return best;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is not { } drag)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        _drag = null;
        ReleaseMouseCapture();

        // A click that did not move the block changes nothing.
        if (Math.Abs(drag.Block.Left - drag.Left) > 0.5 || Math.Abs(drag.Block.Width - drag.Width) > 0.5)
            drag.Block.Commit?.Invoke(drag.Block.Left, drag.Block.Width);
        DragEnded?.Invoke();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is null)
            return;

        // Taken away mid-drag (another window came forward): the block goes back to where it was.
        (_drag.Value.Block.Left, _drag.Value.Block.Width) = (_drag.Value.Left, _drag.Value.Width);
        _drag = null;
        DragEnded?.Invoke();
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The time ruler over a tab's time bars: ticks and times for the stretch of the sequence the bars show, the end of
/// the sequence (the room after it, where clips can be dropped and dragged, shaded), and the playhead. A press or
/// a drag on it moves the playhead; the wheel zooms the bars about the pointer, and Shift + wheel or a drag with
/// the middle button moves them along.
/// </summary>
public sealed class TimelineRuler : TimelineBar
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Pen Tick = Frozen(new Pen(FrozenBrush(Color.FromArgb(150, 255, 255, 255)), 1));
    private static readonly Pen MinorTick = Frozen(new Pen(FrozenBrush(Color.FromArgb(80, 255, 255, 255)), 1));
    private static readonly Brush LabelBrush = FrozenBrush(Color.FromArgb(200, 255, 255, 255));

    // Steps between labelled ticks, in seconds, with the step between the small ticks in between.
    private static readonly (double Major, double Minor)[] Steps =
    [
        (0.05, 0.01), (0.1, 0.02), (0.2, 0.05), (0.5, 0.1), (1, 0.2), (2, 0.5), (5, 1), (10, 2), (15, 5), (30, 5), (60, 10),
        (120, 30), (300, 60), (600, 120), (900, 300), (1800, 300), (3600, 600), (7200, 1800), (14400, 3600),
    ];

    private double _start, _span, _sequence;
    private double? _panFrom;
    private bool _seeking;
    private IReadOnlyList<double> _markers = [];

    /// <summary>The colour markers are drawn in, here and on the master timeline.</summary>
    public static readonly Brush MarkerBrush = FrozenBrush(Color.FromRgb(0x7C, 0xD9, 0x92));

    /// <summary>A marker's flag: pointing down at its moment, 9 pixels wide.</summary>
    public static readonly Geometry MarkerFlag = Frozen(Geometry.Parse("M -4.5,0 L 4.5,0 L 4.5,6 L 0,10 L -4.5,6 Z"));

    /// <summary>A marker was right-clicked: which one (its index in the list shown), for its menu.</summary>
    public event Action<int>? MarkerMenuRequested;

    /// <summary>The ruler was pressed or dragged on: the playhead goes to this moment, in seconds.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>The wheel was turned over it: zoom by a factor about a point (pixels from its left).</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>Shift + wheel, or a middle-button drag: move along by so many pixels.</summary>
    public event Action<double>? PanRequested;

    /// <summary>
    /// Shows a stretch of the sequence: from <paramref name="start"/> seconds, <paramref name="span"/> seconds across,
    /// of a sequence <paramref name="sequence"/> seconds long, with its markers (in seconds).
    /// </summary>
    public void Show(double start, double span, double sequence, IReadOnlyList<double> markers)
    {
        if (start == _start && span == _span && sequence == _sequence && markers.SequenceEqual(_markers))
            return;

        (_start, _span, _sequence, _markers) = (start, span, sequence, [.. markers]);
        InvalidateVisual();
    }

    /// <summary>The marker within a few pixels of a point along the ruler, as its index; -1 for none.</summary>
    private int MarkerNear(double x)
    {
        var (best, nearest) = (-1, 6.0);
        for (var i = 0; i < _markers.Count && ActualWidth > 0 && _span > 0; i++)
        {
            var at = (_markers[i] - _start) / _span * ActualWidth;
            if (Math.Abs(at - x) < nearest)
                (best, nearest) = (i, Math.Abs(at - x));
        }

        return best;
    }

    /// <summary>The moment at a point along the ruler.</summary>
    public double TimeAt(double x) => ActualWidth > 0 ? _start + x / ActualWidth * _span : 0;

    protected override void OnRender(DrawingContext drawing)
    {
        DrawBackground(drawing);
        if (_span <= 0 || ActualWidth <= 0)
            return;

        var scale = ActualWidth / _span;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var end = (_sequence - _start) * scale;
        if (end < ActualWidth)
        {
            drawing.DrawRectangle(TimelineTrack.PastEndShade, null, new Rect(Math.Max(end, 0), 0, ActualWidth - Math.Max(end, 0), ActualHeight));
            if (end >= 0)
                drawing.DrawLine(TimelineTrack.EndLine, new Point(end + 0.5, 0), new Point(end + 0.5, ActualHeight));
        }

        // Labels at least this far apart, so that they never run into each other.
        var (major, minor) = Steps.FirstOrDefault(step => step.Major * scale >= 84, Steps[^1]);
        var bottom = ActualHeight;
        var first = Math.Floor(_start / minor) * minor;
        for (var i = 0; ; i++)
        {
            var time = first + i * minor;
            var x = (time - _start) * scale;
            if (x > ActualWidth)
                break;
            if (x < -1)
                continue;

            var isMajor = Math.Abs(time / major - Math.Round(time / major)) < 1e-6;
            var top = isMajor ? bottom - 9 : bottom - 4;
            drawing.DrawLine(isMajor ? Tick : MinorTick, new Point(Math.Round(x) + 0.5, top), new Point(Math.Round(x) + 0.5, bottom));
            if (isMajor && time >= -1e-6)
            {
                var label = new FormattedText(Format(Math.Max(time, 0), major), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, LabelBrush, dpi);
                drawing.DrawText(label, new Point(Math.Round(x) + 3, Math.Max(bottom - 9 - label.Height, 0)));
            }
        }

        // The markers: a flag at the top at each.
        foreach (var marker in _markers)
        {
            var x = (marker - _start) * scale;
            if (x < -6 || x > ActualWidth + 6)
                continue;

            drawing.PushTransform(new TranslateTransform(Math.Round(x) + 0.5, 0));
            drawing.DrawGeometry(MarkerBrush, null, MarkerFlag);
            drawing.Pop();
        }
    }

    /// <summary>A moment as the ruler writes it: as precisely as its step needs, minutes and seconds, hours when there are any.</summary>
    private static string Format(double seconds, double step)
    {
        var whole = (long)Math.Floor(seconds + 1e-6);
        var (hours, minutes, rest) = (whole / 3600, whole / 60 % 60, whole % 60);
        var text = hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
        if (step >= 1)
            return text;

        var fraction = seconds - whole;
        return step >= 0.1
            ? text + string.Create(CultureInfo.InvariantCulture, $".{(int)Math.Round(fraction * 10) % 10}")
            : text + string.Create(CultureInfo.InvariantCulture, $".{(int)Math.Round(fraction * 100) % 100:00}");
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!CaptureMouse())
            return;

        // On a marker, exactly to it.
        _seeking = true;
        var x = e.GetPosition(this).X;
        SeekRequested?.Invoke(MarkerNear(x) is >= 0 and var marker ? _markers[marker] : TimeAt(x));
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (MarkerNear(e.GetPosition(this).X) is >= 0 and var marker)
        {
            MarkerMenuRequested?.Invoke(marker);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_seeking)
            return;

        _seeking = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && CaptureMouse())
        {
            _panFrom = e.GetPosition(this).X;
            Cursor = Cursors.ScrollWE;
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panFrom is not null)
        {
            _panFrom = null;
            Cursor = null;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this).X;
        if (_panFrom is { } from)
        {
            PanRequested?.Invoke(from - at);
            _panFrom = at;
        }
        else if (_seeking)
        {
            SeekRequested?.Invoke(TimeAt(Math.Clamp(at, 0, ActualWidth)));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        (_seeking, _panFrom) = (false, null);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            PanRequested?.Invoke(-e.Delta);
        else
            ZoomRequested?.Invoke(Math.Pow(1.25, e.Delta / 120.0), e.GetPosition(this).X);
        e.Handled = true;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>The Keyframes pane's bar: the selected clip's stretch of the timeline, a diamond at each of its keyframes, the playhead.</summary>
public sealed class KeyTimelineBar : TimelineBar
{
    private double _spanLeft, _spanWidth;
    private Brush? _spanFill;

    /// <summary>The keyframes, in pixels from the left.</summary>
    public IReadOnlyList<double> Keys { get; private set; } = [];

    public void Show(double spanLeft, double spanWidth, Brush? spanFill, IReadOnlyList<double> keys)
    {
        (_spanLeft, _spanWidth, _spanFill, Keys) = (spanLeft, spanWidth, spanFill, keys);
        InvalidateVisual();
    }

    /// <summary>The keyframe within reach of a point along the bar, or null.</summary>
    public double? KeyNear(double x, double reach = 7)
    {
        double? nearest = null;
        foreach (var key in Keys)
        {
            if (Math.Abs(key - x) <= reach && (nearest is null || Math.Abs(key - x) < Math.Abs(nearest.Value - x)))
                nearest = key;
        }

        return nearest;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        DrawBackground(drawing);
        if (_spanFill is not null && _spanWidth > 0)
            drawing.DrawRoundedRectangle(_spanFill, null, new Rect(_spanLeft, (ActualHeight - 6) / 2, Math.Max(_spanWidth, 2), 6), 2, 2);
        foreach (var key in Keys)
        {
            if (key >= -8 && key <= ActualWidth + 8)
                DrawDiamond(drawing, key, ActualHeight / 2, 7);
        }
    }
}
