using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// Lightning Dial: an eight-slice radial menu opened at the pointer by Mouse 5 (or its bound key). Top-level slices come from settings:
/// each an action (one flick runs it; the default dial is eight actions) or a category. Dwelling on a category, or pushing out through
/// the ring, swaps in its seven actions with Back opposite the entry slice. Held mode: release on an action runs it, release in the
/// centre cancels. A quick click switches to click mode (centre or outside click, Esc, or right-click at top level closes).
/// OnRender with cached geometry and text; hosted in a Popup by the main window so it draws over the player's native HWND.
///
/// Geometry: one continuous ring from <see cref="InnerRadius"/> to <see cref="OuterRadius"/>, 45-degree slices separated by eight
/// constant-width spokes, and a uniform moat around the hub. DialConfiguratorControl draws the same ring via <see cref="DrawRing"/>.
/// </summary>
public sealed class LightningDialOverlay : FrameworkElement
{
    /// <summary>
    /// Ring outer/inner radius, hub radius (the moat between hub and ring is the neutral centre), and spoke width, in DIPs.
    /// </summary>
    public const double OuterRadius = 134, InnerRadius = 48, HubRadius = 42, SpokeWidth = 1.5;

    /// <summary>Dial width and height (ring plus rim).</summary>
    public const double DialSize = 2 * (OuterRadius + Rim);

    private const int Slices = KeybindManager.DialSlices;
    private const double Rim = 6, PushReach = 72;

    /// <summary>Max slice label width (two lines), fitting between the spokes at the label radius.</summary>
    internal const double LabelWidth = 68;

    // Dwell before a category (or Back) opens, and the quick-click threshold.
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(120);
    private const long QuickClickMs = 350;

    private enum SlotKind { Empty, Action, Category, Back }

    private readonly record struct Slot(SlotKind Kind, string Id, string Label, string Glyph, int Category);

    private readonly Slot[] _slots = new Slot[Slices];
    private readonly FormattedText?[] _glyphs = new FormattedText?[Slices];
    private readonly FormattedText?[] _labels = new FormattedText?[Slices];
    private readonly Brush[] _fills = new Brush[Slices];
    private readonly DispatcherTimer _dwell;
    private IReadOnlyList<DialCategoryState> _dial = [];
    private int _level = -1, _hover = -1;
    private bool _held, _moved;
    private long _openedAt;
    private Point _openedFrom;
    private Point? _armedFrom;
    private FormattedText? _centre;
    private object? _themeKey;

    public LightningDialOverlay()
    {
        Width = Height = DialSize;
        Focusable = false;
        _dwell = new DispatcherTimer(DispatcherPriority.Input) { Interval = Dwell };
        _dwell.Tick += (_, _) =>
        {
            _dwell.Stop();
            Follow(_hover);
        };
    }

    /// <summary>Raised with the chosen action id; the host closes the dial and runs it.</summary>
    public event Action<string>? Chosen;

    /// <summary>Raised on cancel (centre or outside click, Esc, or hold released in the centre).</summary>
    public event Action? CloseRequested;

    /// <summary>Host predicate: whether an action can run now (disabled ones are dimmed and inert).</summary>
    public Func<string, bool>? CanRun { get; set; }

    /// <summary>True while the opening button is still held.</summary>
    public bool IsHeld => _held;

    /// <summary>
    /// Opens at the top level, or at <paramref name="category"/> when a slice key targets a category (action slices open the top level),
    /// in held or click mode.
    /// </summary>
    public void Open(IReadOnlyList<DialCategoryState> dial, int? category, bool held)
    {
        _dial = dial;
        (_held, _moved, _hover, _armedFrom) = (held, false, -1, null);
        _openedAt = Environment.TickCount64;
        // Origin is taken from the first move (the dial may be repositioned to fit, so the pointer is not at its centre).
        _openedFrom = new Point(double.NaN, double.NaN);
        _dwell.Stop();
        if (category is { } index && index >= 0 && index < dial.Count && !dial[index].IsAction)
            ShowCategory(index, entry: index);
        else
            ShowTop();
    }

    /// <summary>Captures the mouse (SubTree) so releases and clicks arrive here wherever they occur.</summary>
    public void TakeMouse()
    {
        if (!IsMouseCaptured)
            Mouse.Capture(this, CaptureMode.SubTree);
    }

    /// <summary>Releases capture and clears state (the host closes the popup).</summary>
    public void Reset()
    {
        _dwell.Stop();
        _held = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    /// <summary>Hold button released (forwarded by the host when the dial could not capture the mouse).</summary>
    public void ReleaseHold()
    {
        if (!_held)
            return;

        _held = false;
        var quick = Environment.TickCount64 - _openedAt < QuickClickMs && !_moved;
        switch (_hover >= 0 ? _slots[_hover].Kind : SlotKind.Empty)
        {
            case SlotKind.Action:
                Choose(_hover);
                break;
            case SlotKind.Category or SlotKind.Back:
                Follow(_hover);
                break;
            default:
                // Quick click: stay open in click mode; release in the centre after moving: cancel.
                if (!quick)
                    CloseRequested?.Invoke();
                break;
        }
    }

    // ----- Levels -----

    // Top level: each slice is an action (runs immediately), a category, or empty.
    private void ShowTop()
    {
        _level = -1;
        for (var i = 0; i < Slices; i++)
        {
            var slice = i < _dial.Count ? _dial[i] : null;
            _slots[i] = slice is null ? default
                : slice.IsAction ? KeybindManager.Action(slice.Action) is { } action ? new Slot(SlotKind.Action, action.Id, ShortLabel(action), action.Glyph, -1) : default
                : slice.Actions.Exists(a => a.Length > 0) ? new Slot(SlotKind.Category, "", slice.Name, slice.Glyph, i)
                : default;
        }

        Relabel();
    }

    // Category actions fill clockwise from the entry slice; Back sits opposite it.
    private void ShowCategory(int category, int entry)
    {
        _level = category;
        var back = (entry + Slices / 2) % Slices;
        var actions = _dial[category].Actions;
        var next = 0;
        for (var step = 0; step < Slices; step++)
        {
            var slot = (entry + step) % Slices;
            if (slot == back)
            {
                _slots[slot] = new Slot(SlotKind.Back, "", "Back", "\uE72B", -1);
                continue;
            }

            var id = next < actions.Count ? actions[next] : "";
            next++;
            _slots[slot] = KeybindManager.Action(id) is { } action
                ? new Slot(SlotKind.Action, id, ShortLabel(action), action.Glyph, category)
                : default;
        }

        Relabel();
    }

    // Compact slice label: "ui.dialN" actions show the slice name instead of "Lightning Dial: Slice N"; others drop a trailing period.
    private string ShortLabel(KeyAction action) =>
        action.Id.StartsWith("ui.dial", StringComparison.Ordinal) && action.Id.Length == 8 && action.Id[7] - '1' is var index && index >= 0 && index < _dial.Count
            ? _dial[index].Name
            : action.Label.TrimEnd('.');

    private void Follow(int slot)
    {
        if (slot < 0)
            return;

        switch (_slots[slot].Kind)
        {
            case SlotKind.Category:
                ShowCategory(_slots[slot].Category, slot);
                break;
            case SlotKind.Back:
                ShowTop();
                break;
            default:
                return;
        }

        // Arm against accidental choice: the new slice under the pointer is inert until it moves 8 DIPs.
        _armedFrom = Mouse.GetPosition(this);
        _hover = -1;
        Redraw();
    }

    private void Choose(int slot)
    {
        var id = _slots[slot].Id;
        if (CanRun?.Invoke(id) == false)
            return;

        Chosen?.Invoke(id);
    }

    // ----- Pointer -----

    private static int SlotAt(Point point, out double distance)
    {
        var (dx, dy) = (point.X - Centre.X, point.Y - Centre.Y);
        distance = Math.Sqrt(dx * dx + dy * dy);

        // Hub and moat are neutral (release cancels); the ring plus PushReach beyond it select by angle.
        if (distance < InnerRadius || distance > OuterRadius + PushReach)
            return -1;

        // Slot 0 is up; clockwise in 45-degree steps.
        var degrees = Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 22.5;
        degrees = (degrees % 360 + 360) % 360;
        return Math.Min((int)(degrees / 45), Slices - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (double.IsNaN(_openedFrom.X))
            _openedFrom = point;
        if (!_moved && (point - _openedFrom).Length > 6)
            _moved = true;

        // Near screen edges the dial is shifted to fit, so the pointer may start over a slice: no hover, dwell or push-through until
        // it moves more than 6 DIPs.
        if (!_moved)
            return;
        if (_armedFrom is { } armed)
        {
            if ((point - armed).Length < 8)
                return;
            _armedFrom = null;
        }

        var slot = SlotAt(point, out var distance);
        if (slot >= 0 && _slots[slot].Kind == SlotKind.Empty)
            slot = -1;

        if (slot != _hover)
        {
            _hover = slot;
            _dwell.Stop();
            if (slot >= 0 && _slots[slot].Kind is SlotKind.Category or SlotKind.Back)
                _dwell.Start();
            Redraw();
        }

        // Push-through past the ring opens the category (or Back) immediately.
        if (slot >= 0 && distance > OuterRadius && _slots[slot].Kind is SlotKind.Category or SlotKind.Back)
        {
            _dwell.Stop();
            Follow(slot);
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        e.Handled = true;
        if (_held)
        {
            // Only X1/X2/Middle releases end the hold here; the host calls ReleaseHold for the actual hold source.
            if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2 or MouseButton.Middle)
                ReleaseHold();
            return;
        }

        var slot = SlotAt(e.GetPosition(this), out _);
        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                switch (slot >= 0 ? _slots[slot].Kind : SlotKind.Empty)
                {
                    case SlotKind.Action:
                        Choose(slot);
                        break;
                    case SlotKind.Category or SlotKind.Back:
                        _dwell.Stop();
                        Follow(slot);
                        break;
                    case SlotKind.Empty when slot >= 0:
                        break;
                    default:
                        CloseRequested?.Invoke();
                        break;
                }

                break;

            // Right button: back one level; closes at the top level.
            case MouseButton.Right:
                if (_level >= 0)
                {
                    ShowTop();
                    _hover = -1;
                    Redraw();
                }
                else
                {
                    CloseRequested?.Invoke();
                }

                break;

            // Opening button clicked again: close.
            case MouseButton.XButton1 or MouseButton.XButton2:
                CloseRequested?.Invoke();
                break;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture taken by another window (dialog or app): close rather than linger.
        if (IsVisible && Mouse.Captured is not null && !IsAncestorOf((DependencyObject)Mouse.Captured))
            CloseRequested?.Invoke();
    }

    // ----- Ring geometry (shared with DialConfiguratorControl) -----

    /// <summary>Ring centre in dial coordinates.</summary>
    internal static readonly Point Centre = new(OuterRadius + Rim, OuterRadius + Rim);

    // Slot i centred at -90 + 45i degrees (0 = up, clockwise); edges at +/-22.5 degrees.
    private static double EdgeOf(int slot) => -112.5 + slot * 45;

    private static Point At(double degrees, double radius)
    {
        var angle = degrees * Math.PI / 180;
        return new Point(Centre.X + radius * Math.Cos(angle), Centre.Y + radius * Math.Sin(angle));
    }

    // Eight frozen annular sectors, edge to edge, shared by all levels; unstroked (spokes drawn over the seams avoid doubled edges).
    private static readonly StreamGeometry[] Sectors = MakeSectors();

    // All eight spokes in one geometry, stroked once.
    private static readonly StreamGeometry Spokes = MakeSpokes();

    private static StreamGeometry[] MakeSectors()
    {
        var sectors = new StreamGeometry[Slices];
        for (var i = 0; i < Slices; i++)
        {
            var (from, to) = (EdgeOf(i), EdgeOf(i + 1));
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(At(from, InnerRadius), true, true);
                context.LineTo(At(from, OuterRadius), false, false);
                context.ArcTo(At(to, OuterRadius), new Size(OuterRadius, OuterRadius), 0, false, SweepDirection.Clockwise, false, false);
                context.LineTo(At(to, InnerRadius), false, false);
                context.ArcTo(At(from, InnerRadius), new Size(InnerRadius, InnerRadius), 0, false, SweepDirection.Counterclockwise, false, false);
            }

            geometry.Freeze();
            sectors[i] = geometry;
        }

        return sectors;
    }

    private static StreamGeometry MakeSpokes()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < Slices; i++)
            {
                context.BeginFigure(At(EdgeOf(i), InnerRadius), false, false);
                context.LineTo(At(EdgeOf(i), OuterRadius), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Draws the ring in dial coordinates (centre <see cref="Centre"/>): eight sector fills (<paramref name="fills"/>, clockwise from top),
    /// spokes over the seams, outer and inner edges, and the hub. The moat between hub and ring stays transparent.
    /// </summary>
    internal static void DrawRing(DrawingContext drawing, IReadOnlyList<Brush> fills, Brush hub, Pen edge, Pen spoke)
    {
        for (var i = 0; i < Slices; i++)
            drawing.DrawGeometry(fills[i], null, Sectors[i]);

        drawing.DrawGeometry(null, spoke, Spokes);
        drawing.DrawEllipse(null, edge, Centre, OuterRadius, OuterRadius);
        drawing.DrawEllipse(null, edge, Centre, InnerRadius, InnerRadius);
        drawing.DrawEllipse(hub, edge, Centre, HubRadius, HubRadius);
    }

    /// <summary>Draws a slice's glyph above its label, centred in the slice.</summary>
    internal static void DrawSliceText(DrawingContext drawing, int slot, FormattedText glyph, FormattedText label)
    {
        var at = At(-90 + slot * 45, (InnerRadius + OuterRadius) / 2 + 2);
        var height = glyph.Height + label.Height;
        drawing.DrawText(glyph, new Point(at.X - glyph.Width / 2, at.Y - height / 2));
        drawing.DrawText(label, new Point(at.X - LabelWidth / 2, at.Y - height / 2 + glyph.Height));
    }

    /// <summary>Slice glyph text (Segoe MDL2 Assets, 20 DIP).</summary>
    internal static FormattedText GlyphText(string glyph, Brush brush, double dpi) =>
        new(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, GlyphFace, 20, brush, dpi);

    /// <summary>Slice label text: max two lines, centred, ellipsis-trimmed.</summary>
    internal static FormattedText LabelText(string label, Brush brush, double dpi) =>
        new(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, brush, dpi)
        {
            MaxTextWidth = LabelWidth,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    /// <summary>Hub text: max three lines, centred within the hub.</summary>
    internal static FormattedText CentreText(string text, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11, brush, dpi)
        {
            MaxTextWidth = 2 * HubRadius - 10,
            MaxLineCount = 3,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    /// <summary>Frozen flat-capped pen for ring edges (1 DIP) or spokes (<see cref="SpokeWidth"/>).</summary>
    internal static Pen RingPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

    private static readonly Typeface GlyphFace = new("Segoe MDL2 Assets");
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // ----- Rendering -----

    private Brush Themed(string key, Color fallback) => TryFindResource(key) as Brush ?? Frozen(fallback);

    // Edge and spoke pens, rebuilt only when the theme border brush changes.
    private Pen? _edge, _spoke;
    private Brush? _edgeBrush;

    private (Pen Edge, Pen Spoke) Pens(Brush brush)
    {
        if (_edge is null || _spoke is null || !ReferenceEquals(_edgeBrush, brush))
            (_edge, _spoke, _edgeBrush) = (RingPen(brush, 1), RingPen(brush, SpokeWidth), brush);

        return (_edge, _spoke);
    }

    // Level changed: rebuild glyphs and labels on the next render (for the current theme and DPI).
    private void Relabel()
    {
        _labelsStale = true;
        Redraw();
    }

    private bool _labelsStale = true;
    private double _labelsDpi;

    // Builds slice glyphs and labels for the current level; only on level, theme or DPI change, never on pointer moves.
    private void MakeLabels(Brush text, double dpi)
    {
        var muted = Themed("MutedTextBrush", Color.FromRgb(0xA0, 0xA0, 0xA0));
        (_themeKey, _labelsDpi, _labelsStale) = (text, dpi, false);
        for (var i = 0; i < Slices; i++)
        {
            var slot = _slots[i];
            if (slot.Kind == SlotKind.Empty)
            {
                (_glyphs[i], _labels[i]) = (null, null);
                continue;
            }

            var dim = slot.Kind == SlotKind.Action && CanRun?.Invoke(slot.Id) == false;
            _glyphs[i] = GlyphText(slot.Glyph, dim ? muted : text, dpi);
            _labels[i] = LabelText(slot.Label, dim ? muted : text, dpi);
        }

        _centre = null;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var text = Themed("TextBrush", Colors.White);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_labelsStale || !ReferenceEquals(text, _themeKey) || dpi != _labelsDpi)
            MakeLabels(text, dpi);

        var face = Themed("CardBrush", Color.FromRgb(0x2D, 0x2D, 0x2D));
        var hover = Themed("ControlHoverBrush", Color.FromRgb(0x3A, 0x3A, 0x3A));
        var accent = Themed("AccentBrush", Color.FromRgb(0x00, 0x78, 0xD7));
        var well = Themed("InsetBrush", Color.FromRgb(0x18, 0x18, 0x18));
        var (edge, spoke) = Pens(Themed("ControlBorderBrush", Color.FromRgb(0x3F, 0x3F, 0x3F)));

        for (var i = 0; i < Slices; i++)
        {
            var kind = _slots[i].Kind;
            _fills[i] = kind == SlotKind.Empty ? well
                : i == _hover ? kind == SlotKind.Action ? accent : hover
                : face;
        }

        DrawRing(drawing, _fills, face, edge, spoke);
        for (var i = 0; i < Slices; i++)
            if (_glyphs[i] is { } glyph && _labels[i] is { } label)
                DrawSliceText(drawing, i, glyph, label);

        // Hub text: hovered slice label, else current level name.
        _centre ??= CentreText(CentreWords(), text, dpi);
        drawing.DrawText(_centre, new Point(Centre.X - _centre.MaxTextWidth / 2, Centre.Y - _centre.Height / 2));
    }

    private string CentreWords() =>
        _hover >= 0 && _slots[_hover].Kind != SlotKind.Empty ? _slots[_hover].Label
        : _level >= 0 && _level < _dial.Count ? _dial[_level].Name
        : "Lightning Dial";

    // Redraw on hover or level change (invalidates the hub text).
    private void Redraw()
    {
        _centre = null;
        InvalidateVisual();
    }
}
