using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.ViewModels;

// The subtitle track: cues imported from a file (.srt, .vtt; or the timings of a .sup or VobSub), made from the
// auto-captions, or typed in the Subtitle Editor. There is one. While it has text cues, the captions are made
// of them instead of being transcribed: they are drawn in the caption style, in the caption box, by the same
// steps (the GPU preview's captions, the caption overlay for the export). A picture track's pictures are decoded
// for the GPU preview, which draws them over the main video's picture (5.0-Verify); the export leaves them out.
public partial class MainViewModel
{
    public ObservableCollection<SubtitleCue> SubtitleCues { get; } = [];

    /// <summary>The file the track was imported from; empty for one made in HandPeg.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleTrackSummary))]
    private string _subtitleSourcePath = "";

    /// <summary>The track is pictures (PGS, VobSub): its cues are when the file's pictures are shown, and have no words to edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextCues))]
    [NotifyPropertyChangedFor(nameof(SubtitleTrackSummary))]
    private bool _subtitlesAreImages;

    /// <summary>Milliseconds every cue is shifted by: later when positive, earlier when negative.</summary>
    [ObservableProperty] private double _subtitleOffsetMs;

    /// <summary>The Subtitle Editor is open: it draws the subtitles in a player of its own, and the main player leaves them out meanwhile.</summary>
    [ObservableProperty] private bool _isSubtitleEditorOpen;

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
                ? $"{from}  ·  {count}, image-based: shown in the player and retimed in the Subtitle Editor, but not written into the export"
                : $"{from}  ·  {count}, drawn as captions in the caption style";
        }
    }

    private double SubtitleOffsetSeconds => double.IsFinite(SubtitleOffsetMs) ? SubtitleOffsetMs / 1000 : 0;

    // A picture track as it was read from its file, cue for cue: what the edited times are measured against, and how each
    // cue is matched to its picture.
    private List<SubtitleCue> _subtitleOriginal = [];

    /// <summary>Reads a subtitle file in as the subtitle track, replacing the one there was. Returns false, with the reason in the status bar, when it cannot be read.</summary>
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
            StatusText = $"The subtitles could not be read: {ex.Message}";
            return false;
        }

        if (cues.Count == 0)
        {
            StatusText = $"No subtitles were found in {Path.GetFileName(path)}.";
            return false;
        }

        var images = SubtitleFiles.IsImageFile(path);
        Checkpoint("import subtitles");

        // Words timed to the main video while it is still the whole sequence (its file's time is the timeline's) are
        // anchored to its picture (4.4): cuts, folds and moves made afterwards carry them along. Once it is edited, a file's
        // times can no longer be told apart from the edited timeline's, and they stay on the timeline's clock. A file's cues
        // have no word times, so each is given its words spread over it by their length: a cut or fold through one then
        // divides its words, and the part no window shows is kept through an edit of the part still seen.
        if (!images && HasSource && FrameEngine && IsMainWholeSequence && MainVideoClips().Any())
        {
            var anchors = cues.Where(c => c.End > c.Start).OrderBy(c => c.Start)
                .Select(c => CaptionProjection.WithWordTimes(new SubtitleCueState(c.Start, c.End, c.Text))).ToList();
            SetSubtitleTrack([], path, false, 0, new CaptionSource(CaptionSourceKind.MainFile, LocalMediaPath, -1), anchors);
        }
        else
        {
            SetSubtitleTrack(cues, path, images, 0);
        }
        Log($"Imported {cues.Count} subtitle cue{(cues.Count == 1 ? "" : "s")} from {Path.GetFileName(path)}{(images ? " (image-based: timing only)" : "")}");

        // Words are drawn as captions, so captions are switched on for them; and shown, where there is a player to show them.
        if (!images && !AutoCaptions)
            AutoCaptions = true;
        if (HasSource && !_isBackgroundWorker)
        {
            // Pictures are drawn by the player as they are (decoded first, in the background); words are shown by the
            // subtitles preview, switched on for them.
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

    /// <summary>Puts a subtitle track in place of the one there was.</summary>
    /// <param name="cues">The cues on the timeline's own clock (with anchored captions, those not anchored).</param>
    /// <param name="anchoredTo">What the anchored captions were made from and follow (4.4); null for none.</param>
    /// <param name="anchors">The anchored captions, in their source's time, ordered by their start.</param>
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

    // ----- Anchored captions (4.4) -----
    // Captions made from a source the timeline plays as clips (the main file, a sound clip) are kept in that source's own
    // time, with each word's own times, and the cues are a view of them: projected through every clip that plays the
    // source after every commit, undo and load (ReprojectCaptions). Edits made in the Subtitle Editor are written back into
    // the source's time (WriteBackCaptions). Cues of a track with no anchors are on the timeline's clock, as before 4.4.

    // The anchored captions, in their source's time and ordered by their start; and what they were made from (null for none).
    private List<SubtitleCueState> _captionAnchors = [];
    private CaptionSource? _captionAnchorSource;

    /// <summary>
    /// Shows the anchored captions on the timeline again, through the clips that play their source as they now are: after
    /// every commit, undo and load (4.4). Nothing happens without anchored captions, and nothing is told while what is shown
    /// stays the same.
    /// </summary>
    public void ReprojectCaptions()
    {
        // While a document is being put in place (an undo step, a project), once, as it ends, from the anchors it put back
        // (4.4-BugHunt): never through half of it, nor from the anchors of before it.
        if (_restoring > 0)
        {
            _reprojectPending = true;
            return;
        }

        if (_captionAnchorSource is null || SubtitlesAreImages || IsSubtitleEditorOpen)
            return;

        // Told only when something moved: the preview's captions are then made again (SyncEditorCaptions).
        if (ShowCaptionAnchors([.. SubtitleCues.Where(c => c.Piece is null).Select(StateOf)]))
            NotifySubtitlesChanged();
    }

    /// <summary>
    /// Takes what was changed on the cues of anchored captions (in the Subtitle Editor, in place) back into their source's
    /// time, and shows them again from there. A cue still as it was projected changes nothing.
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
    /// Puts the anchored captions, as the timeline shows them now, beside the cues given (on the timeline's own clock) as the
    /// track's cues, in order. The cue objects there are kept: each goes on showing the caption it showed (the same words at
    /// nearly the same moment) where there still is one, so a list or an editor that holds one goes on holding it, and the
    /// others take what is left. Returns whether anything shown changed.
    /// </summary>
    private bool ShowCaptionAnchors(List<SubtitleCueState> loose)
    {
        var shown = _captionAnchorSource is { } source ? CaptionProjection.Project(_captionAnchors, CaptionSpans(source)) : [];
        if (loose.Count > 0)
        {
            shown.AddRange(loose);
            shown.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        // Which cue object shows which caption: the one that showed it, else one left over, else a new one.
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

            // In order, moved only where it is not already (it mostly is: then nothing is searched).
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

    /// <summary>The words of the anchored captions, in their source's time, when they were made from the source given; null otherwise.</summary>
    private List<CaptionWord>? AnchoredCaptionWords(CaptionSource source) =>
        source.SameAs(_captionAnchorSource) ? CaptionProjection.Words(_captionAnchors) : null;

    /// <summary>
    /// Generate Captions: transcribes the speech now and puts the captions on the timeline as the
    /// subtitle track, replacing what was there, and switches captions on. From then on the track is what is
    /// shown, edited, saved and exported: an export draws it as it stands and never transcribes again.
    /// </summary>
    [RelayCommand]
    private async Task GenerateCaptionsAsync()
    {
        if (!HasSource)
        {
            StatusText = "Load a video first: there is nothing to make captions from.";
            return;
        }

        // whisper.cpp and the speech model are fetched the first time captions are asked for.
        if (!await RequireCaptionToolsAsync("Generate Captions"))
            return;

        // There are subtitles on the timeline already, and whatever was done to them by hand would go with them: asked first.
        if (HasSubtitleCues || _captionAnchorSource is not null)
        {
            var answer = System.Windows.MessageBox.Show(
                System.Windows.Application.Current?.MainWindow!,
                "Generating new captions will overwrite your existing subtitle track and discard any manual timing or text edits. Do you want to proceed?",
                "Generate Captions", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes)
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
    /// The player shows the subtitle track while captions are switched on and there is a track to show. Nothing is
    /// transcribed for this; without a track, the captions are only made when the video is exported.
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

    // The subtitles as the last undo step left them. The Subtitle Editor changes cues in place and says so
    // afterwards (CommitSubtitleEdits): what it changed is told apart from what was there by comparing with this.
    private string _committedSubtitles = "null";

    // The one undo step everything done in the Subtitle Editor goes into while it is open (4.3E). The editor is modal, so
    // nothing else is changed meanwhile, and it keeps finer steps of its own (each typing run, drag, split, addition and
    // deletion): the main window's Undo then takes back the whole visit at once, instead of the editor's every pause
    // pushing the timeline's twenty steps out.
    private byte[]? _subtitleVisitStep;

    // The subtitles as they were when the editor was opened: a visit that leaves them so leaves no step behind.
    private string _subtitlesAtVisit = "null";

    /// <summary>
    /// Changes whenever the undo history does (a step taken, undone or redone): the main window tells by it whether typing
    /// in a text box is newer than the timeline's last step, and so whose Ctrl+Z it is.
    /// </summary>
    public object? UndoMark => _undo.Count > 0 ? _undo[^1].State : null;

    /// <summary>Whether the newest undo step is the open Subtitle Editor's one step, which what is done in it now goes into.</summary>
    private bool InSubtitleVisit => IsSubtitleEditorOpen && _subtitleVisitStep is { } step && _undo.Count > 0 && ReferenceEquals(_undo[^1].State, step);

    /// <summary>
    /// Makes an undo step of the edits made to the cues in place since the last step: one that brings back the
    /// cues as they were then. Nothing when nothing was changed. While the Subtitle Editor is open, the edits go
    /// into its visit's one step instead.
    /// </summary>
    private void CheckpointSubtitleEdits()
    {
        // Never while a step is being put back (4.4-BugHunt): the cues are being written from it, not edited.
        if (_restoring > 0)
            return;

        // Anchored captions take what was changed on their cues back into their source's time first (4.4).
        WriteBackCaptions();
        var now = JsonSerializer.Serialize(CaptureSubtitles());
        if (now == _committedSubtitles)
            return;

        if (!IsSubtitleEditorOpen)
            Checkpoint("edit the subtitles", SnapshotBeforeSubtitleEdits());
        else if (!InSubtitleVisit)
            BeginSubtitleVisit(SnapshotBeforeSubtitleEdits());
        _committedSubtitles = now;

        // Taken back in the editor to what they were when it was opened: the visit changed nothing after all.
        if (InSubtitleVisit && now == _subtitlesAtVisit)
        {
            _undo.RemoveAt(_undo.Count - 1);
            _subtitleVisitStep = null;
        }
    }

    /// <summary>The timeline as it is, with the subtitles as the last undo step left them: what undoing the edits made in place since brings back.</summary>
    private TimelineSnapshot SnapshotBeforeSubtitleEdits() =>
        CaptureSnapshot() with { Subtitles = JsonSerializer.Deserialize<SubtitleTrackData?>(_committedSubtitles) };

    /// <summary>Takes the open Subtitle Editor's one undo step, bringing back the state given; what is done in the editor from now on goes into it.</summary>
    private void BeginSubtitleVisit(TimelineSnapshot before)
    {
        Checkpoint("edit the subtitles", before);
        _subtitleVisitStep = !_isBackgroundWorker && _undo.Count > 0 ? _undo[^1].State : null;
    }

    /// <summary>
    /// Takes the undo step a change to the cues is made after: a step of its own, named for it; while the Subtitle Editor
    /// is open, its visit's one step (taken now when the visit has none yet), the editor keeping the change as a step of its own.
    /// </summary>
    private void CheckpointSubtitles(string what)
    {
        CheckpointSubtitleEdits();
        if (!IsSubtitleEditorOpen)
            Checkpoint(what);
        else if (!InSubtitleVisit)
            BeginSubtitleVisit(CaptureSnapshot());
    }

    private void NotifySubtitlesChanged()
    {
        // Told once as a document put in place is done (4.4-BugHunt): the captions made once, the player told once, not
        // once for each part of the step that touched them.
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
        TimelineChanged?.Invoke();
        if (!IsBusy)
            SyncEditorCaptions();
    }

    /// <summary>
    /// Called when cues were edited by hand (in the Subtitle Editor): they are put in order and numbered again,
    /// the timeline shows them as they are now, and the main player is given them again.
    /// </summary>
    public void CommitSubtitleEdits()
    {
        // (No edit of the cues is made while a step is being put back: 4.4-BugHunt.)
        if (_restoring > 0)
            return;

        CheckpointSubtitleEdits();

        // A picture track's cues stay in the file's order: that is how they are matched to its pictures.
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
            TimelineChanged?.Invoke();
            RefreshSubtitlePreview();
        }
    }

    partial void OnIsSubtitleEditorOpenChanged(bool value)
    {
        // Each visit of the editor is one undo step of its own, measured from the subtitles as it found them.
        _subtitleVisitStep = null;
        if (value)
            _subtitlesAtVisit = JsonSerializer.Serialize(CaptureSubtitles());

        // Closed: the main player draws them again, as they now are. Open: it lets go of them.
        if (!value)
            RefreshSubtitlePreview();
        if (!_isBackgroundWorker)
            InvalidateLiveFilter();
    }

    /// <summary>Has the main player show the subtitle track as it is now.</summary>
    private void RefreshSubtitlePreview()
    {
        if (_isBackgroundWorker)
            return;

        if (SubtitlesAreImages || !HasSubtitleCues)
            ShowPictureSubtitles();

        if (PreviewSubtitles && !IsSubtitleEditorOpen)
            RefreshLiveCaptionsSoon();
    }

    // ----- The picture track in the GPU preview (5.0-Verify) -----
    // Its file is decoded once, in the background (PictureSubtitleDecoder: HandPeg's FFmpeg, the pictures kept packed in
    // memory), and each cue is paired with the picture its file shows at the cue's own moment there (_subtitleOriginal), so
    // a cue retimed in the Subtitle Editor or moved by the offset keeps its picture. The GPU preview is given them with the
    // sequence (PreviewPictureSubtitles, the state's PictureSubtitles) and told when they changed (CaptionsChanged).

    // The file's pictures as far as they have been read, which file (its path, size and time) they are of, the decode
    // under way, and the cues paired with them.
    private PictureSubtitleSet? _pictureSet;
    private string _pictureSetOf = "";
    private CancellationTokenSource? _pictureDecode;
    private PictureSubtitleTrack? _pictureTrack;
    private bool _toldPictures;

    /// <summary>
    /// The picture track's cues with their pictures, for the GPU preview (as far as its file has been read: a track put in
    /// with nothing open is read the first time the preview asks); null when there is none, or while the Subtitle Editor
    /// shows the track instead.
    /// </summary>
    public PictureSubtitleTrack? PreviewPictureSubtitles()
    {
        if (!SubtitlesAreImages || !HasSubtitleCues || IsSubtitleEditorOpen)
            return null;
        if (_pictureSetOf.Length == 0)
            ShowPictureSubtitles();
        return _pictureTrack;
    }

    /// <summary>
    /// The picture track as it is now into the preview: its file decoded the first time (and again once it changed on disk),
    /// its cues paired with their pictures again, and the preview told. Without a picture track, what was read is let go.
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

    /// <summary>Pairs each cue with its picture (as far as the file has been read) and tells the preview.</summary>
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
    /// Reads the picture track's file on the background lane: what has been read is shown about once a second while the
    /// rest is read, so the first pictures are up at once. A decode of a file let go (another track, the file changed) is
    /// stopped and its pictures dropped.
    /// </summary>
    private void DecodePictureSubtitles(string of)
    {
        _pictureDecode?.Cancel();
        var decode = _pictureDecode = new CancellationTokenSource();
        (_pictureSet, _pictureSetOf) = (null, of);

        // A VobSub is read from its .idx (FFmpeg finds the .sub beside it); a .sup as it is.
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
                // Let go of (another track, the file changed, closing): nothing to show.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write($"Picture subtitles: {Path.GetFileName(path)} could not be read for the player", ex);
            }
        }

        // What has been read, into the preview (the UI thread): unless the decode was let go since.
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
    /// What the Subtitle Editor's preview draws a text track with: its cues as captions on the timeline's clock (one line per
    /// cue, its words timed as the export times them), the caption style, and what the style's sizes are multiplied by in
    /// the caption box (as <see cref="WriteCaptionFile"/> works it out). The box is <see cref="SubtitleFrameSize"/>.
    /// </summary>
    public (IReadOnlyList<List<CaptionWord>> Lines, CaptionStyle Style, double TextScale) SubtitleEditorCaptions() =>
        (GetCueCaptions(), CaptionStyle, CaptionTextScale());

    /// <summary>The size of the frame the Subtitle Editor shows the subtitles on: the caption box for words, the output frame for pictures.</summary>
    public (int Width, int Height) SubtitleFrameSize
    {
        get
        {
            var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);

            // Pictures are placed on the whole frame by their own file. Words are shown close up: the caption box and nothing around it.
            if (SubtitlesAreImages)
                return (frameWidth, frameHeight);
            var (_, _, width, height) = GetCaptionRect(frameWidth, frameHeight);
            return (Math.Max(width, 16), Math.Max(height, 16));
        }
    }

    /// <summary>
    /// The text cues as captions: one per cue, its words spread over its time by their length. The cues are timed on the
    /// timeline, which is the output's clock (4.3C: nothing is cut out of it on the way).
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

            // Each word from its boundary to the next: the ones set in the Subtitle Editor, or its share by length.
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

    /// <summary>What is drawn on the caption layer's time bar: the cues, or failing those the captions last transcribed.</summary>
    public IReadOnlyList<(double Start, double End, string Text, SubtitleCue? Cue)> GetSubtitleSegments()
    {
        if (HasSubtitleCues)
            return SubtitleCues.Select(c => (c.Start + SubtitleOffsetSeconds, c.End + SubtitleOffsetSeconds, c.Text.ReplaceLineEndings(" "), (SubtitleCue?)c)).ToList();

        return AutoCaptions
            ? _generatedCaptions.Select(c => (c[0].Start, c[^1].End, string.Join(" ", c.Select(w => w.Text)), (SubtitleCue?)null)).ToList()
            : [];
    }

    // The captions last transcribed for the preview, line by line, on the timeline's clock.
    private List<List<CaptionWord>> _generatedCaptions = [];

    /// <summary>
    /// Makes the subtitle track from the auto-captions: transcribes what they listen to (or takes the words
    /// already transcribed) and turns each caption into a cue, which can then be edited. From then on the
    /// captions are made of the cues, and nothing is transcribed again until the track is removed.
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

        // Made from what the timeline plays as clips: kept in its own time and shown through those clips (4.4), so cuts,
        // folds and dead air made before or after line up by construction.
        if (source.IsAnchored)
        {
            var anchors = CaptionProjection.Anchor(CaptionGenerator.Lines(words, CaptionStyle));
            if (anchors.Count == 0)
                return "No speech was found to make captions of.";

            Checkpoint("generate captions");
            SetSubtitleTrack([], "", false, 0, source, anchors);
            return SubtitleCues.Count == 0
                ? $"Made {anchors.Count} captions from {DescribeCaptionSource(source)}, but none of it plays on the timeline now."
                : $"Made {anchors.Count} captions from {DescribeCaptionSource(source)}: they follow its clips, can be edited, and are drawn from now on.";
        }

        // The voiceover and an external recording are on the timeline's clock, where they are heard.
        var cues = CaptionGenerator.Lines(CaptionProjection.ProjectWords(words, CaptionSpans(source)), CaptionStyle).Select(line =>
        {
            var (start, end) = (line[0].Start, Math.Max(line[^1].End, line[0].Start + 0.1));
            var cue = new SubtitleCue { Start = start, End = end, Text = string.Join(" ", line.Select(w => w.Text.Trim())) };

            // The words keep the moments they were heard at: each boundary is where the next word was heard to begin.
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

    /// <summary>Adds a cue by hand. The first one makes the track; its words are drawn as captions, so captions are switched on for them.</summary>
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
    /// Cuts a cue in two at a moment inside it (seconds on the cue's own clock, before the offset). The words
    /// said before the cut stay, the rest go to a new cue that follows, and each word keeps when it is said.
    /// A cue of one word becomes two cues of that word. Returns the new cue, or null when the moment is not inside the cue.
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
            // How many words are said before the cut; at least one stays and at least one goes.
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

    /// <summary>Takes one word out of a cue; the words beside it share its time. False for a cue's only word.</summary>
    public bool RemoveSubtitleWord(SubtitleCue cue, int word)
    {
        var (words, breaks) = (cue.GetWords(), cue.GetBreaks().ToList());
        if (SubtitlesAreImages || words.Length < 2 || word < 0 || word >= words.Length)
            return false;

        CheckpointSubtitles("delete a word");

        // The boundary that goes is the one after the word, or before it when it is the last.
        breaks.RemoveAt(Math.Min(word, breaks.Count - 1));
        cue.Text = string.Join(" ", words.Where((_, i) => i != word));
        cue.WordBreaks = breaks.Count > 0 ? [.. breaks] : null;
        NotifySubtitlesChanged();
        return true;
    }

    /// <summary>
    /// Puts the cues (and the offset) back as the Subtitle Editor's own Undo kept them. In place: the cues there are take
    /// the values in order, so a picture track's cues stay matched to its file's pictures and the editor's timeline goes on
    /// drawing the same list. Like the editor's other changes, it reaches the main window's Undo when the editor commits
    /// (CommitSubtitleEdits), in the visit's one step.
    /// </summary>
    public void RestoreSubtitleCues(IReadOnlyList<SubtitleCueState> cues, double offsetMs)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            if (i == SubtitleCues.Count)
                SubtitleCues.Add(new SubtitleCue { Index = i + 1 });

            // The words before their boundaries: words of another count let go of the boundaries they had. A cue showing an
            // anchored caption takes back the piece it showed (4.4), so it is written back through the same clip.
            var (cue, state) = (SubtitleCues[i], cues[i]);
            cue.Text = state.Text ?? "";
            cue.WordBreaks = (double[]?)state.Breaks?.Clone();
            (cue.Start, cue.End, cue.Piece) = (state.Start, state.End, state.Piece);
        }

        while (SubtitleCues.Count > cues.Count)
            SubtitleCues.RemoveAt(SubtitleCues.Count - 1);
        SubtitleOffsetMs = offsetMs;
    }

    /// <summary>
    /// The subtitle track as it is written to a project and kept by undo: every cue on the timeline (those that show anchored
    /// captions marked as such, 4.4) and, for anchored captions, their source and the captions in its time.
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
        // Anchored captions are made again from their anchors and the timeline: the cues that showed them are left out.
        var anchoredTo = data is { IsImage: false, Source: { IsAnchored: true } source, Anchors.Count: > 0 } ? source : null;
        var anchors = anchoredTo is null ? null
            : data!.Anchors!.Where(a => double.IsFinite(a.Start) && double.IsFinite(a.End) && a.End >= a.Start).OrderBy(a => a.Start).ToList();
        var cues = (data?.Cues ?? []).Where(c => double.IsFinite(c.Start) && double.IsFinite(c.End) && c.End > c.Start && !(c.Projected && anchoredTo is not null))
            .Select((c, i) => new SubtitleCue { Index = i + 1, Start = c.Start, End = c.End, Text = c.Text ?? "", WordBreaks = c.Breaks }).ToList();
        var images = data?.IsImage == true && cues.Count > 0;
        var kept = cues.Count > 0 || anchoredTo is not null;
        SetSubtitleTrack(cues, kept ? data!.SourcePath ?? "" : "", images, kept ? data!.OffsetMs : 0, anchoredTo, anchors);

        // A picture track is measured against its file as it is on disk: read again, when it is still there and still the same.
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
