using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// One of the Color Studio's three wheels (Pass 5.6A: Lift, Gamma or Gain): a colour disc whose puck is the wheel's colour push (its red, green
/// and blue less their mean, drawn towards the hue they push), and a master jog bar under it that moves all three alike. The puck follows
/// a drag (Shift holds it to the axis it set off along; Ctrl finely); the bar takes a drag across; a double-click puts the wheel (on the disc)
/// or its master (on the bar) back to 0. Mouse, pen and touch (the touch captured and handled here, so it is not a mouse too). The pointer
/// logic is in point methods (<see cref="BeginDrag"/>, <see cref="DragTo"/>, <see cref="EndDrag"/>) the goldens drive directly. A gesture
/// is <see cref="EditStarted"/> .. <see cref="EditEnded"/>, its values said by <see cref="ValueChanged"/>; the pane writes them (one step
/// of Undo a gesture). One OnRender; the disc is one frozen bitmap shared by every wheel.
/// </summary>
public sealed class ColorWheelsControl : FrameworkElement
{
    /// <summary>The colour push at the disc's edge (each channel of a wheel is -1..1; the puck's push is the channels less their mean).</summary>
    public const double Reach = 0.5;

    /// <summary>The master's change for a drag across the whole bar.</summary>
    public const double JogReach = 1;

    private const double TitleHeight = 18, JogHeight = 12, JogGap = 8, Pad = 2, PuckRadius = 5, FineScale = 0.2, AxisLockDip = 3;

    // Where each primary's push points on the disc, degrees anticlockwise from the right (a vectorscope's order: red upper left, green lower
    // left, blue right), 120 degrees apart: the puck's offset P gives channel i the push P . e_i, and a push c gives P = 2/3 sum c_i e_i.
    private static readonly double[] HueAngles = [105, 225, 345];

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Pen PuckEdge = Frozen(new Pen(Solid(255, 0x10, 0x10, 0x12), 1.2));
    private static readonly Pen CrossPen = Frozen(new Pen(Solid(70, 0, 0, 0), 1));
    private static readonly Brush PuckFill = Solid(255, 0xF4, 0xF4, 0xF4);
    private static BitmapSource? _disc;

    private Brush _text = Solid(255, 0xE0, 0xE0, 0xE0), _muted = Solid(255, 0x90, 0x90, 0x90), _inset = Solid(255, 0x16, 0x16, 0x18),
        _accent = Solid(255, 0x4F, 0xC3, 0xF7);
    private Pen _ring = Frozen(new Pen(Solid(255, 0x2A, 0x2A, 0x30), 1));
    private (double R, double G, double B) _value;

    // The gesture: the part it began on, where and with what; the axis Shift holds it to (0 none yet, 1 across, 2 up and down).
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

    /// <summary>The wheel's name ("Lift", "Gamma", "Gain"), drawn over it.</summary>
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

    /// <summary>The wheel's red, green and blue (each -1..1). Set from the card: drawn, nothing raised.</summary>
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

    /// <summary>Whether a gesture is under way (the pane leaves the wheel's value alone meanwhile).</summary>
    public bool IsDragging => _part != Part.None;

    /// <summary>A gesture changed the value (<see cref="Value"/>).</summary>
    public event Action<ColorWheelsControl>? ValueChanged;

    /// <summary>A gesture began (a drag, a double-click's reset): what it changes until it ends is one step.</summary>
    public event Action<ColorWheelsControl>? EditStarted;

    /// <summary>The gesture ended.</summary>
    public event Action<ColorWheelsControl>? EditEnded;

    // ----- The maths: a value as its master and its push, and back (pure: the goldens) -----

    /// <summary>The master (the mean of the three) and the puck's offset (in units of push: <see cref="Reach"/> is the disc's edge, up positive).</summary>
    public static (double Master, double X, double Y) Split((double R, double G, double B) value)
    {
        var master = (value.R + value.G + value.B) / 3;
        var (x, y) = (0.0, 0.0);
        var pushes = (value.R - master, value.G - master, value.B - master);
        for (var i = 0; i < 3; i++)
        {
            var push = i == 0 ? pushes.Item1 : i == 1 ? pushes.Item2 : pushes.Item3;
            var angle = HueAngles[i] * Math.PI / 180;
            (x, y) = (x + 2.0 / 3 * push * Math.Cos(angle), y + 2.0 / 3 * push * Math.Sin(angle));
        }

        return (master, x, y);
    }

    /// <summary>The value of a master and a puck offset (<see cref="Split"/>'s inverse), each channel held to -1..1.</summary>
    public static (double R, double G, double B) Join(double master, double x, double y)
    {
        double Channel(int i)
        {
            var angle = HueAngles[i] * Math.PI / 180;
            return Math.Max(-1, Math.Min(master + x * Math.Cos(angle) + y * Math.Sin(angle), 1));
        }

        return (Channel(0), Channel(1), Channel(2));
    }

    // ----- Where things are -----

    private (Point Centre, double Radius) Disc()
    {
        var room = Math.Max(Math.Min(ActualWidth - 2 * Pad, ActualHeight - TitleHeight - JogGap - JogHeight - 2 * Pad), 8);
        return (new Point(ActualWidth / 2, TitleHeight + Pad + room / 2), room / 2);
    }

    /// <summary>The disc's middle (DIPs): where the goldens press.</summary>
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

    // ----- The gesture (point methods: the mouse, the pen and touch call them; the goldens too) -----

    /// <summary>A press at <paramref name="p"/> (DIPs): a drag begins when it is on the disc or the bar. False elsewhere.</summary>
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

    /// <summary>The pointer at <paramref name="p"/>: the puck moved by as much as the pointer has (Shift: along one axis; Ctrl: finely), or the master.</summary>
    public void DragTo(Point p, bool lockAxis = false, bool fine = false)
    {
        if (_part == Part.None)
            return;

        var (dx, dy) = (p.X - _from.X, p.Y - _from.Y);
        var scale = fine ? FineScale : 1;
        var (master, x, y) = Split(_start);
        if (_part == Part.Jog)
        {
            master += dx / Math.Max(Jog().Width, 1) * JogReach * scale;
        }
        else
        {
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

            var perDip = Reach / Math.Max(Disc().Radius, 1) * scale;
            (x, y) = (x + dx * perDip, y - dy * perDip);
            var length = Math.Sqrt(x * x + y * y);
            if (length > Reach)
                (x, y) = (x * Reach / length, y * Reach / length);
        }

        var value = Join(master, x, y);
        if (value == _value)
            return;
        _value = value;
        InvalidateVisual();
        ValueChanged?.Invoke(this);
    }

    /// <summary>The gesture ends (the pointer let go, the capture lost, the control unloaded).</summary>
    public void EndDrag()
    {
        if (_part == Part.None)
            return;
        (_part, _axis) = (Part.None, 0);
        EditEnded?.Invoke(this);
    }

    /// <summary>The whole wheel (<paramref name="masterOnly"/>: its master alone, the push kept) back to 0, as one gesture.</summary>
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

    // ----- Drawing -----

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(Math.Min(availableSize.Width, 220), 72) : 140;
        return new Size(width, Math.Min(width, 220) + TitleHeight + JogGap + JogHeight + 2 * Pad);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (centre, radius) = Disc();
        var (master, x, y) = Split(_value);

        // The title, and the master's value at the right.
        var title = new FormattedText(_title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, _text, dpi);
        drawing.DrawText(title, new Point(centre.X - radius, 0));
        var number = new FormattedText(master.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, _muted, dpi);
        drawing.DrawText(number, new Point(centre.X + radius - number.Width, 1));

        // The disc, its edge and a faint cross; the puck on a line from the middle.
        drawing.DrawImage(DiscPicture(), new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2));
        drawing.DrawEllipse(null, _ring, centre, radius, radius);
        drawing.DrawLine(CrossPen, new Point(centre.X - radius, centre.Y), new Point(centre.X + radius, centre.Y));
        drawing.DrawLine(CrossPen, new Point(centre.X, centre.Y - radius), new Point(centre.X, centre.Y + radius));
        var puck = new Point(centre.X + x / Reach * radius, centre.Y - y / Reach * radius);
        if ((puck - centre).Length > 0.5)
            drawing.DrawLine(PuckEdge, centre, puck);
        drawing.DrawEllipse(PuckFill, PuckEdge, puck, PuckRadius, PuckRadius);

        // The master's bar: its notch in the middle and where the master stands.
        var jog = Jog();
        drawing.DrawRoundedRectangle(_inset, _ring, jog, 3, 3);
        drawing.DrawLine(_ring, new Point(jog.X + jog.Width / 2, jog.Y + 2), new Point(jog.X + jog.Width / 2, jog.Bottom - 2));
        var at = jog.X + jog.Width / 2 + Math.Max(-1, Math.Min(master / JogReach, 1)) * (jog.Width / 2 - 3);
        drawing.DrawRoundedRectangle(_accent, null, new Rect(at - 3, jog.Y + 1, 6, jog.Height - 2), 2, 2);
    }

    /// <summary>The disc, made once (256 x 256): each point the hue its direction pushes towards, greyer towards the middle, its edge smoothed.</summary>
    private static BitmapSource DiscPicture()
    {
        if (_disc is { } made)
            return made;

        const int size = 256;
        var pixels = new byte[size * size * 4];
        var units = HueAngles.Select(a => (Math.Cos(a * Math.PI / 180), Math.Sin(a * Math.PI / 180))).ToArray();
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
