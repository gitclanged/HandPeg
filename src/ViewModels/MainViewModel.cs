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
    [NotifyCanExecuteChangedFor(nameof(RenderPreviewCommand))]
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

    // Whether the hardware encoders were put in the list before the window's first frame (TakeEarlyHardwareEncoders).
    private bool _hardwareEncodersEarly;

    /// <summary>
    /// The hardware encoders as an earlier probe found them for this FFmpeg and these drivers (read on another thread
    /// while the window was being made), put in the list before the window's first frame: the Export tab then opens with
    /// the encoder it keeps, where a hardware default used to show as Copy, with Copy's warning, until the probe
    /// answered. Not there yet, or none saved: the probe after the first frame does it, as before.
    /// </summary>
    public void TakeEarlyHardwareEncoders()
    {
        if (_isBackgroundWorker || EncoderProber.TakeEarlyResult(TimeSpan.FromMilliseconds(100)) is not { } saved)
            return;

        ++_hardwareProbeRun;
        _hardwareEncodersEarly = true;
        ApplyHardwareEncoders(saved.Supported, saved.Problems);
    }

    /// <summary>
    /// The encoders taken before the first frame were matched on FFmpeg alone (EncoderProber.LoadSavedResultEarly): the
    /// cards and drivers are compared now, away from this thread, and a change since the probe that found them probes again.
    /// </summary>
    private async Task RecheckEarlyEncodersAsync()
    {
        // The cards are listed there too (off this thread, after the first frame): a probe problem of a vendor whose card is not in this
        // PC ("h264_nvenc: Cannot load nvcuda.dll" on an AMD machine, from an encoder-probe.json of before 5.4B) is then left unsaid.
        var same = await Task.Run(() =>
        {
            var still = EncoderProber.CardsStillMatch();
            _ = GpuAdapters.All;
            return still;
        });
        if (!same)
            await ProbeHardwareEncodersAsync();
        else
            ShowHardwareEncoderStatus();
    }

    /// <summary>Tests the GPU encoders in the background and offers the working ones in the Export tab.</summary>
    private async Task ProbeHardwareEncodersAsync()
    {
        // A newer probe (after an install or a settings change) makes the result of an older one stale.
        var run = ++_hardwareProbeRun;

        if (!File.Exists(DependencyUpdater.FfmpegPath))
        {
            HardwareEncoderStatusText = "Install FFmpeg (in Settings) to detect hardware encoders";
            return;
        }

        HardwareEncoderStatusText = "Checking hardware encoders...";
        List<string> supported;
        List<string> problems;
        try
        {
            // Found before for this FFmpeg and these drivers: not tested again (see EncoderProber.LoadSavedResult).
            (supported, problems) = await Task.Run(EncoderProber.LoadSavedResult) is { } saved
                ? saved
                : await WorkScheduler.Run(new WorkSpec("Testing hardware encoders", WorkLane.Idle), work => EncoderProber.ProbeAsync(work.Token), _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (run == _hardwareProbeRun)
                HardwareEncoderStatusText = $"Hardware encoder check failed: {ex.Message}";
            return;
        }

        if (run != _hardwareProbeRun)
            return;

        ApplyHardwareEncoders(supported, problems);
    }

    /// <summary>Puts the hardware encoders found into the list, keeping the selection where it still exists.</summary>
    private void ApplyHardwareEncoders(List<string> supported, List<string> problems)
    {
        // Rebuild the hardware part of the list, keeping the selection where it still exists. The first time
        // round, a hardware encoder set as the default is selected, unless another was chosen in the meantime.
        var untouched = VideoEncoder.Family is EncoderFamily.Copy || VideoEncoder == DefaultVideoEncoder;
        var selected = _pendingDefaultEncoder is { } pending && untouched ? pending : VideoEncoder.Name;
        _pendingDefaultEncoder = null;
        foreach (var stale in VideoEncoders.Where(e => e.IsHardware).ToList())
            VideoEncoders.Remove(stale);
        foreach (var name in supported)
            VideoEncoders.Add(EncoderOption.FromHardwareName(name));
        _hardwareEncodersListed = true;
        _resolvingEncoder = true;
        try
        {
            VideoEncoder = VideoEncoders.FirstOrDefault(e => e.Name == selected) ?? DefaultVideoEncoder;

            // A built-in applied before this list was there fell back to software (5.4C): its codec's hardware encoder now, once, at its
            // slowest step (UpdateEncoderPresets starts a hardware family there).
            if (_pendingPreferredCodec is { } wanted && !VideoEncoder.IsHardware
                && ResolveEncoder(new EncodingPreset { PreferredCodec = wanted, VideoEncoder = VideoEncoder.Name }) is ({ IsHardware: true } hardware, _))
            {
                VideoEncoder = hardware;
                selected = hardware.Name;
            }
        }
        finally
        {
            _resolvingEncoder = false;
            _pendingPreferredCodec = null;
        }

        // The default encoder preset is one of the hardware encoder's own steps, which exist now that it does. A speed-first preset
        // (5.4D, Fast Proxy) applied before the probe keeps its fastest step on the encoder it resolves to now, after that default.
        _placingStep = true;
        try
        {
            if (selected == AppSettings.Current.DefaultVideoEncoder && EncoderPresets.Contains(AppSettings.Current.DefaultEncoderPreset))
                EncoderPreset = AppSettings.Current.DefaultEncoderPreset;
            if (_speedFirst && FastestStepOf(VideoEncoder) is { } fastest && EncoderPresets.Contains(fastest))
                EncoderPreset = fastest;
        }
        finally
        {
            _placingStep = false;
        }

        (_hardwareEncoderCount, _hardwareProblems) = (supported.Count, problems);
        ShowHardwareEncoderStatus();

        // What the card can do is known now, for the encoder that is selected as for the others.
        OnPropertyChanged(nameof(CanNvencTemporalAq));
        OnPropertyChanged(nameof(CanTenBit));
        GenerateCommand();

        // Offering an older FFmpeg is not part of putting the list up: it waits for the window to be drawn (this may run
        // before its first frame).
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            if (EncoderProber.NvencNeedsOlderFfmpeg)
                OfferFfmpegForOlderNvidiaDrivers();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // What the last probe (or the result saved by one) found: how many hardware encoders work, and why the others did not start.
    private int _hardwareEncoderCount;
    private List<string> _hardwareProblems = [];

    /// <summary>
    /// The detected encoders' line on the Export tab: how many work, and an encoder FFmpeg has but could not start (usually a driver
    /// that needs updating). A problem of a vendor whose card is not in this PC is left out once the cards are listed (5.4B,
    /// EncoderProber.ProblemsForCardsHere: never listed for it before the first frame, Law 1; RecheckEarlyEncodersAsync says it again after).
    /// </summary>
    private void ShowHardwareEncoderStatus()
    {
        var text = _hardwareEncoderCount switch
        {
            0 => "No hardware encoders available on this system",
            1 => "1 hardware encoder available",
            _ => $"{_hardwareEncoderCount} hardware encoders available",
        };
        var problems = EncoderProber.ProblemsForCardsHere(_hardwareProblems);
        HardwareEncoderStatusText = problems.Count > 0 ? $"{text}. Not usable: {string.Join("; ", problems)}" : text;
    }

    /// <summary>
    /// NVENC did not start because this FFmpeg asks for a newer NVIDIA driver than the one installed. A card that
    /// still gets drivers needs a driver update; one that does not (the GTX 10 series and older) needs the older
    /// FFmpeg build, which the next FFmpeg update then installs.
    /// </summary>
    private void OfferFfmpegForOlderNvidiaDrivers()
    {
        if (DependencyUpdater.IsFfmpegOverridden)
        {
            HardwareEncoderStatusText += ". NVENC needs a newer NVIDIA driver, or an FFmpeg 8.1 build in place of the one set in Settings.";
            return;
        }

        var settings = AppSettings.Current;
        if (!settings.UseFfmpegForOlderNvidiaDrivers)
        {
            settings.UseFfmpegForOlderNvidiaDrivers = true;
            try
            {
                settings.SaveAsCurrent();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The choice still holds for this session.
            }

            AppLog.Write("NVENC needs a newer NVIDIA driver than is installed, so FFmpeg updates now install the build for older drivers.");
            _ = CheckDependencyUpdatesAsync();
        }

        HardwareEncoderStatusText += ". NVENC needs a newer NVIDIA driver, or the FFmpeg build for older drivers: update FFmpeg in Settings to install it.";
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task LoadSourceAsync()
    {
        // A project file (a .hproj, or a HandPeg 1.x project recognised by its shape), typed in or dropped, opens the project.
        if (ProjectStore.IsProjectFile(SourcePath.Trim().Trim('"')))
        {
            await LoadProjectAsync(SourcePath.Trim().Trim('"'));
            return;
        }

        // A web address is fetched by yt-dlp, which is downloaded the first time one is loaded.
        if (IsWebAddress(SourcePath) && !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return;

        await LoadSourceNowAsync();
    }

    /// <summary>Whether a source is a web address (http or https), which yt-dlp downloads, rather than a file.</summary>
    public static bool IsWebAddress(string source) =>
        Uri.TryCreate(source.Trim().Trim('"'), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Downloads a web address with yt-dlp (or finds the file downloaded for it before), for a link dropped where a
    /// file is wanted: on the timeline, or on a Squisher target. Returns the file; null when nothing came of it.
    /// </summary>
    public async Task<string?> DownloadLinkAsync(string url)
    {
        if (IsBusy || !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return null;

        string? path = null;
        await RunOperationAsync("Downloading a link", WorkLane.Interactive, async cancellationToken =>
        {
            IsProgressIndeterminate = true;
            StatusText = "Starting yt-dlp...";
            var progress = new Progress<string>(ReportStatus);
            var (resolution, subtitles, (sponsorBlock, markSponsorBlock), folder) = (DownloadResolution, DownloadSubtitles, AppSettings.Current.SponsorBlockRequest, AppSettings.Current.DownloadFolder);
            path = await Task.Run(() => YtDlpDownloader.DownloadAsync(url, resolution, subtitles, sponsorBlock, markSponsorBlock, folder, progress, cancellationToken), cancellationToken);
            return $"Downloaded {Path.GetFileName(path)}";
        });

        return path is not null && File.Exists(path) ? path : null;
    }

    private Task LoadSourceNowAsync() => RunOperationAsync("Loading the source", WorkLane.Interactive, async cancellationToken =>
    {
        var source = SourcePath.Trim().Trim('"');
        if (source.Length == 0)
            return null;

        // Loading something by hand ends the editing of a queued job.
        EditingJob = null;
        var (opened, message) = await LoadMediaAsync(source, knownLocalPath: null, applyAutomation: true, cancellationToken);

        // Settings: every video starts as a vertical one (5.4A: unless the New Project studio chose the frame on purpose).
        if (opened && AppSettings.Current.DefaultVerticalVideo && !UseVerticalResolution && !Document.ExplicitFrame)
            UseVerticalResolution = true;
        ForgetHistory();
        MarkSaved();
        return message;
    });

    /// <summary>Loads a file chosen outside the source box (dropped on the window, for instance).</summary>
    public void LoadFile(string path)
    {
        SourcePath = path;
        LoadSourceCommand.Execute(null);
    }

    /// <summary>As <see cref="LoadFile"/>, for what follows once it is open.</summary>
    public Task LoadFileAsync(string path)
    {
        SourcePath = path;
        return LoadSourceCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Loads a file and gives it a Game Preset: what dropping a video on a Game Preset in the launch window does.
    /// </summary>
    public async Task LoadFileWithStyleAsync(string path, string stylePath)
    {
        // A link dropped on a Game Preset is downloaded first, with yt-dlp, which may itself have to be downloaded.
        if (IsWebAddress(path) && !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return;

        await LoadFileWithStyleNowAsync(path, stylePath);
    }

    private Task LoadFileWithStyleNowAsync(string path, string stylePath) => RunOperationAsync("Loading with a Game Preset", WorkLane.Interactive, async cancellationToken =>
    {
        SourcePath = path;
        EditingJob = null;
        var (loaded, message) = await LoadMediaAsync(path, knownLocalPath: null, applyAutomation: true, cancellationToken);
        if (!loaded)
            return message;

        if (ReadStyle(stylePath) is not { } style)
            return $"{message}. {StatusText}";

        ApplyStyle(style, layout: true, color: true, blur: true, subtitles: true);
        MarkSaved();
        return $"{message}. Game Preset \"{Path.GetFileNameWithoutExtension(stylePath)}\" applied.";
    });


    /// <summary>
    /// Makes a source the current one: downloads it if it is a URL, inspects it and starts playback.
    /// </summary>
    /// <param name="knownLocalPath">A file already downloaded for this source, used instead of downloading again.</param>
    /// <param name="applyAutomation">Whether the smart rules and the default preset from the settings apply.</param>
    /// <param name="place">Whether a clip of it goes on the timeline (PlaceOpenedFile); not for a project, whose timeline is its own.</param>
    private async Task<(bool Loaded, string Message)> LoadMediaAsync(
        string source, string? knownLocalPath, bool applyAutomation, CancellationToken cancellationToken, bool place = true)
    {
        string localPath;
        if (File.Exists(source))
        {
            localPath = Path.GetFullPath(source);
        }
        else if (!string.IsNullOrWhiteSpace(knownLocalPath) && File.Exists(knownLocalPath))
        {
            localPath = knownLocalPath;
        }
        else if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (DependencyUpdater.GetMissing() is { Count: > 0 } missing)
                return (false, $"Downloading needs {string.Join(", ", missing)}. Install them from Settings (the gear button) first.");

            IsProgressIndeterminate = true;
            StatusText = "Starting yt-dlp...";
            // The progress object is made here, on the UI thread, so its reports come back to it;
            // the download itself, and the handling of yt-dlp's output, run on the thread pool.
            var progress = new Progress<string>(ReportStatus);
            var (resolution, subtitles, (sponsorBlock, markSponsorBlock), folder) = (DownloadResolution, DownloadSubtitles, AppSettings.Current.SponsorBlockRequest, AppSettings.Current.DownloadFolder);
            localPath = await Task.Run(
                () => YtDlpDownloader.DownloadAsync(source, resolution, subtitles, sponsorBlock, markSponsorBlock, folder, progress, cancellationToken),
                cancellationToken);
        }
        else
        {
            return (false, $"Source not found: {source}");
        }

        // A crash report never names the file, not even on its own (a failed probe's message may).
        CrashReporter.KeepOut(localPath);
        // What the user is waiting on: never queued behind the media browser's posters and strips (4.3E).
        var mediaInfo = await MediaProbe.ProbeAsync(localPath, cancellationToken, urgent: true);
        var previous = LocalMediaPath;
        ResetForNewSource();
        LocalMediaPath = localPath;
        SetMediaInfo(mediaInfo);
        NoteHevcDecoder(mediaInfo?.Video);

        // A clip of the whole file on the timeline, as a dropped file is made (5.2-AuditB): on an empty Sequence at its start, or
        // where the clips of the file opened before were (the other clips stay).
        (_openedOver, _openedClip) = (null, null);
        if (place)
            PlaceOpenedFile(previous);

        // For a crash report: what kind of file this is (container, codecs, size, rate, length), never which.
        var mediaSummary = CrashSummaries.Media(localPath, mediaInfo, Layers.Count);
        CrashReporter.SetContext("media", mediaSummary);
        CrashReporter.Note("media", "opened: " + mediaSummary);
        DevFlightRecorder.Record("media", $"opened {localPath}: {mediaSummary}");

        // Every new source gets its own default name, so one encode never lands on top of the previous one.
        DestinationPath = Path.Combine(
            GetDefaultOutputFolder(localPath), $"{Path.GetFileNameWithoutExtension(localPath)}_HandPeg.{Container}");

        var message = $"Loaded {localPath}";
        if (applyAutomation && AppSettings.Current.FindPresetFor(localPath, source) is { } presetName)
        {
            if (Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase)) is { } preset)
            {
                // Applied whole (5.4B: the rule's preset keeps everything it holds, its layout too; the selector applies an Export
                // Preset's export settings only), then shown as the one selected. It goes on the clip of the file just opened, not on
                // what is selected on the Timeline.
                _templateTargetOnly = _openedClip;
                try
                {
                    ApplyPreset(preset);
                    _selectingAppliedPreset = true;
                    SelectedPreset = preset;
                }
                finally
                {
                    (_templateTargetOnly, _selectingAppliedPreset) = (null, false);
                }

                message += $". Preset \"{preset.Name}\" applied automatically.";
            }
            else
            {
                message += $". The automatic preset \"{presetName}\" no longer exists.";
            }
        }

        // A track named for a microphone or a voice is the Voice track (4.3E), unless the preset above marked one: only for a
        // file loaded anew (a project or a queued job is restored without automation, and keeps its tracks as saved).
        if (applyAutomation && MarkVoiceTrack() is { } voice)
            message = $"{message.TrimEnd('.')}. {voice}";

        // Layers marked on a video of another shape will not sit on the same things in this one.
        if (GetLayoutAspectWarning() is { } warning)
            message += $". {warning}";

        // Playback, the I-frame index and hover previews are for the window; a background instance needs none of them.
        if (!_isBackgroundWorker)
        {
            MediaLoaded?.Invoke(localPath);
            _ = ScanIFramesAsync(localPath);
            _ = GenerateSpriteSheetAsync(localPath);
            _ = GenerateWaveformsAsync(localPath);
        }
        return (true, message);
    }

    /// <summary>The source's own folder, or the Videos library when that folder is not a sensible place to write.</summary>
    private static string GetDefaultOutputFolder(string sourcePath)
    {
        var folder = Path.GetDirectoryName(sourcePath);
        return folder is not null && IsUserWritableFolder(folder)
            ? folder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    private static bool IsUserWritableFolder(string folder)
    {
        string[] systemRoots =
        [
            Path.GetTempPath(),

            // HandPeg's own working folders: a video downloaded into the cache is not saved back into it.
            AppPaths.Temp,
            AppPaths.DataRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        foreach (var root in systemRoots.Where(r => r.Length > 0))
        {
            var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (normalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // The only reliable test for write access is to try it.
        try
        {
            using (File.Create(Path.Combine(folder, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Indexes the source's I-frames without holding up playback or other operations.</summary>
    private async Task ScanIFramesAsync(string path)
    {
        _iFrameScanCancellation?.Cancel();
        var cancellation = _iFrameScanCancellation = new CancellationTokenSource();

        SetSourceIFrames([]);
        IFrameStatusText = "Indexing I-frames...";
        try
        {
            // On the thread pool: ffprobe prints a line per I-frame, and reading thousands of them
            // should not happen on the UI thread.
            var iFrames = await Task.Run(() => FfmpegRunner.GetIFramesAsync(path, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            SetSourceIFrames(iFrames);
            IFrameStatusText = iFrames.Count > 0
                ? $"{iFrames.Count} I-frames indexed"
                : "No I-frame index: cut points will not snap";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            IFrameStatusText = $"I-frame indexing failed: {ex.Message}";
        }
        finally
        {
            // Released here, once nothing uses its token any more, rather than by whoever cancels it.
            if (ReferenceEquals(_iFrameScanCancellation, cancellation))
                _iFrameScanCancellation = null;
            cancellation.Dispose();
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use; it is in the temp folder and will be cleaned up another time.
        }
    }

    /// <summary>Picks up new tool paths after the settings window was saved.</summary>
    public void OnSettingsSaved()
    {
        OnPropertyChanged(nameof(EditLayersOnPlayer));
        OnPropertyChanged(nameof(SponsorBlockMode));

        // The Timeline's switches the Settings window has as well (5.3): this view model's copies of them follow what was saved, so the
        // Snap button, the Timeline Settings menu and the blocks show it (each raised only when it changed).
        var saved = AppSettings.Current;
        ShowClipKeyframes = saved.ShowClipKeyframes;
        SnapClips = saved.SnapClips;
        SnapToBeats = saved.SnapToBeats;
        ShowBeatGrid = saved.ShowBeatGrid;

        // The I-frame lines as well, without what switching them by hand does (AutoToggleTimelineSnap turning the timeline thumb's snap
        // to I-frames with them): what was saved already holds the snap the user left in the window.
        _resyncingShowIFrames = true;
        try
        {
            ShowIFrames = saved.ShowIFrames;
        }
        finally
        {
            _resyncingShowIFrames = false;
        }

        // The proxy settings may name another height, format or place: proxies are looked for again under them.
        _proxies.Clear();
        PreviewCache.Proxies.QueueTrim();

        ApplyDisplaySettings();
        LoadAutomation();
        GenerateCommand();
        _ = ProbeHardwareEncodersAsync();
        _ = CheckDependencyUpdatesAsync();

        // A cache made smaller, or switched off (which empties it), is brought to its new size.
        PreviewCache.Timeline.QueueTrim();
        WorkScheduler.ApplySettings();
        PreviewCache.Browser.QueueTrim();

        var missing = DependencyUpdater.GetMissing();
        StatusText = missing.Count > 0
            ? $"Settings saved. Missing {string.Join(", ", missing)}."
            : "Settings saved.";
    }

    /// <summary>Stops whatever is still running when the application closes.</summary>
    public void Shutdown()
    {
        ForgetProjectProxies(now: true);
        _operationCancellation?.Cancel();
        _iFrameScanCancellation?.Cancel();
        _shutdown.Cancel();
        _spriteCancellation?.Cancel();
        _waveformCancellation?.Cancel();

        // A background instance only owns its own working folder; the shared ones belong to the window.
        if (_isBackgroundWorker)
        {
            DeleteFolder(_workFolder);
            return;
        }

        // Cut lists, thumbnail sheets, previews, queue files: everything this session wrote, in one go.
        SessionPaths.DeleteSession();
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task StartEncodeAsync()
    {
        // FFmpeg, and for captions that are still to be heard whisper and its model: fetched now if they never were.
        if (await RequireForCommandAsync("Start Encode"))
            await RunEncodeAsync();
    }

    private Task RunEncodeAsync() => RunOperationAsync("Exporting", WorkLane.Foreground, async cancellationToken =>
    {
        if (string.IsNullOrWhiteSpace(CommandPreview))
            return "There is no command to run.";

        // Nothing open and nothing on the timeline: the command names a stand-in for a source, which FFmpeg cannot open.
        if (!HasSource && !(SequenceSeconds > 0) && !IsCommandManuallyEdited)
            return "There is nothing to export: open a video or a project first.";

        // A Copy export that cannot cut is refused here, before anything is asked or started (5.2-Arm64AndNpu, the user's call).
        if (CopyRefusal() is { } refused)
            return refused;

        // Copy keeps the picture as it is: said once more before the time is spent, when there is work it would skip.
        if (CopyBypassWarning.Length > 0 && !IsCommandManuallyEdited && Confirm is not null
            && !Confirm("Stream Copy", $"{CopyBypassWarning}\n\nThe video is copied as it is: no clip's effects, crop or place, nothing set on the Filters tab, and no resize reaches the output. To apply them, choose a video codec on the Export tab.", "Encode Anyway"))
        {
            return "Encode cancelled.";
        }

        if (!ResolveOverwrite())
            return "Encode cancelled: the existing file was left untouched.";

        // Read after the overwrite question: renaming the target changes the command.
        var command = CommandPreview;

        // Auto-captions: listen, transcribe and write the subtitle file the command is about to draw.
        if (await PrepareCaptionsForEncodeAsync(command, cancellationToken) is { } captionProblem)
            return captionProblem;

        // Progress is measured against what the output should contain (see GetOutputDuration).
        var expected = TimeSpan.FromSeconds(Math.Max(GetOutputDuration(), 0.001));

        IsProgressIndeterminate = true;
        StatusText = "Starting FFmpeg...";
        SetTaskbarProgress(TaskbarItemProgressState.Normal);

        var progress = new Progress<FfmpegProgress>(report =>
        {
            if (!_acceptProgressReports)
                return;

            if (expected <= TimeSpan.Zero && report.InputDuration is { } inputDuration)
                expected = inputDuration;

            if (report.Position is { } position && expected > TimeSpan.Zero)
            {
                IsProgressIndeterminate = false;
                ProgressValue = Math.Clamp(position / expected * 100, 0, 100);
                SetTaskbarProgress(TaskbarItemProgressState.Normal, ProgressValue / 100);
            }

            if (report.Line.Length > 0)
                StatusText = report.Line;
        });

        try
        {
            // For a crash report: the encoders, the output's kind and length, never the files (the output's name included).
            CrashReporter.KeepOut(DestinationPath);
            var exportSummary = CrashSummaries.Command(command, expected);
            CrashReporter.SetContext("export", "running: " + exportSummary);
            CrashReporter.Note("export", "started: " + exportSummary);
            await RunExportAsync(command, ExportStateForGpu(), progress, cancellationToken);
            CrashReporter.Note("export", "finished");
            NotifyFinished("Encode complete", Path.GetFileName(DestinationPath.Trim().Trim('"')));
            return _exportLeftOut.Length > 0 ? $"Encode complete. {_exportLeftOut}" : "Encode complete.";
        }
        catch (InvalidOperationException ex) when (
            ex is not FfmpegCommandException
            && AppSettings.Current.AutoFallbackToSoftware
            && HardwareFallback.TryRewrite(command, out _, out _, out _))
        {
            // The hardware encoder let us down: one more attempt on the software encoder for the same codec.
            HardwareFallback.TryRewrite(command, out var retry, out var hardware, out var software);
            AppLog.Write($"{hardware} failed and {software} is tried instead: {ex.Message}");
            StatusText = $"Warning: {hardware} failed. Retrying with {software}...";
            Notifier.Show("Hardware encoder failed", $"{hardware} could not encode this video. HandPeg is trying again with {software}.");

            // Reflect the switch in the Export tab where that is possible; a hand-edited command is patched in place.
            if (!IsCommandManuallyEdited && VideoEncoders.FirstOrDefault(e => e.Name == software) is { } replacement)
            {
                PlaceSoftwareRetry(replacement, command);
                retry = CommandPreview;
            }
            else
            {
                CommandPreview = retry;
            }

            ProgressValue = 0;
            IsProgressIndeterminate = true;
            SetTaskbarProgress(TaskbarItemProgressState.Normal);
            CrashReporter.Note("export", $"{hardware} failed, retried with {software}");
            // The retry is the GPU's too where the settings make it (the encoder switched on the Export tab); a patched command is FFmpeg's.
            await RunExportAsync(retry, ExportStateForGpu(), progress, cancellationToken);
            CrashReporter.Note("export", "finished");
            NotifyFinished("Encode complete", Path.GetFileName(DestinationPath.Trim().Trim('"')));
            return $"Encode complete on {software}: the hardware encoder {hardware} failed, so the software encoder was used.";
        }
        finally
        {
            // Finished, failed or cancelled: the taskbar button goes back to plain.
            SetTaskbarProgress(TaskbarItemProgressState.None);
            CrashReporter.SetContext("export", null);
        }
    });

    /// <summary>
    /// A hardware failure's software retry on the Export tab (5.4D): <paramref name="replacement"/> put in place as HandPeg's own pick, not
    /// one by hand (a speed-first state holds), at its fastest step when the export was speed-first or the failed command carried its
    /// vendor's fastest step (HardwareFallback.HadFastestStep: a step picked by hand too), else at its own default, the slowest. The same
    /// rule as HardwareFallback.TryRewrite (5.4D review): the same failure retries at the same step, the command hand-edited or not.
    /// </summary>
    private void PlaceSoftwareRetry(EncoderOption replacement, string failedCommand)
    {
        _placingStep = true;
        try
        {
            VideoEncoder = replacement;
            if ((_speedFirst || HardwareFallback.HadFastestStep(failedCommand)) && FastestStepOf(replacement) is { } fastest && EncoderPresets.Contains(fastest))
                EncoderPreset = fastest;
        }
        finally
        {
            _placingStep = false;
        }
    }

    /// <summary>
    /// Asks what to do when the target file already exists. Returns false when the user cancelled;
    /// after a rename, Save As and the command point at the new name.
    /// </summary>
    private bool ResolveOverwrite()
    {
        var destination = DestinationPath.Trim().Trim('"');
        if (destination.Length == 0 || AskOverwrite is null || !File.Exists(destination)
            || !CommandPreview.Contains(destination, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var decision = AskOverwrite(destination);
        if (decision.Choice == OverwriteChoice.Cancel)
            return false;

        if (decision.Choice == OverwriteChoice.Rename)
        {
            // A generated command follows Save As by itself; a hand-edited one has to be patched.
            var manualCommand = IsCommandManuallyEdited ? CommandPreview : null;
            DestinationPath = decision.NewPath;
            if (manualCommand is not null)
                CommandPreview = manualCommand.Replace(destination, decision.NewPath, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    /// <summary>
    /// Runs one operation at a time, and turns its outcome (returned message, cancellation or failure) into status text.
    /// It is a piece of work in its lane (see WorkScheduler): shown by the progress chip with the progress and the
    /// status it reports here, and cancelled from the chip's flyout as by the Cancel button.
    /// </summary>
    /// <param name="title">What the progress chip calls it.</param>
    private async Task RunOperationAsync(string title, WorkLane lane, Func<CancellationToken, Task<string?>> operation)
    {
        if (IsBusy)
            return;

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _acceptProgressReports = true;
        IsBusy = true;
        CrashReporter.Note("op", "started: " + title);
        var ended = "done";

        string? outcome;
        try
        {
            outcome = await WorkScheduler.Run(new WorkSpec(title, lane), async work =>
            {
                // What the operation says of itself (the progress bar's value, the status line) is what its chip says.
                void Mirror(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName is nameof(ProgressValue) or nameof(IsProgressIndeterminate))
                        work.Report(IsProgressIndeterminate ? -1 : ProgressValue / 100);
                    else if (e.PropertyName == nameof(StatusText) && _acceptProgressReports)
                        work.Report(StatusText);
                }

                using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, work.Token);
                PropertyChanged += Mirror;
                try
                {
                    return await operation(either.Token);
                }
                finally
                {
                    PropertyChanged -= Mirror;
                }
            }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled.";
            ended = "cancelled";
        }
        catch (Exception ex)
        {
            outcome = $"Error: {ex.Message}";
            ended = "failed: " + ex.GetType().Name;
        }

        CrashReporter.Note("op", title + ": " + ended);

        // Progress callbacks are posted, so some can still arrive after the operation has ended.
        _acceptProgressReports = false;
        _operationCancellation = null;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        IsBusy = false;
        if (outcome is not null)
            StatusText = outcome;
    }

    private void ReportStatus(string message)
    {
        if (_acceptProgressReports && !string.IsNullOrWhiteSpace(message))
            StatusText = message.Trim();
    }

    /// <summary>Clears per-source state when a different source is loaded.</summary>
    private void ResetForNewSource()
    {
        ForgetProjectProxies();
        PositionMs = 0;
        DurationMs = 0;

        // A file opened goes on the timeline where any does: at the beginning of the timeline, whole (PlaceOpenedFile, once it has
        // been looked at). The other clips stay; the bin and the markers were the old file's.
        SetBin(null);
        SetMarkers(null);
        Document.Markers = [];
        _previewState = null;
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
