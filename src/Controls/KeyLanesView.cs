using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

/// <summary>
/// One row of the Keyframes pane: everything at once (All), one property of the clip (X, Y, Scale, Rotation,
/// Opacity), or one animated value of one of its effects. The window makes them; the view draws them.
/// </summary>
public sealed class KeyLane
{
    public string Name { get; init; } = "";

    /// <summary>The row of every keyframe: its diamonds stand for whatever is keyed at that moment.</summary>
    public bool IsAll { get; init; }

    /// <summary>Its keyframes, in seconds of the sequence, ascending.</summary>
    public IReadOnlyList<double> Times { get; init; } = [];

    /// <summary>Its value at a moment of the sequence, for the graph; null for a row with no single value (All).</summary>
    public Func<double, double>? ValueAt { get; init; }

    /// <summary>Its keyframes' values, in the order of <see cref="Times"/>, for the graph's dots.</summary>
    public IReadOnlyList<double> Values { get; init; } = [];

    /// <summary>How its values are written beside the graph (a unit, decimals).</summary>
    public Func<double, string> Format { get; init; } = value => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>What it stands for, for the window: a KeyProperty, an effect's value, or null for All.</summary>
    public object? Target { get; init; }
}

/// <summary>
/// The Keyframes pane's lanes: a row per property of the selected clip (and per animated effect value), each with its
/// own diamonds, which are dragged along the row to move a keyframe in time, clicked to go to it, and deleted with
/// Delete; the diamond at a row's name keys that property at the playhead (or takes its keyframe there away). Under
/// the selected row, when switched on, the graph of its value over time, eased as the export eases it. It shows the
/// time bars' stretch of the sequence (their zoom and scroll), and scrolls them when a keyframe is dragged to an edge.
/// Everything is drawn in OnRender and found by arithmetic: no element per keyframe.
/// Since 4.1.1-B, several keyframes can be selected, on one row or several: Ctrl+click adds or takes one away,
/// Shift+click takes every keyframe between the last one clicked and this one (on those rows and the rows between),
/// and a drag on empty lane space draws a box round the ones to take (with Ctrl or Shift: added to those already
/// selected). Dragging one of them moves them all; Ctrl+C, Ctrl+V (at the playhead), Delete and the right-click
/// menu work on them all, as the window wires them (SelectionChanged and the requests).
/// </summary>
public sealed class KeyLanesView : TimelineBar
{
    protected override string AutomationName => "Keyframe lanes";

    /// <summary>How tall a row is, and the graph under the selected one.</summary>
    public const double LaneHeight = 20, GraphHeight = 72;

    /// <summary>The names' column, at the left; the time runs across what is right of it.</summary>
    public const double NameWidth = 112;

    private const double Reach = 6, EdgeZone = 24;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush NameBrush = FrozenBrush(Color.FromArgb(220, 255, 255, 255));
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromArgb(130, 255, 255, 255));
    private static readonly Brush SelectedRow = FrozenBrush(Color.FromArgb(40, 0x4F, 0xC3, 0xF7));
    private static readonly Brush GraphFill = FrozenBrush(Color.FromArgb(28, 255, 255, 255));
    private static readonly Pen RowLine = Frozen(new Pen(FrozenBrush(Color.FromArgb(28, 255, 255, 255)), 1));
    private static readonly Pen CurvePen = Frozen(new Pen(FrozenBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), 1.5));
    private static readonly Pen GuidePen = Frozen(new Pen(FrozenBrush(Color.FromArgb(50, 255, 255, 255)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen SelectedKeyPen = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Brush MarqueeFill = FrozenBrush(Color.FromArgb(40, 0x4F, 0xC3, 0xF7));
    private static readonly Pen MarqueePen = Frozen(new Pen(FrozenBrush(Color.FromArgb(200, 0x4F, 0xC3, 0xF7)), 1));

    private IReadOnlyList<KeyLane> _lanes = [];
    private double _start, _span;
    private (double From, double To)? _clip;
    private Brush? _clipFill;
    private double _playhead = double.NaN;

    // A diamond being dragged: its row, where it was, where it is now, and whether it has moved past a click's wobble.
    private (int Lane, double From, double At, double PointerX, bool Moved)? _drag;
    private bool _seeking;

    // The keyframes selected (4.1.1-B): each a row's target and its moment in seconds of the sequence. The one last
    // clicked is where a Shift+click's range starts.
    private readonly List<(object? Lane, double Time)> _selected = [];
    private (object? Lane, double Time)? _rangeFrom;

    // A box being drawn round keyframes on empty lane space: where it started, where the pointer is, and whether the
    // keyframes already selected stay (Ctrl or Shift held).
    private (Point From, Point To, bool Add)? _marquee;
    private Point _lastPointer;
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public KeyLanesView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        _autoScroll.Tick += AutoScroll_Tick;
    }

    /// <summary>The selected row (its index), whose graph is drawn and whose keyframe the easing box changes.</summary>
    public int SelectedLane { get; private set; }

    /// <summary>Whether the graph is drawn under the selected row.</summary>
    public bool ShowGraph { get; set; }

    /// <summary>A row was chosen (clicked).</summary>
    public event Action<KeyLane>? LaneSelected;

    /// <summary>The diamond at a row's name: key that row at the playhead, or take its keyframe there away.</summary>
    public event Action<KeyLane>? KeyToggleRequested;

    /// <summary>A diamond was let go somewhere else on its row: from one moment (seconds of the sequence) to another.</summary>
    public event Action<KeyLane, double, double>? KeyMoved;

    /// <summary>Delete with a keyframe selected (the playhead on one of the selected row's).</summary>
    public event Action<KeyLane>? DeleteRequested;

    /// <summary>The playhead to this moment (a click on the time, or on a diamond).</summary>
    public event Action<double>? SeekRequested;

    /// <summary>A diamond dragged to an edge: the time bars are moved along by so many seconds.</summary>
    public event Action<double>? PanRequested;

    /// <summary>Ctrl + wheel: the time bars zoom by a factor about a point (a fraction of the time's width).</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>The keyframes selected changed (4.1.1-B): each a row's target and its moment in seconds of the sequence.</summary>
    public event Action<IReadOnlyList<(object? Lane, double Time)>>? SelectionChanged;

    /// <summary>Several selected keyframes dragged together, by so many seconds (one undo step).</summary>
    public event Action<IReadOnlyList<(object? Lane, double Time)>, double>? KeysMoved;

    /// <summary>Delete (or the menu's Delete) with several keyframes selected: every one of them goes.</summary>
    public event Action<IReadOnlyList<(object? Lane, double Time)>>? DeleteSelectedRequested;

    /// <summary>Ctrl+C (or the menu's Copy): the selected keyframes (or, with none, the selected row's at the playhead) are copied.</summary>
    public event Action<IReadOnlyList<(object? Lane, double Time)>>? CopyRequested;

    /// <summary>Ctrl+V (or the menu's Paste): the copied keyframes go in at the playhead.</summary>
    public event Action? PasteRequested;

    /// <summary>Whether there are keyframes copied to paste, for the menu (set by the window).</summary>
    public Func<bool>? CanPaste { get; set; }

    /// <summary>The keyframes selected, each a row's target and its moment in seconds of the sequence.</summary>
    public IReadOnlyList<(object? Lane, double Time)> SelectedKeys => _selected;

    /// <summary>
    /// Shows rows over a stretch of the sequence (<paramref name="start"/>, <paramref name="span"/> seconds across), the
    /// clip's own stretch shaded, and the playhead. The selected row is kept by its target when it is still there.
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

        // A selected keyframe that is no longer there (another clip, a keyframe moved or deleted) is let go.
        if (_drag is null && _selected.RemoveAll(s => !lanes.Any(l => Equals(l.Target, s.Lane) && l.Times.Any(t => Math.Abs(t - s.Time) <= Models.Layer.KeyTolerance))) > 0)
            SelectionChanged?.Invoke(_selected);
        if (_drag is null)
            InvalidateMeasure();
        MovePlayhead(playhead);
        InvalidateVisual();
    }

    /// <summary>The selected row, or null while there is none.</summary>
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

    // ----- Where things are -----

    private double TimeWidth => Math.Max(ActualWidth - NameWidth, 1);

    private double X(double seconds) => _span > 0 ? NameWidth + (seconds - _start) / _span * TimeWidth : NameWidth;

    private double TimeAt(double x) => _start + (x - NameWidth) / TimeWidth * _span;

    /// <summary>The top of a row: the graph sits under the selected one and pushes the rest down.</summary>
    private double Top(int lane) => lane * LaneHeight + (HasGraph && lane > SelectedLane ? GraphHeight : 0);

    /// <summary>The row under a height, or -1 (the graph, or below the rows).</summary>
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

    /// <summary>Whether a row has a keyframe at the playhead (its name's diamond is filled).</summary>
    private bool KeyedAtPlayhead(KeyLane lane) => lane.Times.Any(t => Math.Abs(t - _playhead) <= Models.Layer.KeyTolerance);

    // ----- Drawing -----

    protected override void OnRender(DrawingContext drawing)
    {
        DrawBackground(drawing);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_lanes.Count == 0)
        {
            drawing.DrawText(Text("Select a clip: its keyframes are shown here, a row for each property.", MutedBrush, dpi, ActualWidth - 12), new Point(6, 3));
            return;
        }

        // The clip's own stretch, shaded across every row.
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
            drawing.DrawLine(RowLine, new Point(0, top + LaneHeight - 0.5), new Point(ActualWidth, top + LaneHeight - 0.5));

            // The name, with the diamond that keys the row at the playhead: filled when it has a keyframe there.
            var keyed = KeyedAtPlayhead(lane);
            drawing.DrawGeometry(keyed ? HandPegApp.Services.ThemeManager.KeyFill : HandPegApp.Services.ThemeManager.HollowKeyFill, KeyOutline, Diamonds([NameWidth - 12], double.MaxValue, middle, 5));
            var name = Text(lane.Name, lane.IsAll ? NameBrush : MutedBrush, dpi, NameWidth - 22);
            drawing.DrawText(name, new Point(5, middle - name.Height / 2));

            // The row's keyframes, the one at the playhead on the selected row outlined in white.
            drawing.PushClip(new RectangleGeometry(new Rect(NameWidth, top, TimeWidth, LaneHeight)));
            var xs = new List<double>(lane.Times.Count);
            var chosen = new List<double>();
            foreach (var time in lane.Times)
            {
                // Dragged: the diamond itself, or every selected one with it, moved by as much.
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
                drawing.DrawGeometry(null, SelectedKeyPen, Diamonds(chosen, ActualWidth, middle, 7));
            if (i == SelectedLane && keyed && !IsSelected(lane.Target, _playhead))
                drawing.DrawGeometry(null, SelectedKeyPen, Diamonds([X(_playhead)], ActualWidth, middle, 7));
            drawing.Pop();

            if (HasGraph && i == SelectedLane)
                DrawGraph(drawing, lane, top + LaneHeight, dpi);
        }

        // The box being drawn round keyframes.
        if (_marquee is { } box)
            drawing.DrawRectangle(MarqueeFill, MarqueePen, new Rect(box.From, box.To));

        drawing.Pop();
    }

    /// <summary>
    /// The selected row's value over the stretch shown: a line through its value every two pixels (the easing between
    /// keyframes as the export works it out), a dot at each keyframe, and its lowest and highest values at the left.
    /// </summary>
    private void DrawGraph(DrawingContext drawing, KeyLane lane, double top, double dpi)
    {
        var area = new Rect(NameWidth, top + 6, TimeWidth, GraphHeight - 12);
        drawing.DrawRectangle(GraphFill, null, new Rect(0, top, ActualWidth, GraphHeight));
        if (lane.ValueAt is not { } valueAt || _span <= 0)
            return;

        // The range drawn: every keyframe's value, and what lies between them on screen (an overshoot runs past them).
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
        drawing.DrawLine(GuidePen, new Point(area.Left, area.Top), new Point(area.Right, area.Top));
        drawing.DrawLine(GuidePen, new Point(area.Left, area.Bottom), new Point(area.Right, area.Bottom));

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

        var highText = Text(lane.Format(high), MutedBrush, dpi, NameWidth - 10);
        var lowText = Text(lane.Format(low), MutedBrush, dpi, NameWidth - 10);
        drawing.DrawText(highText, new Point(NameWidth - 6 - highText.Width, area.Top - highText.Height / 2));
        drawing.DrawText(lowText, new Point(NameWidth - 6 - lowText.Width, area.Bottom - lowText.Height / 2));
    }

    private static FormattedText Text(string text, Brush brush, double dpi, double width) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11, brush, dpi)
        {
            MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(width, 4),
        };

    // ----- The pointer -----

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

        // The name's diamond: key the row at the playhead.
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
            // A diamond: chosen (Ctrl: added or taken away; Shift: the range from the one last clicked), the playhead
            // goes to it, and it can be dragged along its row (with the others selected, when it is one of them).
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

        // Empty lane space: the playhead goes there, and a drag draws a box round the keyframes to take.
        if (!inGraph)
            _marquee = (point, point, modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift));
        else
            _seeking = true;
        SeekRequested?.Invoke(Math.Max(TimeAt(Math.Max(point.X, NameWidth)), 0));
    }

    // ----- The selection -----

    private bool IsSelected(object? lane, double time) =>
        _selected.Any(s => Equals(s.Lane, lane) && Math.Abs(s.Time - time) <= Models.Layer.KeyTolerance);

    private bool Unselect(object? lane, double time) =>
        _selected.RemoveAll(s => Equals(s.Lane, lane) && Math.Abs(s.Time - time) <= Models.Layer.KeyTolerance) > 0;

    /// <summary>Whether a drag moves the whole selection: the diamond dragged is one of several selected.</summary>
    private bool DragMovesSelection((int Lane, double From, double At, double PointerX, bool Moved) drag) =>
        _selected.Count > 1 && drag.Lane < _lanes.Count && IsSelected(_lanes[drag.Lane].Target, drag.From);

    /// <summary>Shift+click: every keyframe between two, in time, on their rows and the rows between them.</summary>
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

    /// <summary>Every keyframe the box drawn on empty lane space covers: its rows (by their middles) and its stretch of time.</summary>
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

    /// <summary>Selects every keyframe on every row (Ctrl+A).</summary>
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

    /// <summary>What Copy and Delete work on: the selected keyframes, or with none the selected row's at the playhead.</summary>
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

        // Near an edge, the time bars move along by themselves while the pointer stays there.
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

        // Faster the further into the edge the pointer is: up to a tenth of the stretch shown every tick.
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

        // A box drawn (past a click's wobble) takes the keyframes in it; a plain click on empty space lets them all go.
        if (box is { } drawn)
        {
            if (Math.Abs(drawn.To.X - drawn.From.X) > 3 || Math.Abs(drawn.To.Y - drawn.From.Y) > 3)
                TakeMarquee(drawn);
            else if (!drawn.Add)
                ClearSelection();
        }

        // A plain click on one of several selected keyframes, not dragged: it alone stays selected.
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

        // Taken away mid-drag: the diamond goes back.
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

    // The right-click menu: Copy, Paste (at the playhead), Delete, Select All.
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_lanes.Count == 0)
            return;

        // A right-click on a diamond not selected takes it alone, as a click would.
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

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
