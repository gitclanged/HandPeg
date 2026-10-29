using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// The Lightning Dial (4.4): a ring of eight slices opened at the pointer by Mouse 5 (or the key it is bound to). Its top
/// level holds the seven categories of the settings' dial and, in the eighth slice, Settings. Resting on a category for a
/// moment, or pushing the pointer out through the ring, swaps the slices in place for that category's seven actions, with
/// Back in the slice opposite the one the pointer came in by. Held open, releasing the button on an action runs it and
/// releasing it in the middle cancels; a quick click leaves the dial open to be used with clicks (a click in the middle
/// or outside, Esc, or a right-click on the top level closes it). Drawn in OnRender with cached geometry and text; hosted
/// by the main window in a Popup (it must be drawn over the player's native window).
///
/// 4.4-BugHunt (the user's design): one unbroken ring from <see cref="InnerRadius"/> to <see cref="OuterRadius"/>, each
/// slice a full 45 degrees, divided by eight straight spokes of one constant width; inside it a moat of one width all round
/// the hub. The settings' picture of the dial draws the same ring (<see cref="DrawRing"/>, DialConfiguratorControl).
/// </summary>
public sealed class LightningDialOverlay : FrameworkElement
{
    /// <summary>
    /// The ring's outer and inner radius, the hub's (between the hub and the ring is the moat: the middle, where nothing is
    /// chosen), and the spokes' width, in DIPs.
    /// </summary>
    public const double OuterRadius = 134, InnerRadius = 48, HubRadius = 42, SpokeWidth = 1.5;

    /// <summary>The dial's width and height (the ring and a rim round it).</summary>
    public const double DialSize = 2 * (OuterRadius + Rim);

    private const int Slices = 8, SettingsSlot = 7;
    private const double Rim = 6, PushReach = 72;

    /// <summary>How wide a slice's name may be (two lines at most): inside the slice's two spokes, at the names' radius.</summary>
    internal const double LabelWidth = 68;

    // How long the pointer rests on a category (or Back) before it opens, and what a quick click is.
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

    /// <summary>An action was chosen: the host closes the dial and runs it.</summary>
    public event Action<string>? Chosen;

    /// <summary>The dial was closed (a click in the middle or outside, Esc, or the button let go in the middle).</summary>
    public event Action? CloseRequested;

    /// <summary>Whether an action can run now: one that cannot is drawn dimmed and does nothing.</summary>
    public Func<string, bool>? CanRun { get; set; }

    /// <summary>Whether the dial is being held open (the button that opened it is still down).</summary>
    public bool IsHeld => _held;

    /// <summary>Starts the dial on its top level, or, for a category's own key, on that category, held or in click mode.</summary>
    public void Open(IReadOnlyList<DialCategoryState> dial, int? category, bool held)
    {
        _dial = dial;
        (_held, _moved, _hover, _armedFrom) = (held, false, -1, null);
        _openedAt = Environment.TickCount64;
        // Where the pointer was is known with its first move over the dial (which is not under it when put where it fits).
        _openedFrom = new Point(double.NaN, double.NaN);
        _dwell.Stop();
        if (category is { } index && index >= 0 && index < dial.Count)
            ShowCategory(index, entry: index);
        else
            ShowTop();
    }

    /// <summary>Takes the mouse, so that the button's release and every click come here, wherever they are.</summary>
    public void TakeMouse()
    {
        if (!IsMouseCaptured)
            Mouse.Capture(this, CaptureMode.SubTree);
    }

    /// <summary>Lets the mouse go and forgets the state (the host closes the popup).</summary>
    public void Reset()
    {
        _dwell.Stop();
        _held = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    /// <summary>The button that holds the dial open was released (heard by the host while the dial could not take the mouse).</summary>
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
                // A quick click leaves the dial open, to be used with clicks; a release in the middle after moving cancels.
                if (!quick)
                    CloseRequested?.Invoke();
                break;
        }
    }

    // ----- Levels -----

    // The top level: the seven categories, and Settings in the eighth slice (an action like any other: chosen, it opens the
    // full-window Settings).
    private void ShowTop()
    {
        _level = -1;
        for (var i = 0; i < Slices; i++)
        {
            _slots[i] = i == SettingsSlot ? new Slot(SlotKind.Action, "ui.settings", "Settings", SettingsGlyph, -1)
                : i < _dial.Count ? new Slot(SlotKind.Category, "", _dial[i].Name, _dial[i].Glyph, i)
                : default;
        }

        Relabel();
    }

    /// <summary>The Settings slice's glyph (the action's own, a cog).</summary>
    internal static string SettingsGlyph => KeybindManager.Action("ui.settings")?.Glyph ?? "\uE713";

    // The category's actions fill the slices clockwise from the one the pointer came in by; Back is opposite it.
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

    // Long labels lose what the dial makes plain ("Go to Start" stays; "Lightning Dial: Category 2" becomes the category's name).
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

        // What is now under the pointer is not chosen until the pointer moves on: a release right after the swap is no choice.
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

        // The hub and its moat are the middle (a release there cancels); the ring, and a reach outside it, choose by angle.
        if (distance < InnerRadius || distance > OuterRadius + PushReach)
            return -1;

        // Slot 0 is straight up; they go round clockwise, 45 degrees each.
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

        // Near a screen edge the dial is put where it fits, so the pointer may sit over a slice the moment it opens: nothing
        // is hovered (or dwelt on, or pushed through) until the pointer has really moved.
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

        // Pushed out through the ring: the category (or Back) opens at once.
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
            // Only the button that holds the dial open ends the hold (the host tells which by ReleaseHold).
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

            // The right button goes back a level, and closes the top one.
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

            // Clicked again with the button that opened it: closed.
            case MouseButton.XButton1 or MouseButton.XButton2:
                CloseRequested?.Invoke();
                break;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Another window took the mouse (a dialog, or another program): the dial is not left standing.
        if (IsVisible && Mouse.Captured is not null && !IsAncestorOf((DependencyObject)Mouse.Captured))
            CloseRequested?.Invoke();
    }

    // ----- The ring (shared with the settings' picture of the dial) -----

    /// <summary>The ring's middle, in the dial's own coordinates.</summary>
    internal static readonly Point Centre = new(OuterRadius + Rim, OuterRadius + Rim);

    // Slot i is centred on -90 + 45 i degrees (0 straight up, clockwise): its edges lie 22.5 degrees either side.
    private static double EdgeOf(int slot) => -112.5 + slot * 45;

    private static Point At(double degrees, double radius)
    {
        var angle = degrees * Math.PI / 180;
        return new Point(Centre.X + radius * Math.Cos(angle), Centre.Y + radius * Math.Sin(angle));
    }

    // The slices' fills: annular sectors meeting edge to edge, the same eight for every level, made once. Not stroked (no
    // doubled edges): the spokes drawn over the seams divide them.
    private static readonly StreamGeometry[] Sectors = MakeSectors();

    // The eight spokes as one geometry, stroked once.
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
    /// Draws the ring in the dial's own coordinates (its middle at <see cref="Centre"/>): the eight slices filled edge to edge
    /// (<paramref name="fills"/>, one a slice, clockwise from the top), the eight spokes over their seams, the rim and the
    /// ring's inner edge, and the hub. The moat between the hub and the ring is left see-through.
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

    /// <summary>A slice's glyph above its name, both centred in the slice.</summary>
    internal static void DrawSliceText(DrawingContext drawing, int slot, FormattedText glyph, FormattedText label)
    {
        var at = At(-90 + slot * 45, (InnerRadius + OuterRadius) / 2 + 2);
        var height = glyph.Height + label.Height;
        drawing.DrawText(glyph, new Point(at.X - glyph.Width / 2, at.Y - height / 2));
        drawing.DrawText(label, new Point(at.X - LabelWidth / 2, at.Y - height / 2 + glyph.Height));
    }

    /// <summary>A slice's glyph as the dial draws it.</summary>
    internal static FormattedText GlyphText(string glyph, Brush brush, double dpi) =>
        new(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, GlyphFace, 20, brush, dpi);

    /// <summary>A slice's name as the dial draws it: two lines at most, centred, cut short with an ellipsis.</summary>
    internal static FormattedText LabelText(string label, Brush brush, double dpi) =>
        new(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, brush, dpi)
        {
            MaxTextWidth = LabelWidth,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    /// <summary>The text in the hub as the dial draws it: three lines at most, centred, inside the hub.</summary>
    internal static FormattedText CentreText(string text, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11, brush, dpi)
        {
            MaxTextWidth = 2 * HubRadius - 10,
            MaxLineCount = 3,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    /// <summary>The pen of the ring's rim and edges (1 DIP), or of its spokes (<see cref="SpokeWidth"/>), in a brush: frozen.</summary>
    internal static Pen RingPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

    private static readonly Typeface GlyphFace = new("Segoe MDL2 Assets");
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // ----- Drawing -----

    private Brush Themed(string key, Color fallback) => TryFindResource(key) as Brush ?? Frozen(fallback);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // The rim's and the spokes' pens, made again only when the theme's outline brush changes.
    private Pen? _edge, _spoke;
    private Brush? _edgeBrush;

    private (Pen Edge, Pen Spoke) Pens(Brush brush)
    {
        if (_edge is null || _spoke is null || !ReferenceEquals(_edgeBrush, brush))
            (_edge, _spoke, _edgeBrush) = (RingPen(brush, 1), RingPen(brush, SpokeWidth), brush);

        return (_edge, _spoke);
    }

    // A new level: its glyphs and names are made at the next drawing (with the theme and the screen it is drawn for).
    private void Relabel()
    {
        _labelsStale = true;
        Redraw();
    }

    private bool _labelsStale = true;
    private double _labelsDpi;

    // The slices' glyphs and names for the level shown: made when the level, the theme or the screen changes, never when
    // the pointer moves.
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

        // The middle: what the pointer is on, or where the dial is.
        _centre ??= CentreText(CentreWords(), text, dpi);
        drawing.DrawText(_centre, new Point(Centre.X - _centre.MaxTextWidth / 2, Centre.Y - _centre.Height / 2));
    }

    private string CentreWords() =>
        _hover >= 0 && _slots[_hover].Kind != SlotKind.Empty ? _slots[_hover].Label
        : _level >= 0 && _level < _dial.Count ? _dial[_level].Name
        : "Lightning Dial";

    // Draws again for a new slice under the pointer or a new level (the middle's text follows them).
    private void Redraw()
    {
        _centre = null;
        InvalidateVisual();
    }
}
