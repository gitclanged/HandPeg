using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Auto-captions (whisper.cpp), and the Automation tab's smart rules.
public partial class MainViewModel
{
    // ----- Auto-captions -----

    /// <summary>Burn captions made from the spoken words into the picture.</summary>
    [ObservableProperty] private bool _autoCaptions;

    // What captions listen to: one of the video's own audio tracks (when neither of these is set), a
    // separate audio file, or the voiceover recorded in the mixer pane.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionUseVideoAudio))]
    private bool _captionUseExternalAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionUseVideoAudio))]
    private bool _captionUseVoiceover;

    /// <summary>The first of the three radio buttons: neither of the others.</summary>
    public bool CaptionUseVideoAudio
    {
        get => !CaptionUseExternalAudio && !CaptionUseVoiceover;
        set
        {
            if (value)
                (CaptionUseExternalAudio, CaptionUseVoiceover) = (false, false);
        }
    }

    partial void OnCaptionUseExternalAudioChanged(bool value)
    {
        if (value)
            CaptionUseVoiceover = false;
    }

    partial void OnCaptionUseVoiceoverChanged(bool value)
    {
        if (value)
            CaptionUseExternalAudio = false;
        OnPropertyChanged(nameof(IncludeVoiceover));
    }

    // ----- How whisper is asked to listen -----

    /// <summary>The languages offered: "auto" lets whisper decide (not with the English-only .en models).</summary>
    public IReadOnlyList<string> WhisperLanguages { get; } =
        ["auto", "en", "es", "fr", "de", "it", "pt", "nl", "pl", "sv", "tr", "ru", "uk", "ar", "hi", "ja", "ko", "zh"];

    /// <summary>Words, names and spellings to expect: whisper's initial prompt.</summary>
    [ObservableProperty] private string _whisperPrompt = "";

    [ObservableProperty] private string _whisperLanguage = "en";

    /// <summary>Have whisper write English whatever language is spoken.</summary>
    [ObservableProperty] private bool _whisperTranslate;

    // ----- The caption box -----

    /// <summary>
    /// Where the captions are drawn on the output frame: a layer like the elements, placed and sized on the
    /// Edit Layout canvas, and always the top one.
    /// </summary>
    public OverlayRegion CaptionLayer { get; } = CreateCaptionLayer();

    private static OverlayRegion CreateCaptionLayer() => new()
    {
        Kind = ElementKind.Captions,
        Name = "Subtitles",
        LockAspectRatio = false,
        PositionX = DefaultCaptionX,
        PositionY = DefaultCaptionY,
        SizeWidth = DefaultCaptionWidth,
        SizeHeight = DefaultCaptionHeight,
    };

    // The lower part of the frame, nearly its whole width.
    private const double DefaultCaptionX = 0.05;
    private const double DefaultCaptionY = 0.70;
    private const double DefaultCaptionWidth = 0.90;
    private const double DefaultCaptionHeight = 0.20;

    private void SetCaptionLayer(OverlayRegionState? state)
    {
        (CaptionLayer.PositionX, CaptionLayer.PositionY) = (state?.PositionX ?? DefaultCaptionX, state?.PositionY ?? DefaultCaptionY);
        CaptionLayer.SizeWidth = state is { SizeWidth: > 0 } ? state.SizeWidth : DefaultCaptionWidth;
        CaptionLayer.SizeHeight = state is { SizeHeight: > 0 } ? state.SizeHeight : DefaultCaptionHeight;
        CaptionLayer.Opacity = Math.Clamp(state?.Opacity ?? 100, 0, 100);
    }

    [RelayCommand]
    private void ResetCaptionLayer() => SetCaptionLayer(null);

    /// <summary>The layout pane has something to show with the engine on, or with captions to place.</summary>
    public bool CanEditLayout => FrameEngine || AutoCaptions;

    partial void OnAutoCaptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditLayout));
        RefreshElementRows();
        if (!CanEditLayout)
            IsArrangeActive = false;
        else if (value)
            OpenLayoutPaneIfWanted();
    }

    /// <summary>The caption box on an output frame of the given size, in pixels, with even sides as a video frame needs.</summary>
    private (int X, int Y, int Width, int Height) GetCaptionRect(int frameWidth, int frameHeight)
    {
        var (x, y, width, height) = CaptionLayer.GetOutputRect(frameWidth, frameHeight, 0, 0);
        return (x, y, Math.Max(width, 16), Math.Max(height, 16));
    }

    /// <summary>Which of the video's audio tracks is transcribed: the first one unless another is chosen.</summary>
    [ObservableProperty] private AudioTrack? _captionAudioTrack;

    /// <summary>The separate audio file: a clean voice recording in sync with the video, say.</summary>
    [ObservableProperty] private string _captionAudioPath = "";

    /// <summary>After the track list has been rebuilt for a new source: the choice falls back to the first track.</summary>
    private void KeepCaptionTrackValid()
    {
        if (CaptionAudioTrack is null || !AudioTracks.Contains(CaptionAudioTrack))
            CaptionAudioTrack = AudioTracks.FirstOrDefault();
    }

    /// <summary>The Subtitles tab's "Show Advanced" switch. Remembered between sessions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStandardSubtitles))]
    private bool _showAutoCaptions = AppSettings.Current.ShowAutoCaptions;

    public bool ShowStandardSubtitles => !ShowAutoCaptions;

    public CaptionStyle CaptionStyle { get; private set; } = new();

    /// <summary>The style in a few words, shown beside the button that opens its dialog.</summary>
    public string CaptionStyleSummary =>
        $"{CaptionStyle.FontName} {CaptionStyle.FontSize}, {CaptionStyle.Animation}, up to {CaptionStyle.MaxWordsPerLine} words";

    public void SetCaptionStyle(CaptionStyle style)
    {
        CaptionStyle = style;
        OnPropertyChanged(nameof(CaptionStyleSummary));
    }

    /// <summary>The subtitle file the encode draws. Rewritten by every encode that uses captions.</summary>
    public string CaptionsFilePath => Path.Combine(_workFolder, "captions.ass");

    /// <summary>What stands between the user and captions, if anything.</summary>
    public string CaptionHint
    {
        get
        {
            if (!AutoCaptions)
                return "";
            if (!File.Exists(DependencyUpdater.WhisperPath))
                return DependencyUpdater.IsWhisperOverridden
                    ? $"The whisper.exe set in Settings (Tools) was not found: {DependencyUpdater.WhisperPath}"
                    : "whisper.cpp is not installed yet: use Install / Update All Dependencies in Settings (Tools).";
            if (!File.Exists(DependencyUpdater.WhisperModelPath))
                return $"The speech model {AppSettings.Current.WhisperModel} is not downloaded yet: see Settings (Tools).";
            if (VideoEncoder.Family == EncoderFamily.Copy && !IsAnimatedOutput)
                return "Captions are drawn into the picture, so they need a video encoder other than Copy (Video tab).";
            return "The words are transcribed when you press Start Encode or Render Preview.";
        }
    }

    partial void OnShowAutoCaptionsChanged(bool value)
    {
        AppSettings.Current.ShowAutoCaptions = value;
        try
        {
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still holds for this session.
        }
    }

    /// <summary>How a command names a caption file: this is what is looked for to tell that a command draws one.</summary>
    private static string BuildCaptionFilter(string assPath) => $"ass='{EscapeFilterPath(assPath)}'";

    /// <summary>
    /// The step that puts the captions on the picture as its top layer. They are drawn on a transparent
    /// canvas the size of the caption box, which is then laid over the frame where the box sits; that is
    /// what lets the captions be placed, sized and made translucent like any other layer.
    /// </summary>
    private string BuildCaptionOverlay(string assPath)
    {
        var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        var (x, y, width, height) = GetCaptionRect(frameWidth, frameHeight);
        var rate = GetTargetFramerate() ?? Number(SourceFrameRate);
        var opacity = Math.Clamp(CaptionLayer.Opacity, 0, 100) / 100.0;
        var fade = opacity < 1 ? $",colorchannelmixer=aa={Number(opacity)}" : "";

        return $"null[cap_base];color=c=black@0:s={width}x{height}:r={rate},format=rgba,{BuildCaptionFilter(assPath)}:alpha=1{fade}[cap_layer];"
               + $"[cap_base][cap_layer]overlay={x}:{y}:shortest=1";
    }

    private bool CommandUsesCaptions(string command) =>
        command.Contains(BuildCaptionFilter(CaptionsFilePath), StringComparison.OrdinalIgnoreCase);

    // Transcribing is the slow part, and the words do not change when only the style does. So the words are
    // kept, by what was listened to and with which model.
    private readonly Dictionary<string, List<CaptionWord>> _captionWords = [];

    /// <summary>
    /// Makes the caption file a command is about to draw, when it draws one. Returns a message when
    /// captions are wanted but cannot be made; null when all is well (or no captions are involved).
    /// </summary>
    private async Task<string?> PrepareCaptionsForEncodeAsync(string command, CancellationToken cancellationToken)
    {
        if (!CommandUsesCaptions(command))
        {
            return AutoCaptions && !IsCommandManuallyEdited
                ? "Auto-Captions are on, but captions are drawn into the picture: choose a video encoder other than Copy, or switch them off."
                : null;
        }

        // What will be heard: the kept segments end to end, or everything.
        // The voiceover was recorded against the finished timeline, so nothing is cut out of it.
        var ranges = CaptionUseVoiceover ? [] : GetMergedSegments().Select(s => (s.Start.TotalSeconds, s.End.TotalSeconds)).ToList();
        return await PrepareCaptionsAsync(ranges, CaptionsFilePath, timeOffset: CaptionUseVoiceover ? VoiceoverStartSeconds : 0, cancellationToken);
    }

    /// <summary>
    /// The pipeline: extract the audio that will be heard as a 16 kHz mono wave file, transcribe it with
    /// whisper.cpp, and write the words as an ASS file in the chosen style. Returns null, or what went wrong.
    /// </summary>
    private async Task<string?> PrepareCaptionsAsync(
        List<(double Start, double End)> ranges, string assPath, double timeOffset, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.WhisperPath))
            return "Auto-captions need whisper.cpp: use Install / Update All Dependencies in Settings (Tools), or set the path to your own whisper.exe there.";

        var modelPath = DependencyUpdater.WhisperModelPath;
        if (!File.Exists(modelPath))
            return $"Auto-captions need the speech model {AppSettings.Current.WhisperModel}: download it in Settings (Tools).";

        // What to listen to: one of the video's tracks, the first track of a separate file, or the voiceover.
        string audioPath;
        var trackIndex = 0;
        if (CaptionUseVoiceover)
        {
            if (await GetTrimmedVoiceoverAsync(cancellationToken) is not { } voiceover)
                return "Auto-captions are set to the Recorded Voiceover Track, but there is no voiceover yet (Voiceover Studio, on the Audio tab).";
            audioPath = voiceover;
        }
        else if (CaptionUseExternalAudio)
        {
            audioPath = CaptionAudioPath.Trim().Trim('"');
            if (audioPath.Length == 0)
                return "Auto-captions are set to an External Audio File, but none is chosen (Subtitles tab).";
            if (!File.Exists(audioPath))
                return $"The external audio file for captions was not found: {audioPath}";
        }
        else
        {
            if (_mediaInfo is { Audio.Count: 0 })
                return "This video has no audio to make captions from. Choose an External Audio File, or switch Auto-Captions off.";
            audioPath = LocalMediaPath;
            trackIndex = CaptionAudioTrack?.Index ?? 0;
        }

        var (language, prompt, translate) = (WhisperLanguage, WhisperPrompt.Trim(), WhisperTranslate);
        var key = $"{audioPath}|{trackIndex}|{File.GetLastWriteTimeUtc(audioPath).Ticks}|{modelPath}|{language}|{translate}|{prompt}|"
                  + string.Join(",", ranges.Select(r => $"{r.Start:0.###}-{r.End:0.###}"));
        if (!_captionWords.TryGetValue(key, out var words))
        {
            Directory.CreateDirectory(_workFolder);
            var wavPath = Path.Combine(_workFolder, $"captions_{Guid.NewGuid():N}.wav");
            try
            {
                IsProgressIndeterminate = true;
                StatusText = "Captions: extracting the audio...";
                await FfmpegRunner.RunAsync(
                    CaptionGenerator.BuildExtractCommand(audioPath, trackIndex, ranges, wavPath), new Progress<FfmpegProgress>(), cancellationToken);

                StatusText = $"Captions: transcribing with {Path.GetFileName(modelPath)}...";
                words = await Task.Run(
                    () => CaptionGenerator.TranscribeAsync(wavPath, modelPath, language, prompt, translate, cancellationToken), cancellationToken);
            }
            finally
            {
                File.Delete(wavPath);
            }

            _captionWords[key] = words;
        }

        // The file is written for the caption box, not the frame: that is the canvas it is drawn on.
        // Text size follows the frame (so a style looks the same at 720p and 4K) and the height of the
        // box (so making the box taller makes the words bigger).
        var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        var (_, _, width, height) = GetCaptionRect(frameWidth, frameHeight);
        var textScale = Math.Min(frameWidth, frameHeight) / 1080.0 * (height / (DefaultCaptionHeight * frameHeight));
        CaptionGenerator.WriteAss(words, CaptionStyle, width, height, textScale, assPath, timeOffset);
        StatusText = $"Captions: {words.Count} word{(words.Count == 1 ? "" : "s")} written.";
        return null;
    }

    // ----- whisper.cpp model -----

    /// <summary>Downloads a speech model into the models folder.</summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task DownloadWhisperModelAsync(string? model) => RunOperationAsync(async cancellationToken =>
    {
        if (string.IsNullOrWhiteSpace(model))
            return "Choose a model first.";

        IsProgressIndeterminate = true;
        var outcome = await DependencyUpdater.DownloadWhisperModelAsync(model, new Progress<string>(ReportStatus), cancellationToken);
        OnPropertyChanged(nameof(CaptionHint));
        return outcome;
    });

    // ----- Automation -----

    private const string NoPreset = "(None)";

    /// <summary>The smart rules as edited on the Automation tab. Saved to the settings by <see cref="SaveAutomation"/>.</summary>
    public ObservableCollection<SmartRule> SmartRules { get; } = [];

    /// <summary>The presets by name, for the rules to choose from.</summary>
    public ObservableCollection<string> PresetNames { get; } = [];

    /// <summary>The same, with "(None)" in front: the choices for the global default.</summary>
    public ObservableCollection<string> DefaultPresetChoices { get; } = [];

    /// <summary>The preset applied when no rule matches, or "(None)".</summary>
    [ObservableProperty] private string _defaultPresetChoice = NoPreset;


    private bool _loadingAutomation;

    /// <summary>Fills the Automation tab from the settings in effect.</summary>
    private void LoadAutomation()
    {
        _loadingAutomation = true;
        try
        {
            RefreshPresetNames();

            SmartRules.Clear();
            foreach (var rule in AppSettings.Current.SmartRules)
                SmartRules.Add(new SmartRule { Type = rule.Type, Path = rule.Path, Preset = rule.Preset });

            var saved = AppSettings.Current.DefaultPreset;
            DefaultPresetChoice = PresetNames.Contains(saved) ? saved : NoPreset;
        }
        finally
        {
            _loadingAutomation = false;
        }
    }

    private void RefreshPresetNames()
    {
        var chosen = DefaultPresetChoice;
        var wasLoading = _loadingAutomation;
        _loadingAutomation = true;
        try
        {
            PresetNames.Clear();
            DefaultPresetChoices.Clear();
            DefaultPresetChoices.Add(NoPreset);
            foreach (var preset in Presets)
            {
                PresetNames.Add(preset.Name);
                DefaultPresetChoices.Add(preset.Name);
            }

            // Emptying the list cleared the selection that was bound to it.
            DefaultPresetChoice = DefaultPresetChoices.Contains(chosen) ? chosen : NoPreset;
        }
        finally
        {
            _loadingAutomation = wasLoading;
        }
    }

    partial void OnDefaultPresetChoiceChanged(string value)
    {
        // A drop-down whose list is being refilled reports "nothing selected" for a moment.
        if (!_loadingAutomation && value is not null)
            SaveAutomation();
    }

    /// <summary>Adds a rule of the given kind, assigned to the first preset, and saves.</summary>
    public SmartRule? AddSmartRule(string type, string match)
    {
        if (PresetNames.Count == 0)
        {
            StatusText = "There are no presets to assign yet.";
            return null;
        }

        var rule = new SmartRule { Type = type, Path = match, Preset = PresetNames[0] };
        SmartRules.Add(rule);
        SaveAutomation();
        return rule;
    }

    public void RemoveSmartRule(SmartRule rule)
    {
        SmartRules.Remove(rule);
        SaveAutomation();
    }

    /// <summary>
    /// Writes the rules and the default preset to the settings file. There is no Save button on the tab:
    /// this runs after every change. Rules still missing their text or preset stay on the tab but are not saved.
    /// </summary>
    public void SaveAutomation()
    {
        if (_loadingAutomation || _isBackgroundWorker)
            return;

        var settings = AppSettings.Current;
        settings.SmartRules = SmartRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Path) && PresetNames.Contains(r.Preset))
            .Select(r => new SmartRule { Type = SmartRule.Types.Contains(r.Type) ? r.Type : SmartRule.Folder, Path = r.Path.Trim(), Preset = r.Preset })
            .ToList();
        settings.DefaultPreset = DefaultPresetChoice is { } choice && choice != NoPreset ? choice : "";

        try
        {
            settings.SaveAsCurrent();
            StatusText = $"Automation saved: {settings.SmartRules.Count} rule{(settings.SmartRules.Count == 1 ? "" : "s")}"
                         + (settings.DefaultPreset.Length > 0 ? $", default preset \"{settings.DefaultPreset}\"." : ", no default preset.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not write {AppSettings.FilePath}: {ex.Message}";
        }
    }
}
