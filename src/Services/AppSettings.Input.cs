namespace HandPegApp.Services;

/// <summary>
/// Lightning Dial top-level slice: either an immediate action (<see cref="Action"/>, a KeybindManager id; the default dial has eight)
/// or a category with name, glyph and seven sub-slice action ids ("" = empty).
/// </summary>
public sealed class DialCategoryState
{
    /// <summary>Immediate action id; "" for a category (<see cref="Actions"/>).</summary>
    public string Action { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>A Segoe MDL2 Assets glyph, as the character itself.</summary>
    public string Glyph { get; set; } = "";

    public List<string> Actions { get; set; } = [];

    /// <summary>Whether the slice runs an action at once, rather than opening a category.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAction => Action.Length > 0;

    public DialCategoryState Clone() => new() { Action = Action, Name = Name, Glyph = Glyph, Actions = [.. Actions] };
}

// Keybind, Lightning Dial and Quick-Start Tour settings (read by KeybindManager).
public sealed partial class AppSettings
{
    /// <summary>The key map every other preset is a set of changes to.</summary>
    public const string DefaultKeybindPreset = "WASD (Default)";

    /// <summary>The preset the keys start from: one of <see cref="KeybindManager.PresetNames"/>.</summary>
    public string KeybindPreset { get; set; } = DefaultKeybindPreset;

    /// <summary>
    /// Per-action overrides of the keymap preset: chords ("Ctrl+Alt+S", "Shift+Mouse5", "Tab"); an empty list unbinds. Null = unmodified.
    /// </summary>
    public Dictionary<string, List<string>>? CustomKeybinds { get; set; }

    /// <summary>
    /// When on, pan keys (default A/D) over an unzoomed Canvas or player pan the Timeline instead. Off by default (keys target the hovered pane).
    /// </summary>
    public bool PanTimelineWhenNotZoomed { get; set; }

    /// <summary>
    /// Momentum scrolling after flicks or fast wheels (Timeline, Subtitle Studio strip, Media Browser; <see cref="MomentumScroller"/>):
    /// starts at 250 DIP/s, not after a 40 ms pause before release, stopped by any press. On by default; also in Timeline Settings.
    /// </summary>
    public bool EnableMomentumScrolling { get; set; } = true;

    /// <summary>
    /// Cursor-Locked Scrub Pointer (Pass 5.7.1): while the playhead is dragged the OS cursor is hidden and the playhead's own visual
    /// draws the pointer, so the two never part on a high-refresh display; the OS cursor returns on release, capture loss, Esc or a
    /// fault. Off by default (user decision Q1); Settings > Timeline. Serialized only when on; <see cref="Clone"/> copies it with the
    /// other value settings (MemberwiseClone).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CursorLockedScrubPointer { get; set; }

    /// <summary>
    /// Developer flight recorder (<see cref="DevFlightRecorder"/>; also on while %AppData%\HandPeg\dev-verbose.flag exists). Settings >
    /// General > Diagnostics > Verbose flight recorder, off by default; serialized only when true.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("devVerboseLogging")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool DevVerboseLogging { get; set; }

    /// <summary>Lightning Dial's eight slices (action or seven-slot category); null for the default dial.</summary>
    public List<DialCategoryState>? LightningDial { get; set; }

    /// <summary>
    /// True once the Quick-Start Tour was auto-offered (counted at start), finished or skipped. While false it is offered once on any
    /// install; Settings can always reopen it.
    /// </summary>
    public bool QuickStartTourDone { get; set; }

    /// <summary>
    /// True when this session shows the first-run window (no settings, or first run never completed); never just for missing engines.
    /// </summary>
    internal static bool FirstRunThisSession { get; private set; }

    /// <summary>
    /// Load-time input cleanup: drops and logs unusable chords (Ctrl+Shift+S taken by GPU instant-replay hotkeys, Win chords, Alt+F4, ...)
    /// and unknown action ids. Free when no keys were customized.
    /// </summary>
    internal void CleanUpInput()
    {
        if (!FirstRunComplete)
            FirstRunThisSession = true;

        // (KeybindManager reads no settings while it is made, so it may be used here, as AppSettings.Current is loaded.)
        if (KeybindPreset != DefaultKeybindPreset)
            KeybindPreset = KeybindManager.KnownPreset(KeybindPreset);
        if (CustomKeybinds is { Count: > 0 } custom)
            KeybindManager.CleanUp(custom);
        else
            CustomKeybinds = null;
    }

    /// <summary>First start (no or unreadable settings): the first-run window precedes the main window.</summary>
    private static void NoteFirstRun() => FirstRunThisSession = true;

    /// <summary>Deep copies of the input settings into a <see cref="Clone"/>.</summary>
    private void CloneInput(AppSettings copy)
    {
        copy.CustomKeybinds = CustomKeybinds?.ToDictionary(p => p.Key, p => new List<string>(p.Value ?? []));
        copy.LightningDial = LightningDial?.Select(c => c.Clone()).ToList();
    }
}
