using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// Keyframes pane row: All, one clip property (X, Y, Scale, Rotation, Opacity), or one animated effect value. Built by the window,
/// rendered by <see cref="KeyLanesView"/>.
/// </summary>
public sealed class KeyLane
{
    public string Name { get; init; } = "";

    /// <summary>Aggregate row: a diamond wherever any property is keyed.</summary>
    public bool IsAll { get; init; }

    /// <summary>Keyframe times in sequence seconds, ascending.</summary>
    public IReadOnlyList<double> Times { get; init; } = [];

    /// <summary>Value at a sequence time for the graph; null when there is no single value (All).</summary>
    public Func<double, double>? ValueAt { get; init; }

    /// <summary>Keyframe values aligned with <see cref="Times"/> (graph dots).</summary>
    public IReadOnlyList<double> Values { get; init; } = [];

    /// <summary>Value formatter for graph labels (unit, decimals).</summary>
    public Func<double, string> Format { get; init; } = value => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Window-side identity: a KeyProperty, an effect value reference, or null for All.</summary>
    public object? Target { get; init; }
}

/// <summary>
/// Keyframes pane lanes: one row per clip property and animated effect value, with diamonds that drag to retime, click to seek and
/// Delete to remove; the name-column diamond toggles a key at the playhead. Optionally graphs the selected row's value with export
/// easing. Mirrors the timeline's zoom/scroll and pans it when a diamond is dragged to an edge. All rendering is in OnRender with
/// arithmetic hit-testing (no element per keyframe).
/// Multi-select across rows: Ctrl+click toggles, Shift+click selects the time range from the last click across the spanned rows,
/// and a drag on empty space draws a marquee (Ctrl/Shift adds). Dragging a selected diamond moves all; Ctrl+C, Ctrl+V (at playhead),
/// Delete and the context menu act on the selection via SelectionChanged and the request events the window wires.
/// </summary>
public sealed class KeyLanesView : TimelineBar
{
    protected override string AutomationName => "Keyframe lanes";

    /// <summary>Row height and selected-row graph height in DIPs.</summary>
    public const double LaneHeight = 20, GraphHeight = 72;

    /// <summary>Left name column width; the time area fills the rest.</summary>
    public const double NameWidth = 112;

    private const double Reach = 6, EdgeZone = 24;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush SelectedRow = FrozenBrush(Color.FromArgb(40, 0x4F, 0xC3, 0xF7));
    private static readonly Pen CurvePen = Frozen(new Pen(FrozenBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), 1.5));
    private static readonly Brush MarqueeFill = FrozenBrush(Color.FromArgb(40, 0x4F, 0xC3, 0xF7));
    private static readonly Pen MarqueePen = Frozen(new Pen(FrozenBrush(Color.FromArgb(200, 0x4F, 0xC3, 0xF7)), 1));

    private IReadOnlyList<KeyLane> _lanes = [];
    private double _start, _span;
    private (double From, double To)? _clip;
    private Brush? _clipFill;
    private double _playhead = double.NaN;

    // Diamond drag: row, original time, current time, press x, and whether it passed the click-jitter threshold.
    private (int Lane, double From, double At, double PointerX, bool Moved)? _drag;
    private bool _seeking;

    // Selected keyframes as (row target, sequence seconds); _rangeFrom is the Shift+click anchor (last clicked).
    private readonly List<(object? Lane, double Time)> _selected = [];
    private (object? Lane, double Time)? _rangeFrom;

    // Marquee in progress: start point, current point, and additive mode (Ctrl or Shift held).
    private (Point From, Point To, bool Add)? _marquee;
    private Point _lastPointer;
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public KeyLanesView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        _autoScroll.Tick += AutoScroll_Tick;
    }

    /// <summary>Selected row index (graphed; target of the easing box).</summary>
    public int SelectedLane { get; private set; }

    /// <summary>Draw the value graph under the selected row.</summary>
    public bool ShowGraph { get; set; }

    /// <summary>Raised when a row is clicked.</summary>
    public event Action<KeyLane>? LaneSelected;

    /// <summary>Name-column diamond: toggle the row's keyframe at the playhead.</summary>
    public event Action<KeyLane>? KeyToggleRequested;

    /// <summary>Single diamond moved along its row: (row, from, to) in sequence seconds.</summary>
    public event Action<KeyLane, double, double>? KeyMoved;

    /// <summary>Delete with no multi-selection: remove the selected row's keyframe at the playhead.</summary>
    public event Action<KeyLane>? DeleteRequested;

    /// <summary>Seek request from a click on the time area or a diamond.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Edge auto-scroll during a diamond drag: pan the timeline by seconds.</summary>
    public event Action<double>? PanRequested;

    /// <summary>Ctrl+wheel: zoom (factor, anchor as fraction of time-area width).</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>Selection changed: (row target, sequence seconds) pairs.</summary>
    public event Action<IReadOnlyList<(object? Lane, double Time)>>? SelectionChanged;

    /// <summary>Multi-selection moved by a delta in seconds (one undo step).</summary>
    public event Action<IReadOnlyList<(object? Lane, double Time)>, double>? KeysMoved;

    /// <summary>Delete (key or menu) with a multi-selection: remove all selected keyframes.</summary>
    public event Action<IReadOnlyList<(object? Lane, double Time)>>? DeleteSelectedRequested;

    /// <summary>Ctrl+C / menu Copy: the selection, or the selected row's keyframe at the playhead when none.</summary>
    public event Action<IReadOnlyList<(object? Lane, double Time)>>? CopyRequested;

    /// <summary>Ctrl+V / menu Paste: paste copied keyframes at the playhead.</summary>
    public event Action? PasteRequested;

    /// <summary>Window-supplied predicate enabling the menu's Paste.</summary>
    public Func<bool>? CanPaste { get; set; }

    /// <summary>Current selection as (row target, sequence seconds).</summary>
    public IReadOnlyList<(object? Lane, double Time)> SelectedKeys => _selected;

    /// <summary>
    /// Shows <paramref name="lanes"/> over [<paramref name="start"/>, start + <paramref name="span"/>] with the clip span shaded and the
    /// playhead; the selected row is preserved by Target when still present.
    /// </summary>
    public void Show(IReadOnlyList<KeyLane> lanes, double start, double span, (double From, double To)? clip, Brush? clipFill, double playhead)
    {
        var selected = SelectedLane < _lanes.Count ? _lanes[SelectedLane].Target : null;
        _lanes = lanes;
        SelectedLane = 0;
        for (var i = 0; i < lanes.Count; i++)
        {
            if (Equals(lanes[i].Target, selected))
                SelectedLane = i;
        }

        (_start, _span, _clip, _clipFill) = (start, span, clip, clipFill);

        // Drop selected keys that no longer exist (clip changed, key moved or deleted).
        if (_drag is null && _selected.RemoveAll(s => !lanes.Any(l => Equals(l.Target, s.Lane) && l.Times.Any(t => Math.Abs(t - s.Time) <= Models.Layer.KeyTolerance))) > 0)
            SelectionChanged?.Invoke(_selected);
        if (_drag is null)
            InvalidateMeasure();
        MovePlayhead(playhead);
        InvalidateVisual();
    }

    /// <summary>Selected row, or null.</summary>
    public KeyLane? Selected => SelectedLane < _lanes.Count ? _lanes[SelectedLane] : null;

    public void MovePlayhead(double seconds)
    {
        _playhead = seconds;
        var x = X(seconds);
        PlayheadX = double.IsNaN(seconds) || _span <= 0 || x < NameWidth - 1 || x > ActualWidth + 1 ? double.NaN : x;
    }

    private bool HasGraph => ShowGraph && Selected is { ValueAt: not null };

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, Math.Max(_lanes.Count, 1) * LaneHeight + (HasGraph ? GraphHeight : 0));

    // ----- Geometry -----

    private double TimeWidth => Math.Max(ActualWidth - NameWidth, 1);

    private double X(double seconds) => _span > 0 ? NameWidth + (seconds - _start) / _span * TimeWidth : NameWidth;

    private double TimeAt(double x) => _start + (x - NameWidth) / TimeWidth * _span;

    /// <summary>Row top y; rows below the selected one are offset by the graph height when shown.</summary>
    private double Top(int lane) => lane * LaneHeight + (HasGraph && lane > SelectedLane ? GraphHeight : 0);

    /// <summary>Row index at y (the selected row when in its graph, with <paramref name="inGraph"/> set); -1 below all rows.</summary>
    private int LaneAt(double y, out bool inGraph)
    {
        inGraph = false;
        for (var i = 0; i < _lanes.Count; i++)
        {
            var top = Top(i);
            if (y >= top && y < top + LaneHeight)
                return i;
            if (HasGraph && i == SelectedLane && y >= top + LaneHeight && y < top + LaneHeight + GraphHeight)
            {
                inGraph = true;
                return i;
            }
        }

        return -1;
    }

    private double? KeyNear(int lane, double x)
    {
        double? nearest = null;
        foreach (var time in _lanes[lane].Times)
        {
            var at = X(time);
            if (Math.Abs(at - x) <= Reach && (nearest is null || Math.Abs(at - x) < Math.Abs(X(nearest.Value) - x)))
                nearest = time;
        }

        return nearest;
    }

    /// <summary>True if the row has a keyframe at the playhead (filled name diamond).</summary>
    private bool KeyedAtPlayhead(KeyLane lane) => lane.Times.Any(t => Math.Abs(t - _playhead) <= Models.Layer.KeyTolerance);

    // ----- Rendering -----

    protected override void OnRender(DrawingContext drawing)
    {
        // Resolve theme ink once per render (lanes redraw as the playhead moves).
        _renderInk = null;
        DrawBackground(drawing);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_lanes.Count == 0)
        {
            drawing.DrawText(Text("Select a clip: its keyframes are shown here, a row for each property.", Ink().Muted, dpi, ActualWidth - 12), new Point(6, 3));
            return;
        }

        // Shade the clip's span across all rows.
        if (_clip is { } clip && _clipFill is not null)
        {
            var (from, to) = (Math.Max(X(clip.From), NameWidth), Math.Min(X(clip.To), ActualWidth));
            if (to > from)
            {
                drawing.PushOpacity(0.35);
                drawing.DrawRectangle(_clipFill, null, new Rect(from, 0, to - from, ActualHeight));
                drawing.Pop();
            }
        }

        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            var top = Top(i);
            var middle = top + LaneHeight / 2;
            if (i == SelectedLane)
                drawing.DrawRectangle(SelectedRow, null, new Rect(0, top, ActualWidth, LaneHeight));
            drawing.DrawLine(Ink().RowLine, new Point(0, top + LaneHeight - 0.5), new Point(ActualWidth, top + LaneHeight - 0.5));

            // Row name and its toggle diamond (filled when keyed at the playhead).
            var keyed = KeyedAtPlayhead(lane);
            drawing.DrawGeometry(keyed ? HandPegApp.Services.ThemeManager.KeyFill : HandPegApp.Services.ThemeManager.HollowKeyFill, KeyOutline, Diamonds([NameWidth - 12], double.MaxValue, middle, 5));
            var name = Text(lane.Name, lane.IsAll ? Ink().Name : Ink().Muted, dpi, NameWidth - 22);
            drawing.DrawText(name, new Point(5, middle - name.Height / 2));

            // Row keyframes; selected ones and the selected row's playhead key get an outline.
            drawing.PushClip(new RectangleGeometry(new Rect(NameWidth, top, TimeWidth, LaneHeight)));
            var xs = new List<double>(lane.Times.Count);
            var chosen = new List<double>();
            foreach (var time in lane.Times)
            {
                // During a drag, offset the dragged diamond (or the whole selection) by the drag delta.
                var isSelected = IsSelected(lane.Target, time);
                var moved = _drag is { } drag && drag.Moved
                            && (drag.Lane == i && Math.Abs(time - drag.From) <= Models.Layer.KeyTolerance || isSelected && DragMovesSelection(drag));
                var x = X(moved ? time + (_drag!.Value.At - _drag.Value.From) : time);
                xs.Add(x);
                if (isSelected)
                    chosen.Add(x);
            }

            if (xs.Count > 0)
                drawing.DrawGeometry(HandPegApp.Services.ThemeManager.KeyFill, KeyOutline, Diamonds(xs, ActualWidth, middle, lane.IsAll ? 6 : 5));
            if (chosen.Count > 0)
                drawing.DrawGeometry(null, Ink().SelectedKey, Diamonds(chosen, ActualWidth, middle, 7));
            if (i == SelectedLane && keyed && !IsSelected(lane.Target, _playhead))
                drawing.DrawGeometry(null, Ink().SelectedKey, Diamonds([X(_playhead)], ActualWidth, middle, 7));
            drawing.Pop();

            if (HasGraph && i == SelectedLane)
                DrawGraph(drawing, lane, top + LaneHeight, dpi);
        }

        // Marquee rectangle.
        if (_marquee is { } box)
            drawing.DrawRectangle(MarqueeFill, MarqueePen, new Rect(box.From, box.To));

        drawing.Pop();
    }

    /// <summary>
    /// Value graph for the selected row: polyline sampled every 2 px using export easing, a dot per keyframe, and min/max labels at left.
    /// </summary>
    private void DrawGraph(DrawingContext drawing, KeyLane lane, double top, double dpi)
    {
        var area = new Rect(NameWidth, top + 6, TimeWidth, GraphHeight - 12);
        drawing.DrawRectangle(Ink().GraphFill, null, new Rect(0, top, ActualWidth, GraphHeight));
        if (lane.ValueAt is not { } valueAt || _span <= 0)
            return;

        // Y range covers keyframe values and on-screen samples (easing overshoot can exceed the keys).
        var samples = new List<Point>((int)(area.Width / 2) + 2);
        var (low, high) = (double.MaxValue, double.MinValue);
        for (var x = area.Left; x <= area.Right + 0.5; x += 2)
        {
            var value = valueAt(TimeAt(x));
            if (!double.IsFinite(value))
                continue;
            samples.Add(new Point(x, value));
            (low, high) = (Math.Min(low, value), Math.Max(high, value));
        }

        foreach (var value in lane.Values)
            (low, high) = (Math.Min(low, value), Math.Max(high, value));
        if (samples.Count < 2 || low > high)
            return;
        if (high - low < 1e-9)
            (low, high) = (low - 1, high + 1);

        double Y(double value) => area.Bottom - (value - low) / (high - low) * area.Height;
        drawing.DrawLine(Ink().Guide, new Point(area.Left, area.Top), new Point(area.Right, area.Top));
        drawing.DrawLine(Ink().Guide, new Point(area.Left, area.Bottom), new Point(area.Right, area.Bottom));

        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(new Point(samples[0].X, Y(samples[0].Y)), false, false);
            for (var k = 1; k < samples.Count; k++)
                path.LineTo(new Point(samples[k].X, Y(samples[k].Y)), true, false);
        }

        curve.Freeze();
        drawing.PushClip(new RectangleGeometry(new Rect(NameWidth, top, TimeWidth, GraphHeight)));
        drawing.DrawGeometry(null, CurvePen, curve);
        for (var k = 0; k < lane.Times.Count && k < lane.Values.Count; k++)
            drawing.DrawEllipse(HandPegApp.Services.ThemeManager.KeyFill, KeyOutline, new Point(X(lane.Times[k]), Y(lane.Values[k])), 3.5, 3.5);
        drawing.Pop();

        var highText = Text(lane.Format(high), Ink().Muted, dpi, NameWidth - 10);
        var lowText = Text(lane.Format(low), Ink().Muted, dpi, NameWidth - 10);
        drawing.DrawText(highText, new Point(NameWidth - 6 - highText.Width, area.Top - highText.Height / 2));
        drawing.DrawText(lowText, new Point(NameWidth - 6 - lowText.Width, area.Bottom - lowText.Height / 2));
    }

    private static FormattedText Text(string text, Brush brush, double dpi, double width) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11, brush, dpi)
        {
            MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(width, 4),
        };

    // ----- Pointer input -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        var lane = LaneAt(point.Y, out var inGraph);
        if (lane < 0)
            return;

        if (lane != SelectedLane)
        {
            SelectedLane = lane;
            InvalidateMeasure();
            InvalidateVisual();
            LaneSelected?.Invoke(_lanes[lane]);
        }

        // Name-column diamond: toggle the row key at the playhead.
        if (!inGraph && point.X < NameWidth)
        {
            if (point.X >= NameWidth - 20)
                KeyToggleRequested?.Invoke(_lanes[lane]);
            e.Handled = true;
            return;
        }

        if (!CaptureMouse())
            return;

        e.Handled = true;
        _lastPointer = point;
        var modifiers = Keyboard.Modifiers;
        if (!inGraph && KeyNear(lane, point.X) is { } key)
        {
            // Diamond: select (Ctrl toggles, Shift selects the range from the anchor), seek to it, and arm a drag along its row
            // (moving the whole selection when it is part of one).
            var target = _lanes[lane].Target;
            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                if (!Unselect(target, key))
                    _selected.Add((target, key));
            }
            else if (modifiers.HasFlag(ModifierKeys.Shift) && _rangeFrom is { } from)
            {
                SelectRange(from, (target, key));
            }
            else if (!IsSelected(target, key))
            {
                _selected.Clear();
                _selected.Add((target, key));
            }

            if (!modifiers.HasFlag(ModifierKeys.Shift))
                _rangeFrom = (target, key);
            SelectionChanged?.Invoke(_selected);
            _drag = (lane, key, key, point.X, false);
            SeekRequested?.Invoke(key);
            InvalidateVisual();
            return;
        }

        // Empty lane space: seek there and start a marquee (graph area: seek-drag instead).
        if (!inGraph)
            _marquee = (point, point, modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift));
        else
            _seeking = true;
        SeekRequested?.Invoke(Math.Max(TimeAt(Math.Max(point.X, NameWidth)), 0));
    }

    // ----- Selection -----

    private bool IsSelected(object? lane, double time) =>
        _selected.Any(s => Equals(s.Lane, lane) && Math.Abs(s.Time - time) <= Models.Layer.KeyTolerance);

    private bool Unselect(object? lane, double time) =>
        _selected.RemoveAll(s => Equals(s.Lane, lane) && Math.Abs(s.Time - time) <= Models.Layer.KeyTolerance) > 0;

    /// <summary>True when the dragged diamond belongs to a multi-selection (the drag moves all of it).</summary>
    private bool DragMovesSelection((int Lane, double From, double At, double PointerX, bool Moved) drag) =>
        _selected.Count > 1 && drag.Lane < _lanes.Count && IsSelected(_lanes[drag.Lane].Target, drag.From);

    /// <summary>Shift+click: adds every keyframe within the two keys' time range on their rows and all rows between.</summary>
    private void SelectRange((object? Lane, double Time) from, (object? Lane, double Time) to)
    {
        var (a, b) = (IndexOf(from.Lane), IndexOf(to.Lane));
        if (a < 0 || b < 0)
            return;

        var (first, last) = (Math.Min(a, b), Math.Max(a, b));
        var (early, late) = (Math.Min(from.Time, to.Time) - Models.Layer.KeyTolerance, Math.Max(from.Time, to.Time) + Models.Layer.KeyTolerance);
        for (var i = first; i <= last; i++)
        {
            foreach (var time in _lanes[i].Times.Where(t => t >= early && t <= late))
            {
                if (!IsSelected(_lanes[i].Target, time))
                    _selected.Add((_lanes[i].Target, time));
            }
        }
    }

    private int IndexOf(object? lane)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (Equals(_lanes[i].Target, lane))
                return i;
        }

        return -1;
    }

    /// <summary>Selects keyframes inside the marquee (rows by centre line, time by x within Reach); replaces unless additive.</summary>
    private void TakeMarquee((Point From, Point To, bool Add) box)
    {
        if (!box.Add)
            _selected.Clear();

        var area = new Rect(box.From, box.To);
        for (var i = 0; i < _lanes.Count; i++)
        {
            var middle = Top(i) + LaneHeight / 2;
            if (middle < area.Top || middle > area.Bottom)
                continue;

            foreach (var time in _lanes[i].Times)
            {
                var x = X(time);
                if (x >= area.Left - Reach && x <= area.Right + Reach && !IsSelected(_lanes[i].Target, time))
                    _selected.Add((_lanes[i].Target, time));
            }
        }

        SelectionChanged?.Invoke(_selected);
    }

    /// <summary>Ctrl+A: selects every keyframe on every row.</summary>
    public void SelectAll()
    {
        _selected.Clear();
        foreach (var lane in _lanes)
            _selected.AddRange(lane.Times.Select(t => (lane.Target, t)));
        SelectionChanged?.Invoke(_selected);
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_selected.Count == 0)
            return;

        _selected.Clear();
        SelectionChanged?.Invoke(_selected);
        InvalidateVisual();
    }

    /// <summary>Copy/Delete working set: the selection, or the selected row's keyframe at the playhead when empty.</summary>
    private List<(object? Lane, double Time)> Working() =>
        _selected.Count > 0 ? [.. _selected] : Selected is { } lane && KeyedAtPlayhead(lane) ? [(lane.Target, _playhead)] : [];

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_marquee is { } box)
        {
            _marquee = box with { To = new Point(Math.Clamp(point.X, NameWidth, ActualWidth), Math.Clamp(point.Y, 0, ActualHeight)) };
            InvalidateVisual();
            return;
        }

        if (_drag is null && !_seeking)
        {
            var lane = LaneAt(point.Y, out var inGraph);
            Cursor = lane >= 0 && !inGraph && point.X >= NameWidth && KeyNear(lane, point.X) is not null ? Cursors.SizeWE
                : point.X < NameWidth && point.X >= NameWidth - 20 && lane >= 0 && !inGraph ? Cursors.Hand : null;
            return;
        }

        _lastPointer = point;
        Follow(point);

        // Edge auto-scroll while the pointer stays in an edge zone.
        var nearEdge = point.X < NameWidth + EdgeZone || point.X > ActualWidth - EdgeZone;
        if (nearEdge && !_autoScroll.IsEnabled)
            _autoScroll.Start();
        else if (!nearEdge)
            _autoScroll.Stop();
    }

    private void Follow(Point point)
    {
        if (_drag is { } drag)
        {
            var moved = drag.Moved || Math.Abs(point.X - drag.PointerX) > 3;
            var at = Math.Max(TimeAt(Math.Clamp(point.X, NameWidth, ActualWidth)), 0);
            if (moved)
                SeekRequested?.Invoke(at);
            _drag = drag with { At = moved ? at : drag.From, Moved = moved };
            InvalidateVisual();
        }
        else if (_seeking)
        {
            SeekRequested?.Invoke(Math.Max(TimeAt(Math.Clamp(point.X, NameWidth, ActualWidth)), 0));
        }
    }

    private void AutoScroll_Tick(object? sender, EventArgs e)
    {
        if (_drag is null && !_seeking || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            _autoScroll.Stop();
            return;
        }

        // Speed scales with edge depth: up to 6% of the visible span per tick (1.5 x 4%).
        var x = _lastPointer.X;
        var depth = x < NameWidth + EdgeZone ? -(NameWidth + EdgeZone - x) : x - (ActualWidth - EdgeZone);
        var speed = Math.Clamp(depth / EdgeZone, -1.5, 1.5) * _span * 0.04;
        if (Math.Abs(speed) < 1e-6)
            return;

        PanRequested?.Invoke(speed);
        Follow(_lastPointer);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var (drag, box) = (_drag, _marquee);
        (_drag, _seeking, _marquee) = (null, false, null);
        _autoScroll.Stop();
        if (IsMouseCaptured)
            ReleaseMouseCapture();

        // Marquee beyond 3 px selects its keys; a plain click on empty space clears the selection.
        if (box is { } drawn)
        {
            if (Math.Abs(drawn.To.X - drawn.From.X) > 3 || Math.Abs(drawn.To.Y - drawn.From.Y) > 3)
                TakeMarquee(drawn);
            else if (!drawn.Add)
                ClearSelection();
        }

        // Plain click (no drag) on a member of a multi-selection: reduce the selection to it.
        if (drag is { Moved: false } click && Keyboard.Modifiers == ModifierKeys.None && _selected.Count > 1 && click.Lane < _lanes.Count
            && IsSelected(_lanes[click.Lane].Target, click.From))
        {
            _selected.Clear();
            _selected.Add((_lanes[click.Lane].Target, click.From));
            SelectionChanged?.Invoke(_selected);
        }

        if (drag is { Moved: true } done && Math.Abs(done.At - done.From) > 0.0005 && done.Lane < _lanes.Count)
        {
            if (DragMovesSelection(done))
            {
                var delta = done.At - done.From;
                var moved = _selected.ToList();
                _selected.Clear();
                _selected.AddRange(moved.Select(s => (s.Lane, Math.Max(s.Time + delta, 0))));
                KeysMoved?.Invoke(moved, delta);
                SelectionChanged?.Invoke(_selected);
            }
            else
            {
                Unselect(_lanes[done.Lane].Target, done.From);
                _selected.Add((_lanes[done.Lane].Target, Math.Max(done.At, 0)));
                KeyMoved?.Invoke(_lanes[done.Lane], done.From, done.At);
                SelectionChanged?.Invoke(_selected);
            }
        }

        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is null && !_seeking && _marquee is null)
            return;

        // Capture lost mid-drag: cancel; the diamond snaps back.
        (_drag, _seeking, _marquee) = (null, false, null);
        _autoScroll.Stop();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            base.OnMouseWheel(e);
            return;
        }

        ZoomRequested?.Invoke(Math.Pow(1.25, e.Delta / 120.0), Math.Clamp((e.GetPosition(this).X - NameWidth) / TimeWidth, 0, 1));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var control = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key is Key.Delete or Key.Back && _selected.Count > 0)
        {
            DeleteSelectedRequested?.Invoke([.. _selected]);
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && Selected is { } lane && KeyedAtPlayhead(lane))
        {
            DeleteRequested?.Invoke(lane);
            e.Handled = true;
        }
        else if (control && e.Key == Key.C)
        {
            if (Working() is { Count: > 0 } keys)
                CopyRequested?.Invoke(keys);
            e.Handled = true;
        }
        else if (control && e.Key == Key.V)
        {
            PasteRequested?.Invoke();
            e.Handled = true;
        }
        else if (control && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _selected.Count > 0)
        {
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && _lanes.Count > 0)
        {
            SelectedLane = Math.Clamp(SelectedLane + (e.Key == Key.Up ? -1 : 1), 0, _lanes.Count - 1);
            InvalidateMeasure();
            InvalidateVisual();
            LaneSelected?.Invoke(_lanes[SelectedLane]);
            e.Handled = true;
        }
    }

    // Context menu: Copy, Paste at Playhead, Delete, Select All.
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_lanes.Count == 0)
            return;

        // Right-click on an unselected diamond selects it alone, like a click.
        var point = e.GetPosition(this);
        var lane = LaneAt(point.Y, out var inGraph);
        if (lane >= 0 && !inGraph && KeyNear(lane, point.X) is { } key && !IsSelected(_lanes[lane].Target, key))
        {
            _selected.Clear();
            _selected.Add((_lanes[lane].Target, key));
            _rangeFrom = (_lanes[lane].Target, key);
            SelectionChanged?.Invoke(_selected);
            InvalidateVisual();
        }

        Focus();
        var working = Working();
        var menu = new ContextMenu { PlacementTarget = this };
        void Item(string header, string gesture, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        var count = working.Count;
        Item(count > 1 ? $"Copy {count} Keyframes" : "Copy Keyframe", "Ctrl+C", count > 0, () => CopyRequested?.Invoke(working));
        Item("Paste at Playhead", "Ctrl+V", CanPaste?.Invoke() == true, () => PasteRequested?.Invoke());
        Item(count > 1 ? $"Delete {count} Keyframes" : "Delete Keyframe", "Delete", count > 0, () =>
        {
            if (_selected.Count > 0)
                DeleteSelectedRequested?.Invoke(working);
            else if (Selected is { } row)
                DeleteRequested?.Invoke(row);
        });
        menu.Items.Add(new Separator());
        Item("Select All Keyframes", "Ctrl+A", _lanes.Any(l => l.Times.Count > 0), SelectAll);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Theme-derived ink (light on dark themes, dark on light): names, muted labels, graph band, row lines, guides and selected-key
    /// outline. Rebuilt only when the theme text colour changes.
    /// </summary>
    private sealed record LaneInk(Color Text, Brush Name, Brush Muted, Brush GraphFill, Pen RowLine, Pen Guide, Pen SelectedKey);

    private LaneInk? _ink, _renderInk;

    private LaneInk Ink() => _renderInk ??= ThemeInk();

    private LaneInk ThemeInk()
    {
        var text = (TryFindResource("TextBrush") as SolidColorBrush)?.Color ?? Colors.White;
        if (_ink is { } ink && ink.Text == text)
            return ink;

        Color Alpha(byte alpha) => Color.FromArgb(alpha, text.R, text.G, text.B);
        var muted = TryFindResource("MutedTextBrush") as Brush ?? FrozenBrush(Alpha(130));
        return _ink = new LaneInk(text, FrozenBrush(Alpha(220)), muted, FrozenBrush(Alpha(28)), Frozen(new Pen(FrozenBrush(Alpha(28)), 1)),
            Frozen(new Pen(FrozenBrush(Alpha(50)), 1) { DashStyle = DashStyles.Dash }), Frozen(new Pen(FrozenBrush(text), 1.5)));
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
