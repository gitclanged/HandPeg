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

    // Caption audio source: a video audio track (when neither flag is set), a separate audio file, or the mixer voiceover.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionUseVideoAudio))]
    private bool _captionUseExternalAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionUseVideoAudio))]
    private bool _captionUseVoiceover;

    /// <summary>First radio button: use a video audio track (neither alternative selected).</summary>
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

    /// <summary>Caption box on the output frame: a pseudo-layer placed and sized on the Edit Layout canvas.</summary>
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

    // Default box: lower frame area, nearly full width.
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

        // Same box styling as pictures: corners, feather and drop shadow.
        (CaptionLayer.CornerRadius, CaptionLayer.Feather) = (Math.Clamp(state?.CornerRadius ?? 0, 0, 50), state?.Feather ?? false);
        CaptionLayer.FeatherRadius = state?.FeatherRadius ?? 12;
        CaptionLayer.TakeShadow(state);
        (CaptionLayer.CustomMask, CaptionLayer.MaskPath) = (state?.CustomMask ?? false, state?.MaskPath ?? "");
    }

    [RelayCommand]
    private void ResetCaptionLayer() => SetCaptionLayer(null);

    // ----- Caption stacking position -----

    /// <summary>The tracks the captions can be put under: those with pictures (or an adjustment's), bottom first.</summary>
    private List<TimelineTrackModel> CaptionPlaces() => [.. Document.Tracks.Where(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle))];

    /// <summary>
    /// Caption stack position: under the picture track at this index of <see cref="CaptionPlaces"/> (0 = bottom), or at its count,
    /// over everything including fades (the default).
    /// </summary>
    public int CaptionsPlace
    {
        get
        {
            var places = CaptionPlaces();
            var at = Document.CaptionsUnderTrack is { } under ? places.IndexOf(under) : -1;
            return at < 0 ? places.Count : at;
        }
    }

    // ----- Strict order: timeline order top to bottom equals compositing order -----

    // The next track above a track's subtree (captions directly over it sit beneath this); null for none.
    private TimelineTrackModel? TrackAboveFamilyOf(TimelineTrackModel track)
    {
        var family = new HashSet<Guid> { track.Id };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var other in Document.Tracks)
                grew |= other.ParentTrackId is { } parent && family.Contains(parent) && family.Add(other.Id);
        }

        var places = CaptionPlaces();
        var last = places.FindLastIndex(t => family.Contains(t.Id));
        return last >= 0 && last + 1 < places.Count ? places[last + 1] : null;
    }

    /// <summary>Places new captions directly above their source track (row order = draw order). No undo step of its own.</summary>
    private void PlaceCaptionsOverTheirTrack()
    {
        if (CaptionAnchorTrackId is not { } id || Document.Tracks.Find(t => t.Id == id) is not { } anchor)
            return;

        var above = TrackAboveFamilyOf(anchor);
        if (!ReferenceEquals(above, Document.CaptionsUnderTrack))
            Edit("place the captions over their track", edit => edit.SetCaptionsUnder(above), undoable: false);
    }

    /// <summary>Whether captions sit directly above their source track (row shown as its child); elsewhere the row follows the draw position.</summary>
    public bool CaptionsAreOverTheirTrack =>
        CaptionAnchorTrackId is { } id && Document.Tracks.Find(t => t.Id == id) is { } anchor && ReferenceEquals(TrackAboveFamilyOf(anchor), Document.CaptionsUnderTrack);

    /// <summary>A track's index among valid caption positions (<see cref="CaptionsPlace"/>); -1 for audio tracks.</summary>
    public int CaptionPlaceOf(TimelineTrackModel track) => CaptionPlaces().IndexOf(track);

    /// <summary>Whether the captions are over everything (no track has them under it).</summary>
    public bool CaptionsOnTop => CaptionsPlace >= CaptionPlaces().Count;

    /// <summary>
    /// Moves captions under the picture track at <paramref name="place"/> (<see cref="CaptionsPlace"/>; count or more = top). One undo
    /// step; false if unchanged or refused.
    /// </summary>
    public bool PutCaptionsAt(int place, string label)
    {
        var places = CaptionPlaces();
        place = Math.Clamp(place, 0, places.Count);
        var track = place < places.Count ? places[place] : null;
        if (ReferenceEquals(track, Document.CaptionsUnderTrack) || (track is null && Document.CaptionsUnderTrack is null))
            return false;
        return Edit(label, edit => edit.SetCaptionsUnder(track));
    }

    /// <summary>Moves captions directly under a track (null = top), as the 3D view lift does. One undo step.</summary>
    public bool PutCaptionsUnder(TimelineTrackModel? track, string label) =>
        track is null ? PutCaptionsAt(int.MaxValue, label) : CaptionPlaces().IndexOf(track) is >= 0 and var at && PutCaptionsAt(at, label);

    /// <summary>Moves captions one track up (<paramref name="step"/> 1) or down (-1) in the stack.</summary>
    public bool MoveCaptionsInStack(int step)
    {
        var moved = PutCaptionsAt(CaptionsPlace + Math.Sign(step), step > 0 ? "bring the captions forward" : "send the captions back");
        StatusText = moved ? CaptionsPlaceText()
            : step > 0 ? "The captions are in front of everything already." : "The captions are behind every track already.";
        return moved;
    }

    /// <summary>Where the captions are in the stack, in words (the status line, the captions' menu).</summary>
    public string CaptionsPlaceText()
    {
        var places = CaptionPlaces();
        var place = CaptionsPlace;
        return place >= places.Count ? "The captions are over everything, as they always were."
            : $"The captions are under {places[place].DisplayName} and everything over it, over {(place > 0 ? places[place - 1].DisplayName : "the background")}.";
    }

    /// <summary>The layout pane always shows the output frame all clips are composited onto.</summary>
    public bool CanEditLayout => true;

    partial void OnAutoCaptionsChanged(bool value)
    {
        RefreshLayerRows();
        if (value)
            OpenLayoutPaneIfWanted();
        SyncEditorCaptions();
    }

    /// <summary>Caption box in pixels for a given output size, with even sides.</summary>
    private (int X, int Y, int Width, int Height) GetCaptionRect(int frameWidth, int frameHeight)
    {
        var (x, y, width, height) = CaptionLayer.GetOutputRect(frameWidth, frameHeight, 0, 0);
        return (x, y, Math.Max(width, 16), Math.Max(height, 16));
    }

    /// <summary>The separate audio file: a clean voice recording in sync with the video, say.</summary>
    [ObservableProperty] private string _captionAudioPath = "";

    /// <summary>
    /// Handles a clip's voice mark (Voiceover / Dialogue tick, or a stream detected by name on load): marked becomes the caption
    /// source unless a separate file or voiceover is chosen; unmarked clears that choice and falls back to the defaults.
    /// </summary>
    private void ListenToVoice(Layer clip)
    {
        if (!clip.CarriesSound)
            return;

        var source = new CaptionSource(CaptionSourceKind.Clip, clip.ImagePath, Math.Max(ClipOf(clip)?.AudioStreamIndex ?? 0, 0));
        if (clip.IsVoice && CaptionUseVideoAudio)
            _captionSourceChoice = source;
        else if (!clip.IsVoice && source.SameAs(_captionSourceChoice))
            _captionSourceChoice = null;
        OnPropertyChanged(nameof(CaptionSourceSummary));
    }


    public CaptionStyle CaptionStyle { get; private set; } = AppSettings.Current.DefaultCaptionStyle?.Clone() ?? new();

    /// <summary>The style in a few words, shown beside the button that opens its dialog.</summary>
    public string CaptionStyleSummary =>
        $"{CaptionStyle.FontName} {CaptionStyle.FontSize}, {CaptionStyle.Animation}, up to {CaptionStyle.MaxWordsPerLine} words";

    public void SetCaptionStyle(CaptionStyle style)
    {
        CaptionStyle = style;
        OnPropertyChanged(nameof(CaptionStyleSummary));

        // Style is not observable, so notify the player here whenever it draws captions (Burn Captions on). Only the preview
        // transcription is rebuilt once changes settle (words are cached; lines re-break by the style's word count).
        if (AutoCaptions && !_isBackgroundWorker)
            CaptionsChanged?.Invoke();
        if (PreviewSubtitles && !HasTextCues)
            RefreshLiveCaptionsSoon();
    }

    // ----- Preview Subtitles -----
    // Player overlay of styled captions in the caption box, timed to the sequence including cuts.
    // The GPU preview rasterizes cues directly (GpuCaptions -> TextRasterizer, same as export), so no file is written; the first
    // build transcribes (words are cached) and CaptionsChanged makes the preview re-read them.

    /// <summary>Whether the player draws styled captions; enabling builds them (first time transcribes).</summary>
    [ObservableProperty] private bool _previewSubtitles;

    /// <summary>
    /// Raised on the UI thread when drawn captions or subtitles change (cues, style, rebuild, Preview Subtitles off, picture
    /// subtitles re-read); the GPU preview re-reads <see cref="GpuCaptions"/> and <see cref="PreviewPictureSubtitles"/>.
    /// </summary>
    public event Action? CaptionsChanged;

    partial void OnPreviewSubtitlesChanged(bool value)
    {
        if (value)
        {
            _ = RefreshLiveCaptionsAsync();
            return;
        }

        if (!_isBackgroundWorker)
            CaptionsChanged?.Invoke();
    }

    private CancellationTokenSource? _liveCaptionsWait;

    /// <summary>Debounced rebuild of preview captions after caption box move, resize or restyle.</summary>
    private void RefreshLiveCaptionsSoon() => Faults.Forget(RefreshLiveCaptionsSoonAsync(), nameof(RefreshLiveCaptionsSoon));

    private async Task RefreshLiveCaptionsSoonAsync()
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
            // Superseded by a later change.
        }
    }

    /// <summary>Rebuilds preview captions (cached words; only the first run transcribes) and shows them in the player.</summary>
    /// <param name="asked">True for an explicit button press; style/size refreshes are skipped while another operation runs.</param>
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

        const string shown = "Subtitles preview on: the player draws the captions over the picture as they are styled.";

        // A subtitle track with words takes precedence: draw its cues as-is, no transcription.
        if (HasTextCues)
        {
            CaptionsChanged?.Invoke();
            if (asked)
                StatusText = shown;
            return;
        }

        await RunOperationAsync("Making captions", WorkLane.Interactive, async cancellationToken =>
        {
            if (await HearPreviewCaptionsAsync(cancellationToken) is { } problem)
            {
                PreviewSubtitles = false;
                return problem;
            }

            CaptionsChanged?.Invoke();
            return shown;
        });
    }

    /// <summary>
    /// Builds player captions when no subtitle track has words: transcribes the source with whisper.cpp (or uses the cache) and
    /// projects through every clip playing it on the sequence clock. Returns null or an error message.
    /// </summary>
    private async Task<string?> HearPreviewCaptionsAsync(CancellationToken cancellationToken)
    {
        if (ResolveCaptionSource() is not { } source)
            return "There is nothing to make captions from: load a video, or choose what to listen to (Generate Auto-Captions...).";

        var (words, problem) = await GetCaptionWordsAsync(source, cancellationToken);
        if (words is null)
            return problem;

        _generatedCaptions = CaptionProjection.ProjectLines(words, CaptionSpans(source), CaptionStyle);
        RaiseTimelineChanged();
        return null;
    }

    /// <summary>Caption hand-off file for the export (GpuExport reads its words); rewritten by every captioned encode.</summary>
    public string CaptionsFilePath => Path.Combine(_workFolder, "captions.ass");

    /// <summary>Reason captions are unavailable, or null.</summary>
    public string CaptionHint
    {
        get
        {
            if (!AutoCaptions)
                return "";
            if (HasTextCues)
            {
                return VideoEncoder.Family == EncoderFamily.Copy && !IsAnimatedOutput
                    ? "Captions are drawn into the picture, so they need a video codec other than Copy (Export tab)."
                    : $"The captions are drawn from the subtitle track ({SubtitleCues.Count} cue{(SubtitleCues.Count == 1 ? "" : "s")}), in the caption style. Nothing is transcribed while it is there.";
            }

            if (!File.Exists(DependencyUpdater.WhisperPath))
                return DependencyUpdater.IsWhisperOverridden
                    ? $"The whisper.exe set in Settings (Tools) was not found: {DependencyUpdater.WhisperPath}"
                    : "whisper.cpp is not installed yet: use Install / Update All Dependencies in Settings (Tools).";
            if (!File.Exists(DependencyUpdater.WhisperModelPath))
                return $"The speech model {AppSettings.Current.WhisperModel} is not downloaded yet: see Settings (Tools).";
            if (VideoEncoder.Family == EncoderFamily.Copy && !IsAnimatedOutput)
                return "Captions are drawn into the picture, so they need a video codec other than Copy (Export tab).";
            const string when = "The words are transcribed when the video is exported, or now with Generate Captions, to see and edit them first.";
            if (!File.Exists(DependencyUpdater.VadModelPath))
                return $"{when} Silence detection (VAD) is not installed, so quiet stretches may get words that were never said: see Settings (Tools).";
            return when;
        }
    }

    // Whether the export burns captions (the compositor draws the caption file's words; the command never names the file):
    // Auto-Captions on and a rendered picture (not Copy, not Audio Only).
    private bool CommandUsesCaptions(string command) =>
        AutoCaptions && command.Length > 0 && !IsAudioOnlyExport && !(VideoEncoder.Family == EncoderFamily.Copy && !IsAnimatedOutput);

    // Transcription cache keyed by source and model: style changes reuse the words.
    private readonly Dictionary<string, List<CaptionWord>> _captionWords = [];

    /// <summary>
    /// Writes the caption file before an export that burns captions. Returns an error message when captions are required but
    /// cannot be made; null otherwise.
    /// </summary>
    private async Task<string?> PrepareCaptionsForEncodeAsync(string command, CancellationToken cancellationToken)
    {
        if (IsAudioOnlyExport)
            return null;
        if (!CommandUsesCaptions(command))
        {
            return AutoCaptions && !IsCommandManuallyEdited
                ? "Auto-Captions are on, but captions are drawn into the picture: choose a video encoder other than Copy, or switch them off."
                : null;
        }

        return await PrepareCaptionsAsync(CaptionsFilePath, cancellationToken);
    }

    // ----- Caption source and timeline projection -----

    // Manually chosen source (Auto-Captions dialog, track or clip menu); null follows the defaults.
    private CaptionSource? _captionSourceChoice;

    /// <summary>
    /// Sets the caption source: voiceover and external file via their persisted switches; a clip's file and stream as the manual choice.
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
            default:
                CaptionUseVideoAudio = true;
                _captionSourceChoice = source;
                break;
        }

        OnPropertyChanged(nameof(CaptionSourceSummary));
    }

    /// <summary>
    /// Resolves the caption source from a request (<see cref="AudioTrack"/>, <see cref="Layer"/>, <see cref="CaptionSource"/>) or, with
    /// none: voiceover/external file if switched on; else the manual choice; else the existing captions' source; else a voice-marked
    /// clip; else the first stream of the playhead/selected video (<see cref="ListenedSound"/>). Null when nothing is available.
    /// </summary>
    public CaptionSource? ResolveCaptionSource(object? requested = null)
    {
        switch (requested)
        {
            case CaptionSource given:
                return given;
            case AudioTrack track when AudioTracks.Contains(track) && OpenedFile.Length > 0:
                return new CaptionSource(CaptionSourceKind.Clip, OpenedFile, track.Index);
            case Layer { CarriesSound: true } clip when ClipOf(clip) is { } model:
                return new CaptionSource(CaptionSourceKind.Clip, clip.ImagePath, Math.Max(model.AudioStreamIndex, 0));
            case Layer row when CaptionStreamsOf(row) is { Count: > 0 } streams:
                return streams[0];
        }

        if (CaptionUseVoiceover)
            return new CaptionSource(CaptionSourceKind.Voiceover, VoiceoverPath, 0);
        if (CaptionUseExternalAudio)
        {
            // A file played as clips is projected through those clips.
            var external = CaptionAudioPath.Trim().Trim('"');
            var clip = new CaptionSource(CaptionSourceKind.Clip, external, 0);
            return PlaysCaptionSource(clip) ? clip : new CaptionSource(CaptionSourceKind.External, external, 0);
        }

        if (_captionSourceChoice is { } chosen && PlaysCaptionSource(chosen))
            return chosen;
        if (_captionAnchorSource is { } anchored && PlaysCaptionSource(anchored))
            return anchored;

        // Voice-marked audio, else the first stream of the playhead (or selected) video.
        return ListenedSound() is var (path, stream) ? new CaptionSource(CaptionSourceKind.Clip, path, stream) : null;
    }

    /// <summary>Short description of the current caption source (Auto-Captions dialog, track header tooltip).</summary>
    public string CaptionSourceSummary => ResolveCaptionSource() is { } source ? DescribeCaptionSource(source) : "Nothing to listen to yet.";

    /// <summary>A source in a few words.</summary>
    public string DescribeCaptionSource(CaptionSource source) => source.Kind switch
    {
        CaptionSourceKind.External => $"External audio file: {Path.GetFileName(source.Path)}",
        CaptionSourceKind.Voiceover => "Recorded voiceover",
        _ when source.Stream < 0 => $"The picture of {Path.GetFileName(source.Path)}",
        _ => CaptionLaneOf(source) is { Name.Length: > 0 } lane ? $"{Path.GetFileName(source.Path)}: {lane.Name}" : $"Sound clip: {Path.GetFileName(source.Path)}",
    };

    /// <summary>Whether the timeline still plays a source (a clip of its file, or the voiceover).</summary>
    private bool PlaysCaptionSource(CaptionSource source) => source.Kind switch
    {
        CaptionSourceKind.External => source.Path.Length > 0,
        CaptionSourceKind.Voiceover => HasVoiceover,
        _ => source.Path.Length > 0 && CaptionClips(source).Any(),
    };

    /// <summary>
    /// Clips playing a source: those whose audio comes from its stream (stream lanes each carry one), or for stream -1 its
    /// top-level picture clips.
    /// </summary>
    private IEnumerable<(TimelineTrackModel Track, TimelineClip Clip)> CaptionClips(CaptionSource source) => source.Stream < 0
        ? Document.Tracks.SelectMany(t => t.Clips.Where(c => c is { ContentKind: LayerKind.VideoFile, ParentClipId: null } && SameFile(c.SourcePath, source.Path)).Select(c => (t, c)))
        : Document.Tracks.SelectMany(t => t.Clips.Where(c => c.HasSound && SameFile(c.SourcePath, source.Path) && Math.Max(c.AudioStreamIndex, 0) == source.Stream).Select(c => (t, c)));

    /// <summary>
    /// Ordered timeline windows playing a source: its file's clips (with J/L cuts and unlinked slip; stream -1 uses visible picture
    /// spans), the voiceover placement, or the timeline clock for an external recording.
    /// </summary>
    private List<CaptionSpan> CaptionSpans(CaptionSource source)
    {
        var spans = new List<CaptionSpan>();
        switch (source.Kind)
        {
            case CaptionSourceKind.Clip when source.Stream < 0:
                foreach (var span in SequenceSpans.SpansOf(Document, source.Path))
                    spans.Add(new CaptionSpan(span.Start, span.End, span.MediaOffset, span.Speed > 0 ? span.Speed : 1));
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

    /// <summary>A clip's window onto its file: picture span, or audio span including J/L cut, unlinked slip and stream slip.</summary>
    private static CaptionSpan SpanOf(TimelineClip clip, bool sound, double slip)
    {
        var speed = clip.Speed > 0 ? clip.Speed : 1;
        var (lead, tail) = sound ? (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) : (0, 0);
        var shift = slip + (sound && !clip.AudioLinked ? clip.AudioSlipSeconds : 0);
        var start = clip.Start - lead;
        return new CaptionSpan(start + shift, Math.Max(clip.End + tail, start) + shift, clip.MediaOffset - lead * speed, speed);
    }

    /// <summary>Source duration in seconds; 0 when unknown.</summary>
    private double CaptionSourceSeconds(CaptionSource source) => source.Kind switch
    {
        CaptionSourceKind.Clip => CaptionClips(source).Select(c => c.Clip.SourceDuration).DefaultIfEmpty(0).Max(),
        CaptionSourceKind.Voiceover => VoiceoverDuration,
        _ => 0,
    };

    /// <summary>The track captions follow: that of the first clip playing their source; null when none.</summary>
    public Guid? CaptionAnchorTrackId => _captionAnchorSource switch
    {
        // A stream lane resolves to its parent video track, so captions join that track's stack.
        { Kind: CaptionSourceKind.Clip } source => CaptionClips(source).OrderBy(c => c.Clip.Start).Select(c => (Guid?)(c.Track.Kind == TimelineTrackKind.Audio ? c.Track.ParentTrackId ?? c.Track.Id : c.Track.Id)).FirstOrDefault(),
        _ => null,
    };

    /// <summary>
    /// Caption pipeline over the whole sequence: extract 16 kHz mono WAV, transcribe with whisper.cpp, project words through playing
    /// clips, and write the styled ASS hand-off file for the caption box (a subtitle track with words is written as-is).
    /// Returns null or an error message.
    /// </summary>
    private async Task<string?> PrepareCaptionsAsync(string assPath, CancellationToken cancellationToken)
    {
        // A subtitle track with words takes precedence: no transcription.
        if (HasTextCues)
        {
            var cues = CaptionProjection.ToOutputClock(GetCueCaptions(), [], 0);
            WriteCaptionFile(cues.SelectMany(l => l).ToList(), cues, assPath, 0);
            StatusText = $"Captions: {cues.Count} subtitle cue{(cues.Count == 1 ? "" : "s")} written.";
            return null;
        }

        if (ResolveCaptionSource() is not { } source)
            return "There is nothing to make captions from: load a video, or choose what to listen to (Generate Auto-Captions...).";

        var (words, problem) = await GetCaptionWordsAsync(source, cancellationToken);
        if (words is null)
            return problem;

        // Words are in source time; project through every playing clip onto the sequence clock.
        var lines = CaptionProjection.ProjectLines(words, CaptionSpans(source), CaptionStyle);
        var output = CaptionProjection.ToOutputClock(lines, [], 0);
        WriteCaptionFile(output.SelectMany(l => l).ToList(), output, assPath, 0);
        StatusText = $"Captions: {output.Sum(l => l.Count)} word{(output.Sum(l => l.Count) == 1 ? "" : "s")} written.";
        return null;
    }

    /// <summary>
    /// Transcribed words of a source in source time (whisper.cpp or cache), or an error. Only timeline-played ranges plus padding are transcribed.
    /// </summary>
    private async Task<(List<CaptionWord>? Words, string? Problem)> GetCaptionWordsAsync(CaptionSource source, CancellationToken cancellationToken)
    {
        var (job, problem) = CreateCaptionJob(source, assPath: "", timeOffset: 0);
        return job is null ? (null, problem) : (await HearCaptionsAsync(job, cancellationToken), null);
    }

    /// <summary>
    /// Captures a transcription job without running it: audio source, whisper settings, projection clips, style and size. Run
    /// immediately by exports, deferred by queued jobs. Returns the failure reason when the source cannot be captioned.
    /// </summary>
    private (CaptionJob? Job, string? Problem) CreateCaptionJob(CaptionSource source, string assPath, double timeOffset)
    {
        if (!File.Exists(DependencyUpdater.WhisperPath))
            return (null, "Auto-captions need whisper.cpp: use Install / Update All Dependencies in Settings (Tools), or set the path to your own whisper.exe there.");

        var modelPath = DependencyUpdater.WhisperModelPath;
        if (!File.Exists(modelPath))
            return (null, $"Auto-captions need the speech model {AppSettings.Current.WhisperModel}: download it in Settings (Tools).");

        // Audio input: a video track, a clip's file, a separate file or the voiceover, plus its range.
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

                // Only the trimmed range is transcribed, matching what is mixed.
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
            // Transcribe clip-played files whole in source time so one transcription survives later cuts, trims, folds and undo.
            default:
                if (spans.Count == 0)
                    return (null, $"{Path.GetFileName(source.Path)} is not on the timeline any more: there is nothing of it to make captions for.");
                if (!File.Exists(audioPath))
                    return (null, $"The sound to make captions from was not found: {audioPath}");
                break;
        }

        // Sized for the caption box (WriteCaptionFile).
        var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        var (_, _, width, height) = GetCaptionRect(frameWidth, frameHeight);
        var textScale = Math.Min(frameWidth, frameHeight) / 1080.0 * (height / (DefaultCaptionHeight * frameHeight));

        // Snapshot whisper decoding and silence settings.
        return (new CaptionJob(audioPath, trackIndex, ranges, modelPath, WhisperLanguage, WhisperPrompt.Trim(), WhisperTranslate,
            AppSettings.Current.UseVad, WhisperOptions.FromSettings(AppSettings.Current), CaptionStyle.Clone(), width, height, textScale, timeOffset, assPath,
            spans), null);
    }

    /// <summary>
    /// Runs a transcription job: extracts 16 kHz mono WAV and runs whisper.cpp, or returns cached words for an identical job.
    /// </summary>
    /// <param name="label">Status bar step label: "Captions" for exports, the job name for the queue.</param>
    /// <param name="onPercent">Progress callback (percent through the audio), in addition to the progress bar.</param>
    private async Task<List<CaptionWord>> HearCaptionsAsync(CaptionJob job, CancellationToken cancellationToken, string label = "Captions", Action<double>? onPercent = null)
    {
        // Queued jobs may find their inputs gone.
        if (!File.Exists(DependencyUpdater.WhisperPath))
            throw new InvalidOperationException("whisper.cpp is not installed any more: see Settings (Tools & Backup).");
        if (!File.Exists(job.ModelPath))
            throw new InvalidOperationException($"the speech model {Path.GetFileName(job.ModelPath)} is not there any more: download it in Settings (Tools & Backup).");
        if (!File.Exists(job.AudioPath))
            throw new InvalidOperationException($"the audio to transcribe was not found: {job.AudioPath}");

        // VAD: feed whisper only speech so it cannot hallucinate over silence; used when the model exists and whisper.exe supports it.
        var vadModel = job.UseVad && File.Exists(DependencyUpdater.VadModelPath) && await CaptionGenerator.SupportsVadAsync(cancellationToken)
            ? DependencyUpdater.VadModelPath
            : null;

        // Decoding and silence settings are part of the cache key.
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
                // Switch the bar from indeterminate to whisper's reported progress.
                var heard = new Progress<double>(percent =>
                {
                    IsProgressIndeterminate = false;
                    ProgressValue = percent;
                    onPercent?.Invoke(percent);
                });
                words = await Task.Run(
                    () => CaptionGenerator.TranscribeAsync(wavPath, job.ModelPath, job.Language, job.Prompt, job.Translate, vadModel, cancellationToken, heard, job.Options), cancellationToken);

                // Map from concatenated-range time back to source time.
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

    /// <summary>
    /// Caption lines for the GPU preview on the sequence clock, with the export's style and text scale (<see cref="WriteCaptionFile"/>).
    /// Shown while Burn Captions (<see cref="AutoCaptions"/>) is on: subtitle track cues when it has words; the preview-only
    /// transcription also requires Preview Subtitles. Empty when Burn Captions is off.
    /// </summary>
    public (IReadOnlyList<IReadOnlyList<CaptionWord>>? Lines, CaptionStyle Style, double TextScale) GpuCaptions()
    {
        if (!AutoCaptions)
            return (null, CaptionStyle, 1);
        var lines = HasTextCues ? GetCueCaptions() : PreviewSubtitles ? _generatedCaptions : [];
        return (lines.Count > 0 ? lines : null, CaptionStyle, CaptionTextScale());
    }

    /// <summary>Scale applied to style sizes (authored for 1080 px) inside the caption box, as computed by <see cref="WriteCaptionFile"/>.</summary>
    private double CaptionTextScale()
    {
        var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        var (_, _, _, height) = GetCaptionRect(frameWidth, frameHeight);
        return Math.Min(frameWidth, frameHeight) / 1080.0 * (height / (DefaultCaptionHeight * frameHeight));
    }

    /// <summary>Writes captions as an ASS file sized for the caption box; GpuExport reads its words for burn-in.</summary>
    /// <param name="lines">Explicit lines (subtitle track cues); null groups words by the style.</param>
    private void WriteCaptionFile(IReadOnlyList<CaptionWord> words, IReadOnlyList<List<CaptionWord>>? lines, string assPath, double timeOffset)
    {
        // Canvas is the caption box, not the frame. Text scales with frame height (same look at 720p and 4K) and box height
        // (a taller box means larger text).
        var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        var (_, _, width, height) = GetCaptionRect(frameWidth, frameHeight);
        var textScale = Math.Min(frameWidth, frameHeight) / 1080.0 * (height / (DefaultCaptionHeight * frameHeight));
        CaptionGenerator.WriteAss(words, CaptionStyle, width, height, textScale, assPath, timeOffset, lines);
    }

    // ----- whisper.cpp model -----

    // Models by name ("base.en", "large-v3", "turbo"); files are "ggml-<name>.bin".
    private const string TurboFile = "ggml-large-v3-turbo.bin";

    private static string ModelLabel(string file) =>
        file == TurboFile ? "turbo" : file.StartsWith("ggml-", StringComparison.Ordinal) && file.EndsWith(".bin", StringComparison.Ordinal) ? file[5..^4] : file;

    public IReadOnlyList<string> WhisperModelChoices { get; } = DependencyUpdater.WhisperModels.Select(ModelLabel).ToList();

    /// <summary>
    /// Whisper model for the Auto-Captions dialog; selecting one saves it to settings and starts its download if missing.
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
    /// Selects a model by label without downloading; Generate Captions fetches it later after confirmation.
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

    /// <summary>Preset names prefixed with "(None)": choices for the global default.</summary>
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

            // Clearing the list reset the bound selection; restore it.
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
    /// Persists rules and the default preset after every change (no Save button); incomplete rules stay on the tab unsaved.
    /// </summary>
    public void SaveAutomation()
    {
        if (_loadingAutomation || _isBackgroundWorker)
            return;

        var rules = SmartRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Path) && PresetNames.Contains(r.Preset))
            .Select(r => new SmartRule { Type = SmartRule.Types.Contains(r.Type) ? r.Type : SmartRule.Folder, Path = r.Path.Trim(), Preset = r.Preset })
            .ToList();
        var defaultPreset = DefaultPresetChoice is { } choice && choice != NoPreset ? choice : "";

        try
        {
            AppSettings.Update(current => (current.SmartRules, current.DefaultPreset) = (rules, defaultPreset), saveNow: true);
            var settings = AppSettings.Current;
            StatusText = $"Automation saved: {settings.SmartRules.Count} rule{(settings.SmartRules.Count == 1 ? "" : "s")}"
                         + (settings.DefaultPreset.Length > 0 ? $", default preset \"{settings.DefaultPreset}\"." : ", no default preset.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"Could not write {AppSettings.FilePath}: {ex.Message}", "Save the automation rules", ex);
        }
    }
}
