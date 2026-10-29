namespace HandPegApp.Services;

/// <summary>
/// One category of the Lightning Dial (4.4): its name and glyph on the dial's top level, and the actions (KeybindManager
/// ids, "" for an empty slice) its seven slices run.
/// </summary>
public sealed class DialCategoryState
{
    public string Name { get; set; } = "";

    /// <summary>A Segoe MDL2 Assets glyph, as the character itself.</summary>
    public string Glyph { get; set; } = "";

    public List<string> Actions { get; set; } = [];

    public DialCategoryState Clone() => new() { Name = Name, Glyph = Glyph, Actions = [.. Actions] };
}

// Keybinds, the Lightning Dial and the Quick-Start Tour (4.4): their settings, read by KeybindManager.
public sealed partial class AppSettings
{
    /// <summary>The key map every other preset is a set of changes to.</summary>
    public const string DefaultKeybindPreset = "WASD (Default)";

    /// <summary>The preset the keys start from: one of <see cref="KeybindManager.PresetNames"/>.</summary>
    public string KeybindPreset { get; set; } = DefaultKeybindPreset;

    /// <summary>
    /// The user's own changes to the preset, by action id: the chords ("Ctrl+Alt+S", "Shift+Mouse5", "Tab") that action has
    /// now (an empty list unbinds it). Null while the preset is used as it is.
    /// </summary>
    public Dictionary<string, List<string>>? CustomKeybinds { get; set; }

    /// <summary>
    /// The pan keys (A and D as HandPeg comes) held over a Canvas or a player that is not zoomed in pan the timeline instead
    /// of doing nothing (4.4.0, the user's B7; off by default: the keys act on what is under the pointer).
    /// </summary>
    public bool PanTimelineWhenNotZoomed { get; set; }

    /// <summary>
    /// The hidden developer flight recorder (<see cref="DevFlightRecorder"/>, 4.4.0), also on while %AppData%\HandPeg\dev-verbose.flag
    /// exists. Never shown in Settings, and never written unless set: "devVerboseLogging": true in appsettings.json.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("devVerboseLogging")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool DevVerboseLogging { get; set; }

    /// <summary>The Lightning Dial's seven categories of seven actions each; null for the dial HandPeg comes with.</summary>
    public List<DialCategoryState>? LightningDial { get; set; }

    /// <summary>
    /// The Quick-Start Tour was had: offered by itself (it counts as soon as it starts), finished or skipped. While false it
    /// is offered once, on any install (4.4-BugHunt: existing ones too, not only after the first-run window); Settings still
    /// shows it whenever wanted.
    /// </summary>
    public bool QuickStartTourDone { get; set; }

    /// <summary>
    /// Whether this session began with the first-run window to come (no settings yet, or settings that never went through
    /// it). Never true for a PC that only lacks its engines. (The Quick-Start Tour no longer waits for it, 4.4-BugHunt.)
    /// </summary>
    internal static bool FirstRunThisSession { get; private set; }

    /// <summary>
    /// Load-time cleanup of the input settings: a shortcut that can never work (Ctrl+Shift+S, which graphics software takes
    /// for its instant-replay hotkey before HandPeg sees it, a Windows key chord, Alt+F4...) is dropped and logged, and an
    /// action that is no longer HandPeg's is forgotten. Costs nothing while the user has changed no key.
    /// </summary>
    private void CleanUpInput()
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

    /// <summary>A first start (no settings file, or one that could not be read): the first-run window comes before the main one.</summary>
    private static void NoteFirstRun() => FirstRunThisSession = true;

    /// <summary>Deep copies of the input settings into a <see cref="Clone"/>.</summary>
    private void CloneInput(AppSettings copy)
    {
        copy.CustomKeybinds = CustomKeybinds?.ToDictionary(p => p.Key, p => new List<string>(p.Value ?? []));
        copy.LightningDial = LightningDial?.Select(c => c.Clone()).ToList();
    }
}
