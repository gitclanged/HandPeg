using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// The unified timeline's mouse: everything the old per-track bar did (selecting, seeking beside the blocks, moving a block
// along its row or onto another, trimming either end, rippling with Ctrl, rolling a shared cut, slipping with Alt, dragging
// fade handles and gain lines, snapping, Shift for frame steps without snapping, auto-scroll near the ends, putting a drag
// back when the mouse is taken away, cursors and tooltips), plus what 4.4 adds: the headers' switches and chevrons, the J/L
// cut (Alt on the lower half of an edge), Alt + click to cut, the seams and gap pills, the skim line and the menus; and what
// 5.0 adds: a group of selected blocks dragged (moved, or trimmed at either end) as one, the captions under a clip following
// it as it is dragged, clicks told apart from presses, and the moment under the pointer for Instant Seek's hints.
public sealed partial class UnifiedTimelineControl
{
    // How close to the gain line the pointer has to be to take hold of it.
    private const double GainReach = 4;

    // How close to a fade handle the pointer has to be, along the block; and how deep the band along a block's top is where
    // its fade handles are (below it, its edges trim).
    private const double FadeReach = 6;
    private const double FadeBand = 10;

    // How close to an end of the time area a drag has to come to move the view along by itself.
    private const double AutoScrollZone = 28;

    // How wide a gap has to be (pixels) to show the pill that closes it.
    private const double GapLeast = 28;

    // How far a plain wheel notch scrolls the rows.
    private const double WheelStep = 48;

    // An Alt-press on a block that moves less than this (pixels) before it is let go is a click: a cut there.
    private const double RazorTravel = 2;

    // A block being dragged: its row, what the drag does, where the pointer and the block (and for a roll the next block)
    // were, whether Alt was held as it began (a click that does not move is then a cut), how far the pointer has gone,
    // and the other row it is over (and whether it may go there).
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

        /// <summary>A slip so far, in pixels: what of the file shows has moved with the pointer by this much.</summary>
        public double Slip { get; set; }

        public double Travel { get; set; }
        public TimelineRow? Target { get; set; }
        public bool Fits { get; set; }

        /// <summary>An edge dragged at a seam of folded dead air (4.4 BugHunt): it shows the fold again or folds more into it, the later blocks sliding.</summary>
        public SeamGrip? Seam { get; init; }

        /// <summary>Whether what goes with the block (its children) can go where it is drawn: a child would start before the timeline's start otherwise.</summary>
        public bool FamilyFits { get; set; } = true;

        /// <summary>The other selected blocks dragged with it (5.0, a group): moved, or trimmed at the same end, by as much. Null for none.</summary>
        public List<Coupled>? Group { get; set; }

        /// <summary>A group moved: the lowest the block's left edge may go, so that none of the group passes the timeline's start.</summary>
        public double GroupFloor { get; set; }

        /// <summary>The block itself rides with another block of its group (its parent's move or trim takes it): only the group's other edits are made.</summary>
        public bool CarriedByGroup { get; set; }

        /// <summary>Where the pointer was pressed, down the rows (laid out): a move goes for a new track's seam only once it has gone far enough from it.</summary>
        public double PointerY { get; init; }

        /// <summary>
        /// The seam a move is over (5.4A): a new track goes in above or below the top-level picture row <c>Beside</c>, the insertion bar
        /// drawn at <c>Y</c> (laid out). Null while it is over none, or the host refuses it there.
        /// </summary>
        public (TimelineRow Beside, bool Above, double Y)? NewTrack { get; set; }

        /// <summary>Whether the block may go onto the new track at <see cref="NewTrack"/> (the host's CanMoveToNewTrack, and its children within the timeline).</summary>
        public bool NewTrackFits { get; set; }
    }

    // An edge pressed at a seam of folded dead air (4.4 BugHunt, Q11): the seam, which edge (true: the later clip's start,
    // the fold's tail; false: the earlier clip's end, its head), how far it may go outward (Reveal) and inward (Fold) in
    // seconds, where the cut is (laid-out pixels), how far outward it has gone (seconds, negative inward), and the rows that
    // slide with it (null: every row that is not locked).
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

    // What a dragged block carries along (4.4 BugHunt): a child block (the clips linked to it on other rows, collapsed or
    // open; its unlinked sound; its file's sound on its Audio Sub-Lanes), drawn on the overlay as the parent is dragged. Moved by the
    // parent's travel, or with the parent's start or end edge (a trim, a roll, a seam), or left where it is and shown to come
    // loose (Detaching: a trim leaves it wholly outside the parent, which unlinks it). Where it was when the drag began.
    // 5.0: a caption under a clip goes with it too (moved with it; trimmed, cut to it as it is now: Within; slipped, slid
    // with what of its file it plays and cut to it: SlipWithin), and so do the other blocks of a selected group (Member),
    // moved or trimmed at the same end. Root is the block whose edges a child is held to (its parent, its clip, itself).
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

    // The other blocks of the selected group being dragged (5.0): never gathered as anyone's child.
    private readonly HashSet<TimelineBlock> _groupSet = new(ReferenceEqualityComparer.Instance);

    // A J/L cut being dragged: which edge of the sound (true: where it starts), where the pointer was, and the cut's lead-in
    // and tail-out when it began and now (seconds).
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

    // A row's lower edge dragged (5.1-Polish): where the pointer was pressed, and how many rows grow with the setting from the
    // top down to that edge (the move is shared among them, so the edge follows the pointer). With a row (5.1-AutoHud-Part1,
    // the user's Q2): that row alone, from the height it had.
    private (double FromY, int Rows, TimelineRow? Row, double FromHeight)? _heightDrag;

    // How near (DIPs) a row's lower edge in the headers is taken for the edge, and how many notches of the wheel are one step.
    private const double HeightEdgeReach = 3;

    // A gain line being dragged: the row, the block, the pointer and the values where it started.
    private (TimelineRow Row, TimelineBlock Block, Point From, (double GainDb, double FadeIn, double FadeOut) Start)? _gainDrag;

    // A fade handle being dragged: the row, the block, which fade (true: in), and the block's fades when the drag began.
    private (TimelineRow Row, TimelineBlock Block, bool In, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) Start)? _fadeDrag;

    // The block the pointer is over: its fade handles show at its corners while it is.
    private (TimelineRow Row, TimelineBlock Block)? _hovered;

    // The gap the pointer is in (its row, and an index into the row's gaps): only its pill shows, and only it is clicked.
    private (TimelineRow Row, int Index)? _hoveredGap;

    // The last pointer during a drag, in laid-out pixels: what an auto-scroll tick drags on with.
    private Point _lastPointer;
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    // A drag with the middle button: where the pointer was.
    private double? _panFrom;

    // Momentum (5.4C): along the view (Shift + wheel, the sideways wheel, a middle-button flick) and down the rows (the plain wheel),
    // made at their first use, so nothing of it exists before Frame 1; how far the middle-button drag has panned since its press.
    private MomentumScroller? _momentumX, _momentumY;
    private double _panTravel;

    private MomentumScroller MomentumX => _momentumX ??= new MomentumScroller();

    private MomentumScroller MomentumY => _momentumY ??= new MomentumScroller();

    private IReadOnlyList<double> _snapTimes = [];
    private double _skimSeconds = double.NaN;

    // The moment under the pointer last told (PointerSecondsChanged), NaN for none.
    private double _pointerSeconds = double.NaN;

    // What the tooltip is up for (a row or a block, and which part of it), so it is set once per thing, not on every move.
    private object? _tipTarget;
    private int _tipPart = -1;

    // A right-click under way: what it was on and where; whether a key went down meanwhile (the button was held while
    // W, A, S or D steered the timeline), which opens no menu; and the window whose keys are listened to meanwhile.
    private (TimelineRow? Row, TimelineBlock? Block, bool OnHeader, bool? FadeIn, Point At)? _menuPress;
    private bool _menuCancelled;
    private Window? _menuWindow;

    /// <summary>The right-click under way opens no menu when its button is let go (the button was held for something else).</summary>
    public void CancelPendingMenu() => _menuCancelled = true;

    private TimelineBlock? DraggedBlock => _drag?.Block ?? _fadeDrag?.Block ?? _gainDrag?.Block ?? _splitDrag?.Block;

    private bool IsDragged(TimelineBlock block) => _drag is { } drag
        ? ReferenceEquals(block, drag.Block) || ReferenceEquals(block, drag.Neighbor) || (_coupledSet.Count > 0 && _coupledSet.Contains(block))
        : ReferenceEquals(block, _fadeDrag?.Block) || ReferenceEquals(block, _gainDrag?.Block) || ReferenceEquals(block, _splitDrag?.Block);

    // A point on the control as a point on the laid-out rows: x as the blocks are laid out, y from the top of all the rows.
    private Point Laid(Point point) => new(point.X - _headerWidth + ViewOffsetX, point.Y + _verticalOffset);

    /// <summary>
    /// Raises something to the host (an event, a block's callback) so that a fault in what the host does for it never
    /// reaches the dispatcher (4.4 BugHunt): it goes to <see cref="CallbackFailed"/> (or the log, with nobody listening).
    /// False when it failed.
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

    /// <summary>A coast's pan step raised as <see cref="Raise"/> does, with nothing made for it (5.4C review: it runs every frame).</summary>
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

    // What the host did for a raise threw: told to CallbackFailed, else written to the log. Always false.
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

    // ----- Pressing -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _clickOnly = null;

        // The rows the host still owes (an edit just made) are laid out first, and a fold still sliding is where it was
        // going: what is found under the pointer is the timeline as it is now, never as it was before the last edit.
        if (!IsDragging)
            Raise(() => PressStarting?.Invoke());
        FinishSlide();

        var point = e.GetPosition(this);
        var laid = Laid(point);
        var index = LaneAt(laid.Y);
        if (point.X < _headerWidth)
        {
            // A row's lower edge (5.1-AutoHud-Part1, the user's Q2): that track's height, dragged; double-clicked, back to the
            // one Track Height. With Shift, or on a row that is no track's: the one Track Height itself (5.1-Polish).
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

        // A crossfade's box: its button takes it away and its edges size it. The rest of it is the clips' (5.2, the user's call: a
        // crossfade never stands between a click and its clips or their effects), so a press there falls through to them.
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

        // A block's own switches (5.2): its On/Off and a child's Lock, one click each, on a lane drawn in full.
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
            // A click on a lane of a collapsed stack opens it; any other click beside the blocks seeks.
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

        // Until a drag starts (BeginDrag), the press is a click: let go where it was, it is told as one (5.0-Verify), for a
        // block that is not dragged too (one that cannot be, a lane drawn small).
        _clickOnly = (row, block, laid);

        // A lane drawn small is not edited on: a click on a lane of a collapsed stack opens the stack.
        if (!lane.Detailed)
        {
            if (lane.Mini && lane.Parent >= 0)
                OpenStacksAbove(index);
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var rect = BlockRect(lane, block, 0);

        // Alt on the lower part of an edge of a clip whose sound can be split from its picture: the J/L cut.
        if (modifiers.HasFlag(ModifierKeys.Alt) && SplitEdgeAt(block, rect, laid) is { } atLead && CaptureMouse())
        {
            var (lead, tail) = block.AudioSplit ?? (0.0, 0.0);
            _splitDrag = new SplitDrag { Row = row, Block = block, AtLead = atLead, PointerX = laid.X, StartLead = lead, StartTail = tail, Lead = lead, Tail = tail };
            BeginDrag();
            return;
        }

        // A fade handle, at the top of the block: dragged along, its fade's length (snapping to other clips' edges, on any
        // track, and the playhead: where a clip under or over this one starts or ends is where a crossfade with it ends).
        if (FadeHandleAt(lane, block, laid) is { } fadeIn && block.Fades is { } fades && CaptureMouse())
        {
            _fadeDrag = (row, block, fadeIn, fades);
            BeginDrag();
            return;
        }

        // The gain line, on a clip with sound: dragged up and down.
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
                _lastPointer = laid;
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

    // ----- A rubber band over empty space (5.2A; the seek on the press again, 5.2B) -----

    // A press on empty space still held, that may yet become a rubber band (its row, where it was in laid-out pixels, whether Ctrl or Shift
    // was held), and the band once the pointer has moved past the system's drag distance from it (laid-out pixels).
    private (TimelineRow? Row, Point From, bool Add)? _marqueePress;
    private (Point From, Point To, bool Add)? _marquee;

    /// <summary>A rubber band is being drawn: the pointer, still held after a press on empty space, has gone past the drag distance.</summary>
    internal bool IsBandDrawn => _marquee is not null;

    /// <summary>
    /// A press on empty space (beside the blocks, below the rows): the seek it always was, told at once (5.2B, the user's Q3). Held and
    /// dragged past the system's drag distance it becomes a rubber band too (5.2A), which selects the clips it touches as it is let go and
    /// leaves the playhead where the press put it. A double-click, or a host that takes no band, is the seek alone.
    /// </summary>
    private void PressEmpty(TimelineRow? row, Point point, MouseButtonEventArgs e)
    {
        var mayBand = e.ClickCount == 1 && MarqueeSelected is not null;
        PressEmptyAt(row, point, e, Keyboard.Modifiers, mayBand && CaptureMouse());

        // What the host did for the seek took the mouse away (a window came forward): no band can follow it.
        if (_marqueePress is not null && !IsMouseCaptured)
            _marqueePress = null;
    }

    /// <summary>
    /// What a press on empty space does, at a point on the control (its own coordinates): the seek is told now; <paramref name="held"/> (the
    /// mouse was taken for a band that may follow) waits for the pointer to move (<see cref="MoveEmptyPress"/>) or be let go
    /// (<see cref="ReleaseEmptyPress"/>). Its own method so a test can drive it without a mouse.
    /// </summary>
    internal void PressEmptyAt(TimelineRow? row, Point point, MouseButtonEventArgs e, ModifierKeys modifiers, bool held)
    {
        (_marqueePress, _marquee) = (null, null);
        Raise(() => EmptyPressed?.Invoke(row, TimeAt(point.X), e));
        if (held)
            _marqueePress = (row, Laid(point), modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>The pointer, still held after a press on empty space, at a point on the control: the band, once it has gone far enough. False with no such press.</summary>
    internal bool MoveEmptyPress(Point point) => FollowMarquee(Laid(point));

    /// <summary>The press on empty space let go: the clips the band touched are told (a press that drew none has done all it does). False with no such press.</summary>
    internal bool ReleaseEmptyPress() => EndMarquee();

    private static Rect MarqueeBox((Point From, Point To, bool Add) band) => new(band.From, band.To);

    /// <summary>The blocks a band touches (laid-out pixels), top to bottom, on lanes drawn in full.</summary>
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
    /// The band follows the pointer once it has moved past the system's drag distance from the press (SystemParameters' minimum horizontal
    /// and vertical drag distances, about 4 px: a hand that shakes as it clicks draws none); true while a press on empty space is held.
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

    /// <summary>Lets go of a press on empty space: the band's blocks told (the press's seek was told as it went down); false when there was none.</summary>
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

    // The block switch a point is on (5.2), on a lane drawn in full; null for none.
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

    // The crossfade box a point is in, and which part of it; null for none. A crossfade is drawn on a lane in full only.
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

    // A crossfade's edge dragged: its row, its block and how long it was. The box grows from its cut, both ways alike.
    private (TimelineRow Row, TimelineBlock Block, double Start)? _crossfadeDrag;

    /// <summary>The clip whose crossfade is selected (a click on its box): Delete takes it away.</summary>
    public object? SelectedCrossfade { get; set; }

    private void FollowCrossfade((TimelineRow Row, TimelineBlock Block, double Start) drag, Point laid)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0)
            return;

        drag.Block.CrossfadeIn = Math.Max(2 * Math.Abs(laid.X - drag.Block.Left) / scale, 0.04);
        Invalidate();
    }

    // A block pressed that started no drag, its row and where it was pressed (laid out): let go there, a click (BlockClicked).
    private (TimelineRow Row, TimelineBlock Block, Point At)? _clickOnly;

    /// <summary>Whether a press at a laid-out x, with these keys, ripples the block's edge: Ctrl on an edge that StartDrag trims there.</summary>
    private static bool RipplesAt(TimelineBlock block, double x, ModifierKeys modifiers)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control) || block is not { Edit: not null, CanDrag: not null, Commit: not null })
            return false;

        var (left, right, edge) = (block.Left, block.Left + block.Width, TimelineMetrics.EdgePixels);
        return block.Resizable && x > right - edge || block.TrimmableStart && x < left + edge;
    }

    // A click on a lane of a collapsed stack: its stack opens, and every closed stack above it, so the lane is shown in full.
    private void OpenStacksAbove(int index)
    {
        var closed = new List<(TimelineRow Row, ChildStack Stack)>();
        for (var at = index; at >= 0 && _lanes[at].Mini && _lanes[at].Parent >= 0; at = _lanes[at].Parent)
            closed.Add((_lanes[_lanes[at].Parent].Row, _lanes[at].Row.Stack));

        foreach (var (row, stack) in closed)
            SetExpanded(row, stack, true);

        // The lane clicked stays in view while the stacks open round it (5.0-Release: the Background Blur, last of its stack,
        // was pushed below the bottom by the rows opening above it, and its selection could not be seen).
        if (closed.Count > 0)
            (_keepInView, _keepInViewUntil) = (_lanes[index].Row, System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
    }

    // A lane kept in view while the stacks opened by a click on it grow (OpenStacksAbove), until when (Stopwatch ticks).
    private TimelineRow? _keepInView;
    private long _keepInViewUntil;

    /// <summary>
    /// The block pressed, found again after its press was told: the host may have made the rows again for it (a selection
    /// draws them again), and the block that stands for the same clip (by its family's key, else its Clip) is then the one
    /// to drag.
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

    // Whether two blocks (one of rows made again) stand for the same thing: the same family key, else the same Clip.
    private static bool SameThing(TimelineBlock a, TimelineBlock b) =>
        a.Family is { } first && b.Family is { } second ? Equals(first.Key, second.Key) : a.Clip is not null && ReferenceEquals(a.Clip, b.Clip);

    private void HeaderPressed(int index, Point at, MouseButtonEventArgs e)
    {
        var lane = _lanes[index];
        var row = lane.Row;

        // Every click on a chevron turns its stack (4.4 BugHunt: a quick second click is never taken for half a double
        // click and dropped), from where it is if it is still moving.
        if (ChevronAt(lane, at) is { } stack)
        {
            // Its bars or open: a stack is never hidden (the user's directive), except its effect lanes, which take no room while it is
            // closed (5.3, TimelineRow.HidesWhenClosed).
            SetExpanded(row, stack, !IsExpanded(row, stack));
        }
        else if (SwitchAt(lane, at) is { } flag)
        {
            var alone = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            Raise(() => HeaderSwitchToggled?.Invoke(row, flag, alone));

            // The host sets the switch on the row; its tooltip says so when the pointer next moves.
            ForgetTip();
            _headersDirty = true;
            InvalidateVisual();
        }
        else if (BadgeAt(lane, at))
        {
            // Its preset badge (5.2A): the host drops the presets from it, at its place on the control.
            var badge = BadgeRect(lane, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            badge.Offset(0, -_verticalOffset);
            ForgetTip();
            Raise(() => BadgePressed?.Invoke(row, badge));
        }
        else if (e.ClickCount % 2 == 0 && row.Children.Count > 0)
        {
            // A double click on the name opens or closes all of its stacks (every second click of a quick run of them).
            SetExpanded(row, !IsExpanded(row));
        }
        else
        {
            // Its name: the host may select what the row stands for (5.0-Verify: the captions, the background).
            Raise(() => HeaderClicked?.Invoke(row));
        }
    }

    /// <summary>
    /// What a press on a block starts: on its right edge a trim of its end, on its left edge a trim of its start, on the
    /// cut where it meets the next block a roll of both, Alt inside it a slip, anywhere else a move. Ctrl on an edge
    /// ripples (the later clips follow its end). Shift, held at any time during the drag, snaps to nothing and steps by
    /// whole frames (see <see cref="Snap"/>).
    /// </summary>
    private BlockDrag StartDrag(TimelineRow row, TimelineBlock block, Point point, ModifierKeys modifiers)
    {
        var ripple = modifiers.HasFlag(ModifierKeys.Control) && block.Edit is not null;
        var (left, right) = (block.Left, block.Left + block.Width);
        var edge = TimelineMetrics.EdgePixels;

        // An edge at a seam of folded dead air (4.4 BugHunt, Q11): never a roll there. Dragged outward it shows the fold
        // again (as much as the fold, and the clip's file, have), inward it folds that much more of the clip into it; what
        // follows slides either way. The side of the cut the pointer is on says which clip's edge it is.
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

        // The cut between this block and the next, when they meet: the block on either side of it can be pressed.
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

    /// <summary>The block whose edge meets this one's (its end, or its start), on its row; null when there is none within a pixel.</summary>
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

    /// <summary>The seam of a row at a cut (laid-out pixels), within a pixel of it; null for none.</summary>
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

    // A drag began: the snapping moments are asked for (less where what the dragged block carries along is: it would snap
    // to its own children; and, 5.2B, less the beats of what a move or a slip takes along: they would catch on where they
    // were), the hover and the skim line go, and the rows are drawn again without what is dragged (the overlay draws it as
    // it goes).
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
        _snapTimes = [];
        _coupled.Clear();
        _coupledSet.Clear();
        _loose.Clear();
        _groupSet.Clear();
        Invalidate();

        // Let go with the pointer outside: nothing is under it any more.
        if (!new Rect(RenderSize).Contains(Mouse.GetPosition(this)))
            PointerSeconds(double.NaN);
        Raise(() => DragEnded?.Invoke());
    }

    /// <summary>
    /// Lets go of a drag in progress (4.4 BugHunt): what it drew moved is put back, the mouse is let go, and nothing is
    /// committed (DragEnded is raised, as for any drag's end). Nothing happens when nothing is being dragged.
    /// </summary>
    public void AbortDrag()
    {
        _clickOnly = null;
        if (!IsDragging)
            return;

        // Put back first: losing the capture then finds no drag to put back again.
        Revert();
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    // ----- What a dragged block carries along (4.4 BugHunt) -----

    /// <summary>
    /// Finds what goes with a dragged block, on every lane (open or collapsed): moved, the children that ride with it
    /// (and theirs); a trim (or a seam's edge), the children on that edge of it, through an unbroken line of children on
    /// that edge, and the children the trim may leave outside it; a roll, the earlier clip's on its end and the later's on
    /// its start; a slip, none. 5.0: the other blocks of a selected group the block is one of (moved, or trimmed at the same
    /// end) with what each of them carries along, and the captions under every clip dragged (see <see cref="Captions"/>).
    /// </summary>
    private void CollectFamily(BlockDrag drag)
    {
        _coupled.Clear();
        _coupledSet.Clear();
        _loose.Clear();
        _groupSet.Clear();
        (drag.Group, drag.CarriedByGroup) = (null, false);

        // The blocks of every lane by the parent they belong to (the dragged block and its roll's neighbour among them: a
        // group's other block may be the parent of either).
        var byParent = new Dictionary<object, List<(TimelineRow Row, TimelineBlock Block)>>();
        foreach (var lane in _lanes)
        {
            foreach (var block in lane.Row.Blocks)
            {
                if (block.Family is not { Parent: { } parent })
                    continue;
                if (!byParent.TryGetValue(parent, out var children))
                    byParent[parent] = children = [];
                children.Add((lane.Row, block));
            }
        }

        var members = GroupOf(drag, byParent);
        if (members.Count > 0)
        {
            var how = drag.Kind switch { BlockEdit.TrimEnd => Follows.End, BlockEdit.TrimStart => Follows.Start, _ => Follows.Move };
            var lowest = drag.Block.MayStartBeforeZero ? double.PositiveInfinity : drag.Left;
            drag.Group = new List<Coupled>(members.Count);
            foreach (var (row, member) in members)
            {
                var entry = new Coupled { Row = row, Block = member, How = how, Root = member, Member = true, Left0 = member.Left, Width0 = member.Width };
                drag.Group.Add(entry);
                _coupled.Add(entry);
                _coupledSet.Add(member);
                _groupSet.Add(member);
                if (!member.MayStartBeforeZero)
                    lowest = Math.Min(lowest, member.Left);
            }

            // Moved as one, the group stops where the first of it would pass the timeline's start.
            drag.GroupFloor = double.IsPositiveInfinity(lowest) ? double.NegativeInfinity : drag.Left - lowest;
        }

        if (drag.Kind != BlockEdit.Slip && byParent.Count > 0)
        {
            switch (drag.Kind)
            {
                case BlockEdit.Move:
                    Gather(drag.Block, null, Follows.Move, false);
                    break;
                case BlockEdit.TrimEnd:
                    Gather(drag.Block, BlockEdges.End, drag.Seam is null ? Follows.End : Follows.SeamEdge, false);
                    break;
                case BlockEdit.TrimStart:
                    Gather(drag.Block, BlockEdges.Start, drag.Seam is null ? Follows.Start : Follows.SeamEdge, false);
                    break;
                case BlockEdit.Roll:
                    Gather(drag.Block, BlockEdges.End, Follows.End, false);
                    if (drag.Neighbor is { } next)
                        Gather(next, BlockEdges.Start, Follows.Start, true);
                    break;
            }

            // What each other block of the group carries along goes with it the same way.
            foreach (var member in drag.Group ?? [])
            {
                Gather(member.Block, drag.Kind switch { BlockEdit.TrimEnd => BlockEdges.End, BlockEdit.TrimStart => BlockEdges.Start, _ => null },
                    member.How, false);
            }
        }

        // The captions under every clip dragged (5.0): never at a seam of folded dead air (what slides there is the fold's).
        if (drag.Seam is null)
        {
            var how = drag.Kind switch { BlockEdit.Move => Follows.Move, BlockEdit.Slip => Follows.SlipWithin, _ => Follows.Within };
            Captions(drag.Row, drag.Block, how);
            if (drag.Neighbor is { } next)
                Captions(drag.Row, next, Follows.Within);
            foreach (var member in drag.Group ?? [])
                Captions(member.Row, member.Block, how);
        }

        // Every child (and theirs, the children on the same edge) that rides with the block: on that edge (a trim), or
        // all of them (a move). A trim also shows the children it may leave outside the parent, which come loose. The
        // dragged block, its neighbour and the group's own blocks are never anyone's children here: each goes by itself.
        void Gather(TimelineBlock root, BlockEdges? edge, Follows how, bool ofNeighbor)
        {
            var queue = new Queue<TimelineBlock>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                if (parent.Family is not { } family || !byParent.TryGetValue(family.Key, out var children))
                    continue;

                foreach (var (row, child) in children)
                {
                    if (child.Family is not { Rides: true } childFamily || _coupledSet.Contains(child) || ReferenceEquals(child, drag.Block)
                        || ReferenceEquals(child, drag.Neighbor) || _groupSet.Contains(child))
                    {
                        continue;
                    }

                    var onEdge = edge is { } wanted && (childFamily.OnParentEdges & wanted) != 0;
                    if (edge is not null && !onEdge)
                    {
                        // Off the dragged edge: only the root's own children can be left outside by a trim, and come loose.
                        if (ReferenceEquals(parent, root) && drag.Seam is null && drag.Kind is BlockEdit.TrimEnd or BlockEdit.TrimStart)
                            Add(row, child, how, ofNeighbor, mayDetach: true, root);
                        continue;
                    }

                    Add(row, child, how, ofNeighbor, mayDetach: false, root);
                    queue.Enqueue(child);
                }
            }
        }

        void Add(TimelineRow row, TimelineBlock child, Follows how, bool ofNeighbor, bool mayDetach, TimelineBlock root)
        {
            _coupled.Add(new Coupled
            {
                Row = row, Block = child, How = mayDetach ? Follows.Move : how, OfNeighbor = ofNeighbor, Left0 = child.Left, Width0 = child.Width,
                Detaching = false, Root = root,
            });
            _coupledSet.Add(child);
            if (mayDetach)
                _loose.Add(child);
        }
    }

    /// <summary>
    /// The other selected blocks a drag of a selected block takes along (5.0, multi-select): a move, or a plain trim of either
    /// end (no ripple, roll, slip or seam), of one of them is made to every one that can take it (moved: it can be dragged;
    /// trimmed: it has that edge). One the model moves or trims with another of them (a child riding with its parent, on
    /// the edge trimmed) is left to it; when the dragged block itself is such a child, its own edit is left out.
    /// </summary>
    private List<(TimelineRow Row, TimelineBlock Block)> GroupOf(BlockDrag drag, Dictionary<object, List<(TimelineRow Row, TimelineBlock Block)>> byParent)
    {
        var found = new List<(TimelineRow Row, TimelineBlock Block)>();
        if (drag.Kind is not (BlockEdit.Move or BlockEdit.TrimEnd or BlockEdit.TrimStart) || drag.Ripple || drag.Seam is not null || drag.Alt
            || IsSelected is not { } isSelected || drag.Block.Clip is not { } pressed)
        {
            return found;
        }

        var selected = false;
        Raise(() => selected = isSelected(pressed));
        if (!selected)
            return found;

        foreach (var lane in _lanes)
        {
            foreach (var block in lane.Row.Blocks)
            {
                if (ReferenceEquals(block, drag.Block) || block.Clip is not { } clip || !Takes(block))
                    continue;

                var chosen = false;
                if (Raise(() => chosen = isSelected(clip)) && chosen)
                    found.Add((lane.Row, block));
            }
        }

        if (found.Count == 0)
            return found;

        // What the model takes along with each of them (and with the dragged block): never edited twice.
        var edge = drag.Kind switch { BlockEdit.TrimEnd => BlockEdges.End, BlockEdit.TrimStart => BlockEdges.Start, _ => BlockEdges.None };
        var carried = new HashSet<TimelineBlock>(ReferenceEqualityComparer.Instance);
        Reach(drag.Block);
        foreach (var (_, member) in found)
            Reach(member);
        found.RemoveAll(m => carried.Contains(m.Block));
        drag.CarriedByGroup = carried.Contains(drag.Block);
        return found;

        bool Takes(TimelineBlock block) => drag.Kind switch
        {
            BlockEdit.Move => block.Commit is not null && block.CanDrag is { } can && CanDragNow(can),
            BlockEdit.TrimEnd => block.Resizable && block.Commit is not null && block.CanDrag is not null,
            _ => block.TrimmableStart && block.Edit is not null && block.CanDrag is not null,
        };

        bool CanDragNow(Func<bool> can)
        {
            var yes = false;
            return Raise(() => yes = can()) && yes;
        }

        void Reach(TimelineBlock root)
        {
            var queue = new Queue<TimelineBlock>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                if (parent.Family is not { } family || !byParent.TryGetValue(family.Key, out var children))
                    continue;

                foreach (var (_, child) in children)
                {
                    if (child.Family is not { Rides: true } childFamily || (edge != BlockEdges.None && (childFamily.OnParentEdges & edge) == 0) || !carried.Add(child))
                        continue;
                    queue.Enqueue(child);
                }
            }
        }
    }

    /// <summary>
    /// The captions under a clip's row (5.0, the user's directive 2): the blocks of a captions lane in the row's own stack
    /// (the captions are anchored to the sound of its track) that lie within the clip as the drag began. They go with it
    /// every move, as its HUD pieces do: moved by as much; trimmed or rolled, cut to the clip as it is now (what its new
    /// edge leaves outside it is not shown); slipped, slid with what of its file it plays.
    /// </summary>
    private void Captions(TimelineRow row, TimelineBlock clip, Follows how)
    {
        var (left, right) = (clip.Left, clip.Left + clip.Width);
        foreach (var lane in _lanes)
        {
            if (lane.Row.Kind != TimelineRowKind.Captions || !ReferenceEquals(lane.Row.Parent, row))
                continue;

            foreach (var caption in lane.Row.Blocks)
            {
                if (_coupledSet.Contains(caption) || Math.Min(caption.Left + caption.Width, right) - Math.Max(caption.Left, left) < 0.5)
                    continue;

                _coupled.Add(new Coupled { Row = lane.Row, Block = caption, How = how, Root = clip, Left0 = caption.Left, Width0 = caption.Width });
                _coupledSet.Add(caption);
            }
        }
    }

    // The children a trim may leave outside their parent (they stay where they are, and are shown coming loose once it does).
    private readonly HashSet<TimelineBlock> _loose = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The blocks whose sound a drag takes along with it (5.2B, Snap to Audio Beats): a move's (the dragged block, the rest of its group and
    /// what rides with them, as <see cref="CollectFamily"/> found them) and a slip's (what of its file the block plays slides under it). Their
    /// clips' beats move with the drag, so they are no moments to snap to. A trim, a roll or a seam's edge leaves every beat where it is (what
    /// of the file plays moves with the edge): none then, and a clip's own beats stay for its edges to catch on.
    /// </summary>
    private List<TimelineBlock> SoundTakenAlong(BlockDrag? drag)
    {
        var moving = new List<TimelineBlock>();
        if (drag is not { Kind: BlockEdit.Move or BlockEdit.Slip })
            return moving;

        moving.Add(drag.Block);
        if (drag.Kind == BlockEdit.Move)
            moving.AddRange(_coupled.Select(c => c.Block));
        return moving;
    }

    /// <summary>The snapping moments less the edges of what the drag carries along (each its own child's), but never the dragged block's own.</summary>
    private IReadOnlyList<double> WithoutFamily(IReadOnlyList<double> times, BlockDrag drag)
    {
        var scale = PixelsPerSecond;
        if (_coupled.Count == 0 || times.Count == 0 || scale <= 0)
            return times;

        var own = new List<double> { drag.Left, drag.Left + drag.Width };
        if (drag.Neighbor is not null)
            own.AddRange([drag.NeighborLeft, drag.NeighborLeft + drag.NeighborWidth]);

        var kept = new List<double>(times.Count);
        foreach (var time in times)
        {
            var x = time * scale;
            if (own.Exists(edge => Math.Abs(edge - x) <= 0.5) || !_coupled.Exists(c => Math.Abs(c.Left0 - x) <= 0.5 || Math.Abs(c.Left0 + c.Width0 - x) <= 0.5))
                kept.Add(time);
        }

        return kept;
    }

    /// <summary>
    /// Lays out what the dragged block carries along for where it is now: moved by as much as it moved, or with its dragged
    /// edge (the edge's own children trimmed with it); a child the trim leaves wholly outside the parent comes loose (it
    /// stays where it is, drawn coming away). A child that would start before the timeline's start makes the move refused.
    /// </summary>
    private void FollowFamily(BlockDrag drag)
    {
        if (_coupled.Count == 0)
            return;

        var block = drag.Block;
        var (startMoved, endMoved) = (block.Left - drag.Left, block.Left + block.Width - (drag.Left + drag.Width));
        var cutMoved = drag.Neighbor is { } next ? next.Left - drag.NeighborLeft : 0;
        var seamGrowth = drag.Seam is { } grip ? grip.Seconds * PixelsPerSecond : 0;
        var fits = true;
        foreach (var child in _coupled)
        {
            var (left, width) = (child.Left0, child.Width0);
            var root = child.Root ?? block;
            switch (child.How)
            {
                // A caption: where it was (slid with its clip's file, slipped), cut to its clip as it is now; nothing of it
                // when it lies outside.
                case Follows.Within or Follows.SlipWithin:
                {
                    var from = child.Left0 + (child.How == Follows.SlipWithin ? drag.Slip : 0);
                    var (cutLeft, cutRight) = (Math.Max(from, root.Left), Math.Min(from + child.Width0, root.Left + root.Width));
                    (child.Block.Left, child.Block.Width) = (cutLeft, Math.Max(cutRight - cutLeft, 0));
                    continue;
                }

                case Follows.Move when drag.Kind == BlockEdit.Move:
                    left += startMoved;
                    if (!child.Block.MayStartBeforeZero && left < -0.5)
                        fits = false;
                    break;
                case Follows.End:
                    width = child.Width0 + (child.OfNeighbor ? 0 : endMoved);
                    break;
                case Follows.Start:
                {
                    var moved = child.OfNeighbor ? cutMoved : startMoved;
                    (left, width) = (child.Left0 + moved, child.Width0 - moved);
                    break;
                }

                case Follows.SeamEdge:
                    width = child.Width0 + seamGrowth;
                    break;
            }

            // One of a selected group: never loose; trimmed no shorter than a block is drawn, nor before the timeline's start.
            if (child.Member)
            {
                if (left < 0 && !child.Block.MayStartBeforeZero && drag.Kind == BlockEdit.TrimStart)
                    (left, width) = (0, width + left);
                (child.Block.Left, child.Block.Width) = (left, Math.Max(width, 4));
                continue;
            }

            // Trimmed past: left wholly outside its parent, it comes loose and stays where it was.
            var (parentLeft, parentRight) = (root.Left, root.Left + root.Width);
            var loose = _loose.Contains(child.Block)
                ? drag.Kind == BlockEdit.TrimEnd && child.Left0 >= parentRight - 0.5 || drag.Kind == BlockEdit.TrimStart && child.Left0 + child.Width0 <= parentLeft + 0.5
                : width < 1;
            child.Detaching = loose && drag.Seam is null;
            (child.Block.Left, child.Block.Width) = loose ? (child.Left0, child.Width0) : (left, Math.Max(width, 4));
        }

        drag.FamilyFits = fits;
    }

    // ----- Letting go -----

    // Each drag is let go of first (so nothing the host does finds it still held), then what it does is raised to the host
    // (4.4 BugHunt): a fault there puts back what was drawn moved and is told (CallbackFailed), never thrown on; the drag's
    // end is always told, so the host lays the rows out again from what the timeline is now, whether the edit was made,
    // refused (the model says why) or failed.
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
            // A block pressed that started no drag, let go where it was: a click all the same (5.0-Verify).
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

        _drag = null;
        ReleaseMouseCapture();
        e.Handled = true;
        CommitDrag(drag);
    }

    /// <summary>
    /// A dragged block let go (the release of <see cref="OnMouseLeftButtonUp"/>, its own method so a test can drive it without a mouse,
    /// <see cref="ReleaseBlockDrag"/>): what the drag did is made, or the block is laid back where it was. False when it was refused.
    /// </summary>
    private bool CommitDrag(BlockDrag drag)
    {
        var block = drag.Block;
        var made = true;
        try
        {
            // Alt and a click that did not move: a cut there (the block goes back to where it was).
            if (drag.Alt && drag.Travel < RazorTravel && drag.Target is null && drag.NewTrack is null)
            {
                PutBack(drag);
                var scale = PixelsPerSecond;
                var at = scale > 0 ? drag.PointerX / scale : _viewStart;
                Raise(() => RazorRequested?.Invoke(drag.Row, block, at));
                return true;
            }

            // An edge at a seam: the fold shown again, or more folded into it, by as much as it went.
            if (drag.Seam is { } grip)
            {
                if (Math.Abs(grip.Seconds) > 1e-4 && block.SeamTrim is { } seamTrim)
                    made = Raise(() => seamTrim(grip.AtStart, grip.Seconds));
                return made;
            }

            var changed = Math.Abs(block.Left - drag.Left) > 0.5 || Math.Abs(block.Width - drag.Width) > 0.5;

            // A selected group (5.0): every one of its blocks moved or trimmed as one step (refused as one, laid back).
            if (drag.Group is { Count: > 0 } group)
            {
                if (changed)
                    made = CommitGroup(drag, group);
            }

            // Let go at a seam (5.4A): onto a new track there (or, refused, laid back where it was).
            else if (drag.NewTrack is { } seam && block.MoveToNewTrack is { } moveNew)
            {
                var at = block.Left;
                made = drag.NewTrackFits && drag.FamilyFits && Raise(() => moveNew(seam.Beside.Data, seam.Above, at));
            }

            // Let go on another row: it goes there (or, refused, is laid back where it was).
            else if (drag.Target is { } target && block.MoveTo is { } moveTo)
            {
                var at = block.Left;
                made = Raise(() => moveTo(target.Data, at));
            }

            // A trim of the start, a roll, a slip or a ripple: the block says what that does.
            else if (drag.Kind is BlockEdit.TrimStart or BlockEdit.Roll or BlockEdit.Slip || drag.Ripple)
            {
                if ((changed || Math.Abs(drag.Slip) > 0.5) && block.Edit is { } edit)
                {
                    var done = new BlockEditDone(drag.Kind, block.Left, block.Width, drag.Slip, drag.Ripple, drag.Neighbor);
                    made = Raise(() => edit(done));
                }
            }

            // A click that did not move the block changes nothing.
            else if (changed && block.Commit is { } commit)
            {
                var (left, width) = (block.Left, block.Width);
                made = Raise(() => commit(left, width));
            }

            // Pressed and let go where it was: a click, which the host may make something of (5.0: the selection).
            if (!changed && drag.Travel < RazorTravel && Math.Abs(drag.Slip) <= 0.5 && drag.NewTrack is null)
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

    /// <summary>A block pressed at a point on the control (its own coordinates), its drag begun as a press with the mouse begins it: for a test, without a mouse (5.4A).</summary>
    internal void PressBlockAt(TimelineRow row, TimelineBlock block, Point point, ModifierKeys modifiers)
    {
        var laid = Laid(point);
        _drag = StartDrag(row, block, laid, modifiers);
        _lastPointer = laid;
        BeginDrag();
    }

    /// <summary>The pointer of a block's drag at a point on the control (<see cref="PressBlockAt"/>). False with no drag.</summary>
    internal bool MoveBlockDrag(Point point)
    {
        if (_drag is not { } drag)
            return false;
        _lastPointer = Laid(point);
        Follow(drag, _lastPointer, pointerMoved: true);
        return true;
    }

    /// <summary>A block's drag let go (<see cref="PressBlockAt"/>): what <see cref="CommitDrag"/> makes of it. False with no drag, or when it was refused.</summary>
    internal bool ReleaseBlockDrag()
    {
        if (_drag is not { } drag)
            return false;
        _drag = null;
        return CommitDrag(drag);
    }

    /// <summary>The seam a block's drag is over now (5.4A): the key of the row the new track goes beside and whether above it; null for none.</summary>
    internal (object Key, bool Above)? NewTrackSeam => _drag?.NewTrack is { } seam ? (seam.Beside.Key, seam.Above) : null;

    /// <summary>
    /// A selected group let go (5.0): the dragged block's edit and each other block's, made as one through
    /// <see cref="Together"/> (one step of Undo, refused as a whole). Moved, they are made from the far end of the way they
    /// went, so that none is let go onto another of them that has yet to move. False when it was refused or failed.
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

        // What letting go of one block of the group does, as it is drawn now.
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

        // A middle-button drag taken away mid-way is no flick: nothing coasts from it (a release lets go of its capture after it is done).
        if (_panFrom is not null)
            _momentumX?.Stop();
        _panFrom = null;
        _clickOnly = null;
        if (_marqueePress is not null)
        {
            // Taken away mid-band: nothing is selected, and the band goes.
            (_marqueePress, _marquee) = (null, null);
            _overlayDirty = true;
            InvalidateVisual();
        }

        if (_heightDrag is { Row: { } sized })
            Raise(() => RowHeightDragEnded?.Invoke(sized, false));
        _heightDrag = null;
        if (IsDragging)
            Revert();
    }

    // Taken away mid-drag (another window came forward, the rows it was on are gone, the host let go of it): what it moved
    // goes back, and nothing is committed.
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
        EndDrag();
    }

    // A dragged block, the block a roll moved with it, and what it carried along, back where they were when it began.
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

        // As for the left button: the rows the host still owes first, so the menu is about the timeline as it is now.
        if (!IsDragging)
            Raise(() => PressStarting?.Invoke());
        FinishSlide();

        // A right-click selects the block as well, so that the menu that opens is about it; on a fade handle, the menu is
        // that fade's.
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
    /// Opens the selected block's menu from the keyboard (the Menu key, Shift+F10: the surface takes no keyboard focus of its
    /// own), at the block's top left as far as it is in view, as a right-click on it would. False when no selected block is
    /// on a lane shown in full, or its menu has nothing in it.
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

                // The block on the control: its laid-out place less the view's, kept within the time area.
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

    // A right-click began: it opens its menu when the button is let go, unless a key goes down meanwhile.
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
