using System.IO;
using System.Text.Json;

namespace HandPegApp.Services;

/// <summary>
/// "Apply this preset to videos like this": those in a folder such as D:\Shadowplay\, those whose name or
/// path contains a word such as Overwatch, or those with an extension such as *.mkv.
/// </summary>
public sealed class SmartRule
{
    public const string Folder = "Folder";
    public const string Keyword = "Filename Keyword";
    public const string Extension = "Extension";

    public static IReadOnlyList<string> Types { get; } = [Folder, Keyword, Extension];

    /// <summary>One of <see cref="Types"/>. Rules saved before there were keyword rules have none; see <see cref="Normalize"/>.</summary>
    public string Type { get; set; } = "";

    /// <summary>What the rule looks for: the folder, the keyword, or the extension written as *.ext.</summary>
    public string Path { get; set; } = "";

    public string Preset { get; set; } = "";

    /// <summary>Gives an older rule its type: it was an extension rule when it was written as *.ext, otherwise a folder rule.</summary>
    public void Normalize()
    {
        if (!Types.Contains(Type))
            Type = Path.TrimStart().StartsWith("*.", StringComparison.Ordinal) ? Extension : Folder;
    }
}

/// <summary>
/// What the interface looks like and starts with in one mode. Encoder Mode and Editor Mode each keep their
/// own: switching mode puts the other one's away and takes this one's out, so a change made in one mode
/// (a taller timeline, the Command Preview tab) does not follow into the other. Every property here has a
/// property of the same name in <see cref="AppSettings"/>, which holds the values of the mode in use.
/// </summary>
public sealed class ModeProfile
{
    public int TimelineHeight { get; set; }
    public int AudioTrackHeight { get; set; }
    public string PresetBarLocation { get; set; } = "";
    public bool ShowAdvancedFiltersTab { get; set; }
    public bool ShowTimelineWaveform { get; set; }
    public bool ShowAdvancedPlayback { get; set; }
    public bool ShowCommandPreviewTab { get; set; }
    public bool GenerateHoverPreviews { get; set; }
    public bool? ShowTimelineThumbnails { get; set; }
    public int ThumbnailIntervalSeconds { get; set; }
    public int HoverPreviewScale { get; set; }
    public int PreviewDurationSeconds { get; set; }
    public int PreviewResolutionPercent { get; set; }
    public bool AutoOpenLayoutPane { get; set; }
    public bool ShowAutoCaptions { get; set; }
    public bool ShowKeyframes { get; set; }
    public bool SnapTimelineToKeyframes { get; set; }
    public bool AutoToggleTimelineSnap { get; set; }
    public bool SaveTargetSizeInPresets { get; set; }
    public bool LivePreview { get; set; }
    public bool StartWithPlayOnlySegments { get; set; }
    public bool StartWithSnapToKeyframes { get; set; }
    public bool StartWithChapterMarkers { get; set; }
    public bool StartWithChaptersAtCuts { get; set; }
    public bool StartWithFrameEngine { get; set; }
}

/// <summary>
/// User overrides stored in appsettings.json in the settings folder (see <see cref="AppPaths"/>). A blank value means "use the default".
/// </summary>
public sealed class AppSettings
{
    public const string DefaultYtDlpReleaseUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    public const string DefaultFfmpegReleaseUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";

    /// <summary>Where HandPeg's own releases are published. The updater (Velopack) reads them from here.</summary>
    public const string HandPegRepositoryUrl = "https://github.com/gitclanged/HandPeg";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// The list of whisper.cpp releases rather than "the latest": its numbered releases carry no programs,
    /// which are attached to separate build releases, so the newest release that has the Windows build is used.
    /// </summary>
    public const string DefaultWhisperReleaseUrl = "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=30";

    /// <summary>Where the speech models are downloaded from: this address followed by the model's file name.</summary>
    public const string WhisperModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static string FilePath => AppPaths.SettingsFile;

    /// <summary>The settings in effect. Replaced as a whole when the user saves the settings window.</summary>
    public static AppSettings Current { get; private set; } = Load();

    public string YtDlpPath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";

    /// <summary>A whisper.cpp program of the user's own (whisper-cli.exe, say), used instead of the downloaded one.</summary>
    public string WhisperPath { get; set; } = "";

    public string YtDlpReleaseUrl { get; set; } = "";
    public string FfmpegReleaseUrl { get; set; } = "";

    public string WhisperReleaseUrl { get; set; } = "";

    /// <summary>File name of the speech model auto-captions use, in the models folder.</summary>
    public string WhisperModel { get; set; } = "ggml-base.en.bin";

    /// <summary>Preset applied to a newly loaded video when no smart rule matches. Blank for none.</summary>
    public string DefaultPreset { get; set; } = "";

    public List<SmartRule> SmartRules { get; set; } = [];

    /// <summary>When on, saving a preset also stores the Target File Size that is set at the time.</summary>
    public bool SaveTargetSizeInPresets { get; set; }

    // Timeline
    public bool ShowAdvancedPlayback { get; set; }

    /// <summary>True puts the frame-step and speed controls on their own row above the timeline; false keeps them inline.</summary>
    public bool AdvancedPlaybackAboveTimeline { get; set; }

    /// <summary>True shows times as HH:MM:SS:FF (frames); false as HH:MM:SS.mmm (milliseconds).</summary>
    public bool ShowTimesAsFrames { get; set; }

    public bool SnapTimelineToKeyframes { get; set; }

    /// <summary>Whether the keyframe lines are drawn on the timeline: the "Show keyframes" box of the main window.</summary>
    public bool ShowKeyframes { get; set; } = true;

    /// <summary>When on, ticking or clearing "Show keyframes" switches <see cref="SnapTimelineToKeyframes"/> with it.</summary>
    public bool AutoToggleTimelineSnap { get; set; }

    /// <summary>How far the timeline thumb is pulled towards a keyframe while dragging: 1 (weak) to 5 (strong).</summary>
    public int TimelineMagnetism { get; set; } = 3;

    /// <summary>Builds a sheet of thumbnails after each load, shown when the pointer is over the timeline.</summary>
    public bool GenerateHoverPreviews { get; set; }

    /// <summary>Seconds between hover thumbnails. Stretched automatically for videos too long for one sheet.</summary>
    public int ThumbnailIntervalSeconds { get; set; } = 5;

    /// <summary>Size of the hover thumbnail on screen, 1 to 5: 1 is as generated, 5 is four times that.</summary>
    public int HoverPreviewScale { get; set; } = 1;

    /// <summary>What a hover preview size on the 1-to-5 scale multiplies the thumbnail by: 1 at one end, 4 at the other.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double HoverPreviewZoom => 1 + (Math.Clamp(HoverPreviewScale, 1, 5) - 1) * 0.75;

    // Render Preview
    /// <summary>Size of a rendered preview as a percentage of the real output.</summary>
    public int PreviewResolutionPercent { get; set; } = 50;

    public int PreviewDurationSeconds { get; set; } = 3;

    /// <summary>How many of the most recent yt-dlp downloads are kept between sessions.</summary>
    public int DownloadCacheSize { get; set; } = 5;

    // Interface
    public bool ShowCommandPreviewTab { get; set; }

    /// <summary>Shows the Advanced tab: colour correction, sharpening and the live frame preview.</summary>
    public bool ShowAdvancedFiltersTab { get; set; } = true;

    public const string PresetBarInSummary = "Show in Summary Tab";
    public const string PresetBarAtTop = "Show at top of Window";

    public static IReadOnlyList<string> PresetBarLocations { get; } = [PresetBarInSummary, PresetBarAtTop];

    /// <summary>Where the bar for choosing and saving presets is: one of <see cref="PresetBarLocations"/>.</summary>
    public string PresetBarLocation { get; set; } = PresetBarInSummary;

    /// <summary>Draws waveforms after each load: the mix of the active tracks behind the timeline, and each track in the Audio tab.</summary>
    public bool ShowTimelineWaveform { get; set; } = true;

    /// <summary>How tall the timeline is, 1 (a slim bar) to 5 (room for a readable waveform).</summary>
    public int TimelineHeight { get; set; } = 3;

    /// <summary>How tall each track's row is in the Audio tab, 1 to 5: taller rows show more of the waveform.</summary>
    public int AudioTrackHeight { get; set; } = 2;

    /// <summary>
    /// Draws a strip of pictures from the video along the timeline, behind the waveform. Null until it has been decided once:
    /// it then starts out on in Editor Mode and off in Encoder Mode.
    /// </summary>
    public bool? ShowTimelineThumbnails { get; set; }

    // ----- Launch -----

    /// <summary>How many style presets the launch window offers as buttons, 1 to 5. 0 switches the launch window off. It is only shown in Editor Mode.</summary>
    public int SplashPresetCount { get; set; } = 3;

    /// <summary>How many frames are taken from a video layer's file for the strip its block on the timeline is drawn with: 1 to 25.</summary>
    public int LayerThumbnailCount { get; set; } = 8;

    /// <summary>Every video that is opened starts as a vertical one: a tall frame with the picture across its middle.</summary>
    public bool DefaultVerticalVideo { get; set; }

    /// <summary>Which of the style presets HandPeg comes with have been written to the Styles folder; they are written once.</summary>
    public int BuiltInStylesVersion { get; set; }

    /// <summary>
    /// Live Preview: the player shows the picture with the export's filters applied as they are set.
    /// Remembered from the box beside the playback controls.
    /// </summary>
    public bool LivePreview { get; set; }

    /// <summary>
    /// The graphics card decodes the video in the player, as well as drawing it. Off, the processor decodes:
    /// for a driver that shows a green or garbled picture, or stutters.
    /// </summary>
    public bool PlayerHardwareAcceleration { get; set; } = true;

    // ----- Dead air -----

    /// <summary>Remove Dead Air: anything quieter than this, in decibels, counts as silence.</summary>
    public double DeadAirThresholdDb { get; set; } = -35;

    /// <summary>Remove Dead Air: a silence has to last this long, in seconds, to be cut.</summary>
    public double DeadAirMinSeconds { get; set; } = 1;

    // ----- First run, appearance and mode -----

    /// <summary>False until the first-run window has been completed once.</summary>
    public bool FirstRunComplete { get; set; }

    /// <summary>One of ThemeManager.Themes: follow Windows, or always dark, or always light.</summary>
    public string Theme { get; set; } = ThemeManager.FollowSystem;

    public const string EncoderMode = "Encoder Mode";
    public const string EditorMode = "Editor Mode";

    public const string LastUsedMode = "Last Used";

    public static IReadOnlyList<string> DefaultModes { get; } = [LastUsedMode, EncoderMode, EditorMode];

    /// <summary>The mode in use. The interface settings in this object are that mode's; the other mode's wait in <see cref="ModeProfiles"/>.</summary>
    public string UiMode { get; set; } = EncoderMode;

    /// <summary>The mode HandPeg starts in: one of the two, or whichever was in use when it was last closed.</summary>
    public string DefaultMode { get; set; } = LastUsedMode;

    /// <summary>The interface settings of each mode, by mode name. The mode in use is written here whenever the settings are saved.</summary>
    public Dictionary<string, ModeProfile> ModeProfiles { get; set; } = [];

    private static readonly System.Reflection.PropertyInfo[] ProfileProperties = typeof(ModeProfile).GetProperties();

    /// <summary>The interface settings as they are now, as a profile to put away.</summary>
    private ModeProfile CaptureProfile()
    {
        var profile = new ModeProfile();
        foreach (var property in ProfileProperties)
            property.SetValue(profile, typeof(AppSettings).GetProperty(property.Name)!.GetValue(this));
        return profile;
    }

    /// <summary>
    /// Changes mode: the interface settings of the mode being left are put away, and those of the mode being
    /// entered are taken out; a mode never used before starts from its defaults.
    /// </summary>
    public void SwitchMode(string mode)
    {
        mode = mode == EditorMode ? EditorMode : EncoderMode;
        if (mode == UiMode)
            return;

        ModeProfiles[UiMode] = CaptureProfile();
        if (ModeProfiles.TryGetValue(mode, out var profile))
        {
            foreach (var property in ProfileProperties)
                typeof(AppSettings).GetProperty(property.Name)!.SetValue(this, property.GetValue(profile));
            UiMode = mode;
        }
        else
        {
            ApplyMode(mode);
        }
    }

    /// <summary>The style presets shown as buttons in the launch window, by file name. Empty for the most recent ones.</summary>
    public List<string> SplashStylePresets { get; set; } = [];

    // What a freshly started window begins with. Unlike the settings above these are only starting points:
    // the boxes they stand for are on the main window and can be changed there for the session.
    public bool StartWithPlayOnlySegments { get; set; }
    public bool StartWithSnapToKeyframes { get; set; } = true;
    public bool StartWithChapterMarkers { get; set; } = true;
    public bool StartWithChaptersAtCuts { get; set; }

    /// <summary>A new window starts with the Frame &amp; Layer Engine switched on.</summary>
    public bool StartWithFrameEngine { get; set; }

    /// <summary>
    /// The video encoder a new window starts with, by its FFmpeg name (libx264, h264_nvenc...), as chosen in
    /// the first-run window. Blank for the built-in default, Copy.
    /// </summary>
    public string DefaultVideoEncoder { get; set; } = "";

    /// <summary>
    /// The audio codec and bitrate a new window starts with, as chosen in the first-run window: so that sound
    /// is encoded from the start, and nothing an edit does to it runs into a copied track. Blank for Copy.
    /// </summary>
    public string DefaultAudioEncoder { get; set; } = "";

    public string DefaultAudioBitrate { get; set; } = "";

    /// <summary>Asks whether to save the project when HandPeg is closed with changes that have not been saved.</summary>
    public bool PromptToSaveOnExit { get; set; } = true;

    /// <summary>Opens the Edit Layout pane by itself as soon as there is a layout to edit.</summary>
    public bool AutoOpenLayoutPane { get; set; }

    /// <summary>
    /// Sets everything a mode stands for. Encoder Mode is the lean front end for converting files; Editor Mode
    /// switches on the tools for cutting, captioning and laying out a video.
    /// </summary>
    public void ApplyMode(string mode)
    {
        var editor = mode == EditorMode;
        UiMode = editor ? EditorMode : EncoderMode;

        TimelineHeight = editor ? 5 : 1;
        PresetBarLocation = editor ? PresetBarInSummary : PresetBarAtTop;
        ShowAdvancedFiltersTab = editor;
        ShowTimelineWaveform = editor;
        ShowAdvancedPlayback = editor;
        StartWithPlayOnlySegments = editor;
        StartWithSnapToKeyframes = true;
        SnapTimelineToKeyframes = editor;
        AutoToggleTimelineSnap = editor;
        GenerateHoverPreviews = editor;
        SaveTargetSizeInPresets = editor;
        ShowAutoCaptions = editor;
        StartWithChapterMarkers = true;
        StartWithChaptersAtCuts = editor;
        AutoOpenLayoutPane = editor;
        ShowTimelineThumbnails = editor;
        StartWithFrameEngine = editor;
        if (!editor)
            (DefaultVideoEncoder, DefaultAudioEncoder, DefaultAudioBitrate) = ("", "", "");

        if (editor)
        {
            ThumbnailIntervalSeconds = 1;
            HoverPreviewScale = 3;
            PreviewDurationSeconds = 8;
            PreviewResolutionPercent = 50;
        }
        else
        {
            ThumbnailIntervalSeconds = 5;
            HoverPreviewScale = 1;
            PreviewDurationSeconds = 3;
            PreviewResolutionPercent = 50;
        }
    }

    /// <summary>Sends a Windows notification when an encode or the queue finishes.</summary>
    public bool NotifyOnEncodeComplete { get; set; } = true;

    /// <summary>The Subtitles tab's "Show Advanced" switch: true shows the auto-caption controls instead of the track list.</summary>
    public bool ShowAutoCaptions { get; set; }

    /// <summary>When an encode fails on a hardware encoder, try again once with the matching software encoder.</summary>
    public bool AutoFallbackToSoftware { get; set; } = true;

    // SponsorBlock categories yt-dlp cuts out of a download
    public bool SponsorBlockSponsor { get; set; }
    public bool SponsorBlockIntro { get; set; }
    public bool SponsorBlockOutro { get; set; }
    public bool SponsorBlockSelfPromo { get; set; }
    public bool SponsorBlockInteraction { get; set; }

    /// <summary>The value for yt-dlp's --sponsorblock-remove, or an empty string when nothing is ticked.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SponsorBlockCategories => string.Join(",", new[]
    {
        SponsorBlockSponsor ? "sponsor" : null,
        SponsorBlockIntro ? "intro" : null,
        SponsorBlockOutro ? "outro" : null,
        SponsorBlockSelfPromo ? "selfpromo" : null,
        SponsorBlockInteraction ? "interaction" : null,
    }.Where(c => c is not null));

    /// <summary>
    /// The preset to apply automatically to a source. A rule for a folder the file is in wins over a rule
    /// for a keyword, which wins over a rule for the extension; when none matches, the default applies.
    /// Among several folder or keyword rules that match, the most specific (longest) one wins.
    /// </summary>
    /// <param name="filePath">The file that is loaded.</param>
    /// <param name="sourcePath">What the user asked for: the same file, or the address it was downloaded from.</param>
    public string? FindPresetFor(string filePath, string sourcePath)
    {
        var rules = SmartRules.Where(r => !string.IsNullOrWhiteSpace(r.Path) && !string.IsNullOrWhiteSpace(r.Preset)).ToList();

        var folderRule = rules
            .Where(r => r.Type == SmartRule.Folder && IsInFolder(filePath, r.Path))
            .OrderByDescending(r => r.Path.Trim().Length)
            .FirstOrDefault();
        if (folderRule is not null)
            return folderRule.Preset;

        // A keyword may be anywhere in what was loaded: the file's name, a folder above it, or a web address.
        var keywordRule = rules
            .Where(r => r.Type == SmartRule.Keyword
                        && (sourcePath.Contains(r.Path.Trim(), StringComparison.OrdinalIgnoreCase)
                            || filePath.Contains(r.Path.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Path.Trim().Length)
            .FirstOrDefault();
        if (keywordRule is not null)
            return keywordRule.Preset;

        var extension = System.IO.Path.GetExtension(filePath);
        var extensionRule = rules.FirstOrDefault(r =>
            r.Type == SmartRule.Extension && r.Path.Trim().TrimStart('*').Equals(extension, StringComparison.OrdinalIgnoreCase));
        if (extensionRule is not null)
            return extensionRule.Preset;

        return string.IsNullOrWhiteSpace(DefaultPreset) ? null : DefaultPreset;
    }

    private static bool IsInFolder(string filePath, string folder)
    {
        try
        {
            var prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Clean(folder))) + System.IO.Path.DirectorySeparatorChar;
            return System.IO.Path.GetFullPath(filePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>A copy that can be edited without touching the settings in effect.</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SmartRules = SmartRules.Select(r => new SmartRule { Type = r.Type, Path = r.Path, Preset = r.Preset }).ToList();
        copy.ModeProfiles = new(ModeProfiles);
        copy.SplashStylePresets = [.. SplashStylePresets];
        return copy;
    }

    /// <summary>Writes the settings to disk and puts them into effect.</summary>
    public void SaveAsCurrent()
    {
        YtDlpPath = Clean(YtDlpPath);
        FfmpegPath = Clean(FfmpegPath);
        FfprobePath = Clean(FfprobePath);
        WhisperPath = Clean(WhisperPath);
        YtDlpReleaseUrl = YtDlpReleaseUrl.Trim();
        FfmpegReleaseUrl = FfmpegReleaseUrl.Trim();

        WhisperReleaseUrl = WhisperReleaseUrl.Trim();
        SmartRules = SmartRules.Where(r => !string.IsNullOrWhiteSpace(r.Path) && !string.IsNullOrWhiteSpace(r.Preset)).ToList();

        // The mode in use keeps its interface settings in the profiles too, so the file always holds both modes whole.
        ModeProfiles[UiMode] = CaptureProfile();

        Directory.CreateDirectory(AppPaths.Settings);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        Current = this;
    }

    /// <summary>Reads the file again and puts it into effect: for when it was replaced from outside, by an import.</summary>
    public static void Reload() => Current = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                foreach (var rule in settings.SmartRules)
                    rule.Normalize();

                // Settings saved before the option existed: on where the mode would have switched it on.
                settings.ShowTimelineThumbnails ??= settings.UiMode == EditorMode;

                // Starting in a set mode, whatever was in use when HandPeg was last closed.
                if (settings.DefaultMode is EncoderMode or EditorMode)
                    settings.SwitchMode(settings.DefaultMode);
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable settings file must not stop the app from starting; defaults apply.
        }

        return new AppSettings { ShowTimelineThumbnails = false };
    }

    // Paths are often pasted with the quotes Explorer's "Copy as path" adds.
    private static string Clean(string path) => path.Trim().Trim('"');
}
