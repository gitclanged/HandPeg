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

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowerBorderBrushProperty = DependencyProperty.Register(
        nameof(LowerBorderBrush), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(Thickness), typeof(TimelineBar), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsRender));

    protected static readonly Pen KeyOutline = Frozen(new Pen(Brushes.Black, 0.7));

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

    /// <summary>
    /// The well's sunken edge (Pass 4.1C.2: the theme's WellBorderBrush along the top and left), drawn over the background
    /// and under everything else. It takes no room: blocks and times are where they were.
    /// </summary>
    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    /// <summary>The edge's bottom and right (WellBorderLowerBrush); null for one colour all round.</summary>
    public Brush? LowerBorderBrush
    {
        get => (Brush?)GetValue(LowerBorderBrushProperty);
        set => SetValue(LowerBorderBrushProperty, value);
    }

    /// <summary>The edge's width (its top is used for all four sides); 0, as in the flat themes, draws none.</summary>
    public Thickness BorderThickness
    {
        get => (Thickness)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
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

    // The edge's pens, kept while their brush and width stay (the theme's brushes are frozen: the same brush is the same look).
    private Pen? _edgePen, _lowerEdgePen;

    // Square ends: each half of a lit edge covers its corners whole, the lower half (drawn last) taking the two it shares.
    private static Pen EdgePen(ref Pen? kept, Brush brush, double width)
    {
        if (kept is null || !ReferenceEquals(kept.Brush, brush) || kept.Thickness != width)
            kept = Frozen(new Pen(brush, width) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square });
        return kept;
    }

    protected void DrawBackground(DrawingContext drawing)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        drawing.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, width, height));

        var edge = BorderThickness.Top;
        if (edge <= 0 || BorderBrush is not { } brush || width <= 2 * edge || height <= 2 * edge)
            return;

        // On whole device pixels, as the buttons' edges are (ButtonChrome); along the edge's centre line.
        var guidelines = new GuidelineSet([0, width], [0, height]);
        guidelines.Freeze();
        drawing.PushGuidelineSet(guidelines);
        var (left, top, right, bottom) = (edge / 2, edge / 2, width - edge / 2, height - edge / 2);
        if (LowerBorderBrush is { } lower && !ReferenceEquals(lower, brush))
        {
            drawing.DrawGeometry(null, EdgePen(ref _edgePen, brush, edge), Frozen(Polyline(new(left, bottom), new(left, top), new(right, top))));
            drawing.DrawGeometry(null, EdgePen(ref _lowerEdgePen, lower, edge), Frozen(Polyline(new(right, top), new(right, bottom), new(left, bottom))));
        }
        else
        {
            drawing.DrawRectangle(null, EdgePen(ref _edgePen, brush, edge), new Rect(left, top, right - left, bottom - top));
        }

        drawing.Pop();
    }

    private static StreamGeometry Polyline(Point from, Point corner, Point to)
    {
        var line = new StreamGeometry();
        using var path = line.Open();
        path.BeginFigure(from, false, false);
        path.LineTo(corner, true, false);
        path.LineTo(to, true, false);
        return line;
    }

    /// <summary>A keyframe's diamond, centred on a point.</summary>
    protected static void DrawDiamond(DrawingContext drawing, double x, double y, double radius) =>
        drawing.DrawGeometry(HandPegApp.Services.ThemeManager.KeyFill, KeyOutline, Diamonds([x], double.MaxValue, y, radius));

    /// <summary>
    /// Keyframes' diamonds as one shape: one centred on each of the given x, all at the height y, those that
    /// would lie wholly outside 0 to <paramref name="width"/> left out. Made once and frozen, however many keys.
    /// </summary>
    protected static Geometry Diamonds(IReadOnlyList<double> xs, double width, double y, double radius)
    {
        var diamonds = new StreamGeometry();
        using (var path = diamonds.Open())
        {
            foreach (var x in xs)
            {
                if (x < -radius || x > width + radius)
                    continue;

                path.BeginFigure(new Point(x, y - radius), isFilled: true, isClosed: true);
                path.LineTo(new Point(x + radius, y), true, false);
                path.LineTo(new Point(x, y + radius), true, false);
                path.LineTo(new Point(x - radius, y), true, false);
            }
        }

        diamonds.Freeze();
        return diamonds;
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

/// <summary>What a drag does to a block (and, for a roll, the block it meets).</summary>
public enum BlockEdit
{
    /// <summary>Moved along its bar (or onto another track's).</summary>
    Move,

    /// <summary>Its right edge: how long it is.</summary>
    TrimEnd,

    /// <summary>Its left edge: where it starts, and what of its file it starts with.</summary>
    TrimStart,

    /// <summary>The cut where it meets the block after it: its end and the next one's start move together.</summary>
    Roll,

    /// <summary>Alt-dragged inside: what of its file it plays, with the block left where it is.</summary>
    Slip,
}

/// <summary>A drag of a block that was let go: its new left edge and width, how far its file was slipped (pixels), whether
/// it ripples (Shift: the later clips move by as much as its end did), and for a roll the block after it, as it is now.</summary>
public readonly record struct BlockEditDone(BlockEdit Kind, double Left, double Width, double Slip, bool Ripple, TimelineBlock? Neighbor);

/// <summary>A gain line dragged on a block: its new gain (dB), or a fade handle's new length (seconds) at its start or end.</summary>
public readonly record struct GainEditDone(double GainDb, double FadeIn, double FadeOut);

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

    /// <summary>Whether its left edge can be dragged too (trimming where it starts in its file), and it can be rolled and rippled.</summary>
    public bool TrimmableStart { get; init; }

    /// <summary>Whether Alt-dragging inside it slips what of its file it plays (a video or a sound with a length).</summary>
    public bool Slippable { get; init; }

    /// <summary>What letting go of a trim of its start, a roll, a slip or a ripple does; null for a block that only moves and trims its end.</summary>
    public Action<BlockEditDone>? Edit { get; init; }

    /// <summary>
    /// Its sound's gain line, for a clip that carries sound: the gain in dB (drawn as a line, 0 dB at two thirds of its
    /// height), and its fades in and out (seconds); null for a block with none. Dragged, <see cref="GainEdit"/> is told.
    /// </summary>
    public (double GainDb, double FadeIn, double FadeOut)? Gain { get; set; }

    /// <summary>What letting go of a dragged gain line does.</summary>
    public Action<GainEditDone>? GainEdit { get; init; }

    /// <summary>
    /// The clip's fades in and out (seconds of the timeline) and their transition styles: handles at its top corners (or
    /// as far in as each fade), dragged along to set them, and each fade shaded above its curve. Null for a block with none.
    /// </summary>
    public (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle)? Fades { get; set; }

    /// <summary>What letting go of a dragged fade handle does: the fade in (true) or out, and its new length in seconds.</summary>
    public Action<bool, double>? FadeEdit { get; init; }

    /// <summary>A crossfade from the block before it on its bar, in seconds: drawn as a pill across their cut, centred on it.</summary>
    public double CrossfadeIn { get; init; }

    /// <summary>How many pixels a second of its file takes on the bar (its speed taken in), for a slip and for fades; 0 for none.</summary>
    public double PixelsPerMediaSecond { get; init; }

    /// <summary>What letting go of a moved block does, given its new left edge and width in pixels.</summary>
    public Action<double, double>? Commit { get; init; }

    /// <summary>What to say when it cannot be dragged.</summary>
    public string CannotDrag { get; init; } = "";

    /// <summary>May be dragged past the left end of the bar: a sound that was slipped.</summary>
    public bool MayStartBeforeZero { get; init; }

    /// <summary>
    /// Whether the block may be let go on another bar (another track's row), given that bar's data and the block's
    /// left edge in pixels; null for a block that stays on its own bar.
    /// </summary>
    public Func<object, double, bool>? CanMoveTo { get; init; }

    /// <summary>What letting go of the block on another bar does, given that bar's data and the block's left edge in pixels.</summary>
    public Action<object, double>? MoveTo { get; init; }

    internal FormattedText? Label;

    // What its drawing is made of, made once and used again while the block keeps its place and size (a pan or a
    // playhead move draws it again unchanged): its rounded outline, and its keyframes' diamonds as one shape.
    internal Rect ShapeRect;
    internal Geometry? Shape;
    internal Geometry? Diamonds;

    /// <summary>The block's rounded outline at <paramref name="rect"/>: the one made before when it is still there.</summary>
    internal Geometry ShapeAt(Rect rect)
    {
        if (Shape is null || ShapeRect != rect)
        {
            // Resized (its right edge dragged): the diamonds are laid out again for its new width.
            if (ShapeRect.Size != rect.Size)
                Diamonds = null;

            var shape = new RectangleGeometry(rect, 3, 3);
            shape.Freeze();
            (Shape, ShapeRect) = (shape, rect);
        }

        return Shape;
    }
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

    /// <summary>The gain line's reach: from silence at the bottom of a block to +12 dB at its top, 0 dB a third of the way down.</summary>
    public const double LowestGain = -40, HighestGain = 12;

    /// <summary>How close to the gain line (or a fade handle) the pointer has to be to take hold of it.</summary>
    private const double GainReach = 4;

    /// <summary>How close to an edge of the bar a drag has to come to move the time bars along by itself.</summary>
    private const double AutoScrollZone = 28;

    private static readonly Pen GainPen = Frozen(new Pen(FrozenBrush(Color.FromArgb(230, 0xFF, 0xD5, 0x4F)), 1.2));
    private static readonly Brush HandleFill = FrozenBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));

    /// <summary>How close to a fade handle the pointer has to be, along the block, to take hold of it.</summary>
    private const double FadeReach = 6;

    /// <summary>How deep the band along a block's top is where its fade handles are (below it, its edges trim).</summary>
    private const double FadeBand = 10;

    private static readonly Brush FadeShade = FrozenBrush(Color.FromArgb(120, 0, 0, 0));
    private static readonly Pen FadeCurve = Frozen(new Pen(FrozenBrush(Color.FromArgb(235, 255, 255, 255)), 1.3));
    private static readonly Brush FadeHandleFill = FrozenBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Pen FadeHandleOutline = Frozen(new Pen(FrozenBrush(Color.FromArgb(200, 0, 0, 0)), 1));
    private static readonly Brush ReadoutFill = FrozenBrush(Color.FromArgb(230, 24, 24, 24));
    private static readonly Brush CrossfadeFill = FrozenBrush(Color.FromArgb(170, 0x4F, 0xC3, 0xF7));
    private static readonly Pen CrossfadeLine = Frozen(new Pen(FrozenBrush(Color.FromArgb(235, 255, 255, 255)), 1.2));

    private static readonly Pen SelectedOutline = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Brush LabelShade = FrozenBrush(Color.FromArgb(150, 0, 0, 0));

    /// <summary>The room after the end of the sequence: darker, and where a clip dropped or dragged makes it longer.</summary>
    internal static readonly Brush PastEndShade = FrozenBrush(Color.FromArgb(90, 0, 0, 0));
    internal static readonly Pen EndLine = Frozen(new Pen(FrozenBrush(Color.FromArgb(150, 255, 255, 255)), 1));

    /// <summary>How close, in pixels, a dragged edge has to come to something to snap to it.</summary>
    public const double SnapPixels = 8;
    private static readonly Typeface LabelFace = new("Segoe UI");

    // A block being dragged: what the drag does, where the pointer and the block (and for a roll the next block) were.
    private sealed class BlockDrag
    {
        public required TimelineBlock Block { get; init; }
        public required BlockEdit Kind { get; init; }
        public double PointerX { get; init; }
        public double Left { get; init; }
        public double Width { get; init; }
        public bool Ripple { get; init; }
        public TimelineBlock? Neighbor { get; init; }
        public double NeighborLeft { get; init; }
        public double NeighborWidth { get; init; }

        /// <summary>A slip so far, in pixels: what of the file shows has moved with the pointer by this much.</summary>
        public double Slip { get; set; }

        public bool Resizing => Kind != BlockEdit.Move;
    }

    private BlockDrag? _drag;

    // A gain line being dragged: the block, which part, the pointer and the values where it started.
    private (TimelineBlock Block, int Part, Point From, (double GainDb, double FadeIn, double FadeOut) Start)? _gainDrag;

    // A fade handle being dragged: the block, which fade (true: in), and the block's fades when the drag began.
    private (TimelineBlock Block, bool In, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) Start)? _fadeDrag;

    // The block the pointer is over: its fade handles show while it is (and always where it has a fade).
    private TimelineBlock? _hovered;

    /// <summary>
    /// The fade handle the last right-button press was on: its block, and which fade (true: in); null when the press was
    /// anywhere else. The menu that opens on the release offers that fade's transition styles.
    /// </summary>
    public (TimelineBlock Block, bool In)? MenuFade { get; private set; }

    // The last pointer during a drag, on the bar as it is laid out: what an auto-scroll tick drags on with.
    private Point _lastPointer;
    private readonly System.Windows.Threading.DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public TimelineTrack()
    {
        // The frames in a block are thumbnails, drawn smaller than they are and many at a time: the cheapest
        // scaling there is, not the smooth one, which costs the most exactly while a timeline is scrolled.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
        _autoScroll.Tick += AutoScroll_Tick;
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

    public bool IsDragging => _drag is not null || _gainDrag is not null || _fadeDrag is not null;

    /// <summary>
    /// How far the bar has been moved along, in pixels: its blocks are laid out as if it were as wide as the
    /// zoom makes it, and this is how much of that lies to the left of what is seen.
    /// </summary>
    public double ViewOffsetX { get; set; }

    // The view's slide, frozen, and made again only when the view moves.
    private TranslateTransform? _viewShift;

    /// <summary>The transform that slides the seen part of the bar into view (see ViewOffsetX).</summary>
    private Transform ViewShift()
    {
        if (_viewShift is null || _viewShift.X != -ViewOffsetX)
        {
            _viewShift = new TranslateTransform(-ViewOffsetX, 0);
            _viewShift.Freeze();
        }

        return _viewShift;
    }

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
        drawing.PushTransform(ViewShift());
        foreach (var block in Blocks)
        {
            // What is off the bar is not drawn at all.
            if (block.Left - ViewOffsetX > ActualWidth || block.Left + block.Width - ViewOffsetX < 0)
                continue;

            var rect = new Rect(block.Left, block.Top, Math.Max(block.Width, 3), Math.Max(block.Height, 1));
            var shape = block.ShapeAt(rect);
            drawing.DrawGeometry(block.Fill, null, shape);
            drawing.PushClip(shape);

            // Being slipped: what of its file it shows slides with the pointer, the block staying put.
            var slip = _drag is { Kind: BlockEdit.Slip } slipping && ReferenceEquals(slipping.Block, block) ? slipping.Slip : 0;
            if (slip != 0)
                drawing.PushTransform(new TranslateTransform(slip, 0));

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

            if (slip != 0)
                drawing.Pop();

            if (block.Gain is { } gain)
                DrawGain(drawing, rect, gain);

            // Its fades: shaded above their curves, with a handle at each (at the corners while there is none, when the
            // pointer is over it).
            if (block.Fades is { } fades && block.FadeEdit is not null)
                DrawFades(drawing, rect, fades, ReferenceEquals(block, _hovered) || ReferenceEquals(block, _fadeDrag?.Block));

            // The diamonds are one shape for the block, made once from its keys, laid where the block is.
            if (block.Keys is { Count: > 0 } keys)
            {
                block.Diamonds ??= Diamonds(keys, rect.Width, rect.Height - 6, 4.5);
                drawing.PushTransform(new TranslateTransform(rect.Left, rect.Top));
                drawing.DrawGeometry(HandPegApp.Services.ThemeManager.KeyFill, KeyOutline, block.Diamonds);
                drawing.Pop();
            }

            if (block.Name.Length > 0 && rect.Width > 14)
            {
                var label = block.Label ??= new FormattedText(block.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, dpi)
                {
                    MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                };

                // Set only when it changes: setting it lays the text out again.
                var widest = Math.Max(rect.Width - 10, 4);
                if (label.MaxTextWidth != widest)
                    label.MaxTextWidth = widest;
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

        // A crossfade across a cut: a pill over the two blocks, as long as the crossfade and centred on the cut.
        foreach (var block in Blocks)
        {
            if (block.CrossfadeIn > 0.01 && PixelsPerSecond > 0)
                DrawCrossfade(drawing, block);
        }

        // The fade being dragged, said as it changes.
        if (_fadeDrag is { } fadeDrag && fadeDrag.Block.Fades is { } dragged)
            DrawFadeReadout(drawing, fadeDrag.Block, fadeDrag.In, dragged, dpi);

        drawing.Pop();

        // Where a block dragged from another bar would go on this one: blue where it can, red where it cannot.
        if (_ghost is { } ghost)
        {
            drawing.PushTransform(ViewShift());
            var rect = new Rect(ghost.Left, 1, Math.Max(ghost.Width, 3), Math.Max(ActualHeight - 2, 1));
            drawing.DrawRoundedRectangle(ghost.Fits ? GhostFill : RefusedFill, ghost.Fits ? GhostOutline : RefusedOutline, rect, 3, 3);
            drawing.Pop();
        }

        // Where a file being dragged over the bar would land.
        if (!double.IsNaN(_dropX) && _dropX >= -2 && _dropX <= ActualWidth + 2)
            drawing.DrawRectangle(DropMarkBrush, null, new Rect(_dropX - 1.5, 0, 3, ActualHeight));
    }

    /// <summary>Where the gain line of a block lies, from the top of the block, for a gain in dB.</summary>
    private static double GainY(Rect rect, double gainDb) =>
        rect.Top + 3 + (1 - (Math.Clamp(gainDb, LowestGain, HighestGain) - LowestGain) / (HighestGain - LowestGain)) * Math.Max(rect.Height - 6, 1);

    /// <summary>The gain in dB at a height in a block: the line's place, the other way round.</summary>
    private static double GainAt(Rect rect, double y) =>
        Math.Round(LowestGain + (1 - Math.Clamp((y - rect.Top - 3) / Math.Max(rect.Height - 6, 1), 0, 1)) * (HighestGain - LowestGain), 1);

    /// <summary>
    /// A clip's gain line: level across it at its gain, sloping down to the bottom corners over its fades (its sound's
    /// envelope), with a grip in its middle. Its fades are set by the handles at the block's top corners (DrawFades).
    /// </summary>
    private void DrawGain(DrawingContext drawing, Rect rect, (double GainDb, double FadeIn, double FadeOut) gain)
    {
        var y = GainY(rect, gain.GainDb);
        var (fadeIn, fadeOut) = FadeSpan(rect, gain.FadeIn, gain.FadeOut);
        var (inX, outX) = (rect.Left + fadeIn, rect.Right - fadeOut);
        drawing.DrawLine(GainPen, new Point(rect.Left, fadeIn > 0.5 ? rect.Bottom : y), new Point(inX, y));
        drawing.DrawLine(GainPen, new Point(inX, y), new Point(outX, y));
        drawing.DrawLine(GainPen, new Point(outX, y), new Point(rect.Right, fadeOut > 0.5 ? rect.Bottom : y));
        drawing.DrawRectangle(HandleFill, null, new Rect((inX + outX) / 2 - 2.5, y - 2.5, 5, 5));
    }

    /// <summary>How wide a block's fades are drawn, in pixels: their seconds at the bar's scale, never more than the block.</summary>
    private (double In, double Out) FadeSpan(Rect rect, double fadeInSeconds, double fadeOutSeconds)
    {
        var scale = PixelsPerSecond > 0 ? PixelsPerSecond : 0;
        var fadeIn = Math.Clamp(fadeInSeconds * scale, 0, rect.Width);
        var fadeOut = Math.Clamp(fadeOutSeconds * scale, 0, rect.Width - fadeIn);
        return (fadeIn, fadeOut);
    }

    /// <summary>Which part of a block's gain line a point is on: 1 the line; 0 none.</summary>
    private int GainPartAt(TimelineBlock block, Point point)
    {
        if (block.Gain is not { } gain || block.GainEdit is null)
            return 0;

        var rect = BlockRect(block);
        var y = GainY(rect, gain.GainDb);
        if (Math.Abs(point.Y - y) > GainReach + 1)
            return 0;
        return point.X > rect.Left + EdgePixels && point.X < rect.Right - EdgePixels ? 1 : 0;
    }

    private static Rect BlockRect(TimelineBlock block) => new(block.Left, block.Top, Math.Max(block.Width, 3), Math.Max(block.Height, 1));

    // ----- Fades -----

    /// <summary>Which of a block's fade handles a point is on: true the fade in's, false the fade out's; null neither.</summary>
    private bool? FadeHandleAt(TimelineBlock block, Point point)
    {
        if (block.Fades is not { } fades || block.FadeEdit is null || point.Y > block.Top + FadeBand || point.Y < block.Top - 1)
            return null;

        var rect = BlockRect(block);
        var (fadeIn, fadeOut) = FadeSpan(rect, fades.In, fades.Out);
        var (toIn, toOut) = (Math.Abs(point.X - HandleX(rect, rect.Left + fadeIn)), Math.Abs(point.X - HandleX(rect, rect.Right - fadeOut)));
        if (Math.Min(toIn, toOut) > FadeReach)
            return null;
        return toIn <= toOut;
    }

    /// <summary>Where a fade handle is drawn: at its fade's end, kept inside the block.</summary>
    private static double HandleX(Rect rect, double x) => Math.Clamp(x, rect.Left + 4, Math.Max(rect.Right - 4, rect.Left + 4));

    /// <summary>
    /// A block's fades: each one shaded above its curve (from nothing at the block's foot to all of it at its top, or
    /// back, in the curve its transition style follows), and a handle where each fade ends, at the top. With no fade, the
    /// handles are shown at the corners only while the pointer is over the block.
    /// </summary>
    private void DrawFades(DrawingContext drawing, Rect rect, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) fades, bool hovered)
    {
        var (fadeIn, fadeOut) = FadeSpan(rect, fades.In, fades.Out);
        if (fadeIn > 0.5)
            DrawFadeRamp(drawing, rect, rect.Left, fadeIn, rising: true, fades.InStyle);
        if (fadeOut > 0.5)
            DrawFadeRamp(drawing, rect, rect.Right - fadeOut, fadeOut, rising: false, fades.OutStyle);

        if (hovered || fadeIn > 0.5)
            DrawFadeHandle(drawing, rect, rect.Left + fadeIn);
        if (hovered || fadeOut > 0.5)
            DrawFadeHandle(drawing, rect, rect.Right - fadeOut);
    }

    private static void DrawFadeRamp(DrawingContext drawing, Rect rect, double left, double width, bool rising, Models.TransitionStyle style)
    {
        var easing = new Models.Keyframe(0, 0, Models.TransitionStyles.Easing(style));
        var steps = Math.Clamp((int)(width / 3), 2, 32);
        var points = new Point[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var u = (double)i / steps;
            var shown = Models.EasingCurves.Evaluate(rising ? u : 1 - u, easing);
            points[i] = new Point(left + u * width, rect.Bottom - Math.Clamp(shown, -0.1, 1.15) * rect.Height);
        }

        // The shade: the top edge across the fade, then back along the curve.
        var shade = new StreamGeometry();
        using (var path = shade.Open())
        {
            path.BeginFigure(new Point(left, rect.Top), isFilled: true, isClosed: true);
            path.LineTo(new Point(left + width, rect.Top), false, false);
            for (var i = steps; i >= 0; i--)
                path.LineTo(points[i], false, false);
        }

        shade.Freeze();
        drawing.DrawGeometry(FadeShade, null, shade);

        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i <= steps; i++)
                path.LineTo(points[i], true, true);
        }

        curve.Freeze();
        drawing.DrawGeometry(null, FadeCurve, curve);
    }

    private static void DrawFadeHandle(DrawingContext drawing, Rect rect, double x) =>
        drawing.DrawRoundedRectangle(FadeHandleFill, FadeHandleOutline, new Rect(HandleX(rect, x) - 3.5, rect.Top + 1.5, 7, 7), 1.5, 1.5);

    /// <summary>A pill across the cut a crossfade spans, with a cross in it: as long as the crossfade, centred on the cut.</summary>
    private void DrawCrossfade(DrawingContext drawing, TimelineBlock block)
    {
        var half = block.CrossfadeIn * PixelsPerSecond / 2;
        var height = Math.Min(Math.Max(block.Height * 0.42, 8), 16);
        var pill = new Rect(block.Left - half, block.Top + (block.Height - height) / 2, Math.Max(half * 2, 6), height);
        drawing.DrawRoundedRectangle(CrossfadeFill, null, pill, height / 2, height / 2);
        var inset = Math.Min(height / 2, pill.Width / 2);
        drawing.DrawLine(CrossfadeLine, new Point(pill.Left + inset, pill.Top + 2), new Point(pill.Right - inset, pill.Bottom - 2));
        drawing.DrawLine(CrossfadeLine, new Point(pill.Left + inset, pill.Bottom - 2), new Point(pill.Right - inset, pill.Top + 2));
    }

    /// <summary>The fade being dragged, in words beside its handle: "Fade In: 0.75 s", with its style.</summary>
    private void DrawFadeReadout(DrawingContext drawing, TimelineBlock block, bool fadeIn, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) fades, double dpi)
    {
        var rect = BlockRect(block);
        var (inWidth, outWidth) = FadeSpan(rect, fades.In, fades.Out);
        var x = HandleX(rect, fadeIn ? rect.Left + inWidth : rect.Right - outWidth);
        var text = new FormattedText(FadeWords(fadeIn, fadeIn ? fades.In : fades.Out, fadeIn ? fades.InStyle : fades.OutStyle),
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, dpi);

        // Beside the handle, on the side of the block it fades (and inside what is seen of the bar).
        var left = fadeIn ? x + 7 : x - 7 - text.Width - 8;
        left = Math.Clamp(left, ViewOffsetX + 2, Math.Max(ViewOffsetX + ActualWidth - text.Width - 10, ViewOffsetX + 2));
        var box = new Rect(left, rect.Top + 1, text.Width + 8, text.Height + 2);
        drawing.DrawRoundedRectangle(ReadoutFill, null, box, 3, 3);
        drawing.DrawText(text, new Point(box.Left + 4, box.Top + 1));
    }

    private static string FadeWords(bool fadeIn, double seconds, Models.TransitionStyle style) =>
        string.Create(CultureInfo.InvariantCulture, $"Fade {(fadeIn ? "In" : "Out")}: {seconds:0.00} s · {Models.TransitionStyles.Label(style)}");

    private static readonly Brush DropMarkBrush = FrozenBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly Brush GhostFill = FrozenBrush(Color.FromArgb(70, 0x4F, 0xC3, 0xF7));
    private static readonly Pen GhostOutline = Frozen(new Pen(DropMarkBrush, 1.5));
    private static readonly Brush RefusedFill = FrozenBrush(Color.FromArgb(60, 0xE5, 0x39, 0x35));
    private static readonly Pen RefusedOutline = Frozen(new Pen(FrozenBrush(Color.FromRgb(0xE5, 0x39, 0x35)), 1.5));

    // A block being dragged over this bar from another: where it is, and whether it may be let go here.
    private (double Left, double Width, bool Fits)? _ghost;

    private void ShowGhost((double Left, double Width, bool Fits)? ghost)
    {
        if (_ghost == ghost)
            return;

        _ghost = ghost;
        InvalidateVisual();
    }

    // The bar a dragged block is over, when that is another track's row.
    private TimelineTrack? _dragOver;

    /// <summary>The bar of another track's row under the pointer (in this window); null over this bar or anything else.</summary>
    private TimelineTrack? OtherTrackUnder(MouseEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
            return null;

        // The bars draw themselves, so a hit on one is the bar itself; anything else in a row is not one.
        return window.InputHitTest(e.GetPosition(window)) is TimelineTrack track && !ReferenceEquals(track, this)
            && track.Tag is null && track.DataContext is not null && track.IsVisible ? track : null;
    }

    /// <summary>Follows a dragged block that may go to another track: the bar under the pointer shows where it would go there.</summary>
    private void TrackDragOver(MouseEventArgs e, TimelineBlock block)
    {
        var over = block.CanMoveTo is null ? null : OtherTrackUnder(e);
        if (!ReferenceEquals(over, _dragOver))
            _dragOver?.ShowGhost(null);
        _dragOver = over;
        over?.ShowGhost((block.Left, block.Width, block.CanMoveTo!(over.DataContext, block.Left)));
    }

    private void EndTrackDrag()
    {
        _dragOver?.ShowGhost(null);
        _dragOver = null;
    }
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
        // A right-click selects the block as well, so that the menu that opens is about it; on a fade handle, the menu
        // that opens is that fade's.
        var point = Virtual(e);
        MenuFade = null;
        if (BlockAt(point) is { } block)
        {
            if (FadeHandleAt(block, point) is { } fadeIn)
                MenuFade = (block, fadeIn);
            BlockPressed?.Invoke(block, e);
        }

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

        // A fade handle, at the top of the block: dragged along, its fade's length (snapping to other clips' edges, on
        // any track, and the playhead: where a clip under or over this one starts or ends is where a crossfade with it ends).
        if (FadeHandleAt(block, point) is { } fadeIn && block.Fades is { } fades && CaptureMouse())
        {
            _fadeDrag = (block, fadeIn, fades);
            _snapTimes = SnapTimes?.Invoke() ?? [];
            ToolTip = null;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // The gain line, on a clip with sound: dragged up and down.
        if (GainPartAt(block, point) is > 0 and var part && block.Gain is { } gain && CaptureMouse())
        {
            _gainDrag = (block, part, point, gain);
            e.Handled = true;
            return;
        }

        if (block is { CanDrag: { } canDrag, Commit: not null })
        {
            if (!canDrag())
            {
                DragRefused?.Invoke(block.CannotDrag);
            }
            else if (CaptureMouse())
            {
                _drag = StartDrag(block, point);
                _snapTimes = SnapTimes?.Invoke() ?? [];
                _lastPointer = point;
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// What a press on a block starts: on its right edge a trim of its end, on its left edge a trim of its start, on the
    /// cut where it meets the next block a roll of both, Alt inside it a slip, anywhere else a move. Shift on an edge
    /// ripples (the later clips follow its end).
    /// </summary>
    private BlockDrag StartDrag(TimelineBlock block, Point point)
    {
        var modifiers = Keyboard.Modifiers;
        var ripple = modifiers.HasFlag(ModifierKeys.Shift) && block.Edit is not null;
        var (left, right) = (block.Left, block.Left + block.Width);

        // The cut between this block and the next, when they meet: the block on either side of it can be pressed.
        if (!ripple && block.Edit is not null && block.TrimmableStart)
        {
            var (before, after) = point.X > right - EdgePixels ? (block, NextTo(block, after: true))
                : point.X < left + EdgePixels ? (NextTo(block, after: false), block) : (null, null);
            if (before is { Edit: not null, TrimmableStart: true } && after is { Edit: not null, TrimmableStart: true })
            {
                return new BlockDrag
                {
                    Block = before, Kind = BlockEdit.Roll, PointerX = point.X, Left = before.Left, Width = before.Width,
                    Neighbor = after, NeighborLeft = after.Left, NeighborWidth = after.Width,
                };
            }
        }

        var kind = block.Resizable && point.X > right - EdgePixels ? BlockEdit.TrimEnd
            : block.TrimmableStart && block.Edit is not null && point.X < left + EdgePixels ? BlockEdit.TrimStart
            : block.Slippable && block.Edit is not null && modifiers.HasFlag(ModifierKeys.Alt) ? BlockEdit.Slip
            : BlockEdit.Move;
        return new BlockDrag { Block = block, Kind = kind, PointerX = point.X, Left = left, Width = block.Width, Ripple = ripple && kind is BlockEdit.TrimEnd or BlockEdit.TrimStart };
    }

    /// <summary>The block whose edge meets this one's (its end, or its start), on this bar; null when there is none within a pixel.</summary>
    private TimelineBlock? NextTo(TimelineBlock block, bool after)
    {
        foreach (var other in Blocks)
        {
            if (ReferenceEquals(other, block) || Math.Abs(other.Top - block.Top) > 0.5)
                continue;
            if (after ? Math.Abs(other.Left - (block.Left + block.Width)) < 1 : Math.Abs(other.Left + other.Width - block.Left) < 1)
                return other;
        }

        return null;
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
        if (_fadeDrag is { } fadeDrag)
        {
            FollowFade(fadeDrag, point);
            return;
        }

        if (_gainDrag is { } gainDrag)
        {
            FollowGain(gainDrag, point);
            return;
        }

        if (_drag is not { } drag)
        {
            // The edges are the handles for the length and the start; the cut between two clips rolls; Alt slips; the
            // top corners (or as far in as a fade) are the fades' handles.
            var over = BlockAt(point);
            Hover(over);
            var fadeHandle = over is null ? null : FadeHandleAt(over, point);
            ShowFadeTip(over, fadeHandle);
            Cursor = over is null ? Cursors.Hand
                : fadeHandle is not null ? Cursors.Cross
                : GainPartAt(over, point) is 1 ? Cursors.SizeNS
                : over is { Resizable: true, CanDrag: not null } && point.X > over.Left + over.Width - EdgePixels ? Cursors.SizeWE
                : over is { TrimmableStart: true, CanDrag: not null, Edit: not null } && point.X < over.Left + EdgePixels ? Cursors.SizeWE
                : over is { Slippable: true, Edit: not null } && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Cursors.ScrollWE
                : over.CanDrag is not null ? Cursors.SizeAll
                : Cursors.Hand;
            return;
        }

        _lastPointer = point;
        Follow(drag, point, e);

        // Near an edge of the bar, the time bars move along by themselves while the pointer stays there.
        var seen = point.X - ViewOffsetX;
        var nearEdge = seen < AutoScrollZone || seen > ActualWidth - AutoScrollZone;
        if (nearEdge && !_autoScroll.IsEnabled)
            _autoScroll.Start();
        else if (!nearEdge)
            _autoScroll.Stop();
    }

    /// <summary>Lays a dragged block (and a rolled neighbour) out for where the pointer is.</summary>
    private void Follow(BlockDrag drag, Point point, MouseEventArgs? e)
    {
        // A clip may be dragged, or stretched, past the right-hand end of the bar: the sequence is as long as
        // its clips reach, and the bars are drawn to the new length when it is let go.
        var moved = point.X - drag.PointerX;
        switch (drag.Kind)
        {
            case BlockEdit.TrimEnd:
                drag.Block.Width = Math.Max(Snap(drag.Left + drag.Width + moved) - drag.Left, 4);
                break;

            case BlockEdit.TrimStart:
            {
                var right = drag.Left + drag.Width;
                var left = Math.Min(Snap(drag.Left + moved), right - 4);
                (drag.Block.Left, drag.Block.Width) = (Math.Max(left, 0), right - Math.Max(left, 0));
                break;
            }

            case BlockEdit.Roll when drag.Neighbor is { } next:
            {
                var cut = Math.Clamp(Snap(drag.Left + drag.Width + moved), drag.Left + 4, drag.NeighborLeft + drag.NeighborWidth - 4);
                drag.Block.Width = cut - drag.Left;
                (next.Left, next.Width) = (cut, drag.NeighborLeft + drag.NeighborWidth - cut);
                break;
            }

            case BlockEdit.Slip:
                drag.Slip = moved;
                break;

            default:
            {
                // Either edge of a moved block snaps, whichever is the nearer to something.
                var left = drag.Left + moved;
                var (toLeft, toRight) = (Snap(left) - left, Snap(left + drag.Width) - (left + drag.Width));
                left += Math.Abs(toLeft) <= Math.Abs(toRight) ? toLeft : toRight;
                drag.Block.Left = drag.Block.MayStartBeforeZero ? left : Math.Max(left, 0);
                if (e is not null)
                    TrackDragOver(e, drag.Block);
                break;
            }
        }

        InvalidateVisual();
    }

    /// <summary>A gain line dragged up or down: the gain.</summary>
    private void FollowGain((TimelineBlock Block, int Part, Point From, (double GainDb, double FadeIn, double FadeOut) Start) drag, Point point)
    {
        var rect = BlockRect(drag.Block);
        drag.Block.Gain = drag.Start with { GainDb = GainAt(rect, GainY(rect, drag.Start.GainDb) + point.Y - drag.From.Y) };
        InvalidateVisual();
    }

    /// <summary>
    /// A fade handle dragged along: its fade's length, from the block's edge to where the handle is, snapped (to the
    /// other clips' edges and the playhead) and no longer than the block leaves beside its other fade. The gain line's
    /// slope follows.
    /// </summary>
    private void FollowFade((TimelineBlock Block, bool In, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) Start) drag, Point point)
    {
        if (PixelsPerSecond <= 0)
            return;

        var (block, start) = (drag.Block, drag.Start);
        var rect = BlockRect(block);
        var x = Snap(point.X);
        var room = Math.Max(rect.Width / PixelsPerSecond - (drag.In ? start.Out : start.In), 0);
        var seconds = Math.Round(Math.Clamp((drag.In ? x - rect.Left : rect.Right - x) / PixelsPerSecond, 0, room), 2);
        block.Fades = drag.In ? start with { In = seconds } : start with { Out = seconds };
        if (block.Gain is { } gain)
            block.Gain = drag.In ? gain with { FadeIn = seconds } : gain with { FadeOut = seconds };
        InvalidateVisual();
    }

    /// <summary>The pointer is over another block (or none): the one it left hides its handles, the one it is over shows them.</summary>
    private void Hover(TimelineBlock? block)
    {
        if (ReferenceEquals(block, _hovered))
            return;

        var shows = (_hovered?.Fades is not null) || (block?.Fades is not null);
        _hovered = block;
        if (shows)
            InvalidateVisual();
    }

    // The fade a tooltip is up for, so it is set once per handle, not on every move.
    private (TimelineBlock Block, bool In)? _tipFor;

    /// <summary>Over a fade handle, a tooltip saying what it is and what it does; elsewhere, none.</summary>
    private void ShowFadeTip(TimelineBlock? block, bool? fadeIn)
    {
        (TimelineBlock, bool)? tip = block is not null && fadeIn is { } which ? (block, which) : null;
        if (tip == _tipFor)
            return;

        _tipFor = tip;
        if (block?.Fades is not { } fades || fadeIn is not { } isIn)
        {
            ToolTip = null;
            return;
        }

        var seconds = isIn ? fades.In : fades.Out;
        ToolTip = (seconds > 0.005 ? FadeWords(isIn, seconds, isIn ? fades.InStyle : fades.OutStyle) : $"Fade {(isIn ? "In" : "Out")}: none")
            + $". Drag {(isIn ? "right" : "left")} to fade the clip's picture and sound {(isIn ? "in" : "out")} over what is under it; it snaps to where clips on the other tracks start and end"
            + ", for a crossfade with one. Right-click for the transition style.";
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_fadeDrag is null)
        {
            Hover(null);
            ShowFadeTip(null, null);
        }
    }

    private void AutoScroll_Tick(object? sender, EventArgs e)
    {
        if (_drag is not { } drag || Mouse.LeftButton != MouseButtonState.Pressed || ActualWidth <= 0)
        {
            _autoScroll.Stop();
            return;
        }

        // Faster the further into the edge the pointer is: up to about 3% of the bar every tick.
        var seen = _lastPointer.X - ViewOffsetX;
        var depth = seen < AutoScrollZone ? seen - AutoScrollZone : seen > ActualWidth - AutoScrollZone ? seen - (ActualWidth - AutoScrollZone) : 0;
        var pixels = Math.Clamp(depth / AutoScrollZone, -1.5, 1.5) * ActualWidth * 0.03;
        if (Math.Abs(pixels) < 0.5)
            return;

        // The window moves every bar along, this one with them (its ViewOffsetX), and the pointer is where it was on screen.
        var before = ViewOffsetX;
        PanRequested?.Invoke(this, pixels);
        _lastPointer.X += ViewOffsetX - before;
        Follow(drag, _lastPointer, null);
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
        _autoScroll.Stop();
        if (_fadeDrag is { } fadeDrag)
        {
            _fadeDrag = null;
            ReleaseMouseCapture();
            if (fadeDrag.Block.Fades is { } fades && fades != fadeDrag.Start)
                fadeDrag.Block.FadeEdit?.Invoke(fadeDrag.In, fadeDrag.In ? fades.In : fades.Out);
            InvalidateVisual();
            DragEnded?.Invoke();
            return;
        }

        if (_gainDrag is { } gainDrag)
        {
            _gainDrag = null;
            ReleaseMouseCapture();
            if (gainDrag.Block.Gain is { } gain && gain != gainDrag.Start)
                gainDrag.Block.GainEdit?.Invoke(new GainEditDone(gain.GainDb, gain.FadeIn, gain.FadeOut));
            DragEnded?.Invoke();
            return;
        }

        if (_drag is not { } drag)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        _drag = null;
        var over = _dragOver;
        EndTrackDrag();
        ReleaseMouseCapture();

        var changed = Math.Abs(drag.Block.Left - drag.Left) > 0.5 || Math.Abs(drag.Block.Width - drag.Width) > 0.5;

        // Let go on another track's row: it goes there (or, refused, is drawn back where it was).
        if (over?.DataContext is { } row && drag.Block.MoveTo is { } moveTo)
            moveTo(row, drag.Block.Left);

        // A trim of the start, a roll, a slip or a ripple: the block says what that does.
        else if (drag.Kind is BlockEdit.TrimStart or BlockEdit.Roll or BlockEdit.Slip || drag.Ripple)
        {
            if ((changed || Math.Abs(drag.Slip) > 0.5) && drag.Block.Edit is { } edit)
                edit(new BlockEditDone(drag.Kind, drag.Block.Left, drag.Block.Width, drag.Slip, drag.Ripple, drag.Neighbor));
        }

        // A click that did not move the block changes nothing.
        else if (changed)
            drag.Block.Commit?.Invoke(drag.Block.Left, drag.Block.Width);
        InvalidateVisual();
        DragEnded?.Invoke();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _autoScroll.Stop();
        if (_fadeDrag is { } fadeDrag)
        {
            // Taken away mid-drag: the fades go back to what they were.
            fadeDrag.Block.Fades = fadeDrag.Start;
            if (fadeDrag.Block.Gain is { } gain)
                fadeDrag.Block.Gain = gain with { FadeIn = fadeDrag.Start.In, FadeOut = fadeDrag.Start.Out };
            _fadeDrag = null;
            InvalidateVisual();
            DragEnded?.Invoke();
            return;
        }

        if (_gainDrag is { } gainDrag)
        {
            gainDrag.Block.Gain = gainDrag.Start;
            _gainDrag = null;
            InvalidateVisual();
            DragEnded?.Invoke();
            return;
        }

        if (_drag is not { } drag)
            return;

        // Taken away mid-drag (another window came forward): the block goes back to where it was.
        (drag.Block.Left, drag.Block.Width) = (drag.Left, drag.Width);
        if (drag.Neighbor is { } next)
            (next.Left, next.Width) = (drag.NeighborLeft, drag.NeighborWidth);
        _drag = null;
        EndTrackDrag();
        InvalidateVisual();
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

    // The labels by their text: a zoom or a pan draws mostly the same ones again, and they are not made afresh.
    private readonly Dictionary<string, FormattedText> _labels = [];
    private double _labelsDpi;

    private FormattedText Label(string text, double dpi)
    {
        if (dpi != _labelsDpi || _labels.Count > 256)
        {
            _labelsDpi = dpi;
            _labels.Clear();
        }

        if (!_labels.TryGetValue(text, out var label))
            _labels[text] = label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, LabelBrush, dpi);
        return label;
    }

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
                var label = Label(Format(Math.Max(time, 0), major), dpi);
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
