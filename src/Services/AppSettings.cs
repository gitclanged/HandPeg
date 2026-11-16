using System.IO;
using System.Text.Json;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Smart Rule mapping matching videos to a preset: by folder (D:\Shadowplay\), path keyword (Overwatch) or extension (*.mkv).
/// </summary>
public sealed class SmartRule
{
    public const string Folder = "Folder";
    public const string Keyword = "Filename Keyword";
    public const string Extension = "Extension";

    public static IReadOnlyList<string> Types { get; } = [Folder, Keyword, Extension];

    /// <summary>One of <see cref="Types"/>; empty in rules that predate keyword rules (<see cref="Normalize"/>).</summary>
    public string Type { get; set; } = "";

    /// <summary>Match value: folder path, keyword, or extension as *.ext.</summary>
    public string Path { get; set; } = "";

    public string Preset { get; set; } = "";

    /// <summary>Infers a legacy rule's type: *.ext means extension, anything else folder.</summary>
    public void Normalize()
    {
        (Type, Path, Preset) = (Type ?? "", Path ?? "", Preset ?? "");
        if (!Types.Contains(Type))
            Type = Path.TrimStart().StartsWith("*.", StringComparison.Ordinal) ? Extension : Folder;
    }
}

/// <summary>Saved custom layout (Save Current Layout): name, slot 1-3, and Docking.DockLayoutState JSON.</summary>
public sealed class SavedDockLayout
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public string Layout { get; set; } = "";
}

/// <summary>
/// User overrides stored in appsettings.json in the settings folder (see <see cref="AppPaths"/>). A blank value means "use the default".
/// </summary>
public sealed partial class AppSettings
{
    public const string DefaultYtDlpReleaseUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    public const string DefaultFfmpegReleaseUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";

    /// <summary>HandPeg release feed read by the Velopack updater.</summary>
    public const string HandPegRepositoryUrl = "https://github.com/gitclanged/HandPeg";

    // Indented JSON with default naming, via source-generated metadata (AppSettingsJsonContext).
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, TypeInfoResolver = AppSettingsJsonContext.Default };

    /// <summary>
    /// whisper.cpp release list (not /latest): numbered releases carry no binaries, so the newest one with a Windows build is picked.
    /// </summary>
    public const string DefaultWhisperReleaseUrl = "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=30";

    /// <summary>Speech model download base URL; the model file name is appended.</summary>
    public const string WhisperModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static string FilePath => AppPaths.SettingsFile;

    /// <summary>
    /// One-time Editor Mode view migration marker (no status bar, Audio tab after Layers; see <see cref="Load"/>). True in all newer files.
    /// </summary>
    public bool Migrated411Defaults { get; set; }

    /// <summary>Active settings instance; replaced wholesale when the Settings window saves.</summary>
    public static AppSettings Current { get; private set; } = Load();

    public string YtDlpPath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";

    /// <summary>Custom whisper.cpp executable (e.g. whisper-cli.exe) used instead of the downloaded build.</summary>
    public string WhisperPath { get; set; } = "";

    /// <summary>Background update of HandPeg-installed tools, shortly after startup.</summary>
    public bool AutoUpdateDependencies { get; set; } = true;

    /// <summary>Checks for a HandPeg update at startup and shows the Update button; downloads only on click.</summary>
    public bool CheckAppUpdatesOnStartup { get; set; } = true;

    /// <summary>Downloads updates as soon as found and applies them on exit, without waiting for the Update button.</summary>
    public bool AutoDownloadAppUpdates { get; set; }

    public string YtDlpReleaseUrl { get; set; } = "";
    public string FfmpegReleaseUrl { get; set; } = "";

    /// <summary>
    /// Pins FFmpeg 8.1: newer builds require NVIDIA driver 610+, which GTX 10-series and older never receive, so NVENC fails.
    /// Set by the encoder probe on that failure (<see cref="EncoderProber.NvencNeedsOlderFfmpeg"/>).
    /// </summary>
    public bool UseFfmpegForOlderNvidiaDrivers { get; set; }

    public string WhisperReleaseUrl { get; set; } = "";

    // ----- Pinned dependency builds -----

    /// <summary>
    /// False (default): FFmpeg and whisper.cpp stay on the tested builds (<see cref="DependencyUpdater.PinnedBuilds"/>); true tracks
    /// untested upstream releases (enabled behind a warning). yt-dlp always tracks upstream.
    /// </summary>
    public bool AllowUnpinnedDependencyUpdates { get; set; }

    /// <summary>File name of the speech model auto-captions use, in the models folder.</summary>
    public string WhisperModel { get; set; } = "ggml-base.en.bin";

    // ----- Auto-Captions whisper parameters (defaults match whisper.cpp) -----

    /// <summary>Default spoken language (whisper code: en, es, ... or auto); presets and projects override it.</summary>
    public string WhisperDefaultLanguage { get; set; } = "en";

    /// <summary>Beam search width, 1-16; wider is more accurate and slower.</summary>
    public int WhisperBeamSize { get; set; } = 5;

    /// <summary>Sampling best-of candidate count, 1-16.</summary>
    public int WhisperBestOf { get; set; } = 5;

    /// <summary>Sampling temperature, 0-1 (0 is greedy).</summary>
    public double WhisperTemperature { get; set; }

    /// <summary>Retries a failed segment at a higher temperature.</summary>
    public bool WhisperTemperatureFallback { get; set; } = true;

    /// <summary>Voice activity detection: only speech segments are sent to whisper (requires the VAD model).</summary>
    public bool UseVad { get; set; } = true;

    /// <summary>VAD speech probability threshold, 0-1; higher rejects more.</summary>
    public double VadThreshold { get; set; } = 0.5;

    /// <summary>Minimum speech duration (ms); shorter bursts are treated as noise.</summary>
    public int VadMinSpeechMs { get; set; } = 250;

    /// <summary>Minimum silence (ms) that ends a speech segment.</summary>
    public int VadMinSilenceMs { get; set; } = 100;

    /// <summary>Padding (ms) added to each side of a speech segment to avoid clipping words.</summary>
    public int VadSpeechPadMs { get; set; } = 30;

    /// <summary>
    /// On load, marks the audio track titled like a microphone ("Mic", "Track 2: Microphone") as the Voice track
    /// (<see cref="VoiceTrackScanner"/>) for Auto-Captions and ducking. Saved projects and queued jobs keep their own tracks.
    /// </summary>
    public bool AutoMarkVoiceTrack { get; set; } = true;

    /// <summary>Default caption style (null: built-in); presets and projects override it.</summary>
    public HandPegApp.Models.CaptionStyle? DefaultCaptionStyle { get; set; }

    /// <summary>Preset applied to a newly loaded video when no smart rule matches. Blank for none.</summary>
    public string DefaultPreset { get; set; } = "";

    public List<SmartRule> SmartRules { get; set; } = [];

    /// <summary>
    /// Rewrites Smart Rules and the default preset that reference a renamed built-in Export Preset (ExportPresetCatalog.RenamedBuiltIns),
    /// unless a custom preset still uses the old name (<paramref name="userHas"/>). Called from PresetStore.Load off the UI thread;
    /// persisted on the next save. Returns true when anything changed.
    /// </summary>
    public bool RenamePresetReferences(IReadOnlyDictionary<string, string> renamed, Func<string, bool> userHas)
    {
        string? NewName(string name) => name.Length > 0 && renamed.TryGetValue(name.Trim(), out var now) && !userHas(name.Trim()) ? now : null;
        var changed = false;
        if (NewName(DefaultPreset) is { } preset)
            (DefaultPreset, changed) = (preset, true);
        foreach (var rule in SmartRules ?? [])
        {
            if (NewName(rule.Preset) is { } named)
                (rule.Preset, changed) = (named, true);
        }

        return changed;
    }

    /// <summary>
    /// Maps legacy Game Preset file names in <see cref="SplashStylePresets"/> to current ones (<see cref="LegacyPresetNames"/>) on load;
    /// Export Preset references are never mapped here. Duplicates created by the mapping are dropped, order kept. Returns true on change.
    /// </summary>
    public bool MapLegacyPresetNames()
    {
        if (SplashStylePresets is not { Count: > 0 } list)
            return false;

        var mapped = new List<string>(list.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
        {
            var now = LegacyPresetNames.Map(entry) ?? entry;
            if (now is null || seen.Add(now.Trim()))
                mapped.Add(now!);
        }

        if (mapped.SequenceEqual(list, StringComparer.Ordinal))
            return false;

        SplashStylePresets = mapped;
        return true;
    }

    /// <summary>Saving a preset also stores the current Target File Size.</summary>
    public bool SaveTargetSizeInPresets { get; set; }

    // ----- Timeline -----
    /// <summary>
    /// Separate Playback Controls pane (transport, frame step, speed) instead of the transport folded into the Overview Scrubber row.
    /// Defaults on so installs that skip the first-run window match ApplyEditorDefaults; existing files keep their value.
    /// </summary>
    public bool ShowAdvancedPlayback { get; set; } = true;

    /// <summary>True puts the frame-step and speed controls on their own row above the timeline; false keeps them inline.</summary>
    public bool AdvancedPlaybackAboveTimeline { get; set; }

    /// <summary>True formats times as HH:MM:SS:FF (frames, default); false as HH:MM:SS.mmm.</summary>
    public bool ShowTimesAsFrames { get; set; } = true;

    public bool SnapTimelineToIFrames { get; set; }

    /// <summary>Draws I-frame markers on the timeline ("Show I-frames").</summary>
    public bool ShowIFrames { get; set; } = true;

    /// <summary>Toggling "Show I-frames" also toggles <see cref="SnapTimelineToIFrames"/>.</summary>
    public bool AutoToggleTimelineSnap { get; set; }

    /// <summary>I-frame snap pull while dragging the thumb, 1 (weak) to 5 (strong).</summary>
    public int TimelineMagnetism { get; set; } = 3;

    /// <summary>Builds a hover thumbnail sheet after each load.</summary>
    public bool GenerateHoverPreviews { get; set; }

    /// <summary>Seconds between hover thumbnails; stretched automatically when the video exceeds one sheet.</summary>
    public int ThumbnailIntervalSeconds { get; set; } = 5;

    /// <summary>Hover thumbnail display scale step, 1 (native) to 5 (4x).</summary>
    public int HoverPreviewScale { get; set; } = 1;

    /// <summary>Maps a 1-5 hover size step to a 1x-4x magnification.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double HoverPreviewZoom => 1 + (Math.Clamp(HoverPreviewScale, 1, 5) - 1) * 0.75;

    /// <summary>Number of recent yt-dlp downloads kept in the cache between sessions.</summary>
    public int DownloadCacheSize { get; set; } = 5;

    /// <summary>
    /// Permanent download folder (never cleaned by HandPeg); blank keeps downloads in the cache, capped at <see cref="DownloadCacheSize"/>.
    /// </summary>
    public string DownloadFolder { get; set; } = "";

    /// <summary>
    /// Cookie source for retrying a gated download (age check, sign-in, 403); the first attempt never uses cookies. "auto" tries each
    /// installed browser then the cookies file; a browser name uses only it; "file" uses <see cref="YtDlpCookiesFilePath"/>; "off" never
    /// retries. One of <see cref="YtDlpCookieSources"/>.
    /// </summary>
    public string YtDlpCookieSource { get; set; } = CookiesAuto;

    /// <summary>Netscape-format cookies.txt for <see cref="YtDlpCookieSource"/> "file" or "auto"; blank for none.</summary>
    public string YtDlpCookiesFilePath { get; set; } = "";

    public const string CookiesAuto = "auto", CookiesFile = "file", CookiesOff = "off";

    /// <summary>Allowed <see cref="YtDlpCookieSource"/> values with their Settings labels.</summary>
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

    // ----- Preview caches (PreviewCache) -----
    /// <summary>Persists timeline previews (hover sheet, filmstrips, waveforms, I-frames) across sessions.</summary>
    public bool CacheTimelinePreviews { get; set; } = true;

    /// <summary>Timeline preview cache cap (MB); least recently used entries are evicted.</summary>
    public int TimelineCacheMegabytes { get; set; } = 1024;

    /// <summary>Persists Media Browser frames across sessions.</summary>
    public bool CacheBrowserPreviews { get; set; } = true;

    /// <summary>How large the media browser's cache may grow, in megabytes.</summary>
    public int BrowserCacheMegabytes { get; set; } = 512;

    // ----- Proxies (ProxyCache) -----

    /// <summary>
    /// Median I-frame spacing (s) above which a video counts as long-GOP; such clips offer Optimize Long-GOP Clip for Scrubbing when
    /// preview decode is slow (MainViewModel.OptimizeIsUseful).
    /// </summary>
    public double ProxyHeavyIFrameSeconds { get; set; } = 2;

    public static IReadOnlyList<int> ProxyTiers { get; } = [480, 540, 720];

    /// <summary>Proxy height in pixels: 480, 540 or 720.</summary>
    public int ProxyTier { get; set; } = 540;

    public const string ProxyCodecH264 = "H.264 (short GOP)", ProxyCodecMjpeg = "MJPEG (every frame an I-frame)";

    public static IReadOnlyList<string> ProxyCodecs { get; } = [ProxyCodecH264, ProxyCodecMjpeg];

    /// <summary>Proxy codec: H.264 (GOP 15, no B-frames) or MJPEG (all-intra seeks, ~3x larger).</summary>
    public string ProxyCodec { get; set; } = ProxyCodecH264;

    public const string ProxyEncoderAuto = "Auto", ProxyEncoderProcessor = "Processor";

    public static IReadOnlyList<string> ProxyEncoders { get; } = [ProxyEncoderAuto, "NVENC", "Quick Sync", "AMF", ProxyEncoderProcessor];

    /// <summary>H.264 proxy encoder: Auto (background GPU, then compositor GPU, then CPU) or a named encoder.</summary>
    public string ProxyEncoder { get; set; } = ProxyEncoderAuto;

    /// <summary>Suspends the proxy FFmpeg process during playback and resumes it afterwards.</summary>
    public bool PauseProxiesWhilePlaying { get; set; } = true;

    /// <summary>Proxy cache cap (GB); least recently used proxies are evicted.</summary>
    public double ProxyCacheGigabytes { get; set; } = 20;

    public const string ProxiesInCache = "In the cache folder", ProxiesBesideProject = "Beside the project";

    public static IReadOnlyList<string> ProxyLocations { get; } = [ProxiesInCache, ProxiesBesideProject];

    /// <summary>Proxy location: the cache folder, or Name.proxies beside the project file when saved.</summary>
    public string ProxyLocation { get; set; } = ProxiesInCache;

    /// <summary>The proxies of files that have been deleted are deleted too, at startup.</summary>
    public bool PurgeProxiesOfDeletedFiles { get; set; } = true;

    /// <summary>Deletes the project's proxies when the project closes.</summary>
    public bool ClearProjectProxiesOnExit { get; set; }

    /// <summary>
    /// Always true: proxies exist only on explicit request (Optimize Long-GOP Clip), so the cache is controlled by its size cap and
    /// Clear Proxy Cache rather than an on/off switch.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesProxies => true;

    // ----- Cache clean-up at startup -----

    /// <summary>Purges cached previews of deleted or changed files shortly after startup.</summary>
    public bool PurgeMissingAtStartup { get; set; } = true;

    /// <summary>How long after startup the clean-up runs, in seconds.</summary>
    public int PurgeDelaySeconds { get; set; } = 60;

    /// <summary>Treats files on missing drives as deleted; false keeps their previews until size eviction.</summary>
    public bool MissingDrivesCountAsDeleted { get; set; }

    // ----- Media Browser folder indexing -----

    /// <summary>Seconds a folder must stay open before its videos are pre-indexed; 0 disables.</summary>
    public int BrowserIndexDelaySeconds { get; set; } = 40;

    /// <summary>Maximum videos pre-indexed per folder.</summary>
    public int BrowserIndexMaxFiles { get; set; } = 500;

    /// <summary>Folders are indexed on battery power too.</summary>
    public bool BrowserIndexOnBattery { get; set; }

    /// <summary>Folders on network drives are indexed too.</summary>
    public bool BrowserIndexNetwork { get; set; }

    /// <summary>A notification says when a folder has been indexed.</summary>
    public bool NotifyIndexingDone { get; set; }

    // ----- Downloads used by projects -----

    /// <summary>Cache trimming skips downloads referenced by a project.</summary>
    public bool KeepDownloadsUsedByProjects { get; set; } = true;

    /// <summary>Saving a project that uses files from the download cache offers to copy them into the download folder.</summary>
    public bool AskToCopyDownloadsOnSave { get; set; } = true;

    /// <summary>
    /// Copies referenced downloads on save without prompting; read only while <see cref="AskToCopyDownloadsOnSave"/> is false.
    /// Both false leaves them in the cache.
    /// </summary>
    public bool AlwaysCopyDownloadsOnSave { get; set; }

    public const string DownloadsAsk = "ask", DownloadsCopy = "copy", DownloadsKeep = "keep";

    /// <summary>
    /// Single Settings choice combining the two flags above (one of <see cref="SavedProjectDownloadChoices"/>); not serialized.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SavedProjectDownloads
    {
        get => AskToCopyDownloadsOnSave ? DownloadsAsk : AlwaysCopyDownloadsOnSave ? DownloadsCopy : DownloadsKeep;
        set
        {
            if (value is DownloadsAsk or DownloadsCopy or DownloadsKeep)
                (AskToCopyDownloadsOnSave, AlwaysCopyDownloadsOnSave) = (value == DownloadsAsk, value == DownloadsCopy);
        }
    }

    /// <summary>Allowed <see cref="SavedProjectDownloads"/> values with their Settings labels.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SavedProjectDownloadChoices { get; } =
    [
        new(DownloadsAsk, "Ask each time"),
        new(DownloadsCopy, "Always copy into the download folder"),
        new(DownloadsKeep, "Leave them in the download cache"),
    ];

    // ----- Media Browser -----
    /// <summary>Hover-scrub frames extracted per Media Browser video.</summary>
    public int BrowserPreviewFrames { get; set; } = 15;

    /// <summary>Hover-scrub frame height in pixels.</summary>
    public int BrowserPreviewHeight { get; set; } = 480;

    /// <summary>Dragged clips snap to clip edges, the Sequence end and the playhead.</summary>
    public bool SnapClips { get; set; } = true;

    /// <summary>
    /// True drops a clip onto the target track when it has room; false (default) always creates a new track above the drop row.
    /// Drops never overlap an existing clip.
    /// </summary>
    public bool DroppedClipsJoinTrack { get; set; }

    /// <summary>The folder the media browser last showed.</summary>
    public string BrowserFolder { get; set; } = "";

    /// <summary>Tile size 0-100 (lowest values render as a list; 38 is ~200 px wide).</summary>
    public int BrowserTileSize { get; set; } = 38;

    /// <summary>What the media browser's files are sorted by: Name, Date, Size or Type.</summary>
    public string BrowserSort { get; set; } = "Name";

    public bool BrowserSortDescending { get; set; }

    /// <summary>Lists the open project's files (Project Media) instead of a folder.</summary>
    public bool BrowserShowsProject { get; set; }

    /// <summary>Shows the Clip Library (ClipLibrary) instead of project files or a folder.</summary>
    public bool BrowserShowsLibrary { get; set; }

    /// <summary>Media Browser layout: "Grid", "List" (details table) or "Filmstrip" (preview and sub-clip cutter beside a list).
    /// Empty resolves to Grid, or List when the zoom is in list range.</summary>
    public string BrowserView { get; set; } = "";

    /// <summary>Width (DIP) of the list beside the Filmstrip preview.</summary>
    public double BrowserFilmstripListWidth { get; set; } = 240;

    // ----- Interface -----
    public bool ShowCommandPreviewTab { get; set; }

    /// <summary>Shows the Advanced tab (colour correction, sharpening, live frame preview).</summary>
    public bool ShowAdvancedFiltersTab { get; set; } = true;

    /// <summary>Shows the Export Presets selector in the Export tab's first card (the legacy "Show in Summary Tab" value maps here).</summary>
    public const string PresetBarInExport = "Show in Export Tab";
    public const string PresetBarAtTop = "Show at top of Window";

    public static IReadOnlyList<string> PresetBarLocations { get; } = [PresetBarInExport, PresetBarAtTop];

    /// <summary>Preset bar placement; one of <see cref="PresetBarLocations"/>.</summary>
    public string PresetBarLocation { get; set; } = PresetBarInExport;

    /// <summary>Draws waveforms after each load (active-track mix on the timeline, per-track in the Audio tab).</summary>
    public bool ShowTimelineWaveform { get; set; } = true;

    /// <summary>Timeline height step, 1 (slim bar) to 5 (readable waveform).</summary>
    public int TimelineHeight { get; set; } = 3;

    /// <summary>Audio tab row height step, 1-5.</summary>
    public int AudioTrackHeight { get; set; } = 2;

    /// <summary>
    /// Draws a filmstrip behind the timeline waveform; null until first decided, then defaults on.
    /// </summary>
    public bool? ShowTimelineThumbnails { get; set; }

    // ----- Launch -----

    /// <summary>Number of Game Presets the drop overlay offers from the top of <see cref="SplashStylePresets"/>, 0-8.</summary>
    public int SplashPresetCount { get; set; } = 3;

    /// <summary>Filmstrip frames per video clip block on the timeline, 1-25.</summary>
    public int LayerThumbnailCount { get; set; } = 8;

    /// <summary>Opens every video as a vertical project (tall frame, picture centred).</summary>
    public bool DefaultVerticalVideo { get; set; }

    /// <summary>
    /// Preview hardware decode (Media Foundation on the compositor's D3D11 device). False forces CPU decode for drivers that emit
    /// corrupt frames. Exports are unaffected.
    /// </summary>
    public bool PlayerHardwareAcceleration { get; set; } = true;

    /// <summary>
    /// Export GPU DXGI index (legacy JSON name; read via <see cref="ExportGpuAdapter"/>): adapter that decodes and composites exports
    /// (GpuExport.ExportAdapter). -1 or a missing adapter selects the Compositor GPU.
    /// </summary>
    public int HardwareDecodeAdapter { get; set; } = -1;

    // ----- Background tool performance (WorkScheduler, JobObjects) -----

    /// <summary>Concurrent background jobs (thumbnails, waveforms, I-frame lists, proxies); 0 = Auto, one per six unreserved cores.</summary>
    public int BackgroundJobs { get; set; }

    /// <summary>Cores reserved for UI and playback (background tools avoid the first N); -1 = Auto (2).</summary>
    public int ReservedCores { get; set; } = -1;

    public const string PriorityBelowNormal = "Below Normal", PriorityIdle = "Idle";

    public static IReadOnlyList<string> BackgroundPriorities { get; } = [PriorityBelowNormal, PriorityIdle];

    /// <summary>Background process priority: BelowNormal or Idle.</summary>
    public string BackgroundPriority { get; set; } = PriorityBelowNormal;

    /// <summary>Runs background tools under EcoQoS (Windows Efficiency mode).</summary>
    public bool BackgroundEfficiencyMode { get; set; } = true;

    /// <summary>Holds queued (not running) background work during playback.</summary>
    public bool PauseBackgroundWhilePlaying { get; set; } = true;

    /// <summary>Holds queued background work while an export or the queue runs.</summary>
    public bool PauseBackgroundWhileExporting { get; set; } = true;

    /// <summary>
    /// Background decode/proxy-encode adapter: -1 Auto (always the Compositor GPU, never an automatic secondary GPU), -2 CPU, else a
    /// DXGI index.
    /// </summary>
    public int BackgroundGpu { get; set; } = -1;

    /// <summary>Threads per background FFmpeg; 0 = FFmpeg default.</summary>
    public int BackgroundToolThreads { get; set; }

    /// <summary>whisper transcription threads; 0 = whisper default (max 4).</summary>
    public int WhisperThreads { get; set; }

    /// <summary>Progress chip also shows background work, not only exports and loads.</summary>
    public bool ShowBackgroundTasks { get; set; } = true;

    /// <summary>How long a finished task stays in the progress flyout, in seconds.</summary>
    public int FinishedTaskSeconds { get; set; } = 5;

    /// <summary>Effective reserved cores: Auto gives 2 at 8+ cores, 1 at 4+, else 0.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveReservedCores => ReservedCores >= 0
        ? Math.Min(ReservedCores, Math.Max(Environment.ProcessorCount - 2, 0))
        : Environment.ProcessorCount >= 8 ? 2 : Environment.ProcessorCount >= 4 ? 1 : 0;

    // ----- Dead air -----

    /// <summary>Remove Dead Air silence threshold (dB).</summary>
    public double DeadAirThresholdDb { get; set; } = -35;

    /// <summary>Remove Dead Air minimum silence duration (s) to cut.</summary>
    public double DeadAirMinSeconds { get; set; } = 1;

    // ----- Diagnostics: crash reports (CrashReporter) -----

    /// <summary>
    /// Writes sanitized .hpcrash reports (no names, paths or media text) on a crash and runs the native-crash sentinel process.
    /// With <see cref="CrashReportEndpoint"/> set, pending reports upload once after startup (CrashReports.UploadPendingAsync).
    /// False disables writing, uploading and the sentinel.
    /// </summary>
    public bool SendCrashReports { get; set; } = true;

    /// <summary>
    /// Crash report upload URL (HTTPS; plain http only to localhost for tests). appsettings.json only; empty by default, so reports
    /// stay local until a collection service exists.
    /// </summary>
    public string CrashReportEndpoint { get; set; } = DefaultCrashReportEndpoint;

    public const string DefaultCrashReportEndpoint = "";

    // ----- First run, appearance and mode -----

    /// <summary>False until the first-run window has been completed once.</summary>
    public bool FirstRunComplete { get; set; }

    /// <summary>One of ThemeManager.Themes: follow Windows, or always dark, or always light.</summary>
    public string Theme { get; set; } = ThemeManager.FollowSystem;

    /// <summary>
    /// Interface name reported where one is needed (crash reports); always the editor since Encoder Mode was removed.
    /// </summary>
    public const string EditorMode = "Editor Mode";

    // Legacy UiMode/ModeProfiles members are ignored on read; their values were migrated to top-level settings long ago.

    /// <summary>Game Preset file names offered by the drop overlay, in display order; empty uses the most recent.</summary>
    public List<string> SplashStylePresets { get; set; } = [];


    // Session start values for main-window toggles; changes in the window last only for the session.
    public bool StartWithSnapToIFrames { get; set; } = true;
    public bool StartWithChapterMarkers { get; set; } = true;
    public bool StartWithChaptersAtCuts { get; set; }

    /// <summary>
    /// Sequence Overview Scrubber height step (legacy property name), 1 (16 px) to 52 in 4 px steps. 0 in older files, which derive
    /// it from the five-step <see cref="TimelineHeight"/>.
    /// </summary>
    public int MasterTimelineHeight { get; set; }

    /// <summary>How tall each track's time bar is on the timeline, 1 to 50.</summary>
    public int LayerHeight { get; set; } = 25;

    /// <summary>Audio tab row height step, 1-50.</summary>
    public int AudioHeight { get; set; } = 25;

    /// <summary>One-time marker: pane and window-start defaults were applied to a pre-defaults settings file.</summary>
    public int ViewDefaultsVersion { get; set; }

    public const string ResetToPreset = "Active Game Preset";
    public const string ResetToSystem = "System Default";

    // Legacy JSON name of ResetToPreset, read for compatibility.
    internal const string LegacyResetToPreset = "Active Style Preset";

    public static IReadOnlyList<string> ResetTargets { get; } = [ResetToPreset, ResetToSystem];

    /// <summary>Double-click reset target for sliders and number boxes: the active Game Preset's value, or the built-in default.</summary>
    public string DoubleClickResetsTo { get; set; } = ResetToPreset;

    /// <summary>
    /// Hidden tab pane ids ("Tab:Video"); null uses <see cref="DefaultHiddenTabs"/>. Replace the list, never mutate it (copies share it).
    /// </summary>
    public List<string>? HiddenTabs { get; set; }

    /// <summary>
    /// Default hidden tabs: none. A stale "Tab:Video" entry names a retired pane and is ignored.
    /// </summary>
    public static IReadOnlyList<string> DefaultHiddenTabs { get; } = [];

    /// <summary>Shows the Sequence Overview Scrubber pane (legacy property name).</summary>
    public bool ShowMasterTimeline { get; set; } = true;

    /// <summary>
    /// Retired status bar flag, kept only so older settings files deserialize; nothing reads it. Status and progress live in the
    /// title bar.
    /// </summary>
    public bool ShowStatusBar { get; set; }

    /// <summary>Shows the Keyframes pane (selected clip's keyframe lanes).</summary>
    public bool ShowKeyframesPane { get; set; }

    /// <summary>Shows the Effects pane (selected clip's or Sequence Output's effect stack).</summary>
    public bool ShowEffectsPane { get; set; } = true;

    /// <summary>Shows the Media Browser pane.</summary>
    public bool ShowMediaBrowser { get; set; }

    /// <summary>Playback bar control names (PlaybackControlSet); null for defaults.</summary>
    public string? PlaybackControls { get; set; }

    /// <summary>Main-window dock arrangement (Docking.DockLayoutState JSON; floating windows in SideWindows); null for defaults.</summary>
    public string? DockLayout { get; set; }

    /// <summary>
    /// Pending built-in layout name from the first-run window. The next dock build applies it, stores it in <see cref="DockLayout"/>
    /// and clears this (only the main window can build its own panes).
    /// </summary>
    public string? FirstRunLayout { get; set; }

    /// <summary>
    /// Custom saved layouts, at most <see cref="SavedLayoutSlots"/>, by slot. Replaced wholesale on save/delete so settings copies never share it.
    /// </summary>
    public List<SavedDockLayout> SavedLayouts { get; set; } = [];

    /// <summary>Number of custom layout slots.</summary>
    public const int SavedLayoutSlots = 3;

    /// <summary>
    /// Startup layout (Layout selector's Default pill): a built-in name or "saved:N" for custom slot N; unknown or empty uses
    /// <see cref="DockLayout"/>. Null in legacy files until startup resolves the built-in layout matching their arrangement.
    /// </summary>
    public string? DefaultStartupLayout { get; set; } = "Standard Editing";

    /// <summary>Each project tab stores its own dock layout and restores it on activation; false shares one layout.</summary>
    public bool PerTabWindowLayouts { get; set; } = true;

    /// <summary>Opening a project applies its saved workspace (.hproj "workspace": layout, Media Browser view, 3D pose).</summary>
    public bool RestoreProjectWorkspaceOnOpen { get; set; } = true;

    /// <summary>Capture folder pre-indexed at idle (posters, filmstrips, waveforms, seek indexes); empty for none.</summary>
    public string WatchedCaptureFolder { get; set; } = "";

    /// <summary>Export Quality Preview magnifier: the loupe follows the pointer over the compare view. Viewer preference only.</summary>
    public bool ExportQualityPreviewLoupe { get; set; }

    /// <summary>Whether clips on the timeline show a diamond at each of their keyframes.</summary>
    public bool ShowClipKeyframes { get; set; } = true;

    /// <summary>
    /// With keyframing on, true keys only the changed property (an anchor-relative resize also keys position); false keys every
    /// motion property (Key All).
    /// </summary>
    public bool KeyChangedOnly { get; set; }

    /// <summary>The Keyframes pane draws the selected row's value over time under it.</summary>
    public bool ShowKeyframeGraph { get; set; } = true;

    /// <summary>A ripple edit (Ctrl-drag of a clip's edge, Shift+Delete) moves the later clips of every track, not only its own.</summary>
    public bool RippleAllTracks { get; set; } = true;

    /// <summary>
    /// Speed &amp; Time Ripple Timeline: a speed change shifts later clips (all tracks when <see cref="RippleAllTracks"/>) by the
    /// duration delta. False leaves a gap on shrink and pushes only same-track clips on growth.
    /// </summary>
    public bool SpeedRipplesTimeline { get; set; } = true;

    /// <summary>Exclusive solo (clears other solos); false makes solo additive with Alt-click for exclusive.</summary>
    public bool SoloIsExclusive { get; set; }

    /// <summary>Clips with sound show a gain line and fade handles on their blocks, to be dragged.</summary>
    public bool ShowClipGainLines { get; set; } = true;

    /// <summary>Shows the Creator Hotbar's Quick FX button (flyout with five slots) on the Timeline toolbar.</summary>
    public bool ShowCreatorHotbar { get; set; } = true;

    /// <summary>
    /// Shows effect lanes under a track while its stack is open; false shows only the clip's "fx N" badge.
    /// </summary>
    public bool ShowTimelineEffectLanes { get; set; } = true;

    /// <summary>Also shows effect lanes for a Cutout's built-in Mask and Rounded &amp; Feathered Edges cards (default off).</summary>
    public bool ShowBuiltInMaskEffectLanes { get; set; }

    /// <summary>Draws filmstrips on video and picture clips.</summary>
    public bool ShowClipFilmstrips { get; set; } = true;

    /// <summary>Draws waveforms on clips with audio.</summary>
    public bool ShowClipWaveforms { get; set; } = true;

    /// <summary>Page-scrolls the Timeline during playback to keep the playhead visible.</summary>
    public bool TimelineFollowsPlayhead { get; set; } = true;

    /// <summary>Shows Undo/Redo buttons on the Timeline toolbar (default off; Ctrl+Z/Ctrl+Y cover it).</summary>
    public bool ShowTimelineUndoRedo { get; set; }

    /// <summary>
    /// Creator Hotbar slot contents as "kind:id" (Models.HotbarSlot); null for HotbarSlot.Defaults. Replace the list, never mutate it
    /// (copies share it).
    /// </summary>
    public List<string>? HotbarSlots { get; set; }

    /// <summary>How long a crossfade made from a cut's right-click menu is, in seconds.</summary>
    public double DefaultTransitionSeconds { get; set; } = 1;

    /// <summary>The layout pane draws an animated layer's path, with a dot at each of its place keyframes to drag.</summary>
    public bool ShowMotionPath { get; set; } = true;

    /// <summary>A layer dragged in the layout pane snaps to the frame's middle and the safe margins (Alt held: no snapping).</summary>
    public bool SnapLayoutGuides { get; set; } = true;

    /// <summary>
    /// On-player transform gizmo for the selected layer (move, resize, rotate); hidden during playback.
    /// </summary>
    public bool EditLayersOnPlayer { get; set; } = true;

    /// <summary>How tall the Sequence Overview Scrubber starts out, 1 to 52: 88 px.</summary>
    public const int DefaultMasterTimelineHeight = 19;

    /// <summary>Reset Panes Layout: every pane back where it starts, at the size it starts with.</summary>
    public void ResetPaneLayout()
    {
        DockLayout = null;
        MasterTimelineHeight = DefaultMasterTimelineHeight;
    }

    // Legacy StartWithFrameEngine is ignored on read (unknown members are skipped).

    /// <summary>
    /// Default video encoder FFmpeg name (libx264, h264_nvenc, ...) from the first-run window; blank for Copy.
    /// </summary>
    public string DefaultVideoEncoder { get; set; } = "";

    /// <summary>
    /// Default audio codec and bitrate from the first-run window, so audio edits never hit a stream-copied track; blank for Copy.
    /// </summary>
    public string DefaultAudioEncoder { get; set; } = "";

    public string DefaultAudioBitrate { get; set; } = "";

    /// <summary>Default export sample rate (Hz); 0 = Auto.</summary>
    public int DefaultAudioSampleRate { get; set; }

    /// <summary>Default export channel layout (<see cref="Models.AudioChannelChoice"/>).</summary>
    public string DefaultAudioChannels { get; set; } = Models.AudioChannelChoice.Stereo;

    // Remaining default encoding settings; blank keeps the built-in default. Export Presets always override them; Game Presets
    // override them unless DefaultsOverrideStyles is set.
    public string DefaultContainer { get; set; } = "";
    public bool DefaultWebOptimized { get; set; } = true;
    public string DefaultEncoderPreset { get; set; } = "";
    public string DefaultRateControl { get; set; } = "";

    /// <summary>The constant-quality value (CRF, CQ, CQP...); null for the built-in one.</summary>
    public int? DefaultQuality { get; set; }

    /// <summary>Video bitrate (kbps); blank for automatic (MainViewModel.AutoBitrateKbps, from output size and rate).</summary>
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

    // Legacy DefaultHardwareDecoding (-hwaccel auto) is ignored on read.

    /// <summary>Re-applies default encoding settings after a Game Preset overrides them.</summary>
    public bool DefaultsOverrideStyles { get; set; }

    /// <summary>
    /// Prompts on exit when there are unsaved changes or never-saved work; multiple tabs are listed in one dialog that can also
    /// suspend them for the next launch. Skipped while <see cref="KeepOpenTabsOnClose"/> is set.
    /// </summary>
    public bool PromptToSaveOnExit { get; set; } = true;

    /// <summary>
    /// Suspends all open tabs to the session cache on exit without prompting and restores them, with the same active tab, on the next
    /// launch. Default off: exit then prompts (<see cref="PromptToSaveOnExit"/>) to save, suspend or close.
    /// </summary>
    public bool KeepOpenTabsOnClose { get; set; }

    /// <summary>Project Manager shows a hover-scrubbable poster of each project's source video.</summary>
    public bool ShowProjectPreviews { get; set; } = true;

    // ----- Social Sharing Squisher (startup dialog) -----

    /// <summary>Whether the startup dialog has its Squisher section.</summary>
    public bool ShowSquisher { get; set; } = true;

    /// <summary>The sizes a video can be squeezed under, each a target on the startup dialog.</summary>
    public List<SquishPreset> SquisherPresets { get; set; } = SquishPreset.Defaults();

    /// <summary>How many of the presets, from the top of the list, the startup dialog shows.</summary>
    public int SquisherPresetCount { get; set; } = 4;

    // ----- Startup window placement -----
    public const string DefaultWindowSize = "Default Size", RememberWindowSize = "Remember Last Size", MaximizedWindow = "Launch Maximized";

    public static IReadOnlyList<string> StartupWindowSizes { get; } = [DefaultWindowSize, RememberWindowSize, MaximizedWindow];

    /// <summary>How large the window is when HandPeg starts: as designed, as it was left, or maximized.</summary>
    public string StartupWindowSize { get; set; } = DefaultWindowSize;

    // Last restored bounds and maximized state.
    public double LastWindowWidth { get; set; }
    public double LastWindowHeight { get; set; }
    public bool LastWindowMaximized { get; set; }

    /// <summary>Global scrollbar thickness (px), 4 (hairline) to 16 (Windows default).</summary>
    public double ScrollbarThickness { get; set; } = 10;

    /// <summary>Megabytes kept in hand below a preset's size when the bitrate is worked out.</summary>
    public double SquisherOffsetMb { get; set; } = 1.5;

    /// <summary>Use <see cref="SquisherEncoderPreset"/> in place of the encoder's most careful preset.</summary>
    public bool SquisherManualPreset { get; set; }

    /// <summary>
    /// Encoder preset used while the override is on ("veryslow", "p5", "balanced", ...). Defaults to the slowest so enabling the
    /// override never silently lowers quality; saved values are kept.
    /// </summary>
    public string SquisherEncoderPreset { get; set; } = "veryslow";

    /// <summary>Writes the open project to "Name.autosave.hproj" every so often, in the background.</summary>
    public bool AutosaveEnabled { get; set; }

    /// <summary>How many minutes pass between autosaves.</summary>
    public int AutosaveMinutes { get; set; } = 5;

    /// <summary>
    /// Portable saves (default off): a manual save writes Projects\Name\Name.hproj and gathers every referenced file into
    /// Projects\Name\media\ (hard link on the same volume, else copy) with relative paths (PortableProject). Autosave and Save As are unaffected.
    /// </summary>
    public bool FullyPortableProjectSave { get; set; }

    /// <summary>
    /// Bundles (.hpb) store long, sparsely used recordings (over 2 min, under 60 % used) as lossless stream-copied ranges with 5 s
    /// handles (VodTrim); false bundles whole files. The project and its sources are never modified.
    /// </summary>
    public bool TrimLongVodsOnBundle { get; set; } = true;

    /// <summary>Opens the Canvas by itself as soon as there is a layout to edit.</summary>
    public bool AutoOpenLayoutPane { get; set; }

    /// <summary>
    /// Applies the default editor interface. Used by the first-run window and once for legacy Encoder Mode settings (<see cref="Load"/>).
    /// </summary>
    public void ApplyEditorDefaults()
    {
        TimelineHeight = 5;
        ResetPaneLayout();
        PresetBarLocation = PresetBarInExport;

        ShowAdvancedFiltersTab = true;
        ShowTimelineWaveform = true;
        ShowAdvancedPlayback = true;
        StartWithSnapToIFrames = true;
        SnapTimelineToIFrames = true;
        AutoToggleTimelineSnap = true;
        GenerateHoverPreviews = true;
        SaveTargetSizeInPresets = true;
        StartWithChapterMarkers = true;
        StartWithChaptersAtCuts = true;
        AutoOpenLayoutPane = true;
        ShowTimelineThumbnails = true;
        (ShowKeyframesPane, ShowClipKeyframes) = (true, true);
        ShowMediaBrowser = true;
        // No status bar: the title bar carries status and progress.
        (HiddenTabs, ShowMasterTimeline, ShowStatusBar) = (null, true, false);

        ThumbnailIntervalSeconds = 1;
        HoverPreviewScale = 3;
    }

    /// <summary>Sends a Windows notification when an encode or the queue finishes.</summary>
    public bool NotifyOnEncodeComplete { get; set; } = true;

    /// <summary>When an encode fails on a hardware encoder, try again once with the matching software encoder.</summary>
    public bool AutoFallbackToSoftware { get; set; } = true;

    // ----- SponsorBlock categories for yt-dlp -----
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
    /// SponsorBlock action for checked categories: cut (--sponsorblock-remove), mark as chapters (--sponsorblock-mark), or ignore.
    /// Unknown values are treated as cut.
    /// </summary>
    public string SponsorBlockMode { get; set; } = SponsorBlockCut;

    /// <summary>The choices for <see cref="SponsorBlockMode"/>, with the words shown for them.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SponsorBlockModes { get; } =
    [
        new(SponsorBlockCut, "Cut Out SponsorBlock Segments"),
        new(SponsorBlockChapters, "Keep as Named Chapters"),
        new(SponsorBlockIgnore, "Ignore"),
    ];

    /// <summary>yt-dlp SponsorBlock request: categories (empty when ignored or none checked) and whether to mark instead of cut.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (string Categories, bool Mark) SponsorBlockRequest =>
        SponsorBlockMode == SponsorBlockIgnore ? ("", false) : (SponsorBlockCategories, SponsorBlockMode == SponsorBlockChapters);

    // Legacy UseEasySocialExport is ignored on read (superseded by ExportPresetCatalog).

    /// <summary>
    /// Resolves the auto-applied preset for a source. Precedence: folder rule, keyword rule, extension rule, then the default;
    /// among matching folder or keyword rules the longest wins.
    /// </summary>
    /// <param name="filePath">The loaded file.</param>
    /// <param name="sourcePath">The requested source: the same file, or the URL it was downloaded from.</param>
    public string? FindPresetFor(string filePath, string sourcePath)
    {
        var rules = SmartRules.Where(r => !string.IsNullOrWhiteSpace(r.Path) && !string.IsNullOrWhiteSpace(r.Preset)).ToList();

        var folderRule = rules
            .Where(r => r.Type == SmartRule.Folder && IsInFolder(filePath, r.Path))
            .OrderByDescending(r => r.Path.Trim().Length)
            .FirstOrDefault();
        if (folderRule is not null)
            return folderRule.Preset;

        // Keywords match anywhere in the file name, parent folders or URL.
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

    /// <summary>Editable deep copy, isolated from the active settings.</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SquisherPresets = SquisherPresets.Select(p => p.Clone()).ToList();
        copy.SmartRules = SmartRules.Select(r => new SmartRule { Type = r.Type, Path = r.Path, Preset = r.Preset }).ToList();
        copy.SplashStylePresets = [.. SplashStylePresets];

        copy.SavedLayouts = [.. SavedLayouts];
        copy.HiddenTabs = HiddenTabs is null ? null : [.. HiddenTabs];
        copy.DefaultCaptionStyle = DefaultCaptionStyle?.Clone();
        copy.Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra);
        CloneInput(copy);
        return copy;
    }
}
