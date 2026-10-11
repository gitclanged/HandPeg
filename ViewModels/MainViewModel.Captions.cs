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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionOptionsEnabled))]
    private bool _autoCaptions;

    /// <summary>Whether the caption options can be set: always in Editor Mode, where captions are made with a button; in Encoder Mode, while Auto-Captions is ticked.</summary>
    public bool CaptionOptionsEnabled => IsEditorMode || AutoCaptions;

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
    /// Where the captions are drawn on the output frame: a layer like the layers, placed and sized on the
    /// Edit Layout canvas, and always the top one.
    /// </summary>
    public Layer CaptionLayer { get; } = CreateCaptionLayer();

    private static Layer CreateCaptionLayer() => new()
    {
        Kind = LayerKind.Captions,
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

    private void SetCaptionLayer(LayerState? state)
    {
        (CaptionLayer.PositionX, CaptionLayer.PositionY) = (state?.PositionX ?? DefaultCaptionX, state?.PositionY ?? DefaultCaptionY);
        CaptionLayer.SizeWidth = state is { SizeWidth: > 0 } ? state.SizeWidth : DefaultCaptionWidth;
        CaptionLayer.SizeHeight = state is { SizeHeight: > 0 } ? state.SizeHeight : DefaultCaptionHeight;
        CaptionLayer.Opacity = Math.Clamp(state?.Opacity ?? 100, 0, 100);

        // The same style a picture has: corners, soft edges and a drop shadow.
        (CaptionLayer.CornerRadius, CaptionLayer.Feather) = (Math.Clamp(state?.CornerRadius ?? 0, 0, 50), state?.Feather ?? false);
        (CaptionLayer.FeatherRadius, CaptionLayer.Shadow) = (state?.FeatherRadius ?? 12, state?.Shadow ?? false);
        (CaptionLayer.ShadowOpacity, CaptionLayer.ShadowOffset) = (state?.ShadowOpacity ?? 0.5, state?.ShadowOffset ?? 10);
        (CaptionLayer.CustomMask, CaptionLayer.MaskPath) = (state?.CustomMask ?? false, state?.MaskPath ?? "");
    }

    [RelayCommand]
    private void ResetCaptionLayer() => SetCaptionLayer(null);

    /// <summary>The layout pane has something to show with the engine on, or with captions to place.</summary>
    public bool CanEditLayout => FrameEngine || AutoCaptions;

    partial void OnAutoCaptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditLayout));
        RefreshLayerRows();
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

        // A preview of the captions shows the style they have now.
        if (PreviewSubtitles)
            _ = RefreshLiveCaptionsAsync(asked: false);
    }

    // ----- Preview Subtitles -----
    // The captions as they will be drawn, over the picture in the player: the words whisper heard, in the
    // style that is set, in the caption box where it sits. The file is made for the preview and timed to the
    // sequence as it plays (cuts and all), which the encode's own file, timed to the finished output, is not.
    //
    // The player draws it itself, as a subtitle track of its own (see MpvPlayer.SetSubtitleFile), and not
    // Live Preview's filter graph: there every frame had the captions drawn on it by the processor, which a
    // long video could not keep up with. So the file is written for the whole frame, not for the caption box,
    // and carries of the caption layer's look what a subtitle style can: its opacity and its drop shadow. The
    // rounded or soft edges and the mask are the export's alone.

    /// <summary>Whether the player draws the styled captions. Switching it on makes them first, which takes a transcription the first time.</summary>
    [ObservableProperty] private bool _previewSubtitles;

    /// <summary>The caption file the player is to draw over the picture, or an empty string for none.</summary>
    public string LiveCaptionsPath =>
        // Encoder Mode has no timeline for captions to be seen on: they are made for its export, and not shown in its player.
        IsSubtitleEditorOpen || IsEncoderMode ? "" : PreviewSubtitles && _liveCaptionsPath.Length > 0 ? _liveCaptionsPath : LiveImageSubtitlePath;

    private string _liveCaptionsPath = "";
    private int _liveCaptionsRun;

    partial void OnPreviewSubtitlesChanged(bool value)
    {
        if (value)
        {
            _ = RefreshLiveCaptionsAsync();
            return;
        }

        DeleteLiveCaptions(_liveCaptionsPath);
        _liveCaptionsPath = "";
        if (!_isBackgroundWorker)
            LiveFilterInvalidated?.Invoke();
    }

    private CancellationTokenSource? _liveCaptionsWait;

    /// <summary>Writes the preview's caption file again once the caption box has stopped being moved, resized or restyled.</summary>
    private async void RefreshLiveCaptionsSoon()
    {
        _liveCaptionsWait?.Cancel();
        var wait = _liveCaptionsWait = new CancellationTokenSource();
        try
        {
            await Task.Delay(600, wait.Token);
            await RefreshLiveCaptionsAsync(asked: false);
        }
        catch (OperationCanceledException)
        {
            // Changed again: the later change writes the file.
        }
    }

    /// <summary>Writes the preview's caption file again (the words are kept, so only the first time transcribes) and has the player draw it.</summary>
    /// <param name="asked">Whether the button was pressed. A refresh that follows a change of style or size is simply skipped while something else is running.</param>
    private async Task RefreshLiveCaptionsAsync(bool asked = true)
    {
        if (_isBackgroundWorker || (!asked && (IsBusy || !PreviewSubtitles)))
            return;

        if (!HasSource || !AutoCaptions || IsBusy)
        {
            var why = !HasSource ? "Load a video first: there is nothing to make captions from."
                : !AutoCaptions ? "Switch Auto-Captions on first: the preview shows the captions it makes."
                : "Wait for the running operation to finish, then press Preview Subtitles again.";
            PreviewSubtitles = false;
            StatusText = why;
            return;
        }

        await RunOperationAsync(async cancellationToken =>
        {
            // A new name each time: the player only loads the file again when its name has changed.
            var path = Path.Combine(_workFolder, $"captions_live_{++_liveCaptionsRun}.ass");

            // The whole of what is listened to, on its own clock: the preview plays the sequence, not the cut output.
            if (await PrepareCaptionsAsync([], path, CaptionUseVoiceover ? VoiceoverStartSeconds : 0, cancellationToken, forPlayer: true) is { } problem)
            {
                PreviewSubtitles = false;
                return problem;
            }

            // The player read the earlier file whole when it was given it, so that one can go.
            var before = _liveCaptionsPath;
            _liveCaptionsPath = path;
            DeleteLiveCaptions(before);

            LiveFilterInvalidated?.Invoke();
            return LivePreview
                ? "Subtitles preview on: the player draws the captions over the picture as they are styled."
                : "Subtitles preview on: the player draws the captions over the source. Switch Live Preview on to see them on the finished frame.";
        });
    }

    private static void DeleteLiveCaptions(string path)
    {
        try
        {
            if (path.Length > 0)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere; it is in the session folder and goes when the application closes.
        }
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
            if (HasTextCues)
            {
                return VideoEncoder.Family == EncoderFamily.Copy && !IsAnimatedOutput
                    ? "Captions are drawn into the picture, so they need a video encoder other than Copy (Video tab)."
                    : $"The captions are drawn from the subtitle track ({SubtitleCues.Count} cue{(SubtitleCues.Count == 1 ? "" : "s")}), in the caption style. Nothing is transcribed while it is there.";
            }

            if (!File.Exists(DependencyUpdater.WhisperPath))
                return DependencyUpdater.IsWhisperOverridden
                    ? $"The whisper.exe set in Settings (Tools) was not found: {DependencyUpdater.WhisperPath}"
                    : "whisper.cpp is not installed yet: use Install / Update All Dependencies in Settings (Tools).";
            if (!File.Exists(DependencyUpdater.WhisperModelPath))
                return $"The speech model {AppSettings.Current.WhisperModel} is not downloaded yet: see Settings (Tools).";
            if (VideoEncoder.Family == EncoderFamily.Copy && !IsAnimatedOutput)
                return "Captions are drawn into the picture, so they need a video encoder other than Copy (Video tab).";
            if (!File.Exists(DependencyUpdater.VadModelPath))
                return "The words are transcribed when you press Start Encode or Render Preview. Silence detection (VAD) is not installed, so quiet stretches may get words that were never said: see Settings (Tools).";
            return "The words are transcribed when you press Start Encode or Render Preview.";
        }
    }

    partial void OnShowAutoCaptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLegacySubtitles));
        OnPropertyChanged(nameof(ShowSubtitleImportRow));
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

    private bool CommandUsesCaptions(string command) =>
        command.Contains(FilterGraphBuilder.CaptionFilter(CaptionsFilePath), StringComparison.OrdinalIgnoreCase);

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
    /// <param name="forPlayer">
    /// Writes the file for the whole output frame, with the words where the caption box is, for the player to
    /// draw by itself. Otherwise it is written for the box, which is how the export's graph draws it.
    /// </param>
    private async Task<string?> PrepareCaptionsAsync(
        List<(double Start, double End)> ranges, string assPath, double timeOffset, CancellationToken cancellationToken, bool forPlayer = false)
    {
        // A subtitle track with words is what the captions say: nothing is listened to.
        if (HasTextCues)
        {
            var lines = GetCueCaptions(ranges);
            WriteCaptionFile(lines.SelectMany(l => l).ToList(), lines, assPath, 0, forPlayer);
            StatusText = $"Captions: {lines.Count} subtitle cue{(lines.Count == 1 ? "" : "s")} written.";
            return null;
        }

        // Editor Mode exports what is on the timeline and nothing else: with no subtitles there, nothing is
        // transcribed behind the user's back. Captions are made with Generate Captions, and then they are there.
        if (IsEditorMode)
            return "There are no captions on the timeline: press Generate Captions on the Subtitles tab first, or export without them.";

        var (words, problem) = await GetCaptionWordsAsync(ranges, cancellationToken);
        if (words is null)
            return problem;

        // What the preview shows is what the caption layer's time bar shows: the captions, on the timeline's clock.
        if (forPlayer)
        {
            _generatedCaptions = CaptionGenerator.Lines(words, CaptionStyle)
                .Select(line => line.Select(w => w with { Start = w.Start + timeOffset, End = w.End + timeOffset }).ToList()).ToList();
            TimelineChanged?.Invoke();
        }

        WriteCaptionFile(words, null, assPath, timeOffset, forPlayer);
        StatusText = $"Captions: {words.Count} word{(words.Count == 1 ? "" : "s")} written.";
        return null;
    }

    /// <summary>The words that are spoken, transcribed with whisper.cpp (or as they were kept from the last time); or why they could not be.</summary>
    private async Task<(List<CaptionWord>? Words, string? Problem)> GetCaptionWordsAsync(List<(double Start, double End)> ranges, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.WhisperPath))
            return (null, "Auto-captions need whisper.cpp: use Install / Update All Dependencies in Settings (Tools), or set the path to your own whisper.exe there.");

        var modelPath = DependencyUpdater.WhisperModelPath;
        if (!File.Exists(modelPath))
            return (null, $"Auto-captions need the speech model {AppSettings.Current.WhisperModel}: download it in Settings (Tools).");

        // What to listen to: one of the video's tracks, the first track of a separate file, or the voiceover.
        string audioPath;
        var trackIndex = 0;
        if (CaptionUseVoiceover)
        {
            if (await GetTrimmedVoiceoverAsync(cancellationToken) is not { } voiceover)
                return (null, "Auto-captions are set to the Recorded Voiceover Track, but there is no voiceover yet (Voiceover Studio, on the Audio tab).");
            audioPath = voiceover;
        }
        else if (CaptionUseExternalAudio)
        {
            audioPath = CaptionAudioPath.Trim().Trim('"');
            if (audioPath.Length == 0)
                return (null, "Auto-captions are set to an External Audio File, but none is chosen (Subtitles tab).");
            if (!File.Exists(audioPath))
                return (null, $"The external audio file for captions was not found: {audioPath}");
        }
        else
        {
            if (_mediaInfo is { Audio.Count: 0 })
                return (null, "This video has no audio to make captions from. Choose an External Audio File, or switch Auto-Captions off.");
            audioPath = LocalMediaPath;
            trackIndex = CaptionAudioTrack?.Index ?? 0;
        }

        var (language, prompt, translate) = (WhisperLanguage, WhisperPrompt.Trim(), WhisperTranslate);

        // Voice activity detection: whisper is only handed the stretches with speech in them, so it has no
        // silence to make words up for. Used whenever its model is there and this whisper.exe knows the option.
        var vadModel = File.Exists(DependencyUpdater.VadModelPath) && await CaptionGenerator.SupportsVadAsync(cancellationToken)
            ? DependencyUpdater.VadModelPath
            : null;

        var key = $"{audioPath}|{trackIndex}|{File.GetLastWriteTimeUtc(audioPath).Ticks}|{modelPath}|{language}|{translate}|{prompt}|{vadModel is not null}|"
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

                StatusText = $"Captions: transcribing with {Path.GetFileName(modelPath)}{(vadModel is null ? "" : ", skipping silence")}...";
                // Whisper says how far through the sound it is; the bar shows that, and no longer just that something is happening.
                var heard = new Progress<double>(percent =>
                {
                    IsProgressIndeterminate = false;
                    ProgressValue = percent;
                });
                words = await Task.Run(
                    () => CaptionGenerator.TranscribeAsync(wavPath, modelPath, language, prompt, translate, vadModel, cancellationToken, heard), cancellationToken);
            }
            finally
            {
                File.Delete(wavPath);
            }

            _captionWords[key] = words;
        }

        return (words, null);
    }

    /// <summary>Writes the captions as an ASS file: for the caption box, which is how the export draws them, or for the whole frame, for a player.</summary>
    /// <param name="lines">The captions line by line, when they are given that way (a subtitle track's cues); otherwise the words are grouped by the style.</param>
    private void WriteCaptionFile(IReadOnlyList<CaptionWord> words, IReadOnlyList<List<CaptionWord>>? lines, string assPath, double timeOffset, bool forPlayer)
    {
        // The file is written for the caption box, not the frame: that is the canvas it is drawn on.
        // Text size follows the frame (so a style looks the same at 720p and 4K) and the height of the
        // box (so making the box taller makes the words bigger).
        var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        var (x, y, width, height) = GetCaptionRect(frameWidth, frameHeight);
        var textScale = Math.Min(frameWidth, frameHeight) / 1080.0 * (height / (DefaultCaptionHeight * frameHeight));
        var frame = forPlayer
            ? new CaptionFramePlacement(x, y, frameWidth, frameHeight, CaptionLayer.Opacity / 100.0,
                CaptionLayer.Shadow ? Math.Clamp(CaptionLayer.ShadowOffset, 0, 200) : 0, CaptionLayer.ShadowOpacity)
            : null;
        CaptionGenerator.WriteAss(words, CaptionStyle, width, height, textScale, assPath, timeOffset, frame, lines);
    }

    // ----- whisper.cpp model -----

    // The models by the names they are known by: "base.en", "large-v3", "turbo". Their files are "ggml-<name>.bin".
    private const string TurboFile = "ggml-large-v3-turbo.bin";

    private static string ModelLabel(string file) =>
        file == TurboFile ? "turbo" : file.StartsWith("ggml-", StringComparison.Ordinal) && file.EndsWith(".bin", StringComparison.Ordinal) ? file[5..^4] : file;

    public IReadOnlyList<string> WhisperModelChoices { get; } = DependencyUpdater.WhisperModels.Select(ModelLabel).ToList();

    /// <summary>
    /// The speech model the captions are transcribed with, for the box on the Subtitles tab. Choosing one
    /// makes it the model in the settings; when its file has not been downloaded yet, the download starts.
    /// </summary>
    public string WhisperModelChoice
    {
        get => ModelLabel(AppSettings.Current.WhisperModel);
        set
        {
            var file = DependencyUpdater.WhisperModels.FirstOrDefault(m => ModelLabel(m) == value);
            if (file is null || file == AppSettings.Current.WhisperModel)
                return;

            SaveView(settings => settings.WhisperModel = file);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CaptionHint));
            OnPropertyChanged(nameof(WhisperModelStatus));
            if (!File.Exists(DependencyUpdater.WhisperModelPath) && DownloadWhisperModelCommand.CanExecute(file))
                DownloadWhisperModelCommand.Execute(file);
            else if (PreviewSubtitles)
                RefreshLiveCaptionsSoon();
        }
    }

    public string WhisperModelStatus =>
        File.Exists(DependencyUpdater.WhisperModelPath) ? "downloaded" : "not downloaded yet: it is fetched when chosen";

    /// <summary>Downloads a speech model into the models folder.</summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task DownloadWhisperModelAsync(string? model) => RunOperationAsync(async cancellationToken =>
    {
        if (string.IsNullOrWhiteSpace(model))
            return "Choose a model first.";

        IsProgressIndeterminate = true;
        var outcome = await DependencyUpdater.DownloadWhisperModelAsync(model, new Progress<string>(ReportStatus), cancellationToken);
        OnPropertyChanged(nameof(CaptionHint));
        OnPropertyChanged(nameof(WhisperModelStatus));
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
