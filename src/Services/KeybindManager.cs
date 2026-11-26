using System.Globalization;
using System.Windows.Input;

namespace HandPegApp.Services;

/// <summary>
/// Shortcut chord: key or mouse button plus modifiers. Value equality, usable as an allocation-free dictionary key.
/// </summary>
public readonly record struct InputChord(ModifierKeys Mods, Key Key, MouseButton? Button = null)
{
    public bool IsMouse => Button is not null;

    public bool IsEmpty => Key == Key.None && Button is null;

    /// <summary>Chord without Shift (Shift accelerates held navigation keys and is ignored for lookup).</summary>
    public InputChord WithoutShift => this with { Mods = Mods & ~ModifierKeys.Shift };

    public override string ToString() => KeybindManager.Format(this);
}

/// <summary>What sets an action apart from the others when its key is pressed.</summary>
[Flags]
public enum KeyActionFlags
{
    None = 0,

    /// <summary>Runs even while a text box has the keyboard (saving).</summary>
    WorksInText = 1,

    /// <summary>Fixed chords that cannot be reassigned (undo, redo).</summary>
    Fixed = 2,

    /// <summary>Runs again while its key repeats (stepping, zooming); any other action runs once per press.</summary>
    Repeat = 4,

    /// <summary>
    /// Runs every frame while held (WASD zoom/pan): press starts, auto-repeats are swallowed, release stops. Unmodified (Shift accelerates).
    /// </summary>
    Continuous = 8,

    /// <summary>Active while held (X-Ray); a quick Tap of Tab still moves focus.</summary>
    Hold = 16,

    /// <summary>
    /// Mutates the project or opens a dialog/dial: swallowed during a timeline drag so the drag never commits onto a changed project.
    /// </summary>
    ChangesProject = 32,
}

/// <summary>
/// Bindable action for shortcuts and the Lightning Dial. <paramref name="Also"/> lists extra non-fixed default chords (e.g. Mouse 4 for
/// Undo) that presets include and users may remove or reassign.
/// </summary>
public sealed record KeyAction(string Id, string Label, string Category, string Glyph, string[] Defaults, KeyActionFlags Flags = KeyActionFlags.None,
    string[]? Also = null)
{
    public bool Has(KeyActionFlags flag) => (Flags & flag) != 0;
}

/// <summary>
/// Global shortcut map: actions, presets, user overrides, and chord-to-action lookup (<see cref="Resolve"/>; menus use
/// <see cref="GestureText"/>). No visual tree or disk access; the map is built lazily from in-memory settings on first lookup, never
/// before Frame 1. Single key layer: navigation keys act while held (<see cref="KeyActionFlags.Continuous"/>).
/// </summary>
public static class KeybindManager
{
    // ----- Actions -----

    public const string Transport = "Transport", Editing = "Editing", Markers = "Markers & Points", View = "View",
        Navigation = "Navigation", Keyframes = "Keyframes", Project = "Project", Tools = "Tools";

    /// <summary>The categories in the order the keybind list shows them.</summary>
    public static IReadOnlyList<string> Categories { get; } = [Project, Transport, Editing, Markers, View, Navigation, Keyframes, Tools];

    /// <summary>
    /// All actions in keybind-list order (earlier wins hand-edited conflicts). Left-hand cluster: 1-7 dial categories, Tab X-Ray, Q/E frame
    /// step (Shift+Q/E and [ ] Top &amp; Tail), W/S zoom, A/D pan, R ripple delete, T keyframe, Caps snap, F split, G link, Z/X edit points, C marker, V in point, B dead air;
    /// Space play, Mouse 5 dial, Mouse 4 undo. The cluster key is listed first (shown in menus/tooltips), legacy keys after.
    /// </summary>
    public static IReadOnlyList<KeyAction> Actions { get; } =
    [
        new("file.save", "Save Project", Project, "\uE74E", ["Ctrl+S"], KeyActionFlags.WorksInText | KeyActionFlags.ChangesProject),
        new("file.saveAs", "Save Project As", Project, "\uE792", ["Ctrl+Alt+S"], KeyActionFlags.WorksInText | KeyActionFlags.ChangesProject),
        new("file.saveBundle", "Save Project Bundle (.hpb)", Project, "\uE7B8", [], KeyActionFlags.WorksInText),
        new("edit.undo", "Undo", Project, "\uE7A7", ["Ctrl+Z"], KeyActionFlags.Fixed | KeyActionFlags.Repeat | KeyActionFlags.ChangesProject, Also: ["Mouse4"]),
        new("edit.redo", "Redo", Project, "\uE7A6", ["Ctrl+Y", "Ctrl+Shift+Z"], KeyActionFlags.Fixed | KeyActionFlags.Repeat | KeyActionFlags.ChangesProject),
        new("file.addMedia", "Add Media", Project, "\uE710", [], KeyActionFlags.ChangesProject),
        new("ui.settings", "Settings", Project, "\uE713", [], KeyActionFlags.ChangesProject),
        // Project tabs (MainWindow.ProjectTabs.cs): new, close active, next, previous. Pre-empt focused controls, like Save.
        new("tab.new", "New Project Tab", Project, "\uE710", ["Ctrl+T"], KeyActionFlags.WorksInText),
        new("tab.close", "Close Project Tab", Project, "\uE8BB", ["Ctrl+W"], KeyActionFlags.WorksInText),
        new("tab.next", "Next Project Tab", Project, "\uE76C", ["Ctrl+Tab"], KeyActionFlags.WorksInText),
        new("tab.prev", "Previous Project Tab", Project, "\uE76B", ["Ctrl+Shift+Tab"], KeyActionFlags.WorksInText),
        // Auto-Captions dialog (on the default dial). Unbound.
        new("captions.generate", "Generate Auto-Captions...", Tools, "\uE7F0", [], KeyActionFlags.ChangesProject),

        new("transport.playPause", "Play / Pause", Transport, "\uE768", ["Space"]),
        new("transport.shuttleBack", "Shuttle Back", Transport, "\uEB9E", ["J"]),
        new("transport.stop", "Stop", Transport, "\uE71A", ["K"]),
        new("transport.shuttleForward", "Shuttle Forward", Transport, "\uEB9D", ["L"]),
        new("transport.stepBack", "Previous Frame", Transport, "\uE892", ["Q", "Left"], KeyActionFlags.Repeat),
        new("transport.stepForward", "Next Frame", Transport, "\uE893", ["E", "Right"], KeyActionFlags.Repeat),
        new("transport.start", "Go to Start", Transport, "\uE892", ["Home"]),
        new("transport.end", "Go to End", Transport, "\uE893", ["End"]),
        new("transport.prevIFrame", "Previous I-Frame", Transport, "\uE76B", [], KeyActionFlags.Repeat),
        new("transport.nextIFrame", "Next I-Frame", Transport, "\uE76C", [], KeyActionFlags.Repeat),

        new("edit.split", "Split at Playhead", Editing, "\uE8C6", ["F"], KeyActionFlags.ChangesProject),
        new("edit.delete", "Delete Clip", Editing, "\uE74D", ["Delete"], KeyActionFlags.ChangesProject),
        new("edit.rippleDelete", "Ripple Delete", Editing, "\uE74D", ["R", "Shift+Delete"], KeyActionFlags.ChangesProject),
        new("edit.trimStart", "Trim Start to Playhead", Editing, "\uE76B", [], KeyActionFlags.ChangesProject),
        new("edit.trimEnd", "Trim End to Playhead", Editing, "\uE76C", [], KeyActionFlags.ChangesProject),
        // Top & Tail (Pass 5.7.1): ripple-trim the clip's start / end to the playhead, closing the gap. Shift+Q / Shift+E and [ / ]
        // leave Q/E frame steps and the held WASD keys alone (Shift accelerates only held keys).
        new(TrimStartToPlayhead, "Ripple Trim Start (Top)", Editing, "\uE76B", ["Shift+Q", "["], KeyActionFlags.ChangesProject),
        new(TrimEndToPlayhead, "Ripple Trim End (Tail)", Editing, "\uE76C", ["Shift+E", "]"], KeyActionFlags.ChangesProject),
        new("clip.toggleLink", "Link / Unlink A/V", Editing, "\uE71B", ["G", "U"], KeyActionFlags.ChangesProject),
        new("edit.deadAir", "Remove Dead Air...", Editing, "\uE74F", ["B"], KeyActionFlags.ChangesProject),
        new("edit.bypassEffects", "Bypass Sequence Effects", Editing, "\uE711", [], KeyActionFlags.ChangesProject),
        // Clip clipboard: copy selection with Cutouts, paste at playhead, duplicate after, Paste Attributes onto selection.
        new("edit.copy", "Copy Clips", Editing, "\uE8C8", ["Ctrl+C"]),
        new("edit.cut", "Cut Clips", Editing, "\uE8C6", ["Ctrl+X"], KeyActionFlags.ChangesProject),
        new("edit.paste", "Paste Clips at Playhead", Editing, "\uE77F", ["Ctrl+V"], KeyActionFlags.ChangesProject),
        new("edit.duplicate", "Duplicate Clips", Editing, "\uF413", ["Ctrl+D"], KeyActionFlags.ChangesProject),
        new("edit.pasteAttributes", "Paste Attributes...", Editing, "\uE77F", ["Ctrl+Alt+V"], KeyActionFlags.ChangesProject),
        // Move clip (with family) to the next free track above/below, creating one if needed.
        new("clip.newTrackAbove", "Move Clip to Track Above", Editing, "\uE74A", ["Alt+Up"], KeyActionFlags.ChangesProject),
        new("clip.newTrackBelow", "Move Clip to Track Below", Editing, "\uE74B", ["Alt+Down"], KeyActionFlags.ChangesProject),

        new("marker.add", "Add Marker", Markers, "\uE7C1", ["C", "M"], KeyActionFlags.ChangesProject),
        // Ping-to-Mark: highlight at the playhead; Alt+click on the player also marks a point.
        new("marker.ping", "Ping-to-Mark Highlight", Markers, "\uE735", ["Ctrl+J"], KeyActionFlags.ChangesProject),
        new("marker.next", "Next Marker", Markers, "\uE893", ["Shift+M"], KeyActionFlags.Repeat),
        new("marker.prev", "Previous Marker", Markers, "\uE892", ["Ctrl+Shift+M"], KeyActionFlags.Repeat),
        new("transport.prevEdit", "Previous Edit Point", Markers, "\uE892", ["Z", "Up"], KeyActionFlags.Repeat),
        new("transport.nextEdit", "Next Edit Point", Markers, "\uE893", ["X", "Down"], KeyActionFlags.Repeat),

        new("view.zoomIn", "Zoom Timeline In", View, "\uE8A3", ["="], KeyActionFlags.Repeat),
        new("view.zoomOut", "Zoom Timeline Out", View, "\uE71F", ["-"], KeyActionFlags.Repeat),
        // Shift+\ (plain \ is the held grade Before/After).
        new("view.fit", "Fit Timeline", View, "\uE9A6", ["Shift+\\"]),
        // Color Studio Before/After: shows the ungraded picture while \ is held.
        new("view.compareGrade", "Before/After (Hold)", View, "\uE7B3", ["\\"], KeyActionFlags.Hold),
        new("view.toggleSnap", "Toggle Snap", View, "\uE80A", ["Caps"]),
        // Toggle 3D Exploded View. Unbound by default (left-hand cluster is full).
        new("view.exploded3d", "3D Exploded View", View, "\uE81E", []),
        // Toggle fullscreen player (MainWindow.Fullscreen.cs): F11; Esc also exits.
        new("view.fullscreen", "Fullscreen Preview", View, "\uE740", ["F11"]),

        new("nav.forward", "Zoom In Under the Pointer", Navigation, "\uE8A3", ["W"], KeyActionFlags.Continuous),
        new("nav.back", "Zoom Out Under the Pointer", Navigation, "\uE71F", ["S"], KeyActionFlags.Continuous),
        new("nav.left", "Pan Left", Navigation, "\uE76B", ["A"], KeyActionFlags.Continuous),
        new("nav.right", "Pan Right", Navigation, "\uE76C", ["D"], KeyActionFlags.Continuous),

        new("key.toggle", "Add / Remove Keyframe", Keyframes, "\uE823", ["T"], KeyActionFlags.ChangesProject),
        new("key.prev", "Previous Keyframe", Keyframes, "\uE892", [], KeyActionFlags.Repeat),
        new("key.next", "Next Keyframe", Keyframes, "\uE893", [], KeyActionFlags.Repeat),

        new("ui.dial", "Lightning Dial", Tools, "\uE945", ["Mouse5"], KeyActionFlags.ChangesProject),
        // Dial slice keys: run the slice's action or open its category.
        new("ui.dial1", "Lightning Dial: Slice 1", Tools, "\uE945", ["1"], KeyActionFlags.ChangesProject),
        new("ui.dial2", "Lightning Dial: Slice 2", Tools, "\uE945", ["2"], KeyActionFlags.ChangesProject),
        new("ui.dial3", "Lightning Dial: Slice 3", Tools, "\uE945", ["3"], KeyActionFlags.ChangesProject),
        new("ui.dial4", "Lightning Dial: Slice 4", Tools, "\uE945", ["4"], KeyActionFlags.ChangesProject),
        new("ui.dial5", "Lightning Dial: Slice 5", Tools, "\uE945", ["5"], KeyActionFlags.ChangesProject),
        new("ui.dial6", "Lightning Dial: Slice 6", Tools, "\uE945", ["6"], KeyActionFlags.ChangesProject),
        new("ui.dial7", "Lightning Dial: Slice 7", Tools, "\uE945", ["7"], KeyActionFlags.ChangesProject),
        new("ui.dial8", "Lightning Dial: Slice 8", Tools, "\uE945", ["8"], KeyActionFlags.ChangesProject),
        new("ui.xray", "X-Ray (Hold)", Tools, "\uE9D9", ["Tab"], KeyActionFlags.Hold),
        new("help.tour", "Quick-Start Tour", Tools, "\uE897", [], KeyActionFlags.ChangesProject),
    ];

    /// <summary>Top &amp; Tail action ids (Pass 5.7.1): ripple trim of the selected clip's start / end to the playhead.</summary>
    public const string TrimStartToPlayhead = "clip.trimStartToPlayhead", TrimEndToPlayhead = "clip.trimEndToPlayhead";

    private static readonly Dictionary<string, KeyAction> ById = Actions.ToDictionary(a => a.Id);

    /// <summary>The action with this id, or null for one HandPeg does not have (any more).</summary>
    public static KeyAction? Action(string id) => ById.GetValueOrDefault(id);

    // ----- Presets -----

    private const string BroadcastPreset = "Classic Broadcast Track-Edit", RazorPreset = "Studio Razor & Ripple",
        ColorPreset = "Color & Node Studio";

    /// <summary>The key maps to start from, HandPeg's own first (the settings' preset box lists these).</summary>
    public static IReadOnlyList<string> PresetNames { get; } = [AppSettings.DefaultKeybindPreset, BroadcastPreset, RazorPreset, ColorPreset];

    // Presets are overrides on WASD (Default): listed actions get these chords instead (fixed actions add them). No preset binds a chord
    // twice (CheckPresets).
    private static readonly Dictionary<string, Dictionary<string, string[]>> PresetChanges = new()
    {
        [AppSettings.DefaultKeybindPreset] = [],
        [BroadcastPreset] = new()
        {
            // B is the blade here, as in the track editors this preset is named after: Remove Dead Air gives it up.
            ["edit.split"] = ["B"],
            ["edit.deadAir"] = [],
        },
        [RazorPreset] = new()
        {
            // Razor on C, Top & Tail ripple trims on Q/W (the plain trims stay in menus and the dial); marker on M, frame back on Left,
            // zoom on =/- (A/D still pan).
            ["edit.split"] = ["C"],
            ["marker.add"] = ["M"],
            [TrimStartToPlayhead] = ["Q"],
            ["transport.stepBack"] = ["Left"],
            [TrimEndToPlayhead] = ["W"],
            ["edit.trimStart"] = [],
            ["edit.trimEnd"] = [],
            ["nav.forward"] = [],
            ["nav.back"] = [],
            ["edit.rippleDelete"] = ["Shift+Delete", "Backspace"],
        },
        [ColorPreset] = new()
        {
            // Top & Tail on Q/W as in the Razor preset (frame back on Left, zoom on =/-); [ and ] step keyframes here.
            [TrimStartToPlayhead] = ["Q"],
            ["transport.stepBack"] = ["Left"],
            [TrimEndToPlayhead] = ["W"],
            ["nav.forward"] = [],
            ["nav.back"] = [],
            ["key.toggle"] = ["T", "Alt+K"],
            ["key.prev"] = ["["],
            ["key.next"] = ["]"],
            // Ctrl+D is Duplicate globally, so bypass uses Ctrl+Shift+D.
            ["edit.bypassEffects"] = ["Ctrl+Shift+D"],
        },
    };

    /// <summary>
    /// Preset by name; unknown or retired names ("HandPeg Default", "FPS Gamer") map to WASD (Default).
    /// </summary>
    public static string KnownPreset(string? name) =>
        name is not null && PresetChanges.ContainsKey(name) ? name : AppSettings.DefaultKeybindPreset;

    /// <summary>All action chords for a preset (fresh lists; the Keybinds page mutates them).</summary>
    public static Dictionary<string, List<InputChord>> PresetChords(string preset)
    {
        var chords = new Dictionary<string, List<InputChord>>(Actions.Count);
        foreach (var action in Actions)
            chords[action.Id] = ParseAll(action.Also is { } also ? action.Defaults.Concat(also) : action.Defaults);

        foreach (var (id, changed) in PresetChanges[KnownPreset(preset)])
        {
            var list = chords[id];
            if (!ById[id].Has(KeyActionFlags.Fixed))
                list.Clear();
            foreach (var chord in ParseAll(changed))
                if (!list.Contains(chord))
                    list.Add(chord);
        }

        return chords;
    }

    /// <summary>
    /// Preset validation errors (duplicate or unbindable chords); empty when valid. Test-only.
    /// </summary>
    public static List<string> CheckPresets()
    {
        var problems = new List<string>();
        foreach (var preset in PresetNames)
        {
            var owners = new Dictionary<InputChord, string>();
            foreach (var (id, list) in PresetChords(preset))
            {
                foreach (var chord in list)
                {
                    if (Validate(chord, id) is { } why)
                        problems.Add($"{preset}: {Format(chord)} on {id}: {why}");
                    if (!owners.TryAdd(chord, id))
                        problems.Add($"{preset}: {Format(chord)} is both {owners[chord]} and {id}");
                }
            }
        }

        return problems;
    }

    // ----- The map in effect -----

    private sealed class Map
    {
        public readonly Dictionary<InputChord, string> ByChord = [];
        public readonly Dictionary<string, InputChord[]> Chords = [];
        public readonly Dictionary<string, string> Gestures = [];
    }

    private static Map? _map;

    // Built lazily on first lookup (never during settings load). Must not be touched before Frame 1: the action table is built with
    // the class; KeyTip placeholders do not query it.
    private static Map Current => _map ??= Build(AppSettings.Current.KeybindPreset, AppSettings.Current.CustomKeybinds);

    private static List<DialCategoryState>? _dial;

    /// <summary>Raised when the map or dial is replaced (settings saved) so menus and tooltips refresh.</summary>
    public static event Action? Changed;

    /// <summary>Applies saved settings: preset, user overrides and dial.</summary>
    public static void Reload(AppSettings settings)
    {
        _map = Build(settings.KeybindPreset, settings.CustomKeybinds);
        _dial = Normalized(settings.LightningDial);
        Changed?.Invoke();
    }

    private static Map Build(string preset, Dictionary<string, List<string>>? custom)
    {
        var chords = Effective(preset, custom);
        var map = new Map();
        foreach (var action in Actions)
        {
            var list = chords[action.Id];
            map.Chords[action.Id] = [.. list];
            foreach (var chord in list)
                map.ByChord.TryAdd(chord, action.Id);

            if (list.Count > 0)
                map.Gestures[action.Id] = action.Has(KeyActionFlags.Hold) ? "Hold " + Format(list[0]) : Format(list[0]);
        }

        return map;
    }

    /// <summary>
    /// Preset plus user overrides (unbindable chords dropped). User-assigned keys are exclusive: conflicting defaults (preset or newer
    /// versions) yield, so an update never rebinds a user's key.
    /// </summary>
    internal static Dictionary<string, List<InputChord>> Effective(string preset, Dictionary<string, List<string>>? custom)
    {
        var chords = PresetChords(preset);
        if (custom is null)
            return chords;

        var claimed = new HashSet<InputChord>();
        foreach (var (id, texts) in custom)
        {
            if (!ById.TryGetValue(id, out var action) || texts is null)
                continue;

            var list = action.Has(KeyActionFlags.Fixed) ? ParseAll(action.Defaults) : [];
            foreach (var chord in ParseAll(texts))
                if (!list.Contains(chord) && Validate(chord, id) is null)
                    list.Add(chord);
            chords[id] = list;
            claimed.UnionWith(list);
        }

        foreach (var (id, list) in chords)
            if (!custom.ContainsKey(id))
                list.RemoveAll(chord => claimed.Contains(chord) && !KeybindDraft.IsFixed(id, chord));

        return chords;
    }

    /// <summary>The action a chord runs, or null.</summary>
    public static string? Resolve(in InputChord chord) => Current.ByChord.TryGetValue(chord, out var id) ? id : null;

    /// <summary>The chords an action has now (none when unbound).</summary>
    public static IReadOnlyList<InputChord> ChordsOf(string actionId) =>
        Current.Chords.TryGetValue(actionId, out var chords) ? chords : [];

    /// <summary>True while any key of a held (navigation) action is down.</summary>
    public static bool IsHeld(string actionId)
    {
        if (!Current.Chords.TryGetValue(actionId, out var chords))
            return false;

        foreach (var chord in chords)
            if (!chord.IsMouse && chord.Key != Key.None && Keyboard.IsKeyDown(chord.Key))
                return true;
        return false;
    }

    /// <summary>
    /// Display shortcut ("Ctrl+S", "Hold Tab") for InputGestureText or tooltips; "" when unbound. Not for static header text (changes on rebind).
    /// </summary>
    public static string GestureText(string actionId) => Current.Gestures.GetValueOrDefault(actionId, "");

    /// <summary>A label with the action's shortcut after it, for a tooltip: "Split at Playhead (F)".</summary>
    public static string WithGesture(string label, string actionId) =>
        GestureText(actionId) is { Length: > 0 } keys ? $"{label} ({keys})" : label;

    // ----- Fixed undo/redo chords -----

    /// <summary>Ctrl+Z, wherever it is pressed (dialogs with an undo of their own use this too).</summary>
    public static bool IsUndo(KeyEventArgs e) => e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control;

    /// <summary>Ctrl+Y or Ctrl+Shift+Z.</summary>
    public static bool IsRedo(KeyEventArgs e) =>
        (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));

    // ----- Chords from input -----

    /// <summary>Chord for a key event: effective key (resolving Alt system keys and IME keys) plus current modifiers.</summary>
    public static InputChord ChordOf(KeyEventArgs e) => new(Keyboard.Modifiers, Normalize(KeyOf(e)));

    /// <summary>The chord a mouse button press is, with the modifiers held now.</summary>
    public static InputChord ChordOf(MouseButton button) => new(Keyboard.Modifiers, Key.None, button);

    /// <summary>The key itself: with Alt held WPF reports a system key, and an IME or a dead key reports its own.</summary>
    public static Key KeyOf(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    // The number pad's plus and minus are the same keys as = and - (the timeline's zoom), and the second backslash key
    // of some keyboards the same as the first, as they always were.
    private static Key Normalize(Key key) => key switch
    {
        Key.Add => Key.OemPlus,
        Key.Subtract => Key.OemMinus,
        Key.OemBackslash => Key.Oem5,
        _ => key,
    };

    // ----- Text form ("Ctrl+Alt+S", "Shift+Mouse5", "Tab") -----

    private static readonly (Key Key, string Text)[] NamedKeys =
    [
        (Key.Space, "Space"), (Key.Left, "Left"), (Key.Right, "Right"), (Key.Up, "Up"), (Key.Down, "Down"),
        (Key.Home, "Home"), (Key.End, "End"), (Key.PageUp, "PageUp"), (Key.PageDown, "PageDown"), (Key.Insert, "Insert"),
        (Key.Delete, "Delete"), (Key.Back, "Backspace"), (Key.Enter, "Enter"), (Key.Tab, "Tab"), (Key.Escape, "Esc"),
        (Key.OemPlus, "="), (Key.OemMinus, "-"), (Key.Oem5, "\\"), (Key.Oem4, "["), (Key.Oem6, "]"), (Key.Oem1, ";"),
        (Key.Oem7, "'"), (Key.OemComma, ","), (Key.OemPeriod, "."), (Key.Oem2, "/"), (Key.Oem3, "`"),
        (Key.Multiply, "Num*"), (Key.Divide, "Num/"), (Key.Decimal, "Num."), (Key.Pause, "Pause"), (Key.Apps, "Menu"),
        (Key.CapsLock, "Caps"), (Key.NumLock, "NumLock"), (Key.Scroll, "ScrollLock"), (Key.PrintScreen, "PrtSc"),
        (Key.LeftCtrl, "Ctrl"), (Key.RightCtrl, "Ctrl"), (Key.LeftShift, "Shift"), (Key.RightShift, "Shift"),
        (Key.LeftAlt, "Alt"), (Key.RightAlt, "Alt"), (Key.LWin, "Win"), (Key.RWin, "Win"),
    ];

    private static readonly Dictionary<Key, string> KeyText = MakeKeyText();
    private static readonly Dictionary<string, Key> TextKey = KeyText.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<Key, string> MakeKeyText()
    {
        var text = new Dictionary<Key, string>();
        for (var key = Key.A; key <= Key.Z; key++)
            text[key] = ((char)('A' + (key - Key.A))).ToString();
        for (var key = Key.D0; key <= Key.D9; key++)
            text[key] = ((char)('0' + (key - Key.D0))).ToString();
        for (var key = Key.NumPad0; key <= Key.NumPad9; key++)
            text[key] = "Num" + (char)('0' + (key - Key.NumPad0));
        for (var key = Key.F1; key <= Key.F24; key++)
            text[key] = "F" + (1 + (key - Key.F1)).ToString(CultureInfo.InvariantCulture);
        foreach (var (key, name) in NamedKeys)
            text.TryAdd(key, name);
        return text;
    }

    /// <summary>The text of a key alone, as it is written in a chord and drawn on the keybind map's keys.</summary>
    public static string KeyName(Key key) => KeyText.GetValueOrDefault(key) ?? key.ToString();

    /// <summary>A mouse button's name: the middle one is button 3, the side ones 4 (back) and 5 (forward).</summary>
    public static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Left => "Mouse1",
        MouseButton.Right => "Mouse2",
        MouseButton.Middle => "Mouse3",
        MouseButton.XButton1 => "Mouse4",
        _ => "Mouse5",
    };

    /// <summary>Text form of a chord: modifiers (Ctrl, Alt, Shift, Win) then key or button.</summary>
    public static string Format(InputChord chord)
    {
        var mods = chord.Mods;
        var prefix = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")
            + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + (mods.HasFlag(ModifierKeys.Windows) ? "Win+" : "");
        return prefix + (chord.Button is { } button ? ButtonName(button) : KeyName(chord.Key));
    }

    /// <summary>A chord from its text form, or null for text that is not one.</summary>
    public static InputChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var rest = text.Trim();
        var mods = ModifierKeys.None;
        while (true)
        {
            if (rest.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Control;
            else if (rest.StartsWith("Alt+", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Alt;
            else if (rest.StartsWith("Shift+", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Shift;
            else if (rest.StartsWith("Win+", StringComparison.OrdinalIgnoreCase))
                mods |= ModifierKeys.Windows;
            else
                break;

            // The key after the modifiers may itself be "+" only as part of a longer name ("Num+" is written "=" instead).
            rest = rest[(rest.IndexOf('+') + 1)..];
            if (rest.Length == 0)
                return null;
        }

        MouseButton? button = rest.ToUpperInvariant() switch
        {
            "MOUSE1" => MouseButton.Left,
            "MOUSE2" => MouseButton.Right,
            "MOUSE3" => MouseButton.Middle,
            "MOUSE4" => MouseButton.XButton1,
            "MOUSE5" => MouseButton.XButton2,
            _ => null,
        };
        if (button is not null)
            return new InputChord(mods, Key.None, button);

        return TextKey.TryGetValue(rest, out var key) ? new InputChord(mods, Normalize(key)) : null;
    }

    private static List<InputChord> ParseAll(IEnumerable<string> texts)
    {
        var list = new List<InputChord>();
        foreach (var text in texts)
            if (Parse(text) is { } chord && !list.Contains(chord))
                list.Add(chord);
        return list;
    }

    // ----- What may be bound -----

    /// <summary>Why a chord cannot run an action, or null when it can.</summary>
    public static string? Validate(InputChord chord, string actionId)
    {
        if (chord.IsEmpty)
            return "Press a key or a mouse button.";

        var action = Action(actionId);
        var mods = chord.Mods;
        if (mods.HasFlag(ModifierKeys.Windows) || chord.Key is Key.LWin or Key.RWin)
            return "Shortcuts with the Windows key belong to Windows.";
        if (chord.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.System)
            return "A modifier key alone is not a shortcut: hold it with another key.";
        if (chord.Key == Key.S && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            return "Ctrl+Shift+S never reaches HandPeg on many PCs: graphics software takes it for its instant-replay hotkey. Save As is Ctrl+Alt+S.";
        if (chord.Key == Key.F4 && mods == ModifierKeys.Alt)
            return "Alt+F4 closes the window.";
        if (chord.Key == Key.F10 && mods is ModifierKeys.None or ModifierKeys.Shift)
            return "F10 opens the window's menu.";
        if (chord.Key == Key.Tab && mods == ModifierKeys.Shift)
            return "Shift+Tab moves the keyboard back through the controls.";
        if (chord.Key == Key.Escape)
            return "Esc cancels drags and closes menus and dialogs.";
        if (chord.Key is Key.NumLock or Key.Scroll or Key.PrintScreen)
            return "That key belongs to the keyboard and to Windows.";

        // Caps Lock may run an action (snap toggle) alone, once per press; its lock state is restored on release, which fails if held
        // or combined.
        if (chord.Key == Key.CapsLock && mods != ModifierKeys.None)
            return "Caps Lock is pressed alone.";
        if (chord.Key == Key.CapsLock && action is not null && action.Has(KeyActionFlags.Hold | KeyActionFlags.Continuous))
            return "Caps Lock runs an action once per press: it cannot be held for one.";

        if (chord.Button is MouseButton.Left or MouseButton.Right)
            return "The left and right buttons select, drag and open menus.";
        if (chord.Button == MouseButton.Middle && mods == ModifierKeys.None)
            return "The middle button pans the timeline: hold Ctrl, Alt or Shift with it.";

        if (action is not null && action.Has(KeyActionFlags.Continuous) && (chord.IsMouse || mods != ModifierKeys.None))
            return "A held navigation key is pressed alone (Shift goes faster).";
        if (action is not null && action.Has(KeyActionFlags.Hold) && (chord.IsMouse || mods != ModifierKeys.None))
            return "A held action takes a key alone.";

        // Actions that pre-empt text boxes (Save, Save As) require Ctrl/Alt or an F-key so they never steal typing keys.
        if (action is not null && action.Has(KeyActionFlags.WorksInText) && !chord.IsMouse
            && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && chord.Key is not (>= Key.F1 and <= Key.F24))
            return $"{action.Label} works while typing in a text box: hold Ctrl or Alt with the key, or use an F-key.";

        // Undo and redo keep their keys.
        foreach (var other in Actions)
            if (other.Has(KeyActionFlags.Fixed) && other.Id != actionId && other.Defaults.Any(d => Parse(d) == chord))
                return $"{Format(chord)} is {other.Label}, which stays fixed.";

        return null;
    }

    /// <summary>Load-time cleanup of user keys (AppSettings.Input.cs): drops and logs invalid chords.</summary>
    internal static void CleanUp(Dictionary<string, List<string>> custom)
    {
        foreach (var id in custom.Keys.ToList())
        {
            if (!ById.ContainsKey(id) || custom[id] is null)
            {
                custom.Remove(id);
                continue;
            }

            var list = custom[id];
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (Parse(list[i]) is { } chord && Validate(chord, id) is null)
                    continue;

                AppLog.Write($"Keybinds: dropped the shortcut \"{list[i]}\" of {id} (it can never work).");
                list.RemoveAt(i);
            }
        }
    }

    // ----- The Lightning Dial -----

    /// <summary>
    /// Dial top-level slice count (each an action or category) and actions per category.
    /// </summary>
    public const int DialSlices = 8, DialSlots = 7;

    /// <summary>The dial in effect: the settings' own, or the one HandPeg comes with.</summary>
    public static IReadOnlyList<DialCategoryState> Dial => _dial ??= Normalized(AppSettings.Current.LightningDial);

    /// <summary>
    /// Default dial: eight one-flick actions, no categories (Play/Pause, Split, Ripple Delete, Remove Dead Air, Keyframe, Auto-Captions,
    /// 3D view, Undo). Fresh lists per call.
    /// </summary>
    public static List<DialCategoryState> DefaultDial() =>
    [
        Leaf("transport.playPause"), Leaf("edit.split"), Leaf("edit.rippleDelete"), Leaf("edit.deadAir"),
        Leaf("key.toggle"), Leaf("captions.generate"), Leaf("view.exploded3d"), Leaf("edit.undo"),
    ];

    /// <summary>A slice that runs an action at once, named and drawn as the action is.</summary>
    public static DialCategoryState Leaf(string id) =>
        new() { Action = id, Name = ById.TryGetValue(id, out var action) ? action.Label.TrimEnd('.') : id, Glyph = ById.TryGetValue(id, out var known) ? known.Glyph : "" };

    /// <summary>
    /// Legacy seven-category dial (eighth slice opened Settings): seeds new categories (<see cref="CategoryFor"/>) and detects untouched
    /// legacy dials.
    /// </summary>
    private static List<DialCategoryState> LegacyCategories() =>
    [
        new() { Name = "Play", Glyph = "\uE768", Actions = ["transport.playPause", "transport.shuttleBack", "transport.stop", "transport.shuttleForward", "transport.start", "transport.end"] },
        new() { Name = "Edit", Glyph = "\uE8C6", Actions = ["edit.split", "edit.delete", "edit.rippleDelete", "edit.trimStart", "edit.trimEnd", "clip.toggleLink", "edit.deadAir"] },
        new() { Name = "Mark", Glyph = "\uE7C1", Actions = ["marker.add", "marker.prev", "marker.next"] },
        new() { Name = "Navigate", Glyph = "\uE8A3", Actions = ["view.zoomIn", "view.zoomOut", "view.fit", "transport.prevEdit", "transport.nextEdit", "transport.prevIFrame", "transport.nextIFrame"] },
        new() { Name = "Keyframes", Glyph = "\uE823", Actions = ["key.toggle", "key.prev", "key.next", "transport.stepBack", "transport.stepForward", "", ""] },
        new() { Name = "Project", Glyph = "\uE74E", Actions = ["file.save", "file.saveAs", "file.addMedia", "edit.undo", "edit.redo", "ui.settings", "help.tour"] },
        new() { Name = "Tools", Glyph = "\uE945", Actions = ["edit.deadAir", "edit.bypassEffects", "ui.xray", "view.fit", "file.addMedia", "ui.settings", "help.tour"] },
    ];

    /// <summary>Initial category for slice <paramref name="slice"/> (its legacy category, else Tools).</summary>
    public static DialCategoryState CategoryFor(int slice)
    {
        var legacy = LegacyCategories();
        return legacy[Math.Clamp(slice, 0, legacy.Count - 1)];
    }

    /// <summary>
    /// Normalizes to exactly eight slices, each a non-held action or a seven-slot category (slots: action or ""). Customized legacy dials
    /// keep their categories and Settings slice; an untouched legacy default becomes the new default.
    /// </summary>
    public static List<DialCategoryState> Normalized(List<DialCategoryState>? dial)
    {
        var defaults = DefaultDial();
        if (dial is null || IsLegacyDefault(dial))
            return defaults;

        // Legacy shape: seven categories with Settings in slice eight.
        List<DialCategoryState> given = dial.Count == DialSlices - 1 && dial.All(s => s is not null && string.IsNullOrEmpty(s.Action))
            ? [.. dial, Leaf("ui.settings")]
            : dial;

        var made = new List<DialCategoryState>(DialSlices);
        for (var i = 0; i < DialSlices; i++)
        {
            var source = i < given.Count && given[i] is { } slice ? slice : defaults[i];
            if (!string.IsNullOrEmpty(source.Action))
            {
                made.Add(Runnable(source.Action) ? Leaf(source.Action) : new DialCategoryState { Name = "", Glyph = "", Actions = [.. Enumerable.Repeat("", DialSlots)] });
                continue;
            }

            var actions = new List<string>(DialSlots);
            for (var slot = 0; slot < DialSlots; slot++)
            {
                var id = slot < (source.Actions?.Count ?? 0) ? source.Actions![slot] ?? "" : "";
                actions.Add(id.Length == 0 || Runnable(id) ? id : "");
            }

            var template = CategoryFor(i);
            made.Add(new DialCategoryState
            {
                Name = string.IsNullOrWhiteSpace(source.Name) ? template.Name : source.Name.Trim(),
                Glyph = string.IsNullOrEmpty(source.Glyph) ? template.Glyph : source.Glyph,
                Actions = actions,
            });
        }

        return made;

        static bool Runnable(string id) => ById.TryGetValue(id, out var known) && !known.Has(KeyActionFlags.Continuous);
    }

    /// <summary>True for an unmodified legacy seven-category dial.</summary>
    private static bool IsLegacyDefault(List<DialCategoryState> dial)
    {
        var legacy = LegacyCategories();
        return dial.Count == legacy.Count && dial.Zip(legacy).All(p => p.First is { } slice && string.IsNullOrEmpty(slice.Action)
            && slice.Name == p.Second.Name && slice.Glyph == p.Second.Glyph && (slice.Actions ?? []).SequenceEqual(p.Second.Actions));
    }

    /// <summary>The settings' form of a dial: null while it is the one HandPeg comes with.</summary>
    public static List<DialCategoryState>? ForSettings(IReadOnlyList<DialCategoryState> dial)
    {
        var defaults = DefaultDial();
        var same = dial.Count == defaults.Count && dial.Zip(defaults).All(p => p.First.Action == p.Second.Action
            && (p.First.IsAction || (p.First.Name == p.Second.Name && p.First.Glyph == p.Second.Glyph && p.First.Actions.SequenceEqual(p.Second.Actions))));
        return same ? null : dial.Select(c => c.Clone()).ToList();
    }
}

/// <summary>
/// Editable keymap for the Keybinds page (preset plus overrides); applied only on save (<see cref="ToSettings"/>, then
/// <see cref="KeybindManager.Reload"/>).
/// </summary>
public sealed class KeybindDraft
{
    private Dictionary<string, List<InputChord>> _chords;

    public KeybindDraft(string preset, Dictionary<string, List<string>>? custom)
    {
        Preset = KeybindManager.KnownPreset(preset);
        _chords = KeybindManager.Effective(Preset, custom);
    }

    public string Preset { get; private set; }

    /// <summary>Raised after every change: the map and the list draw themselves again.</summary>
    public event Action? Changed;

    public IReadOnlyList<InputChord> ChordsOf(string actionId) => _chords.TryGetValue(actionId, out var list) ? list : [];

    /// <summary>The actions that have the chord (more than one is a conflict, tinted on the map).</summary>
    public List<KeyAction> ActionsOn(InputChord chord)
    {
        var found = new List<KeyAction>();
        foreach (var action in KeybindManager.Actions)
            if (_chords[action.Id].Contains(chord))
                found.Add(action);
        return found;
    }

    /// <summary>Whether the chord is shared with another action (only a hand-edited file can do that).</summary>
    public bool IsConflicted(string actionId, InputChord chord) =>
        KeybindManager.Action(actionId) is not null && ActionsOn(chord).Count > 1;

    /// <summary>True for fixed chords (undo/redo; Mouse 4 for Undo is only a default).</summary>
    public static bool IsFixed(string actionId, InputChord chord) =>
        KeybindManager.Action(actionId) is { } action && action.Has(KeyActionFlags.Fixed) && action.Defaults.Any(d => KeybindManager.Parse(d) == chord);

    /// <summary>
    /// Switches preset, reapplying user overrides (diffs against the old preset) onto the new one.
    /// </summary>
    public void SetPreset(string preset)
    {
        preset = KeybindManager.KnownPreset(preset);
        if (preset == Preset)
            return;

        var custom = ToSettings();
        Preset = preset;
        _chords = KeybindManager.Effective(Preset, custom);
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds a chord to an action, removing it from any other. Returns an error or null; <paramref name="took"/> names the previous owner.
    /// </summary>
    public string? Bind(string actionId, InputChord chord, out KeyAction? took)
    {
        took = null;
        if (KeybindManager.Validate(chord, actionId) is { } problem)
            return problem;

        if (KeybindManager.Action(actionId) is null)
            return "HandPeg has no such action.";
        if (_chords[actionId].Contains(chord))
            return null;

        foreach (var other in ActionsOn(chord))
        {
            if (IsFixed(other.Id, chord))
                return $"{KeybindManager.Format(chord)} is {other.Label}, which stays fixed.";
            _chords[other.Id].Remove(chord);
            took = other;
        }

        _chords[actionId].Add(chord);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Takes a chord off an action (not undo's or redo's own).</summary>
    public void Unbind(string actionId, InputChord chord)
    {
        if (IsFixed(actionId, chord) || !_chords.TryGetValue(actionId, out var list) || !list.Remove(chord))
            return;
        Changed?.Invoke();
    }

    /// <summary>Back to the preset as it is.</summary>
    public void ResetToPreset()
    {
        _chords = KeybindManager.PresetChords(Preset);
        Changed?.Invoke();
    }

    /// <summary>The user's changes to the preset, as the settings keep them; null when there are none.</summary>
    public Dictionary<string, List<string>>? ToSettings()
    {
        var preset = KeybindManager.PresetChords(Preset);
        Dictionary<string, List<string>>? custom = null;
        foreach (var action in KeybindManager.Actions)
        {
            var mine = _chords[action.Id];
            var theirs = preset[action.Id];
            if (mine.Count == theirs.Count && mine.All(theirs.Contains))
                continue;

            custom ??= [];
            custom[action.Id] = mine.Select(KeybindManager.Format).ToList();
        }

        return custom;
    }
}
