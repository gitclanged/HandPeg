using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// The keybind page's map (Settings > Keybinds &amp; Lightning Dial, 4.4), on the settings' stage: a keyboard and a mouse
/// drawn from their shapes, each key showing what it does in the layer chosen above it (no modifier, Ctrl, Shift, Alt, or
/// their pairs; 4.4-BugHunt: the held navigation keys are ordinary keys of the first, and Caps Lock is a key like the
/// others). Clicking a key or a mouse button offers every action for it; a key two actions share is tinted. Edits go to the
/// page's <see cref="KeybindDraft"/>, in effect once the settings are saved.
///
/// As a glance (<see cref="Glance"/>, 4.4.0: over the player while Tab holds X-Ray) it changes nothing: the pointer on a key
/// or a mouse button names its action, its chord and what it does, and a click on one (or on the "Edit Keybinds" pill)
/// asks for the keybind page at it (<see cref="EditRequested"/>).
/// </summary>
public sealed class KeybindMapControl : FrameworkElement
{
    private readonly record struct KeyCap(Key Key, double X, double Row, double Width);

    private readonly record struct KeyLayer(string Name, ModifierKeys Mods);

    private static readonly KeyLayer[] Layers =
    [
        new("No Modifier", ModifierKeys.None),
        new("Ctrl", ModifierKeys.Control),
        new("Shift", ModifierKeys.Shift),
        new("Alt", ModifierKeys.Alt),
        new("Ctrl+Shift", ModifierKeys.Control | ModifierKeys.Shift),
        new("Ctrl+Alt", ModifierKeys.Control | ModifierKeys.Alt),
    ];

    // The keyboard, in key widths: a function row, the five main rows, and the editing and arrow keys beside them.
    private static readonly KeyCap[] Caps = MakeCaps();

    private static KeyCap[] MakeCaps()
    {
        var caps = new List<KeyCap>();
        void Row(double row, double x, params (Key Key, double Width)[] keys)
        {
            foreach (var (key, width) in keys)
            {
                if (key != Key.None)
                    caps.Add(new KeyCap(key, x, row, width));
                x += width;
            }
        }

        static (Key, double) K(Key key, double width = 1) => (key, width);
        static (Key, double) Gap(double width) => (Key.None, width);

        Row(0, 0, K(Key.Escape), Gap(1), K(Key.F1), K(Key.F2), K(Key.F3), K(Key.F4), Gap(0.5), K(Key.F5), K(Key.F6), K(Key.F7), K(Key.F8),
            Gap(0.5), K(Key.F9), K(Key.F10), K(Key.F11), K(Key.F12));
        Row(1, 0, K(Key.Oem3), K(Key.D1), K(Key.D2), K(Key.D3), K(Key.D4), K(Key.D5), K(Key.D6), K(Key.D7), K(Key.D8), K(Key.D9), K(Key.D0),
            K(Key.OemMinus), K(Key.OemPlus), K(Key.Back, 2), Gap(0.5), K(Key.Insert), K(Key.Home), K(Key.PageUp));
        Row(2, 0, K(Key.Tab, 1.5), K(Key.Q), K(Key.W), K(Key.E), K(Key.R), K(Key.T), K(Key.Y), K(Key.U), K(Key.I), K(Key.O), K(Key.P),
            K(Key.Oem4), K(Key.Oem6), K(Key.Oem5, 1.5), Gap(0.5), K(Key.Delete), K(Key.End), K(Key.PageDown));
        Row(3, 0, K(Key.CapsLock, 1.75), K(Key.A), K(Key.S), K(Key.D), K(Key.F), K(Key.G), K(Key.H), K(Key.J), K(Key.K), K(Key.L),
            K(Key.Oem1), K(Key.Oem7), K(Key.Enter, 2.25));
        Row(4, 0, K(Key.LeftShift, 2.25), K(Key.Z), K(Key.X), K(Key.C), K(Key.V), K(Key.B), K(Key.N), K(Key.M), K(Key.OemComma),
            K(Key.OemPeriod), K(Key.Oem2), K(Key.RightShift, 2.75), Gap(1.5), K(Key.Up));
        Row(5, 0, K(Key.LeftCtrl, 1.25), K(Key.LWin, 1.25), K(Key.LeftAlt, 1.25), K(Key.Space, 6.25), K(Key.RightAlt, 1.25),
            K(Key.RWin, 1.25), K(Key.Apps, 1.25), K(Key.RightCtrl, 1.25), Gap(0.5), K(Key.Left), K(Key.Down), K(Key.Right));
        return [.. caps];
    }

    private const double KeyboardUnits = 18.5, MouseUnits = 4.6, LayerRowHeight = 30, StatusHeight = 34, MaxUnit = 52;

    private enum MousePart { None, Left, Right, Middle, Back, Forward }

    private readonly KeybindDraft _draft;
    private int _layer;
    private int _hoverCap = -1, _hoverLayer = -1;
    private MousePart _hoverMouse;
    private string _status = "Click a key or a mouse button to choose what it does.";
    private bool _hoverEdit;
    private int _pickedCap = -1;

    /// <summary>Shown over the editor (X-Ray): nothing is edited here; a click asks for the keybind page (<see cref="EditRequested"/>).</summary>
    public bool Glance { get; init; }

    /// <summary>A glance's key or mouse button clicked (its chord), or its "Edit Keybinds" pill (null).</summary>
    public event Action<InputChord?>? EditRequested;

    // What the keys show, made when the layer, the keys or the theme change (not when the pointer moves).
    private FormattedText?[] _names = [], _actions = [];
    private bool[] _bound = [], _conflict = [];
    private bool _stale = true;
    private object? _themeKey;
    private double _textDpi, _textUnit;

    public KeybindMapControl(KeybindDraft draft)
    {
        _draft = draft;
        Focusable = false;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "Keybind map");
        draft.Changed += () =>
        {
            _stale = true;
            InvalidateVisual();
        };
    }

    // ----- Layout -----

    private double Unit(double width) => Math.Clamp(width / (KeyboardUnits + MouseUnits + 0.6), 18, MaxUnit);

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 900 : available.Width;
        var unit = Unit(width);
        return new Size(width, LayerRowHeight + 10 + 6.5 * unit + StatusHeight);
    }

    private double KeyboardTop => LayerRowHeight + 10;

    private Rect CapRect(KeyCap cap, double unit)
    {
        // The function row stands a little apart from the others.
        var top = KeyboardTop + cap.Row * unit + (cap.Row > 0 ? unit * 0.4 : 0);
        return new Rect(cap.X * unit + 1.5, top + 1.5, cap.Width * unit - 3, unit - 3);
    }

    private Rect MouseBody(double unit) => new((KeyboardUnits + 0.6) * unit, KeyboardTop + 0.4 * unit, 3.4 * unit, 5.6 * unit);

    private Rect MouseRect(MousePart part, double unit)
    {
        var body = MouseBody(unit);
        var half = body.Width / 2;
        return part switch
        {
            MousePart.Left => new Rect(body.X, body.Y, half - 1, body.Height * 0.42),
            MousePart.Right => new Rect(body.X + half + 1, body.Y, half - 1, body.Height * 0.42),
            MousePart.Middle => new Rect(body.X + half - 0.32 * unit, body.Y + 0.45 * unit, 0.64 * unit, 1.1 * unit),
            MousePart.Forward => new Rect(body.X - 0.42 * unit, body.Y + body.Height * 0.46, 0.5 * unit, 0.85 * unit),
            MousePart.Back => new Rect(body.X - 0.42 * unit, body.Y + body.Height * 0.46 + 1.0 * unit, 0.5 * unit, 0.85 * unit),
            _ => Rect.Empty,
        };
    }

    private static MouseButton? ButtonOf(MousePart part) => part switch
    {
        MousePart.Left => MouseButton.Left,
        MousePart.Right => MouseButton.Right,
        MousePart.Middle => MouseButton.Middle,
        MousePart.Back => MouseButton.XButton1,
        MousePart.Forward => MouseButton.XButton2,
        _ => null,
    };

    private Rect LayerRect(int index, double dpi)
    {
        var widths = LayerWidths(dpi);
        var x = 0.0;
        for (var i = 0; i < index; i++)
            x += widths[i] + 6;
        return new Rect(x, 2, widths[index], LayerRowHeight - 6);
    }

    private double[]? _layerWidths;
    private double _layerWidthsDpi;

    // The layers' buttons are as wide as their names (measured once for the screen they are drawn on).
    private double[] LayerWidths(double dpi)
    {
        if (_layerWidths is null || dpi != _layerWidthsDpi)
        {
            _layerWidths = Layers.Select(l => Text(l.Name, 11.5, Brushes.White, dpi).Width + 20).ToArray();
            _layerWidthsDpi = dpi;
        }

        return _layerWidths;
    }

    // ----- What a key does -----

    private InputChord ChordOf(Key key) => new(Layers[_layer].Mods, key);

    // (Caps Lock is not one: it can run an action, 4.4-BugHunt.)
    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin;

    private ModifierKeys ModifierOf(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        _ => ModifierKeys.None,
    };

    private void MakeTexts(double unit, double dpi)
    {
        var text = Themed("TextBrush", Colors.White);
        var muted = Themed("MutedTextBrush", Color.FromRgb(0xA0, 0xA0, 0xA0));
        (_themeKey, _textDpi, _textUnit, _stale) = (text, dpi, unit, false);
        (_names, _actions) = (new FormattedText?[Caps.Length], new FormattedText?[Caps.Length]);
        (_bound, _conflict) = (new bool[Caps.Length], new bool[Caps.Length]);
        for (var i = 0; i < Caps.Length; i++)
        {
            var cap = Caps[i];
            var width = cap.Width * unit - 8;
            _names[i] = Text(KeybindManager.KeyName(cap.Key), Math.Clamp(unit * 0.24, 8, 11), muted, dpi, width);
            if (IsModifier(cap.Key))
                continue;

            var on = _draft.ActionsOn(ChordOf(cap.Key));
            _bound[i] = on.Count > 0;
            _conflict[i] = on.Count > 1;
            if (on.Count > 0)
            {
                var label = Text(on.Count > 1 ? $"{on[0].Label} + {on[1].Label}" : on[0].Label, Math.Clamp(unit * 0.2, 7.5, 10), text, dpi, width);
                label.MaxLineCount = 2;
                _actions[i] = label;
            }
        }
    }

    private static FormattedText Text(string text, double size, Brush brush, double dpi, double maxWidth = 0)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, dpi) { Trimming = TextTrimming.CharacterEllipsis };
        if (maxWidth > 0)
            formatted.MaxTextWidth = Math.Max(maxWidth, 4);
        return formatted;
    }

    private static readonly Typeface Face = new("Segoe UI");

    private Brush Themed(string key, Color fallback) => TryFindResource(key) as Brush ?? Frozen(fallback);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush ConflictFill = Frozen(Color.FromArgb(0x70, 0xD0, 0x40, 0x40));
    private Brush? _boundFill, _accentSource;
    private Pen? _edgePen, _hoverPen;
    private Brush? _edgeSource;

    // The accent, see-through, for the keys that do something (made again when the theme's accent changes).
    private Brush BoundFill(Brush accent)
    {
        if (_boundFill is null || !ReferenceEquals(accent, _accentSource))
        {
            var color = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(0x00, 0x78, 0xD7);
            _boundFill = Frozen(Color.FromArgb(0x60, color.R, color.G, color.B));
            _hoverPen = new Pen(accent, 2);
            _hoverPen.Freeze();
            _accentSource = accent;
        }

        return _boundFill;
    }

    private Pen EdgePen(Brush edge)
    {
        if (_edgePen is null || !ReferenceEquals(edge, _edgeSource))
        {
            _edgePen = new Pen(edge, 1);
            _edgePen.Freeze();
            _edgeSource = edge;
        }

        return _edgePen;
    }

    // ----- Drawing -----

    protected override void OnRender(DrawingContext drawing)
    {
        var width = ActualWidth;
        if (width <= 0)
            return;

        var unit = Unit(width);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = Themed("TextBrush", Colors.White);
        if (_stale || !ReferenceEquals(text, _themeKey) || dpi != _textDpi || unit != _textUnit)
            MakeTexts(unit, dpi);

        var face = Themed("CardBrush", Color.FromRgb(0x2D, 0x2D, 0x2D));
        var well = Themed("InsetBrush", Color.FromRgb(0x18, 0x18, 0x18));
        var accent = Themed("AccentBrush", Color.FromRgb(0x00, 0x78, 0xD7));
        var muted = Themed("MutedTextBrush", Color.FromRgb(0xA0, 0xA0, 0xA0));
        var edge = EdgePen(Themed("ControlBorderBrush", Color.FromRgb(0x3F, 0x3F, 0x3F)));
        var bound = BoundFill(accent);

        // The whole map takes the pointer (between the keys too), so its tooltip and hover follow it smoothly.
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        // A glance has a panel of its own under it (it is drawn over the picture), and its pill to the keybind page.
        if (Glance)
        {
            drawing.DrawRoundedRectangle(Themed("PanelBrush", Color.FromRgb(0x20, 0x20, 0x20)), edge, new Rect(RenderSize), 8, 8);
            var pill = EditPillRect(dpi);
            drawing.DrawRoundedRectangle(_hoverEdit ? accent : bound, _hoverEdit ? _hoverPen : edge, pill, pill.Height / 2, pill.Height / 2);
            var label = Text(EditPillText, 11.5, text, dpi);
            drawing.DrawText(label, new Point(pill.X + (pill.Width - label.Width) / 2, pill.Y + (pill.Height - label.Height) / 2));
        }

        // The layers: the one shown filled with the accent.
        for (var i = 0; i < Layers.Length; i++)
        {
            var rect = LayerRect(i, dpi);
            drawing.DrawRoundedRectangle(i == _layer ? accent : i == _hoverLayer ? bound : face, edge, rect, rect.Height / 2, rect.Height / 2);
            var label = Text(Layers[i].Name, 11.5, text, dpi);
            drawing.DrawText(label, new Point(rect.X + (rect.Width - label.Width) / 2, rect.Y + (rect.Height - label.Height) / 2));
        }

        var layer = Layers[_layer];
        for (var i = 0; i < Caps.Length; i++)
        {
            var cap = Caps[i];
            var rect = CapRect(cap, unit);
            var held = IsModifier(cap.Key) && (layer.Mods & ModifierOf(cap.Key)) != 0;
            var fill = held ? accent : _conflict[i] ? ConflictFill : _bound[i] ? bound : IsModifier(cap.Key) ? well : face;
            drawing.DrawRoundedRectangle(fill, i == _hoverCap || i == _pickedCap ? _hoverPen : edge, rect, 4, 4);
            if (_names[i] is { } name)
                drawing.DrawText(name, new Point(rect.X + 4, rect.Y + 2));
            if (_actions[i] is { } action)
                drawing.DrawText(action, new Point(rect.X + 4, rect.Bottom - action.Height - 3));
        }

        DrawMouse(drawing, unit, dpi, face, well, bound, edge, text, muted);

        // What the pointer is on, or what was done last.
        var status = Text(_status, 11.5, muted, dpi, width - 8);
        status.MaxLineCount = 2;
        drawing.DrawText(status, new Point(4, KeyboardTop + 6.5 * unit + 6));
    }

    private void DrawMouse(DrawingContext drawing, double unit, double dpi, Brush face, Brush well, Brush bound, Pen edge, Brush text, Brush muted)
    {
        var body = MouseBody(unit);
        drawing.DrawRoundedRectangle(well, edge, body, body.Width / 2, body.Width / 2.2);
        foreach (var part in (ReadOnlySpan<MousePart>)[MousePart.Left, MousePart.Right, MousePart.Middle, MousePart.Forward, MousePart.Back])
        {
            var rect = MouseRect(part, unit);
            var actions = MouseActions(part);
            var fill = part is MousePart.Left or MousePart.Right ? face : actions.Count > 1 ? ConflictFill : actions.Count > 0 ? bound : face;
            var radius = part is MousePart.Left or MousePart.Right ? unit * 0.9 : unit * 0.25;
            drawing.DrawRoundedRectangle(fill, part == _hoverMouse ? _hoverPen : edge, rect, radius, radius);
        }

        // What the wheel and the side buttons do, under the mouse.
        var y = body.Bottom + 0.2 * unit;
        foreach (var part in (ReadOnlySpan<MousePart>)[MousePart.Middle, MousePart.Back, MousePart.Forward])
        {
            var actions = MouseActions(part);
            var line = Text($"{KeybindManager.ButtonName(ButtonOf(part)!.Value)}: {(actions.Count > 0 ? actions[0].Label : "-")}", Math.Clamp(unit * 0.22, 8, 10.5),
                actions.Count > 0 ? text : muted, dpi, MouseUnits * unit);
            if (y + line.Height > KeyboardTop + 6.5 * unit)
                break;
            drawing.DrawText(line, new Point(body.X - 0.4 * unit, y));
            y += line.Height;
        }
    }

    private List<KeyAction> MouseActions(MousePart part) =>
        ButtonOf(part) is { } button ? _draft.ActionsOn(new InputChord(Layers[_layer].Mods, Key.None, button)) : [];

    private const string EditPillText = "Click to Edit Keybinds...";

    // The glance's pill, at the right end of the layers' row.
    private Rect EditPillRect(double dpi)
    {
        var width = Text(EditPillText, 11.5, Brushes.White, dpi).Width + 24;
        return new Rect(Math.Max(ActualWidth - width - 4, 0), 2, width, LayerRowHeight - 6);
    }

    // What a glance says of a chord: each action's name, its keys, its category and how it acts.
    private string DescribeFully(InputChord chord)
    {
        var on = _draft.ActionsOn(chord);
        var name = KeybindManager.Format(chord);
        if (on.Count == 0)
            return $"{name}: nothing. Click to give it an action.";

        static string How(KeyAction action) =>
            action.Has(KeyActionFlags.Continuous) ? " Acts while held, on what is under the pointer (Shift goes faster)."
            : action.Has(KeyActionFlags.Hold) ? " Shown while held."
            : action.Has(KeyActionFlags.Repeat) ? " Repeats while held."
            : "";

        return string.Join("   ", on.Select(action =>
        {
            var keys = string.Join(", ", _draft.ChordsOf(action.Id).Select(KeybindManager.Format));
            return $"{name}: {action.Label} ({action.Category}; keys {keys}).{How(action)}{(action.Has(KeyActionFlags.Fixed) ? " Always on these keys." : "")}";
        })) + " Click to change it.";
    }

    /// <summary>The keybind page opened at a key (from a glance): its layer shown, the key marked and its menu of actions opened at it.</summary>
    public void Pick(InputChord chord)
    {
        var layer = Array.FindIndex(Layers, l => l.Mods == chord.Mods);
        SetLayer(layer >= 0 ? layer : 0);
        _pickedCap = chord.Button is null ? Array.FindIndex(Caps, c => c.Key == chord.Key) : -1;
        _status = $"{KeybindManager.Format(chord)}: choose what it does.";
        InvalidateVisual();
        if (IsLoaded && ActualWidth > 0)
        {
            OfferActions(chord, PickedRect());
            return;
        }

        Loaded += OpenWhenLoaded;

        void OpenWhenLoaded(object? sender, RoutedEventArgs e)
        {
            Loaded -= OpenWhenLoaded;
            Dispatcher.BeginInvoke(() => OfferActions(chord, PickedRect()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        Rect? PickedRect()
        {
            var unit = Unit(ActualWidth);
            if (_pickedCap >= 0)
                return CapRect(Caps[_pickedCap], unit);

            foreach (var part in Enum.GetValues<MousePart>())
                if (part != MousePart.None && ButtonOf(part) == chord.Button)
                    return MouseRect(part, unit);
            return null;
        }
    }

    // ----- Pointer -----

    private (int Cap, int Layer, MousePart Mouse) HitTest(Point point)
    {
        var width = ActualWidth;
        var unit = Unit(width);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (point.Y < LayerRowHeight)
        {
            for (var i = 0; i < Layers.Length; i++)
                if (LayerRect(i, dpi).Contains(point))
                    return (-1, i, MousePart.None);
            return (-1, -1, MousePart.None);
        }

        for (var i = 0; i < Caps.Length; i++)
            if (CapRect(Caps[i], unit).Contains(point))
                return (i, -1, MousePart.None);

        foreach (var part in (ReadOnlySpan<MousePart>)[MousePart.Middle, MousePart.Forward, MousePart.Back, MousePart.Left, MousePart.Right])
            if (MouseRect(part, unit).Contains(point))
                return (-1, -1, part);

        return (-1, -1, MousePart.None);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var onEdit = Glance && EditPillRect(VisualTreeHelper.GetDpi(this).PixelsPerDip).Contains(point);
        var (cap, layer, mouse) = onEdit ? (-1, -1, MousePart.None) : HitTest(point);
        if (cap == _hoverCap && layer == _hoverLayer && mouse == _hoverMouse && onEdit == _hoverEdit)
            return;

        (_hoverCap, _hoverLayer, _hoverMouse, _hoverEdit) = (cap, layer, mouse, onEdit);
        InputChord? chord = cap >= 0 && !IsModifier(Caps[cap].Key) ? ChordOf(Caps[cap].Key)
            : mouse != MousePart.None ? new InputChord(Layers[_layer].Mods, Key.None, ButtonOf(mouse))
            : null;
        if (Glance)
        {
            // A glance names what is under the pointer in its banner (a tooltip would come too late for a held key).
            _status = onEdit ? "Opens Settings at Keybinds & Lightning Dial."
                : chord is { } over ? DescribeFully(over)
                : cap >= 0 ? "Click Ctrl, Shift or Alt (or a layer above) to see what the keys do with it held."
                : "Point at a key or a mouse button to see what it does; click it to change it.";
        }
        else
        {
            ToolTip = cap >= 0 ? Describe(ChordOf(Caps[cap].Key)) : chord is { } over ? Describe(over) : null;
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        (_hoverCap, _hoverLayer, _hoverMouse, _hoverEdit) = (-1, -1, MousePart.None, false);
        InvalidateVisual();
    }

    private string Describe(InputChord chord)
    {
        var on = _draft.ActionsOn(chord);
        var name = KeybindManager.Format(chord);
        var held = on.Count > 0 && on[0].Has(KeyActionFlags.Continuous) ? " (while it is held, on what is under the pointer; Shift goes faster)" : "";
        return on.Count == 0 ? $"{name}: nothing" : $"{name}: {string.Join(" and ", on.Select(a => a.Label))}{held}";
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Glance && EditPillRect(VisualTreeHelper.GetDpi(this).PixelsPerDip).Contains(e.GetPosition(this)))
        {
            e.Handled = true;
            EditRequested?.Invoke(null);
            return;
        }

        var (cap, layer, mouse) = HitTest(e.GetPosition(this));
        if (layer >= 0)
        {
            SetLayer(layer);
            e.Handled = true;
            return;
        }

        if (cap >= 0 && IsModifier(Caps[cap].Key))
        {
            ToggleModifier(ModifierOf(Caps[cap].Key));
            e.Handled = true;
            return;
        }

        InputChord? chord = cap >= 0 ? ChordOf(Caps[cap].Key) : mouse != MousePart.None ? new InputChord(Layers[_layer].Mods, Key.None, ButtonOf(mouse)) : null;
        if (chord is not { } pressed)
            return;

        e.Handled = true;
        if (Glance)
            EditRequested?.Invoke(pressed);
        else
            OfferActions(pressed);
    }

    private void SetLayer(int layer)
    {
        _layer = layer;
        _stale = true;
        _status = Layers[layer].Mods == ModifierKeys.None
            ? "What each key does pressed alone. The navigation keys act while held, on the timeline, the Canvas or the player under the pointer (Shift goes faster)."
            : $"What each key does with {Layers[layer].Name} held.";
        InvalidateVisual();
    }

    // Clicking Ctrl, Shift or Alt on the drawing goes to the layer with that modifier added (or taken away), if there is one.
    private void ToggleModifier(ModifierKeys modifier)
    {
        if (modifier == ModifierKeys.None)
            return;

        var mods = Layers[_layer].Mods ^ modifier;
        var index = Array.FindIndex(Layers, l => l.Mods == mods);
        SetLayer(index >= 0 ? index : 0);
    }

    // The menu of every action this layer's chord can run: the one it runs is checked; choosing one gives the chord to it.
    private void OfferActions(InputChord chord, Rect? at = null)
    {
        var menu = at is { } rect
            ? new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom, PlacementRectangle = rect }
            : new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        var title = KeybindManager.Format(chord);
        if (KeybindManager.Validate(chord, "edit.split") is { } refused && !chord.IsEmpty)
        {
            _status = $"{title}: {refused}";
            InvalidateVisual();
            if (_draft.ActionsOn(chord).Count == 0)
                return;
        }

        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        var on = _draft.ActionsOn(chord);
        var clear = new MenuItem { Header = "Nothing", IsCheckable = true, IsChecked = on.Count == 0 };
        clear.Click += (_, _) =>
        {
            foreach (var action in on)
                _draft.Unbind(action.Id, chord);
            _status = on.Any(a => KeybindDraft.IsFixed(a.Id, chord)) ? $"{title} stays {on[0].Label}: undo and redo keep their keys." : $"{title} does nothing now.";
            InvalidateVisual();
        };
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());

        // Every action, by category (one the chord cannot run says why when it is chosen).
        foreach (var category in KeybindManager.Categories)
        {
            var into = new MenuItem { Header = category };
            foreach (var action in KeybindManager.Actions.Where(a => a.Category == category))
            {
                var item = new MenuItem { Header = action.Label, IsCheckable = true, IsChecked = on.Contains(action) };
                var id = action.Id;
                item.Click += (_, _) => Assign(id, chord, title);
                into.Items.Add(item);
            }

            menu.Items.Add(into);
        }

        menu.IsOpen = true;
    }

    private void Assign(string id, InputChord chord, string title)
    {
        var action = KeybindManager.Action(id)!;
        _status = _draft.Bind(id, chord, out var took) is { } problem ? $"{title}: {problem}"
            : took is not null ? $"{title} is {action.Label} now (it was {took.Label})."
            : $"{title} is {action.Label} now.";
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MapPeer(this);

    private sealed class MapPeer(KeybindMapControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(KeybindMapControl);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetNameCore() => "Keybind map";
    }
}

/// <summary>
/// The keybind page's list (4.4): every action under its category with its keys as buttons. Click a key's button to change
/// it, or + to add one, then press the keys (or a mouse's middle or side button here); Esc cancels; right-click a key's
/// button to take it off. A key another action has moves to the new one.
/// </summary>
public sealed class KeybindListControl : StackPanel
{
    private readonly KeybindDraft _draft;
    private readonly TextBlock _status;
    private readonly Dictionary<string, WrapPanel> _rows = [];
    private (string Id, InputChord? Replacing, Button Button)? _listening;

    private static readonly Brush ConflictText = MakeConflictText();

    private static Brush MakeConflictText()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
        brush.Freeze();
        return brush;
    }

    public KeybindListControl(KeybindDraft draft)
    {
        _draft = draft;
        AutomationProperties.SetName(this, "Keybinds");

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6), Text = "Click a key to change it, + to add one." };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Children.Add(_status);

        foreach (var category in KeybindManager.Categories)
        {
            var heading = new TextBlock { Text = category, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Children.Add(heading);
            foreach (var action in KeybindManager.Actions.Where(a => a.Category == category))
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = true };
                var label = new TextBlock { Text = action.Label, Width = 190, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                DockPanel.SetDock(label, Dock.Left);
                var keys = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(label);
                row.Children.Add(keys);
                Children.Add(row);
                _rows[action.Id] = keys;
            }
        }

        var reset = new Button { Content = "Reset Keys to Preset", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        reset.Click += (_, _) =>
        {
            _draft.ResetToPreset();
            _status.Text = $"Every key is as {_draft.Preset} has it.";
        };
        Children.Add(reset);

        draft.Changed += Refresh;
        PreviewKeyDown += Listening_PreviewKeyDown;
        PreviewMouseDown += Listening_PreviewMouseDown;
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false && _listening is not null)
                StopListening();
        };
        Refresh();
    }

    private void Refresh()
    {
        _listening = null;
        foreach (var (id, keys) in _rows)
        {
            keys.Children.Clear();
            foreach (var chord in _draft.ChordsOf(id))
                keys.Children.Add(ChordButton(id, chord));

            var add = new Button { Content = "+", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 1, 4, 1), ToolTip = "Add a key" };
            AutomationProperties.SetName(add, "Add a key to " + KeybindManager.Action(id)?.Label);
            add.Click += (_, _) => StartListening(id, null, add);
            keys.Children.Add(add);
        }
    }

    private Button ChordButton(string id, InputChord chord)
    {
        var fixedKey = KeybindDraft.IsFixed(id, chord);
        var button = new Button
        {
            Content = KeybindManager.Format(chord),
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(0, 1, 4, 1),
            IsEnabled = !fixedKey,
            ToolTip = fixedKey ? "Undo and redo keep their keys." : "Click to change this key; right-click to take it off.",
        };
        ToolTipService.SetShowOnDisabled(button, true);
        if (_draft.IsConflicted(id, chord))
        {
            button.Foreground = ConflictText;
            button.ToolTip = "Another action has this key too: give one of them another.";
        }

        button.Click += (_, _) => StartListening(id, chord, button);
        button.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            _draft.Unbind(id, chord);
            _status.Text = $"{KeybindManager.Format(chord)} is off {KeybindManager.Action(id)?.Label}.";
        };
        return button;
    }

    private void StartListening(string id, InputChord? replacing, Button button)
    {
        if (_listening is { } previous && !ReferenceEquals(previous.Button, button))
            previous.Button.Content = previous.Replacing is { } chord ? KeybindManager.Format(chord) : "+";

        _listening = (id, replacing, button);
        button.Content = "Press a key...";
        _status.Text = $"Press the keys for {KeybindManager.Action(id)?.Label} (or a mouse's middle or side button over this list). Esc cancels.";

        // The button keeps the keyboard (the list hears its keys on their way to it): the page does not scroll.
        button.Focus();
    }

    private void StopListening()
    {
        if (_listening is not { } listening)
            return;

        _listening = null;
        listening.Button.Content = listening.Replacing is { } chord ? KeybindManager.Format(chord) : "+";
    }

    private void Listening_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_listening is null)
            return;

        // Every key is the binding's while it listens: Esc cancels it (and never closes Settings).
        e.Handled = true;
        var key = KeybindManager.KeyOf(e);
        if (key == Key.Escape)
        {
            StopListening();
            _status.Text = "Nothing was changed.";
            return;
        }

        // A modifier alone: the key it is held with is still to come.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        Apply(KeybindManager.ChordOf(e));
    }

    private void Listening_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_listening is null || e.ChangedButton is MouseButton.Left or MouseButton.Right)
            return;

        e.Handled = true;
        Apply(KeybindManager.ChordOf(e.ChangedButton));
    }

    private void Apply(InputChord chord)
    {
        if (_listening is not { } listening)
            return;

        var action = KeybindManager.Action(listening.Id)!;
        _listening = null;
        if (listening.Replacing == chord)
        {
            listening.Button.Content = KeybindManager.Format(chord);
            _status.Text = "Nothing was changed.";
            return;
        }

        if (_draft.Bind(listening.Id, chord, out var took) is { } problem)
        {
            listening.Button.Content = listening.Replacing is { } old ? KeybindManager.Format(old) : "+";
            _status.Text = problem;
            return;
        }

        if (listening.Replacing is { } replaced)
            _draft.Unbind(listening.Id, replaced);

        _status.Text = took is not null
            ? $"{KeybindManager.Format(chord)} is {action.Label} now (it was {took.Label})."
            : $"{KeybindManager.Format(chord)} is {action.Label} now.";
    }
}
