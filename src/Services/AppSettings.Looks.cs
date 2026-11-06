namespace HandPegApp.Services;

// ----- How new clips are placed on the frame (4.4) -----

public sealed partial class AppSettings
{
    /// <summary>What <see cref="NewClipSizing"/> can be: the whole picture shows, or the picture covers the frame.</summary>
    public const string NewClipFit = "fit", NewClipFill = "fill";

    /// <summary>
    /// How a picture or a video added to the timeline is sized: "fit" (the default: the whole picture shows, touching the
    /// frame's edges on one side) or "fill" (it covers the whole frame, what reaches past its edges cut off). Either way its
    /// middle is the frame's. Templates, styles and HUD pieces keep the places they were made with.
    /// </summary>
    public string NewClipSizing { get; set; } = NewClipFit;

    /// <summary>What <see cref="NewClipSizing"/> can be, with the words Settings shows for each.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> NewClipSizings { get; } =
    [
        new(NewClipFit, "Fit (the whole picture shows)"),
        new(NewClipFill, "Fill (cover the frame)"),
    ];

    /// <summary>Whether new clips cover the frame (<see cref="NewClipSizing"/> is "fill"); anything else fits them inside it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NewClipsFill => string.Equals(NewClipSizing, NewClipFill, StringComparison.OrdinalIgnoreCase);

    // ----- The UI's scale (5.1-Polish, the user's directive) -----

    /// <summary>
    /// Whether HandPeg is drawn at Windows' display scaling (125 %, 150 %...), as Windows scales every program. Off (the
    /// default), it is drawn at a scale of its own (UiScale.BasePixelsPerDip, screen-aware since 5.2): 1:1 at the monitor's own
    /// pixels on a desktop monitor, with as much more room, and about 1920 DIPs across on a high-PPI screen (1.5 pixels a DIP
    /// on a 2880 x 1800 laptop at 200 %). Settings > General > Appearance; applied when the settings are saved.
    /// </summary>
    public bool FollowWindowsDpiScaling { get; set; }

    /// <summary>HandPeg's own scale on top of that, in percent (75 to 150; 100 as the setting above draws it). Settings > General > Appearance.</summary>
    public int UiScalePercent { get; set; } = 100;

    /// <summary>
    /// The interface's extra motion (5.1-AutoHud-Part1): short, bounded transitions only (the Timeline's stacks and folds, the
    /// Cutout Studio's eased zoom). Off, they snap in 0 ms. Since 5.2 nothing decorative hangs off it: the Media pane's tile tilt
    /// and the 3D view's Studio Stage are gone, and the 3D view's depth rails and gizmo are tools, always shown. Settings >
    /// General > Appearance.
    /// </summary>
    public bool EnableExtraAnimations { get; set; } = true;

    /// <summary>
    /// The <see cref="GamePresetLibrary.Version"/> of the built-in Game Presets last written into the Styles folder (5.1-Release): a
    /// newer version writes the ones missing there, never over a file. Not a choice: no control.
    /// </summary>
    public int BuiltInGamePresetsVersion { get; set; }
}
