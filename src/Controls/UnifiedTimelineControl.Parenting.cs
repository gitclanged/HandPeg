using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// ----- Fluid drags and child layers (5.5A, the user's directive) -----
// What a move wakes is held back while the pointer sweeps past it: a "+ New Track" seam and a clip's tether wake only once the pointer has
// settled on them (DragSettle: under 60 DIP/s for 75 ms, or still), never in Transit (over 160 DIP/s), and stay awake until Transit once
// woken; a move changes rows only past a 6 DIP deadzone beyond the row it is on; Shift on a move that has left its lane keeps its time.
// Two ways to make a child layer: a clip let go on the bottom edge of a clip it overlaps on another lane (or anywhere on it with Alt pressed
// during the move) is tethered to it, a curve drawn to it with "Attach as Child of ..." (the host makes it that clip's child: its Link
// switch unlinks it); a track's header dragged to a seam between the picture tracks moves the track with its Sub-Lanes there (a Sub-Lane
// leaves its stack), and dragged right onto the lower half of another top-level picture track's header makes it that track's Sub-Lane.
// All of it on the UI thread; the dwell's timer runs only while a drag rests in a window, and nothing of it is made before the first drag.
public sealed partial class UnifiedTimelineControl
{
    // How far (DIPs) a move goes past the row it is on before it changes rows; how far right a header goes to nest; a tether's band.
    private const double VerticalDeadzone = 6, NestIndent = 18, AttachBand = 14;

    // The pointer's speed for the drags, made at the first one.
    private DragSettle? _settle;

    private DragSettle Settle => _settle ??= new DragSettle();

    /// <summary>The drags' clock, ms (MomentumScroller.Now when null). Test seam.</summary>
    internal Func<double>? Clock { get; set; }

    private double Now() => Clock?.Invoke() ?? MomentumScroller.Now();

    // The dwell's look again (WPF raises no move while the pointer rests): made at a drag's first move, dropped as the drag ends. While a
    // move of one block rests with the button held it ticks on every SettleMs (Input priority), watching the keys (RestTick, 5.5A review).
    private DispatcherTimer? _settleTimer;

    /// <summary>Whether a track's header may be dragged (5.5A): the host's say for the row (a picture track's).</summary>
    public Func<TimelineRow, bool>? CanDragRow { get; set; }

    /// <summary>Whether a dragged header's row may go to a seam beside a top-level picture row (that row, and above it: true); asked once a seam in a drag.</summary>
    public Func<TimelineRow, TimelineRow, bool, bool>? CanMoveRowTo { get; set; }

    /// <summary>Whether a dragged header's row may become a Sub-Lane of another top-level picture row; asked once a row in a drag.</summary>
    public Func<TimelineRow, TimelineRow, bool>? CanNestRow { get; set; }

    /// <summary>A dragged header let go at a seam (5.5A): the row, the top-level picture row the seam is beside, and whether above it.</summary>
    public event Action<TimelineRow, TimelineRow, bool>? RowMoveRequested;

    /// <summary>A dragged header let go nested (5.5A): the row, and the top-level picture row it is to be a Sub-Lane of (its stack is opened).</summary>
    public event Action<TimelineRow, TimelineRow>? RowNestRequested;

    // A track's header pressed (held for a drag) or dragged: its row, where it was pressed (the control's x, the rows' y), whether the drag
    // has begun, the pointer's last place on the control, the seam or the row to nest under it is over, and the host's answers so far.
    private sealed class RowDrag
    {
        public required TimelineRow Row { get; init; }
        public double PressX { get; init; }
        public double PressY { get; init; }
        public bool Started { get; set; }
        public Point Pointer { get; set; }
        public (TimelineRow Beside, bool Above, double Y)? Seam { get; set; }
        public TimelineRow? Nest { get; set; }
        public (TimelineRow Beside, bool Above, bool May)? SeamAsked { get; set; }
        public (TimelineRow Parent, bool May)? NestAsked { get; set; }
    }

    private RowDrag? _rowDrag;

    // ----- The dwell -----

    // A move of a drag that waits on the dwell: its look again once the pointer has rested past it (in a window only: never in a test).
    // Started once and left running while moves come (never restarted at every move: a fast mouse sends a thousand a second); its tick
    // waits on for what is left of the dwell when the pointer moved meanwhile.
    private void RestSoon()
    {
        if (_settleTimer is { IsEnabled: true } || PresentationSource.FromVisual(this) is null)
            return;

        if (_settleTimer is null)
        {
            _settleTimer = new DispatcherTimer(DispatcherPriority.Input);
            _settleTimer.Tick += SettleTimer_Tick;
        }

        _settleTimer.Interval = TimeSpan.FromMilliseconds(DragSettle.SettleMs + 5);
        _settleTimer.Start();
    }

    private void StopSettleTimer()
    {
        if (_settleTimer is not { } timer)
            return;

        timer.Stop();
        timer.Tick -= SettleTimer_Tick;
        _settleTimer = null;
    }

    private void SettleTimer_Tick(object? sender, EventArgs e)
    {
        if (_settleTimer is not { } timer)
            return;

        timer.Stop();
        if (Mouse.LeftButton != MouseButtonState.Pressed)
            return;

        // (Started again only while it is still the drag's: a look that ended the drag has dropped it.)
        if (RestTick(Now(), Keyboard.Modifiers) is { } wait && ReferenceEquals(_settleTimer, timer))
        {
            timer.Interval = TimeSpan.FromMilliseconds(wait);
            timer.Start();
        }
    }

    /// <summary>
    /// The dwell's timer at <paramref name="nowMs"/>, <paramref name="keys"/> held (5.5A). Moved since it was started: it waits on for the rest of
    /// the dwell from the last move. Rested past it: the drag looks again once for what it is over (a zone may wake), with the keys as they are
    /// now; then (5.5A review), while a move of one block rests with the button held, it looks at the keys every <see cref="DragSettle.SettleMs"/>
    /// and looks again only when they changed (Alt pressed at rest tethers at once, Shift keeps its time once it is off its lane). The wait to
    /// the next tick (ms), or null to stop. Test seam (the timer is made only in a window).
    /// </summary>
    internal double? RestTick(double nowMs, ModifierKeys keys)
    {
        var rested = double.IsNaN(Settle.LastSampleMs) ? DragSettle.SettleMs : nowMs - Settle.LastSampleMs;
        if (rested < DragSettle.SettleMs)
            return DragSettle.SettleMs - rested + 5;

        BlockDrag? OneMove() => _drag is { Kind: BlockEdit.Move, Group: not { Count: > 0 } } one && !_dragParked ? one : null;
        if (!Settle.RestLooked || (OneMove() is { } moving && moving.Keys != keys))
        {
            if (_drag is { } drag)
                drag.Keys = keys;
            Settle.RestLooked = true;
            SettleTick(nowMs);
        }

        return OneMove() is not null ? DragSettle.SettleMs : null;
    }

    /// <summary>The pointer at rest at <paramref name="nowMs"/>: a drag looks again for what it is over (a zone may wake). Test seam.</summary>
    internal void SettleTick(double nowMs)
    {
        if (_rowDrag is { Started: true } rowDrag)
            EvaluateRowDrag(rowDrag, rowDrag.Pointer, nowMs);
        else if (_drag is { Kind: BlockEdit.Move, Group: not { Count: > 0 } } drag && !_dragParked)
            Follow(drag, _lastPointer, pointerMoved: true, nowMs);
    }

    // ----- A move's rows: the deadzone -----

    // The lane a move is over: the row it is on (its own, or the one it is over) while the pointer is within the deadzone past its edges,
    // else the open lane under the pointer (a thin lane of a closed stack stands for its open row).
    private int StickyLaneAt(BlockDrag drag, double y)
    {
        if (LaneOf(drag.Target ?? drag.Row) is >= 0 and var at && y >= _lanes[at].Top - VerticalDeadzone && y < _lanes[at].Bottom + LaneGap + VerticalDeadzone)
            return at;
        return OpenLaneOf(LaneAt(y));
    }

    // ----- Way 2: a clip tethered to a clip -----

    /// <summary>
    /// The clip a move is tethered to at <paramref name="laid"/> (5.5A): a clip on another lane drawn in full that the dragged block overlaps
    /// in time, the pointer on the band along its bottom edge (just above a "+ New Track" seam's band there, never on it) once settled or
    /// still awake from before; with Alt pressed during the move (not at the press: a slip, a cut), anywhere on it but a seam's band, at
    /// once. The host says whether it may be (its name for the pill). Only a move of one block (never a group) the host offers it for.
    /// </summary>
    private (TimelineRow Row, TimelineBlock Block, string Name)? AttachAt(BlockDrag drag, Point laid, double nowMs)
    {
        if (drag.Kind != BlockEdit.Move || drag.Group is { Count: > 0 } || drag.Block.CanAttachTo is not { } canAttach || drag.Block.AttachTo is null)
            return null;

        var index = LaneAt(laid.Y);
        if (index < 0 || !_lanes[index].Detailed || ReferenceEquals(_lanes[index].Row, drag.Row))
            return null;

        var lane = _lanes[index];
        var (left, right) = (drag.Block.Left, drag.Block.Left + drag.Block.Width);
        var offersNewTrack = drag.Block is { MoveToNewTrack: not null, CanMoveToNewTrack: not null };
        var alt = drag.Keys.HasFlag(ModifierKeys.Alt) && !drag.Alt;
        SeamGroup? group = null;
        foreach (var each in _seamGroups)
        {
            if (index >= each.First && index <= each.Last)
                group = each;
        }

        foreach (var parent in lane.Row.Blocks)
        {
            if (ReferenceEquals(parent, drag.Block) || parent.Family is null || (_coupledSet.Count > 0 && _coupledSet.Contains(parent)))
                continue;

            var rect = BlockRect(lane, parent, 0);
            if (laid.X < rect.Left || laid.X >= rect.Right || Math.Min(right, parent.Left + parent.Width) - Math.Max(left, parent.Left) < 1)
                continue;

            var (top, bottom) = (rect.Top, rect.Bottom);
            if (offersNewTrack && group is { } seams)
                (top, bottom) = (Math.Max(top, seams.Top + seams.Band), Math.Min(bottom, seams.Bottom - seams.Band));
            var inside = alt ? laid.Y >= top && laid.Y < bottom : laid.Y >= bottom - Math.Min(AttachBand, rect.Height / 4) && laid.Y < bottom;
            if (!inside)
                continue;
            if (!alt && !Settle.Awake(drag.Attach is { } was && ReferenceEquals(was.Block, parent), nowMs))
                return null;

            string? name = null;
            var at = drag.Block.Left;
            return Raise(() => name = canAttach(parent, at)) && name is not null ? (lane.Row, parent, name) : null;
        }

        return null;
    }

    // ----- Way 1: a track's header dragged -----

    // A header pressed on its name: held for a drag when the host lets the row be dragged (at: the control's x, the rows' y).
    private bool ArmRowDrag(TimelineRow row, Point at, double nowMs)
    {
        var may = false;
        if (CanDragRow is not { } canDrag || !Raise(() => may = canDrag(row)) || !may)
            return false;

        var pointer = new Point(at.X, at.Y - _verticalOffset);
        _rowDrag = new RowDrag { Row = row, PressX = at.X, PressY = at.Y, Pointer = pointer };
        Settle.Begin(pointer, nowMs);
        return true;
    }

    // A move of a held header (the control's own point): the speed sampled, and what it is over looked for.
    private void FollowRowDrag(Point point, double nowMs)
    {
        if (_rowDrag is not { } drag)
            return;

        Settle.Sample(point, nowMs);
        drag.Pointer = point;
        EvaluateRowDrag(drag, point, nowMs);
        if (drag.Started)
            RestSoon();
    }

    /// <summary>
    /// What a header's drag is over (5.5A). It begins once the pointer has gone past the system's drag distance from the press. Then: right
    /// of the press by <see cref="NestIndent"/> or more over the lower half of another top-level picture row's header, once settled (or
    /// still awake from before), a nest under that row; else the seam between the picture tracks nearest the pointer, at once (a seam
    /// moves a track, it makes none). Each only where the host says it may.
    /// </summary>
    private void EvaluateRowDrag(RowDrag drag, Point point, double nowMs)
    {
        var laidY = point.Y + _verticalOffset;
        var changed = false;
        if (!drag.Started)
        {
            if (Math.Abs(point.X - drag.PressX) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(laidY - drag.PressY) <= SystemParameters.MinimumVerticalDragDistance)
                return;

            drag.Started = changed = true;
            _clickOnly = null;
            ToolTip = null;
            ForgetTip();
            Skim(double.NaN);
            (_hovered, _hoveredGap) = (null, null);
            Cursor = Cursors.SizeAll;
            WatchEscape(true);
        }

        // 5.5A review: a Sub-Lane over its own family's rows (but their outer seam bands) is over nothing: let go there, nothing changes.
        var (seam, nest) = (drag.Seam, drag.Nest);
        BuildSeamGroups();
        drag.Nest = NestAt(drag, point.X, laidY, nowMs);
        drag.Seam = drag.Nest is null && !OverOwnFamily(drag, laidY) ? MaySeam(drag, RowSeamAt(laidY)) : null;
        changed |= !ReferenceEquals(nest, drag.Nest) || seam?.Beside != drag.Seam?.Beside || seam?.Above != drag.Seam?.Above || seam?.Y != drag.Seam?.Y;
        if (changed)
        {
            _headersDirty = _overlayDirty = true;
            InvalidateVisual();
        }
    }

    // The top-level picture row a dragged header would be nested under at this point, or null.
    private TimelineRow? NestAt(RowDrag drag, double x, double laidY, double nowMs)
    {
        if (x - drag.PressX < NestIndent || LaneAt(laidY) is not (>= 0 and var index))
            return null;

        var lane = _lanes[index];
        if (lane is not { Depth: 0, Row.Kind: TimelineRowKind.Video or TimelineRowKind.Empty } || ReferenceEquals(lane.Row, drag.Row) || laidY < lane.Top + lane.Height / 2
            || !Settle.Awake(ReferenceEquals(drag.Nest, lane.Row), nowMs))
        {
            return null;
        }

        if (drag.NestAsked is not { } asked || !ReferenceEquals(asked.Parent, lane.Row))
        {
            var may = false;
            if (CanNestRow is { } canNest)
                Raise(() => may = canNest(drag.Row, lane.Row));
            drag.NestAsked = asked = (lane.Row, may);
        }

        return asked.May ? lane.Row : null;
    }

    // A seam the host lets the dragged row go to (asked once a seam in a drag: nothing changes the tracks meanwhile), or null.
    private (TimelineRow Beside, bool Above, double Y)? MaySeam(RowDrag drag, (TimelineRow Beside, bool Above, double Y)? seam)
    {
        if (seam is not { } found)
            return null;

        if (drag.SeamAsked is not { } asked || !ReferenceEquals(asked.Beside, found.Beside) || asked.Above != found.Above)
        {
            var may = false;
            if (CanMoveRowTo is { } canMove)
                Raise(() => may = canMove(drag.Row, found.Beside, found.Above));
            drag.SeamAsked = asked = (found.Beside, found.Above, may);
        }

        return asked.May ? found : null;
    }

    /// <summary>
    /// The seam between the picture tracks nearest a height (the rows' y), as the "+ New Track" seams are named (the top-level picture row
    /// it goes beside, above it or below it, and where its bar is drawn): the upper half of a picture group, the seam over it; the lower
    /// half, the one under it (said as the next group's, above it); above them all, over the first; below them all, under the last.
    /// The picture groups are the caller's (<see cref="BuildSeamGroups"/>, once a look).
    /// </summary>
    private (TimelineRow Beside, bool Above, double Y)? RowSeamAt(double y)
    {
        if (_seamGroups.Count == 0)
            return null;

        for (var g = 0; g < _seamGroups.Count; g++)
        {
            var group = _seamGroups[g];
            if (y < group.Top)
                return (_lanes[group.Root].Row, true, group.Top);
            if (y >= group.Bottom + LaneGap)
                continue;

            if (y < (group.Top + group.Bottom) / 2)
                return (_lanes[group.Root].Row, true, g > 0 && _seamGroups[g - 1].Last + 1 == group.First ? (_seamGroups[g - 1].Bottom + group.Top) / 2 : group.Top);
            if (g + 1 < _seamGroups.Count)
            {
                var next = _seamGroups[g + 1];
                return (_lanes[next.Root].Row, true, next.First == group.Last + 1 ? (group.Bottom + next.Top) / 2 : group.Bottom);
            }

            return (_lanes[group.Root].Row, false, group.Bottom);
        }

        var last = _seamGroups[^1];
        return (_lanes[last.Root].Row, false, last.Bottom);
    }

    /// <summary>
    /// Whether a Sub-Lane's header drag is over its own family's rows (5.5A review: its top-level track's row and that row's Sub-Lanes, the
    /// picture group its lane is in), but the outer seam bands at the family's top and bottom edges: the neutral zone, over nothing, so that
    /// letting go where it began changes nothing (it stays in its stack, its clips linked). A top-level track's own seams are refused anyway.
    /// The picture groups are the caller's.
    /// </summary>
    private bool OverOwnFamily(RowDrag drag, double laidY)
    {
        if (LaneOf(drag.Row) is not (>= 0 and var own) || _lanes[own].Depth == 0)
            return false;

        foreach (var group in _seamGroups)
        {
            if (own >= group.First && own <= group.Last)
                return laidY >= group.Top + group.Band && laidY < group.Bottom - group.Band;
        }

        return false;
    }

    // ----- Esc during a header's drag (5.5A review) -----
    // Heard on the window while a header's drag is under way (the surface takes no keyboard focus), after the window's own keys and handled
    // ones too; hooked as the drag begins, unhooked as it ends (EndRowDrag, CancelRowDrag), never before the first drag.

    private Window? _escapeWindow;

    private void WatchEscape(bool on)
    {
        if (on && _escapeWindow is null && Window.GetWindow(this) is { } window)
        {
            _escapeWindow = window;
            window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(RowDrag_PreviewKeyDown), handledEventsToo: true);
        }
        else if (!on && _escapeWindow is { } watched)
        {
            _escapeWindow = null;
            watched.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(RowDrag_PreviewKeyDown));
        }
    }

    private void RowDrag_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && EscapeRowDrag())
            e.Handled = true;
    }

    /// <summary>
    /// Esc during a header's drag (5.5A review): a drag under way let go of as an abort is (<see cref="AbortDrag"/>: its seam bar, its nest
    /// bracket and pill gone, the mouse let go, nothing moved or nested; the drag's end told); false with none under way. Test seam.
    /// </summary>
    internal bool EscapeRowDrag()
    {
        if (_rowDrag is not { Started: true })
            return false;

        AbortDrag();
        return true;
    }

    /// <summary>
    /// A header let go (5.5A): false with no header held. Never begun, it was a click (HeaderClicked was raised as it was pressed). Nested,
    /// the new parent's layers' stack is opened (kept by its key across the rows made again) and the host asked to nest it; at a seam, to
    /// move it there. The drag's end is raised either way, so the host lays the rows out from what the timeline is now.
    /// </summary>
    private bool EndRowDrag()
    {
        if (_rowDrag is not { } drag)
            return false;

        _rowDrag = null;
        StopSettleTimer();
        WatchEscape(false);
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (!drag.Started)
            return true;

        Cursor = null;
        try
        {
            if (drag.Nest is { } nest)
            {
                if (Raise(() => RowNestRequested?.Invoke(drag.Row, nest)))
                    SetExpanded(nest, ChildStack.Visual, true);
            }
            else if (drag.Seam is { } seam)
            {
                Raise(() => RowMoveRequested?.Invoke(drag.Row, seam.Beside, seam.Above));
            }
        }
        finally
        {
            EndDrag();
        }

        return true;
    }

    // A header held or dragged let go of, nothing done (the rows made again, the mouse taken away, the control gone): its marks go.
    private void CancelRowDrag()
    {
        if (_rowDrag is not { } drag)
            return;

        _rowDrag = null;
        StopSettleTimer();
        WatchEscape(false);
        if (!drag.Started)
            return;

        Cursor = null;
        _headersDirty = _overlayDirty = true;
        InvalidateVisual();
    }

    // ----- Drawn -----

    // The pills' words, laid out once for the name they carry and the screen's DPI (ForgetWords lets them go).
    private FormattedText? _attachWords, _nestWords;
    private string? _attachWordsOf, _nestWordsOf;

    // The accent's pen for the tether and the nest's bracket: made again only when the theme's accent changes.
    private Pen? _accentPen;
    private Brush? _accentPenOf;

    private Pen AccentPen()
    {
        if (_accentPen is null || !ReferenceEquals(_accentPenOf, _accent))
        {
            var pen = new Pen(_accent, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            (_accentPen, _accentPenOf) = (pen, _accent);
        }

        return _accentPen;
    }

    private static FormattedText PillWords(ref FormattedText? kept, ref string? keptOf, string name, string lead, double pixelsPerDip)
    {
        if (kept is null || keptOf != name)
        {
            kept = new FormattedText(lead + name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, pixelsPerDip)
            {
                MaxTextWidth = 320,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            keptOf = name;
        }

        return kept;
    }

    /// <summary>
    /// A header's drag drawn over the headers (5.5A; the headers' space: the control's x, the rows' y): the dragged header shaded, and at a
    /// seam a 3 DIP accent bar across the headers, or nested an indented accent bracket on the new parent's header with a down-right arrow
    /// drawn in lines (never a font's glyph) and the pill "Make Sub-Lane of ...", which may run over the time area.
    /// </summary>
    private void DrawRowDragMarks(DrawingContext drawing, double pixelsPerDip)
    {
        if (_rowDrag is not { Started: true } drag)
            return;

        var width = _headerWidth;
        if (LaneOf(drag.Row) is >= 0 and var own)
            drawing.DrawRectangle(Translucent(_accent, 0.22), null, new Rect(0, _lanes[own].Top, width, _lanes[own].Height));

        if (drag.Seam is { } seam)
        {
            drawing.DrawRectangle(_accent, null, new Rect(0, seam.Y - 1.5, width, 3));
            return;
        }

        if (drag.Nest is not { } nest || LaneOf(nest) is not (>= 0 and var index))
            return;

        var lane = _lanes[index];
        drawing.DrawRectangle(Translucent(_accent, 0.14), null, new Rect(0, lane.Top + lane.Height / 2, width, lane.Height / 2));
        var pen = AccentPen();
        var x = 10 + NestIndent;
        var (top, bottom) = (lane.Top + lane.Height / 2, Math.Max(lane.Bottom - 6, lane.Top + lane.Height / 2 + 4));
        drawing.DrawLine(pen, new Point(x, top), new Point(x, bottom));
        drawing.DrawLine(pen, new Point(x, bottom), new Point(x + 12, bottom));
        drawing.DrawLine(pen, new Point(x + 12, bottom), new Point(x + 8, bottom - 4));
        drawing.DrawLine(pen, new Point(x + 12, bottom), new Point(x + 8, bottom + 4));

        var words = PillWords(ref _nestWords, ref _nestWordsOf, nest.Name, "Make Sub-Lane of ", pixelsPerDip);
        var box = new Rect(x + 18, bottom - (words.Height + 2) / 2, words.Width + 10, words.Height + 2);
        drawing.DrawRoundedRectangle(_accent, null, box, 3, 3);
        drawing.DrawText(words, new Point(box.Left + 5, box.Top + 1));
    }

    // A header's seam across the time area too (the overlay's space: laid-out pixels less the drawing's origin, the rows' y).
    private void DrawRowSeamBar(DrawingContext drawing, double y, double from, double to) =>
        drawing.DrawRectangle(_accent, null, new Rect(from, y - 1.5, to - from, 3));

    /// <summary>
    /// A tether (5.5A, in the overlay's space): a frozen cubic curve from the dragged block's near edge (its top when the parent is above
    /// it) to the parent's bottom edge under the pointer, a dot there, and a pill with the link glyph and "Attach as Child of ...".
    /// </summary>
    private void DrawTether(DrawingContext drawing, BlockDrag drag, (TimelineRow Row, TimelineBlock Block, string Name) attach, double from, double to, double pixelsPerDip)
    {
        if (LaneOf(drag.Row) is not (>= 0 and var childIndex) || LaneOf(attach.Row) is not (>= 0 and var parentIndex))
            return;

        var child = BlockRect(_lanes[childIndex], drag.Block, _originX);
        var parent = BlockRect(_lanes[parentIndex], attach.Block, _originX);
        var end = new Point(Math.Clamp(_lastPointer.X - _originX, parent.Left + 3, Math.Max(parent.Right - 3, parent.Left + 3)), parent.Bottom);
        var startX = Math.Clamp(child.Left + child.Width / 2, Math.Max(child.Left, from), Math.Max(Math.Min(child.Right, to), Math.Max(child.Left, from)));
        var start = new Point(startX, end.Y <= child.Top + child.Height / 2 ? child.Top : child.Bottom);
        var rise = end.Y - start.Y;

        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(start, isFilled: false, isClosed: false);
            path.BezierTo(new Point(start.X, start.Y + rise * 0.5), new Point(end.X, end.Y - rise * 0.5), end, isStroked: true, isSmoothJoin: false);
        }

        curve.Freeze();
        drawing.DrawGeometry(null, AccentPen(), curve);
        drawing.DrawEllipse(_accent, null, end, 3, 3);

        var glyph = Word("\uE71B", Brushes.White, 10, icon: true, pixelsPerDip);
        var words = PillWords(ref _attachWords, ref _attachWordsOf, attach.Name, "Attach as Child of ", pixelsPerDip);
        var (width, height) = (glyph.Width + 4 + words.Width + 12, Math.Max(glyph.Height, words.Height) + 4);
        var middle = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        var box = new Rect(Math.Clamp(middle.X + 10, from + 2, Math.Max(to - width - 2, from + 2)), middle.Y - height / 2, width, height);
        drawing.DrawRoundedRectangle(_accent, null, box, 3, 3);
        drawing.DrawText(glyph, new Point(box.Left + 6, box.Top + (box.Height - glyph.Height) / 2));
        drawing.DrawText(words, new Point(box.Left + 6 + glyph.Width + 4, box.Top + (box.Height - words.Height) / 2));
    }

    // ----- Test seams (no mouse: the point-based paths, with explicit clocks) -----

    /// <summary>A block pressed at a point on the control (its own coordinates), its drag begun as a press with the mouse begins it: for a test, without a mouse (5.4A). The clock at 0.</summary>
    internal void PressBlockAt(TimelineRow row, TimelineBlock block, Point point, ModifierKeys modifiers) => PressBlockAt(row, block, point, modifiers, 0);

    /// <summary>A block pressed at <paramref name="nowMs"/> (5.5A): as <see cref="PressBlockAt(TimelineRow, TimelineBlock, Point, ModifierKeys)"/>, the dwell begun then.</summary>
    internal void PressBlockAt(TimelineRow row, TimelineBlock block, Point point, ModifierKeys modifiers, double nowMs)
    {
        var laid = Laid(point);
        _drag = StartDrag(row, block, laid, modifiers);
        _drag.Keys = modifiers;
        _lastPointer = laid;
        Settle.Begin(point, nowMs);
        BeginDrag();
    }

    /// <summary>
    /// The pointer of a block's drag at a point on the control (<see cref="PressBlockAt(TimelineRow, TimelineBlock, Point, ModifierKeys)"/>),
    /// then at rest past the dwell (5.5A: a second after the last move, then the look again 75 ms on), so what it is over is awake as before
    /// the dwell was. False with no drag.
    /// </summary>
    internal bool MoveBlockDrag(Point point)
    {
        var at = (double.IsNaN(Settle.LastSampleMs) ? 0 : Settle.LastSampleMs) + 1000;
        if (!MoveBlockDrag(point, at, Keyboard.Modifiers))
            return false;
        SettleTick(at + DragSettle.SettleMs);
        return true;
    }

    /// <summary>The pointer of a block's drag at a point on the control at <paramref name="nowMs"/>, the keys then held (5.5A). False with no drag.</summary>
    internal bool MoveBlockDrag(Point point, double nowMs, ModifierKeys modifiers)
    {
        if (_drag is not { } drag)
            return false;
        drag.Keys = modifiers;
        _lastPointer = Laid(point);
        FollowPointer(drag, point, _lastPointer, nowMs);
        return true;
    }

    /// <summary>The clip a block's drag is tethered to now (5.5A): its family's key and the name on the pill; null for none.</summary>
    internal (object? Key, string Name)? AttachTarget => _drag?.Attach is { } attach ? (attach.Block.Family?.Key, attach.Name) : null;

    /// <summary>The key of the other row a block's drag is over now (5.5A, the deadzone); null for none.</summary>
    internal object? DragTargetKey => _drag?.Target?.Key;

    /// <summary>A header pressed at a point on the control at <paramref name="nowMs"/> (5.5A): held for a drag as a press on its name is (no capture). False when the host refuses.</summary>
    internal bool PressHeaderAt(TimelineRow row, Point point, double nowMs) => ArmRowDrag(row, new Point(point.X, Laid(point).Y), nowMs);

    /// <summary>The pointer of a held header at a point on the control at <paramref name="nowMs"/> (5.5A). False with none held.</summary>
    internal bool MoveHeaderDrag(Point point, double nowMs)
    {
        if (_rowDrag is null)
            return false;
        FollowRowDrag(point, nowMs);
        return true;
    }

    /// <summary>A held header let go (5.5A, <see cref="EndRowDrag"/>). False with none held.</summary>
    internal bool ReleaseHeaderDrag() => EndRowDrag();

    /// <summary>The seam a header's drag is over now (5.5A): the key of the row it is beside and whether above it; null for none.</summary>
    internal (object Key, bool Above)? RowSeam => _rowDrag?.Seam is { } seam ? (seam.Beside.Key, seam.Above) : null;

    /// <summary>The key of the row a header's drag would be nested under now (5.5A); null for none.</summary>
    internal object? NestTargetKey => _rowDrag?.Nest?.Key;
}
