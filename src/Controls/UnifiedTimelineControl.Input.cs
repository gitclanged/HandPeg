using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// Unified timeline mouse input: select, seek, move (within/across rows), trims, Ctrl ripple, roll, Alt slip, fades, gain lines,
// snapping (Shift = frame steps, no snap), edge auto-scroll, revert on capture loss, cursors and tooltips; header switches and
// chevrons, J/L cuts (Alt on an edge's lower half), Alt+click cut, seams, gap pills, skim line, menus; group drags, captions following
// their clip, click-vs-press, Instant Seek pointer time; DragSettle velocity-gated drop zones, a 6 DIP row-change deadzone, Shift
// keeping time on vertical moves, tethers and header drags (UnifiedTimelineControl.Parenting.cs).
public sealed partial class UnifiedTimelineControl
{
    // Gain line hit tolerance.
    private const double GainReach = 4;

    // Fade handle horizontal hit tolerance, and the depth of the top band holding fade handles (edges below it trim).
    private const double FadeReach = 6;
    private const double FadeBand = 10;

    // Edge distance that triggers drag auto-scroll.
    private const double AutoScrollZone = 28;

    // Minimum gap width (pixels) to show a close-gap pill.
    private const double GapLeast = 28;

    // Vertical scroll per plain wheel notch.
    private const double WheelStep = 48;

    // Alt-press movement threshold (pixels): below it the release is a click that cuts.
    private const double RazorTravel = 2;

    // Active block drag: row, mode, start pointer and block (plus the next block for a roll), Alt at press (no-move = cut), travel,
    // and the target row under the pointer (with permission).
    private sealed class BlockDrag
    {
        public required TimelineRow Row { get; init; }
        public required TimelineBlock Block { get; init; }
        public required BlockEdit Kind { get; init; }
        public double PointerX { get; init; }
        public double Left { get; init; }
        public double Width { get; init; }
        public bool Ripple { get; init; }
        public TimelineBlock? Neighbor { get; init; }
        public double NeighborLeft { get; init; }
        public double NeighborWidth { get; init; }
        public bool Alt { get; init; }

        /// <summary>Slip offset so far in pixels (media content shift).</summary>
        public double Slip { get; set; }

        public double Travel { get; set; }
        public TimelineRow? Target { get; set; }
        public bool Fits { get; set; }

        /// <summary>Edge drag at a dead-air seam: unfolds or folds more, sliding later blocks.</summary>
        public SeamGrip? Seam { get; init; }

        /// <summary>False when carried children would start before time 0 at the drawn position.</summary>
        public bool FamilyFits { get; set; } = true;

        /// <summary>Other group members moved or trimmed (same edge) by the same delta; null for none.</summary>
        public List<Coupled>? Group { get; set; }

        /// <summary>Group move: minimum left edge so no member crosses time 0.</summary>
        public double GroupFloor { get; set; }

        /// <summary>The dragged block is carried by a group member (its parent); only the other members' edits are committed.</summary>
        public bool CarriedByGroup { get; set; }

        /// <summary>Press y in layout space; a move targets a new-track seam only after leaving this by the deadzone.</summary>
        public double PointerY { get; init; }

        /// <summary>
        /// New-track seam under a move: insert above/below top-level video row <c>Beside</c>, bar drawn at layout <c>Y</c>. Null when over
        /// none or refused by the host.
        /// </summary>
        public (TimelineRow Beside, bool Above, double Y)? NewTrack { get; set; }

        /// <summary>Whether a drop at <see cref="NewTrack"/> is allowed (host CanMoveToNewTrack, children within bounds).</summary>
        public bool NewTrackFits { get; set; }

        /// <summary>Modifiers at the latest move or dwell tick (initially the press's): Alt mid-move tethers, Shift keeps the move's time.</summary>
        public ModifierKeys Keys { get; set; }

        /// <summary>Shift held on a move that left its lane (other row, seam or tether): time is kept exact while Shift stays down.</summary>
        public bool TimeLocked { get; set; }

        /// <summary>Tether target: on release the block becomes its child (parent row, block, and pill name); null for none.</summary>
        public (TimelineRow Row, TimelineBlock Block, string Name)? Attach { get; set; }
    }

    // Dead-air seam edge drag: seam, edge (true = later clip's start / fold tail; false = earlier clip's end / fold head), outward
    // (Reveal) and inward (Fold) limits in seconds, cut x (layout pixels), current outward travel (seconds, negative inward), and
    // sliding row keys (null = all unlocked).
    private sealed class SeamGrip
    {
        public required SeamMark Mark { get; init; }
        public bool AtStart { get; init; }
        public double Reveal { get; init; }
        public double Fold { get; init; }
        public double Cut { get; init; }
        public HashSet<object>? Rows { get; init; }
        public double Seconds { get; set; }
    }

    // A block carried by a drag, drawn on the overlay: linked children on other rows (collapsed or open), unlinked audio, file audio
    // Sub-Lanes. Follows the parent's travel or its moved edge (trim, roll, seam), or stays and shows Detaching when a trim leaves it
    // wholly outside (which unlinks it). Captions follow too (Within: clipped to the trimmed clip; SlipWithin: slid with the media and
    // clipped), as do group Members (same move or edge). Root is the block whose edges constrain it. Stores its start position.
    private enum Follows { Move, Start, End, SeamEdge, Within, SlipWithin }

    private sealed class Coupled
    {
        public required TimelineRow Row { get; init; }
        public required TimelineBlock Block { get; init; }
        public Follows How { get; init; }
        public bool OfNeighbor { get; init; }
        public double Left0 { get; init; }
        public double Width0 { get; init; }
        public bool Detaching { get; set; }
        public TimelineBlock? Root { get; init; }
        public bool Member { get; init; }
    }

    private readonly List<Coupled> _coupled = [];
    private readonly HashSet<TimelineBlock> _coupledSet = new(ReferenceEqualityComparer.Instance);

    // Other dragged group members (never collected as another block's child).
    private readonly HashSet<TimelineBlock> _groupSet = new(ReferenceEqualityComparer.Instance);

    // J/L cut drag: audio edge (true = start), press pointer, and lead-in/tail-out at start and now (seconds).
    private sealed class SplitDrag
    {
        public required TimelineRow Row { get; init; }
        public required TimelineBlock Block { get; init; }
        public bool AtLead { get; init; }
        public double PointerX { get; init; }
        public double StartLead { get; init; }
        public double StartTail { get; init; }
        public double Lead { get; set; }
        public double Tail { get; set; }
    }

    private BlockDrag? _drag;
    private SplitDrag? _splitDrag;

    // Header lower-edge drag: press y and the number of rows above that share the global height delta (so the edge tracks the
    // pointer); with a row set, a per-row resize from its starting height.
    private (double FromY, int Rows, TimelineRow? Row, double FromHeight)? _heightDrag;

    // Header lower-edge hit tolerance (DIPs), and wheel notches per height step.
    private const double HeightEdgeReach = 3;

    // Gain line drag: row, block, start pointer and start values.
    private (TimelineRow Row, TimelineBlock Block, Point From, (double GainDb, double FadeIn, double FadeOut) Start)? _gainDrag;

    // Fade handle drag: row, block, which fade (true = in), and the fades at drag start.
    private (TimelineRow Row, TimelineBlock Block, bool In, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) Start)? _fadeDrag;

    // Hovered block (shows its fade handles).
    private (TimelineRow Row, TimelineBlock Block)? _hovered;

    // Hovered gap (row, gap index): the only pill shown and clickable.
    private (TimelineRow Row, int Index)? _hoveredGap;

    // Last drag pointer in layout pixels, replayed by auto-scroll ticks.
    private Point _lastPointer;
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    // Middle-button pan: last pointer position.
    private double? _panFrom;

    // Momentum scrollers: horizontal (Shift+wheel, tilt wheel, middle flick) and vertical (plain wheel), created on first use (none before
    // Frame 1); plus the middle-drag pan distance since press.
    private MomentumScroller? _momentumX, _momentumY;
    private double _panTravel;

    private MomentumScroller MomentumX => _momentumX ??= new MomentumScroller();

    private MomentumScroller MomentumY => _momentumY ??= new MomentumScroller();

    private IReadOnlyList<double> _snapTimes = [];
    private double _skimSeconds = double.NaN;

    // Last raised pointer time (PointerSecondsChanged), NaN for none.
    private double _pointerSeconds = double.NaN;

    // Current tooltip target (row/block and part), so the tooltip is set once per target, not per move.
    private object? _tipTarget;
    private int _tipPart = -1;

    // Pending right-click: target and point; whether a key was pressed meanwhile (button held for WASD navigation: no menu); and the
    // window whose keys are monitored.
    private (TimelineRow? Row, TimelineBlock? Block, bool OnHeader, bool? FadeIn, Point At)? _menuPress;
    private bool _menuCancelled;
    private Window? _menuWindow;

    /// <summary>Suppresses the pending right-click menu on release (the button was held for navigation).</summary>
    public void CancelPendingMenu() => _menuCancelled = true;

    private TimelineBlock? DraggedBlock => _drag?.Block ?? _fadeDrag?.Block ?? _gainDrag?.Block ?? _splitDrag?.Block;

    private bool IsDragged(TimelineBlock block) => _drag is { } drag
        ? ReferenceEquals(block, drag.Block) || ReferenceEquals(block, drag.Neighbor) || (_coupledSet.Count > 0 && _coupledSet.Contains(block))
        : ReferenceEquals(block, _fadeDrag?.Block) || ReferenceEquals(block, _gainDrag?.Block) || ReferenceEquals(block, _splitDrag?.Block);

    // Control point to layout space (x in block layout pixels, y from the top of all rows).
    private Point Laid(Point point) => new(point.X - _headerWidth + ViewOffsetX, point.Y + _verticalOffset);

    /// <summary>
    /// Invokes a host event/callback, routing any exception to <see cref="CallbackFailed"/> (or the log if unsubscribed) instead of the
    /// dispatcher. Returns false on failure.
    /// </summary>
    private bool Raise(Action raise)
    {
        try
        {
            raise();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(ex);
        }
    }

    /// <summary>Allocation-free <see cref="Raise"/> for per-frame coast pan steps.</summary>
    private bool RaisePan(double pixels)
    {
        try
        {
            PanRequested?.Invoke(pixels);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(ex);
        }
    }

    // Reports a host exception to CallbackFailed, else the log. Always returns false.
    private bool Failed(Exception ex)
    {
        if (CallbackFailed is { } failed)
        {
            try
            {
                failed(ex);
            }
            catch (Exception reporting) when (reporting is not OutOfMemoryException)
            {
                Services.AppLog.Write("Timeline: telling of a failed edit failed", reporting);
            }
        }
        else
        {
            Services.AppLog.Write("Timeline: an edit failed", ex);
        }

        return false;
    }

    // ----- Press -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _clickOnly = null;

        // Flush pending host rebuilds and finish any fold slide so hit-testing sees the current timeline.
        if (!IsDragging)
            Raise(() => PressStarting?.Invoke());
        FinishSlide();

        var point = e.GetPosition(this);
        var laid = Laid(point);
        var index = LaneAt(laid.Y);
        if (point.X < _headerWidth)
        {
            // Header lower edge: drag resizes that track (double-click clears its override); with Shift or on a non-track row, it
            // drags the global Track Height.
            if (HeightEdgeAt(laid.Y) is >= 0 and var edge)
            {
                var sizedRow = _lanes[edge].Row;
                var alone = sizedRow.SizedAlone && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                if (e.ClickCount == 2)
                {
                    if (alone)
                        Raise(() => RowHeightReset?.Invoke(sizedRow));
                    else
                        Raise(() => TrackHeightReset?.Invoke());
                    return;
                }

                _heightDrag = (point.Y, RowsGrowingDownTo(edge), alone ? sizedRow : null, sizedRow.Height);
                CaptureMouse();
                if (!alone)
                    Raise(() => TrackHeightDragStarted?.Invoke());
                return;
            }

            if (index >= 0)
                HeaderPressed(index, new Point(point.X, laid.Y), e);
            return;
        }

        if (index < 0)
        {
            PressEmpty(null, point, e);
            return;
        }

        var lane = _lanes[index];
        var row = lane.Row;

        // Crossfade box: its button removes it and its edges resize it; any other press falls through to the clips beneath.
        if (CrossfadeAt(lane, laid) is { Part: not CrossfadePart.Inside } crossfade)
        {
            switch (crossfade.Part)
            {
                case CrossfadePart.Remove:
                    SelectedCrossfade = null;
                    Raise(() => crossfade.Block.CrossfadeEdit?.Invoke(0));
                    return;
                case CrossfadePart.Edge when crossfade.Block.CrossfadeEdit is not null && CaptureMouse():
                    _crossfadeDrag = (row, crossfade.Block, crossfade.Block.CrossfadeIn);
                    BeginDrag();
                    return;
                case CrossfadePart.Inside:
                    SelectedCrossfade = crossfade.Block.Clip;
                    Invalidate();
                    return;
            }
        }

        if (SelectedCrossfade is not null)
        {
            SelectedCrossfade = null;
            Invalidate();
        }

        // Block switches (On/Off, child Lock) on full-size lanes: single click each.
        if (BlockSwitchAt(lane, laid) is { } pressedSwitch)
        {
            Raise(() => pressedSwitch.Block.Switched?.Invoke(pressedSwitch.Which));
            return;
        }

        if (RevealAt(lane, laid) is >= 0 and var reveal && !OnFadeHandle(index, laid))
        {
            var mark = row.Reveals[reveal];
            Raise(() => RevealHideRequested?.Invoke(row, mark));
            return;
        }

        if (SeamAt(lane, laid) is >= 0 and var seam && !OnFadeHandle(index, laid))
        {
            var mark = row.Seams[seam];
            Raise(() => SeamClicked?.Invoke(row, mark));
            return;
        }

        if (GapAt(lane, laid) is >= 0 and var gap && PixelsPerSecond > 0)
        {
            var (left, right) = row.Gaps[gap];
            var scale = PixelsPerSecond;
            Raise(() => CloseGapRequested?.Invoke(row, left / scale, right / scale));
            return;
        }

        if (BlockAt(index, laid) is not { } block)
        {
            // Click on a collapsed lane expands it; other clicks on empty space seek.
            if (lane.Mini && lane.Parent >= 0)
                OpenStacksAbove(index);
            else
                PressEmpty(row, point, e);
            return;
        }

        var pressed = block;
        PressRipples = RipplesAt(block, laid.X, Keyboard.Modifiers);
        var told = Raise(() => BlockPressed?.Invoke(row, pressed, e));
        PressRipples = false;
        if (!told)
            return;
        var (found, again) = Refind(index, row, block);
        if (again is null)
            return;

        (index, block) = (found, again);
        lane = _lanes[index];
        row = lane.Row;

        // Until BeginDrag the press is a pending click, raised on a no-move release (also for non-draggable blocks and collapsed lanes).
        _clickOnly = (row, block, laid);

        // Collapsed lanes are not editable: a click expands the stack.
        if (!lane.Detailed)
        {
            if (lane.Mini && lane.Parent >= 0)
                OpenStacksAbove(index);
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var rect = BlockRect(lane, block, 0);

        // Alt on the lower part of an edge of a clip with separable audio: J/L cut.
        if (modifiers.HasFlag(ModifierKeys.Alt) && SplitEdgeAt(block, rect, laid) is { } atLead && CaptureMouse())
        {
            var (lead, tail) = block.AudioSplit ?? (0.0, 0.0);
            _splitDrag = new SplitDrag { Row = row, Block = block, AtLead = atLead, PointerX = laid.X, StartLead = lead, StartTail = tail, Lead = lead, Tail = tail };
            BeginDrag();
            return;
        }

        // Fade handle (top of block): drag sets fade length, snapping to other clips' edges on any track and the playhead (so a fade
        // can end exactly where an overlapping clip starts or ends).
        if (FadeHandleAt(lane, block, laid) is { } fadeIn && block.Fades is { } fades && CaptureMouse())
        {
            _fadeDrag = (row, block, fadeIn, fades);
            BeginDrag();
            return;
        }

        // Gain line on an audible clip: vertical drag.
        if (OnGainLine(lane, block, laid) && block.Gain is { } gain && CaptureMouse())
        {
            _gainDrag = (row, block, laid, gain);
            BeginDrag();
            return;
        }

        if (block is { CanDrag: { } canDrag, Commit: not null })
        {
            if (!canDrag())
            {
                var why = block.CannotDrag;
                Raise(() => DragRefused?.Invoke(why));
            }
            else if (CaptureMouse())
            {
                _drag = StartDrag(row, block, laid, modifiers);
                _drag.Keys = modifiers;
                _lastPointer = laid;
                Settle.Begin(point, Now());
                BeginDrag();
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Alt) && block.Edit is not null)
        {
            _clickOnly = null;
            var cut = block;
            Raise(() => RazorRequested?.Invoke(row, cut, TimeAt(point.X)));
        }
    }

    // ----- Rubber-band selection (press on empty space still seeks) -----

    // Held empty-space press that may become a band (row, layout point, Ctrl/Shift), and the band rect (layout pixels) once past the
    // system drag distance.
    private (TimelineRow? Row, Point From, bool Add)? _marqueePress;
    private (Point From, Point To, bool Add)? _marquee;

    /// <summary>True while a rubber band is active (empty-space press held past the drag distance).</summary>
    internal bool IsBandDrawn => _marquee is not null;

    /// <summary>
    /// Empty-space press: seeks immediately; if held past the system drag distance it also becomes a rubber band that selects touched
    /// clips on release (playhead stays at the press). Double-click, or a host without band support, only seeks.
    /// </summary>
    private void PressEmpty(TimelineRow? row, Point point, MouseButtonEventArgs e)
    {
        var mayBand = e.ClickCount == 1 && MarqueeSelected is not null;
        PressEmptyAt(row, point, e, Keyboard.Modifiers, mayBand && CaptureMouse());

        // The seek handler lost mouse capture (another window activated): no band.
        if (_marqueePress is not null && !IsMouseCaptured)
            _marqueePress = null;
    }

    /// <summary>
    /// Testable empty-space press at a control point: raises the seek now; with <paramref name="held"/> (capture taken) it awaits
    /// <see cref="MoveEmptyPress"/> or <see cref="ReleaseEmptyPress"/>.
    /// </summary>
    internal void PressEmptyAt(TimelineRow? row, Point point, MouseButtonEventArgs e, ModifierKeys modifiers, bool held)
    {
        (_marqueePress, _marquee) = (null, null);
        Raise(() => EmptyPressed?.Invoke(row, TimeAt(point.X), e));
        if (held)
            _marqueePress = (row, Laid(point), modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>Testable move for a held empty-space press (starts the band past the drag distance); false if no such press.</summary>
    internal bool MoveEmptyPress(Point point) => FollowMarquee(Laid(point));

    /// <summary>Testable release: raises the band's selection (nothing if no band formed); false if no such press.</summary>
    internal bool ReleaseEmptyPress() => EndMarquee();

    private static Rect MarqueeBox((Point From, Point To, bool Add) band) => new(band.From, band.To);

    /// <summary>Blocks intersecting a band (layout pixels) on full-size lanes, top to bottom.</summary>
    private List<TimelineBlock> BlocksIn(Rect box)
    {
        var found = new List<TimelineBlock>();
        foreach (var lane in _lanes)
        {
            if (!lane.Detailed || lane.Height < 1 || lane.Bottom < box.Top || lane.Top > box.Bottom)
                continue;

            foreach (var block in lane.Row.Blocks)
            {
                if (BlockRect(lane, block, 0).IntersectsWith(box) && !found.Contains(block))
                    found.Add(block);
            }
        }

        return found;
    }

    /// <summary>
    /// Updates the band once the pointer exceeds SystemParameters' minimum drag distances (~4 px, so click jitter draws none); true while
    /// an empty-space press is held.
    /// </summary>
    private bool FollowMarquee(Point laid)
    {
        if (_marqueePress is not { } press)
            return false;

        if (_marquee is null && (Math.Abs(laid.X - press.From.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(laid.Y - press.From.Y) > SystemParameters.MinimumVerticalDragDistance))
            _marquee = (press.From, laid, press.Add);
        if (_marquee is { } band)
        {
            _marquee = band with { To = laid };
            _overlayDirty = true;
            InvalidateVisual();
        }

        return true;
    }

    /// <summary>Ends an empty-space press, raising the band's blocks (the seek was raised on press); false if none.</summary>
    private bool EndMarquee()
    {
        if (_marqueePress is null)
            return false;

        var band = _marquee;
        (_marqueePress, _marquee) = (null, null);
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (band is { } drawn)
        {
            _overlayDirty = true;
            InvalidateVisual();
            var touched = BlocksIn(MarqueeBox(drawn));
            Raise(() => MarqueeSelected?.Invoke(touched, drawn.Add));
        }

        return true;
    }

    // Block switch hit test on full-size lanes; null for none.
    private static (TimelineBlock Block, BlockSwitch Which)? BlockSwitchAt(in Lane lane, Point at)
    {
        if (!lane.Detailed)
            return null;

        foreach (var block in lane.Row.Blocks)
        {
            if (block.Switched is null)
                continue;

            var (onOff, lockBox) = BlockSwitchBoxes(block, BlockRect(lane, block, 0));
            if (!onOff.IsEmpty && Rect.Inflate(onOff, 1, 1).Contains(at))
                return (block, BlockSwitch.OnOff);
            if (!lockBox.IsEmpty && Rect.Inflate(lockBox, 1, 1).Contains(at))
                return (block, BlockSwitch.Lock);
        }

        return null;
    }

    private enum CrossfadePart { Inside, Edge, Remove }

    // Crossfade box hit test (part included); full-size lanes only; null for none.
    private (TimelineBlock Block, CrossfadePart Part)? CrossfadeAt(in Lane lane, Point at)
    {
        var scale = PixelsPerSecond;
        if (!lane.Detailed || scale <= 0)
            return null;

        foreach (var block in lane.Row.Blocks)
        {
            if (block.CrossfadeIn <= 0.01)
                continue;

            var box = CrossfadeBox(lane, block, scale, 0);
            if (!Rect.Inflate(box, 3, 0).Contains(at))
                continue;
            if (CrossfadeRemove(box) is { IsEmpty: false } remove && Rect.Inflate(remove, 1, 1).Contains(at))
                return (block, CrossfadePart.Remove);
            return (block, Math.Min(Math.Abs(at.X - box.Left), Math.Abs(at.X - box.Right)) <= 4 ? CrossfadePart.Edge : CrossfadePart.Inside);
        }

        return null;
    }

    // Crossfade edge drag: row, block and starting length; the box resizes symmetrically about its cut.
    private (TimelineRow Row, TimelineBlock Block, double Start)? _crossfadeDrag;

    /// <summary>Clip whose crossfade is selected (box clicked); Delete removes the crossfade.</summary>
    public object? SelectedCrossfade { get; set; }

    private void FollowCrossfade((TimelineRow Row, TimelineBlock Block, double Start) drag, Point laid)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0)
            return;

        drag.Block.CrossfadeIn = Math.Max(2 * Math.Abs(laid.X - drag.Block.Left) / scale, 0.04);
        Invalidate();
    }

    // Pending click: pressed block, row and layout point; a no-move release raises BlockClicked.
    private (TimelineRow Row, TimelineBlock Block, Point At)? _clickOnly;

    /// <summary>True when a press at layout x with these modifiers ripples: Ctrl on an edge StartDrag would trim.</summary>
    private static bool RipplesAt(TimelineBlock block, double x, ModifierKeys modifiers)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control) || block is not { Edit: not null, CanDrag: not null, Commit: not null })
            return false;

        var (left, right, edge) = (block.Left, block.Left + block.Width, TimelineMetrics.EdgePixels);
        return block.Resizable && x > right - edge || block.TrimmableStart && x < left + edge;
    }

    // Collapsed-lane click: expand its stack and every collapsed ancestor stack so the lane becomes full size.
    private void OpenStacksAbove(int index)
    {
        var closed = new List<(TimelineRow Row, ChildStack Stack)>();
        for (var at = index; at >= 0 && _lanes[at].Mini && _lanes[at].Parent >= 0; at = _lanes[at].Parent)
            closed.Add((_lanes[_lanes[at].Parent].Row, _lanes[at].Row.Stack));

        foreach (var (row, stack) in closed)
            SetExpanded(row, stack, true);

        // Keep the clicked lane in view while stacks grow around it (e.g. Background Blur, last in its stack, would be pushed off-screen).
        if (closed.Count > 0)
            (_keepInView, _keepInViewUntil) = (_lanes[index].Row, System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
    }

    // Lane kept in view during OpenStacksAbove growth, and the deadline (Stopwatch ticks).
    private TimelineRow? _keepInView;
    private long _keepInViewUntil;

    /// <summary>
    /// Re-resolves the pressed block after BlockPressed (the host may rebuild rows, e.g. on selection): matches by family key, else Clip.
    /// </summary>
    private (int Lane, TimelineBlock? Block) Refind(int lane, TimelineRow row, TimelineBlock block)
    {
        if (lane < _lanes.Count && ReferenceEquals(_lanes[lane].Row, row) && row.Blocks.Contains(block))
            return (lane, block);
        if (block.Clip is null && block.Family is null)
            return (-1, null);

        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < _lanes.Count; i++)
            {
                if (pass == 0 && !Equals(_lanes[i].Row.Key, row.Key))
                    continue;

                foreach (var other in _lanes[i].Row.Blocks)
                {
                    if (SameThing(other, block))
                        return (i, other);
                }
            }
        }

        return (-1, null);
    }

    // Block identity across rebuilds: same family key, else same Clip.
    private static bool SameThing(TimelineBlock a, TimelineBlock b) =>
        a.Family is { } first && b.Family is { } second ? Equals(first.Key, second.Key) : a.Clip is not null && ReferenceEquals(a.Clip, b.Clip);

    private void HeaderPressed(int index, Point at, MouseButtonEventArgs e)
    {
        var lane = _lanes[index];
        var row = lane.Row;

        // Every chevron click toggles (rapid clicks are not swallowed as double-clicks), reversing mid-animation if needed.
        if (ChevronAt(lane, at) is { } stack)
        {
            // Collapsed (thin bars) or open: stacks are never hidden, except HidesWhenClosed effect lanes, which take 0 height collapsed.
            SetExpanded(row, stack, !IsExpanded(row, stack));
        }
        else if (SwitchAt(lane, at) is { } flag)
        {
            var alone = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            Raise(() => HeaderSwitchToggled?.Invoke(row, flag, alone));

            // The host updates the row's switch; the tooltip refreshes on the next move.
            ForgetTip();
            _headersDirty = true;
            InvalidateVisual();
        }
        else if (BadgeAt(lane, at))
        {
            // Preset badge: the host opens the preset list anchored to it.
            var badge = BadgeRect(lane, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            badge.Offset(0, -_verticalOffset);
            ForgetTip();
            Raise(() => BadgePressed?.Invoke(row, badge));
        }
        else if (e.ClickCount % 2 == 0 && row.Children.Count > 0)
        {
            // Double-click on the name toggles all its stacks (every second click in a rapid run).
            SetExpanded(row, !IsExpanded(row));
        }
        else
        {
            // Name click: the host may select the row's item (captions, background). A draggable track header is also held as a pending
            // drag that starts past the system drag distance.
            Raise(() => HeaderClicked?.Invoke(row));
            if (e.ClickCount == 1 && !lane.Mini && ArmRowDrag(row, at, Now()) && !CaptureMouse())
                CancelRowDrag();
        }
    }

    /// <summary>
    /// Chooses the drag mode for a block press: right edge = end trim, left edge = start trim, shared cut = roll, Alt inside = slip, else
    /// move; Ctrl on an edge ripples. Shift during any drag disables snapping and steps whole frames (<see cref="Snap(double)"/>), except on
    /// a move that left its lane (other row, "+ New Track" seam or tether, beyond the 6 DIP deadzone), where Shift keeps the exact time.
    /// Alt pressed mid-move (not at press) tethers to the clip under the pointer, also while resting (the dwell timer reads modifiers).
    /// </summary>
    private BlockDrag StartDrag(TimelineRow row, TimelineBlock block, Point point, ModifierKeys modifiers)
    {
        var ripple = modifiers.HasFlag(ModifierKeys.Control) && block.Edit is not null;
        var (left, right) = (block.Left, block.Left + block.Width);
        var edge = TimelineMetrics.EdgePixels;

        // Dead-air seam edge (never a roll): outward unfolds (limited by the fold and the media), inward folds more; later content
        // slides. The pointer's side of the cut picks which clip's edge.
        if (!ripple && block is { SeamRange: { } seamRange, SeamTrim: not null } && !modifiers.HasFlag(ModifierKeys.Alt))
        {
            var atStart = point.X < left + edge && !(point.X > right - edge && point.X - left > right - point.X);
            var cut = atStart ? left : right;
            (double Reveal, double Fold)? asked = null;
            if ((atStart || point.X > right - edge) && SeamNear(row, cut) is { } mark && Raise(() => asked = seamRange(atStart)) && asked is { } range)
            {
                return new BlockDrag
                {
                    Row = row, Block = block, Kind = atStart ? BlockEdit.TrimStart : BlockEdit.TrimEnd, PointerX = point.X, Left = left, Width = block.Width,
                    Seam = new SeamGrip
                    {
                        Mark = mark, AtStart = atStart, Reveal = Math.Max(range.Reveal, 0), Fold = Math.Max(range.Fold, 0), Cut = cut,
                        Rows = mark.Rows is null ? null : new HashSet<object>(mark.Rows),
                    },
                };
            }
        }

        // Shared cut with the next block: a roll can start from either side.
        if (!ripple && block.Edit is not null && block.TrimmableStart)
        {
            var (before, after) = point.X > right - edge ? (block, NextTo(row, block, after: true))
                : point.X < left + edge ? (NextTo(row, block, after: false), block) : (null, null);
            if (before is { Edit: not null, TrimmableStart: true } && after is { Edit: not null, TrimmableStart: true })
            {
                return new BlockDrag
                {
                    Row = row, Block = before, Kind = BlockEdit.Roll, PointerX = point.X, Left = before.Left, Width = before.Width,
                    Neighbor = after, NeighborLeft = after.Left, NeighborWidth = after.Width,
                };
            }
        }

        var kind = block.Resizable && point.X > right - edge ? BlockEdit.TrimEnd
            : block.TrimmableStart && block.Edit is not null && point.X < left + edge ? BlockEdit.TrimStart
            : block.Slippable && block.Edit is not null && modifiers.HasFlag(ModifierKeys.Alt) ? BlockEdit.Slip
            : BlockEdit.Move;
        return new BlockDrag
        {
            Row = row, Block = block, Kind = kind, PointerX = point.X, PointerY = point.Y, Left = left, Width = block.Width,
            Ripple = ripple && kind is BlockEdit.TrimEnd or BlockEdit.TrimStart,
            Alt = modifiers.HasFlag(ModifierKeys.Alt) && kind is BlockEdit.Move or BlockEdit.Slip,
        };
    }

    /// <summary>Adjacent block on the same row whose edge meets this one's end or start within 1 px; null if none.</summary>
    private static TimelineBlock? NextTo(TimelineRow row, TimelineBlock block, bool after)
    {
        foreach (var other in row.Blocks)
        {
            if (ReferenceEquals(other, block) || Math.Abs(other.Top - block.Top) > 0.5)
                continue;
            if (after ? Math.Abs(other.Left - (block.Left + block.Width)) < 1 : Math.Abs(other.Left + other.Width - block.Left) < 1)
                return other;
        }

        return null;
    }

    /// <summary>Dead-air seam within 1 layout pixel of a cut on a row; null if none.</summary>
    private SeamMark? SeamNear(TimelineRow row, double x)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0)
            return null;

        foreach (var seam in row.Seams)
        {
            if (Math.Abs(seam.Seconds * scale - x) <= 1)
                return seam;
        }

        return null;
    }

    // Drag start: query snap points (excluding carried children's edges and, for move/slip, carried clips' beats, which would snap
    // to themselves), clear hover and skim line, and redraw rows without the dragged content (the overlay draws it).
    private void BeginDrag()
    {
        _clickOnly = null;
        if (_drag is { } drag)
            CollectFamily(drag);

        IReadOnlyList<double> times = [];
        var moving = SoundTakenAlong(_drag);
        Raise(() => times = SnapTimes?.Invoke(moving) ?? []);
        _snapTimes = _drag is { } dragged ? WithoutFamily(times, dragged) : times;

        ToolTip = null;
        ForgetTip();
        Skim(double.NaN);
        _hovered = null;
        _hoveredGap = null;
        _contentDirty = _labelsDirty = _overlayDirty = true;
        InvalidateVisual();
    }

    private void EndDrag()
    {
        LeaveOutside();
        StopSettleTimer();
        _snapTimes = [];
        _coupled.Clear();
        _coupledSet.Clear();
        _loose.Clear();
        _groupSet.Clear();
        Invalidate();

        // Released outside: clear hover state.
        if (!new Rect(RenderSize).Contains(Mouse.GetPosition(this)))
            PointerSeconds(double.NaN);
        Raise(() => DragEnded?.Invoke());
    }

    /// <summary>
    /// Cancels any active drag: reverts drawn state, releases capture, commits nothing, and raises DragEnded. No-op when idle.
    /// </summary>
    public void AbortDrag()
    {
        _clickOnly = null;

        // Pending (unstarted) header drag: release capture; nothing to revert.
        if (_rowDrag is { Started: false })
        {
            CancelRowDrag();
            if (IsMouseCaptured)
                ReleaseMouseCapture();
        }

        if (!IsDragging)
            return;

        // Revert before releasing capture so LostMouseCapture finds no drag to revert twice.
        Revert();
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    // ----- Release -----

    // Each drag's state is cleared before raising its commit (so the host never sees it active). A host fault reverts drawing and is
    // reported (CallbackFailed), never rethrown. DragEnded is always raised so the host rebuilds rows whether the edit succeeded,
    // was refused or failed.
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_heightDrag is { } heightDrag)
        {
            _heightDrag = null;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            e.Handled = true;
            if (heightDrag.Row is { } sized)
                Raise(() => RowHeightDragEnded?.Invoke(sized, true));
            return;
        }

        // Header release: reorder, nest, or (never started) the click already raised.
        if (EndRowDrag())
        {
            e.Handled = true;
            return;
        }

        if (EndMarquee())
        {
            e.Handled = true;
            return;
        }

        _autoScroll.Stop();
        if (_splitDrag is { } split)
        {
            _splitDrag = null;
            ReleaseMouseCapture();
            e.Handled = true;
            try
            {
                if ((split.Lead != split.StartLead || split.Tail != split.StartTail) && split.Block.AudioSplitEdit is { } splitEdit)
                    Raise(() => splitEdit(split.Lead, split.Tail));
            }
            finally
            {
                EndDrag();
            }

            return;
        }

        if (_crossfadeDrag is { } crossfadeDrag)
        {
            _crossfadeDrag = null;
            ReleaseMouseCapture();
            e.Handled = true;
            var done = true;
            try
            {
                if (crossfadeDrag.Block.CrossfadeIn != crossfadeDrag.Start && crossfadeDrag.Block.CrossfadeEdit is { } crossfadeEdit)
                    done = Raise(() => crossfadeEdit(crossfadeDrag.Block.CrossfadeIn));
            }
            finally
            {
                if (!done)
                    crossfadeDrag.Block.CrossfadeIn = crossfadeDrag.Start;
                EndDrag();
            }

            return;
        }

        if (_fadeDrag is { } fadeDrag)
        {
            _fadeDrag = null;
            ReleaseMouseCapture();
            e.Handled = true;
            var done = true;
            try
            {
                if (fadeDrag.Block.Fades is { } fades && fades != fadeDrag.Start && fadeDrag.Block.FadeEdit is { } fadeEdit)
                    done = Raise(() => fadeEdit(fadeDrag.In, fadeDrag.In ? fades.In : fades.Out));
            }
            finally
            {
                if (!done)
                    PutBackFade(fadeDrag);
                EndDrag();
            }

            return;
        }

        if (_gainDrag is { } gainDrag)
        {
            _gainDrag = null;
            ReleaseMouseCapture();
            e.Handled = true;
            var done = true;
            try
            {
                if (gainDrag.Block.Gain is { } gain && gain != gainDrag.Start && gainDrag.Block.GainEdit is { } gainEdit)
                    done = Raise(() => gainEdit(new GainEditDone(gain.GainDb, gain.FadeIn, gain.FadeOut)));
            }
            finally
            {
                if (!done)
                    gainDrag.Block.Gain = gainDrag.Start;
                EndDrag();
            }

            return;
        }

        if (_drag is not { } drag)
        {
            // Pressed block without a drag, released in place: raise as a click.
            if (_clickOnly is { } click)
            {
                _clickOnly = null;
                var at = Laid(e.GetPosition(this));
                if (Math.Abs(at.X - click.At.X) < RazorTravel && Math.Abs(at.Y - click.At.Y) < RazorTravel)
                    Raise(() => BlockClicked?.Invoke(click.Row, click.Block));
            }

            base.OnMouseLeftButtonUp(e);
            return;
        }

        // Released outside over an external target (Clip Library): the host takes it; nothing is committed here.
        if (TryDropOutside(drag, e.GetPosition(this)))
        {
            e.Handled = true;
            return;
        }

        _drag = null;
        ReleaseMouseCapture();
        e.Handled = true;
        CommitDrag(drag);
    }

    /// <summary>
    /// Commits a block drag on release (from <see cref="OnMouseLeftButtonUp"/> or tests via <see cref="ReleaseBlockDrag"/>), or reverts
    /// it. Returns false when refused.
    /// </summary>
    private bool CommitDrag(BlockDrag drag)
    {
        var block = drag.Block;
        var made = true;
        try
        {
            // Alt no-move click: cut at that point (block reverts).
            if (drag.Alt && drag.Travel < RazorTravel && drag.Target is null && drag.NewTrack is null && drag.Attach is null)
            {
                PutBack(drag);
                var scale = PixelsPerSecond;
                var at = scale > 0 ? drag.PointerX / scale : _viewStart;
                Raise(() => RazorRequested?.Invoke(drag.Row, block, at));
                return true;
            }

            // Seam edge: unfold or fold by the travelled amount.
            if (drag.Seam is { } grip)
            {
                if (Math.Abs(grip.Seconds) > 1e-4 && block.SeamTrim is { } seamTrim)
                    made = Raise(() => seamTrim(grip.AtStart, grip.Seconds));
                return made;
            }

            var changed = Math.Abs(block.Left - drag.Left) > 0.5 || Math.Abs(block.Width - drag.Width) > 0.5;

            // Group: all members moved/trimmed as one step (all-or-nothing; reverted on refusal).
            if (drag.Group is { Count: > 0 } group)
            {
                if (changed)
                    made = CommitGroup(drag, group);
            }

            // Tether: becomes the target's child at its time (reverted on refusal).
            else if (drag.Attach is { } attach && block.AttachTo is { } attachTo)
            {
                var at = block.Left;
                made = drag.FamilyFits && Raise(() => attachTo(attach.Block, at));
            }

            // New-track seam: move onto a new track there (reverted on refusal).
            else if (drag.NewTrack is { } seam && block.MoveToNewTrack is { } moveNew)
            {
                var at = block.Left;
                made = drag.NewTrackFits && drag.FamilyFits && Raise(() => moveNew(seam.Beside.Data, seam.Above, at));
            }

            // Another row: move there (reverted on refusal).
            else if (drag.Target is { } target && block.MoveTo is { } moveTo)
            {
                var at = block.Left;
                made = Raise(() => moveTo(target.Data, at));
            }

            // Start trim, roll, slip or ripple: delegated to the block's callback.
            else if (drag.Kind is BlockEdit.TrimStart or BlockEdit.Roll or BlockEdit.Slip || drag.Ripple)
            {
                if ((changed || Math.Abs(drag.Slip) > 0.5) && block.Edit is { } edit)
                {
                    var done = new BlockEditDone(drag.Kind, block.Left, block.Width, drag.Slip, drag.Ripple, drag.Neighbor);
                    made = Raise(() => edit(done));
                }
            }

            // No movement: no change.
            else if (changed && block.Commit is { } commit)
            {
                var (left, width) = (block.Left, block.Width);
                made = Raise(() => commit(left, width));
            }

            // Press + release in place: raise a click (host selection logic).
            if (!changed && drag.Travel < RazorTravel && Math.Abs(drag.Slip) <= 0.5 && drag.NewTrack is null && drag.Attach is null)
            {
                var row = drag.Row;
                Raise(() => BlockClicked?.Invoke(row, block));
            }
        }
        finally
        {
            if (!made)
                PutBack(drag);
            EndDrag();
        }

        return made;
    }

    /// <summary>
    /// Testable release of a drag started by <see cref="PressBlockAt"/>: <see cref="CommitDrag"/>, or true when released outside onto a
    /// host-accepted target. False with no drag or on refusal.
    /// </summary>
    internal bool ReleaseBlockDrag()
    {
        if (_drag is not { } drag)
            return false;
        if (TryDropOutside(drag, _outsideAt))
            return true;
        _drag = null;
        return CommitDrag(drag);
    }

    /// <summary>Current new-track seam of the drag: row key and above flag; null for none.</summary>
    internal (object Key, bool Above)? NewTrackSeam => _drag?.NewTrack is { } seam ? (seam.Beside.Key, seam.Above) : null;

    /// <summary>
    /// Commits a group drag via <see cref="Together"/> (one Undo step, all-or-nothing). Moves apply from the leading end of travel so no
    /// member lands on one not yet moved. Returns false when refused or failed.
    /// </summary>
    private bool CommitGroup(BlockDrag drag, List<Coupled> group)
    {
        var edits = new List<(double Left0, Action Edit)>(group.Count + 1);
        if (!drag.CarriedByGroup && EditOf(drag.Block) is { } own)
            edits.Add((drag.Left, own));
        foreach (var member in group)
        {
            if (EditOf(member.Block) is { } edit)
                edits.Add((member.Left0, edit));
        }

        if (edits.Count == 0)
            return true;

        if (drag.Kind == BlockEdit.Move)
            edits.Sort((a, b) => drag.Block.Left >= drag.Left ? b.Left0.CompareTo(a.Left0) : a.Left0.CompareTo(b.Left0));

        var count = group.Count + 1;
        var label = drag.Kind switch
        {
            BlockEdit.TrimEnd => string.Create(CultureInfo.InvariantCulture, $"trim the ends of {count} clips"),
            BlockEdit.TrimStart => string.Create(CultureInfo.InvariantCulture, $"trim the starts of {count} clips"),
            _ => string.Create(CultureInfo.InvariantCulture, $"move {count} clips"),
        };

        void Body()
        {
            foreach (var (_, edit) in edits)
                edit();
        }

        var made = true;
        return Raise(() => made = Together is { } together ? together(label, Body) : RunAll(Body)) && made;

        static bool RunAll(Action body)
        {
            body();
            return true;
        }

        // Per-member edit derived from its current drawn position.
        Action? EditOf(TimelineBlock block)
        {
            var (left, width) = (block.Left, block.Width);
            if (drag.Kind == BlockEdit.TrimStart)
                return block.Edit is { } edit ? () => edit(new BlockEditDone(BlockEdit.TrimStart, left, width, 0, false, null)) : null;
            return block.Commit is { } commit ? () => commit(left, width) : null;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture lost mid middle-drag: not a flick, no coast (a normal release clears capture after handling).
        if (_panFrom is not null)
            _momentumX?.Stop();
        _panFrom = null;
        _clickOnly = null;
        if (_marqueePress is not null)
        {
            // Capture lost mid-band: discard the band without selecting.
            (_marqueePress, _marquee) = (null, null);
            _overlayDirty = true;
            InvalidateVisual();
        }

        if (_heightDrag is { Row: { } sized })
            Raise(() => RowHeightDragEnded?.Invoke(sized, false));
        _heightDrag = null;

        // Unstarted header drag: nothing to revert; a started one reverts like any drag (Revert).
        if (_rowDrag is { Started: false })
            CancelRowDrag();
        if (IsDragging)
            Revert();
    }

    // Capture lost mid-drag (window activation, rows replaced, host cancel): revert and commit nothing.
    private void Revert()
    {
        _autoScroll.Stop();
        if (_crossfadeDrag is { } crossfadeDrag)
            crossfadeDrag.Block.CrossfadeIn = crossfadeDrag.Start;
        else if (_fadeDrag is { } fadeDrag)
            PutBackFade(fadeDrag);
        else if (_gainDrag is { } gainDrag)
            gainDrag.Block.Gain = gainDrag.Start;
        else if (_drag is { } drag)
            PutBack(drag);

        _drag = null;
        _fadeDrag = null;
        _gainDrag = null;
        _splitDrag = null;
        _crossfadeDrag = null;

        // Header drag: nothing moved yet, only clear its indicators.
        if (_rowDrag is not null)
            CancelRowDrag();
        EndDrag();
    }

    // Restores the dragged block, a roll's partner block, and carried blocks to their drag-start positions.
    private void PutBack(BlockDrag drag)
    {
        (drag.Block.Left, drag.Block.Width) = (drag.Left, drag.Width);
        if (drag.Neighbor is { } next)
            (next.Left, next.Width) = (drag.NeighborLeft, drag.NeighborWidth);
        foreach (var child in _coupled)
            (child.Block.Left, child.Block.Width) = (child.Left0, child.Width0);
        if (drag.Seam is { } grip)
            grip.Seconds = 0;
    }

    private static void PutBackFade((TimelineRow Row, TimelineBlock Block, bool In, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) Start) drag)
    {
        drag.Block.Fades = drag.Start;
        if (drag.Block.Gain is { } gain)
            drag.Block.Gain = gain with { FadeIn = drag.Start.In, FadeOut = drag.Start.Out };
    }

    // ----- Menus -----

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        // As with the left button: flush pending rebuilds so the menu targets the current timeline.
        if (!IsDragging)
            Raise(() => PressStarting?.Invoke());
        FinishSlide();

        // Right-click selects the block so the menu targets it; on a fade handle the menu is the fade's.
        var point = e.GetPosition(this);
        var laid = Laid(point);
        var index = LaneAt(laid.Y);
        var onHeader = point.X < _headerWidth;
        TimelineRow? row = index >= 0 ? _lanes[index].Row : null;
        TimelineBlock? block = null;
        bool? fadeIn = null;
        if (!onHeader && row is not null && BlockAt(index, laid) is { } pressed)
        {
            if (_lanes[index].Detailed)
                fadeIn = FadeHandleAt(_lanes[index], pressed, laid);
            var on = row;
            Raise(() => BlockPressed?.Invoke(on, pressed, e));
            (index, block) = Refind(index, row, pressed);
            row = index >= 0 ? _lanes[index].Row : row;
        }

        ArmMenu(row, block, onHeader, fadeIn, point);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_menuPress is not { } press)
            return;

        var cancelled = _menuCancelled;
        DisarmMenu();
        e.Handled = true;
        if (cancelled || (e.GetPosition(this) - press.At).Length > 6)
            return;

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        if (Raise(() => MenuRequested?.Invoke(new TimelineMenuRequest(press.Row, press.Block, press.OnHeader, press.FadeIn, menu))) && menu.Items.Count > 0)
            menu.IsOpen = true;
    }

    /// <summary>
    /// Keyboard context menu (Menu key, Shift+F10; the surface never takes focus) for the selected block, at its visible top-left as a
    /// right-click would. Returns false when no selected block is on a full-size lane or its menu is empty.
    /// </summary>
    public bool OpenSelectedMenu()
    {
        if (!IsVisible || IsDragging || IsSelected is not { } isSelected)
            return false;

        foreach (var lane in _lanes)
        {
            if (!lane.Detailed)
                continue;

            foreach (var block in lane.Row.Blocks)
            {
                if (!isSelected(block.Clip))
                    continue;

                // Block position in control space (layout minus view offset), clamped to the time area.
                var rect = BlockRect(lane, block, ViewOffsetX - _headerWidth);
                var x = Math.Clamp(rect.Left + 4, _headerWidth, Math.Max(ActualWidth - 4, _headerWidth));
                var y = Math.Clamp(rect.Top - _verticalOffset + 4, 0, Math.Max(ActualHeight - 4, 0));
                var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Relative, HorizontalOffset = x, VerticalOffset = y };
                var (row, pressed) = (lane.Row, block);
                if (!Raise(() => MenuRequested?.Invoke(new TimelineMenuRequest(row, pressed, false, null, menu))) || menu.Items.Count == 0)
                    return false;

                menu.IsOpen = true;
                return true;
            }
        }

        return false;
    }

    // Right-click start: the menu opens on release unless a key is pressed meanwhile.
    private void ArmMenu(TimelineRow? row, TimelineBlock? block, bool onHeader, bool? fadeIn, Point at)
    {
        DisarmMenu();
        _menuPress = (row, block, onHeader, fadeIn, at);
        _menuCancelled = false;
        _menuWindow = Window.GetWindow(this);
        if (_menuWindow is not null)
            _menuWindow.PreviewKeyDown += MenuWindow_PreviewKeyDown;
    }

    private void DisarmMenu()
    {
        if (_menuWindow is not null)
            _menuWindow.PreviewKeyDown -= MenuWindow_PreviewKeyDown;
        _menuWindow = null;
        _menuPress = null;
    }

    private void MenuWindow_PreviewKeyDown(object sender, KeyEventArgs e) => _menuCancelled = true;
}
