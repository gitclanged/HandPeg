using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Services;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// Color Studio grading wheel (Lift, Gamma or Gain): a colour disc whose puck shows the chroma push (RGB minus their mean, plotted toward
/// the pushed hue), plus a master jog bar below that shifts all three channels equally. Drag the puck (Shift locks to the initial axis;
/// Ctrl = fine) or drag across the bar; double-click resets the wheel (disc) or master (bar) to 0. Handles mouse, pen and touch (touch is
/// captured here so it is not promoted to mouse). Pointer logic lives in point methods (<see cref="BeginDrag"/>, <see cref="DragTo"/>,
/// <see cref="EndDrag"/>) that goldens drive directly. A gesture spans <see cref="EditStarted"/> .. <see cref="EditEnded"/> with values via
/// <see cref="ValueChanged"/>; the pane writes them as one Undo step. Single OnRender; the disc is one frozen bitmap shared by all wheels.
/// Pass 5.6.4B: the puck, its stem and the pointer math stay inside the rim (<see cref="WheelPush.ClampToDisc"/>, even for a card value
/// beyond it), and a drag shows its R/G/B change from the press in the title row (<see cref="DeltaText"/>).
/// </summary>
public sealed class ColorWheelsControl : FrameworkElement
{
    /// <summary>Push magnitude at the disc edge (channels range -1..1; the push is the channels minus their mean).</summary>
    public const double Reach = WheelPush.Reach;

    /// <summary>Master delta for a drag across the full bar width.</summary>
    public const double JogReach = 1;

    private const double TitleHeight = 18, JogHeight = 12, JogGap = 8, Pad = 2, PuckRadius = 5, FineScale = 0.2, AxisLockDip = 3;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Pen PuckEdge = Frozen(new Pen(Solid(255, 0x10, 0x10, 0x12), 1.2));
    private static readonly Pen CrossPen = Frozen(new Pen(Solid(70, 0, 0, 0), 1));
    private static readonly Brush PuckFill = Solid(255, 0xF4, 0xF4, 0xF4);
    private static BitmapSource? _disc;

    private Brush _text = Solid(255, 0xE0, 0xE0, 0xE0), _muted = Solid(255, 0x90, 0x90, 0x90), _inset = Solid(255, 0x16, 0x16, 0x18),
        _accent = Solid(255, 0x4F, 0xC3, 0xF7);
    private Pen _ring = Frozen(new Pen(Solid(255, 0x2A, 0x2A, 0x30), 1));
    private (double R, double G, double B) _value;

    // Gesture state: starting part, press point and value; Shift axis lock (0 = undecided, 1 = horizontal, 2 = vertical).
    private enum Part { None, Disc, Jog }

    private Part _part;
    private Point _from;
    private (double R, double G, double B) _start;
    private int _axis;
    private TouchDevice? _touch;

    public ColorWheelsControl()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) =>
        {
            Services.ThemeManager.Applied += ReadLook;
            ReadLook();
        };
        Unloaded += (_, _) =>
        {
            Services.ThemeManager.Applied -= ReadLook;
            EndDrag();
        };
    }

    /// <summary>Wheel title ("Lift", "Gamma", "Gain") drawn above it; also sets the automation name and tooltip.</summary>
    public string Title
    {
        get => _title;
        init
        {
            _title = value;
            AutomationProperties.SetName(this, value + " Wheel");
            ToolTip = $"{value}: drag the puck to tint, the bar for all channels";
        }
    }

    private readonly string _title = "";

    /// <summary>Wheel RGB (each -1..1). Setting it (from the card) only redraws; no events are raised.</summary>
    public (double R, double G, double B) Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;
            _value = value;
            InvalidateVisual();
        }
    }

    /// <summary>True during a gesture (the pane does not overwrite the value meanwhile).</summary>
    public bool IsDragging => _part != Part.None;

    /// <summary>Raised when a gesture changes <see cref="Value"/>.</summary>
    public event Action<ColorWheelsControl>? ValueChanged;

    /// <summary>Raised when a gesture begins (drag or double-click reset); changes until EditEnded form one step.</summary>
    public event Action<ColorWheelsControl>? EditStarted;

    /// <summary>Raised when the gesture ends.</summary>
    public event Action<ColorWheelsControl>? EditEnded;

    // ----- Value decomposition: master + push and back (pure; golden-tested) -----

    /// <summary>Splits RGB into the master (channel mean) and the puck offset in push units (<see cref="Reach"/> = disc edge, +Y up).</summary>
    public static (double Master, double X, double Y) Split((double R, double G, double B) value) => WheelPush.Split(value);

    /// <summary>Inverse of <see cref="Split"/>: master + puck offset to RGB, each channel clamped to -1..1.</summary>
    public static (double R, double G, double B) Join(double master, double x, double y) => WheelPush.Join(master, x, y);

    /// <summary>
    /// The puck's centre for a value on a disc at <paramref name="centre"/> of <paramref name="radius"/> DIPs: the push held to the rim
    /// first, so neither the puck nor its stem can leave the wheel. Pure; golden-tested.
    /// </summary>
    internal static Point PuckAt(Point centre, double radius, (double R, double G, double B) value)
    {
        var (_, x, y) = Split(value);
        var (cx, cy) = WheelPush.ClampToDisc(x, y);
        var travel = TravelRadius(radius);
        return new Point(centre.X + cx / Reach * travel, centre.Y - cy / Reach * travel);
    }

    /// <summary>
    /// How far the puck's centre travels at full reach on a disc of <paramref name="radius"/> DIPs: the radius less the painted puck (its
    /// radius and half its outline), so its edge stays inside the rim; the pointer moves the puck at the same scale. Pure.
    /// </summary>
    internal static double TravelRadius(double radius) => Math.Max(radius - PuckRadius - PuckEdge.Thickness / 2, 0);

    /// <summary>A drag's change per channel from the press ("R +0.05  G -0.02  B +0.01"), shown in the title row while dragging.</summary>
    public static string DeltaText((double R, double G, double B) from, (double R, double G, double B) to) =>
        string.Create(CultureInfo.InvariantCulture, $"R {to.R - from.R:+0.00;-0.00;0.00}  G {to.G - from.G:+0.00;-0.00;0.00}  B {to.B - from.B:+0.00;-0.00;0.00}");

    /// <summary>The current drag's change per channel from its press; null when not dragging.</summary>
    public (double R, double G, double B)? DragDelta => _part == Part.None ? null : (_value.R - _start.R, _value.G - _start.G, _value.B - _start.B);

    // ----- Geometry -----

    private (Point Centre, double Radius) Disc()
    {
        var room = Math.Max(Math.Min(ActualWidth - 2 * Pad, ActualHeight - TitleHeight - JogGap - JogHeight - 2 * Pad), 8);
        return (new Point(ActualWidth / 2, TitleHeight + Pad + room / 2), room / 2);
    }

    /// <summary>Disc centre in DIPs (golden press point).</summary>
    internal Point DiscCentre => Disc().Centre;

    private Rect Jog()
    {
        var (centre, radius) = Disc();
        var width = Math.Max(radius * 2, 16);
        return new Rect(centre.X - width / 2, centre.Y + radius + JogGap, width, JogHeight);
    }

    private Part PartAt(Point p)
    {
        var (centre, radius) = Disc();
        if ((p - centre).Length <= radius + 4)
            return Part.Disc;
        var jog = Jog();
        jog.Inflate(0, 4);
        return jog.Contains(p) ? Part.Jog : Part.None;
    }

    // ----- Gesture (point methods shared by mouse, pen, touch and goldens) -----

    /// <summary>Press at <paramref name="p"/> (DIPs): starts a drag on the disc or bar; returns false elsewhere.</summary>
    public bool BeginDrag(Point p)
    {
        var part = PartAt(p);
        if (part == Part.None)
            return false;

        EndDrag();
        (_part, _from, _start, _axis) = (part, p, _value, 0);
        EditStarted?.Invoke(this);
        return true;
    }

    /// <summary>Pointer at <paramref name="p"/>: moves the puck by the pointer delta (axis lock, fine scale) or adjusts the master on the bar.</summary>
    public void DragTo(Point p, bool lockAxis = false, bool fine = false)
    {
        if (_part == Part.None)
            return;

        var (dx, dy) = (p.X - _from.X, p.Y - _from.Y);
        var scale = fine ? FineScale : 1;
        var (master, x, y) = Split(_start);
        if (_part == Part.Jog)
        {
            // The master bar moves the master only: the push is kept as it was (one beyond the rim included).
            master += dx / Math.Max(Jog().Width, 1) * JogReach * scale;
        }
        else
        {
            (x, y) = WheelPush.ClampToDisc(x, y);
            if (lockAxis)
            {
                if (_axis == 0 && Math.Max(Math.Abs(dx), Math.Abs(dy)) >= AxisLockDip)
                    _axis = Math.Abs(dx) >= Math.Abs(dy) ? 1 : 2;
                (dx, dy) = _axis == 1 ? (dx, 0.0) : _axis == 2 ? (0.0, dy) : (0.0, 0.0);
            }
            else
            {
                _axis = 0;
            }

            var perDip = Reach / Math.Max(TravelRadius(Disc().Radius), 1) * scale;
            (x, y) = WheelPush.ClampToDisc(x + dx * perDip, y - dy * perDip);
        }

        var value = Join(master, x, y);
        if (value == _value)
            return;
        _value = value;
        InvalidateVisual();
        ValueChanged?.Invoke(this);
    }

    /// <summary>Ends the gesture (pointer release, capture loss or unload).</summary>
    public void EndDrag()
    {
        if (_part == Part.None)
            return;
        (_part, _axis) = (Part.None, 0);
        InvalidateVisual();
        EditEnded?.Invoke(this);
    }

    /// <summary>Resets the wheel to 0 as one gesture (<paramref name="masterOnly"/>: master only, keeping the push).</summary>
    public void Reset(bool masterOnly = false)
    {
        var (_, x, y) = Split(_value);
        var value = masterOnly ? Join(0, x, y) : (0, 0, 0);
        if (value == _value)
            return;
        EndDrag();
        EditStarted?.Invoke(this);
        _value = value;
        InvalidateVisual();
        ValueChanged?.Invoke(this);
        EditEnded?.Invoke(this);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        if (e.ClickCount == 2 && PartAt(p) is var part and not Part.None)
        {
            Reset(masterOnly: part == Part.Jog);
            e.Handled = true;
            return;
        }

        if (BeginDrag(p))
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && _touch is null && e.LeftButton == MouseButtonState.Pressed)
            DragTo(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
            return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_touch is null)
            EndDrag();
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        if (_touch is not null || !BeginDrag(e.GetTouchPoint(this).Position))
            return;
        _touch = e.TouchDevice;
        CaptureTouch(e.TouchDevice);
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        if (ReferenceEquals(e.TouchDevice, _touch))
        {
            DragTo(e.GetTouchPoint(this).Position, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        if (!ReferenceEquals(e.TouchDevice, _touch))
            return;
        ReleaseTouchCapture(e.TouchDevice);
        _touch = null;
        EndDrag();
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        base.OnLostTouchCapture(e);
        if (!ReferenceEquals(e.TouchDevice, _touch))
            return;
        _touch = null;
        EndDrag();
    }

    // ----- Rendering -----

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(Math.Min(availableSize.Width, 220), 72) : 140;
        return new Size(width, Math.Min(width, 220) + TitleHeight + JogGap + JogHeight + 2 * Pad);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (centre, radius) = Disc();
        var (master, _, _) = Split(_value);

        // Title at left, master value right-aligned; while dragging, the R/G/B change from the press instead (fitted to the disc width).
        if (_part != Part.None)
        {
            var delta = new FormattedText(DeltaText(_start, _value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, _text, dpi);
            if (delta.Width > radius * 2)
                delta.SetFontSize(Math.Max(10 * radius * 2 / delta.Width, 6));
            drawing.DrawText(delta, new Point(centre.X - Math.Min(delta.Width, radius * 2) / 2, 1));
        }
        else
        {
            var title = new FormattedText(_title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, _text, dpi);
            drawing.DrawText(title, new Point(centre.X - radius, 0));
            var number = new FormattedText(master.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, _muted, dpi);
            drawing.DrawText(number, new Point(centre.X + radius - number.Width, 1));
        }

        // Disc, rim and faint crosshair; puck with a stem from the centre.
        drawing.DrawImage(DiscPicture(), new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2));
        drawing.DrawEllipse(null, _ring, centre, radius, radius);
        drawing.DrawLine(CrossPen, new Point(centre.X - radius, centre.Y), new Point(centre.X + radius, centre.Y));
        drawing.DrawLine(CrossPen, new Point(centre.X, centre.Y - radius), new Point(centre.X, centre.Y + radius));
        var puck = PuckAt(centre, radius, _value);
        if ((puck - centre).Length > 0.5)
            drawing.DrawLine(PuckEdge, centre, puck);
        drawing.DrawEllipse(PuckFill, PuckEdge, puck, PuckRadius, PuckRadius);

        // Master bar: centre notch and the current master indicator.
        var jog = Jog();
        drawing.DrawRoundedRectangle(_inset, _ring, jog, 3, 3);
        drawing.DrawLine(_ring, new Point(jog.X + jog.Width / 2, jog.Y + 2), new Point(jog.X + jog.Width / 2, jog.Bottom - 2));
        var at = jog.X + jog.Width / 2 + Math.Max(-1, Math.Min(master / JogReach, 1)) * (jog.Width / 2 - 3);
        drawing.DrawRoundedRectangle(_accent, null, new Rect(at - 3, jog.Y + 1, 6, jog.Height - 2), 2, 2);
    }

    /// <summary>Lazily built 256 x 256 disc: hue by direction, desaturating toward the centre, anti-aliased edge.</summary>
    private static BitmapSource DiscPicture()
    {
        if (_disc is { } made)
            return made;

        const int size = 256;
        var pixels = new byte[size * size * 4];
        var units = new (double, double)[3];
        for (var i = 0; i < 3; i++)
            units[i] = (Math.Cos(WheelPush.HueAngles[i] * Math.PI / 180), Math.Sin(WheelPush.HueAngles[i] * Math.PI / 180));
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                var (dx, dy) = ((column + 0.5) / (size / 2.0) - 1, 1 - (row + 0.5) / (size / 2.0));
                var r = Math.Sqrt(dx * dx + dy * dy);
                var alpha = Math.Max(0, Math.Min((1 - r) * size / 2 + 0.5, 1));
                var (ux, uy) = r > 1e-9 ? (dx / r, dy / r) : (0, 0);
                var i = (row * size + column) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var push = ux * units[channel].Item1 + uy * units[channel].Item2;
                    var level = Math.Max(0, Math.Min(0.36 + 0.5 * Math.Min(r, 1) * push, 1));
                    pixels[i + 2 - channel] = (byte)Math.Round(level * alpha * 255);
                }

                pixels[i + 3] = (byte)Math.Round(alpha * 255);
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return _disc = bitmap;
    }

    private void ReadLook()
    {
        _text = Look("TextBrush", _text);
        _muted = Look("MutedTextBrush", _muted);
        _inset = Look("InsetBrush", _inset);
        _accent = Look("AccentBrush", _accent);
        _ring = Frozen(new Pen(Look("InspectorCardBorderBrush", _ring.Brush), 1));
        InvalidateVisual();
    }

    private Brush Look(string key, Brush fallback) => TryFindResource(key) is Brush brush ? (Brush)brush.GetCurrentValueAsFrozen() : fallback;

    private static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

}
