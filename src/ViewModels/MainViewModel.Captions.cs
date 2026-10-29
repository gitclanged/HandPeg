using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
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

    /// <summary>Whether the caption options can be set: always, since Generate Captions uses them too.</summary>
    public bool CaptionOptionsEnabled => true;

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
    public IReadOnlyList<string> WhisperLanguages => KnownWhisperLanguages;

    public static IReadOnlyList<string> KnownWhisperLanguages { get; } =
        ["auto", "en", "es", "fr", "de", "it", "pt", "nl", "pl", "sv", "tr", "ru", "uk", "ar", "hi", "ja", "ko", "zh"];

    /// <summary>Words, names and spellings to expect: whisper's initial prompt.</summary>
    [ObservableProperty] private string _whisperPrompt = "";

    // Starts as the settings' default language; a preset or a project then sets its own.
    [ObservableProperty]
    private string _whisperLanguage = KnownWhisperLanguages.Contains(AppSettings.Current.WhisperDefaultLanguage) ? AppSettings.Current.WhisperDefaultLanguage : "en";

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
        CaptionLayer.FeatherRadius = state?.FeatherRadius ?? 12;
        CaptionLayer.TakeShadow(state);
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
        SyncEditorCaptions();
    }

    /// <summary>The caption box on an output frame of the given size, in pixels, with even sides as a video frame needs.</summary>
    private (int X, int Y, int Width, int Height) GetCaptionRect(int frameWidth, int frameHeight)
    {
        var (x, y, width, height) = CaptionLayer.GetOutputRect(frameWidth, frameHeight, 0, 0);
        return (x, y, Math.Max(width, 16), Math.Max(height, 16));
    }

    /// <summary>
    /// Which of the video's audio tracks is transcribed: the one last marked as the Voice track (4.3E), or else the first,
    /// unless another is chosen. Its stream is the one whisper's audio is cut from (CaptionGenerator.BuildExtractCommand's "0:a:N").
    /// </summary>
    [ObservableProperty] private AudioTrack? _captionAudioTrack;

    /// <summary>The separate audio file: a clean voice recording in sync with the video, say.</summary>
    [ObservableProperty] private string _captionAudioPath = "";

    /// <summary>After the track list has been rebuilt for a new source: the choice falls back to the Voice track, or the first track.</summary>
    private void KeepCaptionTrackValid()
    {
        if (CaptionAudioTrack is null || !AudioTracks.Contains(CaptionAudioTrack))
            CaptionAudioTrack = AudioTracks.FirstOrDefault(t => t.IsVoice) ?? AudioTracks.FirstOrDefault();
    }

    /// <summary>
    /// A track of the video has just been marked as the Voice track (a tick on the Audio tab, its menu, a preset, or found by
    /// its name on loading, 4.3E): Auto-Captions listen to it from now on, while they listen to the video's own tracks. A
    /// separate audio file or the voiceover, chosen in the Auto-Captions dialog, stays chosen, and any track can still be
    /// picked there afterwards. A project's own choice is put back after its tracks' flags (RestoreStateAsync), so it wins.
    /// </summary>
    private void ListenToVoice(AudioTrack voice)
    {
        if (CaptionUseVideoAudio && AudioTracks.Contains(voice))
        {
            CaptionAudioTrack = voice;
            if (_captionSourceChoice is { Kind: CaptionSourceKind.MainFile })
                _captionSourceChoice = null;
        }
    }

    /// <summary>
    /// A sound clip has just been marked as the voice (its Voiceover / Dialogue tick, 4.4), or unmarked: marked, Auto-Captions
    /// listen to it from now on, as they do to a Voice track (a separate file or the voiceover, chosen in the Auto-Captions
    /// dialog, stays chosen); unmarked, the choice of it goes, and they fall back to what is set.
    /// </summary>
    private void ListenToVoice(Layer clip)
    {
        if (!clip.CarriesSound || clip.IsMainVideo)
            return;

        var source = new CaptionSource(CaptionSourceKind.Clip, clip.ImagePath, Math.Max(ClipOf(clip)?.AudioStreamIndex ?? 0, 0));
        if (clip.IsVoice && CaptionUseVideoAudio)
            _captionSourceChoice = source;
        else if (!clip.IsVoice && source.SameAs(_captionSourceChoice))
            _captionSourceChoice = null;
        OnPropertyChanged(nameof(CaptionSourceSummary));
    }

    /// <summary>Has Auto-Captions listen to one of the video's own audio tracks, as its right-click menu asks (4.3E).</summary>
    public void UseTrackForCaptions(AudioTrack track)
    {
        if (!AudioTracks.Contains(track))
            return;

        ChooseCaptionSource(new CaptionSource(CaptionSourceKind.MainFile, LocalMediaPath, track.Index));
        StatusText = $"Auto-captions now listen to {track.Title}.";
    }

    public CaptionStyle CaptionStyle { get; private set; } = AppSettings.Current.DefaultCaptionStyle?.Clone() ?? new();

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
        IsSubtitleEditorOpen ? "" : PreviewSubtitles && _liveCaptionsPath.Length > 0 ? _liveCaptionsPath : LiveImageSubtitlePath;

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
            InvalidateLiveFilter();
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

        await RunOperationAsync("Making captions", WorkLane.Interactive, async cancellationToken =>
        {
            // A new name each time: the player only loads the file again when its name has changed.
            var path = Path.Combine(_workFolder, $"captions_live_{++_liveCaptionsRun}.ass");

            // On the sequence's clock, which is what the player plays: the words are shown through the clips that play them.
            if (await PrepareCaptionsAsync([], path, 0, cancellationToken, forPlayer: true) is { } problem)
            {
                PreviewSubtitles = false;
                return problem;
            }

            // The same captions as the file the player was given (4.4-BugHunt): an edit that moved none of them, the words heard
            // again. That file stays, and the player is not handed a new one: each new file swaps its subtitle track (see
            // MpvPlayer's "Subtitles"), and the live graph would be made again for nothing.
            var before = _liveCaptionsPath;
            if (before.Length > 0 && await Task.Run(() => SameFileContents(before, path)))
            {
                DeleteLiveCaptions(path);
            }
            else
            {
                // The player read the earlier file whole when it was given it, so that one can go.
                _liveCaptionsPath = path;
                DeleteLiveCaptions(before);
                InvalidateLiveFilter();
            }

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

    /// <summary>Whether two files hold the same bytes; false when either cannot be read.</summary>
    private static bool SameFileContents(string first, string second)
    {
        try
        {
            if (new FileInfo(first) is not { Exists: true } one || new FileInfo(second) is not { Exists: true } other || one.Length != other.Length)
                return false;

            return File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
            const string when = "The words are transcribed when the video is exported, or now with Generate Captions, to see and edit them first.";
            if (!File.Exists(DependencyUpdater.VadModelPath))
                return $"{when} Silence detection (VAD) is not installed, so quiet stretches may get words that were never said: see Settings (Tools).";
            return when;
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

        return await PrepareCaptionsAsync([], CaptionsFilePath, 0, cancellationToken);
    }

    // ----- What captions listen to, and where they are on the timeline (4.4) -----

    // The source last chosen by hand (the Auto-Captions dialog, a track's or a clip's menu); null to follow the defaults.
    private CaptionSource? _captionSourceChoice;

    /// <summary>
    /// Makes a source what Auto-Captions listen to from now on: the voiceover and an external file through their switches
    /// (which a project keeps), one of the video's tracks or a clip's file as the choice made by hand.
    /// </summary>
    public void ChooseCaptionSource(CaptionSource source)
    {
        switch (source.Kind)
        {
            case CaptionSourceKind.Voiceover:
                CaptionUseVoiceover = true;
                _captionSourceChoice = null;
                break;
            case CaptionSourceKind.External:
                (CaptionAudioPath, CaptionUseExternalAudio) = (source.Path, true);
                _captionSourceChoice = null;
                break;
            case CaptionSourceKind.MainFile:
                CaptionUseVideoAudio = true;
                if (AudioTracks.FirstOrDefault(t => t.Index == source.Stream) is { } track)
                    CaptionAudioTrack = track;
                _captionSourceChoice = source;
                break;
            default:
                CaptionUseVideoAudio = true;
                _captionSourceChoice = source;
                break;
        }

        OnPropertyChanged(nameof(CaptionSourceSummary));
    }

    /// <summary>
    /// What captions are made from, for the request given: a track of the video (<see cref="AudioTrack"/>), a clip with sound
    /// (<see cref="Layer"/>), a <see cref="CaptionSource"/>, or null for what is set. With nothing given: the voiceover or the
    /// external file when switched to; else the source last chosen by hand; else what the captions on the timeline were made
    /// from; else a sound clip marked as the voice (4.4); else the video's chosen track (the Voice track once one is marked),
    /// or its first. Null when there is nothing to listen to.
    /// </summary>
    public CaptionSource? ResolveCaptionSource(object? requested = null)
    {
        switch (requested)
        {
            case CaptionSource given:
                return given;
            case AudioTrack track when AudioTracks.Contains(track):
                return new CaptionSource(CaptionSourceKind.MainFile, LocalMediaPath, track.Index);
            case Layer { CarriesSound: true, IsMainVideo: false } clip when ClipOf(clip) is { } model:
                return new CaptionSource(CaptionSourceKind.Clip, clip.ImagePath, Math.Max(model.AudioStreamIndex, 0));
        }

        if (CaptionUseVoiceover)
            return new CaptionSource(CaptionSourceKind.Voiceover, VoiceoverPath, 0);
        if (CaptionUseExternalAudio)
        {
            // A file the timeline plays as clips of its own is followed through them.
            var path = CaptionAudioPath.Trim().Trim('"');
            var clip = new CaptionSource(CaptionSourceKind.Clip, path, 0);
            return PlaysCaptionSource(clip) ? clip : new CaptionSource(CaptionSourceKind.External, path, 0);
        }

        if (_captionSourceChoice is { } chosen && PlaysCaptionSource(chosen))
            return chosen;
        if (_captionAnchorSource is { } anchored && PlaysCaptionSource(anchored))
            return anchored;

        var voice = Layers.FirstOrDefault(l => l is { IsVoice: true, CarriesSound: true, IsHidden: false, IsMainVideo: false } && ClipOf(l) is not null);
        if (voice is not null)
            return new CaptionSource(CaptionSourceKind.Clip, voice.ImagePath, Math.Max(ClipOf(voice)!.AudioStreamIndex, 0));

        if (!HasSource)
            return null;
        var listened = CaptionAudioTrack is { } set && AudioTracks.Contains(set) ? set : AudioTracks.FirstOrDefault(t => t.IsVoice) ?? AudioTracks.FirstOrDefault();
        return new CaptionSource(CaptionSourceKind.MainFile, LocalMediaPath, listened?.Index ?? 0);
    }

    /// <summary>What Auto-Captions listen to now, in a few words: for the Auto-Captions dialog and the track header's tooltip.</summary>
    public string CaptionSourceSummary => ResolveCaptionSource() is { } source ? DescribeCaptionSource(source) : "Nothing to listen to yet.";

    /// <summary>A source in a few words.</summary>
    public string DescribeCaptionSource(CaptionSource source) => source.Kind switch
    {
        CaptionSourceKind.MainFile => source.Stream < 0 ? "The main video"
            : $"The main video: {AudioTracks.FirstOrDefault(t => t.Index == source.Stream)?.Title ?? $"audio track {source.Stream + 1}"}",
        CaptionSourceKind.Clip => $"Sound clip: {Path.GetFileName(source.Path)}",
        CaptionSourceKind.External => $"External audio file: {Path.GetFileName(source.Path)}",
        _ => "Recorded voiceover",
    };

    /// <summary>Whether something on the timeline still plays a source: a clip of its file, the main file, the voiceover.</summary>
    private bool PlaysCaptionSource(CaptionSource source) => source.Kind switch
    {
        CaptionSourceKind.MainFile => HasSource && string.Equals(source.Path, LocalMediaPath, StringComparison.OrdinalIgnoreCase),
        CaptionSourceKind.Clip => source.Path.Length > 0 && CaptionClips(source).Any(),
        CaptionSourceKind.External => source.Path.Length > 0,
        _ => HasVoiceover,
    };

    /// <summary>The clips, other than the main video's, that play a source's file with sound.</summary>
    private IEnumerable<(TimelineTrackModel Track, TimelineClip Clip)> CaptionClips(CaptionSource source) =>
        Document.Tracks.SelectMany(t => t.Clips.Where(c => c.ContentKind != LayerKind.MainVideo && c.HasSound
            && string.Equals(c.SourcePath, source.Path, StringComparison.OrdinalIgnoreCase)).Select(c => (t, c)));

    /// <summary>
    /// Every window of the timeline that plays a source, in order (4.4): the main video's clips for the main file (its
    /// stream's J and L cuts and slips included; the picture's own place for stream -1), the clips of a file for a clip's
    /// source, where the voiceover is placed for it, and the timeline's own clock for an external recording (or a main
    /// video that is the whole sequence, with the frame engine off).
    /// </summary>
    private List<CaptionSpan> CaptionSpans(CaptionSource source)
    {
        var spans = new List<CaptionSpan>();
        switch (source.Kind)
        {
            case CaptionSourceKind.MainFile:
                if (!FrameEngine)
                {
                    spans.Add(CaptionSpan.Identity);
                    break;
                }

                var sound = source.Stream >= 0;
                var slip = sound ? AudioTracks.FirstOrDefault(t => t.Index == source.Stream)?.OffsetSeconds ?? 0 : 0;
                foreach (var (_, clip) in MainVideoClips())
                {
                    if (string.Equals(clip.SourcePath, source.Path, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(clip.SourcePath))
                        spans.Add(SpanOf(clip, sound, slip));
                }

                break;
            case CaptionSourceKind.Clip:
                foreach (var (_, clip) in CaptionClips(source))
                    spans.Add(SpanOf(clip, sound: true, slip: 0));
                break;
            case CaptionSourceKind.Voiceover:
                var (from, to) = (Math.Max(VoiceoverTrimStart, 0), VoiceoverTrimEnd > 0 ? Math.Min(VoiceoverTrimEnd, VoiceoverDuration) : VoiceoverDuration);
                spans.Add(to - from > 0.05 ? new CaptionSpan(VoiceoverStartSeconds, VoiceoverStartSeconds + (to - from), from, 1)
                    : new CaptionSpan(VoiceoverStartSeconds, CaptionSpan.Identity.End, 0, 1));
                break;
            default:
                spans.Add(CaptionSpan.Identity);
                break;
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    /// <summary>A clip's window onto its file: its picture's, or its sound's (with its J or L cut, its slip while unlinked, and the stream's own slip).</summary>
    private static CaptionSpan SpanOf(TimelineClip clip, bool sound, double slip)
    {
        var speed = clip.Speed > 0 ? clip.Speed : 1;
        var (lead, tail) = sound ? (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) : (0, 0);
        var shift = slip + (sound && !clip.AudioLinked ? clip.AudioSlipSeconds : 0);
        var start = clip.Start - lead;
        return new CaptionSpan(start + shift, Math.Max(clip.End + tail, start) + shift, clip.MediaOffset - lead * speed, speed);
    }

    /// <summary>How long a source is, in its own seconds; 0 when it is not known.</summary>
    private double CaptionSourceSeconds(CaptionSource source) => source.Kind switch
    {
        CaptionSourceKind.MainFile => MainMediaSeconds,
        CaptionSourceKind.Clip => CaptionClips(source).Select(c => c.Clip.SourceDuration).DefaultIfEmpty(0).Max(),
        CaptionSourceKind.Voiceover => VoiceoverDuration,
        _ => 0,
    };

    /// <summary>The document track the captions follow (4.4): the main video's, or the first clip's that plays their source; null for none.</summary>
    public Guid? CaptionAnchorTrackId => _captionAnchorSource switch
    {
        { Kind: CaptionSourceKind.MainFile } => MainVideoClips().Select(m => (Guid?)m.Track.Id).FirstOrDefault(),
        { Kind: CaptionSourceKind.Clip } source => CaptionClips(source).OrderBy(c => c.Clip.Start).Select(c => (Guid?)c.Track.Id).FirstOrDefault(),
        _ => null,
    };

    /// <summary>The captions are anchored to the main file (its sound or its picture): their row is a child of the main video's.</summary>
    public bool CaptionsFollowMainFile => _captionAnchorSource is { Kind: CaptionSourceKind.MainFile };

    /// <summary>
    /// The pipeline: extract the audio that will be heard as a 16 kHz mono wave file, transcribe it with whisper.cpp, show
    /// its words on the timeline through the clips that play them, and write them as an ASS file in the chosen style. A
    /// subtitle track with words is written as it stands instead. Returns null, or what went wrong.
    /// </summary>
    /// <param name="ranges">
    /// The stretches of the sequence the output plays, joined end to end (a Render Preview's); none for the whole sequence.
    /// </param>
    /// <param name="timeOffset">Added to every time of the output: for an output that keeps another clock.</param>
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
            var cues = CaptionProjection.ToOutputClock(GetCueCaptions(), ranges, timeOffset);
            WriteCaptionFile(cues.SelectMany(l => l).ToList(), cues, assPath, 0, forPlayer);
            StatusText = $"Captions: {cues.Count} subtitle cue{(cues.Count == 1 ? "" : "s")} written.";
            return null;
        }

        if (ResolveCaptionSource() is not { } source)
            return "There is nothing to make captions from: load a video, or choose what to listen to (Generate Auto-Captions...).";

        var (words, problem) = await GetCaptionWordsAsync(source, cancellationToken);
        if (words is null)
            return problem;

        // Heard in the source's time; shown through every clip that plays it, on the sequence's clock.
        var lines = CaptionGenerator.Lines(CaptionProjection.ProjectWords(words, CaptionSpans(source)), CaptionStyle);

        // What the preview shows is what the caption row shows: the captions, on the timeline's clock.
        if (forPlayer)
        {
            _generatedCaptions = lines;
            TimelineChanged?.Invoke();
        }

        var output = CaptionProjection.ToOutputClock(lines, ranges, timeOffset);
        WriteCaptionFile(output.SelectMany(l => l).ToList(), output, assPath, 0, forPlayer);
        StatusText = $"Captions: {output.Sum(l => l.Count)} word{(output.Sum(l => l.Count) == 1 ? "" : "s")} written.";
        return null;
    }

    /// <summary>
    /// The words spoken in a source, in its own time, transcribed with whisper.cpp (or as they were kept from the last time);
    /// or why they could not be. Only the stretches the timeline plays are listened to, with a little around them.
    /// </summary>
    private async Task<(List<CaptionWord>? Words, string? Problem)> GetCaptionWordsAsync(CaptionSource source, CancellationToken cancellationToken)
    {
        var (job, problem) = CreateCaptionJob(source, assPath: "", timeOffset: 0);
        return job is null ? (null, problem) : (await HearCaptionsAsync(job, cancellationToken), null);
    }

    /// <summary>
    /// Everything a transcription takes, gathered now and without listening to anything: what is heard, how
    /// whisper is set, the clips the words are shown through, and the style and size the words are written in. Run at once
    /// by an export; kept by a queued job until it runs. Returns the reason instead when captions cannot be made from the source.
    /// </summary>
    private (CaptionJob? Job, string? Problem) CreateCaptionJob(CaptionSource source, string assPath, double timeOffset)
    {
        if (!File.Exists(DependencyUpdater.WhisperPath))
            return (null, "Auto-captions need whisper.cpp: use Install / Update All Dependencies in Settings (Tools), or set the path to your own whisper.exe there.");

        var modelPath = DependencyUpdater.WhisperModelPath;
        if (!File.Exists(modelPath))
            return (null, $"Auto-captions need the speech model {AppSettings.Current.WhisperModel}: download it in Settings (Tools).");

        // What to listen to: one of the video's tracks, a clip's file, a separate file, or the voiceover; and which of it.
        var spans = CaptionSpans(source);
        var audioPath = source.Path;
        var trackIndex = Math.Max(source.Stream, 0);
        List<(double Start, double End)> ranges = [];
        switch (source.Kind)
        {
            case CaptionSourceKind.Voiceover:
                if (!HasVoiceover || !File.Exists(VoiceoverPath))
                    return (null, "Auto-captions are set to the Recorded Voiceover Track, but there is no voiceover yet (Voiceover Studio, on the Audio tab).");
                audioPath = VoiceoverPath;

                // The part of the recording between its trim handles is all that is heard, as it is all that is mixed in.
                var (start, end) = (Math.Max(VoiceoverTrimStart, 0), Math.Min(VoiceoverTrimEnd, VoiceoverDuration));
                ranges = start < 0.05 && end > VoiceoverDuration - 0.05 || end - start < 0.1 ? [] : [(start, end)];
                break;
            case CaptionSourceKind.External:
                audioPath = audioPath.Trim().Trim('"');
                if (audioPath.Length == 0)
                    return (null, "Auto-captions are set to an External Audio File, but none is chosen (Generate Auto-Captions...).");
                if (!File.Exists(audioPath))
                    return (null, $"The external audio file for captions was not found: {audioPath}");
                break;
            // A file the timeline plays as clips is heard whole (4.4): its words are kept in its own time, so the same
            // transcription serves every cut, trim, fold and undo after it, and a stretch put back brings its words back.
            case CaptionSourceKind.MainFile:
                if (_mediaInfo is { Audio.Count: 0 })
                    return (null, "This video has no audio to make captions from. Choose another sound to listen to, or switch Auto-Captions off.");
                if (spans.Count == 0)
                    return (null, "The main video is not on the timeline: there is nothing of it to make captions for.");
                break;
            default:
                if (spans.Count == 0)
                    return (null, $"{Path.GetFileName(source.Path)} is not on the timeline any more: there is nothing of it to make captions for.");
                if (!File.Exists(audioPath))
                    return (null, $"The sound to make captions from was not found: {audioPath}");
                break;
        }

        // Written for the caption box, as an export draws it: see WriteCaptionFile.
        var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        var (_, _, width, height) = GetCaptionRect(frameWidth, frameHeight);
        var textScale = Math.Min(frameWidth, frameHeight) / 1080.0 * (height / (DefaultCaptionHeight * frameHeight));

        // How whisper decodes and what counts as silence, from the settings as they are now.
        return (new CaptionJob(audioPath, trackIndex, ranges, modelPath, WhisperLanguage, WhisperPrompt.Trim(), WhisperTranslate,
            AppSettings.Current.UseVad, WhisperOptions.FromSettings(AppSettings.Current), CaptionStyle.Clone(), width, height, textScale, timeOffset, assPath,
            spans), null);
    }

    /// <summary>
    /// Listens: extracts what the job names as a 16 kHz mono wave file and has whisper.cpp find the words in
    /// it, or takes them as they were kept from the last time the same thing was heard the same way.
    /// </summary>
    /// <param name="label">What the status bar calls the step: "Captions" for an export, the job's name for the queue.</param>
    /// <param name="onPercent">Told how far through the sound whisper is, as well as the progress bar.</param>
    private async Task<List<CaptionWord>> HearCaptionsAsync(CaptionJob job, CancellationToken cancellationToken, string label = "Captions", Action<double>? onPercent = null)
    {
        // A job that waited in the queue may find that what it was to use has gone since.
        if (!File.Exists(DependencyUpdater.WhisperPath))
            throw new InvalidOperationException("whisper.cpp is not installed any more: see Settings (Tools & Backup).");
        if (!File.Exists(job.ModelPath))
            throw new InvalidOperationException($"the speech model {Path.GetFileName(job.ModelPath)} is not there any more: download it in Settings (Tools & Backup).");
        if (!File.Exists(job.AudioPath))
            throw new InvalidOperationException($"the audio to transcribe was not found: {job.AudioPath}");

        // Voice activity detection: whisper is only handed the stretches with speech in them, so it has no
        // silence to make words up for. Used whenever its model is there and this whisper.exe knows the option.
        var vadModel = job.UseVad && File.Exists(DependencyUpdater.VadModelPath) && await CaptionGenerator.SupportsVadAsync(cancellationToken)
            ? DependencyUpdater.VadModelPath
            : null;

        // Words heard one way are not those heard another: the decoding and silence settings are part of what was heard.
        var key = $"{job.AudioPath}|{job.TrackIndex}|{File.GetLastWriteTimeUtc(job.AudioPath).Ticks}|{job.ModelPath}|{job.Language}|{job.Translate}|{job.Prompt}|{vadModel is not null}|{job.Options}|"
                  + string.Join(",", job.Ranges.Select(r => $"{r.Start:0.###}-{r.End:0.###}"));
        if (!_captionWords.TryGetValue(key, out var words))
        {
            Directory.CreateDirectory(_workFolder);
            var wavPath = Path.Combine(_workFolder, $"captions_{Guid.NewGuid():N}.wav");
            try
            {
                IsProgressIndeterminate = true;
                StatusText = $"{label}: extracting the audio...";
                await FfmpegRunner.RunAsync(
                    CaptionGenerator.BuildExtractCommand(job.AudioPath, job.TrackIndex, job.Ranges, wavPath), new Progress<FfmpegProgress>(), cancellationToken);

                StatusText = $"{label}: transcribing with {Path.GetFileName(job.ModelPath)}{(vadModel is null ? "" : ", skipping silence")}...";
                // Whisper says how far through the sound it is; the bar shows that, and no longer just that something is happening.
                var heard = new Progress<double>(percent =>
                {
                    IsProgressIndeterminate = false;
                    ProgressValue = percent;
                    onPercent?.Invoke(percent);
                });
                words = await Task.Run(
                    () => CaptionGenerator.TranscribeAsync(wavPath, job.ModelPath, job.Language, job.Prompt, job.Translate, vadModel, cancellationToken, heard, job.Options), cancellationToken);

                // Heard on the clock of the stretches joined end to end; kept in the source's own time (4.4).
                words = CaptionGenerator.JoinedToSource(job.Ranges, words);
            }
            finally
            {
                File.Delete(wavPath);
            }

            _captionWords[key] = words;
        }

        return words;
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
                CaptionLayer.Shadow ? CaptionLayer.ShadowShift : (0, 0), CaptionLayer.ShadowOpacity)
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
    /// The speech model the captions are transcribed with, for the box in the Auto-Captions dialog. Choosing one
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

    /// <summary>
    /// Makes a model (by its label) the one captions are transcribed with, without fetching it now: Generate Captions
    /// fetches what it needs first, asking (the Auto-Captions dialog, 4.4).
    /// </summary>
    public void UseWhisperModel(string label)
    {
        var file = DependencyUpdater.WhisperModels.FirstOrDefault(m => ModelLabel(m) == label);
        if (file is null || file == AppSettings.Current.WhisperModel)
            return;

        SaveView(settings => settings.WhisperModel = file);
        OnPropertyChanged(nameof(WhisperModelChoice));
        OnPropertyChanged(nameof(CaptionHint));
        OnPropertyChanged(nameof(WhisperModelStatus));
    }

    public string WhisperModelStatus =>
        File.Exists(DependencyUpdater.WhisperModelPath) ? "downloaded" : "not downloaded yet: it is fetched when chosen";

    /// <summary>Downloads a speech model into the models folder.</summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task DownloadWhisperModelAsync(string? model) => RunOperationAsync("Downloading a speech model", WorkLane.Interactive, async cancellationToken =>
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
