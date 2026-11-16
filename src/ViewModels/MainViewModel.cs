using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>
    /// Default choice for Export Framerate, Colorspace and Pixel Aspect: the Project Settings (MainViewModel.ProjectSettings.cs).
    /// Legacy "Same as source" values map to it (Pick).
    /// </summary>
    public const string MatchProject = "Match Project Settings";
    private const string CustomOption = "Custom";
    public const string CopyOption = "Copy (Stream Copy)";
    private const string NoneOption = "None";
    private const string AutoOption = "Auto";
    private const string ConstantQuality = "Constant Quality (CQ)";
    private const string AverageBitrate = "Average Bitrate (ABR)";
    private const string ConstantBitrate = "Constant Bitrate (CBR)";
    private const string TonemapToSdr = "HDR to SDR (Tonemap)";

    // Properties that have no influence on the generated FFmpeg command.
    private static readonly HashSet<string> NonCommandProperties =
    [
        nameof(IsExportQualityPreview), nameof(ExportQualityPreviewMode), nameof(ExportQualityPreviewWipe), nameof(ExportQualityPreviewStatus),
        // The preview Magnifier is a viewer preference.
        nameof(ExportQualityPreviewLoupe),
        // Auto-bitrate views are raised by the command build itself (UpdateProjectedOutput).
        nameof(EffectiveBitrateKbps), nameof(AutoBitrateText),
        // Output kind only toggles the Export tab's video sections.
        nameof(IsAudioOnlyExport), nameof(IsVideoExport),
        nameof(CommandPreview), nameof(IsCommandManuallyEdited), nameof(StatusText), nameof(SourcePath), nameof(DownloadResolution),
        nameof(PositionMs), nameof(PositionText), nameof(DurationMs), nameof(DurationText), nameof(TimelineMaximum),
        nameof(IFrameStatusText), nameof(Volume), nameof(EditingJob), nameof(IsEditingQueuedJob),
        nameof(SourceWidth), nameof(SourceHeight), nameof(SourceSizeText), nameof(IsCustomPixelAspectRatio),
        nameof(HardwareEncoderStatusText), nameof(AreSoftwareEncoderOptionsEnabled), nameof(IsVideoReencoded), nameof(ShowsCopyNote),
        nameof(IsAudioReencoded), nameof(IsCustomFramerate), nameof(IsConstantQuality), nameof(IsBitrateMode),
        nameof(IsSourceHdr), nameof(HdrStatusText), nameof(SelectedPreset), nameof(PresetName),
        nameof(DownloadSubtitles), nameof(AudioTracksHint), nameof(SubtitleTracksHint), nameof(HasRateControl),
        nameof(QueueButtonText), nameof(IsQueuePauseRequested), nameof(IsNvencEncoder), nameof(CanNvencTemporalAq),
        nameof(IsAmfEncoder), nameof(HasLookahead), nameof(HasTenBit), nameof(CanTenBit), nameof(RateControlChoice),
        nameof(IsDependencyUpdateAvailable), nameof(DependencyStatusText),
        nameof(IsInteractiveCropActive), nameof(TargetSizeHint), nameof(PlaybackSpeed), nameof(HasEncoderPresets),
        // The speed pill's step is a view of PlaybackSpeed.
        nameof(PlaybackSpeedStep),
        nameof(FrameWidth), nameof(FrameHeight), nameof(ShowIFrames),
        nameof(CaptionAudioPath), nameof(CaptionHint), nameof(CaptionStyleSummary),
        nameof(DefaultPresetChoice),
        nameof(TimelineWaveform), nameof(SelectedMicrophone), nameof(IsRecording), nameof(IsRecordingPaused), nameof(VoiceoverStatus),
        nameof(HasVoiceover), nameof(VoiceoverWaveform), nameof(VoiceoverDuration), nameof(VoiceoverTrimText),
        nameof(ResolutionPreset), nameof(ShowPresetBarAtTop), nameof(ShowPresetBarInExport),
        nameof(TimelineAreaHeight), nameof(AudioTrackRowHeight), nameof(ShowWaveforms),
        nameof(WhisperPrompt), nameof(WhisperLanguage), nameof(WhisperTranslate), nameof(CanEditLayout),
        nameof(CaptionUseExternalAudio), nameof(CaptionUseVideoAudio), nameof(IsPlaying),
        nameof(ShowAdvancedPlayback), nameof(ShowSimplePlayback),
        nameof(ShowCommandPreviewTab), nameof(ShowAdvancedFiltersTab),
        nameof(DrawTargetLayer), nameof(IsArrangeActive), nameof(SpriteSheetPath),
        nameof(IsBusy), nameof(ProgressValue), nameof(IsProgressIndeterminate),
        nameof(ShowTimelineThumbnails), nameof(ShowHoverPreviews), nameof(HasSource), nameof(SoloedTracks),
        nameof(VoiceoverMixWaveform), nameof(VoiceoverMixStart), nameof(VoiceoverMixWidth),
        nameof(CopyBypassWarning), nameof(ProjectColorNote), nameof(ShowLinkedAudio), nameof(ShowTimelineOptionsBelow), nameof(HasAudioClips),
        nameof(KeyframesEnabled), nameof(KeyframesSnap), nameof(KeyframesCreate), nameof(KeyLayer), nameof(ActiveKeyLayer), nameof(SelectedTextLayer), nameof(IsTextLayerSelected),
        nameof(SubtitleSourcePath), nameof(SubtitleTrackSummary), nameof(HasSubtitleCues), nameof(WhisperModelChoice), nameof(WhisperModelStatus), nameof(AutosaveEnabled), nameof(SyncMasterTimeline), nameof(CaptionOptionsEnabled), nameof(CompactTrackControls), nameof(HasTextCues), nameof(SubtitlesAreImages),
        nameof(SelectedStylePreset), nameof(SelectedProject), nameof(StyleName), nameof(SnapClips), nameof(LayerControlsShown), nameof(LoopPlayback), nameof(ShowSafeMargins), nameof(EditLayersOnPlayer), nameof(IsPlayerMuted),
        nameof(MasterTimelineHeight), nameof(LayerTrackHeight), nameof(AudioTrackHeight), nameof(LayerBarHeight),
        nameof(ShowKeyframesPane), nameof(ShowEffectsPane), nameof(ShowMediaBrowser), nameof(IsMediaBrowserVisible), nameof(SourceActionText), nameof(ShowClipKeyframes), nameof(HasDeleted), nameof(RecycleBinText),
        nameof(RecentActions), nameof(HideDroppedTracks), nameof(HasSelectedKeyframe), nameof(SelectedKeyEasing), nameof(PreviewSubtitles),
        nameof(BackgroundSelected),
        // Studio Audio Suite: master gain is a document edit (its commit rebuilds the command); the rest are views.
        nameof(MasterGainDb), nameof(ShowMasterMeter), nameof(SnapToBeats), nameof(ShowBeatGrid),
    ];


    private readonly CancellationTokenSource _shutdown = new();

    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _iFrameScanCancellation;
    private bool _acceptProgressReports;
    private bool _writingGeneratedCommand;
    private bool _syncingDimensions;
    private int _hardwareProbeRun;

    // Windowless background instance: converts a saved project into a queue job without touching the visible instance's state.
    private readonly bool _isBackgroundWorker;

    public MainViewModel()
    {
        StartupTrace.Mark("MainViewModel: start");
        ClipPictures = new ClipPictures(PicturesKept, FilesInUse);
        _videoEncoder = EncoderOption.Copy;
        Presets = [.. ExportPresetCatalog.BuiltIns, .. PresetStore.TakeEarly()];
        Initialize();

        var missing = DependencyUpdater.GetMissing();
        if (missing.Count > 0)
            StatusText = $"Missing {string.Join(", ", missing)}. Open Settings (the gear button) to install them.";
        StartupTrace.Mark("MainViewModel: made");

        // Deferred until after first render: the encoder probe starts FFmpeg and the update check loads the networking stack.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            _ = _hardwareEncodersEarly ? RecheckEarlyEncodersAsync() : ProbeHardwareEncodersAsync();
            _ = CheckDependencyUpdatesAsync();
            _ = AutoUpdateDependenciesLaterAsync();
            _ = UploadCrashReportsLaterAsync();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Creates a background instance that knows the same encoders and presets as <paramref name="owner"/>.</summary>
    private MainViewModel(MainViewModel owner)
    {
        _isBackgroundWorker = true;
        ClipPictures = new ClipPictures(PicturesKept, FilesInUse);

        // Per-instance folder for the cuts and chapters lists, rewritten on setting changes.
        _workFolder = Path.Combine(SessionPaths.Queue, $"worker_{Guid.NewGuid():N}");

        _videoEncoder = EncoderOption.Copy;
        foreach (var encoder in owner.VideoEncoders.Where(e => e.IsHardware))
            VideoEncoders.Add(encoder);
        Presets = [.. owner.Presets];
        Initialize();
    }

    private void Initialize()
    {
        // First-run defaults from settings, assigned to fields directly so no change handlers fire.
        var settings = AppSettings.Current;
#pragma warning disable MVVMTK0034
        _snapToIFrames = settings.StartWithSnapToIFrames;

        // Mix a video's audio tracks into one, as edits are exported.
        _mergeAudioTracks = true;
        (_chapterMarkers, _chaptersAtCuts) = (settings.StartWithChapterMarkers, settings.StartWithChaptersAtCuts);

        // First-run encoder choice; hardware encoders wait until the probe lists them (ProbeHardwareEncodersAsync).
        if (settings.DefaultVideoEncoder.Length > 0)
        {
            if (VideoEncoders.FirstOrDefault(e => e.Name == settings.DefaultVideoEncoder) is { } chosen)
                _videoEncoder = chosen;
            else
                _pendingDefaultEncoder = settings.DefaultVideoEncoder;
        }

        // First-run audio settings, so audio is encoded (not copied) from the start.
        if (AudioEncoders.Contains(settings.DefaultAudioEncoder))
            _audioEncoder = settings.DefaultAudioEncoder;
        if (AudioBitrates.Contains(settings.DefaultAudioBitrate))
            _audioBitrate = settings.DefaultAudioBitrate;
        _audioSampleRate = AudioChannelChoice.NormalizeRate(settings.DefaultAudioSampleRate);
        _audioChannels = AudioChannelChoice.Normalize(settings.DefaultAudioChannels);
#pragma warning restore MVVMTK0034

        UpdateEncoderPresets();
        UpdateRateControlChoices();
        ApplyEncodingDefaults();

        InitializeQueue();
        ApplyDisplaySettings();
        Layers.CollectionChanged += (_, _) =>
        {
            if (!Layers.Syncing)
                RefreshLayerRows();
        };
        RefreshLayerRows();
        LoadAutomation();
        Presets.CollectionChanged += (_, _) => RefreshPresetNames();
        CaptionLayer.PropertyChanged += (_, _) => GenerateCommand();

        // Preview captions render inside the caption box with its opacity and shadow, tracking moves, resizes and restyles.
        CaptionLayer.PropertyChanged += (_, e) =>
        {
            if (PreviewSubtitles && e.PropertyName is nameof(Layer.SizeWidth) or nameof(Layer.SizeHeight)
                    or nameof(Layer.PositionX) or nameof(Layer.PositionY) or nameof(Layer.Opacity)
                    or nameof(Layer.Shadow) or nameof(Layer.ShadowDistance) or nameof(Layer.ShadowAngle) or nameof(Layer.ShadowOpacity))
            {
                RefreshLiveCaptionsSoon();
            }
        };
        Document.Committed += OnDocumentCommitted;
        LoadPlaybackControls();
        AudioTracks.CollectionChanged += (_, _) => RefreshAudioRows();
        _backgroundRow.PropertyChanged += (_, _) => GenerateCommand();
        Layers.CollectionChanged += (_, _) => GenerateCommand();
        GenerateCommand();
        OpenLayoutPaneIfWanted();

        // Master Mix View voiceover position tracks the voiceover, its trim and cuts.
        if (!_isBackgroundWorker)
        {
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(DurationMs) or nameof(VoiceoverWaveform) or nameof(VoiceoverPath)
                    or nameof(VoiceoverTrimStart) or nameof(VoiceoverTrimEnd) or nameof(VoiceoverStartSeconds))
                {
                    UpdateVoiceoverMix();
                }
            };
        }
    }

    private readonly string _workFolder = SessionPaths.Root;

    // Settings default hardware encoder awaiting the probe result.
    private string? _pendingDefaultEncoder;

    // Probe race: hardware codec(s) a built-in preset wanted ("hevc") when applied before the hardware probe finished (so it fell
    // back to software). ApplyHardwareEncoders re-resolves once unless an encoder was picked manually since (OnPropertyChanged
    // clears it). _resolvingEncoder marks encoder writes made by a preset or the probe.
    private string? _pendingPreferredCodec;
    private bool _resolvingEncoder;
    private bool _hardwareEncodersListed;

    // Active speed-first preset (EncodingPreset.SpeedFirst, Fast Proxy): its encoder, and the probe's re-pick, stay at the fastest
    // step (FastestStepOf; MF -compression_level 0) until a manual encoder/step choice or another preset. _placingStep marks
    // programmatic step writes (probe default, software retry after a hardware failure).
    private bool _speedFirst;
    private bool _placingStep;

    /// <summary>Whether a speed-first preset's fastest step is in effect (read by the command and goldens).</summary>
    internal bool SpeedFirstHolds => _speedFirst;

    /// <summary>Raised with a local file path when a source is ready to be played.</summary>
    public event Action<string>? MediaLoaded;

    /// <summary>Two-option prompt: returns 1 or 2 for the chosen option, 0 for cancel.</summary>
    public Func<string, string, string, string, int>? Choose { get; set; }

    /// <summary>Confirmation prompt (title, message, confirm label); false cancels the action.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }

    /// <summary>
    /// Confirmation prompt with a "Remember my choice" box (title, message, confirm and decline labels); returns the answer and
    /// whether to remember it.
    /// </summary>
    public Func<string, string, string, string, (bool Confirmed, bool Remember)>? ConfirmOrRemember { get; set; }

    /// <summary>Asked before an encode replaces an existing file. Returning false aborts the encode.</summary>
    public Func<string, OverwriteDecision>? AskOverwrite { get; set; }

    /// <summary>
    /// Renders the Sequence at the playhead as a JPEG project thumbnail via the window's GPU engine (MainWindow.GpuPreview.cs);
    /// null when unavailable (no engine, playing, nothing open). Invoked on the UI thread; completion needs no UI thread
    /// (CaptureStateForSave waits briefly). Null for windowless VMs, whose saves carry no thumbnail.
    /// </summary>
    public Func<Task<byte[]?>>? CaptureThumbnail { get; set; }

    /// <summary>The concat list used for I-frame-snapped cuts.</summary>
    public string CutsFilePath => Path.Combine(_workFolder, "cuts.txt");

    // ----- Option lists -----

    public IReadOnlyList<string> DownloadResolutions { get; } = ["Best", "2160p", "1440p", "1080p", "720p", "480p", "360p"];
    /// <summary>Offered containers, in Save dialog filter order; audio-only formats last.</summary>
    public IReadOnlyList<string> Containers { get; } = ["mp4", "mkv", "webm", "mov", "gif", "webp", "flac", "wav", "mp3"];
    public IReadOnlyList<string> PixelAspectRatios { get; } = [MatchProject, "1:1", "4:3", "16:9", CustomOption];

    /// <summary>Software encoders, followed by whichever hardware encoders passed the probe.</summary>
    public ObservableCollection<EncoderOption> VideoEncoders { get; } = [EncoderOption.Copy, .. EncoderOption.Software];

    private static EncoderOption DefaultVideoEncoder => EncoderOption.Software[0];
    public IReadOnlyList<string> Framerates { get; } = [MatchProject, "23.976", "24", "25", "29.97", "30", "48", "50", "59.94", "60", "120", "144", "240", CustomOption];
    public IReadOnlyList<string> AudioEncoders { get; } = [CopyOption, .. AudioTrack.AllCodecs];
    public IReadOnlyList<string> AudioBitrates => AudioTrack.AllBitrates;
    public IReadOnlyList<string> Tunes { get; } = [NoneOption, "film", "animation", "grain", "fastdecode", "zerolatency"];
    public IReadOnlyList<string> Profiles { get; } = [AutoOption, "baseline", "main", "high", "high10"];
    public IReadOnlyList<string> Levels { get; } = [AutoOption, "3.1", "4.0", "4.1", "4.2", "5.1", "5.2"];
    /// <summary>The three stored rate-control modes; the Export tab shows encoder-specific names (<see cref="RateControlChoices"/>).</summary>
    public IReadOnlyList<string> RateControls { get; } = [ConstantQuality, AverageBitrate, ConstantBitrate];
    public IReadOnlyList<string> Colorspaces { get; } = [MatchProject, TonemapToSdr];

    // ----- Presets -----

    /// <summary>Export Presets: built-ins (ExportPresetCatalog), then custom ones (presets.json).</summary>
    public ObservableCollection<EncodingPreset> Presets { get; }

    [ObservableProperty] private EncodingPreset? _selectedPreset;
    [ObservableProperty] private string _presetName = "";

    // Set while selecting an already-applied preset (Smart Rule) so the selection does not re-apply it.
    private bool _selectingAppliedPreset;

    partial void OnSelectedPresetChanged(EncodingPreset? value)
    {
        if (value is null)
            return;

        PresetName = value.Name;
        if (!_selectingAppliedPreset)
            ApplyExportPreset(value);
    }

    /// <summary>
    /// Saves the current settings as a custom preset ("My Presets") under the preset box's name; a built-in name is saved as
    /// "Name (Custom)".
    /// </summary>
    // "Name (Custom)" presets created from built-ins this session; re-saving the same tweak replaces them.
    private readonly HashSet<string> _savedFromBuiltIn = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0)
        {
            StatusText = "Type a name for the preset first.";
            return;
        }

        // A "(Custom)" created this session is replaced rather than producing "(Custom 2)"; one from elsewhere (upgrade rename,
        // import) is kept.
        if (ExportPresetCatalog.IsBuiltInName(name))
        {
            name = PresetStore.UniqueName(name, Presets.Where(p => !p.IsBuiltIn && !_savedFromBuiltIn.Contains(p.Name)));
            _savedFromBuiltIn.Add(name);
        }
        var preset = CaptureSettings(name);

        // Saving under an existing custom name replaces it (built-in names are never reused).
        var existing = Presets.ToList().FindIndex(p => !p.IsBuiltIn && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            Presets[existing] = preset;
        else
            Presets.Add(preset);

        try
        {
            PresetStore.Save(Presets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"Could not write {PresetStore.FilePath}: {ex.Message}", $"Save presets to {PresetStore.FilePath}", ex);
            return;
        }

        SelectedPreset = preset;
        Log($"Saved preset \"{name}\"");
    }

    /// <summary>Reloads presets.json after an external replace (import).</summary>
    public void ReloadPresets()
    {
        SelectedPreset = null;
        Presets.Clear();
        foreach (var preset in ExportPresetCatalog.BuiltIns.Concat(PresetStore.Load()))
            Presets.Add(preset);
    }

    /// <summary>
    /// Applies a full preset: export settings (ApplyEncodingPart) and, with <paramref name="withLayout"/>, size, layout, crop,
    /// captions, filters and colours. Used for project settings (withLayout false) and Smart Rule presets; the Export Preset
    /// selector applies export settings only (ApplyExportPreset).
    /// </summary>
    /// <param name="withLayout">False for project settings: the project's timeline owns clip looks, placement and crops.</param>
    private void ApplyPreset(EncodingPreset preset, bool withLayout = true)
    {
        // About 60 settings change here; the command rebuilds once at the end.
        using var whole = DeferCommand();

        // Set width and height as stored, without aspect recalculation.
        _syncingDimensions = true;
        try
        {
            KeepAspectRatio = preset.KeepAspectRatio;

            // Legacy presets lack the option and default to landscape.
            UseVerticalResolution = preset.UseVerticalResolution ?? false;
            if (withLayout)
                ApplyPresetCrop(preset);
            OutputWidth = preset.OutputWidth;
            OutputHeight = preset.OutputHeight;
        }
        finally
        {
            _syncingDimensions = false;
        }

        // Both sides: exact size; one side: completed from the aspect.
        if (string.IsNullOrWhiteSpace(preset.OutputWidth) || string.IsNullOrWhiteSpace(preset.OutputHeight))
            RelinkDimensions();
        CapShortSide(preset.ShortSideCap);

        PixelAspectRatio = Pick(PixelAspectRatios, preset.PixelAspectRatio, MatchProject);
        CustomPixelAspectRatio = preset.CustomPixelAspectRatio;
        BlurRadius = Math.Clamp(preset.BlurRadius, 0, Models.Effects.EffectRegistry.BackgroundMostRadius);
        BlurPasses = Math.Clamp(preset.BlurPasses, 1, MostBlurPasses);
        BackgroundDim = Math.Clamp(preset.BackgroundDim, -0.5, 0);
        BackgroundZoom = Math.Clamp(preset.BackgroundZoom, 1, MostBackgroundZoom);
        SetBackgroundEffects(preset.BackgroundEffects, preset.BackgroundEffectsBypassed);
        // A preset layout applies as a template to the target video clips (layers -> Cutouts, video look, legacy Center -> placement;
        // pending for the next file with no clips). Project settings carry none: RestoreStateAsync installs the timeline afterwards.
        if (withLayout)
        {
            _backgroundRow.IsHidden = preset.BackgroundHidden;
            TakeBlurFill(preset.BackgroundHidden);
            if (preset.MainLayer is not null || preset.Layers is { Count: > 0 } || Math.Abs(preset.CenterZoom - 1) > 0.0005 && preset.CenterZoom > 0
                || Math.Abs(preset.CenterOffsetX) > 0.0005 || Math.Abs(preset.CenterOffsetY) > 0.0005)
            {
                PutTemplate(TemplateOf(preset.Layers, preset.MainVideoIndex, preset.MainLayer, preset.CenterZoom, preset.CenterOffsetX, preset.CenterOffsetY,
                    blurFilled: !preset.BackgroundHidden), $"apply {preset.Name}");
            }
        }

        _layoutSourceAspect = preset.LayoutSourceAspectRatio;

        AutoCaptions = preset.AutoCaptions;
        if (preset.CaptionStyle is { } captionStyle)
            SetCaptionStyle(captionStyle.Clone());
        SetCaptionLayer(preset.CaptionLayer);
        WhisperPrompt = preset.WhisperPrompt ?? "";
        WhisperLanguage = Pick(WhisperLanguages, preset.WhisperLanguage ?? "", "en");
        WhisperTranslate = preset.WhisperTranslate;

        LutPath = preset.LutPath;
        if (withLayout)
            SetSequenceEffects(preset.SequenceEffects, preset.SequenceEffectsBypassed);
        ColorContrast = Math.Clamp(preset.ColorContrast, 0, 2);
        ColorBrightness = Math.Clamp(preset.ColorBrightness, -1, 1);
        ColorSaturation = Math.Clamp(preset.ColorSaturation, 0, 3);
        ColorGamma = Math.Clamp(preset.ColorGamma, 0.1, 3);
        ColorHue = Math.Clamp(preset.ColorHue, -180, 180);
        ColorRed = Math.Clamp(preset.ColorRed, -1, 1);
        ColorGreen = Math.Clamp(preset.ColorGreen, -1, 1);
        ColorBlue = Math.Clamp(preset.ColorBlue, -1, 1);
        SharpenStrength = Math.Clamp(preset.SharpenStrength, 0, 1);
        FadeIn = preset.FadeIn;
        FadeOut = preset.FadeOut;
        Deinterlace = preset.Deinterlace;
        Denoise = preset.Denoise;

        var encoderFound = ApplyEncodingPart(preset);
        Log(encoderFound
            ? $"Applied preset \"{preset.Name}\""
            : $"Applied preset \"{preset.Name}\", but {preset.VideoEncoder} is not available here: kept {VideoEncoder.DisplayName}");
    }

    /// <summary>
    /// Applies a preset's export settings: encoder (hardware-preferred via ResolveEncoder) and step (default BestStepOf), tune,
    /// profile/level, rate control, quality/bitrate, frame rate cap, colourspace, encoder options, 10-bit, audio, target size,
    /// tracks, container and Web Optimized. Returns whether the named encoder was found.
    /// </summary>
    private bool ApplyEncodingPart(EncodingPreset preset)
    {
        // Encoder before step (steps depend on the encoder); an unavailable hardware encoder keeps the current one.
        // Clears any previous speed-first state; this preset's starts after encoder and step are set.
        _speedFirst = false;
        var (encoder, encoderFound) = ResolveEncoder(preset);
        _resolvingEncoder = true;
        try
        {
            VideoEncoder = encoder;
        }
        finally
        {
            _resolvingEncoder = false;
        }

        // Built-in wanting a hardware codec before the probe finished: re-resolve when it does.
        _pendingPreferredCodec = !_hardwareEncodersListed && preset.IsBuiltIn && !encoder.IsHardware && preset.PreferredCodec is { Length: > 0 } wanted ? wanted : null;

        // No step named: use the encoder's best (veryslow for x264/x265).
        if (EncoderPresets.Contains(preset.EncoderPreset))
            EncoderPreset = preset.EncoderPreset;
        else if (preset.EncoderPreset.Length == 0)
            EncoderPreset = BestStepOf(VideoEncoder) ?? "veryslow";

        // Speed-first (Fast Proxy): fastest step of the resolved encoder; stepless MF gets -compression_level 0 in the command.
        if (preset.SpeedFirst)
        {
            if (FastestStepOf(VideoEncoder) is { } fastest && EncoderPresets.Contains(fastest))
                EncoderPreset = fastest;
            _speedFirst = true;
        }

        Tune = Pick(Tunes, preset.Tune, NoneOption);
        Profile = Pick(Profiles, preset.Profile, AutoOption);
        Level = Pick(Levels, preset.Level, AutoOption);
        RateControl = Pick(RateControls, preset.RateControl, AverageBitrate);
        Crf = Math.Clamp(preset.Quality, 0, 51);
        TargetBitrate = preset.TargetBitrate;
        Framerate = Pick(Framerates, preset.Framerate, MatchProject);
        CustomFramerate = preset.CustomFramerate;

        // Platform frame-rate cap: the project rate when below it, else the cap.
        if (preset.FramerateCap > 0 && Framerate == MatchProject && Document.FrameRate.IsValid && Document.FrameRate.Fps > preset.FramerateCap + 0.01
            && preset.FramerateCap.ToString(CultureInfo.InvariantCulture) is var cap && Framerates.Contains(cap))
        {
            Framerate = cap;
        }

        Colorspace = Pick(Colorspaces, preset.Colorspace, MatchProject);
        ExtraVideoArguments = preset.ExtraVideoArguments;
        NvencTune = Pick(NvencTunes, preset.NvencTune, NvencHighQuality);
        NvencMultipass = Pick(NvencMultipasses, preset.NvencMultipass, NvencSinglePass);
        NvencSpatialAq = preset.NvencSpatialAq;
        NvencTemporalAq = preset.NvencTemporalAq;
        AmfUsage = Pick(AmfUsages, preset.AmfUsage, AmfTranscoding);
        QsvIcq = preset.QsvIcq;

        // Legacy presets store NVENC-specific lookahead and 10-bit fields under its name.
        Lookahead = Math.Clamp(Math.Max(preset.Lookahead, preset.NvencLookahead ?? 0), 0, LargestQsvLookahead);
        TenBit = preset.TenBit || preset.NvencTenBit == true;
        AudioEncoder = Pick(AudioEncoders, preset.AudioEncoder, "aac");
        AudioBitrate = Pick(AudioBitrates, preset.AudioBitrate, "160k");
        AudioSampleRate = AudioChannelChoice.NormalizeRate(preset.AudioSampleRate);
        AudioChannels = AudioChannelChoice.Normalize(preset.AudioChannels);
        MergeAudioTracks = preset.MergeAudioTracks;
        NormalizeAudio = preset.NormalizeAudio;
        LoudnessTarget = NormalizeLoudness(preset.LoudnessTarget);
        DuckAmountDb = Math.Clamp(preset.DuckAmountDb, -40, -1);

        // Apply the target size only when the preset stored one.
        if (!string.IsNullOrWhiteSpace(preset.TargetFileSize))
            TargetFileSize = preset.TargetFileSize;

        // Reset every track to the codec/bitrate above (discarding per-track edits), then apply the preset's per-track values.
        ApplyAudioDefaultsToTracks();
        ApplyAudioTrackStates(preset.AudioTracks ?? []);

        if (preset.Container is { } container && Containers.Contains(container))
            Container = container;
        if (preset.WebOptimized is { } webOptimized)
            WebOptimized = webOptimized;
        return encoderFound;
    }

    /// <summary>
    /// Restores per-track output choices (mode, codec, bitrate, title) for existing tracks; others are ignored. With
    /// <paramref name="legacyMixing"/> (legacy project open), also the old main-video stream mixing fed to ConvertLegacy, which neutralises it.
    /// </summary>
    private void ApplyAudioTrackStates(IEnumerable<AudioTrackState> states, bool legacyMixing = false)
    {
        foreach (var saved in states)
        {
            if (AudioTracks.FirstOrDefault(t => t.Index == saved.Index) is not { } track)
                continue;

            track.Action = AudioTrack.AllActions.Contains(saved.Action) ? saved.Action : track.Action;
            track.Codec = Pick(AudioTrack.AllCodecs, saved.Codec, track.Codec);
            track.Bitrate = Pick(AudioTrack.AllBitrates, saved.Bitrate, track.Bitrate);
            if (!string.IsNullOrWhiteSpace(saved.Title))
                track.Title = saved.Title;
            if (!legacyMixing)
                continue;

            track.GainDb = Math.Clamp(saved.GainDb, -40, 30);
            track.Filters = saved.Filters?.Clone() ?? new TrackAudioFilters();
            track.SetEdits(saved.Offset, saved.Pieces);
            (track.AutoDuck, track.IsVoice) = (saved.AutoDuck, saved.IsVoice);
        }
    }

    private static string Pick(IReadOnlyList<string> options, string value, string fallback) =>
        options.Contains(value) ? value : fallback;

    // ----- Source / destination -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceActionText))]
    private string _sourcePath = "";

    /// <summary>Source box button mode: browses for a file when the box is empty, otherwise loads.</summary>
    public string SourceActionText => string.IsNullOrWhiteSpace(SourcePath) ? "Browse" : "Load";
    [ObservableProperty] private string _downloadResolution = "Best";
    [ObservableProperty] private string _destinationPath = "";

    /// <summary>
    /// The last opened file (or yt-dlp download): drives the Info/Streams/Download panels, default output and project names, and
    /// export subtitle streams/chapters (SequenceExportState.StreamSourcePath). Never used for rendering; opening places it as an
    /// ordinary clip (PlaceOpenedFile). "" for none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    [NotifyPropertyChangedFor(nameof(ShowsCopyNote))]
    private string _localMediaPath = "";

    // ----- Status bar -----

    // Empty at start so the title bar centre shows nothing until the first action.
    [ObservableProperty] private string _statusText = "";

    // Action log: each action flashes a short status-bar message; the last few are kept for the status bar tooltip.

    /// <summary>Raised after an action message is posted to the status bar.</summary>
    public event Action? ActionLogged;

    private readonly List<string> _recentActions = [];

    /// <summary>The last actions, the latest first, a line each with the time it was done.</summary>
    public string RecentActions => _recentActions.Count > 0 ? string.Join(Environment.NewLine, _recentActions) : "Nothing has been done yet.";

    /// <summary>Posts an action message: status bar flash plus recent-actions history.</summary>
    public void Log(string action)
    {
        StatusText = action;
        if (_isBackgroundWorker)
            return;

        _recentActions.Insert(0, $"{DateTime.Now:HH:mm:ss}  {action}");
        if (_recentActions.Count > 12)
            _recentActions.RemoveAt(12);
        OnPropertyChanged(nameof(RecentActions));
        ActionLogged?.Invoke();
    }
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSourceCommand))]

    [NotifyCanExecuteChangedFor(nameof(StartEncodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditQueueJobCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveDeadAirCommand))]
    private bool _isBusy;

    // ----- Playback / timeline -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double _positionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(TimelineMaximum))]
    private double _durationMs;

    // On by default, together with the Copy encoders: a fresh launch is set up for lossless cutting.
    [ObservableProperty] private bool _snapToIFrames = true;

    // I-frame snapping affects only playhead and razor placement (for Copy export cuts), not encoder choice.

    /// <summary>I-frame timestamps of the loaded source in seconds, ascending. Filled in the background after a load.</summary>
    public List<double> IFrames { get; private set; } = [];

    /// <summary>I-frame status text for the status bar; the indexing result clears after 10 s.</summary>
    [ObservableProperty] private string _iFrameStatusText = "";

    private int _iFrameStatusVersion;

    async partial void OnIFrameStatusTextChanged(string value)
    {
        // Progress text persists while indexing; only the result is transient.
        var version = ++_iFrameStatusVersion;
        if (value.Length == 0 || value.StartsWith("Indexing", StringComparison.Ordinal))
            return;

        await Task.Delay(TimeSpan.FromSeconds(10));
        if (version == _iFrameStatusVersion)
            IFrameStatusText = "";
    }

    public string PositionText => TimeDisplay.Format(PositionMs / 1000);
    public string DurationText => TimeDisplay.Format(DurationMs / 1000);

    // Never zero, so the thumb rests at the left edge while nothing is loaded.
    public double TimelineMaximum => DurationMs > 0 ? DurationMs : 1;

    // ----- Output (Export tab top card) -----

    [ObservableProperty] private string _container = "mp4";
    [ObservableProperty] private bool _webOptimized = true;

    // ----- Dimensions -----

    [ObservableProperty] private string _outputWidth = "";
    [ObservableProperty] private string _outputHeight = "";
    [ObservableProperty] private bool _keepAspectRatio = true;

    // CropTarget's crop as percentages (0..100), resolution-independent (see TakeCropBoxes, ShowCropTarget, CropTarget).
    [ObservableProperty] private double _cropTop;
    [ObservableProperty] private double _cropBottom;
    [ObservableProperty] private double _cropLeft;
    [ObservableProperty] private double _cropRight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPixelAspectRatio))]
    private string _pixelAspectRatio = MatchProject;

    [ObservableProperty] private string _customPixelAspectRatio = "";

    public bool IsCustomPixelAspectRatio => PixelAspectRatio == CustomOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSizeText))]
    private int _sourceWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSizeText))]
    private int _sourceHeight;

    public string SourceSizeText => SourceWidth > 0 && SourceHeight > 0
        ? $"Source: {SourceWidth} x {SourceHeight}. Leave width and height blank to keep the Sequence's own size."
        : "Leave width and height blank to keep the Sequence's own size.";

    // One box can determine both sides, so raise both on each change.
    partial void OnOutputWidthChanged(string value)
    {
        OnPropertyChanged(nameof(FrameWidth));
        OnPropertyChanged(nameof(FrameHeight));
        UpdateLinkedDimension(typedIsWidth: true);
        SyncResolutionPreset();
    }

    partial void OnOutputHeightChanged(string value)
    {
        OnPropertyChanged(nameof(FrameWidth));
        OnPropertyChanged(nameof(FrameHeight));
        UpdateLinkedDimension(typedIsWidth: false);
        SyncResolutionPreset();
    }

    // Anything that changes the shape of the frame re-derives one dimension from the other.
    partial void OnKeepAspectRatioChanged(bool value) => RelinkDimensions();
    partial void OnCropTopChanged(double value) => TakeCropBoxes();
    partial void OnCropBottomChanged(double value) => TakeCropBoxes();
    partial void OnCropLeftChanged(double value) => TakeCropBoxes();
    partial void OnCropRightChanged(double value) => TakeCropBoxes();

    /// <summary>Keeps the width when there is one and recalculates the height; otherwise works from the height.</summary>
    private void RelinkDimensions() =>
        UpdateLinkedDimension(typedIsWidth: !string.IsNullOrWhiteSpace(OutputWidth) || string.IsNullOrWhiteSpace(OutputHeight));

    /// <summary>
    /// With Keep Aspect Ratio, completes the other dimension from the Sequence aspect (transposed under Use Vertical Resolution).
    /// </summary>
    private void UpdateLinkedDimension(bool typedIsWidth)
    {
        if (_syncingDimensions || !KeepAspectRatio || GetSequenceSize() is null)
            return;

        var natural = GetNaturalSize();
        var aspect = (double)natural.Width / natural.Height;

        var typed = typedIsWidth ? OutputWidth : OutputHeight;
        string linked;
        if (string.IsNullOrWhiteSpace(typed))
        {
            linked = "";
        }
        else if (int.TryParse(typed, out var size) && size > 0)
        {
            // Rounded to an even number: most encoders reject odd frame sizes.
            var other = typedIsWidth ? size / aspect : size * aspect;
            linked = Math.Max(2, (int)Math.Round(other / 2) * 2).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            return;
        }

        _syncingDimensions = true;
        try
        {
            if (typedIsWidth)
                OutputHeight = linked;
            else
                OutputWidth = linked;
        }
        finally
        {
            _syncingDimensions = false;
        }
    }

    // ----- Filters -----

    [ObservableProperty] private bool _deinterlace;
    [ObservableProperty] private bool _denoise;

    // ----- Video -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreSoftwareEncoderOptionsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsVideoReencoded))]
    [NotifyPropertyChangedFor(nameof(ShowsCopyNote))]
    [NotifyPropertyChangedFor(nameof(HasRateControl))]
    [NotifyPropertyChangedFor(nameof(IsNvencEncoder))]
    [NotifyPropertyChangedFor(nameof(CanNvencTemporalAq))]
    [NotifyPropertyChangedFor(nameof(IsAmfEncoder))]
    [NotifyPropertyChangedFor(nameof(HasLookahead))]
    [NotifyPropertyChangedFor(nameof(HasTenBit))]
    [NotifyPropertyChangedFor(nameof(CanTenBit))]
    private EncoderOption _videoEncoder;

    /// <summary>Only x264 and x265 take the preset names in the list; anything else would reject them.</summary>
    public bool AreSoftwareEncoderOptionsEnabled => VideoEncoder.Name is "libx264" or "libx265";

    /// <summary>False for stream copy, where quality and frame rate settings do not apply.</summary>
    public bool IsVideoReencoded => VideoEncoder.Family != EncoderFamily.Copy;

    /// <summary>
    /// Shows the Timeline note that Copy ignores edits; only while video is copied and the Sequence is non-empty.
    /// </summary>
    public bool ShowsCopyNote => !IsVideoReencoded && HasSource;

    [ObservableProperty] private string _customFramerate = "";
    public bool IsCustomFramerate => Framerate == CustomOption;
    public bool IsAudioReencoded => AudioEncoder != CopyOption;
    [ObservableProperty] private string _hardwareEncoderStatusText = "";
    /// <summary>Encoder speed step; defaults to the highest-quality step (BestStepOf; veryslow for x264/x265).</summary>
    [ObservableProperty] private string _encoderPreset = "veryslow";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomFramerate))]
    private string _framerate = MatchProject;
    [ObservableProperty] private int _crf = 22;

    [ObservableProperty] private string _tune = NoneOption;
    [ObservableProperty] private string _profile = AutoOption;
    [ObservableProperty] private string _level = AutoOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConstantQuality))]
    [NotifyPropertyChangedFor(nameof(IsBitrateMode))]
    [NotifyPropertyChangedFor(nameof(RateControlChoice))]
    [NotifyPropertyChangedFor(nameof(AutoBitrateText))]
    private string _rateControl = AverageBitrate;

    /// <summary>
    /// Typed target bitrate (kbps) for the bitrate modes; empty (default) means automatic from output size and rate
    /// (<see cref="EffectiveBitrateKbps"/>). A typed bitrate or target size overrides it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveBitrateKbps))]
    [NotifyPropertyChangedFor(nameof(AutoBitrateText))]
    private string _targetBitrate = "";

    public bool IsConstantQuality => RateControl == ConstantQuality;
    public bool IsBitrateMode => !IsConstantQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HdrStatusText))]
    private string _colorspace = MatchProject;

    // ----- Rate Control, under the selected encoder's names -----
    // All encoders share three modes (constant quality, CBR, VBR) stored as RateControl; only names differ, and QSV has two
    // constant-quality kinds. The box lists the encoder's names and maps back to RateControl (+ QsvIcq), so storage is unchanged.

    private const string RcCrf = "CRF";
    private const string RcCq = "CQ";
    private const string RcCqp = "CQP";
    private const string RcIcq = "ICQ";
    private const string RcCbr = "CBR";
    private const string RcVbr = "VBR";

    /// <summary>The rate controls of the selected encoder, by its own names; refilled when the encoder changes.</summary>
    public ObservableCollection<string> RateControlChoices { get; } = [RcCrf, RcCbr, RcVbr];

    /// <summary>
    /// <see cref="RateControl"/> under the selected encoder's name (CBR/VBR are universal; constant quality has four names).
    /// Null while <see cref="UpdateRateControlChoices"/> refills the list.
    /// </summary>
    public string? RateControlChoice
    {
        get => _refillingRateControl ? null : RateControl == ConstantBitrate ? RcCbr
            : RateControl == AverageBitrate ? RcVbr
            : VideoEncoder.Family switch
            {
                EncoderFamily.Nvenc => RcCq,
                EncoderFamily.Amf => RcCqp,
                EncoderFamily.Qsv => QsvIcq ? RcIcq : RcCqp,
                _ => RcCrf,
            };
        set
        {
            // A drop-down whose list is being refilled reports "nothing selected" for a moment.
            if (value is null)
                return;

            if (VideoEncoder.Family == EncoderFamily.Qsv)
                QsvIcq = value == RcIcq;
            RateControl = value == RcCbr ? ConstantBitrate : value == RcVbr ? AverageBitrate : ConstantQuality;
        }
    }

    // Set while the Rate Control list is refilled: see UpdateRateControlChoices.
    private bool _refillingRateControl;

    /// <summary>
    /// Refills the Rate Control box for the selected encoder, keeping the mode under its new name. The choice reads null during
    /// the refill: setting the new name mid-refill (e.g. CQP for CRF) left the ComboBox selected but blank.
    /// </summary>
    private void UpdateRateControlChoices()
    {
        string[] choices = VideoEncoder.Family switch
        {
            EncoderFamily.Nvenc => [RcCq, RcCbr, RcVbr],
            EncoderFamily.Amf => [RcCqp, RcCbr, RcVbr],
            EncoderFamily.Qsv => [RcCqp, RcIcq, RcCbr, RcVbr],
            _ => [RcCrf, RcCbr, RcVbr],
        };

        if (!choices.SequenceEqual(RateControlChoices))
        {
            _refillingRateControl = true;
            try
            {
                RateControlChoices.Clear();
                foreach (var choice in choices)
                    RateControlChoices.Add(choice);
            }
            finally
            {
                _refillingRateControl = false;
            }
        }

        OnPropertyChanged(nameof(RateControlChoice));
    }

    // ----- The selected encoder's own settings -----
    // Encoder-specific options: shown (and emitted) only for encoders that support them. Step and rate control are shared settings.

    private const string NvencHighQuality = "High Quality (hq)";
    private const string NvencLossless = "Lossless";
    private const string NvencSinglePass = "Disabled";
    private const string AmfTranscoding = "Transcoding (transcoding)";

    public IReadOnlyList<string> NvencTunes { get; } = [NvencHighQuality, "Low Latency (ll)", "Ultra Low Latency (ull)", NvencLossless];
    public IReadOnlyList<string> NvencMultipasses { get; } = [NvencSinglePass, "Quarter Resolution (qres)", "Full Resolution (fullres)"];

    public IReadOnlyList<string> AmfUsages { get; } =
    [
        AmfTranscoding, "High Quality (high_quality)", "Low Latency (lowlatency)",
        "Low Latency High Quality (lowlatency_high_quality)", "Ultra Low Latency (ultralowlatency)", "Webcam (webcam)",
    ];

    /// <summary>Maximum lookahead frames for NVENC and QSV.</summary>
    private const int LargestNvencLookahead = 32;
    private const int LargestQsvLookahead = 100;

    public bool IsNvencEncoder => VideoEncoder.Family == EncoderFamily.Nvenc;
    public bool IsAmfEncoder => VideoEncoder.Family == EncoderFamily.Amf;

    /// <summary>NVENC and QuickSync can look ahead; AMF and the software encoders have no such setting here.</summary>
    public bool HasLookahead => VideoEncoder.Family is EncoderFamily.Nvenc or EncoderFamily.Qsv;

    /// <summary>Hardware and the four software encoders accept 10-bit; editing codecs set depth by profile.</summary>
    public bool HasTenBit => VideoEncoder.IsHardware || VideoEncoder.Family == EncoderFamily.Software;

    // NVENC
    [ObservableProperty] private string _nvencTune = NvencHighQuality;
    [ObservableProperty] private string _nvencMultipass = NvencSinglePass;
    [ObservableProperty] private bool _nvencSpatialAq;
    [ObservableProperty] private bool _nvencTemporalAq;

    // AMF
    [ObservableProperty] private string _amfUsage = AmfTranscoding;

    /// <summary>QuickSync: constant quality as ICQ (-global_quality) instead of a constant QP (-q:v). Set by choosing ICQ as the rate control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateControlChoice))]
    private bool _qsvIcq;

    /// <summary>Frames of lookahead for the rate control, for NVENC and QuickSync; 0 leaves it off.</summary>
    [ObservableProperty] private int _lookahead;

    /// <summary>10-bit output instead of 8-bit: p010le for a hardware encoder, yuv420p10le for a software one.</summary>
    [ObservableProperty] private bool _tenBit;

    // Probed capabilities of the selected encoder on this GPU (requesting more fails the encode); software encoders always pass.
    public bool CanNvencTemporalAq => EncoderProber.Supports(VideoEncoder.Name, EncoderProber.TemporalAq);
    public bool CanTenBit => EncoderProber.Supports(VideoEncoder.Name, EncoderProber.TenBit);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HdrStatusText))]
    private bool _isSourceHdr;

    /// <summary>
    /// Note for an HDR source in an SDR project: tone-mapping to BT.709 happens only with HDR to SDR (Tonemap), so it points there.
    /// Empty in HDR projects (ProjectColorNote covers those).
    /// </summary>
    public string HdrStatusText => !IsSourceHdr || Models.Timeline.ProjectSettings.IsHdr(Document.ColorSpace) ? ""
        : Colorspace == TonemapToSdr ? "HDR source in an SDR project: it is tone-mapped to BT.709"
        : "HDR source in an SDR project: choose HDR to SDR (Tonemap) under Colorspace";

    // ----- Audio -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAudioReencoded))]
    private string _audioEncoder = CopyOption;
    [ObservableProperty] private string _audioBitrate = "160k";

    /// <summary>Export sample rate (Hz) for every encoded track; 0 = Auto. Saved in presets and projects.</summary>
    [ObservableProperty] private int _audioSampleRate;

    /// <summary>Export channel layout for every encoded track (<see cref="AudioChannelChoice"/>). Saved in presets and projects.</summary>
    [ObservableProperty] private string _audioChannels = AudioChannelChoice.Stereo;

    public IReadOnlyList<LabeledChoice<int>> AudioSampleRates => AudioChannelChoice.SampleRates;
    public IReadOnlyList<LabeledChoice<string>> AudioChannelChoices => AudioChannelChoice.Choices;

    // ----- Chapters -----

    [ObservableProperty] private bool _chapterMarkers = true;

    // ----- Command preview -----

    private string _commandPreview = "";

    // Dirty flag for the command text, which is generated lazily: on read (export, queue, save of a hand-edited command) and,
    // while Command Preview is visible, once changes settle (GenerateCommand).
    private bool _commandStale;

    /// <summary>The export's command line, as generated from the settings or as edited by hand.</summary>
    public string CommandPreview
    {
        get
        {
            // Stale on read: regenerate now; the box updates on its next notification.
            if (_commandStale && !IsCommandManuallyEdited)
            {
                _commandStale = false;
                _commandPreview = BuildFfmpegCommand();
            }

            return _commandPreview;
        }
        set
        {
            _commandStale = false;
            if (SetProperty(ref _commandPreview, value) && !_writingGeneratedCommand)
                IsCommandManuallyEdited = true;
        }
    }

    /// <summary>Whether Command Preview is visible, enabling eager regeneration on setting changes.</summary>
    public bool CommandPreviewWatched
    {
        get => _commandPreviewWatched;
        set
        {
            _commandPreviewWatched = value;
            if (value && _commandStale)
                ScheduleCommandText();
        }
    }

    private bool _commandPreviewWatched;
    private bool _commandTextQueued;

    /// <summary>Regenerates the preview text at background dispatcher priority: one write per slider drag.</summary>
    private void ScheduleCommandText()
    {
        if (_commandTextQueued || System.Windows.Application.Current?.Dispatcher is not { } dispatcher)
            return;

        _commandTextQueued = true;
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _commandTextQueued = false;
            if (!_commandStale || IsCommandManuallyEdited)
                return;

            _writingGeneratedCommand = true;
            try
            {
                CommandPreview = BuildFfmpegCommand();
            }
            finally
            {
                _writingGeneratedCommand = false;
            }
        });
    }

    /// <summary>Set once the command box is hand-edited; settings changes then leave the command untouched.</summary>
    [ObservableProperty] private bool _isCommandManuallyEdited;

    // ----- Long-running operations -----

    private bool CanStartOperation() => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _operationCancellation?.Cancel();

    /// <summary>Raised after dependency tools are installed or replaced (Settings or first-run window).</summary>
    public void OnDependenciesChanged()
    {
        // A new FFmpeg build may support different hardware encoders.
        _ = ProbeHardwareEncodersAsync();
        _ = CheckDependencyUpdatesAsync();
        OnPropertyChanged(nameof(CaptionHint));

        if (DependencyUpdater.GetMissing().Count == 0 && StatusText.StartsWith("Missing ", StringComparison.Ordinal))
            StatusText = "Dependencies installed.";
    }

    /// <summary>True when yt-dlp or FFmpeg is missing or has a newer release. Shown as a badge on the gear button.</summary>
    [ObservableProperty] private bool _isDependencyUpdateAvailable;

    [ObservableProperty] private string _dependencyStatusText = "";

    /// <summary>Background check behind the badge. Never interrupts: a failed check simply shows no badge.</summary>
    private async Task CheckDependencyUpdatesAsync()
    {
        DependencyStatusText = "Checking for updates...";
        try
        {
            IsDependencyUpdateAvailable = await DependencyUpdater.CheckForUpdatesAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DependencyStatusText = DependencyUpdater.GetMissing() is { Count: > 0 } missing ? $"Not installed: {string.Join(", ", missing)}"
            : IsDependencyUpdateAvailable ? "An update is available."
            : "Everything is up to date.";
    }

    // ----- Command generation -----

    [RelayCommand]
    private void RegenerateCommand()
    {
        IsCommandManuallyEdited = false;
        GenerateCommand();
    }

    // Audio codec in use before an audio-only container forced its own, plus that container.
    private string? _audioEncoderBeforeAudioOnly;
    private string LastAudioOnlyContainer { get; set; } = "";

    partial void OnContainerChanged(string value)
    {
        // GIF/WebP are always encoded; leaving Copy selected would keep size, crop and Filters disabled although they apply.
        if (value is "gif" or "webp" && VideoEncoder.Family == EncoderFamily.Copy)
            VideoEncoder = DefaultVideoEncoder;

        // Audio-only containers force their codec (FLAC, PCM s16, MP3); the video encoder is left unused. The previous codec is
        // restored for video containers (WebM with PCM fails to mux; MP4 would carry it).
        if (EncoderCommand.IsAudioOnlyExtension(value))
        {
            _audioEncoderBeforeAudioOnly ??= AudioEncoder;
            AudioEncoder = EncoderCommand.AudioOnlyCodec(value);
        }
        else if (_audioEncoderBeforeAudioOnly is { } before)
        {
            _audioEncoderBeforeAudioOnly = null;
            if (AudioEncoder == EncoderCommand.AudioOnlyCodec(LastAudioOnlyContainer))
                AudioEncoder = before;
        }

        if (EncoderCommand.IsAudioOnlyExtension(value))
            LastAudioOnlyContainer = value;

        if (!string.IsNullOrWhiteSpace(DestinationPath))
            DestinationPath = Path.ChangeExtension(DestinationPath, value);
        RaiseOutputKind();
    }

    partial void OnDestinationPathChanged(string value) => RaiseOutputKind();

    // Output kind follows container and destination extension; toggles the Export tab's video sections.
    private void RaiseOutputKind()
    {
        OnPropertyChanged(nameof(IsAudioOnlyExport));
        OnPropertyChanged(nameof(IsVideoExport));
        OnPropertyChanged(nameof(CopyBypassWarning));
        if (IsAudioOnlyExport && IsExportQualityPreview)
            IsExportQualityPreview = false;
    }

    // Crop box writes from ShowCropTarget are display-only: no command, proxy or preview rebuild (so playback crossing clips is free).
    private static readonly HashSet<string> CropTargetProperties = [nameof(CropLeft), nameof(CropTop), nameof(CropRight), nameof(CropBottom)];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        // During AttachTab, HasSource is raised once the tab's Sequence is installed, not mid-way.
        if (_holdingSource && e.PropertyName == nameof(HasSource))
        {
            _sourceHeld = true;
            return;
        }

        base.OnPropertyChanged(e);

        // A manual encoder/step choice ends speed-first mode before the command rebuild.
        if (_speedFirst && !_resolvingEncoder && !_placingStep && e.PropertyName is nameof(VideoEncoder) or nameof(EncoderPreset))
            _speedFirst = false;

        if (e.PropertyName is { } name && !NonCommandProperties.Contains(name) && !(_showingTarget && CropTargetProperties.Contains(name)))
            GenerateCommand();

        // A manual encoder choice (not preset/probe) clears a built-in's pending hardware codec.
        if (e.PropertyName == nameof(VideoEncoder) && !_resolvingEncoder)
            _pendingPreferredCodec = null;
    }

    // Bulk updates (preset, undo step) defer the command build to a single rebuild when the outermost deferral ends.
    private int _commandDeferrals;
    private bool _commandPending;

    private CommandDeferral DeferCommand()
    {
        _commandDeferrals++;
        return new CommandDeferral(this);
    }

    private readonly struct CommandDeferral(MainViewModel owner) : IDisposable
    {
        public void Dispose()
        {
            if (--owner._commandDeferrals > 0 || !owner._commandPending)
                return;

            owner._commandPending = false;
            owner.GenerateCommand();
        }
    }

    private void GenerateCommand(PreviewChange change = PreviewChange.Output)
    {
        // A layer following its keyframes as the playhead moves changes nothing about the command.
        if (_applyingKeys)
            return;

        if (_commandDeferrals > 0)
        {
            _commandPending = true;
            return;
        }

        // Recompute the timeline length from its clips (builder inputs are regathered per state) and re-anchor pictures on reshape.
        _previewState = null;

        // Look-only changes (PreviewChange.Look) cannot affect clip layout, output size/rate/length/audio or files: skip the
        // sequence, Projected Output and proxy updates.
        if (change != PreviewChange.Look)
        {
            // Recompute the timeline length from its clips (builder inputs are regathered per state) and re-anchor pictures on reshape.
            KeepPlacesOnReshape();
            RefreshSequence();

        // The Properties tab follows the settings even while the command itself is frozen by a manual edit.
            UpdateProjectedOutput();

            // Update proxies for the current files.
            RefreshProxies();
            WarmLayerFiles();
        }

        OnPropertyChanged(nameof(CaptionHint));
        OnPropertyChanged(nameof(CopyBypassWarning));
        OnPropertyChanged(nameof(ProjectColorNote));
        OnPropertyChanged(nameof(HdrStatusText));

        // Refresh the player (and Timeline rows unless look-only); it renders from settings, not the command.
        if (!_isBackgroundWorker)
            InvalidatePreviewState(change);

        if (IsCommandManuallyEdited)
            return;

        // Text is generated lazily (CommandPreview).
        _commandStale = true;
        if (CommandPreviewWatched)
            ScheduleCommandText();
    }

    /// <summary>Output frame rate, or null for the Sequence rate (Match Project Settings). Custom values may be any positive number.</summary>
    private string? GetTargetFramerate()
    {
        var text = Framerate switch
        {
            MatchProject => "",
            CustomOption => CustomFramerate.Trim(),
            _ => Framerate,
        };

        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fps) && fps > 0
            ? fps.ToString("0.###", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Writes the Copy export concat list (SequenceSpans.CopyCutOf: the dominant file, one entry per clip with in/out points).
    /// Copied video must start on an I-frame, so each entry starts at the I-frame at or before its in point.
    /// </summary>
    private string _writtenCuts = "";

    private void WriteCutsFile(SequenceExportState state)
    {
        // The command is rebuilt for every setting that changes; the file is only written when the clips have.
        var text = EncoderCommand.BuildCutsList(state);
        if (text == _writtenCuts && File.Exists(CutsFilePath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CutsFilePath)!);
            File.WriteAllText(CutsFilePath, text);
            _writtenCuts = text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"Could not write {CutsFilePath}: {ex.Message}", $"Write the cuts file {CutsFilePath}", ex);
        }
    }

    /// <summary>
    /// Pixel aspect as "n/d" for the -aspect value (EncoderCommand.SampleAspect), or null for square pixels.
    /// Match Project Settings uses the project's pixel aspect.
    /// </summary>
    private string? GetSampleAspectRatio()
    {
        var ratio = PixelAspectRatio switch
        {
            MatchProject => Models.Timeline.ProjectSettings.SampleAspectOf(Document.PixelAspect) ?? "",
            CustomOption => CustomPixelAspectRatio.Trim(),
            _ => PixelAspectRatio,
        };

        var parts = ratio.Split(':', '/');
        return parts.Length == 2 && int.TryParse(parts[0], out var numerator) && int.TryParse(parts[1], out var denominator)
               && numerator > 0 && denominator > 0
            ? $"{numerator}/{denominator}"
            : null;
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quote(string value) => $"\"{value}\"";
}
