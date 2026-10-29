using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>What a row of the unified timeline is: a video layer (or the main video's track), a sound, the captions, the blurred background, or an empty row to drop on.</summary>
public enum TimelineRowKind { Video, Audio, Captions, Background, Empty }

/// <summary>The switches a row's header can show (and which of them are on).</summary>
[Flags]
public enum RowSwitches { None = 0, Hidden = 1, Muted = 2, Locked = 4, Soloed = 8, Voice = 16 }

/// <summary>
/// A folded dead-air seam on a row: where it is (seconds), how long the fold is, the fold itself (FoldedSegment), and the
/// rows that open or close up with it, by their keys (null: every row that is not locked): what slides as it is put back,
/// or as a clip's edge is dragged into it.
/// </summary>
public readonly record struct SeamMark(double Seconds, double FoldedSeconds, object Fold, IReadOnlyCollection<object>? Rows = null);

/// <summary>
/// A stretch of folded dead air that was put back (4.4 BugHunt): where it lies on its row (seconds), what it is (a
/// RestoredFold), and the rows that close up when it is folded again (null: every row that is not locked). It stays
/// shaded, with a pill that folds it out again.
/// </summary>
public readonly record struct RevealMark(double From, double To, object Restored, IReadOnlyCollection<object>? Rows = null);

/// <summary>
/// Which of its parent's mini-stacks a child row is in (4.4 BugHunt): the layers that go with the parent (HUD cut-outs and
/// masks, the captions) or its sound (an unlinked sound's lane, the main file's audio streams). Each opens and closes by itself.
/// </summary>
public enum ChildStack { Visual, Audio }

/// <summary>
/// One row of the unified timeline: a track, with its blocks laid out in pixels (seconds times the control's
/// PixelsPerSecond), and the rows that belong to it (its mini-stack: HUD cut-outs, the main file's sound streams, the
/// captions), drawn under it as thin lanes until it is expanded.
/// </summary>
public sealed class TimelineRow
{
    /// <summary>What the row is known by across a rebuild or an undo (Layer.TrackId, an audio track's index, "captions"): its expansion is kept by it.</summary>
    public required object Key { get; init; }

    /// <summary>What menus and edits act on: a Layer row, an AudioTrack, the caption layer.</summary>
    public required object Data { get; init; }

    public string Name { get; set; } = "";

    public TimelineRowKind Kind { get; init; }

    /// <summary>Its full height in DIPs (a collapsed child lane is a fifth of it, at least 8).</summary>
    public double Height { get; set; } = 56;

    /// <summary>Its blocks: Left and Width in laid-out pixels (seconds times PixelsPerSecond); Top and Height within the row, or both 0 to fill it.</summary>
    public List<TimelineBlock> Blocks { get; } = [];

    /// <summary>Its mini-stack's lanes, top to bottom.</summary>
    public List<TimelineRow> Children { get; } = [];

    /// <summary>The row it is a lane of; set by the control when the host leaves it unset.</summary>
    public TimelineRow? Parent { get; set; }

    /// <summary>Which of its parent's mini-stacks it is in: the layers' (the default) or the sound's. The host keeps each stack's lanes together.</summary>
    public ChildStack Stack { get; set; }

    /// <summary>Which switch glyphs its header shows.</summary>
    public RowSwitches Offered { get; set; }

    /// <summary>Which of them are on.</summary>
    public RowSwitches State { get; set; }

    /// <summary>The stripe down the left of its header (frozen); a child lane without one takes its parent's.</summary>
    public Brush? Accent { get; set; }

    /// <summary>Where dead air was folded out of it: a seam each, a click on it puts the fold back.</summary>
    public IReadOnlyList<SeamMark> Seams { get; set; } = [];

    /// <summary>Where folded dead air was put back on it: each stretch shaded, its pill folding it out again.</summary>
    public IReadOnlyList<RevealMark> Reveals { get; set; } = [];

    /// <summary>What its header's tooltip says, over its name.</summary>
    public string? Tip { get; set; }

    /// <summary>Whether a click closes the gaps between its blocks (the pill shows in the gap the pointer is in): the host's say, for the rows of unlocked tracks.</summary>
    public bool ClosesGaps { get; set; }

    // The control's own: the name laid out as words (and the name, size and colour it was laid out from), and the gaps
    // between its blocks (laid-out pixels) that a pill can close.
    internal FormattedText? NameText;
    internal string? NameTextOf;
    internal double NameTextSize;
    internal Brush? NameTextBrush;
    internal readonly List<(double Left, double Right)> Gaps = [];
}

/// <summary>A menu asked for on the unified timeline: the row and the block it was opened on (or none), whether on the row's header, the fade handle pressed (true: the fade in), and the menu for the host to fill.</summary>
public sealed record TimelineMenuRequest(TimelineRow? Row, TimelineBlock? Block, bool OnHeader, bool? FadeIn, ContextMenu Menu);

/// <summary>
/// The unified timeline (4.4): every track of the sequence on one drawn surface, its headers on the left and its time
/// area on the right. Nothing on it is an element of its own: the rows, their blocks (frames, waveforms, fades, gain
/// lines, keys, crossfade pills), the mini-stacks of each row's children, the seams of folded dead air and the pills that
/// close gaps are drawn, and found under the pointer by arithmetic. What moves often has a visual of its own so the rest is
/// not drawn again for it: the playhead and the skim line slide; a pan or a scroll slides what was drawn (a margin either
/// side is drawn too, so most pans draw nothing); the block being dragged is drawn on an overlay of its own.
/// The host gives it rows (<see cref="SetRows"/>) and the view (<see cref="SetView"/>) and acts on what it raises; it edits
/// nothing itself.
/// </summary>
public sealed partial class UnifiedTimelineControl : FrameworkElement
{
    /// <summary>A collapsed child lane is this much of its height (at least 8 px).</summary>
    public const double MiniStackFraction = 0.2;

    // A collapsed lane is never thinner than this.
    private const double MiniLeast = 8;

    // Between lanes: the panel shows through, as it did between the old bars.
    private const double LaneGap = 1;

    // How fast a mini-stack opens or closes (4.4 BugHunt): each frame it goes this much of the way that is left, by the
    // frame's time over this time constant (seconds; most of the way in three of them, the fastest at the start), and is
    // where it was going once it is this close. A click while it moves turns it round from where it is.
    private const double ExpandTau = 0.028;
    private const double ExpandSettle = 0.002;

    // How long a fold put back, or taken out again, slides what follows it into its place (4.4 BugHunt).
    private const double FoldSlideSeconds = 0.12;

    // Below this a lane is drawn plainly (its blocks' colours and sound only) and is not edited on.
    private const double FullDetailHeight = 22;

    // How far in each level of a mini-stack is set, in its header.
    private const double DeckIndent = 14;

    // One lane: its row, how deep in a mini-stack, where it is (DIPs from the top of all the rows) and how tall, whether
    // it is drawn small (a lane of a collapsed stack), whether it is the first and the last of its stack of its parent's,
    // its parent's lane, whether its deck hangs from its parent's second chevron (the sound's, beside the layers'), and
    // which stacks of its own it has (its chevrons: Stacks bits, 1 the layers', 2 the sound's).
    private struct Lane
    {
        public TimelineRow Row;
        public int Depth;
        public double Top;
        public double Height;
        public bool Mini;
        public bool FirstChild;
        public bool LastChild;
        public int Parent;
        public bool SecondDeck;
        public int Stacks;

        public readonly double Bottom => Top + Height;

        public readonly bool Detailed => !Mini && Height >= FullDetailHeight;

        public readonly bool Has(ChildStack stack) => (Stacks & StackBit(stack)) != 0;

        public readonly int ChevronCount => System.Numerics.BitOperations.PopCount((uint)Stacks);
    }

    private static int StackBit(ChildStack stack) => stack == ChildStack.Audio ? 2 : 1;

    // A row's stacks in the order their chevrons are laid in.
    private static readonly ChildStack[] StackOrder = [ChildStack.Visual, ChildStack.Audio];

    private readonly List<TimelineRow> _rows = [];
    private readonly List<Lane> _lanes = [];

    // How open each of a row's stacks is (by its row's Key and the stack), and how open it is going to be: 0 closed, 1
    // open (4.4 BugHunt). A closed one at rest is not kept. Kept for rows that go: rows are made again after every edit and
    // undo, with the same keys.
    private struct Expansion
    {
        public double Current;
        public double Target;
    }

    private readonly Dictionary<(object Key, ChildStack Stack), Expansion> _expansion = [];
    private readonly List<(object Key, ChildStack Stack)> _expansionKeys = [];

    // The one clock for what moves by itself (the stacks opening and closing, a fold sliding): hooked to the frames while
    // anything moves, unhooked once everything is still.
    private bool _ticking;
    private long _tickClock;
    private TimeSpan _lastRenderingTime;

    // A fold put back (Opening) or taken out, sliding what follows it into place: where it is and how long (seconds), the
    // rows that slide (their keys; null: every row that is not locked), when it began and how far along it is (0 to 1, eased).
    private sealed class FoldSlide
    {
        public double At { get; init; }
        public double Length { get; init; }
        public bool Opening { get; init; }
        public HashSet<object>? Rows { get; init; }
        public long Began { get; init; }
        public double Eased { get; set; }
    }

    private FoldSlide? _slide;

    private double _headerWidth = 168;
    private double _viewStart, _viewSpan = 10, _sequenceSeconds = double.NaN;
    private double _playheadSeconds = double.NaN;
    private double _verticalOffset, _extent;
    private double _laidWidth = double.NaN;

    // The visuals: the time area (clipped to it) holds what is drawn in laid-out pixels and slid into view (_scroller: the
    // rows' content and the drag overlay), the blocks' words (kept in view as their blocks are panned), the skim line and
    // the playhead; the headers are beside it.
    private readonly ContainerVisual _timeArea = new();
    private readonly ContainerVisual _scroller = new();
    private readonly DrawingVisual _content = new();
    private readonly DrawingVisual _overlay = new();
    private readonly DrawingVisual _labels = new();
    private readonly DrawingVisual _skim = new();
    private readonly DrawingVisual _playhead = new();
    private readonly DrawingVisual _headers = new();
    private readonly TranslateTransform _shift = new();
    private readonly TranslateTransform _labelsRise = new();
    private readonly TranslateTransform _headersRise = new();

    // What needs drawing again at the next render (they are drawn together, once a frame, however often they are asked for).
    private bool _contentDirty = true, _labelsDirty = true, _headersDirty = true, _overlayDirty = true;

    public UnifiedTimelineControl()
    {
        ClipToBounds = true;
        Focusable = false;
        SnapsToDevicePixels = true;

        // The frames in a block are thumbnails, drawn smaller than they are and many at a time: the cheapest scaling
        // there is, not the smooth one, which costs the most exactly while a timeline is scrolled.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

        _scroller.Transform = _shift;
        _scroller.Children.Add(_content);
        _scroller.Children.Add(_overlay);
        _labels.Transform = _labelsRise;
        _headers.Transform = _headersRise;
        _timeArea.Children.Add(_scroller);
        _timeArea.Children.Add(_labels);
        _timeArea.Children.Add(_skim);
        _timeArea.Children.Add(_playhead);
        AddVisualChild(_timeArea);
        AddVisualChild(_headers);

        _autoScroll.Tick += AutoScroll_Tick;
        Loaded += (_, _) =>
        {
            Services.ThemeManager.Applied += ThemeApplied;
            ReadLook();
            Invalidate();
        };
        Unloaded += (_, _) =>
        {
            Services.ThemeManager.Applied -= ThemeApplied;
            SettleMotion();
            _autoScroll.Stop();
            DisarmMenu();
        };
        ReadLook();
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _timeArea : _headers;

    /// <summary>How wide the rows' headers are (168 by default); the time area is what is left.</summary>
    public double HeaderWidth
    {
        get => _headerWidth;
        set
        {
            value = Math.Max(value, 0);
            if (value == _headerWidth)
                return;

            _headerWidth = value;
            ClipTimeArea();
            ScaleChanged?.Invoke();
            Invalidate();
        }
    }

    /// <summary>The top-level rows, top to bottom.</summary>
    public IReadOnlyList<TimelineRow> Rows => _rows;

    /// <summary>How many of the time area's pixels a second takes: its width over the view's span. Blocks are laid out in it.</summary>
    public double PixelsPerSecond => TimeAreaWidth > 0 && _viewSpan > 0 ? TimeAreaWidth / _viewSpan : 0;

    /// <summary>How far along the laid-out rows the view begins, in pixels (the view's start times PixelsPerSecond).</summary>
    public double ViewOffsetX => _viewStart * PixelsPerSecond;

    /// <summary>The width of the time area: the control's, less the headers'.</summary>
    public double TimeAreaWidth => Math.Max(ActualWidth - _headerWidth, 0);

    /// <summary>Where the playhead is, in seconds; NaN for none. Setting it slides its own visual: nothing else is drawn again.</summary>
    public double PlayheadSeconds
    {
        get => _playheadSeconds;
        set
        {
            if (value.Equals(_playheadSeconds))
                return;

            _playheadSeconds = value;
            PlaceLine(_playhead, value, ref _playheadHeight, playhead: true);
        }
    }

    /// <summary>How far the rows are scrolled down, in DIPs (kept within 0 and the extent less the control's height).</summary>
    public double VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            value = Math.Clamp(double.IsFinite(value) ? value : 0, 0, Math.Max(_extent - ActualHeight, 0));
            if (value == _verticalOffset)
                return;

            _verticalOffset = value;
            Slide();
            VerticalOffsetChanged?.Invoke();
        }
    }

    /// <summary>How tall all the rows are together, with their lanes as they are now (opening or closing stacks included).</summary>
    public double ExtentHeight => _extent;

    /// <summary>The extent changed (rows, heights, a stack opened or closed): a scroll bar beside the control follows it.</summary>
    public event Action? ExtentChanged;

    /// <summary>The rows were scrolled (the wheel, or the offset kept within a smaller extent).</summary>
    public event Action? VerticalOffsetChanged;

    /// <summary>
    /// PixelsPerSecond changed because the control (or its headers) changed width, not because of <see cref="SetView"/>:
    /// the host lays its blocks out again for the new scale.
    /// </summary>
    public event Action? ScaleChanged;

    /// <summary>The moments a dragged edge, block or fade snaps to, in seconds; asked once, when a drag begins.</summary>
    public Func<IReadOnlyList<double>>? SnapTimes { get; set; }

    /// <summary>The sequence's frame rate: while Shift is held, drags step from frame to frame of it, with no snapping.</summary>
    public Func<double>? FrameRate { get; set; }

    /// <summary>What is selected: the block that stands for it (its Clip) is outlined.</summary>
    public Func<object?, bool>? IsSelected { get; set; }

    /// <summary>
    /// A button went down on the control, before anything is looked for under the pointer (4.4 BugHunt): the host lays out
    /// the rows it still owes the control (an edit just made, still waiting), so what is pressed is the timeline as it is now.
    /// </summary>
    public event Action? PressStarting;

    /// <summary>
    /// What the host did for something the control raised (a drag let go, a click, a menu) failed (4.4 BugHunt): the control
    /// has put back what it had drawn moved, and the host says so and logs it. Never thrown on to the dispatcher.
    /// </summary>
    public event Action<Exception>? CallbackFailed;

    /// <summary>A block was pressed, with either button (on its row).</summary>
    public event Action<TimelineRow, TimelineBlock, MouseButtonEventArgs>? BlockPressed;

    /// <summary>The time area was pressed beside the blocks (on a row, or below them all): the moment, in seconds. A click there seeks.</summary>
    public event Action<TimelineRow?, double, MouseButtonEventArgs>? EmptyPressed;

    /// <summary>A block that cannot be dragged right now was pressed: its reason.</summary>
    public event Action<string>? DragRefused;

    /// <summary>A drag ended, whether or not it changed anything.</summary>
    public event Action? DragEnded;

    /// <summary>Ctrl + wheel: zoom by a factor, about a point (pixels from the time area's left).</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>Shift + wheel, a drag with the middle button, or a drag held near an edge: move along by so many pixels.</summary>
    public event Action<double>? PanRequested;

    /// <summary>A switch on a row's header was clicked: the row, which switch, and whether Alt was held (solo this one alone).</summary>
    public event Action<TimelineRow, RowSwitches, bool>? HeaderSwitchToggled;

    /// <summary>A right-click: the host fills the request's menu, which opens when it has items.</summary>
    public event Action<TimelineMenuRequest>? MenuRequested;

    /// <summary>A seam of folded dead air was clicked: put the fold back.</summary>
    public event Action<TimelineRow, SeamMark>? SeamClicked;

    /// <summary>The Hide pill of a stretch of dead air that was put back was clicked: fold it out again.</summary>
    public event Action<TimelineRow, RevealMark>? RevealHideRequested;

    /// <summary>A gap's pill was clicked: the row, and where the gap starts and ends (seconds).</summary>
    public event Action<TimelineRow, double, double>? CloseGapRequested;

    /// <summary>Alt + click on a block (without dragging it): split it there (seconds).</summary>
    public event Action<TimelineRow, TimelineBlock, double>? RazorRequested;

    /// <summary>The pointer moved over the time area: the moment under it (seconds), the skim line's; null when it leaves.</summary>
    public event Action<double?>? SkimChanged;

    /// <summary>A row's mini-stack was opened or closed (by a click on its chevron or <see cref="SetExpanded(TimelineRow, bool)"/>).</summary>
    public event Action<TimelineRow>? ExpansionChanged;

    /// <summary>
    /// Takes the top-level rows, top to bottom (their children are their mini-stacks). Each row's expansion is kept by its
    /// Key, so rows made again after an edit or an undo open as they were. A drag in progress goes on while its block is
    /// still among them, and is let go (put back, and said why) when it is not: what it was dragging was changed under it.
    /// </summary>
    public void SetRows(IReadOnlyList<TimelineRow> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        if (DraggedBlock is { } dragged && !Contains(_rows, dragged))
        {
            AbortDrag();
            Raise(() => DragRefused?.Invoke("What was being dragged changed meanwhile (an undo or another edit), so it was let go where it was."));
        }

        _hovered = null;
        Relayout();
    }

    /// <summary>
    /// Sets the view: its start and span (seconds) and how long the sequence is (seconds; what lies after it is shaded).
    /// When the span is unchanged (a pan) nothing needs laying out again: what was drawn slides, and is drawn again only
    /// when the view leaves what was drawn. When the span changes the host lays its blocks out again for the new
    /// PixelsPerSecond (and gives them with <see cref="SetRows"/> or <see cref="Redraw"/>).
    /// </summary>
    public void SetView(double viewStart, double viewSpan, double sequenceSeconds)
    {
        var scale = PixelsPerSecond;
        _viewStart = double.IsFinite(viewStart) ? viewStart : 0;
        if (viewSpan > 0 && double.IsFinite(viewSpan))
            _viewSpan = viewSpan;

        var sequenceChanged = !sequenceSeconds.Equals(_sequenceSeconds);
        _sequenceSeconds = sequenceSeconds;
        if (PixelsPerSecond != scale || sequenceChanged)
            Invalidate();
        else
            Slide();
    }

    /// <summary>The blocks changed in place (moved, selected, their sound or pictures arrived): everything is drawn again.</summary>
    public void Redraw()
    {
        Relayout();
    }

    /// <summary>Whether a row's mini-stack is open (or, with a stack of layers and one of sound, either of them): where it is going, while it moves.</summary>
    public bool IsExpanded(TimelineRow row) => IsExpanded(row, ChildStack.Visual) || IsExpanded(row, ChildStack.Audio);

    /// <summary>Whether one of a row's mini-stacks is open: where it is going, while it moves.</summary>
    public bool IsExpanded(TimelineRow row, ChildStack stack) => _expansion.TryGetValue((row.Key, stack), out var expansion) && expansion.Target >= 0.5;

    /// <summary>Opens or closes every mini-stack of a row (see <see cref="SetExpanded(TimelineRow, ChildStack, bool)"/>).</summary>
    public void SetExpanded(TimelineRow row, bool expanded)
    {
        var changed = false;
        foreach (var stack in StackOrder)
        {
            if (HasStack(row, stack))
                changed |= Target(row, stack, expanded);
        }

        if (changed)
            AfterExpansion(row);
    }

    /// <summary>
    /// Opens or closes one of a row's mini-stacks: its lanes grow to their full height (or shrink to a fifth of it), fast at
    /// first and easing in (most of the way in under 90 ms). Asked again while it moves, it turns round from where it is.
    /// </summary>
    public void SetExpanded(TimelineRow row, ChildStack stack, bool expanded)
    {
        if (Target(row, stack, expanded))
            AfterExpansion(row);
    }

    // Sets where a stack is going; false when it was going there already. With nothing to see moving (no lanes in the stack,
    // the control not shown) it is simply there.
    private bool Target(TimelineRow row, ChildStack stack, bool expanded)
    {
        var key = (row.Key, stack);
        var target = expanded ? 1.0 : 0.0;
        var known = _expansion.TryGetValue(key, out var expansion);
        if (expansion.Target == target && (known || !expanded))
            return false;

        expansion.Target = target;
        if (!HasStack(row, stack) || !IsVisible)
            expansion.Current = target;

        if (expansion.Current == 0 && expansion.Target == 0)
            _expansion.Remove(key);
        else
            _expansion[key] = expansion;
        return true;
    }

    private void AfterExpansion(TimelineRow row)
    {
        if (IsMoving())
            StartTicking();
        else
            Relayout();
        Raise(() => ExpansionChanged?.Invoke(row));
    }

    private static bool HasStack(TimelineRow row, ChildStack stack) => row.Children.Exists(child => child.Stack == stack);

    /// <summary>
    /// Slides what follows a fold into place over about 120 ms, after the host put the fold back (<paramref name="opening"/>:
    /// the stretch from <paramref name="atSeconds"/>, <paramref name="lengthSeconds"/> long, wipes in and what follows slides
    /// out to make room) or took it out (what follows slides in over where the stretch was). The rows are already as they
    /// end: only the drawing moves, on the rows given by their keys (null: every row that is not locked). A press ends it at once.
    /// </summary>
    public void AnimateFold(double atSeconds, double lengthSeconds, bool opening, IReadOnlyCollection<object>? rowKeys)
    {
        if (!IsVisible || !(lengthSeconds > 0) || !double.IsFinite(atSeconds) || !double.IsFinite(lengthSeconds))
            return;

        _slide = new FoldSlide
        {
            At = atSeconds, Length = lengthSeconds, Opening = opening,
            Rows = rowKeys is null ? null : new HashSet<object>(rowKeys),
            Began = Stopwatch.GetTimestamp(),
        };
        _contentDirty = _labelsDirty = true;
        InvalidateVisual();
        StartTicking();
    }

    /// <summary>A fold sliding into place is where it was going (a press, a drag: what is drawn is what is there).</summary>
    private void FinishSlide()
    {
        if (_slide is null)
            return;

        _slide = null;
        _contentDirty = _labelsDirty = true;
        InvalidateVisual();
    }

    /// <summary>The moment at a point across the control (its own coordinates), in seconds.</summary>
    public double TimeAt(double x)
    {
        var scale = PixelsPerSecond;
        return scale > 0 ? (x - _headerWidth + ViewOffsetX) / scale : _viewStart;
    }

    /// <summary>
    /// What a file dragged over the control would land on: the row (or open lane) under the point, the open row a collapsed
    /// stack's strip is drawn under, null below them all; the
    /// moment under it (seconds, never before 0); and whether it is over the headers (it then goes after the row's last clip).
    /// The captions' strip in a collapsed stack is the captions' row itself (4.4 BugHunt): a file let go on it is never
    /// taken for one let go on the row above it (on the main video's, that would take the main video's place).
    /// </summary>
    public (TimelineRow? Row, double Seconds, bool LeftEdgeZone) HitTestDrop(Point p)
    {
        var under = LaneAt(p.Y + _verticalOffset);
        var lane = under >= 0 && _lanes[under] is { Mini: true, Row.Kind: TimelineRowKind.Captions } ? under : OpenLaneOf(under);
        return (lane >= 0 ? _lanes[lane].Row : null, Math.Max(TimeAt(p.X), 0), p.X < _headerWidth);
    }

    /// <summary>
    /// Shows where files dragged over the control would land: on a row (or, null, a new row under them all), at a moment,
    /// how many, and whether they would be stacked onto tracks of their own rather than laid one after another.
    /// </summary>
    public void ShowDropMark(TimelineRow? row, double seconds, int count, bool stack)
    {
        var mark = new DropMark(row, seconds, Math.Max(count, 1), stack);
        if (_dropMark == mark)
            return;

        _dropMark = mark;
        _overlayDirty = true;
        InvalidateVisual();
    }

    public void ClearDropMark()
    {
        if (_dropMark is null)
            return;

        _dropMark = null;
        _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>Whether a block, a fade, a gain line or a J/L cut is being dragged (the host leaves the rows alone meanwhile).</summary>
    public bool IsDragging => _drag is not null || _gainDrag is not null || _fadeDrag is not null || _splitDrag is not null;

    // ----- Laying out -----

    /// <summary>Lays the lanes out again (rows, heights, stacks), and draws everything again at the next render.</summary>
    private void Relayout()
    {
        _lanes.Clear();
        var top = 0.0;
        for (var i = 0; i < _rows.Count; i++)
            AddLanes(_rows[i], null, 0, -1, 1, false, false, false, ref top);

        foreach (var lane in _lanes)
            FindGaps(lane);
        _hoveredGap = null;

        var extent = top;
        var extentChanged = extent != _extent;
        _extent = extent;
        Invalidate();
        if (extentChanged)
        {
            ExtentChanged?.Invoke();

            // A shorter extent may leave the rows scrolled past their end.
            VerticalOffset = _verticalOffset;
        }
    }

    // A row's lane and, under it, its children's: a child is as tall as its stack of its parent's is open (a fifth of its
    // height closed, all of it open; the openness is already eased, fastest at first), and a child of a closed one is
    // closed too. Each of the parent's stacks (the layers', the sound's) is laid together, the layers' first.
    private void AddLanes(TimelineRow row, TimelineRow? parent, int depth, int parentLane, double openness, bool firstChild, bool lastChild,
        bool secondDeck, ref double top)
    {
        if (parent is not null)
            row.Parent ??= parent;

        var full = Math.Max(row.Height, MiniLeast);
        var small = Math.Max(full * MiniStackFraction, MiniLeast);
        var height = depth == 0 ? full : small + (full - small) * Math.Clamp(openness, 0, 1);
        var mini = depth > 0 && openness < 0.999;
        var stacks = 0;
        foreach (var child in row.Children)
            stacks |= StackBit(child.Stack);

        var index = _lanes.Count;
        _lanes.Add(new Lane
        {
            Row = row, Depth = depth, Top = top, Height = height, Mini = mini, FirstChild = firstChild, LastChild = lastChild,
            Parent = parentLane, SecondDeck = secondDeck, Stacks = stacks,
        });
        foreach (var block in row.Blocks)
            block.IsMini = mini;
        top += height + LaneGap;

        if (row.Children.Count == 0)
            return;

        var two = stacks == 3;
        for (var i = 0; i < row.Children.Count; i++)
        {
            var child = row.Children[i];
            var first = i == 0 || row.Children[i - 1].Stack != child.Stack;
            var last = i == row.Children.Count - 1 || row.Children[i + 1].Stack != child.Stack;
            var open = Math.Min(openness, Openness(row, child.Stack));
            AddLanes(child, row, depth + 1, index, open, first, last, two && child.Stack == ChildStack.Audio, ref top);
        }
    }

    private double Openness(TimelineRow row, ChildStack stack) => _expansion.TryGetValue((row.Key, stack), out var expansion) ? expansion.Current : 0;

    // The gaps between a lane's blocks that a pill can close: on the rows the host lets close them (unlocked tracks) whose
    // blocks can be dragged, from the end of what comes before to the next block's start (the room before the first block
    // is no gap). Kept in laid-out pixels; only those wide enough for the pill.
    private readonly List<(double Left, double Right)> _spans = [];

    private void FindGaps(Lane lane)
    {
        var row = lane.Row;
        row.Gaps.Clear();
        if (lane.Mini || !row.ClosesGaps || row.Kind is not (TimelineRowKind.Video or TimelineRowKind.Audio) || !row.Blocks.Exists(b => b.CanDrag is not null))
            return;

        _spans.Clear();
        foreach (var block in row.Blocks)
            _spans.Add((block.Left, block.Left + block.Width));
        _spans.Sort(static (a, b) => a.Left.CompareTo(b.Left));

        var reach = _spans[0].Right;
        for (var i = 1; i < _spans.Count; i++)
        {
            var (left, right) = _spans[i];
            if (left - reach >= GapLeast)
                row.Gaps.Add((reach, left));
            reach = Math.Max(reach, right);
        }
    }

    // The lane at a height (DIPs from the top of all the rows); the gap under a lane counts as the lane. -1 for none.
    private int LaneAt(double y)
    {
        if (_lanes.Count == 0 || y < 0)
            return -1;

        var (low, high) = (0, _lanes.Count - 1);
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_lanes[middle].Top <= y)
                low = middle;
            else
                high = middle - 1;
        }

        return y < _lanes[low].Bottom + LaneGap ? low : -1;
    }

    // A lane of a collapsed stack stands for the open row it is drawn under (its nearest ancestor shown in full): what is
    // dropped or dragged onto the thin strip goes to that row, never to the hidden track the strip is. -1 stays -1.
    private int OpenLaneOf(int index)
    {
        while (index >= 0 && _lanes[index].Mini)
            index = _lanes[index].Parent;
        return index;
    }

    private int LaneOf(TimelineRow row)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (ReferenceEquals(_lanes[i].Row, row))
                return i;
        }

        return -1;
    }

    private static bool Contains(List<TimelineRow> rows, TimelineBlock block)
    {
        foreach (var row in rows)
        {
            if (row.Blocks.Contains(block) || Contains(row.Children, block))
                return true;
        }

        return false;
    }

    // ----- What moves by itself: stacks opening and closing, a fold sliding into place -----

    // Whether anything is on its way: a stack not yet where it is going, or a fold still sliding.
    private bool IsMoving()
    {
        if (_slide is not null)
            return true;

        foreach (var expansion in _expansion.Values)
        {
            if (expansion.Current != expansion.Target)
                return true;
        }

        return false;
    }

    private void StartTicking()
    {
        if (_ticking)
            return;

        _ticking = true;
        _tickClock = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRenderingTick;
    }

    private void StopTicking()
    {
        if (!_ticking)
            return;

        _ticking = false;
        CompositionTarget.Rendering -= OnRenderingTick;
    }

    // A frame: each moving stack goes its share of the way that is left (so a click mid-way turns it round from where it
    // is, at full speed), the fold slides on; laid out and drawn again once for all of it. Still once all is still.
    private void OnRenderingTick(object? sender, EventArgs e)
    {
        // Rendering can come more than once for one frame: the frame's own time tells.
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime)
                return;
            _lastRenderingTime = rendering.RenderingTime;
        }

        var now = Stopwatch.GetTimestamp();

        // A stall (a heavy frame, the window dragged) never makes a jump of more than this.
        var seconds = Math.Clamp((now - _tickClock) / (double)Stopwatch.Frequency, 0, 0.05);
        _tickClock = now;

        var laidOut = StepExpansion(seconds);
        if (_slide is { } slide)
        {
            var done = Math.Clamp((now - slide.Began) / (double)Stopwatch.Frequency / FoldSlideSeconds, 0, 1);

            // An ease out: fast at first, settling into place.
            slide.Eased = 1 - Math.Pow(1 - done, 3);
            if (done >= 1)
                _slide = null;
            _contentDirty = _labelsDirty = true;
            InvalidateVisual();
        }

        if (laidOut)
            Relayout();
        if (!IsMoving())
            StopTicking();
    }

    // Moves every stack that is on its way; whether any moved (the lanes are laid out again for it).
    private bool StepExpansion(double seconds)
    {
        if (seconds <= 0)
            return false;

        var share = 1 - Math.Exp(-seconds / ExpandTau);
        _expansionKeys.Clear();
        _expansionKeys.AddRange(_expansion.Keys);
        var moved = false;
        foreach (var key in _expansionKeys)
        {
            var expansion = _expansion[key];
            if (expansion.Current == expansion.Target)
                continue;

            expansion.Current += (expansion.Target - expansion.Current) * share;
            if (Math.Abs(expansion.Target - expansion.Current) < ExpandSettle)
                expansion.Current = expansion.Target;
            if (expansion.Current == 0 && expansion.Target == 0)
                _expansion.Remove(key);
            else
                _expansion[key] = expansion;
            moved = true;
        }

        return moved;
    }

    // Leaving the screen: whatever was moving is where it was going, and the clock stops.
    private void SettleMotion()
    {
        var moving = IsMoving();
        StopTicking();
        _slide = null;
        _expansionKeys.Clear();
        _expansionKeys.AddRange(_expansion.Keys);
        foreach (var key in _expansionKeys)
        {
            var expansion = _expansion[key];
            if (expansion.Target == 0)
            {
                _expansion.Remove(key);
            }
            else
            {
                expansion.Current = expansion.Target;
                _expansion[key] = expansion;
            }
        }

        if (moving)
            Relayout();
    }

    // ----- Sliding and drawing again -----

    /// <summary>Everything is drawn again at the next render.</summary>
    private void Invalidate()
    {
        _contentDirty = _labelsDirty = _headersDirty = _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// The view moved without changing its scale (a pan, a scroll): what was drawn slides; it is drawn again only when
    /// the view has left the part of the rows that was drawn. The words, which keep to the view, are laid again.
    /// </summary>
    private void Slide()
    {
        var view = ViewOffsetX;
        if (view < _renderedFrom || view + TimeAreaWidth > _renderedTo)
            _contentDirty = true;

        ApplyShift();
        _labelsDirty = true;
        if (_drag is not null || _fadeDrag is not null || _dropMark is not null)
            _overlayDirty = true;
        PlaceLine(_playhead, _playheadSeconds, ref _playheadHeight, playhead: true);
        PlaceLine(_skim, _skimSeconds, ref _skimHeight, playhead: false);
        InvalidateVisual();
    }

    private void ApplyShift()
    {
        _shift.X = _headerWidth + _originX - ViewOffsetX;
        _shift.Y = -_verticalOffset;
        _labelsRise.Y = -_verticalOffset;
        _headersRise.Y = -_verticalOffset;
    }

    private void ClipTimeArea()
    {
        var clip = new RectangleGeometry(new Rect(_headerWidth, 0, TimeAreaWidth, Math.Max(ActualHeight, 0)));
        clip.Freeze();
        _timeArea.Clip = clip;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ClipTimeArea();
        _playheadHeight = _skimHeight = -1;
        if (sizeInfo.WidthChanged && ActualWidth != _laidWidth)
        {
            _laidWidth = ActualWidth;
            ScaleChanged?.Invoke();
        }

        VerticalOffset = _verticalOffset;
        Invalidate();
        PlaceLine(_playhead, _playheadSeconds, ref _playheadHeight, playhead: true);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ForgetWords();
        if (oldDpi.PixelsPerDip != newDpi.PixelsPerDip)
            WaveTiles.Clear();
        Invalidate();
    }

    // ----- The theme's look -----

    private Brush _panel = Brushes.DimGray, _card = Brushes.DimGray, _inset = Brushes.Black, _text = Brushes.White, _muted = Brushes.Gray;
    private Brush _accent = Brushes.DodgerBlue, _warning = Brushes.Orange, _gold = Brushes.Gold, _edge = Brushes.Gray;
    private Brush? _lowerEdge;
    private double _edgeWidth;
    private Pen _edgePen = new(), _lowerEdgePen = new(), _deckPen = new();

    private void ThemeApplied()
    {
        ReadLook();
        ForgetWords();
        Invalidate();
    }

    /// <summary>Reads the theme's look tokens (the same ones the old time bars and wells were drawn with), frozen.</summary>
    private void ReadLook()
    {
        _panel = Look("PanelBrush", _panel);
        _card = Look("CardBrush", _card);
        _inset = Look("InsetBrush", _inset);
        _text = Look("TextBrush", _text);
        _muted = Look("MutedTextBrush", _muted);
        _accent = Look("AccentBrush", _accent);
        _warning = Look("WarningTextBrush", _warning);
        _gold = Look("KeyAccentBrush", _gold);
        _edge = Look("WellBorderBrush", _edge);
        _lowerEdge = TryFindResource("WellBorderLowerBrush") is Brush lower ? (Brush)lower.GetCurrentValueAsFrozen() : null;
        _edgeWidth = TryFindResource("WellEdgeThickness") is Thickness edge ? edge.Top : 0;

        _edgePen = FrozenPen(_edge, Math.Max(_edgeWidth, 1));
        _lowerEdgePen = FrozenPen(_lowerEdge ?? _edge, Math.Max(_edgeWidth, 1));
        var deck = _muted is SolidColorBrush solid ? FrozenBrush(Color.FromArgb(150, solid.Color.R, solid.Color.G, solid.Color.B)) : _muted;
        _deckPen = FrozenPen(deck, 1);
    }

    private Brush Look(string key, Brush fallback) => TryFindResource(key) is Brush brush ? (Brush)brush.GetCurrentValueAsFrozen() : fallback;

    private static Pen FrozenPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ----- UI Automation -----
    // The control is one element named "Timeline" with its place on screen; each lane is a child with its row's name and
    // its own place, so a test (or a screen reader) can find a row. What is drawn in a lane has no peers.

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);

    private sealed class TimelinePeer(UnifiedTimelineControl owner) : FrameworkElementAutomationPeer(owner)
    {
        private readonly Dictionary<object, RowPeer> _rowPeers = [];

        protected override string GetClassNameCore() => nameof(UnifiedTimelineControl);

        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : "Timeline";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            var children = new List<AutomationPeer>(owner._lanes.Count);
            foreach (var lane in owner._lanes)
            {
                if (!_rowPeers.TryGetValue(lane.Row.Key, out var peer))
                    _rowPeers[lane.Row.Key] = peer = new RowPeer(owner, lane.Row.Key);
                children.Add(peer);
            }

            return children;
        }
    }

    private sealed class RowPeer(UnifiedTimelineControl owner, object key) : AutomationPeer
    {
        private int Index()
        {
            for (var i = 0; i < owner._lanes.Count; i++)
            {
                if (Equals(owner._lanes[i].Row.Key, key))
                    return i;
            }

            return -1;
        }

        private Rect Bounds()
        {
            var index = Index();
            if (index < 0 || PresentationSource.FromVisual(owner) is null)
                return Rect.Empty;

            var lane = owner._lanes[index];
            var top = Math.Clamp(lane.Top - owner._verticalOffset, 0, owner.ActualHeight);
            var bottom = Math.Clamp(lane.Bottom - owner._verticalOffset, 0, owner.ActualHeight);
            if (bottom <= top)
                return Rect.Empty;
            return new Rect(owner.PointToScreen(new Point(0, top)), owner.PointToScreen(new Point(owner.ActualWidth, bottom)));
        }

        protected override string GetAcceleratorKeyCore() => "";

        protected override string GetAccessKeyCore() => "";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetAutomationIdCore() => "";

        protected override Rect GetBoundingRectangleCore() => Bounds();

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override string GetClassNameCore() => "TimelineRow";

        protected override Point GetClickablePointCore()
        {
            var bounds = Bounds();
            return bounds.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        protected override string GetHelpTextCore() => Index() is >= 0 and var index ? owner._lanes[index].Row.Tip ?? "" : "";

        protected override string GetItemStatusCore() => "";

        protected override string GetItemTypeCore() => "";

        protected override AutomationPeer? GetLabeledByCore() => null;

        protected override string GetNameCore() => Index() is >= 0 and var index ? owner._lanes[index].Row.Name : "";

        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.Horizontal;

        public override object? GetPattern(PatternInterface patternInterface) => null;

        protected override bool HasKeyboardFocusCore() => false;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        protected override bool IsEnabledCore() => owner.IsEnabled;

        protected override bool IsKeyboardFocusableCore() => false;

        protected override bool IsOffscreenCore() => Bounds().IsEmpty;

        protected override bool IsPasswordCore() => false;

        protected override bool IsRequiredForFormCore() => false;

        protected override void SetFocusCore()
        {
        }
    }
}
