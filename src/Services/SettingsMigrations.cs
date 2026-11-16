namespace HandPegApp.Services;

/// <summary>
/// Upgrades loaded <see cref="AppSettings"/> to the current schema: numbered one-time steps keyed by
/// <see cref="AppSettings.ViewDefaultsVersion"/>, then per-load normalizations. Pure (no I/O), so the chain is testable in memory
/// (golden <c>settings-migrations</c> runs every start version).
/// </summary>
/// <remarks>
/// <para>A step runs once when the file version is below its number and raises the version to it. Shipped steps are immutable; new
/// defaults or renames get a new step.</para>
/// <para>Version 0 predates steps. Fresh installs are stamped <see cref="Latest"/> at creation (<see cref="AppSettings.Load"/>) so
/// first-session choices are never migrated; steps that must spare first starts also test <c>fileVersion &gt;= 1</c> for files
/// predating the stamp.</para>
/// <para>Versions above <see cref="Latest"/> (newer writer: rollback or shared profile) run no steps; unknown keys are preserved via
/// <see cref="AppSettings.Extra"/>.</para>
/// </remarks>
internal static class SettingsMigrations
{
    /// <summary>The highest step number: the version every settings object is at after <see cref="Migrate"/>.</summary>
    public const int Latest = 13;

    /// <summary>
    /// Pane id renames (old to new), shared by saved dock layouts (<c>MainWindow.ReplacedPanes</c>) and the
    /// <see cref="AppSettings.HiddenTabs"/> step below.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PaneRenames { get; } = new Dictionary<string, string> { ["Tab:Filters"] = "Tab:Color" };

    /// <summary>Step category: data-shape change, one-time product default, or hardware safety reset.</summary>
    internal enum StepKind { Schema, ProductDefault, Safety }

    /// <param name="Apply">Mutates the settings; the second argument is the original file version.</param>
    internal sealed record Step(int Version, StepKind Kind, string Summary, Action<AppSettings, int> Apply);

    /// <summary>The steps in order, numbered 1 to <see cref="Latest"/>.</summary>
    internal static IReadOnlyList<Step> Steps { get; } =
    [
        new(1, StepKind.Schema, "panes and the overview scrubber get their first defaults", (s, _) =>
        {
            (s.ShowKeyframesPane, s.ShowClipKeyframes) = (false, true);
            s.MasterTimelineHeight = Math.Clamp(4 * s.TimelineHeight - 3, 1, 50);
        }),

        // Colour grading started out on the Filters tab once; it can be hidden again under Interface.
        new(2, StepKind.ProductDefault, "advanced filters tab shown once", (s, _) => s.ShowAdvancedFiltersTab = true),

        // Version 3 opened the since-removed Source pane: no-op.
        new(3, StepKind.Schema, "retired: the Source pane", (_, _) => { }),

        // Expand the Squisher's original single untouched preset to the full set; edited lists are kept.
        new(4, StepKind.ProductDefault, "squisher gets the full preset set", (s, _) =>
        {
            if (s.SquisherPresets is not { Count: > 0 }
                || s.SquisherPresets is [{ Name: "Discord (Non-Nitro)", TargetSizeMb: 20 or 25, Icon: "squish" or null }])
            {
                s.SquisherPresets = SquishPreset.Defaults();
            }
        }),

        // Overview scrubber height scale shifted two steps down (drag reaches 16 px); convert to keep the saved pixel height.
        new(5, StepKind.Schema, "overview scrubber height rescaled", (s, _) =>
        {
            if (s.MasterTimelineHeight > 0)
                s.MasterTimelineHeight += 2;
        }),

        // Default timecode display becomes frames (HH:MM:SS:FF); milliseconds remain selectable.
        new(6, StepKind.ProductDefault, "times shown as frames once", (s, file) =>
        {
            if (file >= 1)
                s.ShowTimesAsFrames = true;
        }),

        // Update an untouched "Discord (Non-Nitro)" preset (20 MB, or legacy 25 MB) to the service's 10 MB limit; edited presets are kept.
        new(7, StepKind.ProductDefault, "squisher Discord preset renamed to 10 MB", (s, _) =>
        {
            foreach (var preset in s.SquisherPresets ?? [])
            {
                if (preset is { Name: "Discord (Non-Nitro)", TargetSizeMb: 20 or 25, AudioKbps: 96 })
                    (preset.Name, preset.TargetSizeMb) = ("Discord (10 MB)", 10);
            }
        }),

        // Export GPU changed meaning (decode adapter to composite adapter): reset older saved adapters (often a secondary GPU) to Auto.
        new(8, StepKind.Safety, "export GPU back to Auto", (s, file) =>
        {
            if (file >= 1)
                s.HardwareDecodeAdapter = -1;
        }),

        // Startup uses DefaultStartupLayout (initially Standard Editing). Older files have none, so none is set and the saved
        // arrangement is kept (MainWindow.StartupLayoutJson then adopts the matching built-in layout as Default).
        new(9, StepKind.Schema, "startup layout follows the kept arrangement", (s, file) =>
        {
            if (file >= 1 && s.DockLayout is not null && s.DefaultStartupLayout == "Standard Editing")
                s.DefaultStartupLayout = null;
        }),

        // Hide the status bar once (title-bar progress wash and rail replace it); restorable via Tabs and Panes or title-bar right-click.
        new(10, StepKind.ProductDefault, "status bar hidden once", (s, _) => s.ShowStatusBar = false),

        // VIDEO_TDR_FAILURE 0x116 on a secondary Intel Arc beside an AMD primary (docs/DECISIONS.md): Auto means the compositor GPU for
        // Background and Decode. Reset saved adapters for either role to Auto once; Processor Only (-2) is an explicit choice and stays.
        new(11, StepKind.Safety, "background and decode GPU back to Auto (0x116)", (s, _) =>
        {
            if (s.BackgroundGpu >= 0)
                s.BackgroundGpu = -1;
            if (s.DecodeGpuAdapter >= 0)
                s.DecodeGpuAdapter = -1;
        }),

        // Restoring open tabs is off by default (close asks save/keep/close); reset once where preview builds saved it on.
        new(12, StepKind.ProductDefault, "keep open tabs off once", (s, file) =>
        {
            if (file >= 1)
                s.KeepOpenTabsOnClose = false;
        }),

        // "Tab:Filters" renamed "Tab:Color" (dock layouts via PaneRenames); a hidden Filters tab stays hidden as Color.
        new(13, StepKind.Schema, "hidden Filters tab renamed to Color", (s, _) =>
        {
            if (s.HiddenTabs is { } hidden && hidden.Any(PaneRenames.ContainsKey))
                s.HiddenTabs = [.. hidden.Select(t => PaneRenames.TryGetValue(t, out var renamed) ? renamed : t).Distinct()];
        }),
    ];

    /// <summary>
    /// Retired settings keys (every removed AppSettings property in history), dropped on read so <see cref="AppSettings.Extra"/> holds
    /// only newer-version keys and retired values are not rewritten forever.
    /// </summary>
    internal static IReadOnlySet<string> RetiredKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "AutoRenderTimelineProxy", "BuiltInStylesVersion", "ClearProxyCacheOnExit", "CutSegmentsOnTop", "DefaultHardwareDecoding", "DefaultMode",
        "DraftScrubSkipHeavyEffects", "EnableEditorProxies", "ExportRenderer", "KeyframesPaneHeight", "LayersWhileScrubbing", "LayoutPaneOnLeft",
        "LayoutPaneWidth", "LivePreview", "MediaBrowserShare", "ModeProfiles", "OverlayEncodingPresetCount", "OverlayEncodingPresets",
        "PresetImportChoices", "PreviewDurationSeconds", "PreviewQuality", "PreviewResolutionPercent", "ProxyCacheLimitMb", "ProxyHeavyHeight",
        "ProxyIdleSeconds", "ProxyIdleSecondsWithBackgroundCard", "ProxyScope", "RecentEditorFiles", "ShowAutoCaptions", "ShowCutSegmentsPane",
        "ShowKeyframes", "ShowSourcePane", "SidePanesOnLeft", "SidePanesWidth", "SnapTimelineToKeyframes", "StartWithFrameEngine",
        "StartWithPlayOnlySegments", "StartWithSnapToKeyframes", "SummaryCardOrder", "TimelineOnTop", "TimelineProxy", "TimelineProxyIdleSeconds",
        "UiMode", "UseEasySocialExport", "VideoProxies",
    };

    private static int _newerFileLogged;

    /// <summary>
    /// Applies pending steps and per-load normalizations in place and returns the same object. Idempotent.
    /// </summary>
    public static AppSettings Migrate(AppSettings settings)
    {
        if (settings.Extra is { } extra)
        {
            foreach (var key in RetiredKeys)
                extra.Remove(key);
            if (extra.Count == 0)
                settings.Extra = null;
        }

        // A file that spells these out as null, or holds null entries, as a hand-edited or older file might.
        settings.SmartRules = [.. (settings.SmartRules ?? []).Where(rule => rule is not null)];
        settings.SavedLayouts = [.. (settings.SavedLayouts ?? []).Where(layout => layout is not null)];
        foreach (var rule in settings.SmartRules)
            rule.Normalize();
        settings.MapLegacyPresetNames();

        // The double-click's reset target under its old name (a "style" before the canonical Game Preset wording).
        if (settings.DoubleClickResetsTo == AppSettings.LegacyResetToPreset)
            settings.DoubleClickResetsTo = AppSettings.ResetToPreset;

        // Settings saved before the option existed: on.
        settings.ShowTimelineThumbnails ??= true;

        var fileVersion = settings.ViewDefaultsVersion;
        if (fileVersion > Latest)
        {
            if (Interlocked.Exchange(ref _newerFileLogged, 1) == 0)
                AppLog.Write($"Settings: written by a newer HandPeg (settings version {fileVersion}, this one knows {Latest}); no migration runs and unknown keys are kept");
        }
        else
        {
            foreach (var step in Steps)
            {
                if (settings.ViewDefaultsVersion >= step.Version)
                    continue;
                step.Apply(settings, fileVersion);
                settings.ViewDefaultsVersion = step.Version;
            }
        }

        // Legacy one-time Editor Mode view migration flag; retained so files read as written.
        settings.Migrated411Defaults = true;

        // Shortcuts that can never work are dropped (AppSettings.Input.cs).
        settings.CleanUpInput();
        return settings;
    }
}
