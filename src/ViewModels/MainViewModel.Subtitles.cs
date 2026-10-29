using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CliWrap;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The subtitle track: cues imported from a file (.srt, .vtt; or the timings of a .sup or VobSub), made from the
// auto-captions, or typed in the Subtitle Editor. There is one. While it has text cues, the captions are made
// of them instead of being transcribed: they are drawn in the caption style, in the caption box, by the same
// steps (the player's own subtitle track for the preview, the caption overlay for the export).
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

    // A picture track as it was read from its file, cue for cue: what the edited times are measured against.
    private List<SubtitleCue> _subtitleOriginal = [];
    private string _imageSubtitleFile = "";
    private int _imageSubtitleRun;

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
            // Pictures are handed to the player as they are; words are shown by the subtitles preview, switched on for them.
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

        // Told only when something moved: the preview's caption file is then written again (SyncEditorCaptions).
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
        // Told once as a document put in place is done (4.4-BugHunt): one caption file written, one swap of the player's
        // subtitles, not one for each part of the step that touched them.
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
    /// the timeline shows them as they are now, and the main player's subtitles are written again.
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
        {
            _imageSubtitleFile = HasSubtitleCues ? WriteImageSubtitles() ?? "" : "";
            InvalidateLiveFilter();
        }

        if (PreviewSubtitles && !IsSubtitleEditorOpen)
            RefreshLiveCaptionsSoon();
    }

    /// <summary>The picture track as a file a player can be given: the imported file itself while nothing was moved, a retimed copy of it otherwise.</summary>
    private string? WriteImageSubtitles()
    {
        if (!SubtitlesAreImages || !File.Exists(SubtitleSourcePath))
            return null;

        var moved = Math.Abs(SubtitleOffsetSeconds) > 0.0005 || _subtitleOriginal.Count != SubtitleCues.Count
            || SubtitleCues.Where((cue, i) => Math.Abs(cue.Start - _subtitleOriginal[i].Start) > 0.0005 || Math.Abs(cue.End - _subtitleOriginal[i].End) > 0.0005).Any();
        if (!moved)
            return SubtitleFiles.IdxPathOf(SubtitleSourcePath) is var idx && Path.GetExtension(SubtitleSourcePath).ToLowerInvariant() is ".sub" or ".vobsub" ? idx : SubtitleSourcePath;

        try
        {
            // A new name each time: the player only loads a file again when its name has changed.
            return SubtitleFiles.WriteRetimed(SubtitleSourcePath, _subtitleOriginal, SubtitleCues, SubtitleOffsetSeconds, _workFolder, $"subtitles_retimed_{++_imageSubtitleRun}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = $"The retimed subtitles could not be written: {ex.Message}";
            return null;
        }
    }

    /// <summary>The picture track's file for the main player; empty when there is none, or while the Subtitle Editor shows it instead.</summary>
    private string LiveImageSubtitlePath => SubtitlesAreImages && HasSubtitleCues && !IsSubtitleEditorOpen ? _imageSubtitleFile : "";

    /// <summary>
    /// Writes the subtitle track as a file for a player to draw over a blank picture: the Subtitle Editor's
    /// own. Text is written as captions for the whole frame, in the caption style; a picture track is its
    /// file, retimed. Returns the file, or null when there is nothing to show.
    /// </summary>
    public string? WriteSubtitlePreview(string name)
    {
        if (!HasSubtitleCues)
            return null;
        if (SubtitlesAreImages)
            return WriteImageSubtitles();

        // Written for the caption box alone, not the frame: the editor's player shows a picture of the box's
        // shape, so the words fill it and are large enough to read, however small the box is on the frame.
        var path = Path.Combine(_workFolder, name + ".ass");
        var lines = GetCueCaptions();
        WriteCaptionFile(lines.SelectMany(l => l).ToList(), lines, path, 0, forPlayer: false);
        return path;
    }

    /// <summary>
    /// Makes the blank video the Subtitle Editor's player plays: black, the shape of the output frame, as long
    /// as the timeline. It is a real file, encoded once and kept for the session, because a player can go to
    /// any moment of a file at once; a picture generated as it is played (lavfi's "color") can only be played
    /// forwards, and every seek into it breaks it. It is tiny (the subtitles are drawn at the size of the
    /// window, not of the video), so it is made in a moment and is a few hundred kilobytes for hours.
    /// Returns the file, or null when it could not be made.
    /// </summary>
    public async Task<string?> PrepareSubtitleCanvasAsync(double seconds, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return null;

        var (frameWidth, frameHeight) = SubtitleFrameSize;
        const int height = 72;
        var width = Math.Clamp((int)Math.Round(height * (double)frameWidth / Math.Max(frameHeight, 1) / 2) * 2, 16, 1024);
        var length = (int)Math.Clamp(Math.Ceiling(seconds), 1, 12 * 3600);
        var path = Path.Combine(_workFolder, $"subtitle_canvas_{width}x{height}_{length}.mkv");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        Directory.CreateDirectory(_workFolder);
        var making = Path.Combine(_workFolder, $"subtitle_canvas_{Guid.NewGuid():N}.mkv");
        try
        {
            // An I-frame every second, so that any moment is a short step from one.
            var result = await WorkScheduler.Run(new WorkSpec("Preparing the Subtitle Editor", WorkLane.Interactive) { Visible = false },
                work => ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                    .WithArguments(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c=black:s={width}x{height}:r=10",
                        "-t", length.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-tune", "stillimage",
                        "-g", "10", "-pix_fmt", "yuv420p", making])
                    .WithValidation(CommandResultValidation.None), work.Token), cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(making))
            {
                AppLog.Write($"The Subtitle Editor's blank video could not be made (exit code {result.ExitCode}): {result.StandardError.Trim()}");
                return null;
            }

            File.Move(making, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write("The Subtitle Editor's blank video could not be made", ex);
            return null;
        }
    }

    /// <summary>The size of the frame the subtitles are placed on, for a player that shows them on a blank picture of that shape.</summary>
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

            _imageSubtitleFile = WriteImageSubtitles() ?? "";
        }
    }
}
