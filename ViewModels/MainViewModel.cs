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

namespace HandPegApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const string SameAsSource = "Same as source";
    private const string CustomOption = "Custom";
    private const string CopyOption = "Copy (Stream Copy)";
    private const string NoneOption = "None";
    private const string AutoOption = "Auto";
    private const string ConstantQuality = "Constant Quality (CQ)";
    private const string AverageBitrate = "Average Bitrate (ABR)";
    private const string ConstantBitrate = "Constant Bitrate (CBR)";
    private const string TonemapToSdr = "HDR to SDR (Tonemap)";

    // Properties that have no influence on the generated FFmpeg command.
    private static readonly HashSet<string> NonCommandProperties =
    [
        nameof(CommandPreview), nameof(IsCommandManuallyEdited), nameof(StatusText), nameof(SourcePath), nameof(DownloadResolution),
        nameof(PositionMs), nameof(PositionText), nameof(DurationMs), nameof(DurationText), nameof(TimelineMaximum),
        nameof(PendingStartMs), nameof(PendingStartText), nameof(SelectedSegment),
        nameof(IFrameStatusText), nameof(Volume), nameof(EditingJob), nameof(IsEditingQueuedJob),
        nameof(SourceWidth), nameof(SourceHeight), nameof(SourceSizeText), nameof(IsCustomPixelAspectRatio),
        nameof(HardwareEncoderStatusText), nameof(AreSoftwareEncoderOptionsEnabled), nameof(IsVideoReencoded),
        nameof(IsAudioReencoded), nameof(IsCustomFramerate), nameof(IsConstantQuality), nameof(IsBitrateMode),
        nameof(IsSourceHdr), nameof(HdrStatusText), nameof(SelectedPreset), nameof(PresetName),
        nameof(DownloadSubtitles), nameof(AudioTracksHint), nameof(SubtitleTracksHint), nameof(HasRateControl),
        nameof(QueueButtonText), nameof(IsQueuePauseRequested),
        nameof(IsDependencyUpdateAvailable), nameof(DependencyStatusText),
        nameof(IsInteractiveCropActive), nameof(TargetSizeHint), nameof(PlaybackSpeed), nameof(CanSetPixelAspect), nameof(HasEncoderPresets),
        nameof(FrameWidth), nameof(FrameHeight), nameof(ShowIFrames),
        nameof(CaptionAudioPath), nameof(ShowAutoCaptions), nameof(ShowStandardSubtitles), nameof(CaptionHint), nameof(CaptionStyleSummary),
        nameof(DefaultPresetChoice),
        nameof(TimelineWaveform), nameof(SelectedMicrophone), nameof(IsRecording), nameof(IsRecordingPaused), nameof(VoiceoverStatus),
        nameof(HasVoiceover), nameof(VoiceoverWaveform), nameof(VoiceoverDuration), nameof(VoiceoverTrimText), nameof(PlayOnlySegments),
        nameof(ResolutionPreset), nameof(ShowPresetBarAtTop), nameof(ShowPresetBarInSummary),
        nameof(TimelineAreaHeight), nameof(AudioTrackRowHeight), nameof(ShowWaveforms),
        nameof(WhisperPrompt), nameof(WhisperLanguage), nameof(WhisperTranslate), nameof(CanEditLayout),
        nameof(CaptionUseExternalAudio), nameof(CaptionUseVideoAudio), nameof(CaptionAudioTrack), nameof(IsPlaying),
        nameof(ShowAdvancedPlayback), nameof(ShowSimplePlayback),
        nameof(ShowCommandPreviewTab), nameof(ShowAdvancedFiltersTab),
        nameof(DrawTargetLayer), nameof(IsArrangeActive), nameof(SpriteSheetPath),
        nameof(IsBusy), nameof(ProgressValue), nameof(IsProgressIndeterminate),
        nameof(ShowTimelineThumbnails), nameof(ShowHoverPreviews), nameof(HasSource), nameof(SoloTrack),
        nameof(VoiceoverMixWaveform), nameof(VoiceoverMixStart), nameof(VoiceoverMixWidth), nameof(LivePreview),
        nameof(IsEditorMode), nameof(CopyBypassWarning), nameof(ModeButtonText), nameof(ShowLinkedAudio), nameof(ShowTimelineOptionsBelow), nameof(HasAudioClips), nameof(IsEncoderMode),
        nameof(KeyframesEnabled), nameof(KeyframesSnap), nameof(KeyframesCreate), nameof(KeyLayer), nameof(ActiveKeyLayer),
        nameof(SelectedStylePreset), nameof(SelectedProject), nameof(StyleName),
        nameof(MasterTimelineHeight), nameof(LayerTrackHeight), nameof(AudioTrackHeight), nameof(LayerBarHeight),
        nameof(ShowCutSegmentsPane), nameof(ShowKeyframesPane), nameof(ShowClipKeyframes), nameof(HasDeleted), nameof(RecycleBinText),
        nameof(RecentActions), nameof(HideDroppedTracks), nameof(HasSelectedKeyframe), nameof(SelectedKeyEasing), nameof(PreviewSubtitles),
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
        _videoEncoder = EncoderOption.Copy;
        Presets = [.. PresetStore.Load()];
        Initialize();

        var missing = DependencyUpdater.GetMissing();
        if (missing.Count > 0)
            StatusText = $"Missing {string.Join(", ", missing)}. Open Settings (the gear button) to install them.";

        _ = ProbeHardwareEncodersAsync();
        _ = CheckDependencyUpdatesAsync();
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
        (_snapToIFrames, _playOnlySegments) = (settings.StartWithSnapToIFrames, settings.StartWithPlayOnlySegments);

        // Editor Mode mixes the tracks it is given into one, which is what an edit is exported as.
        _mergeAudioTracks = settings.UiMode == AppSettings.EditorMode;
        (_chapterMarkers, _chaptersAtCuts) = (settings.StartWithChapterMarkers, settings.StartWithChaptersAtCuts);
        // In Editor Mode the Layer Engine is simply how the picture is composed; there is nothing to switch on.
        _frameEngine = settings.StartWithFrameEngine || settings.UiMode == AppSettings.EditorMode;

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
#pragma warning restore MVVMTK0034

        UpdateEncoderPresets();

        InitializeQueue();
        ApplyDisplaySettings();
        Layers.CollectionChanged += (_, _) => RefreshLayerRows();
        RefreshLayerRows();
        LoadAutomation();
        Presets.CollectionChanged += (_, _) => RefreshPresetNames();
        AudioTracks.CollectionChanged += (_, _) => KeepCaptionTrackValid();
        CaptionLayer.PropertyChanged += (_, _) => GenerateCommand();

        // The caption file is written for the box as large as it is: a preview of it follows the box being resized.
        CaptionLayer.PropertyChanged += (_, e) =>
        {
            if (PreviewSubtitles && e.PropertyName is nameof(Layer.SizeWidth) or nameof(Layer.SizeHeight))
                RefreshLiveCaptionsSoon();
        };
        _mainVideoRow.PropertyChanged += OnMainRowChanged;
        AudioTracks.CollectionChanged += (_, _) => RefreshAudioRows();
        _backgroundRow.PropertyChanged += (_, _) => GenerateCommand();
        Layers.CollectionChanged += (_, _) => GenerateCommand();
        Segments.CollectionChanged += (_, _) => GenerateCommand();
        GenerateCommand();
        OpenLayoutPaneIfWanted();

        // The voiceover's place in the Master Mix View follows the voiceover, its trim and the cuts.
        if (!_isBackgroundWorker)
        {
            Segments.CollectionChanged += (_, _) => UpdateVoiceoverMix();
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

    /// <summary>Raised with a local file path when a source is ready to be played.</summary>
    public event Action<string>? MediaLoaded;

    /// <summary>Asks the user to confirm something (title, message, what the confirming button says). Returning false calls it off.</summary>
    /// <summary>A question with two ways of going ahead: 1 for the first, 2 for the second, 0 for neither.</summary>
    public Func<string, string, string, string, int>? Choose { get; set; }

    public Func<string, string, string, bool>? Confirm { get; set; }

    /// <summary>Asked before an encode replaces an existing file. Returning false aborts the encode.</summary>
    public Func<string, OverwriteDecision>? AskOverwrite { get; set; }

    /// <summary>The concat list used for I-frame-snapped cuts.</summary>
    public string CutsFilePath => Path.Combine(_workFolder, "cuts.txt");

    // ----- Option lists -----

    public IReadOnlyList<string> DownloadResolutions { get; } = ["Best", "2160p", "1440p", "1080p", "720p", "480p", "360p"];
    public IReadOnlyList<string> Containers { get; } = ["mp4", "mkv", "webm", "mov", "gif", "webp"];
    public IReadOnlyList<string> PixelAspectRatios { get; } = [SameAsSource, "1:1", "4:3", "16:9", CustomOption];

    /// <summary>Software encoders, followed by whichever hardware encoders passed the probe.</summary>
    public ObservableCollection<EncoderOption> VideoEncoders { get; } = [EncoderOption.Copy, .. EncoderOption.Software];

    private static EncoderOption DefaultVideoEncoder => EncoderOption.Software[0];
    public IReadOnlyList<string> Framerates { get; } = [SameAsSource, "23.976", "24", "25", "29.97", "30", "48", "50", "59.94", "60", "120", "144", "240", CustomOption];
    public IReadOnlyList<string> AudioEncoders { get; } = [CopyOption, .. AudioTrack.AllCodecs];
    public IReadOnlyList<string> AudioBitrates => AudioTrack.AllBitrates;
    public IReadOnlyList<string> Tunes { get; } = [NoneOption, "film", "animation", "grain", "fastdecode", "zerolatency"];
    public IReadOnlyList<string> Profiles { get; } = [AutoOption, "baseline", "main", "high", "high10"];
    public IReadOnlyList<string> Levels { get; } = [AutoOption, "3.1", "4.0", "4.1", "4.2", "5.1", "5.2"];
    public IReadOnlyList<string> RateControls { get; } = [ConstantQuality, AverageBitrate, ConstantBitrate];
    public IReadOnlyList<string> Colorspaces { get; } = [SameAsSource, TonemapToSdr];

    // ----- Presets -----

    public ObservableCollection<EncodingPreset> Presets { get; }

    [ObservableProperty] private EncodingPreset? _selectedPreset;
    [ObservableProperty] private string _presetName = "";

    partial void OnSelectedPresetChanged(EncodingPreset? value)
    {
        if (value is null)
            return;

        PresetName = value.Name;
        ApplyPreset(value);
    }

    /// <summary>Saves the Dimensions, Filters, Video and Audio settings under the name in the preset box.</summary>
    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0)
        {
            StatusText = "Type a name for the preset first.";
            return;
        }

        var preset = CaptureSettings(name);

        // Saving under an existing name replaces that preset.
        var existing = Presets.ToList().FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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
        foreach (var preset in PresetStore.Load())
            Presets.Add(preset);
    }

    private void ApplyPreset(EncodingPreset preset)
    {
        // Some sixty settings change here; the command is built once, when they have all been set.
        using var whole = DeferCommand();
        FrameEngine = preset.FrameEngine || IsEditorMode;

        // Width and height are set as stored, without one recalculating the other on the way.
        _syncingDimensions = true;
        try
        {
            KeepAspectRatio = preset.KeepAspectRatio;

            // Presets from before the option existed were vertical exactly when they used the framer.
            UseVerticalResolution = preset.UseVerticalResolution ?? preset.FrameEngine;
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

        PixelAspectRatio = Pick(PixelAspectRatios, preset.PixelAspectRatio, SameAsSource);
        CustomPixelAspectRatio = preset.CustomPixelAspectRatio;
        CenterZoom = preset.CenterZoom is > 0 and var zoom ? Math.Clamp(zoom, SmallestCenterZoom, LargestCenterZoom) : 1;
        CenterOffsetX = Math.Clamp(preset.CenterOffsetX, -100, 100);
        CenterOffsetY = Math.Clamp(preset.CenterOffsetY, -100, 100);
        BlurRadius = Math.Clamp(preset.BlurRadius, 5, 50);
        BlurPasses = Math.Clamp(preset.BlurPasses, 1, 5);
        BackgroundDim = Math.Clamp(preset.BackgroundDim, -0.5, 0);
        SetLayers(preset.Layers ?? []);
        _mainVideoRow.ApplyLook(preset.MainLayer);
        _backgroundRow.IsHidden = preset.BackgroundHidden;
        MainVideoIndex = Math.Clamp(preset.MainVideoIndex, 0, Layers.Count);
        _layoutSourceAspect = preset.LayoutSourceAspectRatio;
        AddLegacyWatermark(preset);

        AutoCaptions = preset.AutoCaptions;
        if (preset.CaptionStyle is { } captionStyle)
            SetCaptionStyle(captionStyle.Clone());
        SetCaptionLayer(preset.CaptionLayer);
        WhisperPrompt = preset.WhisperPrompt ?? "";
        WhisperLanguage = Pick(WhisperLanguages, preset.WhisperLanguage ?? "", "en");
        WhisperTranslate = preset.WhisperTranslate;

        // The encoder comes before its preset step: which steps exist depends on the encoder.
        // A preset may name a hardware encoder this machine does not have; then the current one stays.
        var encoderFound = VideoEncoders.FirstOrDefault(e => e.Name == preset.VideoEncoder) is { } encoder;
        if (encoderFound)
            VideoEncoder = VideoEncoders.First(e => e.Name == preset.VideoEncoder);

        LutPath = preset.LutPath;
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

        if (EncoderPresets.Contains(preset.EncoderPreset))
            EncoderPreset = preset.EncoderPreset;
        Tune = Pick(Tunes, preset.Tune, NoneOption);
        Profile = Pick(Profiles, preset.Profile, AutoOption);
        Level = Pick(Levels, preset.Level, AutoOption);
        RateControl = Pick(RateControls, preset.RateControl, ConstantQuality);
        Crf = Math.Clamp(preset.Quality, 0, 51);
        TargetBitrate = preset.TargetBitrate;
        Framerate = Pick(Framerates, preset.Framerate, SameAsSource);
        CustomFramerate = preset.CustomFramerate;
        Colorspace = Pick(Colorspaces, preset.Colorspace, SameAsSource);
        HardwareDecoding = preset.HardwareDecoding;
        ExtraVideoArguments = preset.ExtraVideoArguments;
        AudioEncoder = Pick(AudioEncoders, preset.AudioEncoder, "aac");
        AudioBitrate = Pick(AudioBitrates, preset.AudioBitrate, "160k");
        MergeAudioTracks = preset.MergeAudioTracks;
        NormalizeAudio = preset.NormalizeAudio;
        DuckAudio = false;
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

        ApplyLegacyDuck(preset.DuckAudio);
        Log(encoderFound
            ? $"Applied preset \"{preset.Name}\""
            : $"Applied preset \"{preset.Name}\", but {preset.VideoEncoder} is not available here: kept {VideoEncoder.DisplayName}");
    }

    // A preset from before ducking was set track by track: "duck the game audio to the mic" was one box
    // for the first two tracks. It is carried over as what it meant, once there are two tracks to carry it to.
    private bool _legacyDuckPending;

    private void ApplyLegacyDuck(bool? asked = null)
    {
        _legacyDuckPending = asked ?? _legacyDuckPending;
        if (!_legacyDuckPending || AudioTracks.Count < 2)
            return;

        _legacyDuckPending = false;
        (AudioTracks[0].AutoDuck, AudioTracks[1].IsVoice) = (true, true);
    }

    /// <summary>Puts back per-track choices for the tracks that exist; states for missing tracks are ignored.</summary>
    private void ApplyAudioTrackStates(IEnumerable<AudioTrackState> states)
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
            track.GainDb = Math.Clamp(saved.GainDb, -40, 30);
            track.Filters = saved.Filters?.Clone() ?? new TrackAudioFilters();
            track.SetEdits(saved.Offset, saved.Pieces);
            (track.AutoDuck, track.IsVoice) = (saved.AutoDuck, saved.IsVoice);
        }
    }

    private static string Pick(IReadOnlyList<string> options, string value, string fallback) =>
        options.Contains(value) ? value : fallback;

    // ----- Source / destination -----

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _downloadResolution = "Best";
    [ObservableProperty] private string _destinationPath = "";

    /// <summary>The file FFmpeg reads: the source itself, or the yt-dlp download for a URL.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string _localMediaPath = "";

    // ----- Status bar -----

    [ObservableProperty] private string _statusText = "Ready";

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
    [NotifyCanExecuteChangedFor(nameof(AddStopPointCommand))]
    private double _positionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(TimelineMaximum))]
    [NotifyCanExecuteChangedFor(nameof(AddStartPointCommand))]
    private double _durationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingStartText))]
    [NotifyCanExecuteChangedFor(nameof(AddStopPointCommand))]
    private double? _pendingStartMs;

    // On by default, together with the Copy encoders: a fresh launch is set up for lossless cutting.
    [ObservableProperty] private bool _snapToIFrames = true;

    // Snapping only concerns where the cut points sit. Which encoders are used is a separate choice.
    partial void OnSnapToIFramesChanged(bool value)
    {
        if (value)
            SnapSegmentsToIFrames();
    }

    /// <summary>
    /// Moves existing segments onto I-frames, growing them rather than shrinking: the start goes back
    /// to the I-frame at or before it, the end forward to the I-frame at or after it.
    /// </summary>
    private void SnapSegmentsToIFrames()
    {
        if (IFrames.Count == 0)
            return;

        var adjusted = 0;
        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];
            var start = segment.Start.TotalSeconds;
            var end = segment.End.TotalSeconds;

            // Segment times are whole milliseconds, so allow for that when matching I-frames.
            const double tolerance = 0.0005;
            var floor = IFrames.FindLastIndex(k => k <= start + tolerance);
            var ceiling = IFrames.FindIndex(k => k >= end - tolerance);
            var snappedStart = floor >= 0 ? IFrames[floor] : start;
            var snappedEnd = ceiling >= 0 ? IFrames[ceiling] : end;

            if (Math.Abs(snappedStart - start) <= tolerance && Math.Abs(snappedEnd - end) <= tolerance)
                continue;

            var snapped = new CutSegment(TimeSpan.FromSeconds(snappedStart), TimeSpan.FromSeconds(snappedEnd)) { IsSkipped = segment.IsSkipped };
            Segments[i] = snapped;
            _ = FlashAsync(snapped);
            adjusted++;
        }

        if (adjusted > 0)
            StatusText = adjusted == 1 ? "1 segment was moved onto I-frames." : $"{adjusted} segments were moved onto I-frames.";
    }

    private static async Task FlashAsync(CutSegment segment)
    {
        segment.IsFlashing = true;
        await Task.Delay(300);
        segment.IsFlashing = false;
    }

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

    [ObservableProperty] private CutSegment? _selectedSegment;

    public string PositionText => CutSegment.FormatTime(TimeSpan.FromMilliseconds(PositionMs));
    public string DurationText => CutSegment.FormatTime(TimeSpan.FromMilliseconds(DurationMs));

    // Never zero, so the thumb rests at the left edge while nothing is loaded.
    public double TimelineMaximum => DurationMs > 0 ? DurationMs : 1;

    public string PendingStartText => PendingStartMs is { } start
        ? $"Start point: {CutSegment.FormatTime(TimeSpan.FromMilliseconds(start))}"
        : "No start point set";

    public ObservableCollection<CutSegment> Segments { get; } = [];

    // ----- Summary -----

    [ObservableProperty] private string _container = "mp4";
    [ObservableProperty] private bool _webOptimized = true;

    // ----- Dimensions -----

    [ObservableProperty] private string _outputWidth = "";
    [ObservableProperty] private string _outputHeight = "";
    [ObservableProperty] private bool _keepAspectRatio = true;

    // The crop, as percentages (0 to 100) of the source's height and width. Being relative, a crop set on
    // one recording cuts the same part out of a recording of any other resolution.
    [ObservableProperty] private double _cropTop;
    [ObservableProperty] private double _cropBottom;
    [ObservableProperty] private double _cropLeft;
    [ObservableProperty] private double _cropRight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPixelAspectRatio))]
    private string _pixelAspectRatio = SameAsSource;

    [ObservableProperty] private string _customPixelAspectRatio = "";

    public bool IsCustomPixelAspectRatio => PixelAspectRatio == CustomOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSizeText))]
    private int _sourceWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSizeText))]
    private int _sourceHeight;

    public string SourceSizeText => SourceWidth > 0 && SourceHeight > 0
        ? $"Source: {SourceWidth} x {SourceHeight}. Leave width and height blank to keep the size left after cropping."
        : "Leave width and height blank to keep the source size.";

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
    partial void OnCropTopChanged(double value) => RelinkDimensions();
    partial void OnCropBottomChanged(double value) => RelinkDimensions();
    partial void OnCropLeftChanged(double value) => RelinkDimensions();
    partial void OnCropRightChanged(double value) => RelinkDimensions();
    partial void OnSourceWidthChanged(int value) => RelinkDimensions();
    partial void OnSourceHeightChanged(int value) => RelinkDimensions();

    /// <summary>Keeps the width when there is one and recalculates the height; otherwise works from the height.</summary>
    private void RelinkDimensions() =>
        UpdateLinkedDimension(typedIsWidth: !string.IsNullOrWhiteSpace(OutputWidth) || string.IsNullOrWhiteSpace(OutputHeight));

    /// <summary>
    /// With "Keep Aspect Ratio" on, fills in the other dimension from the one just typed, using the shape
    /// of the source frame after cropping: as it is, or on its side with Use Vertical Resolution.
    /// </summary>
    private void UpdateLinkedDimension(bool typedIsWidth)
    {
        if (_syncingDimensions || !KeepAspectRatio || GetCroppedSourceSize() is null)
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
    [NotifyPropertyChangedFor(nameof(HasRateControl))]
    private EncoderOption _videoEncoder;

    /// <summary>Only x264 and x265 take the preset names in the list; anything else would reject them.</summary>
    public bool AreSoftwareEncoderOptionsEnabled => VideoEncoder.Name is "libx264" or "libx265";

    /// <summary>False for stream copy, where quality and frame rate settings do not apply.</summary>
    public bool IsVideoReencoded => VideoEncoder.Family != EncoderFamily.Copy;

    [ObservableProperty] private string _customFramerate = "";
    public bool IsCustomFramerate => Framerate == CustomOption;
    public bool IsAudioReencoded => AudioEncoder != CopyOption;
    [ObservableProperty] private string _hardwareEncoderStatusText = "";
    [ObservableProperty] private string _encoderPreset = "medium";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomFramerate))]
    private string _framerate = SameAsSource;
    [ObservableProperty] private int _crf = 22;

    [ObservableProperty] private string _tune = NoneOption;
    [ObservableProperty] private string _profile = AutoOption;
    [ObservableProperty] private string _level = AutoOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConstantQuality))]
    [NotifyPropertyChangedFor(nameof(IsBitrateMode))]
    private string _rateControl = ConstantQuality;

    /// <summary>Target bitrate in kbps, used by the two bitrate modes.</summary>
    [ObservableProperty] private string _targetBitrate = "5000";

    public bool IsConstantQuality => RateControl == ConstantQuality;
    public bool IsBitrateMode => !IsConstantQuality;

    [ObservableProperty] private string _colorspace = SameAsSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HdrStatusText))]
    private bool _isSourceHdr;

    public string HdrStatusText => IsSourceHdr ? "HDR source detected: tonemap it for SDR screens" : "";

    // ----- Audio -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAudioReencoded))]
    private string _audioEncoder = CopyOption;
    [ObservableProperty] private string _audioBitrate = "160k";

    // ----- Chapters -----

    [ObservableProperty] private bool _chapterMarkers = true;

    // ----- Command preview -----

    [ObservableProperty] private string _commandPreview = "";

    /// <summary>
    /// Set once the user types in the command box. While set, settings changes leave the command alone.
    /// </summary>
    [ObservableProperty] private bool _isCommandManuallyEdited;

    // ----- Cut points -----

    [RelayCommand(CanExecute = nameof(CanAddStartPoint))]
    private void AddStartPoint()
    {
        var position = GetCutPositionMs();
        PendingStartMs = position;
        PositionMs = position;
        StatusText = $"{PendingStartText}. Scrub forward and add a stop point.";
    }

    private bool CanAddStartPoint() => DurationMs > 0;

    [RelayCommand(CanExecute = nameof(CanAddStopPoint))]
    private void AddStopPoint()
    {
        if (PendingStartMs is not { } start)
            return;

        var stop = GetCutPositionMs();
        if (stop <= start)
        {
            StatusText = "The nearest I-frame is not after the start point. Scrub further forward.";
            return;
        }

        var segment = new CutSegment(TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(stop));
        Checkpoint("add a segment");

        // Keep the list ordered by start time.
        var index = 0;
        while (index < Segments.Count && Segments[index].Start <= segment.Start)
            index++;
        Segments.Insert(index, segment);

        PendingStartMs = null;
        PositionMs = stop;
        Log($"Added segment {segment.Display}");
    }

    private bool CanAddStopPoint() => PendingStartMs is { } start && PositionMs > start;

    /// <summary>
    /// The playback position to cut at: as is, or moved to the nearest I-frame when snapping.
    /// </summary>
    private double GetCutPositionMs()
    {
        if (!SnapToIFrames || IFrames.Count == 0)
            return PositionMs;

        var seconds = PositionMs / 1000;
        var next = IFrames.BinarySearch(seconds);
        if (next >= 0)
            return PositionMs;

        next = ~next;
        if (next == 0)
            return IFrames[0] * 1000;
        if (next == IFrames.Count)
            return IFrames[^1] * 1000;

        var previous = next - 1;
        var nearest = seconds - IFrames[previous] <= IFrames[next] - seconds ? previous : next;
        return IFrames[nearest] * 1000;
    }

    /// <summary>Moves playback to the start of a segment so the cut can be checked.</summary>
    public void SeekToSegment(CutSegment segment) => PositionMs = segment.Start.TotalMilliseconds;

    partial void OnSelectedSegmentChanged(CutSegment? value)
    {
        if (value is not null)
            SeekToSegment(value);
    }

    [RelayCommand]
    private void RemoveSegment(CutSegment? segment)
    {
        if (segment is not null)
        {
            Checkpoint("remove a segment");
            Segments.Remove(segment);
        }
    }

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

    /// <summary>Tests the GPU encoders in the background and offers the working ones in the Video tab.</summary>
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
            (supported, problems) = await EncoderProber.ProbeAsync(_shutdown.Token);
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

        _ = ProbeOpenClBlurAsync();

        // Rebuild the hardware part of the list, keeping the selection where it still exists. The first time
        // round, a hardware encoder set as the default is selected, unless another was chosen in the meantime.
        var untouched = VideoEncoder.Family is EncoderFamily.Copy || VideoEncoder == DefaultVideoEncoder;
        var selected = _pendingDefaultEncoder is { } pending && untouched ? pending : VideoEncoder.Name;
        _pendingDefaultEncoder = null;
        foreach (var stale in VideoEncoders.Where(e => e.IsHardware).ToList())
            VideoEncoders.Remove(stale);
        foreach (var name in supported)
            VideoEncoders.Add(EncoderOption.FromHardwareName(name));
        VideoEncoder = VideoEncoders.FirstOrDefault(e => e.Name == selected) ?? DefaultVideoEncoder;

        HardwareEncoderStatusText = supported.Count switch
        {
            0 => "No hardware encoders available on this system",
            1 => "1 hardware encoder available",
            _ => $"{supported.Count} hardware encoders available",
        };

        // An encoder FFmpeg has but could not start is worth a word: it is usually a driver that needs updating.
        if (problems.Count > 0)
            HardwareEncoderStatusText += $". Not usable: {string.Join("; ", problems)}";
    }

    /// <summary>Tests whether the graphics card can blur the background (OpenCL), and writes the command again when it can.</summary>
    private async Task ProbeOpenClBlurAsync()
    {
        try
        {
            var before = EncoderProber.OpenClBlurAvailable;
            if (await EncoderProber.ProbeOpenClBlurAsync(_shutdown.Token) != before)
                GenerateCommand();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Not known: the processor blurs, as it always could.
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task LoadSourceAsync() => RunOperationAsync(async cancellationToken =>
    {
        var source = SourcePath.Trim().Trim('"');
        if (source.Length == 0)
            return null;

        // Loading something by hand ends the editing of a queued job.
        EditingJob = null;
        var (opened, message) = await LoadMediaAsync(source, knownLocalPath: null, applyAutomation: true, cancellationToken);

        // Settings: every video starts as a vertical one.
        if (opened && AppSettings.Current.DefaultVerticalVideo && !UseVerticalResolution)
            UseVerticalResolution = true;
        _undo.Clear();
        _redo.Clear();
        MarkSaved();
        return message;
    });

    /// <summary>Loads a file chosen outside the source box (dropped on the window, for instance).</summary>
    public void LoadFile(string path)
    {
        SourcePath = path;
        LoadSourceCommand.Execute(null);
    }

    /// <summary>
    /// Loads a file and gives it a style preset: what dropping a video on a style in the launch window does.
    /// </summary>
    public Task LoadFileWithStyleAsync(string path, string stylePath) => RunOperationAsync(async cancellationToken =>
    {
        SourcePath = path;
        EditingJob = null;
        var (loaded, message) = await LoadMediaAsync(path, knownLocalPath: null, applyAutomation: true, cancellationToken);
        if (!loaded)
            return message;

        if (ReadStyle(stylePath) is not { } style)
            return $"{message}. {StatusText}";

        // A style is layers: the Layer Engine is what shows them, whatever mode or preset was in effect.
        FrameEngine = true;
        ApplyStyle(style, layout: true, color: true, blur: true, subtitles: true);
        MarkSaved();
        return $"{message}. Style \"{Path.GetFileNameWithoutExtension(stylePath)}\" applied.";
    });

    /// <summary>
    /// Switches between Encoder Mode and Editor Mode. Each mode keeps its own interface settings, which are
    /// swapped here; what is loaded, cut and set for the encode stays as it is.
    /// </summary>
    public void ToggleMode()
    {
        var settings = AppSettings.Current;
        settings.SwitchMode(IsEditorMode ? AppSettings.EncoderMode : AppSettings.EditorMode);
        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The switch still holds for this session.
        }

        OnSettingsSaved();
        (FrameEngine, PlayOnlySegments, ShowAutoCaptions) = (IsEditorMode || Layers.Count > 0, settings.StartWithPlayOnlySegments, settings.ShowAutoCaptions);
        (ShowIFrames, LivePreview) = (settings.ShowIFrames, settings.LivePreview);

        // With nothing loaded yet the mode's own starting point applies: Editor Mode mixes the tracks into one.
        if (!HasSource)
            MergeAudioTracks = IsEditorMode;
        (ShowCutSegmentsPane, ShowKeyframesPane, ShowClipKeyframes) = (settings.ShowCutSegmentsPane, settings.ShowKeyframesPane, settings.ShowClipKeyframes);
        OnPropertyChanged(nameof(ModeButtonText));

        // The Layers tab draws the main video's clips with frames from it, which Encoder Mode has no use for.
        if (IsEditorMode && HasSource && _mainVideoRow.Filmstrip is null)
            _ = LoadMainFilmstripAsync(LocalMediaPath);
        StatusText = $"{settings.UiMode}: {(IsEditorMode ? "the tools for cutting, layering and captioning." : "the lean front end for converting and trimming.")}";
    }

    /// <summary>What the mode button says: the mode in use.</summary>
    public string ModeButtonText => IsEditorMode ? "Editor" : "Encoder";

    /// <summary>
    /// Makes a source the current one: downloads it if it is a URL, inspects it and starts playback.
    /// </summary>
    /// <param name="knownLocalPath">A file already downloaded for this source, used instead of downloading again.</param>
    /// <param name="applyAutomation">Whether the smart rules and the default preset from the settings apply.</param>
    private async Task<(bool Loaded, string Message)> LoadMediaAsync(
        string source, string? knownLocalPath, bool applyAutomation, CancellationToken cancellationToken)
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
            var (resolution, subtitles, sponsorBlock) = (DownloadResolution, DownloadSubtitles, AppSettings.Current.SponsorBlockCategories);
            localPath = await Task.Run(
                () => YtDlpDownloader.DownloadAsync(source, resolution, subtitles, sponsorBlock, progress, cancellationToken),
                cancellationToken);
        }
        else
        {
            return (false, $"Source not found: {source}");
        }

        var mediaInfo = await MediaProbe.ProbeAsync(localPath, cancellationToken);
        ResetForNewSource();
        LocalMediaPath = localPath;
        SetMediaInfo(mediaInfo);

        // Every new source gets its own default name, so one encode never lands on top of the previous one.
        DestinationPath = Path.Combine(
            GetDefaultOutputFolder(localPath), $"{Path.GetFileNameWithoutExtension(localPath)}_HandPeg.{Container}");

        var message = $"Loaded {localPath}";
        if (applyAutomation && AppSettings.Current.FindPresetFor(localPath, source) is { } presetName)
        {
            if (Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase)) is { } preset)
            {
                // Selecting it applies it; when it is already selected, apply it again to undo later changes.
                if (ReferenceEquals(SelectedPreset, preset))
                    ApplyPreset(preset);
                else
                    SelectedPreset = preset;

                message += $". Preset \"{preset.Name}\" applied automatically.";
            }
            else
            {
                message += $". The automatic preset \"{presetName}\" no longer exists.";
            }
        }

        // Layers marked on a video of another shape will not sit on the same things in this one.
        if (GetLayoutAspectWarning() is { } warning)
            message += $". {warning}";

        // Playback, the I-frame index and hover previews are for the window; a background instance needs none of them.
        if (!_isBackgroundWorker)
        {
            MediaLoaded?.Invoke(localPath);
            _ = ScanIFramesAsync(localPath);
            _ = GenerateSpriteSheetAsync(localPath);
            _mainVideoRow.Filmstrip = null;
            _ = LoadMainFilmstripAsync(localPath);
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

            // The box may have been ticked while the index was still being built.
            if (SnapToIFrames)
                SnapSegmentsToIFrames();
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
        ApplyDisplaySettings();
        if (IsEditorMode)
            FrameEngine = true;
        LoadAutomation();
        GenerateCommand();
        _ = ProbeHardwareEncodersAsync();
        _ = CheckDependencyUpdatesAsync();

        var missing = DependencyUpdater.GetMissing();
        StatusText = missing.Count > 0
            ? $"Settings saved. Missing {string.Join(", ", missing)}."
            : "Settings saved.";
    }

    /// <summary>Stops whatever is still running when the application closes.</summary>
    public void Shutdown()
    {
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
    private Task StartEncodeAsync() => RunOperationAsync(async cancellationToken =>
    {
        if (string.IsNullOrWhiteSpace(CommandPreview))
            return "There is no command to run.";

        // Copy keeps the picture as it is: said once more before the time is spent, when there is work it would skip.
        if (CopyBypassWarning.Length > 0 && !IsCommandManuallyEdited && Confirm is not null
            && !Confirm("Stream Copy", $"{CopyBypassWarning}\n\nThe video is copied as it is: nothing set on the Layers and Filters tabs, and no crop or resize, reaches the output. To apply them, choose an encoder on the Video tab.", "Encode Anyway"))
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

        // Progress is measured against what the output should contain: the kept segments, or everything.
        var segments = GetMergedSegments();
        var expected = segments.Count > 0
            ? TimeSpan.FromTicks(segments.Sum(s => s.Duration.Ticks))
            : TimeSpan.FromMilliseconds(DurationMs);

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
            await FfmpegRunner.RunAsync(command, progress, cancellationToken);
            NotifyFinished("Encode complete", Path.GetFileName(DestinationPath.Trim().Trim('"')));
            return "Encode complete.";
        }
        catch (InvalidOperationException) when (
            AppSettings.Current.AutoFallbackToSoftware
            && HardwareFallback.TryRewrite(command, out _, out _, out _))
        {
            // The hardware encoder let us down: one more attempt on the software encoder for the same codec.
            HardwareFallback.TryRewrite(command, out var retry, out var hardware, out var software);
            StatusText = $"Warning: {hardware} failed. Retrying with {software}...";
            Notifier.Show("Hardware encoder failed", $"{hardware} could not encode this video. HandPeg is trying again with {software}.");

            // Reflect the switch in the Video tab where that is possible; a hand-edited command is patched in place.
            if (!IsCommandManuallyEdited && VideoEncoders.FirstOrDefault(e => e.Name == software) is { } replacement)
            {
                VideoEncoder = replacement;
                retry = CommandPreview;
            }
            else
            {
                CommandPreview = retry;
            }

            ProgressValue = 0;
            IsProgressIndeterminate = true;
            SetTaskbarProgress(TaskbarItemProgressState.Normal);
            await FfmpegRunner.RunAsync(retry, progress, cancellationToken);
            NotifyFinished("Encode complete", Path.GetFileName(DestinationPath.Trim().Trim('"')));
            return $"Encode complete on {software}: the hardware encoder {hardware} failed, so the software encoder was used.";
        }
        finally
        {
            // Finished, failed or cancelled: the taskbar button goes back to plain.
            SetTaskbarProgress(TaskbarItemProgressState.None);
        }
    });

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
    /// </summary>
    private async Task RunOperationAsync(Func<CancellationToken, Task<string?>> operation)
    {
        if (IsBusy)
            return;

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _acceptProgressReports = true;
        IsBusy = true;

        string? outcome;
        try
        {
            outcome = await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled.";
        }
        catch (Exception ex)
        {
            outcome = $"Error: {ex.Message}";
        }

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
        Segments.Clear();
        PendingStartMs = null;
        PositionMs = 0;
        DurationMs = 0;

        // A new main video starts where any does: at the beginning of the timeline, whole, and keeping still.
        _playerMediaSeconds = 0;
        _mainPieces.Clear();
        SetBin(null);
        InvalidateMainClips();
        _mainVideoRow.ApplyTiming(null);
        _mainVideoRow.MediaDuration = 0;
    }

    // ----- Command generation -----

    [RelayCommand]
    private void RegenerateCommand()
    {
        IsCommandManuallyEdited = false;
        GenerateCommand();
    }

    partial void OnCommandPreviewChanged(string value)
    {
        if (!_writingGeneratedCommand)
            IsCommandManuallyEdited = true;
    }

    partial void OnContainerChanged(string value)
    {
        // GIF and WebP are always encoded. Leaving the encoder on Copy would keep the size, crop
        // and Filters tabs switched off even though their settings now apply.
        if (value is "gif" or "webp" && VideoEncoder.Family == EncoderFamily.Copy)
            VideoEncoder = DefaultVideoEncoder;

        if (!string.IsNullOrWhiteSpace(DestinationPath))
            DestinationPath = Path.ChangeExtension(DestinationPath, value);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is { } name && !NonCommandProperties.Contains(name))
            GenerateCommand();
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

        // The timeline is as long as its clips reach, and the main video's row shows where it sits.
        RefreshSequence();
        SyncMainRow();

        // The Properties tab follows the settings even while the command itself is frozen by a manual edit.
        UpdateProjectedOutput();
        OnPropertyChanged(nameof(CaptionHint));
        OnPropertyChanged(nameof(CopyBypassWarning));

        // So does Live Preview, which shows the settings, not the command.
        if (!_isBackgroundWorker)
            LiveFilterInvalidated?.Invoke();

        if (IsCommandManuallyEdited)
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
    }

    /// <summary>The cut segments in order, with overlapping or touching ones merged.</summary>
    private List<CutSegment> GetMergedSegments()
    {
        var merged = new List<CutSegment>();
        foreach (var segment in Segments.Where(s => !s.IsSkipped).OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && segment.Start <= merged[^1].End)
            {
                if (segment.End > merged[^1].End)
                    merged[^1] = new CutSegment(merged[^1].Start, segment.End);
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }

    /// <summary>
    /// The frame rate to convert to, or null to keep the source's. Custom values may be any positive number.
    /// </summary>
    private string? GetTargetFramerate()
    {
        var text = Framerate switch
        {
            SameAsSource => "",
            CustomOption => CustomFramerate.Trim(),
            _ => Framerate,
        };

        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fps) && fps > 0
            ? fps.ToString("0.###", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Writes the concat list: the source named once per segment with its in and out points.
    /// Copied video can only start on an I-frame, so a cut begins at the I-frame at or before its in point.
    /// </summary>
    private string _writtenCuts = "";

    private void WriteCutsFile(string input, List<CutSegment> segments)
    {
        var list = new StringBuilder("ffconcat version 1.0\n");
        var file = input.Replace('\\', '/').Replace("'", @"'\''");

        // The cuts are times on the sequence; the list wants them as times in the file.
        var shift = TimeSpan.FromSeconds(MainShift);
        foreach (var segment in segments)
        {
            list.Append($"file '{file}'\n");
            list.Append($"inpoint {Seconds(segment.Start - shift < TimeSpan.Zero ? TimeSpan.Zero : segment.Start - shift)}\n");
            list.Append($"outpoint {Seconds(segment.End - shift)}\n");
        }

        // The command is rebuilt for every setting that changes; the file is only written when the cuts have.
        var text = list.ToString();
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
    /// The value for setsar, or null to leave the pixel shape alone. Written with a slash:
    /// a colon would be read as the separator between filter options.
    /// </summary>
    private string? GetSampleAspectRatio()
    {
        var ratio = PixelAspectRatio switch
        {
            SameAsSource => "",
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
