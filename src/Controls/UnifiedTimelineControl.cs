using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>Unified timeline row kind: video track, audio, captions, blur fill settings, or empty drop row.</summary>
public enum TimelineRowKind { Video, Audio, Captions, Background, Empty }

/// <summary>Row header switch flags (used for both visibility and on-state).</summary>
[Flags]
public enum RowSwitches { None = 0, Hidden = 1, Muted = 2, Locked = 4, Soloed = 8, Voice = 16 }

/// <summary>
/// A folded dead-air seam: position and fold length (seconds), the FoldedSegment, and the row keys that slide when it is unfolded
/// or an edge is dragged into it (null = all unlocked rows).
/// </summary>
public readonly record struct SeamMark(double Seconds, double FoldedSeconds, object Fold, IReadOnlyCollection<object>? Rows = null);

/// <summary>
/// An unfolded dead-air range (seconds) with its RestoredFold and the row keys that close up on refold (null = all unlocked rows).
/// Drawn shaded with a Hide pill that refolds it.
/// </summary>
public readonly record struct RevealMark(double From, double To, object Restored, IReadOnlyCollection<object>? Rows = null);

/// <summary>
/// Which parent mini-stack a child row belongs to: Visual Sub-Lanes (HUD Cutouts, masks, captions) or Audio Sub-Lanes (unlinked audio,
/// file audio streams). Each stack expands independently.
/// </summary>
public enum ChildStack { Visual, Audio }

/// <summary>
/// One unified timeline row: a track with blocks in pixels (seconds * PixelsPerSecond) and child rows (HUD Cutouts, audio streams,
/// captions) drawn as thin lanes below it until expanded (then <see cref="InFront"/> children go above it). Effect lanes take no
/// space while collapsed (<see cref="HidesWhenClosed"/>).
/// </summary>
public sealed class TimelineRow
{
    /// <summary>Identity stable across rebuilds and undo (Layer.TrackId, audio track index, "captions"); expansion state is keyed by it.</summary>
    public required object Key { get; init; }

    /// <summary>Menu/edit target: a Layer row, an AudioTrack, or the caption layer.</summary>
    public required object Data { get; init; }

    public string Name { get; set; } = "";

    public TimelineRowKind Kind { get; init; }

    /// <summary>Expanded height in DIPs (collapsed child lanes use a fifth, min 8; <see cref="HidesWhenClosed"/> lanes use 0).</summary>
    public double Height { get; set; } = 56;

    /// <summary>
    /// True when dragging its lower edge resizes this row alone (track rows); otherwise that drag, like Shift+drag on any row, sets the
    /// global Track Height.
    /// </summary>
    public bool SizedAlone { get; set; }

    /// <summary>Has a per-row height override that the global Track Height ignores.</summary>
    public bool HasOwnHeight { get; set; }

    /// <summary>Blocks: Left/Width in layout pixels (seconds * PixelsPerSecond); Top/Height within the row, or both 0 to fill it.</summary>
    public List<TimelineBlock> Blocks { get; } = [];

    /// <summary>Mini-stack child rows, top to bottom.</summary>
    public List<TimelineRow> Children { get; } = [];

    /// <summary>Parent row; filled in by the control when the host leaves it null.</summary>
    public TimelineRow? Parent { get; set; }

    /// <summary>Parent mini-stack: visual (default) or audio. The host keeps each stack's children contiguous.</summary>
    public ChildStack Stack { get; set; }

    /// <summary>
    /// Visual child composited above its parent (captions, a Cutout), set by the host from draw order. With the stack open it is laid
    /// above the parent row (topmost first); collapsed, all children sit in the mini-stack below.
    /// </summary>
    public bool InFront { get; set; }

    /// <summary>
    /// Takes zero height while its parent stack is collapsed (effect lanes) instead of a thin bar; grows in with the stack when it opens.
    /// Also zero under a parent that is itself a collapsed thin bar.
    /// </summary>
    public bool HidesWhenClosed { get; set; }

    /// <summary>Switch glyphs shown in the header.</summary>
    public RowSwitches Offered { get; set; }

    /// <summary>Switches currently on.</summary>
    public RowSwitches State { get; set; }

    /// <summary>Frozen header accent stripe; a child without one inherits its parent's.</summary>
    public Brush? Accent { get; set; }

    /// <summary>Folded dead-air seams; clicking one unfolds it.</summary>
    public IReadOnlyList<SeamMark> Seams { get; set; } = [];

    /// <summary>Unfolded dead-air ranges, drawn shaded with a refold pill.</summary>
    public IReadOnlyList<RevealMark> Reveals { get; set; } = [];

    /// <summary>Header tooltip text (shown over the name).</summary>
    public string? Tip { get; set; }

    /// <summary>Host flag (unlocked tracks): show a close-gap pill in the hovered gap between blocks.</summary>
    public bool ClosesGaps { get; set; }

    /// <summary>
    /// Track Preset Node badge: the Game Preset name applied to the track's clips, drawn as a pill that opens the preset list
    /// (<see cref="UnifiedTimelineControl.BadgePressed"/>); null for none.
    /// </summary>
    public string? Badge { get; set; }

    /// <summary>Track accepts a preset: with no <see cref="Badge"/>, tall lanes show a faint "+ Preset" pill.</summary>
    public bool OffersBadge { get; set; }

    // Control-owned caches: the formatted name (with the text, size and colour it was built from) and closable block gaps (layout pixels).
    internal FormattedText? NameText;
    internal string? NameTextOf;
    internal double NameTextSize;
    internal Brush? NameTextBrush;
    internal readonly List<(double Left, double Right)> Gaps = [];
}

// TimelineMenuRequest, ClipMenuRegion and TimelineSeamTarget: TimelineMenuContracts.cs (Pass 5.7.1 wave-0 contract).

/// <summary>
/// A Spring-Zoom view for the host (UnifiedTimelineControl.SpringViewRequested): start and span in seconds, or <paramref name="Home"/>:
/// the view the spring left, restored exactly (Start and Span repeat it).
/// </summary>
public readonly record struct SpringView(double Start, double Span, bool Home);

/// <summary>
/// The unified timeline: all sequence tracks on one immediate-mode surface (headers left, time area right). Rows, blocks, mini-stacks,
/// dead-air seams and gap pills are drawn and hit-tested arithmetically, with no per-item elements. Frequently moving parts have their
/// own visuals: playhead and skim line translate; pans/scrolls translate an over-drawn cache (margins on both sides); the dragged
/// block draws on an overlay. The host supplies rows (<see cref="SetRows"/>) and view (<see cref="SetView"/>) and handles events;
/// the control never edits the model.
/// </summary>
public sealed partial class UnifiedTimelineControl : FrameworkElement
{
    /// <summary>Collapsed child lane height as a fraction of its full height (min 8 px).</summary>
    public const double MiniStackFraction = 0.2;

    // Minimum collapsed lane height (HidesWhenClosed effect lanes are 0 instead).
    private const double MiniLeast = 8;

    // Gap between lanes (panel background shows through).
    private const double LaneGap = 1;

    // Mini-stack expand/collapse: exponential approach with time constant ExpandTau (seconds; ~95% in three), snapping to the target
    // within ExpandSettle. A click mid-animation reverses from the current value.
    private const double ExpandTau = 0.028;
    private const double ExpandSettle = 0.002;

    // Duration of the slide animation when a fold is unfolded or refolded.
    private const double FoldSlideSeconds = 0.12;

    // Below this height a lane draws simplified (block colours and audio only) and is not editable.
    private const double FullDetailHeight = 22;

    // Header indent per mini-stack nesting level.
    private const double DeckIndent = 14;

    // A laid-out lane: row, nesting depth, Top/Height (DIPs from the top of all rows), Small (collapsed), first/last within its parent
    // stack (in screen order), parent lane index, whether its deck hangs from the parent's audio chevron, its own Stacks bits
    // (1 = visual, 2 = audio), and whether it is laid above its parent (InFront with the stack open).
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
        public bool Above;

        // Collapsed audio stack's single summary bar, drawing all audible lanes as one waveform.
        public bool Summary;

        public readonly double Bottom => Top + Height;

        public readonly bool Detailed => !Mini && Height >= FullDetailHeight;

        public readonly bool Has(ChildStack stack) => (Stacks & StackBit(stack)) != 0;

        public readonly int ChevronCount => System.Numerics.BitOperations.PopCount((uint)Stacks);
    }

    private static int StackBit(ChildStack stack) => stack == ChildStack.Audio ? 2 : 1;

    // A row's stacks in chevron order.
    private static readonly ChildStack[] StackOrder = [ChildStack.Visual, ChildStack.Audio];

    private readonly List<TimelineRow> _rows = [];
    private readonly List<Lane> _lanes = [];

    // Current and target openness (0 closed, 1 open) per (row Key, stack); closed-at-rest entries are dropped. Survives row rebuilds
    // after edits and undo because keys are stable.
    private struct Expansion
    {
        public double Current;
        public double Target;
    }

    private readonly Dictionary<(object Key, ChildStack Stack), Expansion> _expansion = [];
    private readonly List<(object Key, ChildStack Stack)> _expansionKeys = [];

    // Single animation clock (stack expansion, fold slides, coasts): subscribed to CompositionTarget.Rendering only while something moves.
    private bool _ticking;
    private long _tickClock;
    private TimeSpan _lastRenderingTime;

    // Active fold slide (Opening = unfold): position and length (seconds), sliding row keys (null = all unlocked), start time, eased progress 0..1.
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

    // Visual tree: the clipped time area holds _scroller (row content + drag overlay in layout pixels, translated into view), block
    // labels (pinned in view while panning), skim line and playhead; headers sit beside it.
    private readonly ContainerVisual _timeArea = new();
    private readonly ContainerVisual _scroller = new();
    private readonly DrawingVisual _content = new();
    private readonly DrawingVisual _overlay = new();
    private readonly DrawingVisual _labels = new();
    private readonly DrawingVisual _pulse = new();
    private readonly DrawingVisual _skim = new();
    private readonly DrawingVisual _playhead = new();
    private readonly DrawingVisual _headers = new();
    private readonly TranslateTransform _shift = new();
    private readonly TranslateTransform _labelsRise = new();
    private readonly TranslateTransform _headersRise = new();

    // Dirty flags, coalesced into one redraw per frame.
    private bool _contentDirty = true, _labelsDirty = true, _headersDirty = true, _overlayDirty = true;

    public UnifiedTimelineControl()
    {
        ClipToBounds = true;
        Focusable = false;
        SnapsToDevicePixels = true;

        // Thumbnails are many and downscaled: use the cheapest bitmap scaling (smooth scaling costs most while scrolling).
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

        // Scroll shift only: Spring-Zoom is a real view zoom (rows relaid at its scale), never a stretch (gestures, Parenting.cs).
        _scroller.Transform = _shift;
        _scroller.Children.Add(_content);
        _scroller.Children.Add(_overlay);
        _labels.Transform = _labelsRise;
        _headers.Transform = _headersRise;
        _timeArea.Children.Add(_scroller);
        _timeArea.Children.Add(_labels);
        _timeArea.Children.Add(_pulse);
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

            // Unloaded mid-spring: the view the spring left comes back at once (no timer runs on a control out of view).
            if (IsSpringZoomed)
            {
                SpringTo(1);
                FinishSpring();
            }

            _autoScroll.Stop();
            DisarmMenu();

            // Cancel any held/dragged track header (nothing moved) and any drag's pending dwell re-check.
            if (_rowDrag is not null)
                AbortDrag();
            StopSettleTimer();
        };

        // Hidden (tab deactivated, pane closed): stop any coast in place.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
                StopMomentum();
        };
        ReadLook();
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _timeArea : _headers;

    /// <summary>Header column width (default 168); the time area takes the remainder.</summary>
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

    /// <summary>Top-level rows, top to bottom.</summary>
    public IReadOnlyList<TimelineRow> Rows => _rows;

    /// <summary>Layout scale: time-area width / view span. Block coordinates use it.</summary>
    public double PixelsPerSecond => TimeAreaWidth > 0 && _viewSpan > 0 ? TimeAreaWidth / _viewSpan : 0;

    /// <summary>View start in layout pixels (view start seconds * PixelsPerSecond).</summary>
    public double ViewOffsetX => _viewStart * PixelsPerSecond;

    /// <summary>Time area width: control width minus header width.</summary>
    public double TimeAreaWidth => Math.Max(ActualWidth - _headerWidth, 0);

    /// <summary>Playhead time in seconds (NaN = none). Setting it only translates the playhead visual.</summary>
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

    /// <summary>Vertical scroll in DIPs, clamped to [0, extent - control height].</summary>
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

    /// <summary>Total laid-out height of all lanes, including animating stacks.</summary>
    public double ExtentHeight => _extent;

    /// <summary>Raised when the vertical extent changes (rows, heights, stack expansion), for an external scroll bar.</summary>
    public event Action? ExtentChanged;

    /// <summary>Raised when the vertical offset changes (wheel, or clamped by a smaller extent).</summary>
    public event Action? VerticalOffsetChanged;

    /// <summary>
    /// PixelsPerSecond changed due to a width change (not <see cref="SetView"/>): the host must re-layout blocks at the new scale.
    /// </summary>
    public event Action? ScaleChanged;

    /// <summary>
    /// Snap targets in seconds, queried once at drag start with the blocks whose audio moves with the drag (move/slip only); those
    /// clips' beats move with them and are excluded.
    /// </summary>
    public Func<IReadOnlyList<TimelineBlock>, IReadOnlyList<double>>? SnapTimes { get; set; }

    /// <summary>
    /// Sequence frame rate: Shift-drags step per frame without snapping, except a move beyond the 6 DIP vertical deadzone, which keeps
    /// its time exactly while Shift is held.
    /// </summary>
    public Func<double>? FrameRate { get; set; }

    /// <summary>Selected item; the block whose Clip matches is outlined.</summary>
    public Func<object?, bool>? IsSelected { get; set; }

    /// <summary>
    /// Raised on mouse down before hit-testing so the host can flush pending row rebuilds and the press sees the current timeline.
    /// </summary>
    public event Action? PressStarting;

    /// <summary>
    /// A host handler (drag commit, click, menu) threw: the control has reverted its drawn state; the host reports and logs it. Never
    /// rethrown to the dispatcher.
    /// </summary>
    public event Action<Exception>? CallbackFailed;

    /// <summary>Block pressed with either button.</summary>
    public event Action<TimelineRow, TimelineBlock, MouseButtonEventArgs>? BlockPressed;

    /// <summary>Empty time area pressed (on a row or below all rows): time in seconds; the host seeks.</summary>
    public event Action<TimelineRow?, double, MouseButtonEventArgs>? EmptyPressed;

    /// <summary>Non-draggable block pressed: the refusal reason.</summary>
    public event Action<string>? DragRefused;

    /// <summary>Drag ended (with or without a change).</summary>
    public event Action? DragEnded;

    /// <summary>Ctrl+wheel: zoom factor about an anchor (pixels from the time area's left).</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>Horizontal pan in pixels (Shift+wheel, middle-drag, or edge auto-scroll during a drag).</summary>
    public event Action<double>? PanRequested;

    /// <summary>Header switch clicked: row, switch, and Alt (exclusive solo).</summary>
    public event Action<TimelineRow, RowSwitches, bool>? HeaderSwitchToggled;

    /// <summary>Right-click: the host fills the request's menu, which opens only if non-empty.</summary>
    public event Action<TimelineMenuRequest>? MenuRequested;

    /// <summary>Dead-air seam clicked: unfold it.</summary>
    public event Action<TimelineRow, SeamMark>? SeamClicked;

    /// <summary>Hide pill on an unfolded range clicked: refold it.</summary>
    public event Action<TimelineRow, RevealMark>? RevealHideRequested;

    /// <summary>Gap pill clicked: row and gap start/end (seconds).</summary>
    public event Action<TimelineRow, double, double>? CloseGapRequested;

    /// <summary>Alt+click (no drag) on a block: split at that time (seconds).</summary>
    public event Action<TimelineRow, TimelineBlock, double>? RazorRequested;

    /// <summary>Alt+Shift+wheel over tracks: change all track heights (positive notches = taller). Alt alone scrolls rows.</summary>
    public event Action<int>? TrackHeightStepped;

    /// <summary>Header lower edge pressed: a global track-height drag begins.</summary>
    public event Action? TrackHeightDragStarted;

    /// <summary>
    /// Global height drag delta in DIPs per video row since drag start, chosen so the edge tracks the pointer (spread over all rows
    /// from the top down to it).
    /// </summary>
    public event Action<double>? TrackHeightDragged;

    /// <summary>Header lower edge double-clicked: reset track height to default.</summary>
    public event Action? TrackHeightReset;

    /// <summary>Per-row height drag: the row's new height in DIPs.</summary>
    public event Action<TimelineRow, double>? RowHeightDragged;

    /// <summary>Per-row height drag ended: committed (true) or cancelled (false, height reverts).</summary>
    public event Action<TimelineRow, bool>? RowHeightDragEnded;

    /// <summary>Per-row height cleared (lower-edge double-click): the row reverts to the global Track Height.</summary>
    public event Action<TimelineRow>? RowHeightReset;

    /// <summary>Skim time under the pointer in the time area (seconds); null on leave.</summary>
    public event Action<double?>? SkimChanged;

    /// <summary>
    /// Pointer time (seconds) for Instant Seek hints, over the time area (also mid-drag) and the ruler (via
    /// <see cref="NotePointerOverRuler"/>); null over headers, outside, or when a drag ends outside. Raised only on change; allocation-free.
    /// </summary>
    public event Action<double?>? PointerSecondsChanged;

    /// <summary>
    /// Left click (press + no-move release) on a block, for decisions deferred from the press: Ctrl/Shift on a selected block, or
    /// narrowing a group to the clicked block.
    /// </summary>
    public event Action<TimelineRow, TimelineBlock>? BlockClicked;

    /// <summary>
    /// True during <see cref="BlockPressed"/> when the press is an edge ripple (Ctrl on a trimmable edge, as <c>StartDrag</c> decides);
    /// the host defers selection changes until release.
    /// </summary>
    public bool PressRipples { get; private set; }

    /// <summary>Header name left-clicked (not a chevron or switch).</summary>
    public event Action<TimelineRow>? HeaderClicked;

    /// <summary>Preset badge clicked: row and badge bounds (control coordinates) to anchor the preset list.</summary>
    public event Action<TimelineRow, Rect>? BadgePressed;

    /// <summary>
    /// Rubber-band selection released: intersected blocks on full-size lanes (top to bottom) and whether Ctrl/Shift was held (add to the
    /// selection instead of replacing it).
    /// </summary>
    public event Action<IReadOnlyList<TimelineBlock>, bool>? MarqueeSelected;

    /// <summary>
    /// Batches a group drag's edits into one Undo step (label, action), all-or-nothing (MainViewModel.EditTogether); returns true when
    /// applied. When unset, each block's edit commits separately.
    /// </summary>
    public Func<string, Action, bool>? Together { get; set; }

    /// <summary>A row's mini-stack expanded or collapsed (chevron click or <see cref="SetExpanded(TimelineRow, bool)"/>).</summary>
    public event Action<TimelineRow>? ExpansionChanged;

    /// <summary>
    /// Replaces the top-level rows (children form their mini-stacks). Expansion is keyed by row Key, so rebuilt rows keep their state.
    /// An active drag continues if its block is still present; otherwise it is cancelled and reverted with a reason.
    /// </summary>
    public void SetRows(IReadOnlyList<TimelineRow> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);

        // Rows relaid at a new scale mid-drag (Spring-Zoom): the drag carries on over them (ReseatDrag, Family.cs).
        var reseated = ReseatDrag();
        if (!reseated && DraggedBlock is { } dragged && !Contains(_rows, dragged))
        {
            AbortDrag();
            Raise(() => DragRefused?.Invoke("What was being dragged changed meanwhile (an undo or another edit), so it was let go where it was."));
        }

        // Held/dragged track header whose row instance is gone (rows rebuilt): cancel, nothing moved.
        if (_rowDrag is { } rowDrag && !ListsRow(_rows, rowDrag.Row))
            AbortDrag();

        _hovered = null;
        Relayout();
        if (reseated)
            ResumeDrag();
        FindSeamAgain();
    }

    /// <summary>
    /// Sets view start/span and sequence duration (seconds; beyond the end is shaded). Same span (pan): the drawn cache translates and
    /// redraws only when the view leaves it. New span: the host re-lays blocks at the new PixelsPerSecond and passes them via
    /// <see cref="SetRows"/> or <see cref="Redraw"/>.
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
        {
            // Zoom, fit or sequence change: stop any coast on the old view.
            _momentumX?.Stop();
            Invalidate();
            PlaceLine(_playhead, _playheadSeconds, ref _playheadHeight, playhead: true);
            PlaceLine(_skim, _skimSeconds, ref _skimHeight, playhead: false);
        }
        else
        {
            Slide();
        }
    }

    /// <summary>Blocks changed in place (moved, selected, waveform/thumbnails arrived): full redraw.</summary>
    public void Redraw()
    {
        Relayout();
    }

    /// <summary>Whether any of a row's mini-stacks is open (target state while animating).</summary>
    public bool IsExpanded(TimelineRow row) => IsExpanded(row, ChildStack.Visual) || IsExpanded(row, ChildStack.Audio);

    /// <summary>Whether a specific mini-stack is open (target state while animating).</summary>
    public bool IsExpanded(TimelineRow row, ChildStack stack) => _expansion.TryGetValue((row.Key, stack), out var expansion) && expansion.Target >= 0.5;

    /// <summary>
    /// Snapshot of open (or opening) mini-stacks by row key, stored per project tab on deactivation because keys ("captions",
    /// "background", track numbers) repeat across projects.
    /// </summary>
    public IReadOnlyList<(object Key, ChildStack Stack)> SaveExpansion() => [.. _expansion.Where(e => e.Value.Target >= 0.5).Select(e => e.Key)];

    /// <summary>
    /// Restores a tab's <see cref="SaveExpansion"/> snapshot on activation: listed stacks open, all others closed, applied instantly
    /// (no animation).
    /// </summary>
    public void RestoreExpansion(IReadOnlyList<(object Key, ChildStack Stack)>? open)
    {
        _expansion.Clear();
        foreach (var key in open ?? [])
            _expansion[key] = new Expansion { Current = 1, Target = 1 };
        if (!IsMoving())
            StopTicking();
        Relayout();
    }

    /// <summary>Expands or collapses all of a row's mini-stacks (see <see cref="SetExpanded(TimelineRow, ChildStack, bool)"/>).</summary>
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
    /// Animates one mini-stack between full and 1/5 lane height (ease-out, ~95% within 90 ms); a repeat call mid-animation reverses
    /// from the current value.
    /// </summary>
    public void SetExpanded(TimelineRow row, ChildStack stack, bool expanded)
    {
        if (Target(row, stack, expanded))
            AfterExpansion(row);
    }

    // Sets a stack's target; false if unchanged. Snaps immediately when nothing visible would animate (no lanes, control hidden).
    private bool Target(TimelineRow row, ChildStack stack, bool expanded)
    {
        var key = (row.Key, stack);
        var target = expanded ? 1.0 : 0.0;
        var known = _expansion.TryGetValue(key, out var expansion);
        if (expansion.Target == target && (known || !expanded))
            return false;

        expansion.Target = target;
        if (!HasStack(row, stack) || !IsVisible || !Services.AppSettings.Current.EnableExtraAnimations)
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

    // ----- Reveal a clip (Find Layer on Timeline) -----

    private const double PulseSeconds = 1.2;
    private const int PulseCount = 3;
    private static readonly Pen PulseOutline = UiBrushes.Frozen(new Pen(Brushes.White, 2.5));

    // The pulsed clip (its block found again by ClipId after row rebuilds) and when its pulse began.
    private Models.Layer? _pulseClip;
    private long _pulseBegan;

    /// <summary>
    /// Find Layer on Timeline: unfolds every collapsed stack above the row showing <paramref name="clip"/>, keeps that row scrolled into
    /// view while they open (as a click-opened stack) and pulses its block's outline three times. False when no row shows the clip.
    /// </summary>
    public bool RevealClip(Models.Layer clip)
    {
        if (RowShowing(_rows, clip) is not { } row)
            return false;

        var child = row;
        while (child.Parent is { } parent)
        {
            if (!IsExpanded(parent, child.Stack))
                SetExpanded(parent, child.Stack, true);
            child = parent;
        }

        (_keepInView, _keepInViewUntil) = (row, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        KeepLaneInView();
        (_pulseClip, _pulseBegan) = (clip, Stopwatch.GetTimestamp());
        _pulse.Opacity = 0;
        PlacePulse();
        StartTicking();
        return true;
    }

    // The first row (depth-first, top to bottom) with a block showing the clip.
    private static TimelineRow? RowShowing(List<TimelineRow> rows, Models.Layer clip)
    {
        foreach (var row in rows)
        {
            foreach (var block in row.Blocks)
            {
                if (Shows(block, clip))
                    return row;
            }

            if (RowShowing(row.Children, clip) is { } shown)
                return shown;
        }

        return null;
    }

    // A block shows the clip: the same Layer, or a view of the same Timeline clip (rows are rebuilt from fresh views).
    private static bool Shows(TimelineBlock block, Models.Layer clip) =>
        ReferenceEquals(block.Clip, clip) || (block.Clip is Models.Layer shown && clip.ClipId != Guid.Empty && shown.ClipId == clip.ClipId);

    // Draws the pulse outline where the pulsed clip's block lies now (stacks opening, scrolled, rows rebuilt); ends a finished pulse.
    private void PlacePulse()
    {
        if (_pulseClip is not { } clip)
            return;
        if (Stopwatch.GetTimestamp() - _pulseBegan > PulseSeconds * Stopwatch.Frequency)
        {
            EndPulse();
            return;
        }

        using var drawing = _pulse.RenderOpen();
        foreach (var lane in _lanes)
        {
            if (lane.Mini || lane.Height <= 0)
                continue;

            foreach (var block in lane.Row.Blocks)
            {
                if (!Shows(block, clip))
                    continue;

                var (top, height) = block.Height > 0 ? (lane.Top + block.Top, block.Height) : (lane.Top, lane.Height);
                var rect = new Rect(_headerWidth + block.Left - ViewOffsetX, top - _verticalOffset, Math.Max(block.Width, 2), Math.Max(height, 2));
                drawing.DrawRoundedRectangle(null, PulseOutline, rect, 3, 3);
                return;
            }
        }
    }

    private void EndPulse()
    {
        _pulseClip = null;
        _pulse.Opacity = 0;
        _pulse.RenderOpen().Close();
    }

    /// <summary>
    /// Animates (~120 ms) content after a fold the host just unfolded (<paramref name="opening"/>: the range at <paramref name="atSeconds"/>,
    /// <paramref name="lengthSeconds"/> long, wipes in and later content slides out) or refolded (later content slides in). Rows already
    /// hold the final state; only drawing is offset, for the given row keys (null = all unlocked). A press finishes it immediately.
    /// </summary>
    public void AnimateFold(double atSeconds, double lengthSeconds, bool opening, IReadOnlyCollection<object>? rowKeys)
    {
        if (!IsVisible || !(lengthSeconds > 0) || !double.IsFinite(atSeconds) || !double.IsFinite(lengthSeconds))
            return;

        // Rows already hold the final state: with animations off, just draw it.
        if (!Services.AppSettings.Current.EnableExtraAnimations)
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

    /// <summary>Completes a fold slide immediately (on press or drag, so drawing matches hit-testing).</summary>
    private void FinishSlide()
    {
        if (_slide is null)
            return;

        _slide = null;
        _contentDirty = _labelsDirty = true;
        InvalidateVisual();
    }

    /// <summary>Time in seconds at an x position (control coordinates).</summary>
    public double TimeAt(double x)
    {
        var scale = PixelsPerSecond;
        return scale > 0 ? (x - _headerWidth + ViewOffsetX) / scale : _viewStart;
    }

    /// <summary>
    /// File-drop hit test: the row/open lane under the point (a collapsed strip maps to its nearest full ancestor; null below all rows),
    /// the time (seconds, >= 0), and whether over headers (append after the last clip). A collapsed caption strip resolves to the
    /// caption row itself, never the video row above it.
    /// </summary>
    public (TimelineRow? Row, double Seconds, bool LeftEdgeZone) HitTestDrop(Point p)
    {
        var under = LaneAt(p.Y + _verticalOffset);
        var lane = under >= 0 && _lanes[under] is { Mini: true, Row.Kind: TimelineRowKind.Captions } ? under : OpenLaneOf(under);
        return (lane >= 0 ? _lanes[lane].Row : null, Math.Max(TimeAt(p.X), 0), p.X < _headerWidth);
    }

    /// <summary>
    /// Shows the drop preview: target row (null = new row below all), time, file count, and whether files stack onto separate tracks
    /// instead of sequencing.
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

    /// <summary>True while a block, fade, gain line, J/L cut or started track-header drag is active; the host must not replace rows meanwhile.</summary>
    public bool IsDragging => _drag is not null || _duckDrag is not null || _gainDrag is not null || _fadeDrag is not null || _splitDrag is not null || _crossfadeDrag is not null
        || _rowDrag is { Started: true };

    // ----- Laying out -----

    /// <summary>Re-lays all lanes (rows, heights, stacks) and schedules a full redraw.</summary>
    private void Relayout()
    {
        _lanes.Clear();
        var top = 0.0;
        for (var i = 0; i < _rows.Count; i++)
            AddLanes(_rows[i], null, 0, -1, 1, 1, false, false, false, false, false, ref top);

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

            // Re-clamp: a shorter extent may leave the offset past the end.
            VerticalOffset = _verticalOffset;
        }

        KeepLaneInView();
        PlacePulse();
    }

    /// <summary>While click-opened stacks grow, keeps the clicked lane scrolled into view (fully, when it fits).</summary>
    private void KeepLaneInView()
    {
        if (_keepInView is not { } row)
            return;
        if (System.Diagnostics.Stopwatch.GetTimestamp() > _keepInViewUntil)
        {
            _keepInView = null;
            return;
        }

        foreach (var lane in _lanes)
        {
            if (!Equals(lane.Row.Key, row.Key))
                continue;
            var height = ActualHeight;
            if (lane.Bottom > _verticalOffset + height)
                VerticalOffset = Math.Min(lane.Top, lane.Bottom - height);
            else if (lane.Top < _verticalOffset)
                VerticalOffset = lane.Top;
            return;
        }
    }

    /// <summary>
    /// Golden-test snapshot (timeline-effect-lanes-fold) of laid-out lanes top to bottom: row key, Top/Height in DIPs (0 for collapsed
    /// effect lanes), Small, Stacks bits (1 = visual, 2 = audio), and whether it is the last visible lane of its stack.
    /// </summary>
    internal IReadOnlyList<(object Key, double Top, double Height, bool Mini, bool Detailed, int Stacks, bool Last)> LaidLanes =>
        _lanes.ConvertAll<(object Key, double Top, double Height, bool Mini, bool Detailed, int Stacks, bool Last)>(
            lane => (lane.Row.Key, lane.Top, lane.Height, lane.Mini, lane.Detailed, lane.Stacks, lane.LastChild));

    // Lanes laid above their parent before the parent's index exists; patched once it is known.
    private const int PendingParent = int.MinValue;

    // Lays a row's lane and its children recursively. Child height = lerp(1/5, 1, eased stack openness); children of a collapsed
    // child are collapsed. HidesWhenClosed effect lanes are 0 when collapsed (or under a collapsed ancestor) and grow from 0.
    // Visual stack then audio stack go below the parent. While the visual stack is open/opening, InFront children are laid above
    // the parent (topmost first) and the rest below; collapsed, all are thin lanes below. During the transition the above-copies
    // grow from 0 while the below-copies shrink, so nothing jumps; such lanes and their gaps scale by <paramref name="fade"/>.
    // Lanes stay sorted top to bottom (LaneAt relies on it). <paramref name="above"/>: this row is laid above its parent.
    private void AddLanes(TimelineRow row, TimelineRow? parent, int depth, int parentLane, double openness, double fade, bool firstChild, bool lastChild,
        bool secondDeck, bool above, bool summary, ref double top)
    {
        if (parent is not null)
            row.Parent ??= parent;

        var full = Math.Max(row.Height, MiniLeast);
        var small = Math.Max(full * MiniStackFraction, MiniLeast);
        var height = (depth == 0 ? full : small + (full - small) * Math.Clamp(openness, 0, 1)) * fade;
        var mini = depth > 0 && (openness < 0.999 || fade < 0.999);
        var stacks = 0;
        foreach (var child in row.Children)
            stacks |= StackBit(child.Stack);

        // InFront children while the visual stack is at all open: laid above it with their own stacks; until fully open their thin
        // copies below it shrink away (leaving).
        var layersOpen = Math.Min(openness, Openness(row, ChildStack.Visual));
        var aboveCount = 0;
        if (layersOpen > 0)
        {
            foreach (var child in row.Children)
                aboveCount += child is { Stack: ChildStack.Visual, InFront: true } ? 1 : 0;
        }

        bool Above(TimelineRow child) => aboveCount > 0 && child is { Stack: ChildStack.Visual, InFront: true };
        var leaving = aboveCount > 0 && layersOpen < 1;

        var firstAbove = _lanes.Count;
        if (aboveCount > 0)
        {
            var k = 0;
            foreach (var child in row.Children)
            {
                if (!Above(child))
                    continue;
                AddLanes(child, row, depth + 1, PendingParent, layersOpen, fade * layersOpen, k == 0, k == aboveCount - 1, false, true, false, ref top);
                k++;
            }
        }

        var index = _lanes.Count;
        _lanes.Add(new Lane
        {
            Row = row, Depth = depth, Top = top, Height = height, Mini = mini, FirstChild = firstChild, LastChild = lastChild,
            Parent = parentLane, SecondDeck = secondDeck, Stacks = stacks, Above = above, Summary = summary,
        });
        foreach (var block in row.Blocks)
            block.IsMini = mini;
        top += height + LaneGap * fade;

        for (var i = firstAbove; i < index; i++)
        {
            if (_lanes[i].Parent != PendingParent)
                continue;
            var lane = _lanes[i];
            lane.Parent = index;
            _lanes[i] = lane;
        }

        if (row.Children.Count == aboveCount && !leaving)
            return;

        // Remaining children below (plus leaving InFront copies, thin and collapsed), grouped per stack; first/last are computed over
        // lanes with non-zero height so the deck line ends at the last visible lane.
        bool Under(TimelineRow child) => leaving || !Above(child);
        bool Shows(TimelineRow child) => !child.HidesWhenClosed || (!Above(child) && Math.Min(openness, Openness(row, child.Stack)) > 0);
        var two = stacks == 3;
        for (var i = 0; i < row.Children.Count; i++)
        {
            var child = row.Children[i];
            if (!Under(child))
                continue;

            var before = i - 1;
            while (before >= 0 && !(Under(row.Children[before]) && Shows(row.Children[before])))
                before--;
            var after = i + 1;
            while (after < row.Children.Count && !(Under(row.Children[after]) && Shows(row.Children[after])))
                after++;

            var first = before < 0 || row.Children[before].Stack != child.Stack;
            var last = after >= row.Children.Count || row.Children[after].Stack != child.Stack;
            var (open, share) = Above(child) ? (0.0, fade * (1 - layersOpen)) : (Math.Min(openness, Openness(row, child.Stack)), fade);

            // Effect lane: height (and gap) scale with its track's stack openness, only when the track is full size; otherwise 0.
            if (child.HidesWhenClosed)
                share *= open;

            // Other children are never hidden: collapsed, each is a 1/5-height bar. A collapsed audio stack is a single bar (its first lane)
            // drawing all audible streams; the others appear as it opens.
            var stackOpen = Openness(row, child.Stack);
            var oneBar = child.Stack == ChildStack.Audio && first && stackOpen < 0.999;
            if (child.Stack == ChildStack.Audio && !first)
                share *= stackOpen;
            AddLanes(child, row, depth + 1, index, open, share, first, last, two && child.Stack == ChildStack.Audio, false, oneBar, ref top);
        }
    }

    private double Openness(TimelineRow row, ChildStack stack) => _expansion.TryGetValue((row.Key, stack), out var expansion) ? expansion.Current : 0;

    // Closable gaps (layout pixels) between draggable blocks on full-size video/audio rows with ClosesGaps: from a block's end
    // to the next block's start, plus the leading gap from 0 s on top-level rows (sub-lanes follow their parent clip), only those
    // wide enough for the pill.
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

        if (lane.Depth == 0 && _spans[0].Left >= GapLeast)
            row.Gaps.Add((0, _spans[0].Left));

        var reach = _spans[0].Right;
        for (var i = 1; i < _spans.Count; i++)
        {
            var (left, right) = _spans[i];
            if (left - reach >= GapLeast)
                row.Gaps.Add((reach, left));
            reach = Math.Max(reach, right);
        }
    }

    // Lane index at a y (DIPs from the top of all rows); the gap below a lane belongs to it. -1 for none.
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

    // Maps a collapsed lane to its nearest full-size ancestor, so drops/drags onto a thin strip target that row, never the hidden
    // track. -1 maps to -1.
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

    // Reference-equality search through all stack depths (a rebuilt row with the same key does not match).
    private static bool ListsRow(List<TimelineRow> rows, TimelineRow row)
    {
        foreach (var each in rows)
        {
            if (ReferenceEquals(each, row) || ListsRow(each.Children, row))
                return true;
        }

        return false;
    }

    // ----- Animation: stack expansion, fold slides, momentum coasts -----

    // True while any stack is off target, a fold is sliding, or the view is coasting.
    private bool IsMoving()
    {
        if (_slide is not null || _momentumX?.IsCoasting == true || _momentumY?.IsCoasting == true)
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

    // Per-frame tick: advance stacks exponentially (mid-way reversals restart at full speed) and the fold slide, then lay out and
    // redraw once. Unsubscribes when everything has settled.
    private void OnRenderingTick(object? sender, EventArgs e)
    {
        // Rendering may fire more than once per frame: dedupe by RenderingTime.
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime)
                return;
            _lastRenderingTime = rendering.RenderingTime;
        }

        var now = Stopwatch.GetTimestamp();

        // Clamp dt so a stall (heavy frame, window drag) cannot cause a large jump.
        var seconds = Math.Clamp((now - _tickClock) / (double)Stopwatch.Frequency, 0, 0.05);
        _tickClock = now;

        var laidOut = StepExpansion(seconds);
        if (_slide is { } slide)
        {
            var done = Math.Clamp((now - slide.Began) / (double)Stopwatch.Frequency / FoldSlideSeconds, 0, 1);

            // Ease-out.
            slide.Eased = 1 - Math.Pow(1 - done, 3);
            if (done >= 1)
                _slide = null;
            _contentDirty = _labelsDirty = true;
            InvalidateVisual();
        }

        if (_pulseClip is not null)
        {
            var pulsed = (now - _pulseBegan) / (double)Stopwatch.Frequency / PulseSeconds;
            if (pulsed >= 1)
                EndPulse();
            else
                _pulse.Opacity = 0.5 - 0.5 * Math.Cos(pulsed * PulseCount * 2 * Math.PI);
        }

        if (laidOut)
            Relayout();
        StepMomentum(now * 1000.0 / Stopwatch.Frequency);

        // A Find Layer pulse keeps the clock running until it ends (it moves nothing, so IsMoving leaves it out).
        if (!IsMoving() && _pulseClip is null)
            StopTicking();
    }

    /// <summary>
    /// Advances momentum to <paramref name="nowMs"/> (<see cref="MomentumScroller.Now"/> clock): pans the view via <see cref="PanRequested"/>
    /// (host applies synchronously) and scrolls rows. A coast that made no progress (end reached, view fitted) or a drag stops it.
    /// Returns whether anything moved. Called by the frame loop; tests step it with their own clock.
    /// </summary>
    internal bool StepMomentum(double nowMs)
    {
        if (IsDragging)
        {
            StopMomentum();
            return false;
        }

        var moved = false;
        if (_momentumX is { IsCoasting: true } alongX)
        {
            var pixels = alongX.Tick(nowMs);
            if (pixels != 0)
            {
                var before = ViewOffsetX;
                RaisePan(pixels);
                if (ViewOffsetX == before)
                    alongX.Stop();
                else
                    moved = true;
            }
        }

        if (_momentumY is { IsCoasting: true } downY)
        {
            var rows = downY.Tick(nowMs);
            if (rows != 0)
            {
                var before = _verticalOffset;
                VerticalOffset = before + rows;
                if (_verticalOffset == before)
                    downY.Stop();
                else
                    moved = true;
            }
        }

        return moved;
    }

    /// <summary>Stops view and row coasts in place (press, playhead page turn, another pan).</summary>
    public void StopMomentum()
    {
        _momentumX?.Stop();
        _momentumY?.Stop();
        _wheelGlide?.StopPan();
    }

    /// <summary>True while the view or rows are coasting.</summary>
    public bool IsCoasting => _momentumX?.IsCoasting == true || _momentumY?.IsCoasting == true;

    // Advances every animating stack; returns true if any moved (caller re-lays lanes).
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

    // On unload/hide: snap animations to their targets, stop coasts in place, and unsubscribe the clock.
    private void SettleMotion()
    {
        StopMomentum();
        var moving = IsMoving();
        StopTicking();
        _slide = null;
        EndPulse();
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

    // ----- Translation and redraw -----

    /// <summary>Schedules a full redraw at the next render.</summary>
    private void Invalidate()
    {
        _contentDirty = _labelsDirty = _headersDirty = _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Same-scale view move (pan/scroll): translates the drawn cache and redraws only when the view leaves the cached range. View-pinned
    /// labels are re-laid.
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
        PlacePulse();
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

    // ----- Theme -----

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

    /// <summary>Reads the theme's timeline brush/pen tokens and freezes them.</summary>
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
    // One automation element named "Timeline"; each lane is a child peer with its row name and bounds so tests and screen readers
    // can locate rows. Lane contents have no peers.

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);

    private sealed class TimelinePeer(UnifiedTimelineControl owner) : FrameworkElementAutomationPeer(owner)
    {
        private readonly Dictionary<object, RowPeer> _rowPeers = [];

        protected override string GetClassNameCore() => nameof(UnifiedTimelineControl);

        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : "Timeline";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            // A row may briefly have two lanes during a stack transition (AddLanes): emit one peer.
            var children = new List<AutomationPeer>(owner._lanes.Count);
            foreach (var lane in owner._lanes)
            {
                if (!_rowPeers.TryGetValue(lane.Row.Key, out var peer))
                    _rowPeers[lane.Row.Key] = peer = new RowPeer(owner, lane.Row.Key);
                if (!children.Contains(peer))
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
