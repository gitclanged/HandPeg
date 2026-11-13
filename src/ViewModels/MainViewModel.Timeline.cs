using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// The timeline as Undo keeps it (4.3C): the document (every track and clip, the markers, the Sequence Output's effects),
/// how each output audio track is made, the blur fill's switch for files opened, the recycle bin, the subtitles, the caption
/// box, and (4.4) the sequence's own look: its colour grade, sharpening and blur fill. Nothing else, and no pictures. A Project
/// Settings step alone (5.4A) also carries the frame as the view model holds it (<see cref="ProjectSettingsFrame"/>; null, and not
/// written, in every other step).
/// </summary>
public sealed record TimelineSnapshot(
    Models.Timeline.TimelineDocument Document, List<AudioOutputEdit> Audio, bool BackgroundHidden = false, List<DeletedClip>? Bin = null,
    SubtitleTrackData? Subtitles = null, LayerState? CaptionLayer = null, CaptionStyle? CaptionStyle = null, SequenceLook? Look = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ProjectSettingsFrame? Frame = null) : System.Text.Json.Serialization.IJsonOnDeserialized
{
    /// <summary>
    /// Whether the step's bin's tethers are to be read as written (5.5.0, the user's Q6; as TimelineDocument.TethersMarked for its
    /// document): written only when the bin holds a piece that lies where a reader of an older step finds a tether.
    /// </summary>
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    internal bool BinTethersMarked
    {
        get => Bin is { Count: > 0 } && Document.LooseIn(Bin).Exists(c => !c.Tethered);
        set => _binTethersMarkedRead = value;
    }

    private bool _binTethersMarkedRead;

    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
    {
        if (_binTethersMarkedRead || Bin is not { Count: > 0 })
            return;

        foreach (var clip in Document.LooseIn(Bin))
            clip.Tethered = true;
    }
}

/// <summary>
/// An undo step as Undo keeps it, and (4.4-BugHunt) as a project carries it: a <see cref="TimelineSnapshot"/> as UTF-8 JSON
/// (System.Text.Json's own way: the names as declared, enums as numbers), Brotli-compressed at quality 1. A project names
/// this shape by its undo format (ProjectFile.CurrentUndoFormat): bump that when the snapshot changes shape incompatibly.
/// </summary>
public static class UndoSteps
{
    /// <summary>
    /// The most a step's JSON may come to as it is read: a timeline with an hour of captions word by word is a few megabytes,
    /// and a damaged step (a project's, from the disk) could claim gigabytes.
    /// </summary>
    public const int MaximumBytes = 128 * 1024 * 1024;

    /// <summary>A timeline packed as a step, its JSON written into the room given (used again; a new one when none).</summary>
    public static byte[] Pack(TimelineSnapshot state, System.Buffers.ArrayBufferWriter<byte>? room = null)
    {
        room ??= new System.Buffers.ArrayBufferWriter<byte>(64 * 1024);
        room.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(room))
            JsonSerializer.Serialize(writer, state);

        var text = room.WrittenSpan;
        var packed = System.Buffers.ArrayPool<byte>.Shared.Rent(System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(text.Length));
        try
        {
            // Quality 1: what CompressionLevel.Fastest is.
            return System.IO.Compression.BrotliEncoder.TryCompress(text, packed, out var written, quality: 1, window: 22)
                ? packed.AsSpan(0, written).ToArray()
                : throw new InvalidOperationException("An undo step could not be compressed.");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(packed);
        }
    }

    /// <summary>
    /// A step read back into a timeline, its JSON written into the room given (used again; a new one when none). Throws for a
    /// step that is not one (InvalidDataException for a damaged stream or one past <see cref="MaximumBytes"/>, JsonException).
    /// </summary>
    public static TimelineSnapshot? Unpack(byte[] state, System.Buffers.ArrayBufferWriter<byte>? room = null)
    {
        room ??= new System.Buffers.ArrayBufferWriter<byte>(64 * 1024);
        room.ResetWrittenCount();
        using (var brotli = new System.IO.Compression.BrotliStream(new MemoryStream(state), System.IO.Compression.CompressionMode.Decompress))
        {
            int read;
            while ((read = brotli.Read(room.GetSpan(65536))) > 0)
            {
                room.Advance(read);
                if (room.WrittenCount > MaximumBytes)
                    throw new InvalidDataException("An undo step is larger than any timeline's.");
            }
        }

        return JsonSerializer.Deserialize<TimelineSnapshot>(room.WrittenSpan);
    }
}

/// <summary>
/// How one output audio track is made (5.2-AuditB: an output choice, by its stream): what is done with it (copied, encoded again,
/// or Ignore / Drop) and its title. Null in a state that did not record them, and then left as they are. (A step from before
/// 5.2-AuditB also held the main video's stream mixing here, by the same name: what is not one of these is not read.)
/// </summary>
public sealed record AudioOutputEdit(int Index, string? Action = null, string? Title = null);

/// <summary>What Remove Dead Air does with the silences it finds.</summary>
public enum DeadAirMode
{
    /// <summary>The cut list becomes everything between the silences (Encoder Mode's way before 4.2; the value at rest).</summary>
    Classic,

    /// <summary>
    /// The silences are folded out of the timeline (4.3D): cut out, what follows closed up, and each kept at its cut to be
    /// put back (a FoldedSegment; right-click the clip after the cut). What is on the timeline is what renders.
    /// </summary>
    SplitAndMark,

    /// <summary>The silences are cut out and removed.</summary>
    Delete,
}

/// <summary>What Remove Dead Air finds the dead air by (4.4).</summary>
public enum DeadAirSource
{
    /// <summary>The stretches of the sound quieter than the threshold, for long enough.</summary>
    Audio,

    /// <summary>The gaps between the words the captions heard, less a little kept on either side of each word.</summary>
    Captions,
}

// Editing on the timeline: undo and redo, deleting and trimming whatever is selected, audio that can be
// slipped and cut on its own, and the question of whether anything has changed since it was last saved.
public partial class MainViewModel
{
    // ----- The time bars -----

    /// <summary>Whether a dragged or dropped clip's edges catch on the other clips' edges, the end of the sequence and the playhead. Remembered.</summary>
    [ObservableProperty] private bool _snapClips = AppSettings.Current.SnapClips;

    partial void OnSnapClipsChanged(bool value)
    {
        AppSettings.Current.SnapClips = value;
        try
        {
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether the layers' controls are shown beside their time bars; folded away, every row's time bar is wider, all alike.</summary>
    [ObservableProperty] private bool _layerControlsShown = true;

    // ----- Undo and redo -----
    // Before anything on the timeline is changed, the timeline as it is goes onto a stack, as a small piece
    // of JSON: the cuts, the layers, the audio edits. Twenty deep. Undo puts the last one back.

    private const int UndoDepth = 20;
    // Each state is kept compressed (Brotli): the JSON of a timeline is mostly the same property names over
    // and over, and shrinks to a small fraction. It is only made text again when it is stepped back to.
    private readonly List<(string What, byte[] State)> _undo = [];
    private readonly List<(string What, byte[] State)> _redo = [];

    // Pass 5.5A: how many of the undo steps predate the last save or open (MarkSaved): the recovery copy lags only through the rest
    // (MainViewModel.Recovery.cs), so it never lands before what was last saved or opened.
    private int _savedDepth;

    // The room a state is written into as UTF-8 before it is compressed, and read back into, kept and used again:
    // with a long subtitle track a state is hundreds of kilobytes of text, which made afresh at every checkpoint
    // (as a string, then its bytes) went on the large object heap each time. Only the compressed copy is kept. (Made
    // anew after a damaged step stretched it far past any timeline's: ReadStep.)
    private System.Buffers.ArrayBufferWriter<byte> _stateText = new(64 * 1024);
    private readonly System.Buffers.ArrayBufferWriter<byte> _otherText = new(16 * 1024);

    /// <summary>A value as JSON (UTF-8), in the room given, which it replaces.</summary>
    private static ReadOnlySpan<byte> WriteJson<T>(T value, System.Buffers.ArrayBufferWriter<byte> room)
    {
        room.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(room))
            JsonSerializer.Serialize(writer, value);
        return room.WrittenSpan;
    }

    /// <summary>Whether two values come out as the same JSON: for "has this part of the state changed".</summary>
    private bool SameJson<T>(T first, T second) => WriteJson(first, _stateText).SequenceEqual(WriteJson(second, _otherText));

    private byte[] Pack(TimelineSnapshot state) => UndoSteps.Pack(state, _stateText);

    private TimelineSnapshot? Unpack(byte[] state) => UndoSteps.Unpack(state, _stateText);

    /// <summary>
    /// An undo step read back, or null when it cannot be (damaged, or of another shape: a history a project carried is read
    /// only when it is stepped to): checked for everything putting it back reads. Why not is written to the log.
    /// </summary>
    private TimelineSnapshot? ReadStep(byte[] state, string what)
    {
        try
        {
            if (Unpack(state) is { } step && IsWhole(step))
            {
                // A step of the shape before 5.2-AuditB (the old main video) is never put back: none of this version's making
                // holds one, and a project of that shape is converted as it is opened, its history let go (TakeUpHistory).
                if (!ProjectStore.HoldsOldKinds(step.Document, step.Bin))
                    return step;

                AppLog.Write($"The undo step \"{what}\" holds the main video of a HandPeg from before 5.2: it is let go");
                return null;
            }

            AppLog.Write($"The undo step \"{what}\" holds no timeline that can be put back");
        }
        catch (Exception ex)
        {
            AppLog.Write($"The undo step \"{what}\" could not be read: {ex.GetType().Name}: {ex.Message}");

            // A damaged step can stretch the room far past any timeline's: it is not kept that size.
            if (_stateText.Capacity > 16 * 1024 * 1024)
                _stateText = new(64 * 1024);
        }

        return null;
    }

    /// <summary>Whether a step read back holds all that putting it back reads: its document, every track's clips with their looks, its output tracks, its bin's looks.</summary>
    private static bool IsWhole(TimelineSnapshot step) =>
        step is { Document: { Tracks: { } tracks, Markers: not null, SequenceEffects: not null }, Audio: not null }
        && tracks.TrueForAll(t => t is { Clips: { } clips } && clips.TrueForAll(c => c is { State: not null }))
        && (step.Bin ?? []).TrueForAll(d => d is not { Clip: { State: null } });

    /// <summary>
    /// The timeline as it is now, the views' looks written into the document first. The document is a copy of its own (its
    /// tracks and clips; the looks shared, which no edit changes in place): an edit's step is packed only once the edit is
    /// committed (OnDocumentCommitted), when the document itself has changed.
    /// </summary>
    private TimelineSnapshot CaptureSnapshot()
    {
        FlushLooks();
        return new(
            Document.Clone(),
            AudioTracks.Select(t => new AudioOutputEdit(t.Index, t.Action, t.Title)).ToList(),
            _backgroundRow.IsHidden, RecycleBin.Select(i => i.Clip).ToList(),
            CaptureSubtitles(), CaptionLayer.ToState(), CaptionStyle.Clone(), CaptureLook());
    }

    /// <summary>
    /// Puts an undo step on the stack that brings back the state given, which may be other than the present one: an edit
    /// of the document keeps the timeline as it was before it (MainViewModel.Document.cs, OnDocumentCommitted).
    /// </summary>
    private void Checkpoint(string what, TimelineSnapshot before)
    {
        // Never while a step is being put back (4.4-BugHunt): the restore moves the histories itself.
        if (_isBackgroundWorker || _restoring > 0)
            return;

        PushStep(what, Pack(before));
        _touchedSinceSaved = true;
    }

    /// <summary>
    /// A new step on Undo's history (Pass 5.5A: the one place a step is taken): Redo's history let go, the oldest step past twenty
    /// dropped, and the count of steps before the last save or open kept true (<see cref="_savedDepth"/>): a step taken after undoing
    /// past the save starts a new branch, the saved state left on Redo's side gone with it.
    /// </summary>
    private void PushStep(string what, byte[] state)
    {
        _savedDepth = Math.Min(_savedDepth, _undo.Count);
        _undo.Add((what, state));
        if (_undo.Count > UndoDepth)
        {
            _undo.RemoveAt(0);
            _savedDepth = Math.Max(0, _savedDepth - 1);
        }

        _redo.Clear();
    }

    /// <summary>
    /// Call before changing what is not an edit of the timeline document (a clip's look, its keyframes and effects, the
    /// subtitles, the caption box, the output audio tracks): what it all looks like now can then be brought back with
    /// Undo. (An edit of the document keeps its own step.)
    /// </summary>
    /// <param name="what">The change about to be made, in a word or two, for the status bar.</param>
    public void Checkpoint(string what)
    {
        // Never while a step is being put back (4.4-BugHunt): what asks then is the restore itself, or something it set going.
        if (_isBackgroundWorker || _restoring > 0)
        {
            if (_restoring > 0)
                AppLog.Write($"Undo step \"{what}\" not taken while a step was being put back");
            return;
        }

        PushStep(what, Pack(CaptureSnapshot()));
        _checkpointedAt = Environment.TickCount64;
        _stepGesture = _inputGesture;
        _touchedSinceSaved = true;
    }

    /// <summary>
    /// After a dialog whose visit was one step (<see cref="Checkpoint(string)"/> as it opened, <see cref="UndoMark"/> just
    /// after): that step goes again when nothing was changed in it (4.3E), so Ctrl+Z never seems to do nothing.
    /// </summary>
    public void DropStepIfUnchanged(object? mark)
    {
        if (_restoring == 0 && mark is byte[] packed && _undo.Count > 0 && ReferenceEquals(_undo[^1].State, packed) && Pack(CaptureSnapshot()).AsSpan().SequenceEqual(packed))
        {
            _undo.RemoveAt(_undo.Count - 1);
            _savedDepth = Math.Min(_savedDepth, _undo.Count);
        }
    }

    public void Undo() => Step(_undo, _redo, "Undid");

    public void Redo() => Step(_redo, _undo, "Redid");

    /// <summary>
    /// Puts back the newest step of one history (Undo's, or Redo's), and keeps the timeline as it is now on the other. Since
    /// 4.4-BugHunt the histories move only once the step has been read and put in place: one that cannot be read, or put
    /// back, is let go and said (the timeline stays, or goes back to, what it was), the histories otherwise as they were.
    /// Refused while a change is under way: an edit open, another step being put back.
    /// </summary>
    private void Step(List<(string What, byte[] State)> from, List<(string What, byte[] State)> to, string verb)
    {
        var (undoing, name) = ReferenceEquals(from, _undo) ? (true, "Undo") : (false, "Redo");
        var category = undoing ? "undo" : "redo";
        if (_restoring > 0 || _openEdit is not null || _committing > 0 || Document.OpenTransactions.Count > 0)
        {
            StatusText = $"{name} waits until the change under way is finished.";
            AppLog.Write($"{name} refused: {(_restoring > 0 ? "a step was still being put back" : "an edit was still open")}");
            return;
        }

        if (from.Count == 0)
        {
            StatusText = undoing ? "Nothing to undo." : "Nothing to redo.";
            return;
        }

        var (what, state) = from[^1];
        if (ReadStep(state, what) is not { } target)
        {
            from.RemoveAt(from.Count - 1);
            StatusText = $"The step \"{what}\" could not be read, and was let go: {name} again goes on from the one before it.";
            CrashReporter.Note(category, "a step that could not be read was let go");
            return;
        }

        CrashReporter.Note(category, NoteOf(what));

        // A Project Settings step (5.4A) keeps the frame as it is now on the other history too, so the step back puts it back.
        var now = Pack(target.Frame is null ? CaptureSnapshot() : CaptureSnapshot() with { Frame = CaptureProjectFrame() });
        using (BeginRestore())
        {
            try
            {
                RestoreTimeline(target);
            }
            catch (Exception ex)
            {
                // Let go, and the timeline put back as it was (the step just taken of it): the histories as they were, less it.
                NoteFault(category, $"{name} of \"{what}\"", $"{category} of {NoteOf(what)}", ex);
                RemoveStep(from, state);
                try
                {
                    if (ReadStep(now, what) is { } was)
                        RestoreTimeline(was);
                }
                catch (Exception again)
                {
                    NoteFault(category, $"Putting the timeline back after a failed {name.ToLowerInvariant()}", "going back after a failed step", again);
                }

                StatusText = $"{name} could not put back \"{what}\", and let that step go: the timeline is as it was (the reason is in the log).";
                return;
            }

            RemoveStep(from, state);
            to.Add((what, now));
            if (to.Count > UndoDepth)
            {
                to.RemoveAt(0);

                // The oldest step of Undo's history dropped (Pass 5.5A): one fewer of those before the last save or open.
                if (ReferenceEquals(to, _undo))
                    _savedDepth = Math.Max(0, _savedDepth - 1);
            }
        }

        Log($"{verb}: {what}");
    }

    /// <summary>Takes a step out of a history: the newest of that state (stepping takes the newest; nothing else moves a history meanwhile).</summary>
    private static void RemoveStep(List<(string What, byte[] State)> steps, byte[] state)
    {
        var at = steps.FindLastIndex(s => ReferenceEquals(s.State, state));
        if (at >= 0)
            steps.RemoveAt(at);
    }

    private void RestoreTimeline(TimelineSnapshot? state)
    {
        if (state is not { } snapshot)
            return;

        // A document being put in place (4.4-BugHunt): nothing it sets going takes a step or makes an edit, and the captions
        // are projected once, as it ends (MainViewModel.Undo.cs).
        using var restoring = BeginRestore();

        // One rebuild of the command for the whole step, not one for every clip that is put back.
        using var whole = DeferCommand();

        // A Project Settings step's frame (5.4A): turned before the clips' views are made again, so they are made for the frame
        // they were kept on, and its boxes set again once the document's natural size is back (MainViewModel.ProjectSettings.cs).
        if (snapshot.Frame is { } frame)
            RestoreProjectFrame(frame);
        InstallDocument(snapshot.Document);
        if (snapshot.Frame is { } boxes)
            RestoreProjectFrame(boxes);
        SetBin(snapshot.Bin);
        _backgroundRow.IsHidden = snapshot.BackgroundHidden;
        foreach (var output in snapshot.Audio)
        {
            if (AudioTracks.FirstOrDefault(t => t.Index == output.Index) is not { } track)
                continue;

            // How it is made, each only where it differs.
            if (output.Action is { } action && AudioTrack.AllActions.Contains(action))
                track.Action = action;
            if (output.Title is { Length: > 0 } title)
                track.Title = title;
        }

        // The sequence's look (4.4), only where it differs: a step that did not touch it leaves the graph's filters as they are.
        // (Compared as JSON: it holds the blur fill's effects, a list, since 5.0-Release.)
        if (snapshot.Look is { } look && !SameJson(look, CaptureLook()))
            ApplyLook(look);

        // A change made right after this is a step of its own, never the end of a burst from before it.
        _lastLookEdit = null;
        _lastFieldEdit = null;

        // The subtitles, the caption box and its style, each only where it differs: a step that did not touch
        // them leaves the caption preview as it is. (What anchored captions show is not the subtitles' own: it is made
        // again from their anchors through the timeline put back, once, as the restore ends. 4.4-BugHunt.)
        if (!SameJson(Unprojected(snapshot.Subtitles), Unprojected(CaptureSubtitles())))
        {
            ApplySubtitles(snapshot.Subtitles);
            RefreshSubtitlePreview();
        }

        if (snapshot.CaptionLayer is { } caption && !SameJson(caption, CaptionLayer.ToState()))
            SetCaptionLayer(caption);
        if (snapshot.CaptionStyle is { } style && !SameJson(style, CaptionStyle))
            SetCaptionStyle(style.Clone());

        RefreshLayerRows();
        GenerateCommand();
        ApplyKeyframes();
    }

    /// <summary>
    /// A subtitle track without the cues that only show its anchored captions (as ApplySubtitles leaves them out, to make them
    /// again from the anchors): what tells whether a step changed the subtitles themselves, or only where the timeline shows them.
    /// </summary>
    private static SubtitleTrackData? Unprojected(SubtitleTrackData? data) =>
        data is { IsImage: false, Source.IsAnchored: true, Anchors.Count: > 0 }
            ? new SubtitleTrackData
            {
                SourcePath = data.SourcePath, IsImage = data.IsImage, OffsetMs = data.OffsetMs, Cues = [.. (data.Cues ?? []).Where(c => c is { Projected: false })],
                Source = data.Source, Anchors = data.Anchors,
            }
            : data;

    // ----- The history a project carries (4.4-BugHunt) -----
    // A save writes the undo and redo history with the project (its steps as Undo keeps them), so that Ctrl+Z goes on where
    // the save left it once the project is opened again. It is never part of what tells whether the project has changed (the
    // fingerprint is CaptureState's, which has none), and a queued job carries none.

    /// <summary>Lets the whole history go (a new source, a new project): nothing to undo or redo, nothing of it carried on.</summary>
    private void ForgetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _savedDepth = 0;
        (_lastLookEdit, _lastFieldEdit, _visitStep) = (null, null, null);
    }

    /// <summary>
    /// The history a project carries, taken up as it is opened in place of none: each step is read only when it is stepped
    /// to (one that cannot be is let go then). When the file opened is another than the project was saved with (a link's
    /// download that moved), the steps are pointed at this one as the timeline is (TimelineToOpen). A project converted as it was opened
    /// (a 5.1 project, LegacyMainVideo) keeps none: its steps hold the old main video and its stream mixing, which is not kept. Takes no
    /// step, and changes nothing that counts as a change of the project.
    /// </summary>
    private void TakeUpHistory(ProjectState state, bool converted)
    {
        ForgetHistory();
        if (converted)
        {
            if ((state.UndoHistory?.Count ?? 0) + (state.RedoHistory?.Count ?? 0) > 0)
                AppLog.Write("The project's undo history was not taken up: it was saved by a HandPeg from before 5.2, with a main video (the project was converted)");
            return;
        }

        foreach (var step in (state.UndoHistory ?? []).Where(s => s is { State.Length: > 0 }).TakeLast(UndoDepth))
            _undo.Add((step.What, step.State));
        foreach (var step in (state.RedoHistory ?? []).Where(s => s is { State.Length: > 0 }).TakeLast(UndoDepth))
            _redo.Add((step.What, step.State));
        if (_undo.Count + _redo.Count == 0)
            return;

        if (state.LocalMediaPath is { Length: > 0 } old && !string.Equals(old, LocalMediaPath, StringComparison.OrdinalIgnoreCase))
        {
            var now = LocalMediaPath;
            string Moved(string path) => string.Equals(path, old, StringComparison.OrdinalIgnoreCase) ? now : path;
            foreach (var history in new[] { _undo, _redo })
            {
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    if (MovedStep(history[i].State, history[i].What, Moved) is { } moved)
                        history[i] = (history[i].What, moved);
                    else
                        history.RemoveAt(i);
                }
            }
        }

        AppLog.Write($"The project's undo history was taken up: {_undo.Count} step(s) to undo, {_redo.Count} to redo");
    }

    /// <summary>
    /// The project as a save writes it: <see cref="CaptureState"/>, with the undo and redo history (4.4-BugHunt). With
    /// <paramref name="moved"/> (downloads kept in a folder of the user's), the steps are written with their files moved as
    /// the timeline's are, while the history this session goes on with is left as it is. With its thumbnail (Pass
    /// 5.2D): the Sequence at the playhead as the GPU engine draws it, or the last one taken
    /// (<see cref="ThumbnailForSave"/>).
    /// </summary>
    public ProjectState CaptureStateForSave(string name = "", Func<string, string>? moved = null)
    {
        var state = CaptureState(name);
        (state.UndoHistory, state.RedoHistory) = (HistoryOf(_undo, moved), HistoryOf(_redo, moved));
        state.ThumbnailJpeg = ThumbnailForSave();
        state.Workspace = _isBackgroundWorker ? null : WorkspaceForSave?.Invoke();
        return state;
    }

    /// <summary>The window's arrangement for a save's "workspace" section (Pass 5.4.1-M5); set by the window, unset for a background worker.</summary>
    public Func<ProjectWorkspace?>? WorkspaceForSave { get; set; }

    /// <summary>Raised after a project carrying a "workspace" section is opened, for the window to take it up (when the user wants that).</summary>
    public event Action<ProjectWorkspace>? WorkspaceOpened;

    /// <summary>A history as a project carries it, oldest first (its steps shared: a packed step is never changed); null when empty.</summary>
    private List<UndoStepState>? HistoryOf(List<(string What, byte[] State)> steps, Func<string, string>? moved)
    {
        if (_isBackgroundWorker || steps.Count == 0)
            return null;

        var history = new List<UndoStepState>(steps.Count);
        foreach (var (what, state) in steps)
        {
            if ((moved is null ? state : MovedStep(state, what, moved)) is { } kept)
                history.Add(new UndoStepState(what, kept));
        }

        return history.Count > 0 ? history : null;
    }

    /// <summary>A step with its files moved (<see cref="MoveFiles"/>): packed anew when any moved, itself when none did; null when it cannot be read.</summary>
    private byte[]? MovedStep(byte[] state, string what, Func<string, string> moved) =>
        ReadStep(state, what) is not { } step ? null : MoveFiles(step, moved) ? Pack(step) : state;

    /// <summary>
    /// Points a step read back at files that have moved: its clips' (on the timeline and in the bin, with what went with
    /// them; their looks' too), Project Media's (5.4C) and its captions' source. The step is a copy of its own, just read, so it is
    /// changed in place.
    /// Whether anything moved.
    /// </summary>
    private static bool MoveFiles(TimelineSnapshot step, Func<string, string> moved)
    {
        var changed = false;
        string Move(string path)
        {
            var to = moved(path);
            changed |= !string.Equals(to, path, StringComparison.Ordinal);
            return to;
        }

        static IEnumerable<TimelineClip> Kept(DeletedClip? deleted) =>
            deleted is { Clip: { } clip } ? (deleted.Children ?? []).SelectMany(Kept).Prepend(clip) : Enumerable.Empty<TimelineClip>();

        foreach (var clip in step.Document.Tracks.SelectMany(t => t.Clips).Concat((step.Bin ?? []).SelectMany(Kept)))
        {
            if (clip.SourcePath is { Length: > 0 } path)
                clip.SourcePath = Move(path);
            if (clip.State is { ImagePath.Length: > 0 } look)
                look.ImagePath = Move(look.ImagePath);
        }

        var pool = step.Document.MediaPool;
        for (var i = 0; i < pool.Count; i++)
            pool[i] = Move(pool[i]);

        if (step.Subtitles is { Source: { Path.Length: > 0 } source } subtitles)
            subtitles.Source = source with { Path = Move(source.Path) };
        return changed;
    }

    // ----- Whatever is selected -----

    /// <summary>Removes the selected clip: it goes to the recycle bin, its time left empty. Any clip, on any track.</summary>
    public bool DeleteClip(object? clip)
    {
        if (clip is Layer layer && ClipOf(layer) is not null)
        {
            var name = layer.Name;
            if (!MoveToBin(layer))
                return false;

            Log($"Deleted clip: {name} (in the recycle bin; Ctrl+Z brings it back)");
            return true;
        }

        StatusText = "Select a clip to delete: click its block on the timeline.";
        return false;
    }

    /// <summary>
    /// Sets how fast a clip runs. The clip plays the same stretch of its file as before, so its block on the
    /// timeline is as much shorter or longer as it is faster or slower (what it grows into on its track moves on).
    /// </summary>
    public void SetClipSpeed(Layer clip, double speed)
    {
        speed = Math.Round(Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed), 3);

        // (A box's echo while a step is being put back, or its bindings settle, is no request: 4.4-BugHunt.)
        if (ClipOf(clip) is not { PlaysFile: true } model || Math.Abs(model.Speed - speed) < 0.0005 || IsRestoringTimeline)
        {
            // A box bound to the speed shows what it is.
            OnViewSpeedRefused(clip);
            return;
        }

        // Its streams' lanes and the Cutouts cut from it are its own file at the same moment: they run at its speed, or they drift off it.
        var family = Document.Tracks.SelectMany(t => t.Clips).Where(c => c.ParentClipId == model.Id && c.PlaysFile && string.Equals(c.SourcePath, model.SourcePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Edit($"change the speed of {clip.Name}", edit =>
            {
                edit.SetClipSpeed(model, speed);
                foreach (var child in family)
                    edit.SetClipSpeed(child, speed);
            }, undoable: TakesUndoStep(clip, nameof(Layer.Speed))))
            Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Set speed of {clip.Name} to {model.Speed:0.##}x: it now runs {TimeDisplay.Format(model.Start)} to {TimeDisplay.Format(model.End)}"));
        else
            OnViewSpeedRefused(clip);
    }

    /// <summary>A view whose speed box asked for what could not be: the box shows the clip's speed again.</summary>
    private void OnViewSpeedRefused(Layer view)
    {
        if (ClipOf(view) is { } clip && Document.TrackOf(clip) is { } track)
        {
            _syncingViews = true;
            try
            {
                TakeClip(view, clip, track);
            }
            finally
            {
                _syncingViews = false;
            }
        }

        view.NotifySpeed();
    }

    /// <summary>Trims the selected clip to the playhead: its start (everything before goes) or its end (everything after).</summary>
    public bool TrimClip(object? clip, bool start, double seconds)
    {
        if (clip is Layer layer && ClipOf(layer) is { } model && seconds > model.Start + TimelineDocument.MinimumClipSeconds && seconds < model.End - TimelineDocument.MinimumClipSeconds)
        {
            var moved = 0.0;
            if (!Edit($"trim {layer.Name}", edit => moved = start ? edit.TrimClipStart(model, seconds) : edit.TrimClipEnd(model, seconds)) || Math.Abs(moved) < 1e-6)
                return false;

            Log($"Trimmed clip: {layer.Name} now {(start ? "starts" : "ends")} at {TimeDisplay.Format(start ? model.Start : model.End)}");
            return true;
        }

        StatusText = "Select a clip, and put the playhead inside it, to trim it there.";
        return false;
    }

    /// <summary>Copies a clip's whole track (every clip on it) onto a new track just above, moved a little on the frame.</summary>
    public void DuplicateLayer(Layer layer)
    {
        if (TrackOfRow(layer) is not { } track || track.Clips.Count == 0)
            return;

        FlushLooks();
        Edit($"duplicate {layer.Name}", edit =>
        {
            var copy = edit.InsertTrack(Document.Tracks.IndexOf(track) + 1, track.Kind == TimelineTrackKind.Switch ? TimelineTrackKind.Video : track.Kind);
            foreach (var clip in track.Clips.ToList())
            {
                var look = TimelineTransaction.CopyOf(clip.State);
                (look.Name, look.PositionX, look.PositionY) = (look.Name + " copy", look.PositionX + 0.03, look.PositionY + 0.03);
                var made = CopyOfClip(clip);
                (made.Id, made.State) = (Guid.NewGuid(), look);

                edit.PlaceClip(copy, made, allowCrossfadeOverlap: false);
            }
        });

        Log($"Duplicated clip: {layer.Name}");
    }

    /// <summary>Removes a track: every clip on it goes to the recycle bin, and the track goes.</summary>
    public void RemoveTrack(Layer layer)
    {
        if (TrackOfRow(layer) is not { } track)
            return;

        var binned = track.Clips.Select(c => (Deleted: Binned(c, track, ViewOf(c)), Name: ViewOf(c)?.Name ?? c.State.Name)).ToList();
        if (!Edit($"remove {layer.Name}", edit => edit.RemoveTrack(track)))
            return;

        foreach (var (deleted, name) in binned)
            AddToBin(deleted.Clip, name, deleted.Preview);
        StatusText = binned.Count > 0
            ? $"Removed {layer.Name}. Its clips are in the recycle bin (the trash can), and Ctrl+Z brings them back."
            : $"Removed {layer.Name}.";
    }

    /// <summary>Raised when a track has been hidden or shown: the layout pane draws its boxes again.</summary>
    public event Action? LayerVisibilityChanged;

    /// <summary>
    /// Hides a row, or shows it again: the blur fill of every video (the blur fill's settings row, 5.2-AuditB); a picture track
    /// (its own switch, its stack with it); a sound track's clips.
    /// </summary>
    public void ToggleHidden(Layer layer)
    {
        // A picture track's eye is its own switch (5.2), not its first clip's.
        var hide = !layer.IsAudio && !layer.IsBackground && !layer.IsCaptions && TrackOfRow(layer) is not null ? !IsTrackHidden(layer) : !layer.IsHidden;
        if (layer.IsBackground)
        {
            SetBackgroundShown(!hide);
            RefreshLayerRows();
            LayerVisibilityChanged?.Invoke();
            return;
        }

        if (layer.IsCaptions || TrackOfRow(layer) is not { } track)
        {
            Checkpoint(hide ? $"hide {layer.Name}" : $"show {layer.Name}");
            layer.IsHidden = hide;
            GenerateCommand();
        }
        else if (!Edit(hide ? $"hide {layer.Name}" : $"show {layer.Name}", edit =>
                 {
                     // A picture track's eye is its whole family's (5.2, the user's rule): the track's own switch hides its clips and
                     // the tracks in its stack with theirs (TimelineDocument.IsHiddenWithParents), and never touches a clip's own switch.
                     // A track hidden clip by clip before 5.2 is shown again whole. A sound track's is still each of its clips' sound.
                     if (!layer.IsAudio)
                     {
                         var byClips = !track.IsHidden;
                         edit.SetTrack(track, hidden: hide);
                         if (!hide && byClips && track.Clips.Count > 0 && track.Clips.All(c => c.IsHidden))
                         {
                             foreach (var clip in track.Clips)
                                 edit.SetClipFlags(clip, hidden: false);
                         }
                     }
                     else
                     {
                         foreach (var clip in track.Clips)
                             edit.SetClipFlags(clip, hidden: hide);
                     }
                 }))
        {
            return;
        }

        RefreshLayerRows();
        LayerVisibilityChanged?.Invoke();
        StatusText = !hide ? $"{layer.Name} is shown again."
            : $"{layer.Name} is hidden: it stays in the list but is left out of the picture.";
    }

    /// <summary>
    /// Whether a picture track's header shows it hidden (5.2): by its own switch, or every clip of it turned off (as a header hid a
    /// track before 5.2, clip by clip), which its eye then shows again whole.
    /// </summary>
    public bool IsTrackHidden(Layer row) => TrackOfRow(row) is { } track
        ? track.IsHidden || (track.Clips.Count > 0 && track.Clips.All(c => c.IsHidden))
        : row.IsHidden;

    /// <summary>Has auto-captions listen to a video clip's own sound.</summary>
    public void UseLayerAudioForCaptions(Layer layer)
    {
        if (!layer.CarriesSound)
        {
            StatusText = $"{layer.Name} has no sound to make captions from.";
            return;
        }

        // Followed through its clips (4.4): its words are shown where each clip of its file plays them, at its speed.
        if (ResolveCaptionSource(layer) is { } source)
            ChooseCaptionSource(source);
        StatusText = $"Auto-captions now listen to {layer.Name}.";
    }

    // ----- Audio on the timeline -----

    /// <summary>
    /// Whether the audio follows the picture. Linked, an audio track cannot be moved by itself, and a video
    /// layer's sound starts where the layer does. Unlinked, each can be slipped on its own.
    /// </summary>
    [ObservableProperty] private bool _audioLinked = true;

    /// <summary>Whether the clips' blocks on the timeline show the waveform of the sound that is linked to their picture, drawn over them.</summary>
    [ObservableProperty] private bool _showLinkedAudio = true;

    /// <summary>
    /// Links or unlinks audio and video. Linking again after something was slipped asks first: linked means
    /// in step, so the audio that was moved has to come back to where its picture is.
    /// </summary>
    public void SetAudioLinked(bool linked)
    {
        if (linked == AudioLinked)
            return;

        // Every clip with sound has its own link (the document's); this switch sets them all.
        var clips = Document.Tracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c))).Where(c => c.Clip.HasSound).ToList();
        var slipped = clips.Any(c => Math.Abs(c.Clip.AudioSlipSeconds) > 0.001);
        var choice = 1;
        if (linked && slipped)
        {
            // Which of the two gives way: the sound goes back to its picture, or the picture goes to its sound.
            choice = Choose?.Invoke("Link Audio and Video",
                "The audio has been moved out of step with its video. Linking puts them back together, one way or the other:\n\n"
                + "Snap Audio to Video: the sound goes back to where its picture is.\n"
                + "Snap Video to Audio: each clip moves to where its sound was put.",
                "Snap Audio to Video", "Snap Video to Audio") ?? 0;
            if (choice == 0)
            {
                OnPropertyChanged(nameof(AudioLinked));
                StatusText = "Left unlinked: the audio stays where it was moved to.";
                return;
            }
        }

        // The clips' links are one edit.
        if (!Edit(linked ? "link audio and video" : "unlink audio and video", edit =>
            {
                foreach (var (track, clip) in clips)
                {
                    var slip = clip.AudioSlipSeconds;
                    edit.SetAudioLink(clip, linked, linked ? 0 : slip);
                    if (linked && choice == 2 && Math.Abs(slip) > 0.001 && !track.IsLocked)
                    {
                        try
                        {
                            edit.MoveClip(clip, track, Math.Max(clip.Start + slip, 0));
                        }
                        catch (TimelineEditException)
                        {
                            // No room where its sound was: it stays, its sound brought back to it.
                        }
                    }
                }
            }))
        {
            OnPropertyChanged(nameof(AudioLinked));
            return;
        }

        AudioLinked = linked;
        GenerateCommand();
        StatusText = linked
            ? "Audio and video are linked: they move and are cut together."
            : "Audio and video are unlinked: an audio block can now be dragged on its own.";
    }


    /// <summary>The video clips whose own sound goes into the output: those that have any, and are not hidden.</summary>
    private List<Layer> GetLayerAudioSources() =>
        Layers.Where(l => l.CarriesSound && !l.IsHidden && !l.IsSoundMuted && FileState.Exists(l.ImagePath)).ToList();

    /// <summary>Draws the waveform of a video clip's sound, for its block on the timeline. In the background; the clip works without it.</summary>
    private async Task LoadLayerWaveformAsync(Layer layer)
    {
        if (_isBackgroundWorker || !layer.CarriesSound || layer.Waveform is not null || !(ShowWaveforms || layer.IsAudio))
            return;

        // The picture of a file is made once in a session, however often its clips are split, undone and
        // put back: every one of those makes new clips, and none of them a new picture.
        // (A stream's lane shows that stream of its file.)
        var path = layer.ImagePath;
        var stream = Math.Max(ClipOf(layer)?.AudioStreamIndex ?? 0, 0);
        var key = stream == 0 ? path : $"{path}|a{stream}";
        if (!_layerWaveforms.TryGetValue(key, out var drawing))
        {
            _layerWaveforms[key] = drawing = Waveforms.RenderAsync(
                path, [stream], Path.Combine(WaveformFolder, $"layer_{Guid.NewGuid():N}.png"), 1200, 60, "white", PictureToken(path));
        }

        try
        {
            layer.Waveform = await drawing;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not kept: the next clip of this file may have better luck.
            _layerWaveforms.Remove(key);
        }
    }

    // The background work that draws layers' pictures, by file: all of it hangs off one source (the session's),
    // and each file's can be stopped on its own. It is, the moment no clip on the timeline uses the file any
    // more: FFmpeg is not left decoding a long file for a clip that was dropped and taken off again.
    private readonly Dictionary<string, CancellationTokenSource> _pictureWork = new(StringComparer.OrdinalIgnoreCase);

    private CancellationToken PictureToken(string path)
    {
        if (!_pictureWork.TryGetValue(path, out var work))
            _pictureWork[path] = work = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        return work.Token;
    }

    /// <summary>Stops what is still being drawn of a file that no clip uses any more.</summary>
    private void CancelStalePictureWork(string path)
    {
        if (path.Length == 0 || !_pictureWork.TryGetValue(path, out var work) || Layers.Any(l => string.Equals(l.ImagePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        _pictureWork.Remove(path);
        if (_layerWaveforms.TryGetValue(path, out var drawing) && !drawing.IsCompleted)
            _layerWaveforms.Remove(path);
        work.Cancel();
    }

    // The pictures made of layers' files this session, by file: waveforms, and strips of frames. No more
    // than this many of each are kept: the file used longest ago gives way to a new one. A clip that is on
    // the timeline holds its own pictures whatever happens here, so nothing that is showing goes blank; what
    // is bounded is how much is remembered of files that are no longer in use. A strip is at most 25 frames
    // 54 pixels high (about half a megabyte), a waveform 1200 by 60.
    private const int PicturesKept = 24;

    // How tall a block's strip is made (FfmpegRunner.GenerateFilmstripAsync's height): the Media Browser's strip is decoded at it.
    private const int LayerStripHeight = 54;

    private readonly LruCache<string, Task<System.Windows.Media.ImageSource?>> _layerWaveforms = new(PicturesKept, StringComparer.OrdinalIgnoreCase);
    private readonly LruCache<string, System.Windows.Media.ImageSource> _layerFilmstrips = new(PicturesKept, StringComparer.OrdinalIgnoreCase);

    /// <summary>The pictures a layer's block is drawn with: the waveform of its sound, and for a video a strip of its frames.</summary>
    private async Task LoadLayerPicturesAsync(Layer layer)
    {
        // Side by side: the frames do not wait for the waveform to be drawn.
        await Task.WhenAll(LoadLayerWaveformAsync(layer), LoadLayerFilmstripAsync(layer));
    }

    /// <summary>
    /// Takes a strip of frames from a video clip's file, evenly spread over its length, for its block on the
    /// timeline. In the background, once for each file; the clip works without it.
    /// </summary>
    private async Task LoadLayerFilmstripAsync(Layer layer)
    {
        if (_isBackgroundWorker || !layer.IsVideoFile || layer.Filmstrip is not null || !File.Exists(layer.ImagePath))
            return;

        try
        {
            if (layer.MediaDuration <= 0 && await MediaProbe.ProbeAsync(layer.ImagePath, _shutdown.Token) is { DurationSeconds: > 0 } info)
                layer.MediaDuration = info.DurationSeconds;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Its length stays unknown: one frame is taken, from its start.
        }

        var file = layer.ImagePath;
        if (await GetFilmstripAsync(file, Math.Clamp(AppSettings.Current.LayerThumbnailCount, 1, 25), layer.MediaDuration) is not { } picture)
            return;

        // Every clip of the file, whichever of them asked: a clip that was split while the strip was being
        // made is two clips by now.
        foreach (var clip in Layers.Where(l => l.IsVideoFile && l.Filmstrip is null && string.Equals(l.ImagePath, file, StringComparison.OrdinalIgnoreCase)).ToList())
            clip.Filmstrip = picture;
        if (layer.Filmstrip is null)
            layer.Filmstrip = picture;
    }


    // The strips that are being made right now, by file: a second clip of a file waits for the first one's.
    private readonly Dictionary<string, Task<System.Windows.Media.ImageSource?>> _filmstripsBeingMade = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The strip of frames of a file: the one made earlier this session, the one being made, or a new one.</summary>
    private Task<System.Windows.Media.ImageSource?> GetFilmstripAsync(string file, int count, double seconds)
    {
        if (_layerFilmstrips.TryGetValue(file, out var made))
            return Task.FromResult<System.Windows.Media.ImageSource?>(made);
        if (!_filmstripsBeingMade.TryGetValue(file, out var making))
            _filmstripsBeingMade[file] = making = MakeFilmstripAsync(file, count, seconds);
        return making;
    }

    private async Task<System.Windows.Media.ImageSource?> MakeFilmstripAsync(string file, int count, double seconds)
    {
        // Let the caller go on first, so that this is in the list of strips being made before it can leave it.
        await Task.Yield();
        try
        {
            var path = Path.Combine(SpriteFolder, $"layer_{Guid.NewGuid():N}.jpg");
            var token = _shutdown.Token;
            var kind = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"strip{count}");

            // Both the extraction and the reading of the picture happen away from the interface. The picture
            // is frozen there, before it is handed over: a bitmap made on one thread cannot be shown by
            // another until it is, and a frozen one is also the only kind WPF does not keep watching.
            var picture = await Task.Run(async () =>
            {
                // Made before, for this file as it is: taken from the cache; else the Media Browser's strip of it (5.4.1-M3: a file dropped
                // from the browser has its frames at once), decoded at a block's height.
                if (PreviewCache.Timeline.Find(file, kind, ".jpg") is { } kept)
                    path = kept;
                else if (MediaBrowserFrames.KeptStripAt(file, LayerStripHeight) is { } shared)
                    return (System.Windows.Media.ImageSource)shared;
                else if (await FfmpegRunner.GenerateFilmstripAsync(file, path, count, seconds, token))
                    path = PreviewCache.Timeline.Keep(file, kind, ".jpg", path);
                else
                    return null;

                // Read into memory, so that the file is not held open.
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return (System.Windows.Media.ImageSource)bitmap;
            }, token);

            if (picture is not null)
                _layerFilmstrips[file] = picture;
            return picture;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or UriFormatException or System.ComponentModel.Win32Exception)
        {
            if (ex is not OperationCanceledException)
                AppLog.Write($"Thumbnails of {Path.GetFileName(file)} could not be read: {ex.Message}");
            return null;
        }
        finally
        {
            // Made or not, it is no longer being made: a file that failed is tried again when next asked for.
            _filmstripsBeingMade.Remove(file);
        }
    }

    // ----- Dead air -----

    private DeadAirMode _deadAirMode;
    private object? _deadAirTarget;
    private DeadAirSource _deadAirSource;

    /// <summary>
    /// Remove Dead Air on what is selected: a clip with sound of its own (it is listened to and cut up), or otherwise the sound
    /// marked as the voice, else the first stream of the video given or at the playhead, every clip of its file cut. The dead air
    /// is found by the sound's silences, or by the gaps between the words its captions heard (4.4).
    /// </summary>
    public void RunDeadAir(DeadAirMode mode, object? target, DeadAirSource source = DeadAirSource.Audio)
    {
        (_deadAirMode, _deadAirTarget, _deadAirSource) = (mode, target, source);
        if (RemoveDeadAirCommand.CanExecute(null))
            RemoveDeadAirCommand.Execute(null);
    }

    /// <summary>
    /// Cuts clips at the silences in their sound (found in their file's own time): each is split where a silence it plays
    /// begins and ends, and the silent parts are then folded (<paramref name="fold"/>, 4.3D: taken out, what follows closed
    /// up, each kept at its cut to be put back, <see cref="UnfoldSegment"/>) or taken out. With <paramref name="closeUp"/>
    /// (the dead air of a file's clips) they go from every unlocked track and what follows on all of them closes up behind them,
    /// as a cut list's left-out stretches did; otherwise (a layer's) a fold closes up its own track, and a silence taken out
    /// leaves its time empty, as in 4.2.0. One step of Undo for them all.
    /// </summary>
    /// <param name="layerLimit">
    /// How many cuts a layer may take: a few dozen silences keep a layer usable, while the gaps between captioned words
    /// (4.4) are many more by nature.
    /// </param>
    private string ApplyDeadAir(IReadOnlyList<Layer> views, List<(double Start, double End)> silences, bool fold, bool closeUp, int layerLimit = 60)
    {
        var clips = views.Select(v => (View: v, Clip: ClipOf(v))).Where(c => c.Clip is { PlaysFile: true }).ToList();
        if (clips.Count == 0)
            return "Nothing to cut: select a clip that plays a file.";

        // Each clip's silent stretches on the sequence: where in its file it is, at its speed.
        var cuts = new List<(TimelineClip Clip, double From, double To)>();
        foreach (var (_, clip) in clips)
        {
            var (start, end, offset, speed) = (clip!.Start, clip.End, clip.MediaOffset, clip.Speed);
            foreach (var (from, to) in silences.OrderBy(s => s.Start))
            {
                var (a, b) = (Math.Max(start + (from - offset) / speed, start), Math.Min(start + (to - offset) / speed, end));
                if (b - a >= TimelineDocument.MinimumClipSeconds)
                    cuts.Add((clip, a, b));
            }
        }

        var name = clips.Count == 1 ? clips[0].View.Name : $"the clips of {Path.GetFileName(clips[0].View.ImagePath)}";
        if (cuts.Count == 0)
            return $"No silence found in {name}.";
        // A layer is cut into clips of its own, which a few dozen keep usable; a file's silences closed up across the timeline are
        // taken out of it (4.2.0's cut list had no limit), however many a long recording has.
        if (cuts.Count > (closeUp ? 5000 : layerLimit))
            return $"{(clips.Count == 1 ? $"{clips[0].View.Name} has" : $"The clips of {Path.GetFileName(clips[0].View.ImagePath)} have")} too many silences to cut ({cuts.Count}): raise the shortest silence and try again.";

        var removed = 0.0;
        var marked = 0;
        Edit($"remove dead air from {name}", edit =>
        {
            // Latest first: what is cut out (and closed up behind) never moves a silence still to be cut.
            foreach (var (clip, from, to) in cuts.OrderByDescending(c => c.From))
            {
                if (Document.TrackOf(clip) is null)
                    continue;

                // Folded out of the timeline at its cut, to be put back: what is on the timeline is what renders.
                if (fold)
                {
                    try
                    {
                        if (edit.FoldStretch(Document.TrackOf(clip)!, from, to, rippleAllUnlockedTracks: closeUp) is { } made)
                            (marked, removed) = (marked + 1, removed + made.Length);
                    }
                    catch (TimelineEditException)
                    {
                        // Too near another cut or an edge, or what follows cannot close up: left as it is.
                    }

                    continue;
                }

                // Taken out of every unlocked track, and what follows closed up behind it, as 4.2.0s cut list left it out.
                if (closeUp)
                {
                    marked++;
                    removed += to - from;
                    TakeOutStretch(edit, from, to);
                    continue;
                }

                var silent = clip;
                try
                {
                    if (to < silent.End - TimelineDocument.MinimumClipSeconds)
                        edit.SplitClipAt(silent, to);
                    if (from > silent.Start + TimelineDocument.MinimumClipSeconds)
                        silent = edit.SplitClipAt(silent, from);
                }
                catch (TimelineEditException)
                {
                    // Too near another cut or an edge to be a clip of its own: left as it is.
                    continue;
                }

                marked++;
                removed += silent.Duration;
                edit.DeleteClip(silent);
            }
        });

        return fold
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{marked} silent stretches ({removed:0.#} s) were folded out of {name}: click a seam (or right-click the clip after it and choose Restore Folded Silence) to put one back, and its Hide pill folds it again.")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{marked} silent stretches ({removed:0.#} s) were removed from {name}{(closeUp ? ", and what follows closed up" : "")}.");
    }

    // ----- Folded silences (4.3D) -----

    /// <summary>The stretches folded out at a clip's cuts (its start, or its end where nothing came after), latest seam first.</summary>
    public IReadOnlyList<FoldedSegment> FoldsAt(Layer clip) =>
        ClipOf(clip) is { } model && Document.Folds is { } folds ? [.. folds.Where(f => f.SeamClipId == model.Id)] : [];

    /// <summary>How many stretches are folded out of the timeline at cuts that are still there.</summary>
    public int FoldCount => Document.Folds?.Count(f => f.SeamIn(Document) is not null) ?? 0;

    /// <summary>Puts a folded stretch back at its cut, as one step of Undo: what follows moves on by its length.</summary>
    public void UnfoldSegment(FoldedSegment fold)
    {
        if (Edit($"put back {fold.Length:0.#} s at a cut", edit => edit.UnfoldSegment(fold)))
            Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Put back {fold.Length:0.#} s at the cut at {TimeDisplay.Format(fold.SeamIn(Document) ?? 0)}"));
    }

    /// <summary>Puts every folded stretch back, latest first (so none moves a seam still to be opened), as one step of Undo.</summary>
    public void UnfoldAll()
    {
        var folds = (Document.Folds ?? []).Where(f => f.SeamIn(Document) is not null).OrderByDescending(f => f.SeamIn(Document)).ToList();
        if (folds.Count > 0 && Edit($"put back {folds.Count} folded stretches", edit =>
            {
                foreach (var fold in folds)
                    edit.UnfoldSegment(fold);
            }))
        {
            Log($"Put back {folds.Count} folded stretch{(folds.Count == 1 ? "" : "es")}");
        }
    }

    // ----- Unsaved changes -----

    // What the project looked like when it was last saved or loaded, to tell whether anything has changed since.
    private string _savedFingerprint = "";

    // Whether the user has touched HandPeg (a key, a click, the wheel, a drop) since then, or an edit has taken a step of
    // Undo (one the user set going earlier, finishing in the background). What changes before either is not theirs (a probe,
    // a scan or the player settling after the load) and never counts as an edit (4.3E: a project opened and closed again
    // must never ask to be saved).
    private bool _touchedSinceSaved;

    private string Fingerprint()
    {
        var state = CaptureState();
        state.SavedAt = default;
        return JsonSerializer.Serialize(state);
    }

    /// <summary>Takes the project as it is now as the saved one: called after a load and after a save.</summary>
    public void MarkSaved()
    {
        _savedFingerprint = HasSource ? Fingerprint() : "";
        _touchedSinceSaved = false;

        // Pass 5.5A: every step there is now predates the save (or open): the recovery copy lags only through the steps that follow.
        _savedDepth = _undo.Count;
    }

    /// <summary>
    /// The user's first key, click, wheel turn or drop since the project was loaded or saved (the window hears them in every
    /// one of HandPeg's windows): whatever changed by itself before it is taken as the saved state, so only what follows counts.
    /// </summary>
    public void NoteUserInput()
    {
        // A touch of the user's ends the settling of a step put back at once (4.4-BugHunt): what follows is theirs.
        _settleEndsAt = 0;

        // For Undo (4.4): a look changed a moment after this is the user's change, and takes a step (MainViewModel.Undo.cs).
        _userInputAt = Environment.TickCount64;
        _inputGesture++;
        if (!_undoSourcesHooked)
            HookUndoSources();

        if (_touchedSinceSaved)
            return;

        _touchedSinceSaved = true;
        if (!HasSource || _savedFingerprint.Length == 0 || Fingerprint() is not { } now || now == _savedFingerprint)
            return;

        AppLog.Write($"Changed by itself since the project was opened or saved (not counted as an edit): {ChangedParts(_savedFingerprint, now)}");
        _savedFingerprint = now;
    }

    /// <summary>Whether there is a video open whose cuts or settings the user has changed since it was loaded or last saved as a project.</summary>
    public bool HasUnsavedChanges => HasSource && _touchedSinceSaved && _savedFingerprint.Length > 0 && Fingerprint() != _savedFingerprint;

    /// <summary>For the log, as HandPeg closes with unsaved changes: which parts of the project they are in.</summary>
    public string DescribeUnsavedChanges() => ChangedParts(_savedFingerprint, Fingerprint());

    /// <summary>
    /// Where two fingerprints of the project differ, by the names of the parts (the first few): never their values, so the
    /// log holds no file names or titles.
    /// </summary>
    private static string ChangedParts(string before, string after)
    {
        var parts = new List<string>();
        void Walk(JsonElement a, JsonElement b, string path)
        {
            if (parts.Count >= 8)
                return;

            if (a.ValueKind != b.ValueKind)
            {
                parts.Add(path);
                return;
            }

            switch (a.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var name in a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name)))
                    {
                        if (a.TryGetProperty(name, out var left) && b.TryGetProperty(name, out var right))
                            Walk(left, right, $"{path}.{name}");
                        else if (parts.Count < 8)
                            parts.Add($"{path}.{name}");
                    }

                    break;

                case JsonValueKind.Array when a.GetArrayLength() != b.GetArrayLength():
                    parts.Add($"{path} (count)");
                    break;

                case JsonValueKind.Array:
                    foreach (var (left, right, index) in a.EnumerateArray().Zip(b.EnumerateArray()).Select((p, i) => (p.First, p.Second, i)))
                        Walk(left, right, $"{path}[{index}]");
                    break;

                default:
                    if (a.GetRawText() != b.GetRawText())
                        parts.Add(path);
                    break;
            }
        }

        try
        {
            using var left = JsonDocument.Parse(before);
            using var right = JsonDocument.Parse(after);
            Walk(left.RootElement, right.RootElement, "$");
        }
        catch (JsonException)
        {
            return "(unreadable)";
        }

        return parts.Count == 0 ? "(nothing)" : string.Join(", ", parts);
    }
}
