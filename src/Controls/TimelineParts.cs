using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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

    // ----- UI Automation -----
    // A bar is one element with a name and its place on screen; what it draws has no peers (and the playhead is a visual,
    // not an element), so its peer never walks its children.

    /// <summary>What UI Automation calls the bar when no AutomationProperties.Name is set on it.</summary>
    protected abstract string AutomationName { get; }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineBarPeer(this);

    private sealed class TimelineBarPeer(TimelineBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => owner.GetType().Name;

        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.AutomationName;

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>
/// The stretch of a sound that a block shows: the whole sound laid out along the timeline would be
/// <paramref name="Span"/> pixels wide, and the block begins <paramref name="Offset"/> pixels into it. A span
/// of nothing means the whole sound, fitted to the block.
/// </summary>
public readonly record struct WaveSpan(HandPegApp.Services.WaveformData Data, double Offset, double Span);

/// <summary>
/// The stretch of a sound a block of the unified timeline shows (4.4), by time rather than by fraction: the sound's own
/// seconds from <paramref name="MediaFrom"/> to <paramref name="MediaTo"/>, laid from the left edge of the block's sound
/// (its left edge, less a J-cut's lead-in, see TimelineBlock.AudioSplit) at <paramref name="Speed"/>, drawn
/// <paramref name="GainScale"/> times as tall (the clip's or the track's gain as a scale, 1 for 0 dB). A second
/// <c>s</c> of the sound is at <c>soundLeft + (s - MediaFrom) / Speed * pixelsPerSecond</c>, so trims, cuts and folds
/// can never put a sound where it is not.
/// </summary>
public readonly record struct WaveWindow(HandPegApp.Services.WaveformData Data, double MediaFrom, double MediaTo, double Speed = 1, double GainScale = 1);

/// <summary>
/// The pictures of a block's video on the unified timeline (4.4): frames taken evenly along the whole file (frame k at
/// <c>k * SecondsPerFrame</c> of it), the block showing its file from <paramref name="MediaFrom"/> on at
/// <paramref name="Speed"/>. The control lays them on the file's own time grid, so they stay still while the block's start
/// is trimmed and slide while it is slipped.
/// </summary>
public readonly record struct FilmWindow(IReadOnlyList<ImageSource> Frames, double SecondsPerFrame, double MediaFrom, double Speed);

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
/// it ripples (Ctrl: the later clips move by as much as its end did), and for a roll the block after it, as it is now.</summary>
public readonly record struct BlockEditDone(BlockEdit Kind, double Left, double Width, double Slip, bool Ripple, TimelineBlock? Neighbor);

/// <summary>A gain line dragged on a block: its new gain (dB), or a fade handle's new length (seconds) at its start or end.</summary>
public readonly record struct GainEditDone(double GainDb, double FadeIn, double FadeOut);

/// <summary>Which edges of its parent a child block shares (4.4 BugHunt): a trim of such an edge takes the child's along.</summary>
[Flags]
public enum BlockEdges { None = 0, Start = 1, End = 2 }

/// <summary>
/// Where a block stands in a family of clips on the unified timeline (4.4 BugHunt), for a drag to draw what goes with
/// what: <paramref name="Key"/> is what the block is known by (its clip's id, or a key made from it: the same across a
/// rebuild of the rows, never shared by two blocks); <paramref name="Parent"/> the Key of the block it belongs to (its
/// parent clip's id), null for none; <paramref name="Rides"/> whether a move of the parent carries it (it overlaps the
/// parent, on a track that is not locked: what the model moves with it); <paramref name="OnParentEdges"/> which of the
/// parent's edges it shares (a trim of that edge trims it too). Worked out in seconds by the host, never from pixels.
/// </summary>
public readonly record struct BlockFamily(object Key, object? Parent, bool Rides, BlockEdges OnParentEdges);

/// <summary>One block of a Timeline row: a clip, a cut segment, a part of an audio track, a subtitle.</summary>
/// <summary>Which of a block's own switches was pressed (5.2).</summary>
public enum BlockSwitch { OnOff, Lock }

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

    /// <summary>
    /// The beats found in its sound (5.2B, Detect &amp; Snap to Audio Beats), in pixels from where its file's stretch is laid (its left edge, as
    /// <see cref="Keys"/> are), in order: faint ticks along its foot (the beat grid); null for none.
    /// </summary>
    public IReadOnlyList<double>? Beats { get; init; }

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

    /// <summary>A crossfade from the block before it on its bar, in seconds: drawn as a box across their cut, centred on it.</summary>
    public double CrossfadeIn { get; set; }

    /// <summary>What letting go of a crossfade's dragged edge does (its new length, seconds), and its remove button (0). Null for one that cannot be changed here.</summary>
    public Action<double>? CrossfadeEdit { get; init; }

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

    /// <summary>
    /// Whether the block may be let go at a seam onto a new track (5.4A, the unified timeline's "+ New Video Track" bar), given the data
    /// of the top-level picture row the new track goes beside, whether it goes above that row (false: below), and the block's left edge in
    /// pixels; null for a block that never goes onto a new track.
    /// </summary>
    public Func<object, bool, double, bool>? CanMoveToNewTrack { get; init; }

    /// <summary>What letting go of the block at a seam does (see <see cref="CanMoveToNewTrack"/>): the row's data, above or below it, the left edge.</summary>
    public Action<object, bool, double>? MoveToNewTrack { get; init; }

    /// <summary>
    /// Whether the block, its left edge at these pixels, may be let go as a child of the given block's clip (5.5A, the clip tether: a move
    /// dropped on the bottom edge of a clip it overlaps on another lane, or anywhere on it with Alt pressed during the drag): the parent's
    /// name for the tether's pill (from the host: a picture block's own Name is empty), or null when it may not. Null for a block that never
    /// becomes another's child by a drag.
    /// </summary>
    public Func<TimelineBlock, double, string?>? CanAttachTo { get; init; }

    /// <summary>What letting go of the block on a tether does (see <see cref="CanAttachTo"/>): the parent's block and the block's left edge in pixels.</summary>
    public Action<TimelineBlock, double>? AttachTo { get; init; }

    /// <summary>The block's sound by time, for the unified timeline (4.4): drawn in place of <see cref="Peaks"/> when given.</summary>
    public WaveWindow? SoundWindow { get; init; }

    /// <summary>
    /// A video block's sound stack heard as one (5.3-Stage2, the user's call): the audible lanes of its audio stack combined
    /// (WaveformData.Combine, each at its gain), laid on the block's own file time from its left edge, drawn across the whole
    /// block, see-through over its pictures. Set by the host once the stacks are made; null for none (nothing audible, or no stack).
    /// </summary>
    public WaveWindow? StackSound { get; set; }

    /// <summary>The block's pictures on its file's time grid, for the unified timeline (4.4): drawn in place of <see cref="Frames"/> when given.</summary>
    public FilmWindow? Film { get; init; }

    /// <summary>A few words or a sign shown in a small tag at the block's top right (its speed, "FX"); null for none.</summary>
    public string? Badge { get; init; }

    /// <summary>
    /// Its own On/Off, drawn at its top right (5.2: one click, its clip's alone, a parent's leaving its children as they are):
    /// true on, false off, null for none. Pressed: <see cref="Switched"/>.
    /// </summary>
    public bool? IsOn { get; init; }

    /// <summary>Whether its On/Off is a sound's (a speaker) rather than a picture's (an eye).</summary>
    public bool SwitchIsSound { get; init; }

    /// <summary>Its Lock to its parent, beside its On/Off (5.2: a Cutout's, a child clip's, an effect's): true locked, false free, null for none.</summary>
    public bool? IsLocked { get; init; }

    /// <summary>One of its own switches pressed.</summary>
    public Action<BlockSwitch>? Switched { get; init; }

    /// <summary>
    /// A J/L cut of a clip with linked sound, in seconds: how much sooner than its picture its sound starts (a J cut;
    /// negative, later) and how much longer it lasts (an L cut; negative, shorter). The sound is drawn in the lower part
    /// of the block, reaching out past its edges that far (the one sound not drawn across the whole block). Null for none (or a
    /// clip whose sound is not its picture's).
    /// </summary>
    public (double LeadIn, double TailOut)? AudioSplit { get; init; }

    /// <summary>
    /// What letting go of a J/L trim does (Alt on the lower half of an edge of the block): the new lead-in and tail-out,
    /// in seconds. Null for a block whose sound cannot be split from its picture.
    /// </summary>
    public Action<double, double>? AudioSplitEdit { get; init; }

    /// <summary>Where the block stands among its clip's family (4.4 BugHunt): what a drag of it carries along, and what it is known by. Null for a block that is in none.</summary>
    public BlockFamily? Family { get; init; }

    /// <summary>
    /// Whether an edge of the block lies at a seam of folded dead air (4.4 BugHunt, fold-aware trims): given the edge
    /// (true: its start), how far it may be dragged outward (seconds of the fold shown again) and inward (seconds of the
    /// clip folded in with it); null when that edge is at no fold. Asked as the edge is pressed.
    /// </summary>
    public Func<bool, (double Reveal, double Fold)?>? SeamRange { get; init; }

    /// <summary>What letting go of an edge dragged at a seam does: which edge (true: its start), and how far outward it went (seconds; negative: inward).</summary>
    public Action<bool, double>? SeamTrim { get; init; }

    /// <summary>Drawn small, as a lane of a collapsed mini-stack: the unified timeline's business (set as it lays out), not the host's.</summary>
    public bool IsMini { get; internal set; }

    internal FormattedText? Label;

    // The unified timeline's drawing of the block, made once and used again while it keeps its place (4.4): its badge's
    // words, its keys' diamonds and its fades' shapes (with where they were made for), and the J/L sound band's clip.
    internal FormattedText? BadgeText;
    internal Rect KeyShapeRect;
    internal Geometry? KeyShape;
    internal (Rect Rect, double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) FadeKey;
    internal Geometry? FadeShade, FadeLine;
    internal Rect SoundClipRect;
    internal Geometry? SoundClip;

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
/// The measures the Timeline's bars share (5.2-Audit: what was left of the old per-track bar control, which the unified
/// Timeline replaced): how near an edge or a snap has to be, the gain line's reach, and the shade past the sequence's end.
/// </summary>
public static class TimelineMetrics
{
    /// <summary>How close to a block's right edge the pointer has to be to take hold of its length.</summary>
    public const double EdgePixels = 7;

    /// <summary>The gain line's reach: from silence at the bottom of a block to +12 dB at its top, 0 dB a third of the way down.</summary>
    public const double LowestGain = -40, HighestGain = 12;

    /// <summary>How close, in pixels, a dragged edge has to come to something to snap to it.</summary>
    public const double SnapPixels = 8;

    /// <summary>The room after the end of the sequence: darker, and where a clip dropped or dragged makes it longer.</summary>
    internal static readonly Brush PastEndShade = FrozenBrush(Color.FromArgb(90, 0, 0, 0));
    internal static readonly Pen EndLine = Frozen(new Pen(FrozenBrush(Color.FromArgb(150, 255, 255, 255)), 1));

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}

/// <summary>
/// The time ruler over a tab's time bars: ticks and times for the stretch of the sequence the bars show, the end of
/// the sequence (the room after it, where clips can be dropped and dragged, shaded), and the playhead. A press or
/// a drag on it moves the playhead; the wheel zooms the bars about the pointer, and Shift + wheel or a drag with
/// the middle button moves them along, with momentum (5.4C): a flick of the middle button or a fast Shift + wheel
/// coasts on and slows to a stop, through a <see cref="MomentumScroller"/>; any press, a zoom, or the ruler hidden stops it.
/// </summary>
public sealed class TimelineRuler : TimelineBar
{
    protected override string AutomationName => "Timeline ruler";

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

    // Momentum (5.4C): made at its first use; how far the middle-button drag has panned since its press; whether a coast has the frame.
    private MomentumScroller? _momentum;
    private double _panTravel;
    private bool _coastHooked;

    private MomentumScroller Momentum => _momentum ??= new MomentumScroller();

    public TimelineRuler()
    {
        Unloaded += (_, _) => StopMomentum();
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
                StopMomentum();
        };
    }
    private IReadOnlyList<double> _markers = [], _highlights = [];

    /// <summary>The colour markers are drawn in, here and on the Sequence Overview Scrubber.</summary>
    public static readonly Brush MarkerBrush = FrozenBrush(Color.FromRgb(0x7C, 0xD9, 0x92));

    /// <summary>The colour Ping-to-Mark highlights are drawn in (5.2C: TimelineMarker.Highlight).</summary>
    public static readonly Brush HighlightBrush = FrozenBrush(Color.FromRgb(0xFF, 0xB3, 0x47));

    /// <summary>A marker's flag: pointing down at its moment, 9 pixels wide.</summary>
    public static readonly Geometry MarkerFlag = Frozen(Geometry.Parse("M -4.5,0 L 4.5,0 L 4.5,6 L 0,10 L -4.5,6 Z"));

    /// <summary>A marker was right-clicked: which one (its index in the list shown), for its menu.</summary>
    public event Action<int>? MarkerMenuRequested;

    /// <summary>The ruler was pressed or dragged on: the playhead goes to this moment, in seconds.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>The press or drag that seeks was let go, or its capture taken away (5.4.2B: the host sets the playhead's value it held back).</summary>
    public event Action? SeekEnded;

    /// <summary>The wheel was turned over it: zoom by a factor about a point (pixels from its left).</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>Shift + wheel, or a middle-button drag: move along by so many pixels.</summary>
    public event Action<double>? PanRequested;

    /// <summary>
    /// The moment under the pointer (seconds, never before 0) as it moves over the ruler, a seek's drag too; null when it
    /// leaves (5.0, Instant Seek's hints). Raised only when the moment changes, and allocates nothing.
    /// </summary>
    public event Action<double?>? PointerSecondsChanged;

    // The moment last told (PointerSecondsChanged), NaN for none.
    private double _pointerSeconds = double.NaN;

    private void NotePointer(double seconds)
    {
        if (seconds.Equals(_pointerSeconds))
            return;

        var was = _pointerSeconds;
        _pointerSeconds = seconds;
        if (!double.IsNaN(seconds))
            PointerSecondsChanged?.Invoke(seconds);
        else if (!double.IsNaN(was))
            PointerSecondsChanged?.Invoke(null);
    }

    /// <summary>
    /// Shows a stretch of the sequence: from <paramref name="start"/> seconds, <paramref name="span"/> seconds across,
    /// of a sequence <paramref name="sequence"/> seconds long, with its markers (in seconds), those of them that are
    /// Ping-to-Mark highlights (5.2C) drawn in their own colour.
    /// </summary>
    public void Show(double start, double span, double sequence, IReadOnlyList<double> markers, IReadOnlyList<double>? highlights = null)
    {
        highlights ??= [];
        if (start == _start && span == _span && sequence == _sequence && markers.SequenceEqual(_markers) && highlights.SequenceEqual(_highlights))
            return;

        // A zoom or a fit: a coast along the old view stops.
        if (span != _span)
            StopMomentum();

        (_start, _span, _sequence, _markers, _highlights) = (start, span, sequence, [.. markers], [.. highlights]);
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
            drawing.DrawRectangle(TimelineMetrics.PastEndShade, null, new Rect(Math.Max(end, 0), 0, ActualWidth - Math.Max(end, 0), ActualHeight));
            if (end >= 0)
                drawing.DrawLine(TimelineMetrics.EndLine, new Point(end + 0.5, 0), new Point(end + 0.5, ActualHeight));
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
            drawing.DrawGeometry(_highlights.Contains(marker) ? HighlightBrush : MarkerBrush, null, MarkerFlag);
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
        SeekEnded?.Invoke();
        e.Handled = true;
    }

    // Any press on the ruler (before anything else of it): a coast stops where it is.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        StopMomentum();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && CaptureMouse())
        {
            MiddlePanPress(e.GetPosition(this).X, MomentumScroller.Now());
            Cursor = Cursors.ScrollWE;
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panFrom is not null)
        {
            // Let go of the capture first (losing it mid-drag stops a coast), then the flick, if it was one, coasts on.
            var now = MomentumScroller.Now();
            _panFrom = null;
            Cursor = null;
            ReleaseMouseCapture();
            MiddlePanRelease(now);
            e.Handled = true;
        }
    }

    /// <summary>The middle button pressed at <paramref name="x"/>: any coast stops and the pan begins (5.4C test seam).</summary>
    internal void MiddlePanPress(double x, double nowMs)
    {
        _panFrom = x;
        _panTravel = 0;
        Momentum.Press(nowMs);
        Momentum.Sample(0, nowMs);
    }

    /// <summary>The middle-button drag at <paramref name="x"/>: the bars move along with the pointer (test seam).</summary>
    internal void MiddlePanMove(double x, double nowMs)
    {
        if (_panFrom is not { } from)
            return;

        _panFrom = x;
        _panTravel += from - x;
        Momentum.Sample(_panTravel, nowMs);
        PanRequested?.Invoke(from - x);
    }

    /// <summary>The middle button let go: a flick (250 DIP/s or more, still moving within the last 40 ms) coasts on (test seam).</summary>
    internal void MiddlePanRelease(double nowMs)
    {
        _panFrom = null;
        if (Momentum.Release(nowMs) != 0)
            HookCoast();
    }

    /// <summary>
    /// Shift + the wheel by <paramref name="pixels"/> (positive: later) at <paramref name="nowMs"/>, with momentum: a fast stream coasts on,
    /// a tick against the coast stops it (test seam; <see cref="OnMouseWheel"/> calls it).
    /// </summary>
    internal void PanWheelAt(double pixels, double nowMs)
    {
        var (before, step) = (_start, Momentum.Wheel(pixels, nowMs));
        if (step != 0)
            PanRequested?.Invoke(step);
        if (step != 0 && _start == before)
            Momentum.Stop();
        if (Momentum.IsCoasting)
            HookCoast();
    }

    /// <summary>One frame of the coast at <paramref name="nowMs"/>: the bars move along by what it covered (the host shows the new view at
    /// once); a view that did not move (an end, the view fitted) stops it. Whether the view moved (test seam; the frame hook calls it).</summary>
    internal bool StepCoast(double nowMs)
    {
        var moved = false;
        if (_momentum is { IsCoasting: true } momentum)
        {
            var pixels = momentum.Tick(nowMs);
            if (pixels != 0)
            {
                var before = _start;
                PanRequested?.Invoke(pixels);
                if (_start == before)
                    momentum.Stop();
                else
                    moved = true;
            }
        }

        if (_momentum?.IsCoasting != true)
            UnhookCoast();
        return moved;
    }

    /// <summary>Whether the bars are coasting along from this ruler (5.4C).</summary>
    public bool IsCoasting => _momentum?.IsCoasting == true;

    /// <summary>Stops any coast from this ruler where it is (a press elsewhere on the timeline, the playhead turning the page).</summary>
    public void StopMomentum()
    {
        _momentum?.Stop();
        UnhookCoast();
    }

    // The frame is hooked only while a coast runs and the ruler is shown; a test steps the coast itself.
    private void HookCoast()
    {
        if (_coastHooked || !IsVisible)
            return;

        _coastHooked = true;
        CompositionTarget.Rendering += OnCoastFrame;
    }

    private void UnhookCoast()
    {
        if (!_coastHooked)
            return;

        _coastHooked = false;
        CompositionTarget.Rendering -= OnCoastFrame;
    }

    private void OnCoastFrame(object? sender, EventArgs e) => StepCoast(MomentumScroller.Now());

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this).X;
        NotePointer(ActualWidth > 0 && _span > 0 ? Math.Max(TimeAt(Math.Clamp(at, 0, ActualWidth)), 0) : double.NaN);
        if (_panFrom is not null)
        {
            MiddlePanMove(at, MomentumScroller.Now());
        }
        else if (_seeking)
        {
            SeekRequested?.Invoke(TimeAt(Math.Clamp(at, 0, ActualWidth)));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // A middle-button drag taken away mid-way is no flick: nothing coasts from it.
        if (_panFrom is not null)
            StopMomentum();
        var wasSeeking = _seeking;
        (_seeking, _panFrom) = (false, null);
        if (wasSeeking)
            SeekEnded?.Invoke();

        // A seek's drag let go outside the ruler: nothing of it is under the pointer.
        if (!IsMouseOver)
            NotePointer(double.NaN);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured)
            NotePointer(double.NaN);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            PanWheelAt(-e.Delta, MomentumScroller.Now());
        }
        else
        {
            // 5.4.1-M1: the zoom glides there, the pointer's moment pinned.
            StopMomentum();
            _wheelGlide ??= new(_ => { }, (factor, at) => ZoomRequested?.Invoke(factor, at), () => IsVisible);
            _wheelGlide.Zoom(Math.Pow(1.25, e.Delta / 120.0), e.GetPosition(this).X);
        }

        e.Handled = true;
    }

    private WheelGlide? _wheelGlide;

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
