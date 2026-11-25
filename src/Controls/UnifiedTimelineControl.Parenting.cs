using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;

namespace HandPegApp;

// ----- Velocity-gated drags and child layers -----
// "+ New Track" seams and tethers activate only once the pointer settles (DragSettle: < 60 DIP/s for 75 ms, or still), never in Transit
// (> 160 DIP/s), and stay active until the next Transit. Row changes need a 6 DIP deadzone past the current row; Shift off-lane keeps time.
// Child layers: (1) a clip released on the bottom-edge band of an overlapped clip on another lane (anywhere on it with Alt mid-move) is
// tethered, drawn as a curve with "Attach as Child of ..." (the Link switch undoes it); (2) a header dragged to a seam between video tracks
// moves the track with its Sub-Lanes (a Sub-Lane leaves its stack), or dragged right onto another top-level video header's lower half
// nests it as a Sub-Lane. UI thread only; the dwell timer runs only while a drag rests in a window, and nothing is allocated before the
// first drag.
public sealed partial class UnifiedTimelineControl
{
    // DIPs: row-change deadzone, rightward header offset to nest, tether band height.
    private const double VerticalDeadzone = 6, NestIndent = 18, AttachBand = 14;

    // Pointer velocity tracker for drags (created on first drag).
    private DragSettle? _settle;

    private DragSettle Settle => _settle ??= new DragSettle();

    /// <summary>Test seam: drag clock in ms (MomentumScroller.Now when null).</summary>
    internal Func<double>? Clock { get; set; }

    private double Now() => Clock?.Invoke() ?? MomentumScroller.Now();

    // Dwell re-evaluation timer (WPF raises no moves while the pointer rests): created on a drag's first move, dropped at drag end. While a
    // single-block move rests with the button held it ticks every SettleMs at Input priority, polling modifiers (RestTick).
    private DispatcherTimer? _settleTimer;

    /// <summary>Host predicate: may this row's header be dragged (video tracks).</summary>
    public Func<TimelineRow, bool>? CanDragRow { get; set; }

    /// <summary>Host predicate: may the dragged row move to the seam beside a top-level video row (row, above); queried once per seam per drag.</summary>
    public Func<TimelineRow, TimelineRow, bool, bool>? CanMoveRowTo { get; set; }

    /// <summary>Host predicate: may the dragged row nest as a Sub-Lane of a top-level video row; queried once per row per drag.</summary>
    public Func<TimelineRow, TimelineRow, bool>? CanNestRow { get; set; }

    /// <summary>Header released at a seam: (row, adjacent top-level video row, above).</summary>
    public event Action<TimelineRow, TimelineRow, bool>? RowMoveRequested;

    /// <summary>Header released as a nest: (row, new parent top-level video row); the parent's stack is expanded.</summary>
    public event Action<TimelineRow, TimelineRow>? RowNestRequested;

    // Header drag state: row, press point (control x, row y), started flag, last pointer (control space), current seam or nest target,
    // and cached host answers.
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

    // ----- Dwell -----

    // Arms the dwell timer to re-evaluate once the pointer rests (window-hosted only, never in tests). Started once and left running
    // across moves (not restarted per move: high-rate mice send ~1000/s); the tick reschedules for the remaining dwell if moved since.
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

        // Restart only if still current: an evaluation that ended the drag has already dropped the timer.
        if (RestTick(Now(), Keyboard.Modifiers) is { } wait && ReferenceEquals(_settleTimer, timer))
        {
            timer.Interval = TimeSpan.FromMilliseconds(wait);
            timer.Start();
        }
    }

    /// <summary>
    /// Test seam: dwell tick at <paramref name="nowMs"/> with <paramref name="keys"/>. If moved recently, waits out the remaining dwell.
    /// Once rested, re-evaluates targets once with current modifiers; then, while a single-block move rests, polls modifiers every
    /// <see cref="DragSettle.SettleMs"/> and re-evaluates only on change (Alt tethers immediately, Shift keeps time off-lane).
    /// Returns ms until the next tick, or null to stop.
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

    /// <summary>Test seam: pointer at rest at <paramref name="nowMs"/>; re-evaluates drag targets (zones may activate).</summary>
    internal void SettleTick(double nowMs)
    {
        if (_rowDrag is { Started: true } rowDrag)
            EvaluateRowDrag(rowDrag, rowDrag.Pointer, nowMs);
        else if (_drag is { Kind: BlockEdit.Move, Group: not { Count: > 0 } } drag && !_dragParked)
            Follow(drag, _lastPointer, pointerMoved: true, nowMs);
    }

    // ----- Row deadzone -----

    // Lane for a move: the current row (own or target) while within the deadzone past its edges, else the open lane under the pointer
    // (thin lanes of collapsed stacks map to their open row).
    private int StickyLaneAt(BlockDrag drag, double y)
    {
        if (LaneOf(drag.Target ?? drag.Row) is >= 0 and var at && y >= _lanes[at].Top - VerticalDeadzone && y < _lanes[at].Bottom + LaneGap + VerticalDeadzone)
            return at;
        return OpenLaneOf(LaneAt(y));
    }

    // ----- Clip tether -----

    /// <summary>
    /// Tether target at <paramref name="laid"/>: a time-overlapping clip on another full-size lane, with the pointer in its bottom-edge band
    /// (above any "+ New Track" seam band) once settled or still active; with Alt pressed mid-move (not at press, which slips), anywhere on it
    /// except seam bands, immediately. The host's CanAttachTo approves and names the pill. Single-block moves only, never groups.
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

    // ----- Header drag (move or nest a track) -----

    // Header name press: arms a row drag if CanDragRow allows (at: control x, row y).
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

    // Header drag move (control space): samples velocity and re-evaluates the target.
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
    /// Header drag target, starting past the system drag distance. At least <see cref="NestIndent"/> right of the press over the lower half
    /// of another top-level video header, once settled (or still active): nest under it. Otherwise the nearest seam between video tracks,
    /// immediately (moves the track; creates none). Each requires host approval.
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

        // A Sub-Lane over its own family (inside the outer seam bands) targets nothing, so releasing there is a no-op.
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

    // Top-level video row the dragged header would nest under at this point, or null.
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

    // Seam if the host allows it (cached per seam: tracks cannot change mid-drag), or null.
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
    /// Nearest video-track seam to row y, in "+ New Track" seam form (adjacent top-level row, above/below, bar y): upper half of a group =
    /// seam above it; lower half = seam below (expressed as above the next group); before all = above first; after all = below last.
    /// Groups come from the caller (<see cref="BuildSeamGroups"/>, once per evaluation).
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
    /// True when a Sub-Lane header drag is over its own family (its top-level row and Sub-Lanes, i.e. its video group) excluding the outer
    /// seam bands: a neutral zone so releasing near the origin is a no-op (stays stacked and linked). Top-level rows' own seams are refused
    /// anyway. Groups come from the caller.
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

    // ----- Esc during header drag -----
    // Window-level PreviewKeyDown (handledEventsToo; the surface is not focusable), hooked at drag start and unhooked at end (EndRowDrag,
    // CancelRowDrag); never hooked before the first drag.

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
    /// Test seam: Esc aborts a started header drag via <see cref="AbortDrag"/> (clears seam bar, nest bracket and pill, releases capture,
    /// applies nothing, raises drag end). False when no drag is active.
    /// </summary>
    internal bool EscapeRowDrag()
    {
        if (_rowDrag is not { Started: true })
            return false;

        AbortDrag();
        return true;
    }

    /// <summary>
    /// Header release; false if none held. Unstarted = click (HeaderClicked fired on press). Nest: raises RowNestRequested and expands the
    /// parent's stack (persisted by key across rebuilds); seam: raises RowMoveRequested. Drag end is always raised so the host re-lays rows.
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

    // Cancels a header drag without applying it (row rebuild, capture loss, unload) and clears its overlays.
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

    // ----- Rendering -----

    // Pill FormattedText cached per name and DPI (released by ForgetWords).
    private FormattedText? _attachWords, _nestWords;
    private string? _attachWordsOf, _nestWordsOf;

    // Frozen accent pen for tether and nest bracket, rebuilt only when the theme accent changes.
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
    /// Header drag overlay in header space (control x, row y): shades the dragged header; at a seam, a 3 DIP accent bar; when nesting, an
    /// indented accent bracket with a line-drawn down-right arrow (no font glyph) and a "Make Sub-Lane of ..." pill that may overflow into
    /// the time area.
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

    // Header seam bar across the time area (overlay space: layout x minus drawing origin, row y).
    private void DrawRowSeamBar(DrawingContext drawing, double y, double from, double to) =>
        drawing.DrawRectangle(_accent, null, new Rect(from, y - 1.5, to - from, 3));

    /// <summary>
    /// Tether overlay (overlay space): frozen cubic Bezier from the dragged block's near edge (top if the parent is above) to the parent's
    /// bottom edge under the pointer, an end dot, and a link-glyph pill "Attach as Child of ...".
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

    // ----- Test seams (mouse-free, point-based, explicit clocks) -----

    /// <summary>Test seam: presses a block at a control-space point and starts its drag as a mouse press would, at clock 0.</summary>
    internal void PressBlockAt(TimelineRow row, TimelineBlock block, Point point, ModifierKeys modifiers) => PressBlockAt(row, block, point, modifiers, 0);

    /// <summary>As <see cref="PressBlockAt(TimelineRow, TimelineBlock, Point, ModifierKeys)"/>, starting the dwell at <paramref name="nowMs"/>.</summary>
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
    /// Test seam: moves the block drag to a control-space point 1 s after the last sample, then settles (SettleTick after SettleMs) so
    /// drop targets are active without simulating a dwell. False with no drag.
    /// </summary>
    internal bool MoveBlockDrag(Point point)
    {
        var at = (double.IsNaN(Settle.LastSampleMs) ? 0 : Settle.LastSampleMs) + 1000;
        if (!MoveBlockDrag(point, at, Keyboard.Modifiers))
            return false;
        SettleTick(at + DragSettle.SettleMs);
        return true;
    }

    /// <summary>Test seam: moves the block drag to a control-space point at <paramref name="nowMs"/> with modifiers. False with no drag.</summary>
    internal bool MoveBlockDrag(Point point, double nowMs, ModifierKeys modifiers)
    {
        if (_drag is not { } drag)
            return false;
        drag.Keys = modifiers;
        _lastPointer = Laid(point);
        FollowPointer(drag, point, _lastPointer, nowMs);
        return true;
    }

    /// <summary>Test seam: current tether target (family key, pill name); null for none.</summary>
    internal (object? Key, string Name)? AttachTarget => _drag?.Attach is { } attach ? (attach.Block.Family?.Key, attach.Name) : null;

    /// <summary>Test seam: key of the other row the block drag targets (after deadzone); null for none.</summary>
    internal object? DragTargetKey => _drag?.Target?.Key;

    /// <summary>Test seam: arms a header drag at a control-space point at <paramref name="nowMs"/> (no capture). False if the host refuses.</summary>
    internal bool PressHeaderAt(TimelineRow row, Point point, double nowMs) => ArmRowDrag(row, new Point(point.X, Laid(point).Y), nowMs);

    /// <summary>Test seam: moves the held header drag at <paramref name="nowMs"/>. False with none held.</summary>
    internal bool MoveHeaderDrag(Point point, double nowMs)
    {
        if (_rowDrag is null)
            return false;
        FollowRowDrag(point, nowMs);
        return true;
    }

    /// <summary>Test seam: releases the held header (<see cref="EndRowDrag"/>). False with none held.</summary>
    internal bool ReleaseHeaderDrag() => EndRowDrag();

    /// <summary>Test seam: current header-drag seam (adjacent row key, above); null for none.</summary>
    internal (object Key, bool Above)? RowSeam => _rowDrag?.Seam is { } seam ? (seam.Beside.Key, seam.Above) : null;

    /// <summary>Test seam: key of the row the header drag would nest under; null for none.</summary>
    internal object? NestTargetKey => _rowDrag?.Nest?.Key;
}

// ----- Apple-Plus timeline gestures (Pass 5.6.5 L3) -----
// Touchpad: 1:1 two-axis scroll (vertical rows, horizontal pan), pinch zoom, Alt heights (TouchpadWheel, fed by the window hook). Alt+wheel
// sizes the track under the pointer, Alt+Shift+wheel every track. Double-clicks: a gap ripple-closes it, a parent clip folds/unfolds its
// Sub-Lanes, a gain line auto-levels to -14 LUFS. Spring-Zoom: a 300 ms rest (or Z) during a clip, trim, fade or ducking drag magnifies the
// time area 4x about the pointer (pointer travel maps 1/4, so edits land finer) and springs back on release; trims show 25% Ghost Trim Tails
// of the unused media, and a Ghost Playhead skims the live composite at the dragged edge, snapping back on release. Hovered effects,
// crossfades and "fx N" clips get inline quick-edit icons; ducked sound shows a draggable amber ducking curve (double-click a node: reset).
public sealed partial class UnifiedTimelineControl
{
    // ----- Events -----

    /// <summary>Alt+wheel over a track that sizes alone: the row and the steps (positive = taller).</summary>
    public event Action<TimelineRow, int>? RowHeightStepped;

    /// <summary>Double-click in a gap of a row: ripple-close the gap containing these Sequence seconds.</summary>
    public event Action<TimelineRow, double>? GapDoubleClicked;

    /// <summary>
    /// Ghost Playhead during a drag: the Sequence seconds of the dragged edge (live composite skim), or NaN when the drag ends (snap back).
    /// </summary>
    public event Action<double>? GhostPlayheadMoved;

    // ----- Touchpad -----

    /// <summary>DIPs per wheel unit for 1:1 touchpad travel (a 120-unit notch = <see cref="WheelStep"/> DIPs, as a mouse notch scrolls rows).</summary>
    internal const double TouchpadDipsPerUnit = WheelStep / 120.0;

    // Fractional Alt height steps carried between touchpad deltas.
    private double _heightCarry;

    /// <summary>
    /// A wheel message the window hook classified (TouchpadWheel): touchpad gestures over the rows, at <paramref name="at"/> (control DIPs).
    /// </summary>
    internal void TakeTouchpadWheel(WheelInput input, Point at) =>
        TouchpadWheelAt(input, MomentumScroller.Now(), Math.Max(at.X - _headerWidth, 0), at.Y + _verticalOffset);

    /// <summary>
    /// Test seam: one touchpad wheel delta. Ctrl (a pinch) zooms about <paramref name="zoomAt"/> immediately (no glide: the fingers are the
    /// animation); Alt steps heights like the wheel (fractions carried); otherwise vertical scrolls rows and horizontal (or Shift) pans, both
    /// 1:1 with the fingers and without our own coast (the touchpad supplies its inertia).
    /// </summary>
    internal void TouchpadWheelAt(WheelInput input, double nowMs, double zoomAt, double laidY)
    {
        StopMomentum();
        _wheelGlide?.Stop();
        var keys = input.Keys;
        if (keys.HasFlag(ModifierKeys.Control))
        {
            var factor = Math.Pow(1.25, input.Notches);
            Raise(() => ZoomRequested?.Invoke(factor, zoomAt));
        }
        else if (keys.HasFlag(ModifierKeys.Alt))
        {
            _heightCarry += input.Delta;
            var steps = (int)(_heightCarry / TouchpadWheel.Notch);
            if (steps == 0)
                return;
            _heightCarry -= steps * TouchpadWheel.Notch;
            WheelAt(steps * TouchpadWheel.Notch, keys, nowMs, zoomAt, overTime: true, laidY);
            return;
        }
        else if (input.Horizontal || keys.HasFlag(ModifierKeys.Shift))
        {
            // Right swipe (positive horizontal) = later; Shift turns the vertical axis into time like Shift+wheel (up = earlier).
            var pixels = (input.Horizontal ? input.Delta : -input.Delta) * TouchpadDipsPerUnit;
            if (pixels != 0)
                RaisePan(pixels);
        }
        else
        {
            VerticalOffset = _verticalOffset - input.Delta * TouchpadDipsPerUnit;
        }

        FollowHeldDrag();
    }

    /// <summary>The row Alt+wheel sizes at layout <paramref name="laidY"/>: a full-size video/audio track that sizes alone; null for none.</summary>
    private TimelineRow? SizedRowAt(double laidY)
    {
        if (!double.IsFinite(laidY) || LaneAt(laidY) is not (>= 0 and var index))
            return null;
        var lane = _lanes[index];
        return !lane.Mini && lane.Row.SizedAlone && lane.Row.Kind is TimelineRowKind.Video or TimelineRowKind.Audio ? lane.Row : null;
    }

    /// <summary>Alt (no Shift or Ctrl) wheel over a sizable track: true once its height step is raised.</summary>
    private bool StepRowHeight(int delta, ModifierKeys modifiers, double laidY)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift) || modifiers.HasFlag(ModifierKeys.Control) || RowHeightStepped is null || SizedRowAt(laidY) is not { } row)
            return false;

        StopMomentum();
        var notches = (int)Math.Round(delta / 120.0);
        var steps = notches != 0 ? notches : Math.Sign(delta);
        Raise(() => RowHeightStepped?.Invoke(row, steps));
        return true;
    }

    // ----- Press, move and release hooks (called first by OnMouseLeftButtonDown, OnMouseMove and OnMouseLeftButtonUp) -----

    /// <summary>
    /// Press in the time area on lane <paramref name="index"/>: quick-edit icons, ducking nodes, then double-clicks (gain line auto-level, gap
    /// ripple close, parent fold). True when consumed.
    /// </summary>
    private bool TakePress(int index, Point laid, MouseButtonEventArgs e)
    {
        var lane = _lanes[index];
        if (!lane.Detailed || IsDragging)
            return false;

        if (QuickEditAt(laid) is { } quick)
        {
            Raise(() => quick.Block.QuickEdit?.Invoke(quick.Target, quick.Verb));
            SetQuick(null);
            return true;
        }

        var block = BlockAt(index, laid);
        if (block is { Duck: { } duck, DuckEdit: not null } && PressDuck(lane, block, duck.Keys, duck.AutoDb, laid, e.ClickCount))
            return true;

        if (e.ClickCount != 2)
            return false;

        if (block is { AutoLevel: { } level } && OnGainLine(lane, block, laid))
        {
            Raise(level);
            return true;
        }

        var scale = PixelsPerSecond;
        if (block is null && scale > 0 && GapUnder(lane, laid) >= 0)
        {
            var (row, seconds) = (lane.Row, laid.X / scale);
            Raise(() => GapDoubleClicked?.Invoke(row, seconds));
            return true;
        }

        if (block is not null && lane.Row.Children.Count > 0)
        {
            SetExpanded(lane.Row, !IsExpanded(lane.Row));
            return true;
        }

        return false;
    }

    /// <summary>Pointer move (any button): spring dwell and ghost while dragging, the ducking drag, hover strips. True when consumed.</summary>
    private bool TakeMove(Point point)
    {
        NoteSpringMotion(point);
        if (_duckDrag is { } duck)
        {
            FollowDuck(duck, Laid(point));
            return true;
        }

        NoteQuickHover(point);
        return false;
    }

    // ----- Spring-Zoom -----

    /// <summary>Magnification of a spring-zoomed drag, the pointer rest that springs it (ms), the rest radius (DIPs) and the spring (ms).</summary>
    internal const double SpringFactor = 4, SpringDwellMs = 300, SpringStillDips = 3, SpringMs = 180;

    // Time-area magnification about the anchor (control x), after the scroll shift; current, start and target factors and start tick.
    private readonly ScaleTransform _springScale = new();
    private double _springZoom = 1, _springFrom = 1, _springTarget = 1, _springAnchor;
    private long _springAt;
    private DispatcherTimer? _springTick, _springDwell;

    // Where the pointer last came to rest (control DIPs), and whether a rest is being timed.
    private Point _springRest;
    private bool _springArmed;

    /// <summary>True while the time area is magnified (or springing).</summary>
    internal bool IsSpringZoomed => _springTarget > 1 || _springZoom > 1.0001;

    /// <summary>The current magnification (1 = none).</summary>
    internal double SpringZoom => _springZoom;

    /// <summary>True during a drag that spring-zooms (Z then toggles it).</summary>
    internal bool CanSpringZoom => Springable;

    // A drag that spring-zooms: clip move, trims and roll, fade, crossfade, gain and ducking.
    private bool Springable => _drag is { Kind: not BlockEdit.Slip } || _fadeDrag is not null || _crossfadeDrag is not null || _gainDrag is not null || _duckDrag is not null;

    /// <summary>Control x to unmagnified control x (identity unless spring-zoomed): pointer travel maps 1/zoom while magnified.</summary>
    private double SpringX(double x) => _springZoom <= 1.0001 ? x : _springAnchor + (x - _springAnchor) / _springZoom;

    // Restarts the rest timer whenever the pointer leaves the rest radius; feeds the Ghost Playhead.
    private void NoteSpringMotion(Point point)
    {
        if (!Springable)
            return;

        if (!_springArmed || (point - _springRest).Length > SpringStillDips)
        {
            (_springRest, _springArmed) = (point, true);
            _springDwell ??= NewTimer(SpringDwellMs, SpringDwell_Tick);
            _springDwell.Stop();
            if (_springTarget <= 1)
                _springDwell.Start();
        }

        UpdateGhost(Laid(point));
    }

    private void SpringDwell_Tick(object? sender, EventArgs e)
    {
        _springDwell?.Stop();
        if (Springable && _springTarget <= 1 && (_drag is not { } drag || drag.Travel >= 3))
            SpringIn(_springRest.X);
    }

    /// <summary>Z during a spring-zooming drag: toggles the magnification (about the pointer when springing in). False when no such drag.</summary>
    internal bool SpringZoomKey()
    {
        if (!Springable)
            return false;

        if (_springTarget > 1)
            SpringTo(1);
        else
            SpringIn(Mouse.GetPosition(this).X);
        return true;
    }

    private void SpringIn(double x)
    {
        _springAnchor = Math.Clamp(x, _headerWidth, _headerWidth + Math.Max(TimeAreaWidth, 0));
        SpringTo(SpringFactor);
    }

    /// <summary>Drag over (EndDrag): springs back to 1x and snaps the Ghost Playhead home.</summary>
    private void SpringRelease()
    {
        _springDwell?.Stop();
        _springArmed = false;
        if (IsSpringZoomed)
            SpringTo(1);

        if (_ghostLive)
        {
            (_ghostLive, _ghostSeconds) = (false, double.NaN);
            Raise(() => GhostPlayheadMoved?.Invoke(double.NaN));
        }
    }

    private void SpringTo(double target)
    {
        (_springFrom, _springTarget, _springAt) = (_springZoom, target, Environment.TickCount64);
        _springTick ??= NewTimer(15, (_, _) => SpringStep());
        _springTick.Start();
        SpringStep();
    }

    // Ease-out cubic toward the target; the labels, skim and playhead lines (not magnified) hide while magnified.
    private void SpringStep()
    {
        var t = Math.Clamp((Environment.TickCount64 - _springAt) / SpringMs, 0, 1);
        _springZoom = _springFrom + (_springTarget - _springFrom) * (1 - Math.Pow(1 - t, 3));
        if (t >= 1)
        {
            _springZoom = _springTarget;
            _springTick?.Stop();
        }

        (_springScale.CenterX, _springScale.ScaleX) = (_springAnchor, _springZoom);
        var plain = _springZoom <= 1.0001 ? 1.0 : 0.0;
        (_labels.Opacity, _skim.Opacity, _playhead.Opacity) = (plain, plain, plain);
        _overlayDirty = true;
        InvalidateVisual();
    }

    private DispatcherTimer NewTimer(double ms, EventHandler tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += tick;
        return timer;
    }

    // ----- Ghost Playhead -----

    private double _ghostSeconds = double.NaN;
    private bool _ghostLive;

    /// <summary>Sequence seconds a drag previews at layout <paramref name="laid"/>: the moved start, the trimmed edge, the fade's edge; NaN for none.</summary>
    private double GhostSecondsAt(Point laid)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0)
            return double.NaN;

        if (_drag is { } drag)
        {
            var dx = laid.X - drag.PointerX;
            return drag.Kind switch
            {
                BlockEdit.Move or BlockEdit.TrimStart => (drag.Left + dx) / scale,
                BlockEdit.TrimEnd or BlockEdit.Roll => (drag.Left + drag.Width + dx) / scale - 0.001,
                _ => double.NaN,
            };
        }

        return _fadeDrag is not null || _crossfadeDrag is not null ? laid.X / scale : double.NaN;
    }

    private void UpdateGhost(Point laid)
    {
        var seconds = GhostSecondsAt(laid);
        if (double.IsNaN(seconds) || (_drag is { } drag && drag.Travel < 3))
            return;

        seconds = Math.Max(seconds, 0);
        if (_ghostLive && Math.Abs(seconds - _ghostSeconds) < 1e-4)
            return;

        (_ghostSeconds, _ghostLive) = (seconds, true);
        _overlayDirty = true;
        InvalidateVisual();
        Raise(() => GhostPlayheadMoved?.Invoke(seconds));
    }

    // ----- Ducking curve -----

    // Ducking node drag: row, block, the keys being shaped (clip seconds, dB), the dragged index, the keys before, and whether it changed.
    private sealed class DuckDrag
    {
        public required TimelineRow Row { get; init; }
        public required TimelineBlock Block { get; init; }
        public required List<Keyframe> Keys { get; init; }
        public required IReadOnlyList<Keyframe> Before { get; init; }
        public int Index { get; set; }
        public bool Changed { get; set; }
    }

    private DuckDrag? _duckDrag;

    // Hovered ducked block whose curve shows.
    private (TimelineRow Row, TimelineBlock Block)? _duckShown;

    private const double DuckNodeReach = 6, DuckLineReach = 4;

    /// <summary>Curve y of a reduction (0 dB at the top, Layer.DuckOverrideFloorDb at the bottom) in <paramref name="rect"/>.</summary>
    internal static double DuckY(Rect rect, double db) =>
        rect.Top + 3 + Math.Clamp(db / Layer.DuckOverrideFloorDb, 0, 1) * Math.Max(rect.Height - 6, 1);

    /// <summary>Reduction (dB, rounded to 0.1) at curve y in <paramref name="rect"/>.</summary>
    internal static double DuckDbAt(Rect rect, double y) =>
        Math.Round(Math.Clamp((y - rect.Top - 3) / Math.Max(rect.Height - 6, 1), 0, 1) * Layer.DuckOverrideFloorDb, 1);

    /// <summary>The ducking curve's reduction (dB) at clip seconds: the eased keys from first to last, the automatic depth elsewhere.</summary>
    internal static double DuckAt(IReadOnlyList<Keyframe> keys, double autoDb, double clipSeconds) =>
        keys.Count > 0 && clipSeconds >= keys[0].Time && clipSeconds <= keys[^1].Time
            ? Math.Clamp(EasingCurves.ValueAt(keys, clipSeconds), Layer.DuckOverrideFloorDb, 0)
            : autoDb;

    // Press on a ducked block: a node (double-click removes it; otherwise drags it) or the curve (adds a node there and drags it).
    private bool PressDuck(in Lane lane, TimelineBlock block, IReadOnlyList<Keyframe> keys, double autoDb, Point laid, int clicks)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0 || _duckShown is not { } shown || !ReferenceEquals(shown.Block, block))
            return false;

        var rect = BlockRect(lane, block, 0);
        for (var i = 0; i < keys.Count; i++)
        {
            var node = new Point(rect.Left + keys[i].Time * scale, DuckY(rect, keys[i].Value));
            if ((node - laid).Length > DuckNodeReach)
                continue;

            if (clicks >= 2)
            {
                var without = keys.Where((_, k) => k != i).ToList();
                Raise(() => block.DuckEdit?.Invoke(without, true));
                return true;
            }

            return StartDuckDrag(lane.Row, block, keys, [.. keys], i);
        }

        var seconds = (laid.X - rect.Left) / scale;
        if (seconds < 0 || laid.X > rect.Right || Math.Abs(DuckY(rect, DuckAt(keys, autoDb, seconds)) - laid.Y) > DuckLineReach)
            return false;

        // A new node keeps the shape (its value is the curve's there); ties keep the first.
        var added = new Keyframe(Math.Round(seconds, 3), DuckAt(keys, autoDb, seconds));
        var shaped = keys.ToList();
        var at = shaped.FindIndex(k => k.Time > added.Time);
        at = at < 0 ? shaped.Count : at;
        shaped.Insert(at, added);
        if (!StartDuckDrag(lane.Row, block, keys, shaped, at))
            return false;

        _duckDrag!.Changed = true;
        Raise(() => block.DuckEdit?.Invoke(shaped.ToList(), false));
        return true;
    }

    private bool StartDuckDrag(TimelineRow row, TimelineBlock block, IReadOnlyList<Keyframe> before, List<Keyframe> keys, int index)
    {
        if (!CaptureMouse())
            return false;

        _duckDrag = new DuckDrag { Row = row, Block = block, Keys = keys, Before = before, Index = index };
        BeginDrag();
        return true;
    }

    // Moves the dragged node: time between its neighbours (and within the clip), reduction from the pointer height; live edit.
    private void FollowDuck(DuckDrag drag, Point laid)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0 || LaneOf(drag.Row) is not (>= 0 and var index))
            return;

        var rect = BlockRect(_lanes[index], drag.Block, 0);
        var keys = drag.Keys;
        var (low, high) = (drag.Index > 0 ? keys[drag.Index - 1].Time + 0.01 : 0, drag.Index < keys.Count - 1 ? keys[drag.Index + 1].Time - 0.01 : rect.Width / scale);
        var time = Math.Round(Math.Clamp((laid.X - rect.Left) / scale, Math.Min(low, high), Math.Max(low, high)), 3);
        var moved = keys[drag.Index] with { Time = time, Value = DuckDbAt(rect, laid.Y) };
        if (moved == keys[drag.Index])
            return;

        keys[drag.Index] = moved;
        drag.Changed = true;
        _overlayDirty = true;
        InvalidateVisual();
        Raise(() => drag.Block.DuckEdit?.Invoke(keys.ToList(), false));
    }

    /// <summary>Release (or capture lost) of a ducking drag: commits the shaped keys. False when none was under way.</summary>
    private bool EndDuckDrag(bool keep = true)
    {
        if (_duckDrag is not { } drag)
            return false;

        _duckDrag = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (drag.Changed)
        {
            var keys = keep ? drag.Keys.ToList() : drag.Before.ToList();
            Raise(() => drag.Block.DuckEdit?.Invoke(keys, true));
        }

        EndDrag();
        return true;
    }

    // ----- Quick-edit icons -----

    private (TimelineRow Row, TimelineBlock Block, QuickEditTarget Target)? _quickShown;

    private static readonly QuickEditVerb[] AllVerbs = [QuickEditVerb.Edit, QuickEditVerb.Keyframes, QuickEditVerb.Toggle, QuickEditVerb.Remove];
    private static readonly QuickEditVerb[] TransitionVerbs = [QuickEditVerb.Edit, QuickEditVerb.Remove];

    private const double QuickIcon = 16, QuickGap = 2, QuickPad = 2;

    private static QuickEditVerb[] VerbsOf(QuickEditTarget target) => target == QuickEditTarget.Transition ? TransitionVerbs : AllVerbs;

    /// <summary>
    /// The icon strip of a target (drawing x = 0 at layout <paramref name="originX"/>): centred under a crossfade box, mid-right on an effect
    /// block, bottom-right on a clip with effects; empty when the block is too small.
    /// </summary>
    private Rect QuickStrip(in Lane lane, TimelineBlock block, QuickEditTarget target, double originX)
    {
        var count = VerbsOf(target).Length;
        var (width, height) = (count * QuickIcon + (count - 1) * QuickGap + 2 * QuickPad, QuickIcon + 2 * QuickPad);
        if (target == QuickEditTarget.Transition)
        {
            var box = CrossfadeBox(lane, block, PixelsPerSecond, originX);
            // Overlaps the box by a pixel, so the pointer never crosses a gap (which would hide the strip) on its way down or up.
            var y = box.Bottom - 1 + height <= lane.Bottom ? box.Bottom - 1 : Math.Max(lane.Top, box.Top - height + 1);
            return new Rect(box.Left + box.Width / 2 - width / 2, y, width, height);
        }

        var rect = BlockRect(lane, block, originX);
        if (rect.Width < width + 8 || rect.Height < height)
            return Rect.Empty;
        var top = target == QuickEditTarget.Effect ? rect.Top + (rect.Height - height) / 2 : rect.Bottom - height - 2;
        return new Rect(rect.Right - width - 4, top, width, height);
    }

    /// <summary>Icon under layout point <paramref name="laid"/> of the shown strip; null for none.</summary>
    private (TimelineBlock Block, QuickEditTarget Target, QuickEditVerb Verb)? QuickEditAt(Point laid)
    {
        if (_quickShown is not { } shown || LaneOf(shown.Row) is not (>= 0 and var index))
            return null;

        var strip = QuickStrip(_lanes[index], shown.Block, shown.Target, 0);
        if (strip.IsEmpty || !strip.Contains(laid))
            return null;

        var verbs = VerbsOf(shown.Target);
        var slot = (int)Math.Clamp((laid.X - strip.Left - QuickPad) / (QuickIcon + QuickGap), 0, verbs.Length - 1);
        return (shown.Block, shown.Target, verbs[slot]);
    }

    // Hover: the strip of the crossfade or quick-editable block under the pointer (kept while on the strip), and the ducked block's curve.
    private void NoteQuickHover(Point point)
    {
        (TimelineRow, TimelineBlock, QuickEditTarget)? quick = null;
        (TimelineRow, TimelineBlock)? duck = null;
        if (!IsDragging && point.X >= _headerWidth && Laid(point) is var laid && LaneAt(laid.Y) is >= 0 and var index && _lanes[index].Detailed)
        {
            var lane = _lanes[index];
            if (_quickShown is { } shown && ReferenceEquals(shown.Row, lane.Row) && QuickStrip(lane, shown.Block, shown.Target, 0).Contains(laid))
                quick = shown;
            else if (CrossfadeAt(lane, laid) is { } fade && fade.Block is { CrossfadeEdit: not null, QuickEdit: not null })
                quick = (lane.Row, fade.Block, QuickEditTarget.Transition);
            else if (BlockAt(index, laid) is { QuickEdit: not null, QuickEditOf: not QuickEditTarget.None } block)
                quick = (lane.Row, block, block.QuickEditOf);

            if (BlockAt(index, laid) is { Duck: not null, DuckEdit: not null } ducked)
                duck = (lane.Row, ducked);
        }

        if (!Equals(quick, _quickShown) || !Equals(duck, _duckShown))
        {
            (_quickShown, _duckShown) = (quick, duck);
            _overlayDirty = true;
            InvalidateVisual();
        }
    }

    private void SetQuick((TimelineRow, TimelineBlock, QuickEditTarget)? quick)
    {
        _quickShown = quick;
        _overlayDirty = true;
        InvalidateVisual();
    }

    // ----- Overlay drawing (RenderOverlay) -----

    private static readonly Brush DuckAmber = Tint(255, 0xFF, 0xB0, 0x20);
    private static readonly Pen DuckLine = Ice(new Pen(DuckAmber, 1.6));
    private static readonly Pen DuckAutoLine = Ice(new Pen(Tint(170, 0xFF, 0xB0, 0x20), 1.2) { DashStyle = DashStyles.Dash });
    private static readonly Pen DuckNodeEdge = Ice(new Pen(Tint(255, 0x1E, 0x1E, 0x22), 1.2));
    private static readonly Brush QuickFill = Tint(235, 0x1E, 0x1E, 0x22);
    private static readonly Pen QuickEdge = Ice(new Pen(Tint(255, 0x2A, 0x2A, 0x30), 1));
    private static readonly Pen QuickGlyph = Ice(new Pen(Tint(255, 0xE6, 0xE6, 0xEA), 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen QuickGlyphOff = Ice(new Pen(Tint(150, 0xE6, 0xE6, 0xEA), 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen GhostPlayheadLine = Ice(new Pen(Tint(220, 0xFF, 0xFF, 0xFF), 1.2) { DashStyle = DashStyles.Dot });
    private static readonly Geometry KeyGlyph = Ice(Geometry.Parse("M 0,-4.5 L 4.5,0 L 0,4.5 L -4.5,0 Z"));
    private static readonly Geometry PowerGlyph = Ice(Geometry.Parse("M -3,-2.8 A 4.3,4.3 0 1 0 3,-2.8 M 0,-5 L 0,-0.5"));

    /// <summary>Ghost Trim Tails, the ducking curve, the quick-edit strip and the Ghost Playhead (drawing x = 0 at _originX).</summary>
    private void DrawGestures(DrawingContext drawing, double scale, double from, double to)
    {
        if (scale <= 0)
            return;

        DrawGhostTails(drawing, scale, from, to);
        if (_duckDrag is { } duck && LaneOf(duck.Row) is >= 0 and var duckLane)
            DrawDuck(drawing, _lanes[duckLane], duck.Block, duck.Keys, duck.Block.Duck?.AutoDb ?? 0, scale, _originX);
        else if (_duckShown is { } shown && LaneOf(shown.Row) is >= 0 and var shownLane && shown.Block.Duck is { } ducking)
            DrawDuck(drawing, _lanes[shownLane], shown.Block, ducking.Keys, ducking.AutoDb, scale, _originX);

        if (!IsDragging && _quickShown is { } quick && LaneOf(quick.Row) is >= 0 and var quickLane)
            DrawQuickStrip(drawing, _lanes[quickLane], quick.Block, quick.Target);

        if (IsDragging && _ghostLive && _lanes.Count > 0)
        {
            var x = _ghostSeconds * scale - _originX;
            drawing.DrawLine(GhostPlayheadLine, new Point(x, 0), new Point(x, _lanes[^1].Bottom));
        }
    }

    // 25% tails of the unused media beyond the trimmed edge (a roll's too), clipped to the drawn range.
    private void DrawGhostTails(DrawingContext drawing, double scale, double from, double to)
    {
        if (_drag is not { Kind: BlockEdit.TrimStart or BlockEdit.TrimEnd or BlockEdit.Roll } drag || LaneOf(drag.Row) is not (>= 0 and var index))
            return;

        var rect = BlockRect(_lanes[index], drag.Block, _originX);
        var (left, right) = drag.Kind == BlockEdit.TrimStart
            ? (rect.Left - drag.Block.HeadRoom * scale, rect.Left)
            : (rect.Right, rect.Right + drag.Block.TailRoom * scale);
        (left, right) = (Math.Max(left, from - _originX - 4), Math.Min(right, to - _originX + 4));
        if (right - left < 1)
            return;

        drawing.PushOpacity(0.25);
        drawing.DrawRoundedRectangle(drag.Block.Fill, null, new Rect(left, rect.Top, right - left, rect.Height), 3, 3);
        drawing.Pop();
    }

    private static void DrawDuck(DrawingContext drawing, in Lane lane, TimelineBlock block, IReadOnlyList<Keyframe> keys, double autoDb, double scale, double originX)
    {
        var rect = BlockRect(lane, block, originX);
        var auto = DuckY(rect, autoDb);
        if (keys.Count == 0)
        {
            drawing.DrawLine(DuckAutoLine, new Point(rect.Left, auto), new Point(rect.Right, auto));
            return;
        }

        var (first, last) = (rect.Left + keys[0].Time * scale, rect.Left + keys[^1].Time * scale);
        if (first > rect.Left)
            drawing.DrawLine(DuckAutoLine, new Point(rect.Left, auto), new Point(Math.Min(first, rect.Right), auto));
        if (last < rect.Right)
            drawing.DrawLine(DuckAutoLine, new Point(Math.Max(last, rect.Left), auto), new Point(rect.Right, auto));
        var shown = rect;

        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            var started = false;
            for (var x = Math.Max(first, rect.Left); ; x = Math.Min(x + 3, last))
            {
                var point = new Point(x, DuckY(shown, DuckAt(keys, autoDb, (x - rect.Left) / scale)));
                if (started)
                    context.LineTo(point, true, false);
                else
                    context.BeginFigure(point, false, false);
                started = true;
                if (x >= last)
                    break;
            }
        }

        curve.Freeze();
        drawing.DrawGeometry(null, DuckLine, curve);
        foreach (var key in keys)
            drawing.DrawEllipse(DuckAmber, DuckNodeEdge, new Point(rect.Left + key.Time * scale, DuckY(shown, key.Value)), 3.5, 3.5);
    }

    private void DrawQuickStrip(DrawingContext drawing, in Lane lane, TimelineBlock block, QuickEditTarget target)
    {
        var strip = QuickStrip(lane, block, target, _originX);
        if (strip.IsEmpty)
            return;

        drawing.DrawRoundedRectangle(QuickFill, QuickEdge, strip, 4, 4);
        var verbs = VerbsOf(target);
        for (var i = 0; i < verbs.Length; i++)
        {
            var centre = new Point(strip.Left + QuickPad + i * (QuickIcon + QuickGap) + QuickIcon / 2, strip.Top + strip.Height / 2);
            DrawVerb(drawing, verbs[i], centre, verbs[i] != QuickEditVerb.Toggle || block.IsOn != false);
        }
    }

    // Vector glyphs: pencil, diamond, power, cross.
    private static void DrawVerb(DrawingContext drawing, QuickEditVerb verb, Point c, bool on)
    {
        var pen = on ? QuickGlyph : QuickGlyphOff;
        switch (verb)
        {
            case QuickEditVerb.Edit:
                drawing.DrawLine(pen, new Point(c.X - 4, c.Y + 4), new Point(c.X + 3, c.Y - 3));
                drawing.DrawLine(pen, new Point(c.X + 1.5, c.Y - 4.5), new Point(c.X + 4.5, c.Y - 1.5));
                drawing.DrawLine(pen, new Point(c.X - 4.5, c.Y + 4.5), new Point(c.X - 2.5, c.Y + 4.5));
                break;
            case QuickEditVerb.Keyframes:
                drawing.PushTransform(new TranslateTransform(c.X, c.Y));
                drawing.DrawGeometry(null, pen, KeyGlyph);
                drawing.Pop();
                break;
            case QuickEditVerb.Toggle:
                drawing.PushTransform(new TranslateTransform(c.X, c.Y + 0.5));
                drawing.DrawGeometry(null, pen, PowerGlyph);
                drawing.Pop();
                break;
            default:
                drawing.DrawLine(pen, new Point(c.X - 3.5, c.Y - 3.5), new Point(c.X + 3.5, c.Y + 3.5));
                drawing.DrawLine(pen, new Point(c.X - 3.5, c.Y + 3.5), new Point(c.X + 3.5, c.Y - 3.5));
                break;
        }
    }

    private static SolidColorBrush Tint(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static T Ice<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
