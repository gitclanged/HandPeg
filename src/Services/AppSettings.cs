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
/// What the interface looked like in one of the two modes HandPeg had before 4.2 (Encoder Mode left for HandPeg Encoder):
/// only read, from settings saved then, so that settings last saved in Encoder Mode take up Editor Mode's once
/// (<see cref="AppSettings.ModeProfiles"/>). Every property here has a property of the same name in <see cref="AppSettings"/>.
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
    public bool ShowIFrames { get; set; }
    public bool SnapTimelineToIFrames { get; set; }
    public bool AutoToggleTimelineSnap { get; set; }
    public bool SaveTargetSizeInPresets { get; set; }
    public bool StartWithSnapToIFrames { get; set; }
    public bool StartWithChapterMarkers { get; set; }
    public bool StartWithChaptersAtCuts { get; set; }
    public bool StartWithFrameEngine { get; set; }
    public int MasterTimelineHeight { get; set; }
    public bool ShowKeyframesPane { get; set; }
    public bool ShowEffectsPane { get; set; } = true;
    public bool ShowMediaBrowser { get; set; }
    public string? PlaybackControls { get; set; }
    public string? DockLayout { get; set; }
    public List<SavedDockLayout> SavedLayouts { get; set; } = [];
    public bool ShowClipKeyframes { get; set; }
    public List<string>? HiddenTabs { get; set; }
    public bool ShowMasterTimeline { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
}

/// <summary>A layout of the user's own (Layout button, Save Current Layout): its name, its place (1 to 3), and the arrangement (Docking.DockLayoutState as JSON).</summary>
public sealed class SavedDockLayout
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public string Layout { get; set; } = "";
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

    // Declared before Current: static fields are set in the order they are written, and loading the settings uses this.
    private static readonly System.Reflection.PropertyInfo[] ProfileProperties = typeof(ModeProfile).GetProperties();

    /// <summary>Editor Mode's saved views were brought to 4.1.1's (no status bar, the Audio tab after Layers), once (see <see cref="Load"/>).</summary>
    public bool Migrated411Defaults { get; set; }

    /// <summary>The settings in effect. Replaced as a whole when the user saves the settings window.</summary>
    public static AppSettings Current { get; private set; } = Load();

    public string YtDlpPath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";

    /// <summary>A whisper.cpp program of the user's own (whisper-cli.exe, say), used instead of the downloaded one.</summary>
    public string WhisperPath { get; set; } = "";

    /// <summary>A while after startup, the tools HandPeg installed are replaced by their newest releases, in the background.</summary>
    public bool AutoUpdateDependencies { get; set; } = true;

    /// <summary>Looks for a newer HandPeg at startup and offers the Update button; nothing is downloaded until it is pressed.</summary>
    public bool CheckAppUpdatesOnStartup { get; set; } = true;

    /// <summary>Downloads a newer HandPeg as soon as it is found (and puts it in when HandPeg closes), instead of waiting for the Update button.</summary>
    public bool AutoDownloadAppUpdates { get; set; }

    public string YtDlpReleaseUrl { get; set; } = "";
    public string FfmpegReleaseUrl { get; set; } = "";

    /// <summary>
    /// Installs the FFmpeg 8.1 build instead of the newest one. Newer FFmpeg asks for an NVIDIA driver (610 or
    /// later) that cards up to the GTX 10 series never get, and NVENC does not start at all without it. Turned
    /// on by the hardware encoder check when it sees exactly that; see <see cref="EncoderProber.NvencNeedsOlderFfmpeg"/>.
    /// </summary>
    public bool UseFfmpegForOlderNvidiaDrivers { get; set; }

    public string WhisperReleaseUrl { get; set; } = "";

    // ----- Pinned dependency builds (v4.1) -----

    /// <summary>
    /// Off (the default): FFmpeg, libmpv and whisper.cpp are installed and updated only to the builds this version of HandPeg
    /// was tested with (see <see cref="DependencyUpdater.PinnedBuilds"/>). On: to their newest upstream releases, untested.
    /// Turned on only after a warning; Restore Pinned Recommended Versions turns it off again. yt-dlp follows its newest release either way.
    /// </summary>
    public bool AllowUnpinnedDependencyUpdates { get; set; }

    /// <summary>File name of the speech model auto-captions use, in the models folder.</summary>
    public string WhisperModel { get; set; } = "ggml-base.en.bin";

    // ----- How whisper listens: the Auto-Captions tab. The defaults are whisper.cpp's own. -----

    /// <summary>The spoken language a new window starts with, as whisper names it (en, es...), or auto. A preset or a project sets its own.</summary>
    public string WhisperDefaultLanguage { get; set; } = "en";

    /// <summary>Beam search: how many candidate sentences whisper follows at once, 1 to 16. More is more careful and slower.</summary>
    public int WhisperBeamSize { get; set; } = 5;

    /// <summary>How many candidates whisper keeps when it samples, 1 to 16.</summary>
    public int WhisperBestOf { get; set; } = 5;

    /// <summary>The sampling temperature, 0 to 1: 0 always takes the likeliest word.</summary>
    public double WhisperTemperature { get; set; }

    /// <summary>When a stretch fails to decode, whisper tries it again at a higher temperature.</summary>
    public bool WhisperTemperatureFallback { get; set; } = true;

    /// <summary>Silence detection (VAD): whisper is handed only the stretches with speech in them, when its model is installed.</summary>
    public bool UseVad { get; set; } = true;

    /// <summary>How sure the detector has to be that a sound is speech, 0 to 1. Higher leaves more out.</summary>
    public double VadThreshold { get; set; } = 0.5;

    /// <summary>Speech shorter than this, in milliseconds, is taken for noise.</summary>
    public int VadMinSpeechMs { get; set; } = 250;

    /// <summary>A silence has to last this long, in milliseconds, to end a stretch of speech.</summary>
    public int VadMinSilenceMs { get; set; } = 100;

    /// <summary>Milliseconds kept on either side of a stretch of speech, so that words are not clipped.</summary>
    public int VadSpeechPadMs { get; set; } = 30;

    /// <summary>
    /// When a new video is loaded, the audio track whose title names a microphone or a voice ("Mic", "Track 2: Microphone")
    /// is marked as its Voice track (4.3E, <see cref="VoiceTrackScanner"/>): Auto-Captions listen to it, and the sounds set
    /// to Duck make room for it. A project or a queued job keeps its tracks as they were saved.
    /// </summary>
    public bool AutoMarkVoiceTrack { get; set; } = true;

    /// <summary>The caption style a new window starts with; null for the built-in one. A preset or a project sets its own.</summary>
    public HandPegApp.Models.CaptionStyle? DefaultCaptionStyle { get; set; }

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

    public bool SnapTimelineToIFrames { get; set; }

    /// <summary>Whether the I-frame lines are drawn on the timeline: the "Show I-frames" box of the main window.</summary>
    public bool ShowIFrames { get; set; } = true;

    /// <summary>When on, ticking or clearing "Show I-frames" switches <see cref="SnapTimelineToIFrames"/> with it.</summary>
    public bool AutoToggleTimelineSnap { get; set; }

    /// <summary>How far the timeline thumb is pulled towards an I-frame while dragging: 1 (weak) to 5 (strong).</summary>
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

    /// <summary>
    /// Where a URL's download is saved, to keep; blank keeps downloads in the cache, where only the most recent
    /// <see cref="DownloadCacheSize"/> are kept. Files saved here are never deleted by HandPeg.
    /// </summary>
    public string DownloadFolder { get; set; } = "";

    /// <summary>
    /// The cookies a download that a site walls off (age check, sign-in, 403) is tried again with, so it is fetched as
    /// the signed-in browser would: "auto" tries each browser found on this PC (Waterfox, Firefox, Edge, Chrome, Brave)
    /// and the cookies file when one is named; a browser's name, that browser only; "file", <see cref="YtDlpCookiesFilePath"/>
    /// only; "off", never. The first try is always without cookies. One of <see cref="YtDlpCookieSources"/>.
    /// </summary>
    public string YtDlpCookieSource { get; set; } = CookiesAuto;

    /// <summary>A cookies.txt (Netscape format, exported from a browser) for <see cref="YtDlpCookieSource"/> "file" or "auto"; blank for none.</summary>
    public string YtDlpCookiesFilePath { get; set; } = "";

    public const string CookiesAuto = "auto", CookiesFile = "file", CookiesOff = "off";

    /// <summary>What <see cref="YtDlpCookieSource"/> can be, with the words Settings shows for each.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> YtDlpCookieSources { get; } =
    [
        new(CookiesAuto, "Automatic (Any Browser Found)"),
        new("waterfox", "Waterfox"),
        new("firefox", "Firefox"),
        new("edge", "Edge"),
        new("chrome", "Chrome"),
        new("brave", "Brave"),
        new(CookiesFile, "Cookies File (cookies.txt)"),
        new(CookiesOff, "Off (Never Use Cookies)"),
    ];

    // Preview caches (see PreviewCache)
    /// <summary>Keeps the timeline's pictures of the videos loaded (hover sheet, strips of frames, waveforms, I-frames) on disk between sessions.</summary>
    public bool CacheTimelinePreviews { get; set; } = true;

    /// <summary>How large the timeline's cache may grow, in megabytes, before what was used longest ago is deleted.</summary>
    public int TimelineCacheMegabytes { get; set; } = 1024;

    /// <summary>Keeps the media browser's frames of each video on disk between sessions.</summary>
    public bool CacheBrowserPreviews { get; set; } = true;

    /// <summary>How large the media browser's cache may grow, in megabytes.</summary>
    public int BrowserCacheMegabytes { get; set; } = 512;

    // ----- Proxies (Settings > Preview & Cache > Proxies; see ProxyCache) -----

    /// <summary>
    /// Per-Clip Video Proxies: heavy videos on the timeline (taller than <see cref="ProxyHeavyHeight"/>, AV1, VP9,
    /// 10-bit HEVC, ProRes, or I-frames further apart than <see cref="ProxyHeavyIFrameSeconds"/>) get a small copy
    /// made in the background, which Live Preview plays in their place. Exports always read the originals.
    /// </summary>
    public bool VideoProxies { get; set; } = true;

    public const string ProxyLayersOnly = "Video layers only", ProxyAllVideos = "Video layers and the main video";

    public static IReadOnlyList<string> ProxyScopes { get; } = [ProxyLayersOnly, ProxyAllVideos];

    /// <summary>Which videos may get a proxy: the video layers, or the main video as well.</summary>
    public string ProxyScope { get; set; } = ProxyLayersOnly;

    /// <summary>A video whose shorter side is longer than this, in pixels, counts as heavy (1080: anything above 1080p, upright or on its side).</summary>
    public int ProxyHeavyHeight { get; set; } = 1080;

    /// <summary>A video whose I-frames are further apart than this, in seconds, counts as heavy (every seek decodes that far).</summary>
    public double ProxyHeavyIFrameSeconds { get; set; } = 2;

    /// <summary>
    /// The Composed Timeline Proxy: the whole sequence rendered once, in the background, as one small video the
    /// player plays with no filter graph at all; dropped at once when anything is changed. Off by default.
    /// </summary>
    public bool TimelineProxy { get; set; }

    public static IReadOnlyList<int> ProxyTiers { get; } = [480, 540, 720];

    /// <summary>How tall proxies are made, in pixels: 480, 540 or 720.</summary>
    public int ProxyTier { get; set; } = 540;

    public const string ProxyCodecH264 = "H.264 (short GOP)", ProxyCodecMjpeg = "MJPEG (every frame an I-frame)";

    public static IReadOnlyList<string> ProxyCodecs { get; } = [ProxyCodecH264, ProxyCodecMjpeg];

    /// <summary>What proxies are encoded as: H.264 with an I-frame every 15 frames and no B-frames, or MJPEG (instant seeks, about three times the size).</summary>
    public string ProxyCodec { get; set; } = ProxyCodecH264;

    public const string ProxyEncoderAuto = "Auto", ProxyEncoderProcessor = "Processor";

    public static IReadOnlyList<string> ProxyEncoders { get; } = [ProxyEncoderAuto, "NVENC", "Quick Sync", "AMF", ProxyEncoderProcessor];

    /// <summary>Which encoder makes H.264 proxies: Auto (the background card's, then the main card's, then the processor), or one named.</summary>
    public string ProxyEncoder { get; set; } = ProxyEncoderAuto;

    /// <summary>How long HandPeg has to be left alone (no input, not playing, not exporting) before proxies start being made, in seconds.</summary>
    public int ProxyIdleSeconds { get; set; } = 10;

    /// <summary>The same wait when a graphics card other than the player's makes the proxies (Settings, Performance): it takes nothing from playback.</summary>
    public int ProxyIdleSecondsWithBackgroundCard { get; set; } = 2;

    /// <summary>A proxy being made is paused (its FFmpeg suspended) while the player plays, and goes on after.</summary>
    public bool PauseProxiesWhilePlaying { get; set; } = true;

    /// <summary>How large the proxy cache may grow, in gigabytes, before the proxies used longest ago are deleted.</summary>
    public double ProxyCacheGigabytes { get; set; } = 20;

    public const string ProxiesInCache = "In the cache folder", ProxiesBesideProject = "Beside the project";

    public static IReadOnlyList<string> ProxyLocations { get; } = [ProxiesInCache, ProxiesBesideProject];

    /// <summary>Where proxies are kept: with the other caches, or in a folder beside the project file (Name.proxies) when there is one.</summary>
    public string ProxyLocation { get; set; } = ProxiesInCache;

    /// <summary>The proxies of files that have been deleted are deleted too, at startup.</summary>
    public bool PurgeProxiesOfDeletedFiles { get; set; } = true;

    /// <summary>The proxies of the project's files are deleted when the project is closed (another opened, or HandPeg closed).</summary>
    public bool ClearProjectProxiesOnExit { get; set; }

    /// <summary>Whether anything uses the proxy cache.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesProxies => VideoProxies || TimelineProxy;

    // ----- Cache clean-up at startup -----

    /// <summary>The previews of files that have been deleted (or changed) are deleted from the caches, a while after startup.</summary>
    public bool PurgeMissingAtStartup { get; set; } = true;

    /// <summary>How long after startup the clean-up runs, in seconds.</summary>
    public int PurgeDelaySeconds { get; set; } = 60;

    /// <summary>A file on a drive that is not there (unplugged, offline) counts as deleted. Off: its previews are kept until the size limit takes them.</summary>
    public bool MissingDrivesCountAsDeleted { get; set; }

    // ----- Media browser: indexing the folder shown -----

    /// <summary>How long a folder stays open in the media browser before all of its videos are prepared, in seconds; 0 for never.</summary>
    public int BrowserIndexDelaySeconds { get; set; } = 40;

    /// <summary>The most videos of one folder prepared that way.</summary>
    public int BrowserIndexMaxFiles { get; set; } = 500;

    /// <summary>Folders are indexed on battery power too.</summary>
    public bool BrowserIndexOnBattery { get; set; }

    /// <summary>Folders on network drives are indexed too.</summary>
    public bool BrowserIndexNetwork { get; set; }

    /// <summary>A notification says when a folder has been indexed.</summary>
    public bool NotifyIndexingDone { get; set; }

    // ----- Downloads used by projects -----

    /// <summary>A download in the download cache that a project uses is not deleted when the cache is trimmed.</summary>
    public bool KeepDownloadsUsedByProjects { get; set; } = true;

    /// <summary>Saving a project that uses files from the download cache offers to copy them into the download folder.</summary>
    public bool AskToCopyDownloadsOnSave { get; set; } = true;

    // Media browser
    /// <summary>How many frames are taken from each video in the media browser, for the picture that follows the pointer over its tile.</summary>
    public int BrowserPreviewFrames { get; set; } = 15;

    /// <summary>How tall those frames are made, in pixels.</summary>
    public int BrowserPreviewHeight { get; set; } = 480;

    /// <summary>A clip dragged or dropped on the time bars snaps to the edges of the others, the end of the sequence and the playhead.</summary>
    public bool SnapClips { get; set; } = true;

    /// <summary>
    /// A clip dropped on a track that has room for it at that moment joins it. Off (the default), every
    /// clip dropped gets a track of its own, just above the row it was dropped on. Either way nothing is ever laid over
    /// a clip already there: a clip that would overlap one goes on a new track instead.
    /// </summary>
    public bool DroppedClipsJoinTrack { get; set; }

    /// <summary>The folder the media browser last showed.</summary>
    public string BrowserFolder { get; set; } = "";

    /// <summary>How large the media browser's tiles are, 0 to 100: the smallest few are the list; 38 is about 200 pixels wide.</summary>
    public int BrowserTileSize { get; set; } = 38;

    /// <summary>What the media browser's files are sorted by: Name, Date, Size or Type.</summary>
    public string BrowserSort { get; set; } = "Name";

    public bool BrowserSortDescending { get; set; }

    /// <summary>The media browser lists the files the open project uses (Project Media) instead of a folder (Folder Browser).</summary>
    public bool BrowserShowsProject { get; set; }

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
    /// it then starts out on.
    /// </summary>
    public bool? ShowTimelineThumbnails { get; set; }

    // ----- Launch -----

    /// <summary>How many style presets the drag-and-drop overlay offers, from the top of <see cref="SplashStylePresets"/>: 0 to 8.</summary>
    public int SplashPresetCount { get; set; } = 3;

    /// <summary>How many frames are taken from a video layer's file for the strip its block on the timeline is drawn with: 1 to 25.</summary>
    public int LayerThumbnailCount { get; set; } = 8;

    /// <summary>Every video that is opened starts as a vertical one: a tall frame with the picture across its middle.</summary>
    public bool DefaultVerticalVideo { get; set; }

    /// <summary>Which of the style presets HandPeg comes with have been written to the Styles folder; they are written once.</summary>
    public int BuiltInStylesVersion { get; set; }

    public const string ScrubLayersLive = "Live", ScrubLayersStill = "Still frame", ScrubLayersHidden = "Hidden";

    /// <summary>
    /// Live Preview, while the playhead is dragged or wound back: video layers decoded as they play (Live), one
    /// frame each, the I-frame nearest the playhead (Still frame), or left out (Hidden). They play live again
    /// as soon as the playhead is let go.
    /// </summary>
    public string LayersWhileScrubbing { get; set; } = ScrubLayersStill;

    public static IReadOnlyList<string> ScrubLayerChoices { get; } = [ScrubLayersLive, ScrubLayersStill, ScrubLayersHidden];

    /// <summary>
    /// The graphics card decodes the video in the player, as well as drawing it. Off, the processor decodes:
    /// for a driver that shows a green or garbled picture, or stutters.
    /// </summary>
    public bool PlayerHardwareAcceleration { get; set; } = true;

    /// <summary>
    /// Hardware Decode Adapter: which graphics card decodes for an encode, by its Direct3D number, so that the
    /// work can be given to a second card. -1 leaves the choice to FFmpeg.
    /// </summary>
    public int HardwareDecodeAdapter { get; set; } = -1;

    // ----- Performance: the tools HandPeg runs in the background (see WorkScheduler, JobObjects) -----

    /// <summary>Background tools at once (thumbnails, waveforms, I-frame lists, proxies); 0 is Auto: one for every six processors not reserved.</summary>
    public int BackgroundJobs { get; set; }

    /// <summary>Processors kept free for the interface and the player: no tool runs on the first this many. -1 is Auto (2).</summary>
    public int ReservedCores { get; set; } = -1;

    public const string PriorityBelowNormal = "Below Normal", PriorityIdle = "Idle";

    public static IReadOnlyList<string> BackgroundPriorities { get; } = [PriorityBelowNormal, PriorityIdle];

    /// <summary>The priority background tools run at: Below Normal, or Idle (only when nothing else wants the processor).</summary>
    public string BackgroundPriority { get; set; } = PriorityBelowNormal;

    /// <summary>Background tools run in Windows' Efficiency mode (EcoQoS): slower cores, lower clocks.</summary>
    public bool BackgroundEfficiencyMode { get; set; } = true;

    /// <summary>Background work waiting to start waits while the player plays (running tools carry on).</summary>
    public bool PauseBackgroundWhilePlaying { get; set; } = true;

    /// <summary>Background work waiting to start waits while an export, Render Preview or the queue runs.</summary>
    public bool PauseBackgroundWhileExporting { get; set; } = true;

    /// <summary>
    /// The graphics card background tools decode on (and proxies are encoded on): -1 Auto (a card other than the
    /// one the player draws on, when there is one), -2 none (the processor), otherwise its DXGI number.
    /// </summary>
    public int BackgroundGpu { get; set; } = -1;

    /// <summary>Threads each background FFmpeg may use; 0 is Auto (FFmpeg's own choice).</summary>
    public int BackgroundToolThreads { get; set; }

    /// <summary>Threads whisper transcribes with; 0 is Auto (whisper's own default, at most 4).</summary>
    public int WhisperThreads { get; set; }

    /// <summary>The order of the Summary tab's cards, by their names, comma-separated; empty for the order they were designed in.</summary>
    public string SummaryCardOrder { get; set; } = "";

    /// <summary>The progress chip at the bottom left shows background work too, not only exports and loads.</summary>
    public bool ShowBackgroundTasks { get; set; } = true;

    /// <summary>How long a finished task stays in the progress flyout, in seconds.</summary>
    public int FinishedTaskSeconds { get; set; } = 5;

    /// <summary>The processors reserved, Auto worked out: two on a machine with eight or more, one with four or more, else none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveReservedCores => ReservedCores >= 0
        ? Math.Min(ReservedCores, Math.Max(Environment.ProcessorCount - 2, 0))
        : Environment.ProcessorCount >= 8 ? 2 : Environment.ProcessorCount >= 4 ? 1 : 0;

    // ----- Dead air -----

    /// <summary>Remove Dead Air: anything quieter than this, in decibels, counts as silence.</summary>
    public double DeadAirThresholdDb { get; set; } = -35;

    /// <summary>Remove Dead Air: a silence has to last this long, in seconds, to be cut.</summary>
    public double DeadAirMinSeconds { get; set; } = 1;

    // ----- Diagnostics: crash reports (Settings > General > Diagnostics; see CrashReporter) -----

    /// <summary>
    /// Writes a crash report (.hpcrash, with no names, paths or words from the media in it) to the crashes folder when
    /// HandPeg falls over and runs the small sentinel process that catches native crashes. When a
    /// <see cref="CrashReportEndpoint"/> is set, each report is also sent once to it a little after a start
    /// (CrashReports.UploadPendingAsync). Off: nothing is written or sent and no sentinel runs.
    /// </summary>
    public bool SendCrashReports { get; set; } = true;

    /// <summary>
    /// Where crash reports are sent (HTTPS; http only to this PC, for tests). Set in appsettings.json only, not in Settings.
    /// Empty by default (the user's call, v4.1.0): HandPeg has no crash report service yet, so reports stay on the PC.
    /// </summary>
    public string CrashReportEndpoint { get; set; } = DefaultCrashReportEndpoint;

    public const string DefaultCrashReportEndpoint = "";

    // ----- First run, appearance and mode -----

    /// <summary>False until the first-run window has been completed once.</summary>
    public bool FirstRunComplete { get; set; }

    /// <summary>One of ThemeManager.Themes: follow Windows, or always dark, or always light.</summary>
    public string Theme { get; set; } = ThemeManager.FollowSystem;

    /// <summary>
    /// What HandPeg's interface is called where a name is wanted (crash reports): the editor. Before 4.2 it was one of two
    /// modes; Encoder Mode left for HandPeg Encoder.
    /// </summary>
    public const string EditorMode = "Editor Mode";

    /// <summary>
    /// Only read, from settings saved before 4.2: the mode that was in use ("Encoder Mode" or "Editor Mode"). <see cref="Load"/>
    /// brings such settings over once and drops it, so it is never written again.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UiMode { get; set; }

    /// <summary>Only read, from settings saved before 4.2: each mode's interface settings, by mode name. Dropped by <see cref="Load"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ModeProfile>? ModeProfiles { get; set; }

    /// <summary>Takes up a profile kept from before 4.2: its interface settings replace these.</summary>
    private void TakeProfile(ModeProfile profile)
    {
        // A setting since retired (Play Only Cut Segments, the remembered Live Preview) is left behind with the profile.
        foreach (var property in ProfileProperties)
            typeof(AppSettings).GetProperty(property.Name)?.SetValue(this, property.GetValue(profile));
    }

    /// <summary>The style presets the drag-and-drop overlay offers, by file name, in the order they are shown. Empty for the most recent ones.</summary>
    public List<string> SplashStylePresets { get; set; } = [];


    // What a freshly started window begins with. Unlike the settings above these are only starting points:
    // the boxes they stand for are on the main window and can be changed there for the session. (4.2.0's "Play only cut
    // segments" went with the cut list in 4.3C; a settings file that has it is read without it.)
    public bool StartWithSnapToIFrames { get; set; } = true;
    public bool StartWithChapterMarkers { get; set; } = true;
    public bool StartWithChaptersAtCuts { get; set; }

    /// <summary>
    /// How tall the master timeline is, 1 (a slim bar) to 50: the slider beside the volume. 0 in settings from
    /// before the slider, which take it from the old five-step <see cref="TimelineHeight"/>.
    /// </summary>
    public int MasterTimelineHeight { get; set; }

    /// <summary>How tall each track's time bar is on the Layers tab, 1 to 50.</summary>
    public int LayerHeight { get; set; } = 25;

    /// <summary>How tall each sound's row is on the Audio tab, 1 to 50: taller rows show more of the waveform.</summary>
    public int AudioHeight { get; set; } = 25;

    /// <summary>The panes and what the window starts with were given their defaults once, for settings from before there were any.</summary>
    public int ViewDefaultsVersion { get; set; }

    public const string ResetToStyle = "Active Style Preset";
    public const string ResetToSystem = "System Default";

    public static IReadOnlyList<string> ResetTargets { get; } = [ResetToStyle, ResetToSystem];

    /// <summary>What a double-click on a slider or a number box puts it back to: the value of the style preset in use, or the built-in default.</summary>
    public string DoubleClickResetsTo { get; set; } = ResetToStyle;

    /// <summary>
    /// The settings tabs hidden (Tabs and Panes, Pass 4.1B), by pane id ("Tab:Video"); null for the defaults
    /// (<see cref="DefaultHiddenTabs"/>). Replaced whole when it changes, never changed in place (copies share lists).
    /// </summary>
    public List<string>? HiddenTabs { get; set; }

    /// <summary>
    /// What is hidden until asked: the Export tab stands for the Video tab (its contents, the chapters among them, are in
    /// it), and the Audio tab, its tracks, stays after Layers until the unified timeline (4.1.1; the Export tab's Audio
    /// Export group has the codec and the mix either way).
    /// </summary>
    public static IReadOnlyList<string> DefaultHiddenTabs { get; } = ["Tab:Video"];

    /// <summary>The master timeline pane (Tabs and Panes).</summary>
    public bool ShowMasterTimeline { get; set; } = true;

    /// <summary>
    /// The status bar at the bottom of the window (HandPeg starts without it, 4.1.1: <see cref="ApplyEditorDefaults"/>). The
    /// title bar carries the status and the progress either way (Pass 4.1B, always since 4.1.1).
    /// </summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>The Keyframes pane beside the player: the selected clip's keyframes on a timeline of their own.</summary>
    public bool ShowKeyframesPane { get; set; }

    /// <summary>The Effects pane: the selected clip's effect stack, or the Sequence Output's. Tabbed with Keyframes to start with.</summary>
    public bool ShowEffectsPane { get; set; } = true;

    /// <summary>Draft Mode: while the playhead is dragged or wound back, Live Preview leaves out the effects that cost it most (red). Exports always have them.</summary>
    public bool DraftScrubSkipHeavyEffects { get; set; } = true;

    /// <summary>The media browser pane in the side column: a folder's videos, or the project's files, to drag onto the timeline. Shown to start with.</summary>
    public bool ShowMediaBrowser { get; set; }

    /// <summary>The controls the playback bar shows, by name (see PlaybackControlSet); null for the defaults.</summary>
    public string? PlaybackControls { get; set; }

    /// <summary>Where every pane is, in the main window and the side window (Docking.DockLayoutState as JSON); null for where they start.</summary>
    public string? DockLayout { get; set; }

    /// <summary>
    /// The built-in layout chosen in the first-run window (its name, as the Layout button lists it), not laid out yet: the
    /// main window lays itself out so on its next dock build, writes that into <see cref="DockLayout"/> and clears this.
    /// The layouts are made of the main window's own panes and tabs, so the first-run window cannot write them itself.
    /// </summary>
    public string? FirstRunLayout { get; set; }

    /// <summary>
    /// The layouts of the user's own (the Layout button: Save Current Layout), at most <see cref="SavedLayoutSlots"/>,
    /// each in its place. Replaced as a whole when one is saved or deleted, so a copy of the settings never shares it.
    /// </summary>
    public List<SavedDockLayout> SavedLayouts { get; set; } = [];

    /// <summary>How many layouts of the user's own are kept (the brief of Pass 2E).</summary>
    public const int SavedLayoutSlots = 3;

    /// <summary>Whether clips on the timeline show a diamond at each of their keyframes.</summary>
    public bool ShowClipKeyframes { get; set; } = true;

    /// <summary>
    /// With keyframes enabled, what a change by hand keys: only the property that changed (true; a resize about the anchor
    /// moves the corner too, so that is keyed with it), or every motion property at once (false, Key All, as before lanes).
    /// </summary>
    public bool KeyChangedOnly { get; set; }

    /// <summary>The Keyframes pane draws the selected row's value over time under it.</summary>
    public bool ShowKeyframeGraph { get; set; } = true;

    /// <summary>A ripple edit (Ctrl-drag of a clip's edge, Shift+Delete) moves the later clips of every track, not only its own.</summary>
    public bool RippleAllTracks { get; set; } = true;

    /// <summary>A track's Solo button plays it alone (the others' solo goes off); off, solo is a set of tracks and Alt-click is alone.</summary>
    public bool SoloIsExclusive { get; set; }

    /// <summary>Clips with sound show a gain line and fade handles on their blocks, to be dragged.</summary>
    public bool ShowClipGainLines { get; set; } = true;

    /// <summary>How long a crossfade made from a cut's right-click menu is, in seconds.</summary>
    public double DefaultTransitionSeconds { get; set; } = 1;

    /// <summary>The layout pane draws an animated layer's path, with a dot at each of its place keyframes to drag.</summary>
    public bool ShowMotionPath { get; set; } = true;

    /// <summary>A layer dragged in the layout pane snaps to the frame's middle and the safe margins (Alt held: no snapping).</summary>
    public bool SnapLayoutGuides { get; set; } = true;

    /// <summary>
    /// Live Preview: the layers are moved, resized and turned on the player itself, as in the layout pane;
    /// only the selected layer's outline and handles are drawn there, and none while playing (4.1.1-B).
    /// </summary>
    public bool EditLayersOnPlayer { get; set; } = true;

    /// <summary>How tall the master timeline starts out, 1 to 50.</summary>
    public const int DefaultMasterTimelineHeight = 17;

    /// <summary>Reset Panes Layout: every pane back where it starts, at the size it starts with.</summary>
    public void ResetPaneLayout()
    {
        DockLayout = null;
        MasterTimelineHeight = DefaultMasterTimelineHeight;
    }

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

    /// <summary>The export sample rate a new session starts with, in Hz; 0 is Auto (4.1.1).</summary>
    public int DefaultAudioSampleRate { get; set; }

    /// <summary>The export channels a new session starts with (<see cref="Models.AudioChannelChoice"/>; 4.1.1).</summary>
    public string DefaultAudioChannels { get; set; } = Models.AudioChannelChoice.Stereo;

    // The rest of the default encoding settings (Settings, Export & Hardware), with the three above: what the Video and
    // Audio tabs start with. A blank one leaves HandPeg's own default. An encoding preset applied afterwards always sets
    // its own; a style preset does too, unless DefaultsOverrideStyles is on.
    public string DefaultContainer { get; set; } = "";
    public bool DefaultWebOptimized { get; set; } = true;
    public string DefaultEncoderPreset { get; set; } = "";
    public string DefaultRateControl { get; set; } = "";

    /// <summary>The constant-quality value (CRF, CQ, CQP...); null for the built-in one.</summary>
    public int? DefaultQuality { get; set; }

    public string DefaultTargetBitrate { get; set; } = "";
    public string DefaultFramerate { get; set; } = "";
    public string DefaultCustomFramerate { get; set; } = "";
    public bool DefaultTenBit { get; set; }

    /// <summary>The output size asked for; either blank keeps the source's (or the frame's) own.</summary>
    public string DefaultOutputWidth { get; set; } = "";

    public string DefaultOutputHeight { get; set; } = "";

    /// <summary>Whether the audio tracks are mixed into one: "" as HandPeg starts (mixed), "on" or "off".</summary>
    public string DefaultMergeAudio { get; set; } = "";

    public bool DefaultNormalizeAudio { get; set; }
    public bool DefaultHardwareDecoding { get; set; } = true;

    /// <summary>The default encoding settings are put back after a style preset is applied, where the style set something else (a vertical frame, say).</summary>
    public bool DefaultsOverrideStyles { get; set; }

    /// <summary>Asks whether to save the project when HandPeg is closed with changes that have not been saved.</summary>
    public bool PromptToSaveOnExit { get; set; } = true;

    /// <summary>The Project Manager shows a picture of each project's main video, scrubbed by the pointer (4.1.1-B).</summary>
    public bool ShowProjectPreviews { get; set; } = true;

    // ----- The Social Sharing Squisher, on the startup dialog -----

    /// <summary>Whether the startup dialog has its Squisher section.</summary>
    public bool ShowSquisher { get; set; } = true;

    /// <summary>The sizes a video can be squeezed under, each a target on the startup dialog.</summary>
    public List<SquishPreset> SquisherPresets { get; set; } = SquishPreset.Defaults();

    /// <summary>How many of the presets, from the top of the list, the startup dialog shows.</summary>
    public int SquisherPresetCount { get; set; } = 4;

    // ----- The window, when HandPeg starts -----
    public const string DefaultWindowSize = "Default Size", RememberWindowSize = "Remember Last Size", MaximizedWindow = "Launch Maximized";

    public static IReadOnlyList<string> StartupWindowSizes { get; } = [DefaultWindowSize, RememberWindowSize, MaximizedWindow];

    /// <summary>How large the window is when HandPeg starts: as designed, as it was left, or maximized.</summary>
    public string StartupWindowSize { get; set; } = DefaultWindowSize;

    // The window as it was when HandPeg was last closed (its size while not maximized, and whether it was).
    public double LastWindowWidth { get; set; }
    public double LastWindowHeight { get; set; }
    public bool LastWindowMaximized { get; set; }

    /// <summary>How thick scrollbars are, everywhere in HandPeg, in pixels: 4 (a hairline) to 16 (as Windows draws them).</summary>
    public double ScrollbarThickness { get; set; } = 10;

    /// <summary>Megabytes kept in hand below a preset's size when the bitrate is worked out.</summary>
    public double SquisherOffsetMb { get; set; } = 1.5;

    /// <summary>Use <see cref="SquisherEncoderPreset"/> in place of the encoder's most careful preset.</summary>
    public bool SquisherManualPreset { get; set; }

    /// <summary>The encoder preset used while the override is on: "medium", "p5", "balanced"...</summary>
    public string SquisherEncoderPreset { get; set; } = "medium";

    /// <summary>Writes the open project to "Name.autosave.hproj" every so often, in the background.</summary>
    public bool AutosaveEnabled { get; set; }

    /// <summary>How many minutes pass between autosaves.</summary>
    public int AutosaveMinutes { get; set; } = 5;

    /// <summary>Opens the Canvas by itself as soon as there is a layout to edit.</summary>
    public bool AutoOpenLayoutPane { get; set; }

    /// <summary>
    /// Sets the interface as HandPeg starts it: the tools for cutting, captioning and laying out a video. Used by the
    /// first-run window, and once for settings last saved in Encoder Mode with no Editor Mode of their own (<see cref="Load"/>).
    /// </summary>
    public void ApplyEditorDefaults()
    {
        TimelineHeight = 5;
        ResetPaneLayout();
        PresetBarLocation = PresetBarInSummary;

        ShowAdvancedFiltersTab = true;
        ShowTimelineWaveform = true;
        ShowAdvancedPlayback = true;
        StartWithSnapToIFrames = true;
        SnapTimelineToIFrames = true;
        AutoToggleTimelineSnap = true;
        GenerateHoverPreviews = true;
        SaveTargetSizeInPresets = true;
        ShowAutoCaptions = true;
        StartWithChapterMarkers = true;
        StartWithChaptersAtCuts = true;
        AutoOpenLayoutPane = true;
        ShowTimelineThumbnails = true;
        StartWithFrameEngine = true;
        (ShowKeyframesPane, ShowClipKeyframes) = (true, true);
        ShowMediaBrowser = true;
        // No status bar (4.1.1): the title bar carries the status and the progress.
        (HiddenTabs, ShowMasterTimeline, ShowStatusBar) = (null, true, false);

        ThumbnailIntervalSeconds = 1;
        HoverPreviewScale = 3;
        PreviewDurationSeconds = 8;
        PreviewResolutionPercent = 50;
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

    public const string SponsorBlockCut = "cut", SponsorBlockChapters = "chapters", SponsorBlockIgnore = "ignore";

    /// <summary>
    /// What a download does with the ticked SponsorBlock categories (Pass 4.2B-2): cuts them out (--sponsorblock-remove,
    /// as before), keeps them as named chapters of the file (--sponsorblock-mark; Copy YouTube Timestamps lists them), or
    /// leaves SponsorBlock alone. An unknown value is taken as cut.
    /// </summary>
    public string SponsorBlockMode { get; set; } = SponsorBlockCut;

    /// <summary>The choices for <see cref="SponsorBlockMode"/>, with the words shown for them.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SponsorBlockModes { get; } =
    [
        new(SponsorBlockCut, "Cut Out SponsorBlock Segments"),
        new(SponsorBlockChapters, "Keep as Named Chapters"),
        new(SponsorBlockIgnore, "Ignore"),
    ];

    /// <summary>What a download asks yt-dlp for: the categories (none when ignored or none ticked), and whether they are marked rather than cut.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (string Categories, bool Mark) SponsorBlockRequest =>
        SponsorBlockMode == SponsorBlockIgnore ? ("", false) : (SponsorBlockCategories, SponsorBlockMode == SponsorBlockChapters);

    /// <summary>The Export tab shows the Easy Social Export cards instead of the Video and Audio sections (Pass 4.2B-2).</summary>
    public bool UseEasySocialExport { get; set; }

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
        copy.SquisherPresets = SquisherPresets.Select(p => p.Clone()).ToList();
        copy.SmartRules = SmartRules.Select(r => new SmartRule { Type = r.Type, Path = r.Path, Preset = r.Preset }).ToList();
        copy.SplashStylePresets = [.. SplashStylePresets];

        copy.SavedLayouts = [.. SavedLayouts];
        copy.HiddenTabs = HiddenTabs is null ? null : [.. HiddenTabs];
        copy.DefaultCaptionStyle = DefaultCaptionStyle?.Clone();
        return copy;
    }

    /// <summary>Writes the settings to disk and puts them into effect.</summary>
    public void SaveAsCurrent()
    {
        YtDlpPath = Clean(YtDlpPath);
        FfmpegPath = Clean(FfmpegPath);
        FfprobePath = Clean(FfprobePath);
        WhisperPath = Clean(WhisperPath);
        YtDlpCookiesFilePath = Clean(YtDlpCookiesFilePath);
        YtDlpReleaseUrl = YtDlpReleaseUrl.Trim();
        FfmpegReleaseUrl = FfmpegReleaseUrl.Trim();

        WhisperReleaseUrl = WhisperReleaseUrl.Trim();
        SmartRules = SmartRules.Where(r => !string.IsNullOrWhiteSpace(r.Path) && !string.IsNullOrWhiteSpace(r.Preset)).ToList();

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

                // A file that spells these out as null, as one written by hand or by an older version might.
                settings.SmartRules ??= [];
                settings.ModeProfiles ??= [];
                settings.SavedLayouts ??= [];
                foreach (var profile in settings.ModeProfiles.Values)
                    profile.SavedLayouts ??= [];

                foreach (var rule in settings.SmartRules)
                    rule.Normalize();

                // Settings saved before the option existed: on where the mode would have switched it on.
                settings.ShowTimelineThumbnails ??= settings.UiMode is null or EditorMode;

                // Settings from before the panes and the timeline's slider: each mode gets what it would have started with.
                if (settings.ViewDefaultsVersion < 1)
                {
                    settings.ViewDefaultsVersion = 1;
                    var editor = settings.UiMode == EditorMode;
                    (settings.ShowKeyframesPane, settings.ShowClipKeyframes) = (editor, true);
                    settings.MasterTimelineHeight = Math.Clamp(4 * settings.TimelineHeight - 3, 1, 50);
                    foreach (var (mode, profile) in settings.ModeProfiles)
                    {
                        (profile.ShowKeyframesPane, profile.ShowClipKeyframes) = (mode == EditorMode, true);
                        profile.MasterTimelineHeight = Math.Clamp(4 * profile.TimelineHeight - 3, 1, 50);
                    }
                }

                // Settings from when Encoder Mode started without the color grading on its Filters tab: it is
                // switched on there once. It can still be hidden again, under Interface.
                if (settings.ViewDefaultsVersion < 2)
                {
                    settings.ViewDefaultsVersion = 2;
                    if (settings.UiMode != EditorMode)
                        settings.ShowAdvancedFiltersTab = true;
                }

                // Version 3 opened the Source pane, which Editor Mode no longer has (Add Media in its header took its place).
                if (settings.ViewDefaultsVersion < 3)
                    settings.ViewDefaultsVersion = 3;

                // The Squisher began with one preset; the others came after. Settings saved in between hold just
                // that one, untouched: they get the full set. A list that was edited is left as it is.
                if (settings.ViewDefaultsVersion < 4)
                {
                    settings.ViewDefaultsVersion = 4;
                    if (settings.SquisherPresets is not { Count: > 0 }
                        || settings.SquisherPresets is [{ Name: "Discord (Non-Nitro)", TargetSizeMb: 20 or 25, Icon: "squish" or null }])
                    {
                        settings.SquisherPresets = SquishPreset.Defaults();
                    }
                }

                // Editor Mode as 4.1.1 starts it, once, for settings saved before: no status bar at the bottom (the title
                // bar carries the status and the progress) and the Audio tab shown, right after Layers. Both stay the
                // user's to change under Views, Tabs and Panes. The mode in use holds its values here; the other, in its profile.
                if (!settings.Migrated411Defaults)
                {
                    settings.Migrated411Defaults = true;
                    if (settings.UiMode == EditorMode)
                        (settings.ShowStatusBar, settings.HiddenTabs, settings.DockLayout) = (false, WithAudioTab(settings.HiddenTabs), AudioAfterLayers(settings.DockLayout));
                    if (settings.ModeProfiles.TryGetValue(EditorMode, out var editorProfile))
                        (editorProfile.ShowStatusBar, editorProfile.HiddenTabs, editorProfile.DockLayout) = (false, WithAudioTab(editorProfile.HiddenTabs), AudioAfterLayers(editorProfile.DockLayout));
                }

                // HandPeg is the editor alone since 4.2 (Encoder Mode left for HandPeg Encoder). Settings last saved in Encoder
                // Mode take up Editor Mode's interface settings, its arrangement and layouts among them, once: from its
                // profile, or as Editor Mode starts if it was never used; so no one loses an Editor workspace. The two
                // modes' leftovers are then dropped and never written again.
                if (settings.UiMode is { } lastMode && lastMode != EditorMode)
                {
                    if (settings.ModeProfiles.GetValueOrDefault(EditorMode) is { } editorProfile)
                        settings.TakeProfile(editorProfile);
                    else
                    {
                        settings.SavedLayouts = [];
                        settings.ApplyEditorDefaults();
                    }
                }

                (settings.UiMode, settings.ModeProfiles) = (null, null);
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable settings file must not stop the app from starting; defaults apply.
        }

        // No settings yet: already as 4.1.1 starts, so there is nothing to bring over.
        return new AppSettings { ShowTimelineThumbnails = false, Migrated411Defaults = true };
    }

    private const string AudioTab = "Tab:Audio", LayersTab = "Tab:Layers";

    /// <summary>The tabs hidden, without the Audio tab (a new list: the profiles share lists); null stays the defaults.</summary>
    private static List<string>? WithAudioTab(List<string>? hidden) => hidden?.Where(t => t != AudioTab).ToList();

    /// <summary>
    /// A saved arrangement with the Audio tab right after the Layers tab in Layers' group (4.1.1): moved there from its
    /// own place unless it is alone in a group (it is shown where it is), or put there when the arrangement lacks it.
    /// Unchanged when there is no Layers tab or the arrangement cannot be read.
    /// </summary>
    private static string? AudioAfterLayers(string? saved)
    {
        if (Docking.DockLayoutState.Read(saved) is not { } state)
            return saved;

        var roots = new[] { state.Main, state.Side, state.SideClosed }.OfType<Docking.DockNode>().ToList();
        if (roots.Select(r => r.GroupOf(LayersTab)).FirstOrDefault(g => g is not null) is not { } layers)
            return saved;

        if (roots.Select(r => r.GroupOf(AudioTab)).FirstOrDefault(g => g is not null) is { } audio)
        {
            if (!ReferenceEquals(audio, layers) && audio.Panes.Count == 1)
                return saved;

            audio.Panes.Remove(AudioTab);
            if (audio.Active == AudioTab)
                audio.Active = audio.Panes.FirstOrDefault();
        }

        layers.Panes.Insert(layers.Panes.IndexOf(LayersTab) + 1, AudioTab);
        return JsonSerializer.Serialize(state);
    }

    // Paths are often pasted with the quotes Explorer's "Copy as path" adds.
    private static string Clean(string path) => path.Trim().Trim('"');
}
