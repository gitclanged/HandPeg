namespace HandPegApp.Services;

// ----- New clip placement -----

public sealed partial class AppSettings
{
    /// <summary>What <see cref="NewClipSizing"/> can be: the whole picture shows, or the picture covers the frame.</summary>
    public const string NewClipFit = "fit", NewClipFill = "fill";

    /// <summary>
    /// New visual clip sizing: "fit" (default, letterboxed) or "fill" (cover, cropped), centred either way. Templates, styles and HUD
    /// pieces keep their authored placement.
    /// </summary>
    public string NewClipSizing { get; set; } = NewClipFit;

    /// <summary>What <see cref="NewClipSizing"/> can be, with the words Settings shows for each.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> NewClipSizings { get; } =
    [
        new(NewClipFit, "Fit (the whole picture shows)"),
        new(NewClipFill, "Fill (cover the frame)"),
    ];

    /// <summary>True when <see cref="NewClipSizing"/> is "fill"; anything else means fit.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NewClipsFill => string.Equals(NewClipSizing, NewClipFill, StringComparison.OrdinalIgnoreCase);

    // ----- UI scale -----

    /// <summary>
    /// When on, follow Windows display scaling. Off (default) uses screen-aware UiScale.BasePixelsPerDip: 1:1 on desktop monitors, about
    /// 1920 DIPs wide on high-PPI screens (1.5 px/DIP on a 2880 x 1800 laptop at 200 %). Settings > General > Appearance; applied on save.
    /// </summary>
    public bool FollowWindowsDpiScaling { get; set; }

    /// <summary>Additional UI zoom in percent (75-150; 100 = base). Settings > General > Appearance.</summary>
    public int UiScalePercent { get; set; } = 100;

    /// <summary>
    /// UI motion: short bounded transitions (Timeline stacks and folds, Cutout Studio eased zoom); off snaps in 0 ms. No decorative
    /// effects depend on it; 3D View depth rails and gizmo always show. Settings > General > Appearance.
    /// </summary>
    public bool EnableExtraAnimations { get; set; } = true;

    /// <summary>
    /// Last seeded <see cref="GamePresetLibrary.Version"/>; newer versions write missing built-ins (never overwriting). Internal, no UI.
    /// </summary>
    public int BuiltInGamePresetsVersion { get; set; }
}
