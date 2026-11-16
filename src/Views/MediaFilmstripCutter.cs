using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using static HandPegApp.UiBrushes;

namespace HandPegApp.Views;

/// <summary>
/// Media Browser filmstrip cutter: a compact frame strip over a ruler sliding under a fixed centre playhead (like the Subtitle Studio
/// timeline, without cue rows), with an In/Out sub-clip.
///
/// Strip drag scrubs; flicks coast (MomentumScroller with its precision guards); clicks seek. Wheel, Shift+wheel (down = later) and
/// horizontal swipe/tilt (right = later) scroll with momentum like the Timeline; Ctrl+wheel or pinch zooms about the pointer (the time
/// under it stays, so the playhead time changes and is raised as a scrub). Drag In/Out ends to move them; drag the ruler band out to
/// the timeline as a sub-clip. Keyboard (IOwnsKeys, so main-window shortcuts yield): I/O mark at the playhead, Left/Right step a frame
/// (Shift: ten), Home/End jump to the ends, Space/J/K/L control preview stepping, Enter inserts the sub-clip at the timeline playhead.
///
/// Times are file seconds on the MediaOffset clock. The strip renders into a three-screen-wide visual slid by a transform; it redraws
/// only when the view passes the drawn range or content changes (never while coasting).
/// </summary>
public sealed class MediaFilmstripCutter : FrameworkElement, IOwnsKeys, ISidewaysWheel
{
    /// <summary>Strip DIPs per wheel delta unit (120 per notch), matching the Timeline's Shift+wheel (PanWheelAt).</summary>
    internal const double WheelDipsPerDelta = 1.0;

    /// <summary>Zoom multiplier per Ctrl+wheel notch.</summary>
    private const double ZoomStep = 1.2;

    /// <summary>Ruler and frame-row heights and total preferred height, in DIPs.</summary>
    public const double RulerHeight = 16, FrameRowHeight = 46, PreferredHeight = RulerHeight + FrameRowHeight + 4;

    private const double LargestZoom = 1200, EdgePixels = 7, ShortestRange = 0.1;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush BackBrush = Frozen(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly Brush FileBrush = Frozen(Color.FromRgb(0x24, 0x24, 0x24));
    private static readonly Brush RulerText = Frozen(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
    private static readonly Brush OutsideBrush = Frozen(Color.FromArgb(0xA8, 0x00, 0x00, 0x00));
    private static readonly Brush BandBrush = Frozen(Color.FromArgb(0x50, 0xFF, 0xC8, 0x3C));
    private static readonly Brush MarkedBandBrush = Frozen(Color.FromArgb(0xC0, 0xFF, 0xC8, 0x3C));
    private static readonly Pen TickPen = FrozenPen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen EdgePen = FrozenPen(Color.FromRgb(0xFF, 0xC8, 0x3C), 2);
    private static readonly Pen HeadPen = FrozenPen(Colors.OrangeRed, 2);
    private static readonly Pen FocusPen = FrozenPen(Color.FromArgb(0x90, 0x5A, 0xA0, 0xFF), 1);
    private static readonly double[] RulerSteps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

    private readonly DrawingVisual _strip = new(), _head = new();
    private readonly TranslateTransform _stripShift = new();
    private readonly Dictionary<string, FormattedText> _rulerLabels = [];
    private double _labelsDpi, _headWidth = -1;
    private (double From, double To) _drawn = (double.NaN, double.NaN);

    private readonly MomentumScroller _momentum = new();
    private bool _coastHooked;

    private double _duration, _position, _in, _out, _zoom = double.NaN;
    private IReadOnlyList<ImageSource>? _frames;
    private Drag? _drag;

    private enum Grip { Pan, In, Out, Band }

    private sealed record Drag(Grip Grip, Point Start, double Position)
    {
        public bool Moved { get; set; }
    }

    public MediaFilmstripCutter()
    {
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        Height = PreferredHeight;
        Cursor = Cursors.Hand;
        _strip.Transform = _stripShift;
        AddVisualChild(_strip);
        AddVisualChild(_head);
        System.Windows.Automation.AutomationProperties.SetName(this, "Filmstrip Cutter");
        ToolTip = "Drag to scrub, click to go there; the wheel or a sideways swipe scrolls, Ctrl + wheel or a pinch zooms. I and O mark the sub-clip; drag the marked band (top) onto the timeline, or press Enter.";

        // Stop coasting (and unhook the frame) when unloaded or hidden.
        Unloaded += (_, _) => StopCoasting();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                StopCoasting();
        };
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _strip : _head;

    /// <summary>File duration in seconds (0 = nothing to cut); setting it clamps the playhead and sub-clip into range.</summary>
    public double DurationSeconds
    {
        get => _duration;
        set
        {
            var duration = double.IsFinite(value) ? Math.Max(value, 0) : 0;
            if (duration == _duration)
                return;
            var whole = !HasRange;
            _duration = duration;
            (_in, _out) = whole ? (0, duration) : (Math.Min(_in, duration), Math.Min(_out, duration));
            _position = Math.Min(_position, duration);
            _zoom = double.NaN;
            InvalidateVisual();
        }
    }

    /// <summary>Frame rate for single-frame steps (30 when unknown).</summary>
    public double FrameRate { get; set; } = 30;

    /// <summary>Time under the playhead; external sets (preview stepping, keys) stop any coast.</summary>
    public double PositionSeconds
    {
        get => _position;
        set
        {
            StopCoasting();
            MoveTo(value);
        }
    }

    /// <summary>Sub-clip In/Out in seconds (the whole file when unmarked).</summary>
    public double InSeconds => _in;

    public double OutSeconds => _out;

    /// <summary>True when a sub-clip shorter than the file is marked.</summary>
    public bool HasRange => _duration > 0 && (_in > 0.0005 || _out < _duration - 0.0005);

    /// <summary>Evenly spaced strip frames from the browser; null for audio or not yet generated.</summary>
    public IReadOnlyList<ImageSource>? Frames
    {
        get => _frames;
        set
        {
            _frames = value;
            InvalidateVisual();
        }
    }

    /// <summary>Pixels per second; NaN until width is known (then initialised to fit the whole file).</summary>
    public double Zoom => _zoom;

    /// <summary>User-driven playhead change: (seconds, settled), settled false while dragging or coasting.</summary>
    public event Action<double, bool>? Scrubbed;

    /// <summary>Raised when In or Out changes.</summary>
    public event Action<double, double>? RangeChanged;

    /// <summary>Marked band dragged out; the pane starts a sub-clip drag-and-drop to the timeline.</summary>
    public event Action? DragOutRequested;

    /// <summary>Preview stepping command: 2 = Space (toggle), 1 = L (forward), -1 = J (back), 0 = K (stop).</summary>
    public event Action<int>? Shuttle;

    /// <summary>Enter: insert the sub-clip at the timeline playhead.</summary>
    public event Action? InsertRequested;

    /// <summary>Loads a new file: whole range marked, playhead at 0, zoom reset to fit.</summary>
    public void Load(double durationSeconds, double frameRate)
    {
        StopCoasting();
        _duration = double.IsFinite(durationSeconds) ? Math.Max(durationSeconds, 0) : 0;
        (_in, _out, _position, _frames, _zoom) = (0, _duration, 0, null, double.NaN);
        FrameRate = frameRate > 0 && double.IsFinite(frameRate) ? frameRate : 30;
        InvalidateVisual();
    }

    /// <summary>Sets the sub-clip, clamped into the file with Out at least 0.1 s after In.</summary>
    public void SetRange(double inSeconds, double outSeconds)
    {
        if (_duration <= 0)
            return;

        var (start, end) = ClampRange(inSeconds, outSeconds, _duration);
        if ((start, end) == (_in, _out))
            return;
        (_in, _out) = (start, end);
        InvalidateVisual();
        RangeChanged?.Invoke(_in, _out);
    }

    /// <summary>Clamps a range into [0, <paramref name="duration"/>] with Out >= In + <see cref="ShortestRange"/> where possible.</summary>
    internal static (double In, double Out) ClampRange(double inSeconds, double outSeconds, double duration)
    {
        // Avoid Math.Clamp: rounding can invert the bounds (0.2 + 0.1 > 0.3) and it would throw on a path reachable from OnRender.
        duration = Math.Max(duration, 0);
        var shortest = Math.Min(ShortestRange, duration);
        var start = Math.Max(0, Math.Min(double.IsFinite(inSeconds) ? inSeconds : 0, Math.Max(duration - shortest, 0)));
        var end = Math.Min(duration, Math.Max(double.IsFinite(outSeconds) ? outSeconds : duration, start + shortest));
        return (start, end);
    }

    /// <summary>I: In at the playhead (Out moves to the end if it would precede In).</summary>
    public void MarkIn() => SetRange(_position, _out <= _position + ShortestRange / 2 ? _duration : _out);

    /// <summary>O: Out at the playhead (In moves to 0 if it would follow Out).</summary>
    public void MarkOut() => SetRange(_in >= _position - ShortestRange / 2 ? 0 : _in, _position);

    /// <summary>Resets the range to the whole file.</summary>
    public void ClearRange() => SetRange(0, _duration);

    /// <summary>Steps the playhead by frames and raises a settled Scrubbed.</summary>
    public void StepFrames(int frames)
    {
        StopCoasting();
        MoveTo(_position + frames / Math.Max(FrameRate, 1));
        Scrubbed?.Invoke(_position, true);
    }

    /// <summary>Pans by DIPs (drag or coast); positive (rightward) moves earlier time under the playhead.</summary>
    public void PanBy(double dips)
    {
        if (!double.IsFinite(dips) || dips == 0 || !(Zoom > 0))
            return;
        MoveTo(_position - dips / _zoom);
    }

    /// <summary>Stops a coast (press, key, external seek, hide); raises a settled Scrubbed if one was running.</summary>
    public void StopCoasting()
    {
        var was = _momentum.IsCoasting;
        _momentum.Stop();
        Unhook();
        if (was)
            Scrubbed?.Invoke(_position, true);
    }

    /// <summary>Keys owned while focused (no Ctrl/Alt/Win; Shift steps ten frames).</summary>
    public bool OwnsKey(Key key, ModifierKeys modifiers) =>
        (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0
        && key is Key.I or Key.O or Key.Space or Key.Left or Key.Right or Key.Home or Key.End or Key.J or Key.K or Key.L or Key.Enter;

    private void MoveTo(double seconds)
    {
        var position = Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, _duration);
        if (position == _position)
            return;
        _position = position;
        Slide();
    }

    private double ToX(double seconds) => ActualWidth / 2 + (seconds - _position) * _zoom;

    private double ToSeconds(double x) => _position + (x - ActualWidth / 2) / _zoom;

    // Min zoom: whole file in half the width (the centre playhead can reach either end with all in view). Capped at LargestZoom, since
    // very short files (under width/2400 s) would otherwise invert the clamp bounds and throw.
    private double SmallestZoom => _duration > 0 && ActualWidth > 0 ? Math.Min(ActualWidth / 2 / _duration, LargestZoom) : 1;

    protected override void OnRender(DrawingContext drawing)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        drawing.DrawRectangle(BackBrush, null, new Rect(0, 0, width, height));
        if (IsKeyboardFocused)
            drawing.DrawRectangle(null, FocusPen, new Rect(0.5, 0.5, Math.Max(width - 1, 0), Math.Max(height - 1, 0)));
        if (width > 0 && _duration > 0 && !(_zoom > 0))
            _zoom = Math.Clamp(width / _duration, SmallestZoom, LargestZoom);

        // Content changed (file, frames, range, zoom, size): invalidate the drawn range and redraw.
        _drawn = (double.NaN, double.NaN);
        Slide();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    /// <summary>Translates the strip to centre the playhead time; redraws only when the view passes the drawn range.</summary>
    private void Slide()
    {
        var width = ActualWidth;
        if (width <= 0)
            return;

        DrawHead(width);
        if (!(_zoom > 0) || _duration <= 0)
        {
            using (_strip.RenderOpen())
            {
            }

            _drawn = (double.NaN, double.NaN);
            return;
        }

        var (first, last) = (ToSeconds(0), ToSeconds(width));
        if (double.IsNaN(_drawn.From) || first < _drawn.From || last > _drawn.To)
            DrawStrip(first, last);
        _stripShift.X = ToX(_drawn.From);
    }

    private void DrawHead(double width)
    {
        if (_headWidth == width)
            return;
        _headWidth = width;
        using var drawing = _head.RenderOpen();
        var middle = Math.Round(width / 2) + 0.5;
        drawing.DrawLine(HeadPen, new Point(middle, 0), new Point(middle, ActualHeight));
    }

    /// <summary>Renders one screen before to one screen after the visible range, with local origin at its start.</summary>
    private void DrawStrip(double firstSecond, double lastSecond)
    {
        var span = lastSecond - firstSecond;
        var (from, to) = (firstSecond - span, lastSecond + span);
        _drawn = (from, to);
        var zoom = _zoom;
        double X(double seconds) => (seconds - from) * zoom;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _labelsDpi || _rulerLabels.Count > 512)
        {
            _labelsDpi = dpi;
            _rulerLabels.Clear();
        }

        using var drawing = _strip.RenderOpen();
        var (fileLeft, fileRight) = (Math.Max(X(0), 0), Math.Min(X(_duration), X(to)));
        var rowTop = RulerHeight + 2;
        var rowRect = new Rect(fileLeft, rowTop, Math.Max(fileRight - fileLeft, 0), FrameRowHeight);
        if (rowRect.Width <= 0)
            return;
        drawing.DrawRectangle(FileBrush, null, rowRect);

        // Frame slots at the file's aspect, each showing the strip frame nearest its centre time.
        if (_frames is { Count: > 0 } frames && frames[0] is { Width: > 0, Height: > 0 } sample)
        {
            var slot = Math.Max(FrameRowHeight * sample.Width / sample.Height, 8);
            var firstSlot = Math.Max((int)Math.Floor(Math.Max(X(0), 0) / slot - X(0) / slot), 0);
            drawing.PushClip(new RectangleGeometry(rowRect));
            for (var k = firstSlot; k < firstSlot + 400; k++)
            {
                var left = X(0) + k * slot;
                if (left > rowRect.Right)
                    break;
                var middle = (left + slot / 2 - X(0)) / zoom;
                var index = Math.Clamp((int)(middle / _duration * frames.Count), 0, frames.Count - 1);
                drawing.DrawImage(frames[index], new Rect(left, rowTop, slot, FrameRowHeight));
            }

            drawing.Pop();
        }

        // Ruler ticks at the smallest round step leaving >= 64 px per label.
        var step = RulerSteps.FirstOrDefault(s => s * zoom >= 64, 7200);
        for (var tick = Math.Max(Math.Floor(from / step) * step, 0); tick <= Math.Min(to, _duration); tick += step)
        {
            var x = Math.Round(X(tick)) + 0.5;
            drawing.DrawLine(TickPen, new Point(x, 0), new Point(x, RulerHeight));
            var time = TimeSpan.FromSeconds(tick);
            var text = step < 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 100}")
                : time.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
            if (!_rulerLabels.TryGetValue(text, out var label))
                _rulerLabels[text] = label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, RulerText, dpi);
            drawing.DrawText(label, new Point(x + 3, 1));
        }

        // Sub-clip: draggable ruler band, frames outside it shaded, In/Out brackets.
        var (inX, outX) = (X(_in), X(_out));
        var marked = HasRange;
        drawing.DrawRectangle(marked ? MarkedBandBrush : BandBrush, null, new Rect(inX, RulerHeight - 5, Math.Max(outX - inX, 1), 5));
        if (marked)
        {
            if (inX > rowRect.Left)
                drawing.DrawRectangle(OutsideBrush, null, new Rect(rowRect.Left, rowTop, inX - rowRect.Left, FrameRowHeight));
            if (outX < rowRect.Right)
                drawing.DrawRectangle(OutsideBrush, null, new Rect(outX, rowTop, rowRect.Right - outX, FrameRowHeight));
            drawing.DrawLine(EdgePen, new Point(inX, RulerHeight - 5), new Point(inX, rowTop + FrameRowHeight));
            drawing.DrawLine(EdgePen, new Point(inX, rowTop + FrameRowHeight), new Point(inX + 5, rowTop + FrameRowHeight));
            drawing.DrawLine(EdgePen, new Point(outX, RulerHeight - 5), new Point(outX, rowTop + FrameRowHeight));
            drawing.DrawLine(EdgePen, new Point(outX, rowTop + FrameRowHeight), new Point(outX - 5, rowTop + FrameRowHeight));
        }
    }

    private Grip HitTest(Point point)
    {
        if (_duration <= 0 || !(_zoom > 0))
            return Grip.Pan;

        var (inX, outX) = (ToX(_in), ToX(_out));
        if (HasRange)
        {
            if (Math.Abs(point.X - inX) <= EdgePixels && Math.Abs(point.X - inX) <= Math.Abs(point.X - outX))
                return Grip.In;
            if (Math.Abs(point.X - outX) <= EdgePixels)
                return Grip.Out;
        }

        return point.Y <= RulerHeight && point.X >= inX && point.X <= outX ? Grip.Band : Grip.Pan;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        WheelAt(e.Delta, Keyboard.Modifiers, MomentumScroller.Now(), e.GetPosition(this).X);
    }

    /// <summary>
    /// Test seam (called by OnMouseWheel): Ctrl zooms about <paramref name="pointerX"/> and stops coasting (pinch arrives as Ctrl+wheel);
    /// otherwise (none or Shift) scrolls through the file, wheel down = later, fast streams coasting.
    /// </summary>
    internal void WheelAt(int delta, ModifierKeys modifiers, double nowMs, double pointerX)
    {
        if (_duration <= 0 || !(_zoom > 0))
            return;

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            StopCoasting();
            ZoomAt(Math.Pow(ZoomStep, delta / 120.0), pointerX);
            return;
        }

        // Wheel up (positive) shows earlier time: the strip slides right, as a drag would, and any coast continues that way.
        PanWheel(delta * WheelDipsPerDelta, nowMs);
    }

    /// <summary>Horizontal swipe/tilt (WM_MOUSEHWHEEL via the window hook); positive = right = later.</summary>
    public void SidewaysWheel(double delta, double nowMs)
    {
        if (_duration > 0 && _zoom > 0)
            PanWheel(-delta * WheelDipsPerDelta, nowMs);
    }

    // Wheel pan in drag-sense DIPs (PanBy: right = earlier); fast streams start a coast.
    private void PanWheel(double dips, double nowMs)
    {
        if (_drag is not null)
            return;
        var (before, wasCoasting) = (_position, _momentum.IsCoasting);
        var step = _momentum.Wheel(dips, nowMs);
        PanBy(step);
        if (_position != before)
        {
            Scrubbed?.Invoke(_position, !_momentum.IsCoasting);
            if (_momentum.IsCoasting)
                Hook();
            else
                Unhook();
            return;
        }

        // Tick already covered by the coast (step 0 while coasting): keep coasting.
        if (step != 0 || !_momentum.IsCoasting)
        {
            // Pinned at an end (no event, since touchpads repeat such ticks) or an opposing tick stopped the coast (one settled event).
            _momentum.Stop();
            Unhook();
            if (wasCoasting)
                Scrubbed?.Invoke(_position, true);
            return;
        }

        Hook();
    }

    // Zooms about x keeping its time fixed; the centre playhead time therefore changes and is raised as a settled scrub.
    private void ZoomAt(double factor, double x)
    {
        // Ignored during drags (pointer maths would jump) and at zoom limits (avoids rounding drift and spurious seeks).
        if (_drag is not null)
            return;
        var at = ToSeconds(x);
        var zoom = Math.Max(SmallestZoom, Math.Min(_zoom * factor, LargestZoom));
        if (zoom == _zoom)
            return;
        _zoom = zoom;
        var before = _position;
        MoveTo(at - (x - ActualWidth / 2) / _zoom);
        InvalidateVisual();
        if (_position != before)
            Scrubbed?.Invoke(_position, true);
    }

    /// <summary>Test seam: true while coasting after a flick or fast wheel stream.</summary>
    internal bool IsCoasting => _momentum.IsCoasting;

    /// <summary>Test seam: cutter x to file seconds.</summary>
    internal double SecondsAt(double x) => ToSeconds(x);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        StopCoasting();
        var point = e.GetPosition(this);
        if (_duration > 0 && CaptureMouse())
        {
            _drag = new Drag(HitTest(point), point, _position);
            _momentum.Press(MomentumScroller.Now());
            _momentum.Sample(point.X, MomentumScroller.Now());
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_drag is not { } drag)
        {
            Cursor = HitTest(point) switch
            {
                Grip.In or Grip.Out => Cursors.SizeWE,
                Grip.Band => Cursors.SizeAll,
                _ => Cursors.Hand,
            };
            return;
        }

        var moved = point - drag.Start;
        if (!drag.Moved && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        drag.Moved = true;

        switch (drag.Grip)
        {
            case Grip.In:
                SetRange(ToSeconds(point.X), _out);
                break;
            case Grip.Out:
                SetRange(_in, ToSeconds(point.X));
                break;
            case Grip.Band:
                // Band dragged out: release capture first, then the pane starts the drag-and-drop.
                _drag = null;
                ReleaseMouseCapture();
                DragOutRequested?.Invoke();
                break;
            default:
                _momentum.Sample(point.X, MomentumScroller.Now());
                MoveTo(drag.Position - (point.X - drag.Start.X) / _zoom);
                Scrubbed?.Invoke(_position, false);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is not { } drag)
            return;

        _drag = null;
        ReleaseMouseCapture();
        e.Handled = true;
        if (!drag.Moved)
        {
            // Click without drag: seek to the clicked time.
            if (drag.Grip is Grip.Pan or Grip.Band)
            {
                MoveTo(ToSeconds(e.GetPosition(this).X));
                Scrubbed?.Invoke(_position, true);
            }

            return;
        }

        if (drag.Grip != Grip.Pan)
            return;

        // Flick may start a coast (momentum guards reject slow or rested releases).
        _momentum.Release(MomentumScroller.Now());
        if (_momentum.IsCoasting)
            Hook();
        else
            Scrubbed?.Invoke(_position, true);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is { Moved: true, Grip: Grip.Pan })
            Scrubbed?.Invoke(_position, true);
        _drag = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !OwnsKey(e.Key, Keyboard.Modifiers))
            return;

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.I:
                MarkIn();
                break;
            case Key.O:
                MarkOut();
                break;
            case Key.Left:
                StepFrames(shift ? -10 : -1);
                break;
            case Key.Right:
                StepFrames(shift ? 10 : 1);
                break;
            case Key.Home:
                StopCoasting();
                MoveTo(0);
                Scrubbed?.Invoke(_position, true);
                break;
            case Key.End:
                StopCoasting();
                MoveTo(_duration);
                Scrubbed?.Invoke(_position, true);
                break;
            case Key.Space:
                Shuttle?.Invoke(2);
                break;
            case Key.J:
                Shuttle?.Invoke(-1);
                break;
            case Key.K:
                Shuttle?.Invoke(0);
                break;
            case Key.L:
                Shuttle?.Invoke(1);
                break;
            case Key.Enter:
                InsertRequested?.Invoke();
                break;
        }

        e.Handled = true;
    }

    // ----- Coast (frame hook only while moving) -----

    private void Hook()
    {
        if (_coastHooked)
            return;
        _coastHooked = true;
        CompositionTarget.Rendering += Coast;
    }

    private void Unhook()
    {
        if (!_coastHooked)
            return;
        _coastHooked = false;
        CompositionTarget.Rendering -= Coast;
    }

    private void Coast(object? sender, EventArgs e) => StepCoast(MomentumScroller.Now());

    /// <summary>Test seam (called per frame): advances the coast at <paramref name="nowMs"/>; returns whether still coasting.</summary>
    internal bool StepCoast(double nowMs)
    {
        var travel = _momentum.Tick(nowMs);
        var before = _position;
        PanBy(travel);

        // End reached or slowed to a stop: finish and raise the rest position as settled.
        if (!_momentum.IsCoasting || _position == before && travel != 0)
        {
            _momentum.Stop();
            Unhook();
            Scrubbed?.Invoke(_position, true);
            return false;
        }

        Scrubbed?.Invoke(_position, false);
        return true;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
