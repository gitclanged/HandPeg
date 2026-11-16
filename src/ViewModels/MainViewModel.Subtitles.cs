using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.ViewModels;

// The single subtitle track: imported (.srt, .vtt, or .sup/VobSub timings), generated from auto-captions, or typed in the
// Subtitle Studio. Text cues replace transcription and render through the caption pipeline (style, box, GPU preview and export).
// Picture tracks are decoded for the GPU preview overlay only; the export omits them.
public partial class MainViewModel
{
    public ObservableCollection<SubtitleCue> SubtitleCues { get; } = [];

    /// <summary>The file the track was imported from; empty for one made in HandPeg.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleTrackSummary))]
    private string _subtitleSourcePath = "";

    /// <summary>Picture track (PGS, VobSub): cues are display times only, with no editable words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextCues))]
    [NotifyPropertyChangedFor(nameof(SubtitleTrackSummary))]
    private bool _subtitlesAreImages;

    /// <summary>Milliseconds every cue is shifted by: later when positive, earlier when negative.</summary>
    [ObservableProperty] private double _subtitleOffsetMs;

    // The Subtitle Studio is a dock pane: the main player keeps drawing captions, each gesture is its own undo step
    // (CommitSubtitleEdits), and track words draw whenever Burn Captions into Export is on (GpuCaptions).

    public bool HasSubtitleCues => SubtitleCues.Count > 0;

    /// <summary>The track has words, which the captions are then made of.</summary>
    public bool HasTextCues => HasSubtitleCues && !SubtitlesAreImages;

    public string SubtitleTrackSummary
    {
        get
        {
            if (!HasSubtitleCues)
                return "No subtitle track: import a file, or make one with Generate Auto-Captions.";

            var from = SubtitleSourcePath.Length > 0 ? Path.GetFileName(SubtitleSourcePath) : "made in HandPeg";
            var count = $"{SubtitleCues.Count} cue{(SubtitleCues.Count == 1 ? "" : "s")}";
            return SubtitlesAreImages
                ? $"{from}  ·  {count}, image-based: shown in the player and retimed in the Subtitle Studio, but not written into the export"
                : $"{from}  ·  {count}, drawn as captions in the caption style";
        }
    }

    private double SubtitleOffsetSeconds => double.IsFinite(SubtitleOffsetMs) ? SubtitleOffsetMs / 1000 : 0;

    // Picture track cues as read from the file: the baseline for edited times and cue-to-picture matching.
    private List<SubtitleCue> _subtitleOriginal = [];

    /// <summary>Imports a subtitle file as the track, replacing the current one; false (reason in the status bar) when unreadable.</summary>
    public bool ImportSubtitles(string path)
    {
        path = path.Trim().Trim('"');
        List<SubtitleCue> cues;
        try
        {
            cues = SubtitleFiles.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            Fail($"The subtitles could not be read: {ex.Message}", "Read subtitles", ex);
            return false;
        }

        if (cues.Count == 0)
        {
            StatusText = $"No subtitles were found in {Path.GetFileName(path)}.";
            return false;
        }

        var images = SubtitleFiles.IsImageFile(path);
        Checkpoint("import subtitles");

        // While the last opened file is still the whole sequence (file time = timeline time, SequenceSpans.WholeFiles), anchor the
        // words to its picture so later cuts, folds and moves carry them; once edited, times stay on the timeline clock. File cues
        // lack word times, so words are spread by length; a cut through a cue splits its words and hidden parts survive edits.
        if (!images && IsOpenedFileWhole)
        {
            var anchors = cues.Where(c => c.End > c.Start).OrderBy(c => c.Start)
                .Select(c => CaptionProjection.WithWordTimes(new SubtitleCueState(c.Start, c.End, c.Text))).ToList();
            SetSubtitleTrack([], path, false, 0, new CaptionSource(CaptionSourceKind.Clip, OpenedFile, -1), anchors);
        }
        else
        {
            SetSubtitleTrack(cues, path, images, 0);
        }
        Log($"Imported {cues.Count} subtitle cue{(cues.Count == 1 ? "" : "s")} from {Path.GetFileName(path)}{(images ? " (image-based: timing only)" : "")}");

        // Words render as captions: enable captions, and the preview when a player exists.
        if (!images && !AutoCaptions)
            AutoCaptions = true;
        if (HasSource && !_isBackgroundWorker)
        {
            // Pictures are drawn as-is after background decode; words need Preview Subtitles enabled.
            if (!images && !PreviewSubtitles)
                PreviewSubtitles = true;
            else
                RefreshSubtitlePreview();
        }

        return true;
    }

    [RelayCommand]
    private void ClearSubtitles()
    {
        if (!HasSubtitleCues && _captionAnchorSource is null)
            return;

        Checkpoint("remove the subtitle track");
        SetSubtitleTrack([], "", false, 0);
        RefreshSubtitlePreview();
        StatusText = "Subtitle track removed.";
    }

    /// <summary>Replaces the subtitle track.</summary>
    /// <param name="cues">Timeline-clock cues (the non-anchored ones when anchors are given).</param>
    /// <param name="anchoredTo">Source the anchored captions follow; null for none.</param>
    /// <param name="anchors">Anchored captions in source time, sorted by start.</param>
    private void SetSubtitleTrack(IEnumerable<SubtitleCue> cues, string path, bool images, double offsetMs,
        CaptionSource? anchoredTo = null, List<SubtitleCueState>? anchors = null)
    {
        if (!images && anchoredTo is { IsAnchored: true } && anchors is { Count: > 0 })
            (_captionAnchorSource, _captionAnchors) = (anchoredTo, anchors);
        else
            (_captionAnchorSource, _captionAnchors) = (null, []);
        SubtitleCues.Clear();
        foreach (var cue in cues)
            SubtitleCues.Add(cue);
        if (_captionAnchorSource is not null)
            ShowCaptionAnchors([.. SubtitleCues.Where(c => c.Piece is null).Select(StateOf)]);

        _subtitleOriginal = images ? SubtitleCues.Select(c => c.Clone()).ToList() : [];
        (SubtitleSourcePath, SubtitlesAreImages, SubtitleOffsetMs) = (path, images, offsetMs);
        if (!images)
            ForgetPictureSubtitles();
        NotifySubtitlesChanged();
    }

    // ----- Anchored captions -----
    // Captions from a clip-played source are stored in source time with per-word times; cues are a projection through every
    // playing clip, refreshed after each commit, undo and load (ReprojectCaptions). Subtitle Studio edits are written back to
    // source time (WriteBackCaptions). Tracks without anchors use the timeline clock.

    // Anchored captions in source time sorted by start, and their source (null for none).
    private List<SubtitleCueState> _captionAnchors = [];
    private CaptionSource? _captionAnchorSource;

    /// <summary>
    /// Re-projects anchored captions through the current clips after commit, undo and load. No-op without anchors; raises nothing
    /// when the projection is unchanged.
    /// </summary>
    public void ReprojectCaptions()
    {
        // During a restore, project once at the end from the restored anchors, never mid-restore or from stale anchors.
        if (_restoring > 0)
        {
            _reprojectPending = true;
            return;
        }

        if (_captionAnchorSource is null || SubtitlesAreImages)
            return;

        // Notify only on change; the preview captions then rebuild (SyncEditorCaptions).
        if (ShowCaptionAnchors([.. SubtitleCues.Where(c => c.Piece is null).Select(StateOf)]))
            NotifySubtitlesChanged();
    }

    /// <summary>
    /// Writes in-place edits of anchored caption cues back to source time and re-projects; unchanged cues are ignored.
    /// </summary>
    private void WriteBackCaptions()
    {
        if (_captionAnchorSource is not { } source || SubtitlesAreImages)
            return;

        var (anchors, loose) = CaptionProjection.WriteBack(_captionAnchors, CaptionSpans(source), [.. SubtitleCues.Select(StateOf)]);
        _captionAnchors = anchors;
        ShowCaptionAnchors(loose);
    }

    /// <summary>
    /// Merges projected anchored captions with the given timeline-clock cues into the ordered track, reusing existing cue objects
    /// for the same caption (same words, nearly same time) so lists and editors keep their references. Returns whether anything changed.
    /// </summary>
    private bool ShowCaptionAnchors(List<SubtitleCueState> loose)
    {
        var shown = _captionAnchorSource is { } source ? CaptionProjection.Project(_captionAnchors, CaptionSpans(source)) : [];
        if (loose.Count > 0)
        {
            shown.AddRange(loose);
            shown.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        // Cue object reuse: the previous one for this caption, else a leftover, else new.
        var byText = new Dictionary<string, List<SubtitleCue>>(StringComparer.Ordinal);
        foreach (var cue in SubtitleCues)
        {
            if (!byText.TryGetValue(cue.Text, out var same))
                byText[cue.Text] = same = [];
            same.Add(cue);
        }

        var order = new SubtitleCue?[shown.Count];
        var taken = new HashSet<SubtitleCue>();
        for (var i = 0; i < shown.Count; i++)
        {
            if (byText.TryGetValue(shown[i].Text, out var same)
                && same.FirstOrDefault(c => !taken.Contains(c) && Math.Abs(c.Start - shown[i].Start) < 0.001) is { } match)
            {
                order[i] = match;
                taken.Add(match);
            }
        }

        var spare = new Queue<SubtitleCue>(SubtitleCues.Where(c => !taken.Contains(c)));
        var changed = shown.Count != SubtitleCues.Count;
        for (var i = 0; i < shown.Count; i++)
        {
            var (cue, state) = (order[i] ?? (spare.Count > 0 ? spare.Dequeue() : new SubtitleCue()), shown[i]);
            if (cue.Start != state.Start || cue.End != state.End || cue.Text != state.Text
                || !(cue.WordBreaks ?? []).AsSpan().SequenceEqual(state.Breaks ?? []))
            {
                changed = true;
                cue.Text = state.Text;
                cue.WordBreaks = state.Breaks;
                (cue.Start, cue.End) = (state.Start, state.End);
            }

            (cue.Index, cue.Piece) = (i + 1, state.Piece);

            // Move only out-of-place items (usually none, so no search).
            var at = i < SubtitleCues.Count && ReferenceEquals(SubtitleCues[i], cue) ? i : SubtitleCues.IndexOf(cue);
            if (at < 0)
            {
                SubtitleCues.Insert(i, cue);
            }
            else if (at != i)
            {
                SubtitleCues.Move(at, i);
                changed = true;
            }
        }

        while (SubtitleCues.Count > shown.Count)
            SubtitleCues.RemoveAt(SubtitleCues.Count - 1);
        return changed;
    }

    private static SubtitleCueState StateOf(SubtitleCue cue) => new(cue.Start, cue.End, cue.Text, cue.WordBreaks) { Piece = cue.Piece };

    /// <summary>Anchored caption words in source time if anchored to the given source; null otherwise.</summary>
    private List<CaptionWord>? AnchoredCaptionWords(CaptionSource source) =>
        source.SameAs(_captionAnchorSource) ? CaptionProjection.Words(_captionAnchors) : null;

    /// <summary>
    /// Generate Captions: transcribes now, replaces the subtitle track with the result and enables captions. The track is then
    /// authoritative for display, editing, saving and export (no re-transcription).
    /// </summary>
    [RelayCommand]
    private async Task GenerateCaptionsAsync()
    {
        if (!HasSource)
        {
            StatusText = "Load a video first: there is nothing to make captions from.";
            return;
        }

        // whisper.cpp and the model download on first use.
        if (!await RequireCaptionToolsAsync("Generate Captions"))
            return;

        // Existing subtitles (possibly hand-edited) would be replaced: confirm first.
        if ((HasSubtitleCues || _captionAnchorSource is not null) && (ConfirmWarning ?? Confirm) is { } ask)
        {
            if (!ask("Generate Captions", "Generating new captions will overwrite your existing subtitle track and discard any manual timing or text edits.", "Overwrite"))
            {
                StatusText = "Generate Captions cancelled: the subtitle track is as it was.";
                return;
            }
        }

        await ConvertCaptionsToCuesAsync();
        if (HasTextCues && !AutoCaptions)
            AutoCaptions = true;
        SyncEditorCaptions();
    }

    /// <summary>
    /// Shows the subtitle track in the player while captions are on and a track exists; never transcribes (without a track,
    /// captions are only built at export).
    /// </summary>
    private void SyncEditorCaptions()
    {
        if (_isBackgroundWorker)
            return;

        if (AutoCaptions && HasTextCues && HasSource)
        {
            if (IsBusy)
                return;
            if (PreviewSubtitles)
                RefreshLiveCaptionsSoon();
            else
                PreviewSubtitles = true;
        }
        else if (PreviewSubtitles)
        {
            PreviewSubtitles = false;
        }
    }

    // Subtitle baseline at the last undo step; Subtitle Studio edits cues in place and CommitSubtitleEdits diffs against this.
    private string _committedSubtitles = "null";

    /// <summary>
    /// Undo history generation (changes on push, undo, redo); the window uses it to decide whether Ctrl+Z belongs to a text box
    /// or the timeline.
    /// </summary>
    public object? UndoMark => _undo.Count > 0 ? _undo[^1].State : null;

    /// <summary>Takes an undo step for in-place cue edits since the last step (restoring the baseline); no-op if unchanged.</summary>
    private void CheckpointSubtitleEdits()
    {
        // Not during a restore: cues are being written from the step, not edited.
        if (_restoring > 0)
            return;

        // Write anchored cue edits back to source time first.
        WriteBackCaptions();
        var now = JsonSerializer.Serialize(CaptureSubtitles());
        if (now == _committedSubtitles)
            return;

        Checkpoint("edit the subtitles", SnapshotBeforeSubtitleEdits());
        _committedSubtitles = now;
    }

    /// <summary>Current state with the subtitle baseline substituted: what undoing the in-place edits restores.</summary>
    private TimelineSnapshot SnapshotBeforeSubtitleEdits() =>
        CaptureSnapshot() with { Subtitles = JsonSerializer.Deserialize<SubtitleTrackData?>(_committedSubtitles) };

    /// <summary>Takes a named undo step before a cue change.</summary>
    private void CheckpointSubtitles(string what)
    {
        CheckpointSubtitleEdits();
        Checkpoint(what);
    }

    private void NotifySubtitlesChanged()
    {
        // During a restore, notify once at the end rather than per touched part.
        if (_restoring > 0)
        {
            _subtitlesChangedPending = true;
            return;
        }

        _subtitlesChangedPending = false;
        _committedSubtitles = JsonSerializer.Serialize(CaptureSubtitles());
        OnPropertyChanged(nameof(HasSubtitleCues));
        OnPropertyChanged(nameof(HasTextCues));
        OnPropertyChanged(nameof(SubtitleTrackSummary));
        OnPropertyChanged(nameof(CaptionHint));
        RaiseTimelineChanged();

        // Push track words to the player even while an operation runs (the sync below waits then); ScheduleGpuState's 8 ms timer
        // coalesces bursts.
        if (!_isBackgroundWorker)
            CaptionsChanged?.Invoke();
        if (!IsBusy)
            SyncEditorCaptions();
    }

    /// <summary>After manual cue edits (Subtitle Studio): re-sorts and renumbers, refreshes the timeline and the player.</summary>
    public void CommitSubtitleEdits()
    {
        // No cue edits during a restore.
        if (_restoring > 0)
            return;

        CheckpointSubtitleEdits();

        // Picture track cues keep file order, which is how they map to pictures.
        if (!SubtitlesAreImages)
        {
            var ordered = SubtitleFiles.Numbered(SubtitleCues.ToList());
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!ReferenceEquals(SubtitleCues[i], ordered[i]))
                    SubtitleCues.Move(SubtitleCues.IndexOf(ordered[i]), i);
            }
        }

        NotifySubtitlesChanged();
        RefreshSubtitlePreview();
        GenerateCommand();
    }

    partial void OnSubtitleOffsetMsChanged(double value)
    {
        if (HasSubtitleCues)
        {
            RaiseTimelineChanged();
            RefreshSubtitlePreview();
        }
    }

    /// <summary>Pushes the current subtitle track to the main player.</summary>
    private void RefreshSubtitlePreview()
    {
        if (_isBackgroundWorker)
            return;

        if (SubtitlesAreImages || !HasSubtitleCues)
            ShowPictureSubtitles();

        if (PreviewSubtitles)
            RefreshLiveCaptionsSoon();
    }

    // ----- Picture subtitles in the GPU preview -----
    // The file is decoded once in the background (PictureSubtitleDecoder via FFmpeg, packed in memory) and each cue is paired with
    // the picture at its original time (_subtitleOriginal), so retimed or offset cues keep their picture. Delivered with the
    // sequence (PreviewPictureSubtitles / state PictureSubtitles); changes raise CaptionsChanged.

    // Decoded pictures so far, the file identity (path, size, mtime), the in-flight decode, and the paired cues.
    private PictureSubtitleSet? _pictureSet;
    private string _pictureSetOf = "";
    private CancellationTokenSource? _pictureDecode;
    private PictureSubtitleTrack? _pictureTrack;
    private bool _toldPictures;

    /// <summary>
    /// Picture cues with their decoded pictures for the GPU preview (lazily decoded on first request); null without a picture track.
    /// </summary>
    public PictureSubtitleTrack? PreviewPictureSubtitles()
    {
        if (!SubtitlesAreImages || !HasSubtitleCues)
            return null;
        if (_pictureSetOf.Length == 0)
            ShowPictureSubtitles();
        return _pictureTrack;
    }

    /// <summary>
    /// Syncs the picture track to the preview: decodes on first use or file change, re-pairs cues and notifies. Without a picture
    /// track, releases decoded data.
    /// </summary>
    private void ShowPictureSubtitles()
    {
        if (_isBackgroundWorker)
            return;

        var file = FileState.Get(SubtitleSourcePath);
        if (!SubtitlesAreImages || !HasSubtitleCues || !file.Exists)
        {
            ForgetPictureSubtitles();
            return;
        }

        var of = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{SubtitleSourcePath}|{file.Length}|{file.WrittenUtc.Ticks}");
        if (of != _pictureSetOf)
            DecodePictureSubtitles(of);
        PairPictureSubtitles();
    }

    /// <summary>Pairs cues with decoded pictures so far and notifies the preview.</summary>
    private void PairPictureSubtitles()
    {
        var offset = SubtitleOffsetSeconds;
        _pictureTrack = _pictureSet is { } set && _subtitleOriginal.Count == SubtitleCues.Count
            ? PictureSubtitleTrack.Match(set, [.. _subtitleOriginal.Select(c => (c.Start, c.End))], [.. SubtitleCues.Select(c => (c.Start + offset, c.End + offset))])
            : null;
        CaptionsChanged?.Invoke();
    }

    private void ForgetPictureSubtitles()
    {
        _pictureDecode?.Cancel();
        (_pictureDecode, _pictureSet, _pictureSetOf, _toldPictures) = (null, null, "", false);
        if (_pictureTrack is null)
            return;
        _pictureTrack = null;
        CaptionsChanged?.Invoke();
    }

    /// <summary>
    /// Decodes the picture track on the background lane, publishing partial results about once per second. A superseded decode
    /// (other track, file changed) is cancelled and its pictures discarded.
    /// </summary>
    private void DecodePictureSubtitles(string of)
    {
        _pictureDecode?.Cancel();
        var decode = _pictureDecode = new CancellationTokenSource();
        (_pictureSet, _pictureSetOf) = (null, of);

        // VobSub opens via its .idx (FFmpeg finds the sibling .sub); .sup opens directly.
        var path = SubtitleSourcePath;
        var input = Path.GetExtension(path).ToLowerInvariant() is ".sub" or ".vobsub" ? SubtitleFiles.IdxPathOf(path) : path;
        if (!File.Exists(DependencyUpdater.FfmpegPath))
        {
            TellPictures("The picture subtitles are shown in the player once FFmpeg is installed (Settings, Tools).");
            return;
        }

        var home = SynchronizationContext.Current;
        _ = ReadAsync();

        async Task ReadAsync()
        {
            try
            {
                var set = await WorkScheduler.Run(new WorkSpec($"Reading the pictures of {Path.GetFileName(path)}", WorkLane.Background),
                    work => PictureSubtitleDecoder.DecodeAsync(input, 0, DependencyUpdater.FfmpegPath, partial => home?.Post(_ => Take(partial), null), work.Token),
                    decode.Token);
                Take(set);
                if (ReferenceEquals(_pictureDecode, decode))
                {
                    if (set is null)
                        TellPictures($"The pictures of {Path.GetFileName(path)} could not be read for the player (the log says why). The cues are kept.");
                    else if (set.OverBudget)
                        TellPictures($"{Path.GetFileName(path)} has more pictures than the player keeps: those after {TimeSpan.FromSeconds(set.Pictures[^1].Start):h\\:mm\\:ss} are not shown.");
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded (other track, file changed, closing): discard.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write($"Picture subtitles: {Path.GetFileName(path)} could not be read for the player", ex);
            }
        }

        // Publish partial results on the UI thread unless the decode was superseded.
        void Take(PictureSubtitleSet? set)
        {
            if (!ReferenceEquals(_pictureDecode, decode) || decode.IsCancellationRequested || set is null)
                return;
            _pictureSet = set;
            PairPictureSubtitles();
        }
    }

    private void TellPictures(string message)
    {
        if (_toldPictures)
            return;
        _toldPictures = true;
        StatusText = message;
    }

    /// <summary>
    /// Subtitle Studio preview inputs for a text track: cues as timeline-clock caption lines (export word timing), the caption style
    /// and its box scale (as <see cref="WriteCaptionFile"/>). The box is <see cref="SubtitleFrameSize"/>.
    /// </summary>
    public (IReadOnlyList<List<CaptionWord>> Lines, CaptionStyle Style, double TextScale) SubtitleEditorCaptions() =>
        (GetCueCaptions(), CaptionStyle, CaptionTextScale());

    /// <summary>Subtitle Studio canvas size: the caption box for text, the output frame for pictures.</summary>
    public (int Width, int Height) SubtitleFrameSize
    {
        get
        {
            var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);

            // Pictures are positioned on the full frame by their file; text is shown cropped to the caption box.
            if (SubtitlesAreImages)
                return (frameWidth, frameHeight);
            var (_, _, width, height) = GetCaptionRect(frameWidth, frameHeight);
            return (Math.Max(width, 16), Math.Max(height, 16));
        }
    }

    /// <summary>
    /// Text cues as caption lines (one per cue, words spread by length) on the timeline clock, which equals the output clock.
    /// </summary>
    private List<List<CaptionWord>> GetCueCaptions()
    {
        var captions = new List<List<CaptionWord>>();
        foreach (var cue in SubtitleCues.OrderBy(c => c.Start))
        {
            var (start, end) = (cue.Start + SubtitleOffsetSeconds, cue.End + SubtitleOffsetSeconds);
            start = Math.Max(start, 0);
            var words = cue.GetWords();
            if (words.Length == 0 || end - start < 0.02)
                continue;

            // Word spans run boundary to boundary: Subtitle Studio boundaries, else length-proportional shares.
            var breaks = cue.GetBreaks();
            var caption = new List<CaptionWord>(words.Length);
            for (var i = 0; i < words.Length; i++)
            {
                var (from, to) = (i == 0 ? 0 : breaks[i - 1], i < breaks.Length ? breaks[i] : 1);
                caption.Add(new CaptionWord(words[i], start + (end - start) * from, start + (end - start) * to));
            }

            captions.Add(caption);
        }

        return captions;
    }

    /// <summary>Caption row content: the cues, else the last preview transcription.</summary>
    public IReadOnlyList<(double Start, double End, string Text, SubtitleCue? Cue)> GetSubtitleSegments()
    {
        if (HasSubtitleCues)
            return SubtitleCues.Select(c => (c.Start + SubtitleOffsetSeconds, c.End + SubtitleOffsetSeconds, c.Text.ReplaceLineEndings(" "), (SubtitleCue?)c)).ToList();

        return AutoCaptions
            ? _generatedCaptions.Select(c => (c[0].Start, c[^1].End, string.Join(" ", c.Select(w => w.Text)), (SubtitleCue?)null)).ToList()
            : [];
    }

    // Last preview transcription as lines on the timeline clock.
    private List<List<CaptionWord>> _generatedCaptions = [];

    /// <summary>
    /// Converts auto-captions into an editable subtitle track (transcribing or reusing cached words). Captions then render from the
    /// cues with no re-transcription until the track is removed.
    /// </summary>
    public async Task ConvertCaptionsToCuesAsync()
    {
        if (await RequireCaptionToolsAsync("Transcribing the captions"))
            await TranscribeToCuesAsync();
    }

    private Task TranscribeToCuesAsync() => RunOperationAsync("Transcribing captions", WorkLane.Interactive, async cancellationToken =>
    {
        if (ResolveCaptionSource() is not { } source)
            return "There is nothing to make captions from: load a video, or choose what to listen to.";

        var (words, problem) = await GetCaptionWordsAsync(source, cancellationToken);
        if (words is null)
            return problem;

        // Clip-played sources: store in source time and project through the clips, so cuts, folds and dead-air removal align by construction.
        if (source.IsAnchored)
        {
            var anchors = CaptionProjection.Anchor(CaptionGenerator.Lines(words, CaptionStyle));
            if (anchors.Count == 0)
                return "No speech was found to make captions of.";

            Checkpoint("generate captions");
            SetSubtitleTrack([], "", false, 0, source, anchors);
            PlaceCaptionsOverTheirTrack();
            return SubtitleCues.Count == 0
                ? $"Made {anchors.Count} captions from {DescribeCaptionSource(source)}, but none of it plays on the timeline now."
                : $"Made {anchors.Count} captions from {DescribeCaptionSource(source)}: they follow its clips, can be edited, and are drawn from now on.";
        }

        // Voiceover and external recordings are already on the timeline clock.
        var cues = CaptionProjection.ProjectLines(words, CaptionSpans(source), CaptionStyle).Select(line =>
        {
            var (start, end) = (line[0].Start, Math.Max(line[^1].End, line[0].Start + 0.1));
            var cue = new SubtitleCue { Start = start, End = end, Text = string.Join(" ", line.Select(w => w.Text.Trim())) };

            // Keep transcribed word times: each boundary is the next word's start.
            if (line.Count > 1)
                cue.WordBreaks = line.Skip(1).Select(w => (w.Start - start) / (end - start)).ToArray();
            return cue;
        }).ToList();
        if (cues.Count == 0)
            return "No speech was found to make captions of.";

        Checkpoint("generate captions");
        SetSubtitleTrack(SubtitleFiles.Numbered(cues), "", false, 0);
        return $"Made {cues.Count} captions from {DescribeCaptionSource(source)}: they can now be edited, and the captions are drawn from them.";
    });

    /// <summary>Adds a manual cue; the first creates the track and enables captions.</summary>
    public SubtitleCue AddSubtitleCue(double start, double end, string text)
    {
        CheckpointSubtitles("add a subtitle");
        var cue = new SubtitleCue { Index = SubtitleCues.Count + 1, Start = Math.Max(start, 0), End = Math.Max(end, start + 0.1), Text = text };
        SubtitleCues.Add(cue);
        if (!AutoCaptions)
            AutoCaptions = true;
        NotifySubtitlesChanged();
        return cue;
    }

    public void RemoveSubtitleCue(SubtitleCue cue)
    {
        if (!SubtitleCues.Contains(cue))
            return;

        CheckpointSubtitles("delete a subtitle");
        SubtitleCues.Remove(cue);
        NotifySubtitlesChanged();
    }

    /// <summary>
    /// Splits a cue at a time (cue clock, pre-offset): earlier words stay, later words move to a new following cue, word times kept.
    /// A one-word cue becomes two copies. Returns the new cue, or null when the time is outside the cue.
    /// </summary>
    public SubtitleCue? SplitSubtitleCue(SubtitleCue cue, double at)
    {
        if (SubtitlesAreImages || !SubtitleCues.Contains(cue) || at <= cue.Start + 0.05 || at >= cue.End - 0.05)
            return null;

        CheckpointSubtitles("split a subtitle");
        var (words, breaks) = (cue.GetWords(), cue.GetBreaks());
        var share = (at - cue.Start) / (cue.End - cue.Start);
        var second = new SubtitleCue { Start = Math.Round(at, 3), End = cue.End, Text = cue.Text };
        if (words.Length > 1)
        {
            // Words before the cut, clamped so each side keeps at least one.
            var kept = Math.Clamp(breaks.Count(b => b < share) + 1, 1, words.Length - 1);
            second.Text = string.Join(" ", words[kept..]);
            second.WordBreaks = breaks[kept..].Select(b => (b - share) / (1 - share)).ToArray();
            cue.Text = string.Join(" ", words[..kept]);
            cue.WordBreaks = breaks[..(kept - 1)].Select(b => b / share).ToArray();
        }

        cue.End = Math.Round(at, 3);
        SubtitleCues.Insert(SubtitleCues.IndexOf(cue) + 1, second);
        NotifySubtitlesChanged();
        return second;
    }

    /// <summary>
    /// Splits the caption at a sequence time at the word there (razor, Split at Playhead, captions menu;
    /// CaptionProjection.SplitAtWord). One undo step; anchored captions are split in source time. Returns a status line.
    /// </summary>
    public string SplitCaptionAt(double sequenceSeconds)
    {
        if (SubtitlesAreImages || !HasTextCues)
            return "There are no caption words to split: generate captions, or import a subtitle file, first.";

        var onCue = sequenceSeconds - SubtitleOffsetSeconds;
        var cue = SubtitleCues.FirstOrDefault(c => onCue > c.Start && onCue < c.End);
        if (cue is null)
            return "No caption is under the playhead to split.";
        if (CaptionProjection.SplitAtWord(StateOf(cue), onCue) is not { } halves)
            return $"\"{cue.Text.ReplaceLineEndings(" ")}\" is one word: there is nothing to split it between.";

        CheckpointSubtitles("split a caption");
        (cue.Start, cue.End, cue.Text, cue.WordBreaks) = (halves.First.Start, halves.First.End, halves.First.Text, halves.First.Breaks);
        var second = new SubtitleCue { Start = halves.Second.Start, End = halves.Second.End, Text = halves.Second.Text, WordBreaks = halves.Second.Breaks };
        SubtitleCues.Insert(SubtitleCues.IndexOf(cue) + 1, second);

        // Split anchored captions in source time so the next projection shows both halves.
        WriteBackCaptions();
        NotifySubtitlesChanged();
        return $"Split the caption: \"{halves.First.Text}\" | \"{halves.Second.Text}\". Ctrl+Z puts it back.";
    }

    /// <summary>Removes a word from a cue, giving its time to its neighbours; false for a cue's only word.</summary>
    public bool RemoveSubtitleWord(SubtitleCue cue, int word)
    {
        var (words, breaks) = (cue.GetWords(), cue.GetBreaks().ToList());
        if (SubtitlesAreImages || words.Length < 2 || word < 0 || word >= words.Length)
            return false;

        CheckpointSubtitles("delete a word");

        // Drop the boundary after the word (before it for the last word).
        breaks.RemoveAt(Math.Min(word, breaks.Count - 1));
        cue.Text = string.Join(" ", words.Where((_, i) => i != word));
        cue.WordBreaks = breaks.Count > 0 ? [.. breaks] : null;
        NotifySubtitlesChanged();
        return true;
    }

    /// <summary>
    /// Serializable subtitle track for projects and undo: all timeline cues (anchored ones flagged) plus the anchor source and
    /// source-time captions.
    /// </summary>
    private SubtitleTrackData? CaptureSubtitles() => !HasSubtitleCues && _captionAnchorSource is null ? null : new SubtitleTrackData
    {
        SourcePath = SubtitleSourcePath,
        IsImage = SubtitlesAreImages,
        OffsetMs = SubtitleOffsetMs,
        Cues = SubtitleCues.Select(c => new SubtitleCueState(c.Start, c.End, c.Text, c.WordBreaks) { Projected = c.Piece is not null }).ToList(),
        Source = _captionAnchorSource,
        Anchors = _captionAnchorSource is null ? null : [.. _captionAnchors],
    };

    private void ApplySubtitles(SubtitleTrackData? data)
    {
        // Omit projected anchored cues; they are rebuilt from anchors and the timeline.
        var anchoredTo = data is { IsImage: false, Source: { IsAnchored: true } source, Anchors.Count: > 0 } ? source : null;
        var anchors = anchoredTo is null ? null
            : data!.Anchors!.Where(a => double.IsFinite(a.Start) && double.IsFinite(a.End) && a.End >= a.Start).OrderBy(a => a.Start).ToList();
        var cues = (data?.Cues ?? []).Where(c => double.IsFinite(c.Start) && double.IsFinite(c.End) && c.End > c.Start && !(c.Projected && anchoredTo is not null))
            .Select((c, i) => new SubtitleCue { Index = i + 1, Start = c.Start, End = c.End, Text = c.Text ?? "", WordBreaks = c.Breaks }).ToList();
        var images = data?.IsImage == true && cues.Count > 0;
        var kept = cues.Count > 0 || anchoredTo is not null;
        SetSubtitleTrack(cues, kept ? data!.SourcePath ?? "" : "", images, kept ? data!.OffsetMs : 0, anchoredTo, anchors);

        // Picture tracks rebase against the on-disk file, re-read when still present and unchanged.
        if (images)
        {
            try
            {
                var read = SubtitleFiles.Read(SubtitleSourcePath);
                _subtitleOriginal = read.Count == cues.Count ? read : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _subtitleOriginal = [];
            }

            ShowPictureSubtitles();
        }
    }
}
