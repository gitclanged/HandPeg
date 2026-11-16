using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// One undo state: the document, output audio tracks, blur-fill default, recycle bin, subtitles, caption box and the Sequence
/// look (grade, sharpening, blur fill); no pictures. Project Settings steps also carry <see cref="ProjectSettingsFrame"/>
/// (null and omitted otherwise).
/// </summary>
public sealed record TimelineSnapshot(
    Models.Timeline.TimelineDocument Document, List<AudioOutputEdit> Audio, bool BackgroundHidden = false, List<DeletedClip>? Bin = null,
    SubtitleTrackData? Subtitles = null, LayerState? CaptionLayer = null, CaptionStyle? CaptionStyle = null, SequenceLook? Look = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ProjectSettingsFrame? Frame = null) : System.Text.Json.Serialization.IJsonOnDeserialized
{
    /// <summary>
    /// Whether the bin's tethers are read as written (like TimelineDocument.TethersMarked); emitted only when the bin holds a
    /// piece an older reader would misread as a tether.
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
/// Packed undo step (in memory and in project files): <see cref="TimelineSnapshot"/> as UTF-8 JSON (declared names, numeric
/// enums), Brotli quality 1. Bump ProjectFile.CurrentUndoFormat on any incompatible snapshot change.
/// </summary>
public static class UndoSteps
{
    /// <summary>
    /// Decompression cap: an hour of word-level captions is a few MB, while a corrupt step from disk could claim gigabytes.
    /// </summary>
    public const int MaximumBytes = 128 * 1024 * 1024;

    /// <summary>Packs a snapshot, serializing into the reusable buffer given (a new one when null).</summary>
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
            // Quality 1 = CompressionLevel.Fastest.
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
    /// Unpacks a step via the reusable buffer given (a new one when null). Throws InvalidDataException for a corrupt stream or
    /// one past <see cref="MaximumBytes"/>, JsonException for invalid JSON.
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
/// One output audio track's handling (copy, re-encode, Ignore/Drop) and title, by stream. Null when not recorded (left unchanged).
/// Legacy steps stored main-video stream mixing under the same name; unknown fields are ignored.
/// </summary>
public sealed record AudioOutputEdit(int Index, string? Action = null, string? Title = null);

/// <summary>What Remove Dead Air does with the silences it finds.</summary>
public enum DeadAirMode
{
    /// <summary>The cut list keeps everything between silences (legacy Encoder Mode behaviour; the default value).</summary>
    Classic,

    /// <summary>
    /// Silences are folded: cut, closed up, and stored at the cut as a FoldedSegment (restorable from the clip menu).
    /// The timeline renders as shown.
    /// </summary>
    SplitAndMark,

    /// <summary>The silences are cut out and removed.</summary>
    Delete,
}

/// <summary>Detection source for Remove Dead Air.</summary>
public enum DeadAirSource
{
    /// <summary>Audio below the threshold for at least the minimum duration.</summary>
    Audio,

    /// <summary>Gaps between caption words, minus padding around each word.</summary>
    Captions,
}

// Timeline editing: undo/redo, delete and trim of the selection, independent audio slip/cut, and dirty tracking.
public partial class MainViewModel
{
    // ----- The time bars -----

    /// <summary>Snap dragged/dropped clip edges to other clip edges, the sequence end and the playhead. Persisted.</summary>
    [ObservableProperty] private bool _snapClips = AppSettings.Current.SnapClips;

    partial void OnSnapClipsChanged(bool value) => AppSettings.Set(s => s.SnapClips, (s, v) => s.SnapClips = v, value);

    /// <summary>Whether row headers are shown beside the time bars; collapsed, every time bar widens equally.</summary>
    [ObservableProperty] private bool _layerControlsShown = true;

    // ----- Undo and redo -----
    // Before each change the current state is pushed as a JSON snapshot (cuts, layers, audio edits); 20 deep.

    private const int UndoDepth = 20;
    // States are stored Brotli-compressed (repetitive property names shrink well) and decoded only when stepped to.
    private readonly List<(string What, byte[] State)> _undo = [];
    private readonly List<(string What, byte[] State)> _redo = [];

    // Number of undo steps older than the last save/open (MarkSaved); the recovery copy only lags through newer steps
    // (MainViewModel.Recovery.cs), so it never predates the saved state.
    private int _savedDepth;

    // Reusable UTF-8 buffer for packing/unpacking: long subtitle tracks make states hundreds of KB, which would hit the LOH
    // on every checkpoint. Only compressed copies are kept; ReadStep replaces the buffer after a corrupt step inflates it.
    private System.Buffers.ArrayBufferWriter<byte> _stateText = new(64 * 1024);
    private readonly System.Buffers.ArrayBufferWriter<byte> _otherText = new(16 * 1024);

    /// <summary>Serializes a value to UTF-8 JSON into the given buffer, replacing its contents.</summary>
    private static ReadOnlySpan<byte> WriteJson<T>(T value, System.Buffers.ArrayBufferWriter<byte> room)
    {
        room.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(room))
            JsonSerializer.Serialize(writer, value);
        return room.WrittenSpan;
    }

    /// <summary>Whether two values serialize to identical JSON (change detection).</summary>
    private bool SameJson<T>(T first, T second) => WriteJson(first, _stateText).SequenceEqual(WriteJson(second, _otherText));

    private byte[] Pack(TimelineSnapshot state) => UndoSteps.Pack(state, _stateText);

    private TimelineSnapshot? Unpack(byte[] state) => UndoSteps.Unpack(state, _stateText);

    /// <summary>
    /// Unpacks and validates a step, or returns null (corrupt or incompatible; project histories are read lazily) and logs why.
    /// </summary>
    private TimelineSnapshot? ReadStep(byte[] state, string what)
    {
        try
        {
            if (Unpack(state) is { } step && IsWhole(step))
            {
                // Legacy main-video steps are rejected: current code never writes them and legacy projects drop their
                // history on conversion (TakeUpHistory).
                if (!ProjectStore.HoldsOldKinds(step.Document, step.Bin))
                    return step;

                AppLog.Write($"The undo step \"{what}\" holds the main video of a HandPeg from before 5.2: it is let go");
                return null;
            }

            AppLog.Write($"The undo step \"{what}\" holds no timeline that can be put back");
        }
        catch (Exception ex)
        {
            AppLog.Write($"The undo step \"{what}\" could not be read", ex);

            // A corrupt step can inflate the buffer far beyond any real timeline; do not keep it at that size.
            if (_stateText.Capacity > 16 * 1024 * 1024)
                _stateText = new(64 * 1024);
        }

        return null;
    }

    /// <summary>Validates that a step has everything a restore reads: document, clips with looks, output tracks, bin looks.</summary>
    private static bool IsWhole(TimelineSnapshot step) =>
        step is { Document: { Tracks: { } tracks, Markers: not null, SequenceEffects: not null }, Audio: not null }
        && tracks.TrueForAll(t => t is { Clips: { } clips } && clips.TrueForAll(c => c is { State: not null }))
        && (step.Bin ?? []).TrueForAll(d => d is not { Clip: { State: null } });

    /// <summary>
    /// Current state after FlushLooks, with a shallow document copy (own tracks/clips, shared immutable looks); an edit's
    /// step is packed on commit (OnDocumentCommitted).
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
    /// Pushes an undo step restoring the given state (not necessarily the current one: document edits push the pre-edit state).
    /// </summary>
    private void Checkpoint(string what, TimelineSnapshot before)
    {
        // Not during a restore: it manages both histories itself.
        if (_isBackgroundWorker || _restoring > 0)
            return;

        PushStep(what, Pack(before));
        _touchedSinceSaved = true;
    }

    /// <summary>
    /// The single push point for undo: clears redo, drops the oldest past 20 and maintains <see cref="_savedDepth"/>. A push after
    /// undoing past the save starts a new branch, discarding the saved state on the redo side.
    /// </summary>
    private void PushStep(string what, byte[] state)
    {
        _savedDepth = Math.Min(_savedDepth, _undo.Count);
        _undo.Add((what, state));
        _gesture.Steps++;
        if (_undo.Count > UndoDepth)
        {
            _undo.RemoveAt(0);
            _savedDepth = Math.Max(0, _savedDepth - 1);
        }

        _redo.Clear();
    }

    /// <summary>
    /// Takes an undo step before a non-document change (looks, keyframes, effects, subtitles, caption box, output audio tracks).
    /// Document edits take their own step.
    /// </summary>
    /// <param name="what">Short status-bar label for the change.</param>
    public void Checkpoint(string what)
    {
        // Not during a restore: requests then come from the restore itself.
        if (_isBackgroundWorker || _restoring > 0)
        {
            if (_restoring > 0)
                AppLog.Write($"Undo step \"{what}\" not taken while a step was being put back");
            return;
        }

        PushStep(what, Pack(CaptureSnapshot()));
        _checkpointedAt = Environment.TickCount64;
        _gesture.StepPress = _inputGesture;
        _touchedSinceSaved = true;
    }

    /// <summary>
    /// Closes a dialog-scoped step (<see cref="Checkpoint(string)"/> on open, <see cref="UndoMark"/> after): drops it if nothing
    /// changed, so Ctrl+Z never appears to do nothing.
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
    /// Restores the newest step of one history and pushes the current state onto the other. Histories move only after a
    /// successful read and install; a failed step is discarded and reported, leaving the timeline unchanged.
    /// Refused while an edit or another restore is in progress.
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

        // Project Settings steps also record the current frame on the opposite history so the reverse step restores it.
        var now = Pack(target.Frame is null ? CaptureSnapshot() : CaptureSnapshot() with { Frame = CaptureProjectFrame() });
        using (BeginRestore())
        {
            try
            {
                RestoreTimeline(target);
            }
            catch (Exception ex)
            {
                // Discard the bad step and reinstall the pre-restore state; histories unchanged otherwise.
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

                // Oldest undo step dropped: one fewer step predates the save.
                if (ReferenceEquals(to, _undo))
                    _savedDepth = Math.Max(0, _savedDepth - 1);
            }
        }

        Log($"{verb}: {what}");
    }

    /// <summary>Removes the given (newest) step from a history; nothing else mutates histories during a restore.</summary>
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

        // Restore scope: no steps or edits from side effects; captions project once at the end (MainViewModel.Undo.cs).
        using var restoring = BeginRestore();

        // Rebuild the command once per step, not per restored clip.
        using var whole = DeferCommand();

        // Apply a Project Settings step's frame before rebuilding views (so they match their saved frame); its boxes are reset
        // once the natural size is restored (MainViewModel.ProjectSettings.cs).
        if (snapshot.Frame is { } frame)
            RestoreProjectFrame(frame);

        // Same project: unknown project fields from open are preserved (they are not in the step JSON).
        snapshot.Document.CarriedProjectFields = Document.CarriedProjectFields;
        InstallDocument(snapshot.Document);
        if (snapshot.Frame is { } boxes)
            RestoreProjectFrame(boxes);
        SetBin(snapshot.Bin);
        _backgroundRow.IsHidden = snapshot.BackgroundHidden;
        foreach (var output in snapshot.Audio)
        {
            if (AudioTracks.FirstOrDefault(t => t.Index == output.Index) is not { } track)
                continue;

            // Apply output track settings only where they differ.
            if (output.Action is { } action && AudioTrack.AllActions.Contains(action))
                track.Action = action;
            if (output.Title is { Length: > 0 } title)
                track.Title = title;
        }

        // Apply the Sequence look only if it differs, leaving render filters untouched otherwise. Compared as JSON because it
        // contains the blur fill's effect list.
        if (snapshot.Look is { } look && !SameJson(look, CaptureLook()))
            ApplyLook(look);

        // End any merge run: the next change takes its own step.
        _gesture.Looks.Reset();
        _gesture.Fields.Reset();

        // Subtitles, caption box and style are applied only where they differ, so the caption preview is otherwise untouched.
        // Anchored caption cues are re-projected from their anchors once at the end of the restore.
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
    /// The subtitle track minus projected anchored-caption cues (as ApplySubtitles strips them), to detect whether a step
    /// changed the subtitles themselves or only their projection.
    /// </summary>
    private static SubtitleTrackData? Unprojected(SubtitleTrackData? data) =>
        data is { IsImage: false, Source.IsAnchored: true, Anchors.Count: > 0 }
            ? new SubtitleTrackData
            {
                SourcePath = data.SourcePath, IsImage = data.IsImage, OffsetMs = data.OffsetMs, Cues = [.. (data.Cues ?? []).Where(c => c is { Projected: false })],
                Source = data.Source, Anchors = data.Anchors,
            }
            : data;

    // ----- Persisted undo history -----
    // Saves include packed undo/redo steps so Ctrl+Z continues after reopening. History is excluded from the dirty
    // fingerprint (CaptureState has none) and from queued jobs.

    /// <summary>Clears both histories (new source or project).</summary>
    private void ForgetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _savedDepth = 0;
        _gesture.Looks.Reset();
        _gesture.Fields.Reset();
        _visitStep = null;
    }

    /// <summary>
    /// Adopts a project's saved history on open; steps are decoded lazily (failures discarded then). If the source file moved,
    /// steps are re-pointed like the timeline (TimelineToOpen). Legacy-converted projects (LegacyMainVideo) keep no history.
    /// Takes no step and does not dirty the project.
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
    /// Save payload: <see cref="CaptureState"/> plus undo/redo history. <paramref name="moved"/> (downloads relocated to a chosen
    /// folder) rewrites paths in the saved steps only, not the live history. Includes a thumbnail of the Sequence at the
    /// playhead, or the last one taken (<see cref="ThumbnailForSave"/>).
    /// </summary>
    public ProjectState CaptureStateForSave(string name = "", Func<string, string>? moved = null) => CaptureStateForSave(name, moved, waitForThumbnail: true);

    /// <summary><see cref="CaptureStateForSave(string, Func{string, string}?)"/>; without <paramref name="waitForThumbnail"/> (autosave) it
    /// uses the last thumbnail and the caller renders a fresh one off the UI thread.</summary>
    private ProjectState CaptureStateForSave(string name, Func<string, string>? moved, bool waitForThumbnail)
    {
        TrustOwnCommandOnSave();
        var state = CaptureState(name);
        (state.UndoHistory, state.RedoHistory) = (HistoryOf(_undo, moved), HistoryOf(_redo, moved));
        state.ThumbnailJpeg = waitForThumbnail ? ThumbnailForSave() : Volatile.Read(ref _thumbnail);
        state.Workspace = _isBackgroundWorker ? null : WorkspaceForSave?.Invoke();
        return state;
    }

    /// <summary>Provides the window layout for a save's "workspace" section; set by the window, null in background workers.</summary>
    public Func<ProjectWorkspace?>? WorkspaceForSave { get; set; }

    /// <summary>Raised after opening a project with a "workspace" section so the window can apply it if enabled.</summary>
    public event Action<ProjectWorkspace>? WorkspaceOpened;

    /// <summary>A history as saved, oldest first (immutable packed steps are shared); null when empty.</summary>
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

    /// <summary>A step with moved files re-pointed (<see cref="MoveFiles"/>): repacked if anything moved, else itself; null if unreadable.</summary>
    private byte[]? MovedStep(byte[] state, string what, Func<string, string> moved) =>
        ReadStep(state, what) is not { } step ? null : MoveFiles(step, moved) ? Pack(step) : state;

    /// <summary>
    /// Re-points a freshly decoded step at moved files (timeline and bin clips with their looks, Project Media, caption source),
    /// mutating it in place. Returns whether anything moved.
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

    /// <summary>Moves the selected clip to the recycle bin, leaving a gap.</summary>
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
    /// Sets a clip's speed over the same source range, so its duration scales inversely; following clips ripple per
    /// AppSettings.SpeedRipplesTimeline (shared by the Speed &amp; Time card, Inspector box and clip menu).
    /// </summary>
    public void SetClipSpeed(Layer clip, double speed) => SetClipSpeed(clip, speed, AppSettings.Current.SpeedRipplesTimeline);

    /// <summary>
    /// Sets the speed of a clip and its family (stream lanes, Cutouts). With <paramref name="ripple"/>, following content (all
    /// tracks or its own, per Ripple All Tracks) shifts by the length delta in the same step; without, growth pushes its own
    /// track's clips and shrinking leaves a gap.
    /// </summary>
    public void SetClipSpeed(Layer clip, double speed, bool ripple)
    {
        speed = Math.Round(Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed), 3);

        // Binding echoes during undo/redo or its settle are not requests.
        if (ClipOf(clip) is not { PlaysFile: true } model || Math.Abs(model.Speed - speed) < 0.0005 || IsRestoringTimeline)
        {
            // Refresh bound speed boxes.
            OnViewSpeedRefused(clip);
            return;
        }

        // Stream lanes and Cutouts share the clip's source time, so they take its speed to stay in sync.
        var family = Document.Tracks.SelectMany(t => t.Clips).Where(c => c.ParentClipId == model.Id && c.PlaysFile && string.Equals(c.SourcePath, model.SourcePath, StringComparison.OrdinalIgnoreCase)).ToList();

        // Ripple once for the family: when growing, shift followers before the family grows; when shrinking, shrink the family
        // first so nothing blocks followers moving up.
        var all = AppSettings.Current.RippleAllTracks;
        var grows = speed < model.Speed;
        if (Edit($"change the speed of {clip.Name}", edit =>
            {
                if (ripple && !grows)
                {
                    foreach (var child in family)
                        edit.SetClipSpeed(child, speed);
                }

                edit.SetClipSpeed(model, speed, ripple: ripple, rippleAll: all);
                if (!ripple || grows)
                {
                    foreach (var child in family)
                        edit.SetClipSpeed(child, speed);
                }
            }, undoable: TakesUndoStep(clip, nameof(Layer.Speed))))
            Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Set speed of {clip.Name} to {model.Speed:0.##}x: it now runs {TimeDisplay.Format(model.Start)} to {TimeDisplay.Format(model.End)}"));
        else
            OnViewSpeedRefused(clip);
    }

    /// <summary>Reverts a view's speed box after a refused speed change.</summary>
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

    /// <summary>Trims the selected clip's start or end to the playhead.</summary>
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

    /// <summary>Duplicates a clip's whole track onto a new track just above, slightly offset on the frame.</summary>
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

    /// <summary>Raised when a track is hidden or shown; the layout pane redraws its boxes.</summary>
    public event Action? LayerVisibilityChanged;

    /// <summary>
    /// Toggles a row's visibility: the blur fill row (all videos' blur fill), a picture track (its switch, hiding its stack),
    /// or an audio track's clips.
    /// </summary>
    public void ToggleHidden(Layer layer)
    {
        // A picture track's eye is the track switch, not its first clip's.
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
                     // Picture track: the track switch hides its clips and stacked tracks (TimelineDocument.IsHiddenWithParents) without
                     // touching clip switches; legacy per-clip hiding is cleared on show. Audio tracks toggle each clip's audio.
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
    /// Whether a picture track header shows hidden: by its own switch, or every clip hidden (legacy per-clip hiding, which the
    /// eye clears on show).
    /// </summary>
    public bool IsTrackHidden(Layer row) => TrackOfRow(row) is { } track
        ? track.IsHidden || (track.Clips.Count > 0 && track.Clips.All(c => c.IsHidden))
        : row.IsHidden;

    /// <summary>Runs auto-captions on a video clip's own audio.</summary>
    public void UseLayerAudioForCaptions(Layer layer)
    {
        if (!layer.CarriesSound)
        {
            StatusText = $"{layer.Name} has no sound to make captions from.";
            return;
        }

        // Captions project through the clips of their file, at each clip's position and speed.
        if (ResolveCaptionSource(layer) is { } source)
            ChooseCaptionSource(source);
        StatusText = $"Auto-captions now listen to {layer.Name}.";
    }

    // ----- Audio on the timeline -----

    /// <summary>Whether audio is linked to picture: linked audio moves with its video; unlinked audio can be slipped independently.</summary>
    [ObservableProperty] private bool _audioLinked = true;

    /// <summary>Whether video clip blocks overlay the waveform of their linked audio.</summary>
    [ObservableProperty] private bool _showLinkedAudio = true;

    /// <summary>Links or unlinks audio and video; relinking after a slip confirms first, since slipped audio snaps back to its picture.</summary>
    public void SetAudioLinked(bool linked)
    {
        if (linked == AudioLinked)
            return;

        // Per-clip audio links live in the document; this switch sets all of them.
        var clips = Document.Tracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c))).Where(c => c.Clip.HasSound).ToList();
        var slipped = clips.Any(c => Math.Abs(c.Clip.AudioSlipSeconds) > 0.001);
        var choice = 1;
        if (linked && slipped)
        {
            // Choose which side moves: audio back to its picture, or picture to its audio.
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

        // All link changes form one edit.
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
                            // No room at the audio's position: keep the picture and move the audio back.
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


    /// <summary>Visible video clips with audio, whose audio is mixed into the output.</summary>
    private List<Layer> GetLayerAudioSources() =>
        Layers.Where(l => l.CarriesSound && !l.IsHidden && !l.IsSoundMuted && FileState.Exists(l.ImagePath)).ToList();

    /// <summary>Builds a clip's timeline waveform in the background (optional decoration).</summary>
    private async Task LoadLayerWaveformAsync(Layer layer)
    {
        if (_isBackgroundWorker || !layer.CarriesSound || !(ShowWaveforms || layer.IsAudio))
            return;

        // Built once per file (and stream) per session; splits and undo create new clips but reuse the cached picture.
        var path = layer.ImagePath;
        var stream = Math.Max(ClipOf(layer)?.AudioStreamIndex ?? 0, 0);
        var key = ClipPictures.SoundKey(path, stream);
        if (ClipPictures.Sound(key) is not null)
            return;
        if (!_layerWaveforms.TryGetValue(key, out var drawing))
        {
            _layerWaveforms[key] = drawing = Waveforms.RenderAsync(
                path, [stream], Path.Combine(WaveformFolder, $"layer_{Guid.NewGuid():N}.png"), 1200, 60, "white", PictureToken(path));
        }

        try
        {
            if (await drawing is { } picture)
                ClipPictures.PutSound(path, stream, picture);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Failures are not cached; the next clip of this file retries.
            _layerWaveforms.Remove(key);
        }
    }

    // Per-file cancellation for picture work, linked to the session token; cancelled as soon as no clip uses the file so
    // decoding stops for removed clips.
    private readonly Dictionary<string, CancellationTokenSource> _pictureWork = new(StringComparer.OrdinalIgnoreCase);

    private CancellationToken PictureToken(string path)
    {
        if (!_pictureWork.TryGetValue(path, out var work))
            _pictureWork[path] = work = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        return work.Token;
    }

    // LRU cap per kind (waveforms, strips) for files no longer in use; on-timeline clips pin their own pictures so nothing
    // visible blanks. A strip is <= 25 frames at 54 px high (~0.5 MB); a waveform is 1200 x 60.
    private const int PicturesKept = 24;

    /// <summary>Timeline filmstrips and waveforms keyed by file, shared across clips and tabs.</summary>
    public ClipPictures ClipPictures { get; }

    /// <summary>The strip of frames a video clip's block shows; null while none is made.</summary>
    public System.Windows.Media.ImageSource? FilmstripOf(Layer layer) => layer.IsVideoFile ? ClipPictures.Film(layer.ImagePath) : null;

    /// <summary>The clip's waveform for its file (or lane's stream); null until built.</summary>
    public System.Windows.Media.ImageSource? WaveformOf(Layer layer) =>
        layer.ImagePath.Length == 0 ? null : ClipPictures.Sound(ClipPictures.SoundKey(layer.ImagePath, Math.Max(ClipOf(layer)?.AudioStreamIndex ?? 0, 0)));

    // Files used by the current Sequence (pinned in the picture store).
    private IEnumerable<string> FilesInUse() => Layers.Select(l => l.ImagePath).Append(LocalMediaPath).Where(f => f.Length > 0);

    // Block filmstrip height (FfmpegRunner.GenerateFilmstripAsync); Media Browser strips are decoded at this height too.
    private const int LayerStripHeight = 54;

    private readonly LruCache<string, Task<System.Windows.Media.ImageSource?>> _layerWaveforms = new(PicturesKept, StringComparer.OrdinalIgnoreCase);

    /// <summary>Requests a layer block's pictures: its waveform and, for video, its filmstrip.</summary>
    private async Task LoadLayerPicturesAsync(Layer layer)
    {
        // Run concurrently: the filmstrip does not wait for the waveform.
        await Task.WhenAll(LoadLayerWaveformAsync(layer), LoadLayerFilmstripAsync(layer));
    }

    /// <summary>Builds an evenly spaced filmstrip for a video clip's block, in the background, once per file (optional).</summary>
    private async Task LoadLayerFilmstripAsync(Layer layer)
    {
        if (_isBackgroundWorker || !layer.IsVideoFile || ClipPictures.Film(layer.ImagePath) is not null || !File.Exists(layer.ImagePath))
            return;

        try
        {
            if (layer.MediaDuration <= 0 && await MediaProbe.ProbeAsync(layer.ImagePath, _shutdown.Token) is { DurationSeconds: > 0 } info)
                layer.MediaDuration = info.DurationSeconds;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Unknown length: grab a single frame from the start.
        }

        // Keyed by file (MakeFilmstripAsync), so every clip of the file shows it, including clips split mid-build.
        await GetFilmstripAsync(layer.ImagePath, Math.Clamp(AppSettings.Current.LayerThumbnailCount, 1, 25), layer.MediaDuration);
    }


    // In-flight filmstrip tasks by file; later clips of the same file await the first.
    private readonly Dictionary<string, Task<System.Windows.Media.ImageSource?>> _filmstripsBeingMade = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A file's filmstrip: cached, in flight, or newly started.</summary>
    private Task<System.Windows.Media.ImageSource?> GetFilmstripAsync(string file, int count, double seconds)
    {
        if (ClipPictures.Film(file) is { } made)
            return Task.FromResult<System.Windows.Media.ImageSource?>(made);
        if (!_filmstripsBeingMade.TryGetValue(file, out var making))
            _filmstripsBeingMade[file] = making = MakeFilmstripAsync(file, count, seconds);
        return making;
    }

    private async Task<System.Windows.Media.ImageSource?> MakeFilmstripAsync(string file, int count, double seconds)
    {
        // Yield first so the task is registered in the in-flight map before it can remove itself.
        await Task.Yield();
        try
        {
            var path = Path.Combine(SpriteFolder, $"layer_{Guid.NewGuid():N}.jpg");
            var token = _shutdown.Token;
            var kind = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"strip{count}");

            // Extract and decode off the UI thread, then Freeze before handing over: unfrozen bitmaps are thread-affine and
            // tracked for changes by WPF.
            var picture = await Task.Run(async () =>
            {
                // Disk cache hit for this file version; else reuse the Media Browser's strip (instant for browser drops) at block height.
                if (PreviewCache.Timeline.Find(file, kind, ".jpg") is { } kept)
                    path = kept;
                else if (MediaBrowserFrames.KeptStripAt(file, LayerStripHeight) is { } shared)
                    return (System.Windows.Media.ImageSource)shared;
                else if (await FfmpegRunner.GenerateFilmstripAsync(file, path, count, seconds, token))
                    path = PreviewCache.Timeline.Keep(file, kind, ".jpg", path);
                else
                    return null;

                // Load into memory so the file handle is released.
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
                ClipPictures.PutFilm(file, picture);
            return picture;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or UriFormatException or System.ComponentModel.Win32Exception)
        {
            if (ex is not OperationCanceledException)
                AppLog.Write($"Thumbnails of {Path.GetFileName(file)} could not be read", ex);
            return null;
        }
        finally
        {
            // Remove from in-flight either way; a failed file retries on the next request.
            _filmstripsBeingMade.Remove(file);
        }
    }

    // ----- Dead air -----

    private DeadAirMode _deadAirMode;
    private object? _deadAirTarget;
    private DeadAirSource _deadAirSource;

    /// <summary>
    /// Remove Dead Air on the selection: a clip with its own audio is cut directly; otherwise the voice-marked audio, else the
    /// first stream of the given/playhead video, cutting every clip of that file. Detection uses silences or caption word gaps.
    /// </summary>
    public void RunDeadAir(DeadAirMode mode, object? target, DeadAirSource source = DeadAirSource.Audio)
    {
        (_deadAirMode, _deadAirTarget, _deadAirSource) = (mode, target, source);
        if (RemoveDeadAirCommand.CanExecute(null))
            RemoveDeadAirCommand.Execute(null);
    }

    /// <summary>
    /// Splits clips at silences (in file time) and folds (<paramref name="fold"/>; restorable via <see cref="UnfoldSegment"/>) or
    /// removes the silent parts. With <paramref name="closeUp"/> they are removed from every unlocked track with a ripple;
    /// otherwise a fold closes up its own track and a removal leaves a gap. One undo step.
    /// </summary>
    /// <param name="layerLimit">
    /// Maximum cuts per layer: a few dozen silences keep a layer usable; caption word gaps are naturally far more numerous.
    /// </param>
    private string ApplyDeadAir(IReadOnlyList<Layer> views, List<(double Start, double End)> silences, bool fold, bool closeUp, int layerLimit = 60)
    {
        var clips = views.Select(v => (View: v, Clip: ClipOf(v))).Where(c => c.Clip is { PlaysFile: true }).ToList();
        if (clips.Count == 0)
            return "Nothing to cut: select a clip that plays a file.";

        // Map each clip's silences from file time to sequence time at its speed.
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
        // Per-layer cuts are capped to stay usable; file-wide ripple removal is unlimited for long recordings.
        if (cuts.Count > (closeUp ? 5000 : layerLimit))
            return $"{(clips.Count == 1 ? $"{clips[0].View.Name} has" : $"The clips of {Path.GetFileName(clips[0].View.ImagePath)} have")} too many silences to cut ({cuts.Count}): raise the shortest silence and try again.";

        var removed = 0.0;
        var marked = 0;
        Edit($"remove dead air from {name}", edit =>
        {
            // Latest first so ripple closes never shift silences still to be cut.
            foreach (var (clip, from, to) in cuts.OrderByDescending(c => c.From))
            {
                if (Document.TrackOf(clip) is null)
                    continue;

                // Fold at the cut (restorable); the timeline renders as shown.
                if (fold)
                {
                    try
                    {
                        if (edit.FoldStretch(Document.TrackOf(clip)!, from, to, rippleAllUnlockedTracks: closeUp) is { } made)
                            (marked, removed) = (marked + 1, removed + made.Length);
                    }
                    catch (TimelineEditException)
                    {
                        // Too near a cut or edge, or the ripple cannot close: leave unchanged.
                    }

                    continue;
                }

                // Ripple-delete across every unlocked track.
                if (closeUp)
                {
                    marked++;
                    removed += to - from;
                    TakeOutStretch(edit, from, to);
                    continue;
                }

                // Both cuts and the lift are atomic: if either cut is refused (too close to an edge or cut), skip the stretch.
                try
                {
                    removed += edit.DeleteStretchOf(clip, from, to);
                    marked++;
                }
                catch (TimelineEditException)
                {
                }
            }
        });

        return fold
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{marked} silent stretches ({removed:0.#} s) were folded out of {name}: click a seam (or right-click the clip after it and choose Restore Folded Silence) to put one back, and its Hide pill folds it again.")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{marked} silent stretches ({removed:0.#} s) were removed from {name}{(closeUp ? ", and what follows closed up" : "")}.");
    }

    // ----- Folded silences -----

    /// <summary>Segments folded at a clip's seams (start, or end when nothing follows), latest seam first.</summary>
    public IReadOnlyList<FoldedSegment> FoldsAt(Layer clip) =>
        ClipOf(clip) is { } model && Document.Folds is { } folds ? [.. folds.Where(f => f.SeamClipId == model.Id)] : [];

    /// <summary>Number of folded segments whose seams still exist.</summary>
    public int FoldCount => Document.Folds?.Count(f => f.SeamIn(Document) is not null) ?? 0;

    /// <summary>Unfolds a segment at its seam, rippling later content by its length. One undo step.</summary>
    public void UnfoldSegment(FoldedSegment fold)
    {
        if (Edit($"put back {fold.Length:0.#} s at a cut", edit => edit.UnfoldSegment(fold)))
            Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Put back {fold.Length:0.#} s at the cut at {TimeDisplay.Format(fold.SeamIn(Document) ?? 0)}"));
    }

    /// <summary>Unfolds every segment, latest first so earlier seams stay put. One undo step.</summary>
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

    // Fingerprint at the last save/load, for dirty tracking.
    private string _savedFingerprint = "";

    // Whether user input (key, click, wheel, drop) or an undoable edit occurred since load/save. Earlier changes (probes,
    // scans, player settling) are absorbed into the saved state so open-then-close never prompts to save.
    private bool _touchedSinceSaved;

    /// <summary>
    /// Dirty-tracking JSON of the live state (no deep copy; serialization only reads), without name or save time, using
    /// <see cref="FingerprintOptions"/> like <see cref="FrozenStateDiffers"/>. Byte-equal to the deep-copy form (golden fingerprint-live-equals-copy).
    /// </summary>
    internal string Fingerprint() => JsonSerializer.Serialize(CaptureState("", liveTimeline: true, copyBin: false), FingerprintOptions);

    /// <summary>Reference fingerprint via a deep copy with default options; used by the golden only.</summary>
    internal string FingerprintOfCopy()
    {
        var state = CaptureState();
        state.SavedAt = default;
        return JsonSerializer.Serialize(state);
    }

    /// <summary>Marks the current state as saved (after load and save).</summary>
    public void MarkSaved()
    {
        _savedFingerprint = Fingerprint();
        _touchedSinceSaved = false;

        // All current steps now predate the save/open; recovery lags only through later steps.
        _savedDepth = _undo.Count;
    }

    /// <summary>
    /// First user input since load/save (from any HandPeg window): rebaselines the saved fingerprint so only later changes
    /// count as dirty.
    /// </summary>
    public void NoteUserInput()
    {
        // User input ends any restore settle immediately.
        _settleEndsAt = 0;

        // Look changes right after input are user changes and take an undo step (MainViewModel.Undo.cs).
        _userInputAt = Environment.TickCount64;
        _inputGesture++;
        if (!_undoSourcesHooked)
            HookUndoSources();

        if (_touchedSinceSaved)
            return;

        _touchedSinceSaved = true;
        if (_savedFingerprint.Length == 0 || Fingerprint() is not { } now || now == _savedFingerprint)
            return;

        AppLog.Write($"Changed by itself since the project was opened or saved (not counted as an edit): {ChangedParts(_savedFingerprint, now)}");
        _savedFingerprint = now;
    }

    /// <summary>Whether user changes (clips, text, emptying the timeline) occurred since load, save or creation.</summary>
    public bool HasUnsavedChanges => _touchedSinceSaved && _savedFingerprint.Length > 0 && Fingerprint() != _savedFingerprint;

    /// <summary>Log helper on close with unsaved changes: which project parts differ.</summary>
    public string DescribeUnsavedChanges() => ChangedParts(_savedFingerprint, Fingerprint());

    /// <summary>The first few differing property paths between two fingerprints; names only, so logs carry no file names or titles.</summary>
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
