using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// The unified timeline's mouse: everything TimelineTrack did on a bar (selecting, seeking beside the blocks, moving a block
// along its row or onto another, trimming either end, rippling with Ctrl, rolling a shared cut, slipping with Alt, dragging
// fade handles and gain lines, snapping, Shift for frame steps without snapping, auto-scroll near the ends, putting a drag
// back when the mouse is taken away, cursors and tooltips), plus what 4.4 adds: the headers' switches and chevrons, the J/L
// cut (Alt on the lower half of an edge), Alt + click to cut, the seams and gap pills, the skim line and the menus.
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
    // open; its unlinked sound; the main file's sound under it), drawn on the overlay as the parent is dragged. Moved by the
    // parent's travel, or with the parent's start or end edge (a trim, a roll, a seam), or left where it is and shown to come
    // loose (Detaching: a trim leaves it wholly outside the parent, which unlinks it). Where it was when the drag began.
    private enum Follows { Move, Start, End, SeamEdge }

    private sealed class Coupled
    {
        public required TimelineRow Row { get; init; }
        public required TimelineBlock Block { get; init; }
        public Follows How { get; init; }
        public bool OfNeighbor { get; init; }
        public double Left0 { get; init; }
        public double Width0 { get; init; }
        public bool Detaching { get; set; }
    }

    private readonly List<Coupled> _coupled = [];
    private readonly HashSet<TimelineBlock> _coupledSet = new(ReferenceEqualityComparer.Instance);

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

    private IReadOnlyList<double> _snapTimes = [];
    private double _skimSeconds = double.NaN;

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
    }

    // ----- Pressing -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;

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
            if (index >= 0)
                HeaderPressed(index, new Point(point.X, laid.Y), e);
            return;
        }

        if (index < 0)
        {
            Raise(() => EmptyPressed?.Invoke(null, TimeAt(point.X), e));
            return;
        }

        var lane = _lanes[index];
        var row = lane.Row;
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
                Raise(() => EmptyPressed?.Invoke(row, TimeAt(point.X), e));
            return;
        }

        var pressed = block;
        if (!Raise(() => BlockPressed?.Invoke(row, pressed, e)))
            return;
        var (found, again) = Refind(index, row, block);
        if (again is null)
            return;

        (index, block) = (found, again);
        lane = _lanes[index];
        row = lane.Row;

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
            var cut = block;
            Raise(() => RazorRequested?.Invoke(row, cut, TimeAt(point.X)));
        }
    }

    // A click on a lane of a collapsed stack: its stack opens, and every closed stack above it, so the lane is shown in full.
    private void OpenStacksAbove(int index)
    {
        var closed = new List<(TimelineRow Row, ChildStack Stack)>();
        for (var at = index; at >= 0 && _lanes[at].Mini && _lanes[at].Parent >= 0; at = _lanes[at].Parent)
            closed.Add((_lanes[_lanes[at].Parent].Row, _lanes[at].Row.Stack));

        foreach (var (row, stack) in closed)
            SetExpanded(row, stack, true);
    }

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
        else if (e.ClickCount % 2 == 0 && row.Children.Count > 0)
        {
            // A double click on the name opens or closes all of its stacks (every second click of a quick run of them).
            SetExpanded(row, !IsExpanded(row));
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
        var edge = TimelineTrack.EdgePixels;

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
            Row = row, Block = block, Kind = kind, PointerX = point.X, Left = left, Width = block.Width,
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
    // to its own children), the hover and the skim line go, and the rows are drawn again without what is dragged (the
    // overlay draws it as it goes).
    private void BeginDrag()
    {
        IReadOnlyList<double> times = [];
        Raise(() => times = SnapTimes?.Invoke() ?? []);
        _snapTimes = times;
        if (_drag is { } drag)
        {
            CollectFamily(drag);
            _snapTimes = WithoutFamily(_snapTimes, drag);
        }

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
        Invalidate();
        Raise(() => DragEnded?.Invoke());
    }

    /// <summary>
    /// Lets go of a drag in progress (4.4 BugHunt): what it drew moved is put back, the mouse is let go, and nothing is
    /// committed (DragEnded is raised, as for any drag's end). Nothing happens when nothing is being dragged.
    /// </summary>
    public void AbortDrag()
    {
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
    /// its start; a slip, none.
    /// </summary>
    private void CollectFamily(BlockDrag drag)
    {
        _coupled.Clear();
        _coupledSet.Clear();
        _loose.Clear();
        if (drag.Kind == BlockEdit.Slip || drag.Block.Family is null)
            return;

        // The blocks of every lane by the parent they belong to.
        var byParent = new Dictionary<object, List<(TimelineRow Row, TimelineBlock Block)>>();
        foreach (var lane in _lanes)
        {
            foreach (var block in lane.Row.Blocks)
            {
                if (block.Family is not { Parent: { } parent } || ReferenceEquals(block, drag.Block) || ReferenceEquals(block, drag.Neighbor))
                    continue;
                if (!byParent.TryGetValue(parent, out var children))
                    byParent[parent] = children = [];
                children.Add((lane.Row, block));
            }
        }

        if (byParent.Count == 0)
            return;

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

        // Every child (and theirs, the children on the same edge) that rides with the block: on that edge (a trim), or
        // all of them (a move). A trim also shows the children it may leave outside the parent, which come loose.
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
                    if (child.Family is not { Rides: true } childFamily || _coupledSet.Contains(child))
                        continue;

                    var onEdge = edge is { } wanted && (childFamily.OnParentEdges & wanted) != 0;
                    if (edge is not null && !onEdge)
                    {
                        // Off the dragged edge: only the root's own children can be left outside by a trim, and come loose.
                        if (ReferenceEquals(parent, root) && drag.Seam is null && drag.Kind is BlockEdit.TrimEnd or BlockEdit.TrimStart)
                            Add(row, child, how, ofNeighbor, mayDetach: true);
                        continue;
                    }

                    Add(row, child, how, ofNeighbor, mayDetach: false);
                    queue.Enqueue(child);
                }
            }
        }

        void Add(TimelineRow row, TimelineBlock child, Follows how, bool ofNeighbor, bool mayDetach)
        {
            _coupled.Add(new Coupled
            {
                Row = row, Block = child, How = mayDetach ? Follows.Move : how, OfNeighbor = ofNeighbor, Left0 = child.Left, Width0 = child.Width,
                Detaching = false,
            });
            _coupledSet.Add(child);
            if (mayDetach)
                _loose.Add(child);
        }
    }

    // The children a trim may leave outside their parent (they stay where they are, and are shown coming loose once it does).
    private readonly HashSet<TimelineBlock> _loose = new(ReferenceEqualityComparer.Instance);

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
        var (parentLeft, parentRight) = (block.Left, block.Left + block.Width);
        var seamGrowth = drag.Seam is { } grip ? grip.Seconds * PixelsPerSecond : 0;
        var fits = true;
        foreach (var child in _coupled)
        {
            var (left, width) = (child.Left0, child.Width0);
            switch (child.How)
            {
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

            // Trimmed past: left wholly outside its parent, it comes loose and stays where it was.
            var loose = _loose.Contains(child.Block)
                ? drag.Kind == BlockEdit.TrimEnd && child.Left0 >= parentRight - 0.5 || drag.Kind == BlockEdit.TrimStart && child.Left0 + child.Width0 <= parentLeft + 0.5
                : width < 1;
            child.Detaching = loose && drag.Seam is null;
            (child.Block.Left, child.Block.Width) = loose ? (child.Left0, child.Width0) : (left, Math.Max(width, 4));
        }

        drag.FamilyFits = fits;
    }

    // ----- What is under the pointer -----

    /// <summary>The block under a point of the laid-out rows in a lane, the topmost (the last drawn) first; its J/L sound's reach counts.</summary>
    private TimelineBlock? BlockAt(int index, Point laid)
    {
        var lane = _lanes[index];
        var blocks = lane.Row.Blocks;
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var block = blocks[i];
            var rect = BlockRect(lane, block, 0);
            if (rect.Contains(laid))
                return block;
            if (lane.Detailed && SoundReach(block, rect) is { } reach && reach.Contains(laid))
                return block;
        }

        return null;
    }

    // Where a J/L cut's sound lies: the lower part of the block, reaching out past its edges.
    private Rect? SoundReach(TimelineBlock block, Rect rect)
    {
        var scale = PixelsPerSecond;
        if (block.AudioSplit is not { } split || (split.LeadIn == 0 && split.TailOut == 0) || scale <= 0)
            return null;

        return new Rect(rect.Left - split.LeadIn * scale, rect.Top + rect.Height * SoundBandTop,
            Math.Max(rect.Width + (split.LeadIn + split.TailOut) * scale, 1), rect.Height * (1 - SoundBandTop));
    }

    /// <summary>Which edge of a block's sound a point is on, in its lower part: true where it starts, false where it ends; null neither.</summary>
    private bool? SplitEdgeAt(TimelineBlock block, Rect rect, Point laid)
    {
        var scale = PixelsPerSecond;
        if (block.AudioSplitEdit is null || scale <= 0 || laid.Y < rect.Top + rect.Height * SoundBandTop || laid.Y > rect.Bottom)
            return null;

        var (lead, tail) = block.AudioSplit ?? (0.0, 0.0);
        if (Math.Abs(laid.X - (rect.Left - lead * scale)) <= TimelineTrack.EdgePixels)
            return true;
        if (Math.Abs(laid.X - (rect.Right + tail * scale)) <= TimelineTrack.EdgePixels)
            return false;
        return null;
    }

    /// <summary>Which of a block's fade handles a point is on: true the fade in's, false the fade out's; null neither.</summary>
    private bool? FadeHandleAt(in Lane lane, TimelineBlock block, Point laid)
    {
        if (block.Fades is not { } fades || block.FadeEdit is null)
            return null;

        var rect = BlockRect(lane, block, 0);
        if (laid.Y > rect.Top + FadeBand || laid.Y < rect.Top - 1)
            return null;

        var (fadeIn, fadeOut) = FadeSpan(rect, fades.In, fades.Out, PixelsPerSecond);
        var (toIn, toOut) = (Math.Abs(laid.X - HandleX(rect, rect.Left + fadeIn)), Math.Abs(laid.X - HandleX(rect, rect.Right - fadeOut)));
        if (Math.Min(toIn, toOut) > FadeReach)
            return null;
        return toIn <= toOut;
    }

    /// <summary>Whether a point is on a block's gain line (away from its ends, which trim).</summary>
    private static bool OnGainLine(in Lane lane, TimelineBlock block, Point laid)
    {
        if (block.Gain is not { } gain || block.GainEdit is null)
            return false;

        var rect = BlockRect(lane, block, 0);
        return Math.Abs(laid.Y - GainY(rect, gain.GainDb)) <= GainReach + 1
            && laid.X > rect.Left + TimelineTrack.EdgePixels && laid.X < rect.Right - TimelineTrack.EdgePixels;
    }

    /// <summary>Whether a point is on a fade handle of the block under it: a seam's tab there gives way to the handle.</summary>
    private bool OnFadeHandle(int index, Point laid) =>
        _lanes[index].Detailed && BlockAt(index, laid) is { } block && FadeHandleAt(_lanes[index], block, laid) is not null;

    /// <summary>The seam whose tab a point is on (an index into the row's seams); -1 for none.</summary>
    private int SeamAt(in Lane lane, Point laid)
    {
        var scale = PixelsPerSecond;
        if (!lane.Detailed || scale <= 0)
            return -1;

        var seams = lane.Row.Seams;
        for (var i = 0; i < seams.Count; i++)
        {
            if (Rect.Inflate(SeamTab(lane, seams[i].Seconds * scale), 2, 2).Contains(laid))
                return i;
        }

        return -1;
    }

    /// <summary>The gap whose pill a point is on (an index into the row's gaps): only the pill of the gap the pointer is in shows, so only it counts; -1 for none.</summary>
    private int GapAt(in Lane lane, Point laid)
    {
        if (!lane.Detailed || IsDragging || _hoveredGap is not { } hovered || !ReferenceEquals(hovered.Row, lane.Row) || hovered.Index >= lane.Row.Gaps.Count)
            return -1;

        var (left, right) = lane.Row.Gaps[hovered.Index];
        return right - left >= GapLeast && Rect.Inflate(GapPill(lane, left, right), 2, 2).Contains(laid) ? hovered.Index : -1;
    }

    /// <summary>The gap a point of a lane drawn in full lies in (an index into the row's gaps), whose pill then shows; -1 for none.</summary>
    private int GapUnder(in Lane lane, Point laid)
    {
        if (!lane.Detailed || IsDragging)
            return -1;

        var gaps = lane.Row.Gaps;
        for (var i = 0; i < gaps.Count; i++)
        {
            if (laid.X >= gaps[i].Left && laid.X <= gaps[i].Right)
                return i;
        }

        return -1;
    }

    /// <summary>The chevron a point is on (x on the control, y on the rows): the stack it opens and closes; null for none.</summary>
    private static ChildStack? ChevronAt(in Lane lane, Point at)
    {
        if (lane.Stacks == 0 || lane.Height < 12)
            return null;

        var (slot, apart) = (0, lane.ChevronCount > 1 ? 0.5 : 2);
        foreach (var stack in StackOrder)
        {
            if (!lane.Has(stack))
                continue;
            if (Rect.Inflate(ChevronRect(lane, slot++), apart, 2).Contains(at))
                return stack;
        }

        return null;
    }

    // What a chevron does, in words: with one stack, its lanes; with two, which of them.
    private static string ChevronTip(in Lane lane, ChildStack stack, bool open)
    {
        var lanes = lane.ChevronCount < 2 ? "its lanes"
            : stack == ChildStack.Audio ? "its sound's lanes (the unlinked sound, the file's audio streams)"
            : "its layers' lanes (the HUD cut-outs and masks, the captions)";
        return open ? $"Fold {lanes} into a mini-stack" : $"Open {lanes} to their full height";
    }

    /// <summary>The stretch put back whose Hide pill a point is on (an index into the row's reveals); -1 for none.</summary>
    private int RevealAt(in Lane lane, Point laid)
    {
        var scale = PixelsPerSecond;
        if (!lane.Detailed || scale <= 0 || lane.Row.Reveals.Count == 0)
            return -1;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var reveals = lane.Row.Reveals;
        for (var i = 0; i < reveals.Count; i++)
        {
            if (RevealPill(lane, reveals[i].From * scale, reveals[i].To * scale, pixelsPerDip, out _) is { IsEmpty: false } pill
                && Rect.Inflate(pill, 2, 2).Contains(laid))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The header switch a point is on (x on the control, y on the rows); null for none.</summary>
    private RowSwitches? SwitchAt(in Lane lane, Point at)
    {
        if (!lane.Detailed)
            return null;

        var slot = 0;
        foreach (var flag in SwitchOrder)
        {
            if ((lane.Row.Offered & flag) == 0)
                continue;
            if (SwitchRect(lane, slot++).Contains(at))
                return flag;
        }

        return null;
    }

    // ----- Moving -----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_panFrom is { } from)
        {
            PanRequested?.Invoke(from - point.X);
            _panFrom = point.X;
            return;
        }

        var laid = Laid(point);
        if (_splitDrag is { } split)
        {
            FollowSplit(split, laid);
            return;
        }

        if (_fadeDrag is { } fadeDrag)
        {
            FollowFade(fadeDrag, laid);
            return;
        }

        if (_gainDrag is { } gainDrag)
        {
            FollowGain(gainDrag, laid);
            return;
        }

        if (_drag is not { } drag)
        {
            HoverAt(point, laid);
            return;
        }

        _lastPointer = laid;
        Follow(drag, laid, pointerMoved: true);

        // Near an end of the time area, the view moves along by itself while the pointer stays there.
        var seen = point.X - _headerWidth;
        var nearEdge = seen < AutoScrollZone || seen > TimeAreaWidth - AutoScrollZone;
        if (nearEdge && !_autoScroll.IsEnabled)
            _autoScroll.Start();
        else if (!nearEdge)
            _autoScroll.Stop();
    }

    /// <summary>Lays a dragged block (and a rolled neighbour) out for where the pointer is.</summary>
    private void Follow(BlockDrag drag, Point point, bool pointerMoved)
    {
        // A clip may be dragged, or stretched, past the end of the sequence: the sequence is as long as its clips reach,
        // and the rows are laid out to the new length when it is let go.
        var moved = point.X - drag.PointerX;
        if (pointerMoved)
            drag.Travel = Math.Max(drag.Travel, Math.Abs(moved));

        switch (drag.Kind)
        {
            // An edge at a seam: outward shows the fold again, inward folds more of the clip into it, within what the fold
            // and the clip have (seconds; Shift: whole frames). The clip grows or shrinks at that edge, its start staying at
            // the cut (what follows slides by as much: drawn shifted, see CurrentShift).
            case BlockEdit.TrimEnd or BlockEdit.TrimStart when drag.Seam is { } grip && PixelsPerSecond > 0:
            {
                var scale = PixelsPerSecond;
                var outward = (grip.AtStart ? -moved : moved) / scale;
                if (PrecisionRate() is var rate and > 0)
                    outward = Math.Round(outward * rate) / rate;
                grip.Seconds = Math.Clamp(outward, -grip.Fold, grip.Reveal);
                drag.Block.Width = Math.Max(drag.Width + grip.Seconds * scale, 4);
                _contentDirty = _labelsDirty = true;
                break;
            }

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
                if (pointerMoved)
                {
                    TargetUnder(drag, point);
                }
                else if (drag.Target is { } target && drag.Block.CanMoveTo is { } canMoveTo)
                {
                    var (fits, at) = (false, drag.Block.Left);
                    Raise(() => fits = canMoveTo(target.Data, at));
                    drag.Fits = fits;
                }

                break;
            }
        }

        // What it carries along (its children, its unlinked sound, the main file's sound under it) goes with it, every move.
        FollowFamily(drag);
        _overlayDirty = true;
        InvalidateVisual();
    }

    // The other row a moved block is over (one it may be let go on, its CanMoveTo says whether), or none.
    private void TargetUnder(BlockDrag drag, Point point)
    {
        var index = OpenLaneOf(LaneAt(point.Y));
        var row = index >= 0 ? _lanes[index].Row : null;
        if (row is null || ReferenceEquals(row, drag.Row) || drag.Block.CanMoveTo is not { } canMoveTo)
        {
            drag.Target = null;
            return;
        }

        drag.Target = row;
        var (fits, at) = (false, drag.Block.Left);
        Raise(() => fits = canMoveTo(row.Data, at));
        drag.Fits = fits;
    }

    /// <summary>A gain line dragged up or down: the gain.</summary>
    private void FollowGain((TimelineRow Row, TimelineBlock Block, Point From, (double GainDb, double FadeIn, double FadeOut) Start) drag, Point point)
    {
        var index = LaneOf(drag.Row);
        if (index < 0)
            return;

        var rect = BlockRect(_lanes[index], drag.Block, 0);
        drag.Block.Gain = drag.Start with { GainDb = GainAt(rect, GainY(rect, drag.Start.GainDb) + point.Y - drag.From.Y) };
        _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// A fade handle dragged along: its fade's length, from the block's edge to where the handle is, snapped (to the other
    /// clips' edges and the playhead) and no longer than the block leaves beside its other fade. The gain line's slope follows.
    /// </summary>
    private void FollowFade((TimelineRow Row, TimelineBlock Block, bool In, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) Start) drag, Point point)
    {
        var scale = PixelsPerSecond;
        var index = LaneOf(drag.Row);
        if (scale <= 0 || index < 0)
            return;

        var (block, start) = (drag.Block, drag.Start);
        var rect = BlockRect(_lanes[index], block, 0);
        var x = Snap(point.X);
        var room = Math.Max(rect.Width / scale - (drag.In ? start.Out : start.In), 0);
        var seconds = Math.Clamp((drag.In ? x - rect.Left : rect.Right - x) / scale, 0, room);
        seconds = PrecisionRate() is var rate and > 0 ? Math.Floor(seconds * rate + 0.5) / rate : Math.Round(seconds, 2);
        block.Fades = drag.In ? start with { In = seconds } : start with { Out = seconds };
        if (block.Gain is { } gain)
            block.Gain = drag.In ? gain with { FadeIn = seconds } : gain with { FadeOut = seconds };
        _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// A J/L cut dragged: the sound's start (or end) follows the pointer, snapped as an edge is, the picture staying where
    /// it is. The sound keeps at least a frame.
    /// </summary>
    private void FollowSplit(SplitDrag split, Point point)
    {
        var scale = PixelsPerSecond;
        var index = LaneOf(split.Row);
        if (scale <= 0 || index < 0)
            return;

        var rect = BlockRect(_lanes[index], split.Block, 0);
        var picture = rect.Width / scale;
        var least = 1 / Math.Max(FrameRate?.Invoke() ?? 0, 30);
        var moved = point.X - split.PointerX;
        if (split.AtLead)
        {
            var x = Snap(rect.Left - split.StartLead * scale + moved);
            split.Lead = Math.Round(Math.Max((rect.Left - x) / scale, -(picture - split.Tail - least)), 3);
        }
        else
        {
            var x = Snap(rect.Right + split.StartTail * scale + moved);
            split.Tail = Math.Round(Math.Max((x - rect.Right) / scale, -(picture - split.Lead - least)), 3);
        }

        _overlayDirty = true;
        InvalidateVisual();
    }

    // The pointer moved with nothing held: what it is over shows its cursor, its tooltip and its fade handles, and the skim
    // line follows it over the time area.
    private void HoverAt(Point point, Point laid)
    {
        var index = LaneAt(laid.Y);

        // The pill that closes a gap shows while the pointer is in that gap.
        var inTime = point.X >= _headerWidth && index >= 0;
        HoverGap(inTime ? _lanes[index].Row : null, inTime ? GapUnder(_lanes[index], laid) : -1);
        if (point.X < _headerWidth)
        {
            Skim(double.NaN);
            Hover(null, null);
            if (index < 0)
            {
                NoTip();
                Cursor = null;
                return;
            }

            var header = _lanes[index];
            var at = new Point(point.X, laid.Y);
            if (ChevronAt(header, at) is { } stack)
            {
                Cursor = Cursors.Hand;
                if (TipFor(header.Row, stack == ChildStack.Audio ? 5 : 1))
                    ToolTip = ChevronTip(header, stack, IsExpanded(header.Row, stack));
                return;
            }

            if (SwitchAt(header, at) is { } flag)
            {
                Cursor = Cursors.Hand;
                if (TipFor(header.Row, 10 + (int)flag))
                    ToolTip = SwitchTip(flag, (header.Row.State & flag) != 0);
                return;
            }

            Cursor = null;
            if (TipFor(header.Row, 2))
                ToolTip = header.Row.Tip;
            return;
        }

        Skim(TimeAt(point.X));
        if (index < 0)
        {
            Hover(null, null);
            NoTip();
            Cursor = Cursors.Hand;
            return;
        }

        var lane = _lanes[index];
        var row = lane.Row;
        var scale = PixelsPerSecond;
        if (RevealAt(lane, laid) is >= 0 and var reveal && !OnFadeHandle(index, laid))
        {
            Hover(null, null);
            Cursor = Cursors.Hand;
            if (TipFor(row, 1_000 + reveal))
            {
                var mark = row.Reveals[reveal];
                ToolTip = string.Create(CultureInfo.InvariantCulture,
                    $"Dead air ({mark.To - mark.From:0.00} s) that was put back here. Click to fold it out again; Undo puts it back.");
            }

            return;
        }

        if (SeamAt(lane, laid) is >= 0 and var seam && !OnFadeHandle(index, laid))
        {
            Hover(null, null);
            Cursor = Cursors.Hand;
            if (TipFor(row, 100 + seam))
                ToolTip = string.Create(CultureInfo.InvariantCulture, $"Dead air was folded out here ({row.Seams[seam].FoldedSeconds:0.00} s). Click to put it back.");
            return;
        }

        if (GapAt(lane, laid) is >= 0 and var gap && scale > 0)
        {
            Hover(null, null);
            Cursor = Cursors.Hand;
            var (left, right) = row.Gaps[gap];
            if (TipFor(row, 100_000 + gap))
                ToolTip = string.Create(CultureInfo.InvariantCulture, $"A gap of {(right - left) / scale:0.00} s. Click to close it: what follows on this track moves up to meet what is before it.");
            return;
        }

        var block = BlockAt(index, laid);
        Hover(block is null ? null : row, block);
        if (block is null || !lane.Detailed)
        {
            NoTip();
            Cursor = Cursors.Hand;
            return;
        }

        // The edges are the handles for the length and the start; the cut between two clips rolls; Alt slips (and, on the
        // lower part of an edge of a clip with linked sound, makes a J/L cut); the top corners (or as far in as a fade) are
        // the fades' handles.
        var rect = BlockRect(lane, block, 0);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var fadeHandle = FadeHandleAt(lane, block, laid);
        var edge = TimelineTrack.EdgePixels;

        // An edge at a seam of folded dead air: dragged, it shows the fold again or folds more into it (never a roll there).
        var atSeam = fadeHandle is null && !alt && block.SeamTrim is not null
            && (laid.X < block.Left + edge ? SeamNear(row, block.Left) : laid.X > block.Left + block.Width - edge ? SeamNear(row, block.Left + block.Width) : null) is not null;
        if (atSeam)
        {
            if (TipFor(block, 6))
                ToolTip = "An edge at folded dead air: drag it outward to show some of the folded silence again, or inward to fold more of this clip into it. What follows slides with it.";
        }
        else
        {
            FadeTip(block, fadeHandle);
        }
        Cursor = fadeHandle is not null ? Cursors.Cross
            : alt && SplitEdgeAt(block, rect, laid) is not null ? Cursors.SizeWE
            : OnGainLine(lane, block, laid) ? Cursors.SizeNS
            : block is { Resizable: true, CanDrag: not null } && laid.X > block.Left + block.Width - edge ? Cursors.SizeWE
            : block is { TrimmableStart: true, CanDrag: not null, Edit: not null } && laid.X < block.Left + edge ? Cursors.SizeWE
            : block is { Slippable: true, Edit: not null } && alt ? Cursors.ScrollWE
            : block.CanDrag is not null ? Cursors.SizeAll
            : Cursors.Hand;
    }

    /// <summary>The pointer is over another block (or none): the one it left hides its corner handles, the one it is over shows them.</summary>
    private void Hover(TimelineRow? row, TimelineBlock? block)
    {
        if (ReferenceEquals(block, _hovered?.Block))
            return;

        var shows = _hovered?.Block.Fades is not null || block?.Fades is not null;
        _hovered = row is not null && block is not null ? (row, block) : null;
        if (shows)
        {
            _overlayDirty = true;
            InvalidateVisual();
        }
    }

    /// <summary>The pointer is in another gap (or none, -1): the pill of the one it left goes, the one it is in shows its own.</summary>
    private void HoverGap(TimelineRow? row, int gap)
    {
        if (gap < 0)
            row = null;
        if (ReferenceEquals(row, _hoveredGap?.Row) && (row is null || gap == _hoveredGap?.Index))
            return;

        _hoveredGap = row is not null ? (row, gap) : null;
        _overlayDirty = true;
        InvalidateVisual();
    }

    // Whether the tooltip is now for something else (and is to be set): it is set once per thing the pointer is over.
    private bool TipFor(object? target, int part)
    {
        if (ReferenceEquals(target, _tipTarget) && part == _tipPart)
            return false;

        (_tipTarget, _tipPart) = (target, part);
        return true;
    }

    private void NoTip()
    {
        if (TipFor(null, 0))
            ToolTip = null;
    }

    private void ForgetTip()
    {
        _tipTarget = null;
        _tipPart = -1;
    }

    /// <summary>Over a fade handle, a tooltip saying what it is and what it does; elsewhere on the block, none.</summary>
    private void FadeTip(TimelineBlock block, bool? fadeIn)
    {
        if (fadeIn is not { } isIn || block.Fades is not { } fades)
        {
            if (TipFor(block, 0))
                ToolTip = null;
            return;
        }

        if (!TipFor(block, isIn ? 3 : 4))
            return;

        var seconds = isIn ? fades.In : fades.Out;
        ToolTip = (seconds > 0.005 ? FadeWords(isIn, seconds, isIn ? fades.InStyle : fades.OutStyle) : $"Fade {(isIn ? "In" : "Out")}: none")
            + $". Drag {(isIn ? "right" : "left")} to fade the clip's picture and sound {(isIn ? "in" : "out")} over what is under it; it snaps to where clips on the other tracks start and end"
            + ", for a crossfade with one. Right-click for the transition style.";
    }

    private static string SwitchTip(RowSwitches flag, bool on) => flag switch
    {
        RowSwitches.Hidden => on ? "Hidden: click to show this track again" : "Hide this track (it is left out of the picture)",
        RowSwitches.Muted => on ? "Muted: click to hear this track again" : "Mute this track",
        RowSwitches.Soloed => on ? "Soloed: click to stop soloing it" : "Solo: hear only the soloed tracks. Alt+click: solo this track alone",
        RowSwitches.Voice => on ? "A voice track: click to unmark it" : "Mark as a voice track (what ducking and captions listen to)",
        RowSwitches.Locked => on ? "Locked: click to unlock this track" : "Lock this track against edits",
        _ => "",
    };

    /// <summary>Moves the skim line to a moment (NaN: none) and says so.</summary>
    private void Skim(double seconds)
    {
        if (seconds.Equals(_skimSeconds))
            return;

        var was = _skimSeconds;
        _skimSeconds = seconds;
        PlaceLine(_skim, seconds, ref _skimHeight, playhead: false);
        if (!double.IsNaN(seconds))
            SkimChanged?.Invoke(seconds);
        else if (!double.IsNaN(was))
            SkimChanged?.Invoke(null);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        // A right-click under way is not captured: let go elsewhere, its release never comes here, so it opens no menu and
        // stops listening to the window's keys now.
        DisarmMenu();
        if (IsDragging)
            return;

        Hover(null, null);
        HoverGap(null, -1);
        NoTip();
        Skim(double.NaN);
    }

    private void AutoScroll_Tick(object? sender, EventArgs e)
    {
        var width = TimeAreaWidth;
        if (_drag is not { } drag || Mouse.LeftButton != MouseButtonState.Pressed || width <= 0)
        {
            _autoScroll.Stop();
            return;
        }

        // Faster the further into the end the pointer is: up to about 3% of the time area every tick.
        var seen = _lastPointer.X - ViewOffsetX;
        var depth = seen < AutoScrollZone ? seen - AutoScrollZone : seen > width - AutoScrollZone ? seen - (width - AutoScrollZone) : 0;
        var pixels = Math.Clamp(depth / AutoScrollZone, -1.5, 1.5) * width * 0.03;
        if (Math.Abs(pixels) < 0.5)
            return;

        // The host moves the view (SetView), and the pointer is where it was on screen: further along the rows.
        var before = ViewOffsetX;
        PanRequested?.Invoke(pixels);
        _lastPointer.X += ViewOffsetX - before;
        Follow(drag, _lastPointer, pointerMoved: false);
    }

    /// <summary>The frame rate to step by while Shift is held (Precision Unsnapped mode); 0 when Shift is not held or no rate is known.</summary>
    private double PrecisionRate() =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && FrameRate?.Invoke() is > 0 and var rate ? rate : 0;

    /// <summary>
    /// A point along the rows moved onto the nearest moment to snap to, when one is close enough; as it was otherwise.
    /// Shift held (Precision Unsnapped mode): no snapping, and the point is put on the nearest frame of the sequence, so an
    /// edge dragged with Shift trims frame by frame. Alt held: no snapping either (Alt-drag inside a clip slips).
    /// </summary>
    private double Snap(double x)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0)
            return x;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return PrecisionRate() is var rate and > 0 ? Math.Floor(x / scale * rate + 0.5) / rate * scale : x;

        if (_snapTimes.Count == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return x;

        var best = x;
        var nearest = TimelineTrack.SnapPixels;
        for (var i = 0; i < _snapTimes.Count; i++)
        {
            var at = _snapTimes[i] * scale;
            if (Math.Abs(at - x) < nearest)
                (best, nearest) = (at, Math.Abs(at - x));
        }

        return best;
    }

    // ----- Letting go -----

    // Each drag is let go of first (so nothing the host does finds it still held), then what it does is raised to the host
    // (4.4 BugHunt): a fault there puts back what was drawn moved and is told (CallbackFailed), never thrown on; the drag's
    // end is always told, so the host lays the rows out again from what the timeline is now, whether the edit was made,
    // refused (the model says why) or failed.
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
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
            base.OnMouseLeftButtonUp(e);
            return;
        }

        _drag = null;
        ReleaseMouseCapture();
        e.Handled = true;
        var block = drag.Block;
        var made = true;
        try
        {
            // Alt and a click that did not move: a cut there (the block goes back to where it was).
            if (drag.Alt && drag.Travel < RazorTravel && drag.Target is null)
            {
                PutBack(drag);
                var scale = PixelsPerSecond;
                var at = scale > 0 ? drag.PointerX / scale : _viewStart;
                Raise(() => RazorRequested?.Invoke(drag.Row, block, at));
                return;
            }

            // An edge at a seam: the fold shown again, or more folded into it, by as much as it went.
            if (drag.Seam is { } grip)
            {
                if (Math.Abs(grip.Seconds) > 1e-4 && block.SeamTrim is { } seamTrim)
                    made = Raise(() => seamTrim(grip.AtStart, grip.Seconds));
                return;
            }

            var changed = Math.Abs(block.Left - drag.Left) > 0.5 || Math.Abs(block.Width - drag.Width) > 0.5;

            // Let go on another row: it goes there (or, refused, is laid back where it was).
            if (drag.Target is { } target && block.MoveTo is { } moveTo)
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
        }
        finally
        {
            if (!made)
                PutBack(drag);
            EndDrag();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panFrom = null;
        if (IsDragging)
            Revert();
    }

    // Taken away mid-drag (another window came forward, the rows it was on are gone, the host let go of it): what it moved
    // goes back, and nothing is committed.
    private void Revert()
    {
        _autoScroll.Stop();
        if (_fadeDrag is { } fadeDrag)
            PutBackFade(fadeDrag);
        else if (_gainDrag is { } gainDrag)
            gainDrag.Block.Gain = gainDrag.Start;
        else if (_drag is { } drag)
            PutBack(drag);

        _drag = null;
        _fadeDrag = null;
        _gainDrag = null;
        _splitDrag = null;
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

    // ----- The wheel and the middle button -----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomRequested?.Invoke(Math.Pow(1.25, e.Delta / 120.0), Math.Max(e.GetPosition(this).X - _headerWidth, 0));
        }
        else if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            PanRequested?.Invoke(-e.Delta);
        }
        else
        {
            // A plain wheel scrolls the rows; when they all fit, it is left to what holds the control.
            var before = _verticalOffset;
            VerticalOffset = before - e.Delta / 120.0 * WheelStep;
            if (_verticalOffset == before)
            {
                base.OnMouseWheel(e);
                return;
            }
        }

        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && !IsDragging && CaptureMouse())
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
