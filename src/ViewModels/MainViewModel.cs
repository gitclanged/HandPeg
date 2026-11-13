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
    /// The first choice of Export's Framerate, Colorspace and Pixel Aspect (5.4A, which retired "Same as source"): what the project's
    /// settings say (MainViewModel.ProjectSettings.cs). A preset or project saved with "Same as source" reads as it (Pick).
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
        // 5.4D: the preview's Magnifier is a viewer preference.
        nameof(ExportQualityPreviewLoupe),
        // 5.4B: the automatic bitrate's views follow the settings that build the command (raised as it is built, UpdateProjectedOutput).
        nameof(EffectiveBitrateKbps), nameof(AutoBitrateText),
        // 5.4C: the output's kind only shows or folds the Export tab's video parts.
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
        // 5.4.4: the speed's step (the speed pill's value) is only another view of PlaybackSpeed.
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
        // The Studio Audio Suite (5.2B): the master gain is an edit of the document (its commit builds the command), the rest are views.
        nameof(MasterGainDb), nameof(ShowMasterMeter), nameof(SnapToBeats), nameof(ShowBeatGrid),
    ];


    private readonly CancellationTokenSource _shutdown = new();

    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _iFrameScanCancellation;
    private bool _acceptProgressReports;
    private bool _writingGeneratedCommand;
    private bool _syncingDimensions;
    private int _hardwareProbeRun;

    // A background instance has no window: it exists to turn a saved project into a queue job
    // while the visible one is busy, and must not touch anything the visible one is using.
    private readonly bool _isBackgroundWorker;

    public MainViewModel()
    {
        StartupTrace.Mark("MainViewModel: start");
        _videoEncoder = EncoderOption.Copy;
        Presets = [.. ExportPresetCatalog.BuiltIns, .. PresetStore.TakeEarly()];
        Initialize();

        var missing = DependencyUpdater.GetMissing();
        if (missing.Count > 0)
            StatusText = $"Missing {string.Join(", ", missing)}. Open Settings (the gear button) to install them.";
        StartupTrace.Mark("MainViewModel: made");

        // Neither is part of getting the window up: testing the encoders starts FFmpeg, looking for updates
        // brings in the whole of networking. Both begin once the window has been laid out and drawn.
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

        // Its own folder for the cuts and chapters lists, which are rewritten whenever a setting changes.
        _workFolder = Path.Combine(SessionPaths.Queue, $"worker_{Guid.NewGuid():N}");

        _videoEncoder = EncoderOption.Copy;
        foreach (var encoder in owner.VideoEncoders.Where(e => e.IsHardware))
            VideoEncoders.Add(encoder);
        Presets = [.. owner.Presets];
        Initialize();
    }

    private void Initialize()
    {
        // What a new window starts with, as chosen in the first-run window (and kept in the settings).
        // Set as fields: nothing is listening yet, and nothing should react as if the user had changed them.
        var settings = AppSettings.Current;
#pragma warning disable MVVMTK0034
        _snapToIFrames = settings.StartWithSnapToIFrames;

        // The tracks a video is given are mixed into one, which is what an edit is exported as.
        _mergeAudioTracks = true;
        (_chapterMarkers, _chaptersAtCuts) = (settings.StartWithChapterMarkers, settings.StartWithChaptersAtCuts);

        // The encoder chosen in the first-run window. A hardware one is only in the list once the probe
        // has found it, so until then it waits; see ProbeHardwareEncodersAsync.
        if (settings.DefaultVideoEncoder.Length > 0)
        {
            if (VideoEncoders.FirstOrDefault(e => e.Name == settings.DefaultVideoEncoder) is { } chosen)
                _videoEncoder = chosen;
            else
                _pendingDefaultEncoder = settings.DefaultVideoEncoder;
        }

        // And the audio settings chosen there, so that sound is encoded rather than copied from the start.
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
        Layers.CollectionChanged += (_, _) => RefreshLayerRows();
        RefreshLayerRows();
        LoadAutomation();
        Presets.CollectionChanged += (_, _) => RefreshPresetNames();
        CaptionLayer.PropertyChanged += (_, _) => GenerateCommand();

        // The preview's captions are drawn in the box where and as large as it is, with its opacity and its
        // shadow: a preview of them follows the box being moved, resized or restyled.
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

        // The voiceover's place in the Master Mix View follows the voiceover, its trim and the cuts.
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

    // The default encoder from the settings, while it is a hardware one the probe has not reported on yet.
    private string? _pendingDefaultEncoder;

    // 5.4C, the probe race: the codec(s) a built-in Export Preset wanted on hardware ("hevc"), when it was applied before the hardware
    // probe had listed any encoder and so fell back to its software one; ApplyHardwareEncoders resolves it again once, unless an encoder
    // was picked by hand since (OnPropertyChanged lets go of it). _resolvingEncoder marks the encoder set by a preset or the probe.
    private string? _pendingPreferredCodec;
    private bool _resolvingEncoder;
    private bool _hardwareEncodersListed;

    // 5.4D, Q2 (b): a speed-first preset (EncodingPreset.SpeedFirst: Fast Proxy) applied: its encoder at the fastest step (FastestStepOf;
    // Media Foundation's -compression_level 0), the probe's re-pick too, until an encoder or a step is picked by hand or another preset
    // is applied. _placingStep marks a step set by HandPeg itself (the probe's default step, a hardware failure's software retry).
    private bool _speedFirst;
    private bool _placingStep;

    /// <summary>Whether a speed-first preset's fastest step holds (5.4D: for the golden tests and the command).</summary>
    internal bool SpeedFirstHolds => _speedFirst;

    /// <summary>Raised with a local file path when a source is ready to be played.</summary>
    public event Action<string>? MediaLoaded;

    /// <summary>A question with two ways of going ahead: 1 for the first, 2 for the second, 0 for neither.</summary>
    public Func<string, string, string, string, int>? Choose { get; set; }

    /// <summary>Asks the user to confirm something (title, message, what the confirming button says). Returning false calls it off.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }

    /// <summary>
    /// Asks the user to confirm something that can be answered once for all (title, message, what the confirming button
    /// says, what the declining one says), with a "Remember my choice" box: whether it was confirmed, and whether that
    /// answer is to be given from now on without asking (4.4).
    /// </summary>
    public Func<string, string, string, string, (bool Confirmed, bool Remember)>? ConfirmOrRemember { get; set; }

    /// <summary>Asked before an encode replaces an existing file. Returning false aborts the encode.</summary>
    public Func<string, OverwriteDecision>? AskOverwrite { get; set; }

    /// <summary>
    /// Draws the Sequence at the playhead as a project's thumbnail, a JPEG (Pass 5.2D: the window's GPU engine,
    /// MainWindow.GpuPreview.cs); null when it cannot (no engine, playing, nothing open). Called on the window's thread, and nothing it
    /// does after the call needs that thread: a save waits for it there a moment (CaptureStateForSave). Unset for a view model with no
    /// window, whose saves carry no thumbnail.
    /// </summary>
    public Func<Task<byte[]?>>? CaptureThumbnail { get; set; }

    /// <summary>The concat list used for I-frame-snapped cuts.</summary>
    public string CutsFilePath => Path.Combine(_workFolder, "cuts.txt");

    // ----- Option lists -----

    public IReadOnlyList<string> DownloadResolutions { get; } = ["Best", "2160p", "1440p", "1080p", "720p", "480p", "360p"];
    /// <summary>The containers offered, the Save dialog's filter in the same order; the sound-only files last (5.4C: an Audio Only export).</summary>
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
    /// <summary>The three ways of spending data, as presets and projects store them. The Export tab shows them under the selected encoder's own names: see <see cref="RateControlChoices"/>.</summary>
    public IReadOnlyList<string> RateControls { get; } = [ConstantQuality, AverageBitrate, ConstantBitrate];
    public IReadOnlyList<string> Colorspaces { get; } = [MatchProject, TonemapToSdr];

    // ----- Presets -----

    /// <summary>The Export Presets (5.4B): the built-in ones (ExportPresetCatalog), then the user's own (presets.json).</summary>
    public ObservableCollection<EncodingPreset> Presets { get; }

    [ObservableProperty] private EncodingPreset? _selectedPreset;
    [ObservableProperty] private string _presetName = "";

    // Set while a preset is selected after being applied whole (a Smart Rule's): the selection then applies nothing again.
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
    /// Saves the export settings, with the rest of what a preset holds, under the name in the preset box, as one of the user's own
    /// ("My Presets"). A built-in's name is not taken: the preset is saved as "Name (Custom)" (5.4B).
    /// </summary>
    // The "Name (Custom)" presets SavePreset made from a built-in this session (the ones a later save of the same tweak replaces).
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

        // The "Name (Custom)" this session saved from the built-in is not in the way: saving the tweak again replaces it, never adds
        // "(Custom 2)". One that came from elsewhere (an older preset of that name the upgrade renamed, an import) is kept.
        if (ExportPresetCatalog.IsBuiltInName(name))
        {
            name = PresetStore.UniqueName(name, Presets.Where(p => !p.IsBuiltIn && !_savedFromBuiltIn.Contains(p.Name)));
            _savedFromBuiltIn.Add(name);
        }
        var preset = CaptureSettings(name);

        // Saving under an existing name replaces that preset (never a built-in: its name is not taken).
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
            StatusText = $"Could not write {PresetStore.FilePath}: {ex.Message}";
            return;
        }

        SelectedPreset = preset;
        Log($"Saved preset \"{name}\"");
    }

    /// <summary>Reads presets.json again: for when it was replaced from outside, by an import.</summary>
    public void ReloadPresets()
    {
        SelectedPreset = null;
        Presets.Clear();
        foreach (var preset in ExportPresetCatalog.BuiltIns.Concat(PresetStore.Load()))
            Presets.Add(preset);
    }

    /// <summary>
    /// Applies the whole of a preset: its export settings (ApplyEncodingPart) and, with <paramref name="withLayout"/>, its size, layout,
    /// crop, captions, filters and colours. A project's settings are put back this way (withLayout false), and a Smart Rule's preset
    /// (5.4B: picking an Export Preset in the selector applies its export settings only, ApplyExportPreset).
    /// </summary>
    /// <param name="withLayout">False for a project's own settings: its timeline (its clips, their looks, places and crops) is the project's, not a preset's.</param>
    private void ApplyPreset(EncodingPreset preset, bool withLayout = true)
    {
        // Some sixty settings change here; the command is built once, when they have all been set.
        using var whole = DeferCommand();

        // Width and height are set as stored, without one recalculating the other on the way.
        _syncingDimensions = true;
        try
        {
            KeepAspectRatio = preset.KeepAspectRatio;

            // Presets from before the option existed say nothing, and are not tall.
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

        // A preset that names both sides means exactly that size; one side alone is completed from the shape.
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
        // Its layout (app data, not a project), 5.2-AuditB: a template on the selected video clips, or every top-level one (its
        // layers their Cutouts, its video look theirs, its old Center values their place; with no clip, the next file opened takes
        // it), when it carries one. A project's settings carry none: its timeline is its own (RestoreStateAsync puts it in place after this).
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
    /// A preset's export settings (both ways of applying one share them): the encoder (a codec it prefers on this PC's hardware first,
    /// ResolveEncoder) and its step (none named: its best, BestStepOf), tune, profile and level, rate control, quality and bitrate, frame
    /// rate (capped where it says), colourspace, the encoder's own settings, 10-bit, the sound (codec, bitrate, rate, channels, mix,
    /// normalize and its target), the target size, the tracks, the container and Web Optimized. Whether the encoder it names was found.
    /// </summary>
    private bool ApplyEncodingPart(EncodingPreset preset)
    {
        // The encoder comes before its preset step: which steps exist depends on the encoder.
        // A preset may name a hardware encoder this machine does not have; then the current one stays.
        // Another preset ends a speed-first one's state (5.4D); this one's starts once its encoder and step are in place.
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

        // A built-in that wants a codec on hardware, applied before the probe listed the hardware encoders (5.4C): resolved again then.
        _pendingPreferredCodec = !_hardwareEncodersListed && preset.IsBuiltIn && !encoder.IsHardware && preset.PreferredCodec is { Length: > 0 } wanted ? wanted : null;

        // A preset that names no step leaves the encoder at its own default: its best (5.4B: veryslow for x264 and x265 too).
        if (EncoderPresets.Contains(preset.EncoderPreset))
            EncoderPreset = preset.EncoderPreset;
        else if (preset.EncoderPreset.Length == 0)
            EncoderPreset = BestStepOf(VideoEncoder) ?? "veryslow";

        // A speed-first preset (5.4D, Fast Proxy) takes the fastest step of the encoder it resolved to, whatever the family; on one with no
        // steps here (Media Foundation) the command says it (-compression_level 0).
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

        // A platform's most frames a second: the project's rate while it is under it, the cap where it is above.
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

        // Presets saved while NVENC had a lookahead and a 10-bit box of its own carry them under its name.
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

        // A preset only speaks for the target size when it was saved with one.
        if (!string.IsNullOrWhiteSpace(preset.TargetFileSize))
            TargetFileSize = preset.TargetFileSize;

        // Every track starts from the codec and bitrate above (which also undoes changes made track by
        // track since), then takes whatever the preset recorded for that track number.
        ApplyAudioDefaultsToTracks();
        ApplyAudioTrackStates(preset.AudioTracks ?? []);

        if (preset.Container is { } container && Containers.Contains(container))
            Container = container;
        if (preset.WebOptimized is { } webOptimized)
            WebOptimized = webOptimized;
        return encoderFound;
    }

    /// <summary>
    /// Puts back per-track output choices (what is made of each, its codec, bitrate and title) for the tracks that exist; states
    /// for missing tracks are ignored. With <paramref name="legacyMixing"/> (a project of 5.1 being opened), also what it mixed each of the
    /// main video's streams with: the input of its conversion (ConvertLegacy), which sets it back to neutral.
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

    /// <summary>What the button in the Source pane's box does: with nothing typed there is nothing to load, and it browses for a file.</summary>
    public string SourceActionText => string.IsNullOrWhiteSpace(SourcePath) ? "Browse" : "Load";
    [ObservableProperty] private string _downloadResolution = "Best";
    [ObservableProperty] private string _destinationPath = "";

    /// <summary>
    /// The file last opened (the source itself, or the yt-dlp download for a URL): what the Info, Streams and Download panels
    /// describe, what the default output and project names come from, and whose own subtitle streams and chapters the export
    /// names (SequenceExportState.StreamSourcePath). Never read to render the Sequence: every clip reads its own file
    /// (5.2-AuditB); opening it puts a clip of it on the timeline like any dropped file (PlaceOpenedFile). "" for none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    [NotifyPropertyChangedFor(nameof(ShowsCopyNote))]
    private string _localMediaPath = "";

    // ----- Status bar -----

    // Empty until something happens (5.3-Stage3, the user's call): the title bar's middle shows nothing at start.
    [ObservableProperty] private string _statusText = "";

    // The action log: what was just done, said in a few words in the status bar and flashed there, so that
    // every edit answers at once. The last few stay readable in the status bar's tooltip.

    /// <summary>Raised when something that was done has been written to the status bar.</summary>
    public event Action? ActionLogged;

    private readonly List<string> _recentActions = [];

    /// <summary>The last actions, the latest first, a line each with the time it was done.</summary>
    public string RecentActions => _recentActions.Count > 0 ? string.Join(Environment.NewLine, _recentActions) : "Nothing has been done yet.";

    /// <summary>Says what was just done: in the status bar, flashed, and kept among the recent actions.</summary>
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

    // Snapping only concerns where the playhead and the razor sit (on I-frames of the main
    // video, for a Copy export's cuts). Which encoders are used is a separate choice.

    /// <summary>I-frame timestamps of the loaded source in seconds, ascending. Filled in the background after a load.</summary>
    public List<double> IFrames { get; private set; } = [];

    /// <summary>Shown in the status bar. What the indexing came to is cleared again after ten seconds.</summary>
    [ObservableProperty] private string _iFrameStatusText = "";

    private int _iFrameStatusVersion;

    async partial void OnIFrameStatusTextChanged(string value)
    {
        // "Indexing I-frames..." stays for as long as that takes; only its outcome is passing news.
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

    // ----- Output (the Export tab's top card, 5.4A) -----

    [ObservableProperty] private string _container = "mp4";
    [ObservableProperty] private bool _webOptimized = true;

    // ----- Dimensions -----

    [ObservableProperty] private string _outputWidth = "";
    [ObservableProperty] private string _outputHeight = "";
    [ObservableProperty] private bool _keepAspectRatio = true;

    // The crop, as percentages (0 to 100) of a picture's height and width. Being relative, a crop set on one recording cuts the
    // same part out of a recording of any other resolution. The crop of the clip the Crop boxes stand for (4.4: each clip has its
    // own; see TakeCropBoxes, ShowCropTarget and CropTarget).
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

    // One box alone also decides the other side of the frame, so both sides are announced each time.
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
    /// With "Keep Aspect Ratio" on, fills in the other dimension from the one just typed, using the shape of the Sequence's own
    /// size: as it is, or on its side with Use Vertical Resolution.
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
    /// The Timeline's note that Copy renders nothing of the edit: only while the video is copied and there is something to export
    /// (5.2E, the user's report: an empty Sequence of a copy that never chose an encoder showed it).
    /// </summary>
    public bool ShowsCopyNote => !IsVideoReencoded && HasSource;

    [ObservableProperty] private string _customFramerate = "";
    public bool IsCustomFramerate => Framerate == CustomOption;
    public bool IsAudioReencoded => AudioEncoder != CopyOption;
    [ObservableProperty] private string _hardwareEncoderStatusText = "";
    /// <summary>The encoder's speed step: its best to start with (5.4B, CLAUDE.md 8: veryslow for x264 and x265, BestStepOf).</summary>
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
    /// Target bitrate in kbps as typed, used by the two bitrate modes; empty (5.4B, the default) for the automatic one worked out from the
    /// output's size and rate (<see cref="EffectiveBitrateKbps"/>). Typing a bitrate or a target size takes over from it.
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
    // Every encoder spends data in the same three ways (a constant quality, a bitrate held, a bitrate
    // averaged), and RateControl says which. What differs is what each encoder calls them, and QuickSync has
    // two kinds of constant quality. So the one Rate Control box lists the selected encoder's own names, and
    // what is chosen there is turned into RateControl (and, for QuickSync, QsvIcq): presets and projects go on
    // storing what they always have.

    private const string RcCrf = "CRF";
    private const string RcCq = "CQ";
    private const string RcCqp = "CQP";
    private const string RcIcq = "ICQ";
    private const string RcCbr = "CBR";
    private const string RcVbr = "VBR";

    /// <summary>The rate controls of the selected encoder, by its own names; refilled when the encoder changes.</summary>
    public ObservableCollection<string> RateControlChoices { get; } = [RcCrf, RcCbr, RcVbr];

    /// <summary>
    /// What <see cref="RateControl"/> is called by the selected encoder. CBR and VBR are the same everywhere; the constant
    /// quality has four names. Null while the list is being refilled (<see cref="UpdateRateControlChoices"/>).
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
    /// Refills the Rate Control box for the selected encoder; what is chosen stays chosen, under its new name. While the
    /// list is refilled the choice reads as nothing: the box writes nothing into it when its item goes, WPF reads it
    /// straight back, and the new encoder's name (CQP for CRF) handed to the box in the middle of its own refill left it
    /// showing nothing, though selected (Pass 4.1B; found with the Video tab of then made before the first frame).
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
    // Each is shown, in the one row under the shared settings, only while an encoder that has it is selected,
    // and only then reaches the command. The speed step and the rate control are not among them: those are
    // Encoder Preset and Rate Control, which every encoder shares.

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

    /// <summary>The most frames NVENC looks ahead, and the most QuickSync does.</summary>
    private const int LargestNvencLookahead = 32;
    private const int LargestQsvLookahead = 100;

    public bool IsNvencEncoder => VideoEncoder.Family == EncoderFamily.Nvenc;
    public bool IsAmfEncoder => VideoEncoder.Family == EncoderFamily.Amf;

    /// <summary>NVENC and QuickSync can look ahead; AMF and the software encoders have no such setting here.</summary>
    public bool HasLookahead => VideoEncoder.Family is EncoderFamily.Nvenc or EncoderFamily.Qsv;

    /// <summary>The hardware encoders and the four software ones take 10-bit frames; the editing codecs set their depth by profile.</summary>
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

    // What the selected encoder can do on this card, as the probe found it: asking for more stops the encode.
    // (The probe only ever turns a hardware encoder down, so a software one can always.)
    public bool CanNvencTemporalAq => EncoderProber.Supports(VideoEncoder.Name, EncoderProber.TemporalAq);
    public bool CanTenBit => EncoderProber.Supports(VideoEncoder.Name, EncoderProber.TenBit);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HdrStatusText))]
    private bool _isSourceHdr;

    /// <summary>
    /// A word under the encoder about an HDR file in an SDR project: tone-mapped to BT.709 only under HDR to SDR (Tonemap), so with
    /// Match Project Settings it says where to turn that on. None in an HDR project, whose colour note says how it is written (5.4B:
    /// kept HDR, or tone-mapped and why: ProjectColorNote).
    /// </summary>
    public string HdrStatusText => !IsSourceHdr || Models.Timeline.ProjectSettings.IsHdr(Document.ColorSpace) ? ""
        : Colorspace == TonemapToSdr ? "HDR source in an SDR project: it is tone-mapped to BT.709"
        : "HDR source in an SDR project: choose HDR to SDR (Tonemap) under Colorspace";

    // ----- Audio -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAudioReencoded))]
    private string _audioEncoder = CopyOption;
    [ObservableProperty] private string _audioBitrate = "160k";

    /// <summary>The export sample rate of every encoded track, in Hz; 0 is Auto (4.1.1). Saved in presets and projects.</summary>
    [ObservableProperty] private int _audioSampleRate;

    /// <summary>The export channels of every encoded track (<see cref="AudioChannelChoice"/>; 4.1.1). Saved in presets and projects.</summary>
    [ObservableProperty] private string _audioChannels = AudioChannelChoice.Stereo;

    public IReadOnlyList<LabeledChoice<int>> AudioSampleRates => AudioChannelChoice.SampleRates;
    public IReadOnlyList<LabeledChoice<string>> AudioChannelChoices => AudioChannelChoice.Choices;

    // ----- Chapters -----

    [ObservableProperty] private bool _chapterMarkers = true;

    // ----- Command preview -----

    private string _commandPreview = "";

    // Set when the settings have changed since the command text was last written. The text (the whole command
    // with its filter graph, tens of kilobytes on a large project) is written only when it is wanted: when it
    // is read (an export, the queue, a project saved with a hand-edited command), and, while the Command Preview
    // box is on screen, once the changes have stopped coming (see GenerateCommand).
    private bool _commandStale;

    /// <summary>The export's command line, as generated from the settings or as edited by hand.</summary>
    public string CommandPreview
    {
        get
        {
            // Read while out of date: written now, for whoever is reading. (The box shows it when it is next told.)
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

    /// <summary>Whether the Command Preview box is on screen, so that the command is written as the settings change.</summary>
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

    /// <summary>Writes the command text for the box once the dispatcher has nothing more pressing: one write for a whole drag of a slider.</summary>
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

    /// <summary>
    /// Set once the user types in the command box. While set, settings changes leave the command alone.
    /// </summary>
    [ObservableProperty] private bool _isCommandManuallyEdited;

    // ----- Long-running operations -----

    private bool CanStartOperation() => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _operationCancellation?.Cancel();

    /// <summary>Tools were installed or replaced from the dependency list (in the settings, or the first-run window).</summary>
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

    // The audio codec chosen before an audio-only container took its own, and that container (5.4C review).
    private string? _audioEncoderBeforeAudioOnly;
    private string LastAudioOnlyContainer { get; set; } = "";

    partial void OnContainerChanged(string value)
    {
        // GIF and WebP are always encoded. Leaving the encoder on Copy would keep the size, crop
        // and Filters tabs switched off even though their settings now apply.
        if (value is "gif" or "webp" && VideoEncoder.Family == EncoderFamily.Copy)
            VideoEncoder = DefaultVideoEncoder;

        // A sound-only file (5.4C) is written with its own codec: FLAC, 16-bit PCM, MP3 (the video encoder is left as it is, unused).
        // The codec it had is kept and comes back when a video container is chosen again (5.4C review: a WebM left with PCM failed in
        // the muxer, an MP4 was written with it).
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

    // The output's kind follows the container and the destination's extension: the Export tab's video parts show or fold (5.4C).
    private void RaiseOutputKind()
    {
        OnPropertyChanged(nameof(IsAudioOnlyExport));
        OnPropertyChanged(nameof(IsVideoExport));
        OnPropertyChanged(nameof(CopyBypassWarning));
        if (IsAudioOnlyExport && IsExportQualityPreview)
            IsExportQualityPreview = false;
    }

    // The Crop boxes, which only show the clip they stand for while ShowCropTarget sets them: that clip is as it was, so the
    // command, the proxies and the preview are too (the playhead crossing into another clip during playback rebuilds nothing).
    private static readonly HashSet<string> CropTargetProperties = [nameof(CropLeft), nameof(CropTop), nameof(CropRight), nameof(CropBottom)];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        // A tab coming back (AttachTab, 5.4D-TabPerf review P12): HasSource is told once its own Sequence is in place, never on the way.
        if (_holdingSource && e.PropertyName == nameof(HasSource))
        {
            _sourceHeld = true;
            return;
        }

        base.OnPropertyChanged(e);

        // An encoder or a step picked by hand ends a speed-first preset's fastest step (5.4D), before the command is built again.
        if (_speedFirst && !_resolvingEncoder && !_placingStep && e.PropertyName is nameof(VideoEncoder) or nameof(EncoderPreset))
            _speedFirst = false;

        if (e.PropertyName is { } name && !NonCommandProperties.Contains(name) && !(_showingTarget && CropTargetProperties.Contains(name)))
            GenerateCommand();

        // An encoder picked by hand (not a preset's, nor the probe's own re-pick) lets go of a built-in's hardware wish (5.4C).
        if (e.PropertyName == nameof(VideoEncoder) && !_resolvingEncoder)
            _pendingPreferredCodec = null;
    }

    // While a whole set of settings is being put in place (a preset, an undo step) the command is not built
    // for each of them: it is built once, when the last deferral ends.
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

    private void GenerateCommand()
    {
        // A layer following its keyframes as the playhead moves changes nothing about the command.
        if (_applyingKeys)
            return;

        if (_commandDeferrals > 0)
        {
            _commandPending = true;
            return;
        }

        // The timeline is as long as its clips reach (what the builder reads from it is gathered afresh for this state), and
        // every picture keeps its middle on a frame of another shape.
        _previewState = null;
        KeepPlacesOnReshape();
        RefreshSequence();

        // The Properties tab follows the settings even while the command itself is frozen by a manual edit.
        UpdateProjectedOutput();
        OnPropertyChanged(nameof(CaptionHint));
        OnPropertyChanged(nameof(CopyBypassWarning));
        OnPropertyChanged(nameof(ProjectColorNote));
        OnPropertyChanged(nameof(HdrStatusText));

        // The proxies follow the files now on it.
        RefreshProxies();
        WarmLayerFiles();

        // So does the player (and the Timeline's rows), which shows the settings, not the command.
        if (!_isBackgroundWorker)
            InvalidatePreviewState();

        if (IsCommandManuallyEdited)
            return;

        // The text itself is written when it is wanted (see CommandPreview).
        _commandStale = true;
        if (CommandPreviewWatched)
            ScheduleCommandText();
    }

    /// <summary>
    /// The frame rate to convert to, or null to keep the Sequence's own (the project's rate: "Match Project Settings"). Custom values may
    /// be any positive number.
    /// </summary>
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
    /// Writes the concat list a Copy export reads (4.3C; 5.2-AuditB: the clips of the file the Sequence shows most of,
    /// SequenceSpans.CopyCutOf: the file named once per clip with its in and out points in it). Copied video can only start on an I-frame, so
    /// a clip's stretch begins at the I-frame at or before its in point.
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
            StatusText = $"Could not write {CutsFilePath}: {ex.Message}";
        }
    }

    /// <summary>
    /// The value for setsar, or null to leave the pixel shape alone (square). Written with a slash:
    /// a colon would be read as the separator between filter options. "Match Project Settings" is the project's pixel aspect (5.4A).
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
