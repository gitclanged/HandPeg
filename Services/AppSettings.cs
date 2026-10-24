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
    public bool ShowIFrames { get; set; }
    public bool SnapTimelineToIFrames { get; set; }
    public bool AutoToggleTimelineSnap { get; set; }
    public bool SaveTargetSizeInPresets { get; set; }
    public bool LivePreview { get; set; }
    public bool StartWithPlayOnlySegments { get; set; }
    public bool StartWithSnapToIFrames { get; set; }
    public bool StartWithChapterMarkers { get; set; }
    public bool StartWithChaptersAtCuts { get; set; }
    public bool StartWithFrameEngine { get; set; }
    public int MasterTimelineHeight { get; set; }
    public bool ShowCutSegmentsPane { get; set; }
    public bool ShowKeyframesPane { get; set; }
    public bool ShowEffectsPane { get; set; } = true;
    public bool ShowMediaBrowser { get; set; }
    public string? PlaybackControls { get; set; }
    public string? DockLayout { get; set; }
    public List<SavedDockLayout> SavedLayouts { get; set; } = [];
    public bool ShowClipKeyframes { get; set; }
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
    /// A clip dropped on a track that has room for it at that moment joins it (Editor Mode). Off (the default), every
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
    /// it then starts out on in Editor Mode and off in Encoder Mode.
    /// </summary>
    public bool? ShowTimelineThumbnails { get; set; }

    // ----- Launch -----

    /// <summary>How many style presets the drag-and-drop overlay offers in Editor Mode, from the top of <see cref="SplashStylePresets"/>: 0 to 8.</summary>
    public int SplashPresetCount { get; set; } = 3;

    /// <summary>How many encoding presets the drag-and-drop overlay offers in Encoder Mode, from the top of <see cref="OverlayEncodingPresets"/>: 0 to 8.</summary>
    public int OverlayEncodingPresetCount { get; set; } = 3;

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

    /// <summary>The order of the Summary tab's cards (Editor Mode), by their names, comma-separated; empty for the order they were designed in.</summary>
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
            // A mode never used has no layouts of its own yet: those of the mode left stay with it.
            SavedLayouts = [];
            ApplyMode(mode);
        }
    }

    /// <summary>The style presets the drag-and-drop overlay offers, by file name, in the order they are shown. Empty for the most recent ones.</summary>
    public List<string> SplashStylePresets { get; set; } = [];

    /// <summary>The encoding presets the drag-and-drop overlay offers in Encoder Mode, by name, in the order they are shown. Empty for the first ones in the preset list.</summary>
    public List<string> OverlayEncodingPresets { get; set; } = [];

    // What a freshly started window begins with. Unlike the settings above these are only starting points:
    // the boxes they stand for are on the main window and can be changed there for the session.
    public bool StartWithPlayOnlySegments { get; set; }
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

    /// <summary>The panes and what each mode starts with were given their defaults once, for settings from before there were any.</summary>
    public int ViewDefaultsVersion { get; set; }

    public const string ResetToStyle = "Active Style Preset";
    public const string ResetToSystem = "System Default";

    public static IReadOnlyList<string> ResetTargets { get; } = [ResetToStyle, ResetToSystem];

    /// <summary>What a double-click on a slider or a number box puts it back to: the value of the style preset in use, or the built-in default.</summary>
    public string DoubleClickResetsTo { get; set; } = ResetToStyle;

    /// <summary>The Cut Segments pane beside the player. Encoder Mode starts with it; Editor Mode has the Keyframes pane there instead.</summary>
    public bool ShowCutSegmentsPane { get; set; } = true;

    /// <summary>The Keyframes pane beside the player: the selected clip's keyframes on a timeline of their own.</summary>
    public bool ShowKeyframesPane { get; set; }

    /// <summary>The Effects pane (Editor Mode only): the selected clip's effect stack, or the Sequence Output's. Tabbed with Keyframes to start with.</summary>
    public bool ShowEffectsPane { get; set; } = true;

    /// <summary>Draft Mode: while the playhead is dragged or wound back, Live Preview leaves out the effects that cost it most (red). Exports always have them.</summary>
    public bool DraftScrubSkipHeavyEffects { get; set; } = true;

    /// <summary>The media browser pane in the side column, in Editor Mode: a folder's videos, or the project's files, to drag onto the timeline. Editor Mode starts with it.</summary>
    public bool ShowMediaBrowser { get; set; }

    /// <summary>The controls the playback bar shows, by name (see PlaybackControlSet); null for the mode's defaults.</summary>
    public string? PlaybackControls { get; set; }

    /// <summary>Where every pane is, in the main window and the side window (Docking.DockLayoutState as JSON); null for where the mode starts them.</summary>
    public string? DockLayout { get; set; }

    /// <summary>
    /// The mode's layouts of the user's own (the Layout button: Save Current Layout), at most <see cref="SavedLayoutSlots"/>,
    /// each in its place. Replaced as a whole when one is saved or deleted, so a copy of the settings never shares it.
    /// </summary>
    public List<SavedDockLayout> SavedLayouts { get; set; } = [];

    /// <summary>How many layouts of the user's own each mode keeps (the brief of Pass 2E).</summary>
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

    /// <summary>A ripple edit (Shift-drag of a clip's edge, Shift+Delete) moves the later clips of every track, not only its own.</summary>
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

    /// <summary>How tall the master timeline starts out in a mode, 1 to 50.</summary>
    public static int DefaultMasterTimelineHeight(string mode) => mode == EditorMode ? 17 : 1;

    /// <summary>Reset Panes Layout: every pane back where the mode in use starts with it, at the size it starts with.</summary>
    public void ResetPaneLayout()
    {
        DockLayout = null;
        MasterTimelineHeight = DefaultMasterTimelineHeight(UiMode);
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

    /// <summary>Whether the audio tracks are mixed into one: "" as each mode starts (Editor Mode mixes), "on" or "off".</summary>
    public string DefaultMergeAudio { get; set; } = "";

    public bool DefaultNormalizeAudio { get; set; }
    public bool DefaultHardwareDecoding { get; set; } = true;

    /// <summary>The default encoding settings are put back after a style preset is applied, where the style set something else (a vertical frame, say).</summary>
    public bool DefaultsOverrideStyles { get; set; }

    /// <summary>Asks whether to save the project when HandPeg is closed with changes that have not been saved.</summary>
    public bool PromptToSaveOnExit { get; set; } = true;

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
        ResetPaneLayout();
        PresetBarLocation = editor ? PresetBarInSummary : PresetBarAtTop;

        // Both modes have the whole Filters tab: a batch of conversions is graded as an edit is.
        ShowAdvancedFiltersTab = true;
        ShowTimelineWaveform = editor;
        ShowAdvancedPlayback = editor;
        StartWithPlayOnlySegments = editor;
        StartWithSnapToIFrames = true;
        SnapTimelineToIFrames = editor;
        AutoToggleTimelineSnap = editor;
        GenerateHoverPreviews = editor;
        SaveTargetSizeInPresets = editor;
        ShowAutoCaptions = editor;
        StartWithChapterMarkers = true;
        StartWithChaptersAtCuts = editor;
        AutoOpenLayoutPane = editor;
        ShowTimelineThumbnails = editor;
        StartWithFrameEngine = editor;
        (ShowCutSegmentsPane, ShowKeyframesPane, ShowClipKeyframes) = (!editor, editor, true);
        ShowMediaBrowser = editor;
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
        copy.SquisherPresets = SquisherPresets.Select(p => p.Clone()).ToList();
        copy.SmartRules = SmartRules.Select(r => new SmartRule { Type = r.Type, Path = r.Path, Preset = r.Preset }).ToList();
        copy.ModeProfiles = new(ModeProfiles);
        copy.SplashStylePresets = [.. SplashStylePresets];
        copy.OverlayEncodingPresets = [.. OverlayEncodingPresets];
        copy.SavedLayouts = [.. SavedLayouts];
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

                // A file that spells these out as null, as one written by hand or by an older version might.
                settings.SmartRules ??= [];
                settings.ModeProfiles ??= [];
                settings.SavedLayouts ??= [];
                foreach (var profile in settings.ModeProfiles.Values)
                    profile.SavedLayouts ??= [];

                foreach (var rule in settings.SmartRules)
                    rule.Normalize();

                // Settings saved before the option existed: on where the mode would have switched it on.
                settings.ShowTimelineThumbnails ??= settings.UiMode == EditorMode;

                // Settings from before the panes and the timeline's slider: each mode gets what it would have started with.
                if (settings.ViewDefaultsVersion < 1)
                {
                    settings.ViewDefaultsVersion = 1;
                    var editor = settings.UiMode == EditorMode;
                    (settings.ShowCutSegmentsPane, settings.ShowKeyframesPane, settings.ShowClipKeyframes) = (!editor, editor, true);
                    settings.MasterTimelineHeight = Math.Clamp(4 * settings.TimelineHeight - 3, 1, 50);
                    foreach (var (mode, profile) in settings.ModeProfiles)
                    {
                        (profile.ShowCutSegmentsPane, profile.ShowKeyframesPane, profile.ShowClipKeyframes) = (mode != EditorMode, mode == EditorMode, true);
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
                    if (settings.ModeProfiles.TryGetValue(EncoderMode, out var encoderProfile))
                        encoderProfile.ShowAdvancedFiltersTab = true;
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
