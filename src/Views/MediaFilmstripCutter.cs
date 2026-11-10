using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp.Views;

/// <summary>
/// The Media Browser's filmstrip cutter (5.4C): a compact strip of the chosen file's frames along a ruler, sliding under a playhead that
/// stays in the middle (the Subtitle Studio's timeline's way, without its cue rows), and a sub-clip marked on it with In and Out.
///
/// Dragging the strip scrubs (the moment under the playhead is the one shown); a flick coasts on with momentum (MomentumScroller, its
/// two guards keeping careful moves still); a click puts the playhead where it was clicked. The wheel, Shift + wheel (down: later) and a
/// sideways swipe or tilt (right: later) scroll in time with the same momentum, the Timeline's way (5.4.1A); Ctrl + wheel, or a precision
/// touchpad's pinch, zooms about the pointer (the moment under it stays put, so the playhead's moves and is told as a scrub). The ends of the sub-clip are dragged to move them; the marked band on the ruler is dragged out onto the timeline (the pane
/// carries it as a sub-clip). With the keyboard in it (IOwnsKeys, so the main window's shortcuts leave them to it): I and O mark In and
/// Out at the playhead, Left and Right step a frame (Shift: ten), Home and End go to the file's ends, Space starts or stops the preview's
/// stepping (J back, K stop, L forward), Enter inserts the sub-clip at the timeline's playhead.
///
/// Times are seconds of the file on its clips' MediaOffset clock. The strip is drawn into a visual of its own three screens wide and slid
/// by a transform, drawn again only when the view runs past what was drawn or something on it changes (no drawing while it coasts).
/// </summary>
public sealed class MediaFilmstripCutter : FrameworkElement, IOwnsKeys, ISidewaysWheel
{
    /// <summary>DIPs the strip slides for a wheel delta of 1 (a notch of 120: 120 DIPs), the Timeline's Shift + wheel feel (PanWheelAt).</summary>
    internal const double WheelDipsPerDelta = 1.0;

    /// <summary>What a notch of Ctrl + wheel multiplies the zoom by.</summary>
    private const double ZoomStep = 1.2;

    /// <summary>The ruler's height and the frame row's, DIPs; the cutter's whole height.</summary>
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

        // Nothing coasts while hidden or gone (the frame hook is let go).
        Unloaded += (_, _) => StopCoasting();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                StopCoasting();
        };
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _strip : _head;

    /// <summary>How long the file plays, seconds (0: nothing to cut). Setting it keeps the playhead and the sub-clip inside it.</summary>
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

    /// <summary>Frames a second, for stepping a frame at a time (30 while not known).</summary>
    public double FrameRate { get; set; } = 30;

    /// <summary>The moment under the playhead. Set from outside (the stepping preview, a key), it stops a coast.</summary>
    public double PositionSeconds
    {
        get => _position;
        set
        {
            StopCoasting();
            MoveTo(value);
        }
    }

    /// <summary>The sub-clip's start and end, seconds (the whole file while none is marked).</summary>
    public double InSeconds => _in;

    public double OutSeconds => _out;

    /// <summary>Whether a sub-clip shorter than the file is marked.</summary>
    public bool HasRange => _duration > 0 && (_in > 0.0005 || _out < _duration - 0.0005);

    /// <summary>The strip's frames, spread evenly over the file (the browser's strip of it); null for none (a sound, or not made yet).</summary>
    public IReadOnlyList<ImageSource>? Frames
    {
        get => _frames;
        set
        {
            _frames = value;
            InvalidateVisual();
        }
    }

    /// <summary>Pixels to the second; NaN until the cutter knows its width (then the whole file fills it).</summary>
    public double Zoom => _zoom;

    /// <summary>The moment under the playhead changed by the user: <c>settled</c> false while the strip is dragged or coasting.</summary>
    public event Action<double, bool>? Scrubbed;

    /// <summary>In or Out moved.</summary>
    public event Action<double, double>? RangeChanged;

    /// <summary>The marked band was dragged out of the cutter: the pane carries the sub-clip onto the timeline.</summary>
    public event Action? DragOutRequested;

    /// <summary>The preview's stepping: 2 Space (start or stop), 1 L (forward), -1 J (back), 0 K (stop).</summary>
    public event Action<int>? Shuttle;

    /// <summary>Enter: the sub-clip put on the timeline at its playhead.</summary>
    public event Action? InsertRequested;

    /// <summary>Takes up another file: its length, and the whole of it marked, the playhead at its start, zoomed to fit.</summary>
    public void Load(double durationSeconds, double frameRate)
    {
        StopCoasting();
        _duration = double.IsFinite(durationSeconds) ? Math.Max(durationSeconds, 0) : 0;
        (_in, _out, _position, _frames, _zoom) = (0, _duration, 0, null, double.NaN);
        FrameRate = frameRate > 0 && double.IsFinite(frameRate) ? frameRate : 30;
        InvalidateVisual();
    }

    /// <summary>Marks the sub-clip, each end kept inside the file and In before Out by at least a tenth of a second.</summary>
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

    /// <summary>A range kept inside a file of <paramref name="duration"/> seconds, In before Out by at least <see cref="ShortestRange"/> where it can be.</summary>
    internal static (double In, double Out) ClampRange(double inSeconds, double outSeconds, double duration)
    {
        // Never Math.Clamp with bounds that rounding can cross (0.2 + 0.1 is over 0.3): it throws, and this runs in OnRender's reach.
        duration = Math.Max(duration, 0);
        var shortest = Math.Min(ShortestRange, duration);
        var start = Math.Max(0, Math.Min(double.IsFinite(inSeconds) ? inSeconds : 0, Math.Max(duration - shortest, 0)));
        var end = Math.Min(duration, Math.Max(double.IsFinite(outSeconds) ? outSeconds : duration, start + shortest));
        return (start, end);
    }

    /// <summary>I: In at the playhead (Out moved to the file's end when it would come before it).</summary>
    public void MarkIn() => SetRange(_position, _out <= _position + ShortestRange / 2 ? _duration : _out);

    /// <summary>O: Out at the playhead (In moved to the file's start when it would come after it).</summary>
    public void MarkOut() => SetRange(_in >= _position - ShortestRange / 2 ? 0 : _in, _position);

    /// <summary>The whole file marked again.</summary>
    public void ClearRange() => SetRange(0, _duration);

    /// <summary>Moves the playhead by a number of frames, and says so (settled).</summary>
    public void StepFrames(int frames)
    {
        StopCoasting();
        MoveTo(_position + frames / Math.Max(FrameRate, 1));
        Scrubbed?.Invoke(_position, true);
    }

    /// <summary>Slides the strip by some DIPs (a drag's or a coast's): to the right brings earlier moments under the playhead.</summary>
    public void PanBy(double dips)
    {
        if (!double.IsFinite(dips) || dips == 0 || !(Zoom > 0))
            return;
        MoveTo(_position - dips / _zoom);
    }

    /// <summary>Stops a coast (a press, a key, the playhead set from outside, the cutter hidden).</summary>
    public void StopCoasting()
    {
        var was = _momentum.IsCoasting;
        _momentum.Stop();
        Unhook();
        if (was)
            Scrubbed?.Invoke(_position, true);
    }

    /// <summary>The keys the cutter keeps while it has the keyboard (with no Ctrl, Alt or Windows key; Shift steps ten frames).</summary>
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

    // The least zoom: the whole file in half the width (the playhead in the middle can then reach either end with all of it in view).
    // Never above the largest: a file under width / 2400 s would otherwise make the clamps below throw (a click.wav).
    private double SmallestZoom => _duration > 0 && ActualWidth > 0 ? Math.Min(ActualWidth / 2 / _duration, LargestZoom) : 1;

    protected override void OnRender(DrawingContext drawing)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        drawing.DrawRectangle(BackBrush, null, new Rect(0, 0, width, height));
        if (IsKeyboardFocused)
            drawing.DrawRectangle(null, FocusPen, new Rect(0.5, 0.5, Math.Max(width - 1, 0), Math.Max(height - 1, 0)));
        if (width > 0 && _duration > 0 && !(_zoom > 0))
            _zoom = Math.Clamp(width / _duration, SmallestZoom, LargestZoom);

        // Something on the strip changed (the file, its frames, the sub-clip, the zoom, the size): it is drawn again.
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

    /// <summary>The strip slid so the playhead's moment is under the middle; drawn again only when the view runs past what was drawn.</summary>
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

    /// <summary>Draws a screen's width before and after what is on screen, its own origin at the first of those moments.</summary>
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

        // The frames: one slot after another as wide as a frame of the file's shape, each showing the strip's frame nearest its middle.
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

        // The ruler: a tick at every round step of time that leaves room for its label.
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

        // The sub-clip: the band on the ruler that is dragged out, the frames outside it shaded, its ends marked.
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
    /// A wheel notch over the cutter (test seam; OnMouseWheel calls it): Ctrl zooms about <paramref name="pointerX"/> (a pinch on a
    /// precision touchpad arrives as Ctrl + wheel) and stops a coast; otherwise (none or Shift) the strip scrolls along the file, wheel
    /// down later, a fast stream coasting on.
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

        // Wheel up (positive) brings earlier moments: the strip slides right, the drag's way, as the coast that may follow goes on.
        PanWheel(delta * WheelDipsPerDelta, nowMs);
    }

    /// <summary>A sideways swipe or tilt over the cutter (WM_MOUSEHWHEEL from the window's hook): positive is to the right, later.</summary>
    public void SidewaysWheel(double delta, double nowMs)
    {
        if (_duration > 0 && _zoom > 0)
            PanWheel(-delta * WheelDipsPerDelta, nowMs);
    }

    // The strip slid by a wheel tick of DIPs in the drag's sense (PanBy: to the right brings earlier moments), a fast stream coasting on.
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

        // A tick the coast had already covered: it coasts on.
        if (step != 0 || !_momentum.IsCoasting)
        {
            // Held against either end (told nothing: a touchpad would ask the same still again and again), or a tick against the
            // coast that stopped it where it was (told once, settled).
            _momentum.Stop();
            Unhook();
            if (wasCoasting)
                Scrubbed?.Invoke(_position, true);
            return;
        }

        Hook();
    }

    // Zooms about a point of the cutter: the moment under it stays under it, so the playhead's moment (the middle) moves and is told, settled.
    private void ZoomAt(double factor, double x)
    {
        // Not while a drag holds the strip (its pointer maths would jump), and nothing at all at a zoom limit (no rounding drift, no seek).
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

    /// <summary>Whether the strip is coasting on after a flick or a fast wheel stream (test seam).</summary>
    internal bool IsCoasting => _momentum.IsCoasting;

    /// <summary>The moment at a point of the cutter, seconds of the file (test seam).</summary>
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
                // The marked band, taken out of the cutter: the pane starts the drag and drop (the cutter lets go of the pointer first).
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
            // A click: the playhead goes to the moment clicked.
            if (drag.Grip is Grip.Pan or Grip.Band)
            {
                MoveTo(ToSeconds(e.GetPosition(this).X));
                Scrubbed?.Invoke(_position, true);
            }

            return;
        }

        if (drag.Grip != Grip.Pan)
            return;

        // A flick coasts on (the momentum's guards: too slow, or rested before letting go, and it stops dead).
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

    // ----- The coast: the frame hook held only while it moves -----

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

    /// <summary>One frame of the coast at <paramref name="nowMs"/> (test seam; the frame hook calls it): whether it is still coasting.</summary>
    internal bool StepCoast(double nowMs)
    {
        var travel = _momentum.Tick(nowMs);
        var before = _position;
        PanBy(travel);

        // At either end of the file, or slowed to a stop: the coast is over, and the moment it came to rest is the settled one.
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

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
