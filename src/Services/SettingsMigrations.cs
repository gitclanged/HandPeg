namespace HandPegApp.Services;

/// <summary>
/// Brings an <see cref="AppSettings"/> read from appsettings.json up to the current schema: the numbered one-time steps keyed by
/// <see cref="AppSettings.ViewDefaultsVersion"/>, followed by the normalizations every load applies. Pure: no file access, so the
/// whole chain is testable in memory (the <c>settings-migrations</c> golden runs it from every start version).
/// </summary>
/// <remarks>
/// <para>Each step runs once, when the file's version is below the step's number, and raises the version to that number. Steps
/// are never edited after they ship: a new default or rename is a new step with the next number.</para>
/// <para>Version 0 means a file from before the steps existed. A fresh install is stamped with <see cref="Latest"/> when its
/// settings are created (<see cref="AppSettings.Load"/>), so its own first-session choices are never treated as an old file's.
/// Steps that must not touch a first start's choices still test <c>fileVersion &gt;= 1</c>, for files written before that
/// stamp existed.</para>
/// <para>A file whose version is above <see cref="Latest"/> was written by a newer HandPeg (a rollback, or two versions sharing
/// one profile): no step runs, and the keys this version does not know are carried through <see cref="AppSettings.Extra"/>, so
/// saving it does not drop them.</para>
/// </remarks>
internal static class SettingsMigrations
{
    /// <summary>The highest step number: the version every settings object is at after <see cref="Migrate"/>.</summary>
    public const int Latest = 13;

    /// <summary>
    /// Panes renamed since a layout or a hidden-tab list could have saved them: old id to new id. One table for the dock's saved
    /// arrangements (<c>MainWindow.ReplacedPanes</c>) and for the <see cref="AppSettings.HiddenTabs"/> step below.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PaneRenames { get; } = new Dictionary<string, string> { ["Tab:Filters"] = "Tab:Color" };

    /// <summary>What a step is for: a data-shape change, a one-time product default chosen by the user, or a hardware safety reset.</summary>
    internal enum StepKind { Schema, ProductDefault, Safety }

    /// <param name="Apply">Changes the settings; the second argument is the version the file was written with (before any step ran).</param>
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

        // Version 3 opened the Source pane, which Editor Mode no longer has (Add Media in its header replaced it): nothing to do.
        new(3, StepKind.Schema, "retired: the Source pane", (_, _) => { }),

        // The Squisher shipped with one preset and gained the others later. A list that is still exactly that one untouched preset
        // gets the full set; an edited list is kept.
        new(4, StepKind.ProductDefault, "squisher gets the full preset set", (s, _) =>
        {
            if (s.SquisherPresets is not { Count: > 0 }
                || s.SquisherPresets is [{ Name: "Discord (Non-Nitro)", TargetSizeMb: 20 or 25, Icon: "squish" or null }])
            {
                s.SquisherPresets = SquishPreset.Defaults();
            }
        }),

        // The overview scrubber's height scale moved two steps lower (so a drag reaches 16 px): a saved height keeps its pixels.
        new(5, StepKind.Schema, "overview scrubber height rescaled", (s, _) =>
        {
            if (s.MasterTimelineHeight > 0)
                s.MasterTimelineHeight += 2;
        }),

        // Times read in frames (HH:MM:SS:FF) by default since 5.3.2. Milliseconds can be chosen again afterwards.
        new(6, StepKind.ProductDefault, "times shown as frames once", (s, file) =>
        {
            if (file >= 1)
                s.ShowTimesAsFrames = true;
        }),

        // The Squisher's first preset is the chat service's own 10 MB limit: an untouched "Discord (Non-Nitro)" (20 MB, or the
        // 25 MB of older builds) takes its name and size once. A preset the user changed stays theirs.
        new(7, StepKind.ProductDefault, "squisher Discord preset renamed to 10 MB", (s, _) =>
        {
            foreach (var preset in s.SquisherPresets ?? [])
            {
                if (preset is { Name: "Discord (Non-Nitro)", TargetSizeMb: 20 or 25, AudioKbps: 96 })
                    (preset.Name, preset.TargetSizeMb) = ("Discord (10 MB)", 10);
            }
        }),

        // The Export GPU is the card exports are composited on since 5.4.1. A card saved before then meant something else (the card an
        // export decoded on), often the second card a hardware test chose: it goes back to Auto once.
        new(8, StepKind.Safety, "export GPU back to Auto", (s, file) =>
        {
            if (file >= 1)
                s.HardwareDecodeAdapter = -1;
        }),

        // HandPeg opens in the Default layout (DefaultStartupLayout, Standard Editing to start with). A file from before that kept its
        // own arrangement has no Default: none is set, so the arrangement the user left is kept (MainWindow.StartupLayoutJson then makes
        // the built-in layout of that shape the Default).
        new(9, StepKind.Schema, "startup layout follows the kept arrangement", (s, file) =>
        {
            if (file >= 1 && s.DockLayout is not null && s.DefaultStartupLayout == "Standard Editing")
                s.DefaultStartupLayout = null;
        }),

        // The status bar is hidden once for everyone (product decision: the title bar's progress wash and rail carry the work).
        // Tabs and Panes, or a right-click on the title bar status, brings it back.
        new(10, StepKind.ProductDefault, "status bar hidden once", (s, _) => s.ShowStatusBar = false),

        // 0x116 VIDEO_TDR_FAILURE in the Intel Arc A310 driver beside an RX 7900 XTX (docs/DECISIONS.md): Auto is the compositor GPU
        // for the background and decode roles, never a second card. A card saved for either role (usually a hardware test's pick of
        // the second card) goes back to Auto once. Processor Only (-2) is the user's explicit choice and stays.
        new(11, StepKind.Safety, "background and decode GPU back to Auto (0x116)", (s, _) =>
        {
            if (s.BackgroundGpu >= 0)
                s.BackgroundGpu = -1;
            if (s.DecodeGpuAdapter >= 0)
                s.DecodeGpuAdapter = -1;
        }),

        // Keeping open project tabs between sessions is off by default (closing asks whether to save, keep or close the tabs); 5.5.0
        // previews saved it on, so it goes off once.
        new(12, StepKind.ProductDefault, "keep open tabs off once", (s, file) =>
        {
            if (file >= 1)
                s.KeepOpenTabsOnClose = false;
        }),

        // The Filters tab became the Color tab ("Tab:Filters" is "Tab:Color"; saved dock arrangements follow through PaneRenames):
        // a hidden Filters tab stays hidden as the Color tab.
        new(13, StepKind.Schema, "hidden Filters tab renamed to Color", (s, _) =>
        {
            if (s.HiddenTabs is { } hidden && hidden.Any(PaneRenames.ContainsKey))
                s.HiddenTabs = [.. hidden.Select(t => PaneRenames.TryGetValue(t, out var renamed) ? renamed : t).Distinct()];
        }),
    ];

    /// <summary>
    /// Settings keys earlier versions wrote and this one retired (every AppSettings property name in the repository's history that is
    /// gone now). They are dropped as a file is read, so <see cref="AppSettings.Extra"/> carries only keys of a newer version, and a
    /// retired setting is not written back forever.
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
    /// Runs the steps the settings have not had and the normalizations every load applies, in place, and returns the same object.
    /// Running it twice changes nothing the second time.
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

        // 4.1.1 brought Editor Mode's saved views to its own once; every file written since says so. Kept so a file reads as written.
        settings.Migrated411Defaults = true;

        // Shortcuts that can never work are dropped (AppSettings.Input.cs).
        settings.CleanUpInput();
        return settings;
    }
}
