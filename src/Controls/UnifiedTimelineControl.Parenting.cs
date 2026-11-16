using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
