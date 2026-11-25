using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.ViewModels;

// ----- Timeline document and clip views -----
// The timeline is one TimelineDocument (tracks bottom-first, clips, markers, Sequence Output effects) and the single source of
// clip placement. Every timing/structure change is one transaction via Edit: one undo step, then OnDocumentCommitted syncs views.
//
// Each clip has a Layer view used by the rows, Canvas, Effects and Keyframes panes. Timing and switches flow document -> view
// (SyncViews only); a direct view write is treated as a request and becomes a clip edit (TakeClipField). Looks flow view ->
// document: FlushLooks writes a new immutable LayerState before any edit, undo, render or save reads the document.
// Looks are per clip; legacy shared track looks are folded once (TimelineDocument.FoldTrackLooks).
public partial class MainViewModel
{
    /// <summary>The timeline document: tracks, clips, markers and Sequence Output effects.</summary>
    public TimelineDocument Document { get; private set; } = new() { PoolsPlacedFiles = true };

    // Views by clip id; the LayerState each view last loaded (reloaded only when the clip's state object changes); per-track
    // Layer.TrackId numbers (never reused within a session); stand-in views for empty tracks.
    private readonly Dictionary<Guid, Layer> _views = [];
    private readonly Dictionary<Guid, LayerState> _taken = [];
    private readonly Dictionary<Guid, int> _trackNumbers = [];
    private readonly Dictionary<int, TimelineTrackModel> _tracksByNumber = [];
    private readonly Dictionary<Guid, Layer> _emptyTrackViews = [];
    private int _lastTrackNumber;

    // Views whose look is newer than their clip's.
    private readonly HashSet<Layer> _staleLooks = new(ReferenceEqualityComparer.Instance);

    // Nesting depth of document -> view writes (SyncViews, clip value refresh); view changes during it are echoes, not requests.
    // A counter so a nested sync (a handler committing mid-sync) cannot end the outer one early.
    private int _syncDepth;

    /// <summary>True while any document -> view sync is running; setters increment/decrement the depth (callers use finally).</summary>
    private bool _syncingViews
    {
        get => _syncDepth > 0;
        set => _syncDepth = Math.Max(_syncDepth + (value ? 1 : -1), 0);
    }

    // The open edit transaction (Edit) and its pre-edit snapshot for the undo step.
    private ITimelineTransaction? _openEdit;
    private TimelineSnapshot? _beforeEdit;

    /// <summary>Time of the last manual Checkpoint, so a box change immediately after it does not take a second step.</summary>
    private long _checkpointedAt;

    /// <summary>A timeline with nothing on it, of the frame and rate the settings give now.</summary>
    private TimelineDocument NewDocument() => new()
    {
        Width = Math.Max(FrameWidth, 2),
        Height = Math.Max(FrameHeight, 2),
        UseVerticalResolution = UseVerticalResolution,
        FrameRate = Models.Timeline.FrameRate.OfSource(SourceFrameRate),
        PoolsPlacedFiles = true,
    };

    /// <summary>
    /// Installs a document (new project, undo/redo, open): rebuilds views (selection follows its clip), loads markers and
    /// Sequence Output effects into their panes. Runs as a restore: no undo step or edit; captions project once at the
    /// outermost <see cref="EndRestore"/>.
    /// </summary>
    internal void InstallDocument(TimelineDocument document) => PutDocumentInPlace(document, kept: null);

    /// <summary>
    /// <see cref="InstallDocument"/> reusing a backgrounded tab's <see cref="KeptViews"/>: re-synced incrementally instead of rebuilt.
    /// Null, or views kept for another Sequence, rebuilds them.
    /// </summary>
    private void PutDocumentInPlace(TimelineDocument document, KeptViews? kept)
    {
        using var restoring = BeginRestore();
        _turnBeforePreset = null;
        Document.Committed -= OnDocumentCommitted;

        // Project Media registers every clip's source file of the installed document.
        document.PoolsPlacedFiles = true;
        document.FoldTrackLooks();
        Document = document;
        Document.Committed += OnDocumentCommitted;

        using (DeferCommand())
        {
            if (kept is not null && ReferenceEquals(kept.Document, document))
            {
                TakeUpViews(kept);
                SyncViews();
            }
            else
            {
                SyncViews(afresh: true);
            }

            FollowSelection();
            SetMarkers(document.Markers);
            SetSequenceEffects(document.SequenceEffects, document.SequenceEffectsBypassed);
            AudioLinked = AllAudioLinked();
            NoteDocumentShape();
            _previewState = null;
            RefreshLayerRows();
            RaiseTimelineChanged();
            GenerateCommand();
        }

        // Captions track their source clips: project them after the restored anchors are in place.
        _reprojectPending = true;
    }

    // ----- Finding a clip's view, a view's clip, a row's track -----

    /// <summary>The clip behind a view; null for stand-ins (caption box, background, empty track).</summary>
    public TimelineClip? ClipOf(Layer? view) => view is { ClipId: var id } && id != Guid.Empty ? Document.FindClip(id) : null;

    /// <summary>
    /// Whether a layer renders at a time: not hidden itself, not on a hidden track, not fully transparent. Undrawn layers are
    /// not hit-testable in the player or Canvas.
    /// </summary>
    public bool IsPictureDrawn(Layer layer, double seconds)
    {
        if (layer.IsHidden || (ClipOf(layer) is { } clip && (clip.IsHidden || Document.TrackOf(clip) is { IsHidden: true })))
            return false;

        // Opacity is a percentage: 0.1 means 0.1 %.
        return layer.Evaluate(KeyProperty.Opacity, seconds - layer.StartTime) > 0.1;
    }

    /// <summary>The row's track (its clip's, or the empty track); null for stand-ins.</summary>
    /// <summary>Sets a per-track row height, or null for the global Track Height. One undo step; saved with the project.</summary>
    public void SetTrackHeight(TimelineTrackModel track, double? height) =>
        Edit(height is null ? $"reset the height of {track.DisplayName}" : $"resize {track.DisplayName}", edit => edit.SetTrackHeight(track, height));

    public TimelineTrackModel? TrackOfRow(Layer? row) =>
        row is not null && _tracksByNumber.TryGetValue(row.TrackId, out var track) && Document.Tracks.Contains(track) ? track : null;

    /// <summary>The view of a clip, once it has one.</summary>
    public Layer? ViewOf(TimelineClip clip) => _views.GetValueOrDefault(clip.Id);

    private int NumberOf(TimelineTrackModel track)
    {
        if (!_trackNumbers.TryGetValue(track.Id, out var number))
        {
            number = ++_lastTrackNumber;
            _trackNumbers[track.Id] = number;
        }

        _tracksByNumber[number] = track;
        return number;
    }

    /// <summary>Insertion index for a new top picture track: after the last non-audio track.</summary>
    private int TopPictureIndex()
    {
        var index = Document.Tracks.FindLastIndex(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle));
        return index + 1;
    }

    /// <summary>
    /// Whether every clip with audio has it linked to its picture (the Layers tab's global switch); with no audio clips, the
    /// switch value applied to the next one.
    /// </summary>
    private bool AllAudioLinked()
    {
        var sounds = Document.Tracks.SelectMany(t => t.Clips).Where(c => c.HasSound).ToList();
        return sounds.Count == 0 ? AudioLinked : sounds.All(c => c.AudioLinked);
    }

    // ----- Edits -----

    /// <summary>
    /// Runs <paramref name="change"/> as one transaction: tracks it empties are removed (explicitly created empty tracks stay),
    /// and on commit an undo step named <paramref name="label"/> is taken and views sync (<see cref="OnDocumentCommitted"/>).
    /// A <see cref="TimelineEditException"/> rolls back and posts the reason. Nested calls fold into the outer transaction.
    /// No exception escapes: faults roll back, are logged and return false. Refused during a document restore.
    /// </summary>
    /// <param name="undoable">False for derived changes (a probed length after a drop): no undo step.</param>
    /// <param name="removeEmptied">False to keep tracks the change empties.</param>
    internal bool Edit(string label, Action<ITimelineTransaction> change, bool undoable = true, bool removeEmptied = true)
    {
        if (_openEdit is { } open)
        {
            change(open);
            return true;
        }

        // Edits requested during a restore come from the restore itself and would land on a document about to be replaced.
        if (_restoring > 0)
        {
            AppLog.Write($"Edit \"{label}\" refused while a step was being put back");
            return false;
        }

        _editCommitted = false;
        try
        {
            // CaptureSnapshot flushes the looks itself: only an edit that keeps no step flushes them here.
            var before = undoable && !_isBackgroundWorker ? CaptureSnapshot() : null;
            if (before is null)
                FlushLooks();
            var filled = removeEmptied ? Document.Tracks.Where(t => t.Clips.Count > 0).ToList() : [];
            using var edit = Document.BeginTransaction(label);
            (_openEdit, _beforeEdit) = (edit, before);
            change(edit);
            foreach (var track in filled)
            {
                if (track.Clips.Count == 0 && !track.IsLocked && Document.Tracks.Contains(track))
                    edit.RemoveTrack(track);
            }

            edit.Commit();
            DevFlightRecorder.Record("edit", label);
            return true;
        }
        catch (TimelineEditException refused)
        {
            DevFlightRecorder.Record("refused", $"{label}: {refused.Message}");
            StatusText = refused.Message;
            return false;
        }
        catch (Exception ex)
        {
            // A fault: the transaction has rolled the document back (unless it was committed, and what followed failed).
            NoteFault("edit", $"The edit \"{label}\"", NoteOf(label), ex);
            StatusText = _editCommitted
                ? $"\"{label}\" was made, but the timeline could not be fully redrawn after it (the reason is in the log)."
                : $"\"{label}\" could not be made, and nothing was changed (the reason is in the log).";
            return _editCommitted;
        }
        finally
        {
            (_openEdit, _beforeEdit) = (null, null);
        }
    }

    // Set once OnDocumentCommitted ran: a later fault must not be reported as a no-op rollback.
    private bool _editCommitted;

    /// <summary>
    /// Commit handler: pushes the undo step, syncs views and rebuilds the render state. Runs inside Commit, so nothing may
    /// escape: a view-sync fault is logged and views are rebuilt from the document; the undo step is kept.
    /// </summary>
    private void OnDocumentCommitted(TimelineCommit commit)
    {
        // From here, handler edits are separate edits. Undo waits on _committing so this step is pushed before history moves.
        var before = _beforeEdit;
        (_openEdit, _beforeEdit) = (null, null);
        _editCommitted = true;
        _committing++;
        try
        {
            try
            {
                using (DeferCommand())
                {
                    _previewState = null;
                    SyncViews();
                    AudioLinked = AllAudioLinked();
                    NoteDocumentShape();
                    RefreshLayerRows();
                    RaiseTimelineChanged();
                    GenerateCommand();
                }
            }
            catch (Exception ex)
            {
                NoteFault("edit", $"Bringing the views into line after \"{commit.Label}\"", $"views after {NoteOf(commit.Label)}", ex);
                RecoverViews();
            }

            try
            {
                if (before is not null)
                    Checkpoint(commit.Label, before);
            }
            catch (Exception ex)
            {
                NoteFault("edit", $"Keeping the undo step of \"{commit.Label}\"", $"undo step of {NoteOf(commit.Label)}", ex);
            }
        }
        finally
        {
            _committing--;
        }

        // Captions track their source clips.
        try
        {
            ReprojectCaptions();
        }
        catch (Exception ex)
        {
            NoteFault("edit", $"Showing the captions after \"{commit.Label}\"", $"captions after {NoteOf(commit.Label)}", ex);
        }
    }

    // Commits currently in OnDocumentCommitted (view sync + undo push); Undo waits until zero.
    private int _committing;

    /// <summary>
    /// Rebuilds all views from the document after an incremental sync failed (selection follows its clip; one
    /// <see cref="TimelineRestored"/>). Faults here are logged only: the document, not the views, renders and saves.
    /// </summary>
    private void RecoverViews()
    {
        try
        {
            using (BeginRestore())
            using (DeferCommand())
            {
                _previewState = null;
                SyncViews(afresh: true);
                FollowSelection();
                AudioLinked = AllAudioLinked();
                NoteDocumentShape();
                RefreshLayerRows();
                RaiseTimelineChanged();
                GenerateCommand();
            }
        }
        catch (Exception ex)
        {
            NoteFault("edit", "Making the views afresh", "views made afresh", ex);
        }
    }

    // ----- The views -----

    /// <summary>
    /// Syncs <see cref="Layers"/> to the document: one view per clip in stacking order (picture tracks bottom-up, then audio;
    /// clips in time), stand-ins for empty tracks; removed clips' views are released. View identity is stable per clip so
    /// selections and panes keep their references; <paramref name="afresh"/> rebuilds all views.
    /// <para>Applied as one <see cref="ViewList.ReplaceAll"/> (a single Reset, none if order is unchanged) so bulk edits notify each
    /// listener once. Callers refresh rows themselves, so the rows' listener skips that Reset (<see cref="ViewList.Syncing"/>).</para>
    /// </summary>
    private void SyncViews(bool afresh = false)
    {
        if (afresh)
        {
            foreach (var view in _views.Values.Concat(_emptyTrackViews.Values).ToArray())
                Unhook(view);
            ForgetViews();
            DrawTargetLayer = null;
        }

        var wanted = new List<Layer>();
        var clips = new HashSet<Guid>();
        var empties = new HashSet<Guid>();
        _syncingViews = true;
        try
        {
            foreach (var sounds in new[] { false, true })
            {
                foreach (var track in Document.Tracks)
                {
                    if (track.Kind == TimelineTrackKind.Subtitle || (track.Kind == TimelineTrackKind.Audio) != sounds)
                        continue;

                    var number = NumberOf(track);
                    if (track.Clips.Count == 0)
                    {
                        wanted.Add(EmptyTrackView(track, number));
                        empties.Add(track.Id);
                        continue;
                    }

                    foreach (var clip in track.Clips)
                    {
                        wanted.Add(ViewFor(clip, track));
                        clips.Add(clip.Id);
                    }
                }
            }

        }
        finally
        {
            _syncingViews = false;
        }

        foreach (var (id, view) in _views.Where(v => !clips.Contains(v.Key)).ToList())
        {
            Unhook(view);
            _views.Remove(id);
            _taken.Remove(id);
            _staleLooks.Remove(view);
            _placedFor.Remove(view);
        }

        foreach (var (id, view) in _emptyTrackViews.Where(v => !empties.Contains(v.Key)).ToList())
        {
            Unhook(view);
            _emptyTrackViews.Remove(id);
        }

        Layers.ReplaceAll(wanted, syncing: true);

        if (DrawTargetLayer is { } target && !Layers.Contains(target))
            DrawTargetLayer = null;
        RefreshAnyKeys();

        // Refresh the Crop boxes from the document (undo may have restored CropTarget's crop).
        ShowCropTarget();
    }

    /// <summary>Returns the clip's existing view (reloading its look if the state changed) or creates one from its look.</summary>
    private Layer ViewFor(TimelineClip clip, TimelineTrackModel track)
    {
        if (_views.TryGetValue(clip.Id, out var view))
        {
            if (!ReferenceEquals(_taken.GetValueOrDefault(clip.Id), clip.State))
            {
                // New state object: keys shifted by an edit (start trim, split, speed), an undo restore, or a new placement,
                // crop or blur fill.
                view.ApplyLook(clip.State);
                view.ApplySource(clip.State);
                view.ApplyPlace(clip.State);
                view.ApplyTiming(clip.State);
                _taken[clip.Id] = clip.State;
                _placedFor.Remove(view);
            }
        }
        else
        {
            view = Layer.FromState(LookOf(clip), SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            view.ClipId = clip.Id;
            Hook(view);
            _views[clip.Id] = view;
            _taken[clip.Id] = clip.State;

            // Derived clip (split's second half): inherit the source's reshape anchor so both halves stay identical on reshape.
            if (_placedFor.FirstOrDefault(p => p.Value.Wrote == (view.SizeWidth, view.PositionX, view.PositionY) && CropOf(p.Key) == CropOf(view)
                    && p.Key.ImagePath == view.ImagePath) is { Key: not null } same)
            {
                _placedFor[view] = same.Value;
            }

            // Clip captured mid-probe (undo step or save during a drop): re-probe to fill length and audio, keeping placement.
            if (view is { IsVideoFile: true } && (view.ImageWidth <= 0 || clip.SourceDuration <= 0) && !_isBackgroundWorker)
            {
                view.IsLoading = true;

                // Dropped just before its tab was backgrounded: finish as the drop would (file length, fitted), not at stand-in size.
                if (_addsToResume is { } resumed && resumed.TryGetValue(clip.Id, out var add))
                    _ = FinishVideoAddAsync(view, add);
                else
                    _ = CompleteVideoLayerAsync(view, playOnce: true, keepLength: true);
            }
            else if (!_isBackgroundWorker)
            {
                _ = LoadLayerPicturesAsync(view);
            }
        }

        TakeClip(view, clip, track);
        return view;
    }

    /// <summary>The LayerState a view is built from, with kind and file taken from the clip.</summary>
    private static LayerState LookOf(TimelineClip clip)
    {
        var look = clip.State.ShallowCopy();
        look.Kind = clip.ContentKind;
        if (clip.SourcePath is { Length: > 0 } path && clip.ContentKind is LayerKind.VideoFile or LayerKind.Image or LayerKind.Audio)
            look.ImagePath = path;
        return look;
    }

    /// <summary>
    /// Writes clip-owned fields (timing, fades, switches, gain, slip, proxy, track) into the view; track and parent hide
    /// switches are folded into the view's own.
    /// </summary>
    private void TakeClip(Layer view, TimelineClip clip, TimelineTrackModel track)
    {
        view.TrackId = NumberOf(track);
        (view.StartTime, view.Duration, view.MediaOffset) = (clip.Start, clip.Duration, clip.MediaOffset);
        view.Speed = clip.Speed;
        (view.FadeInSeconds, view.FadeOutSeconds, view.FadeInStyle, view.FadeOutStyle) = (clip.FadeInSeconds, clip.FadeOutSeconds, clip.FadeInStyle, clip.FadeOutStyle);
        view.TransitionIn = clip.TransitionIn;
        (view.IsHidden, view.IsSoundMuted, view.AudioGainDb) = (clip.IsHidden || (track.Kind != TimelineTrackKind.Audio && Document.IsHiddenWithParents(track)), clip.IsMuted, clip.GainDb);
        (view.AudioOffset, view.ProxyChoice) = (clip.AudioLinked ? 0 : clip.AudioSlipSeconds, clip.ProxyChoice);
        if (clip.SourceDuration > 0)
            view.MediaDuration = clip.SourceDuration;
    }

    /// <summary>Stand-in row for an empty track; replaced by the first clip dropped on it.</summary>
    private Layer EmptyTrackView(TimelineTrackModel track, int trackNumber)
    {
        if (!_emptyTrackViews.TryGetValue(track.Id, out var view))
        {
            view = new Layer
            {
                Kind = track.Kind == TimelineTrackKind.Audio ? LayerKind.Audio : track.Kind == TimelineTrackKind.Adjustment ? LayerKind.Adjustment : LayerKind.VideoFile,
                Name = track.Name.Length > 0 ? track.Name : "Empty Track",
                IsEmptyTrack = true,
            };
            Hook(view);
            _emptyTrackViews[track.Id] = view;
        }

        (view.TrackId, view.IsHidden) = (trackNumber, track.IsHidden);
        return view;
    }

    /// <summary>
    /// Attaches a view: subscribes to its changes (OnViewChanging takes an undo step before a user look change) and enables
    /// centre-anchored resizing.
    /// </summary>
    private void Hook(Layer view)
    {
        view.PropertyChanged -= OnLayerChanged;
        view.PropertyChanged += OnLayerChanged;
        view.PropertyChanging -= OnViewChanging;
        view.PropertyChanging += OnViewChanging;
        view.SpeedEditor = SetClipSpeed;
        view.AnchorCenter = true;
    }

    private void Unhook(Layer view)
    {
        view.PropertyChanged -= OnLayerChanged;
        view.PropertyChanging -= OnViewChanging;
        view.SpeedEditor = null;
        if (ReferenceEquals(DrawTargetLayer, view))
            DrawTargetLayer = null;
    }

    // ----- Per-tab kept views -----
    // A live background tab (MainViewModel.Tabs.cs) keeps its clip views so activation skips rebuilding them (look load, picture
    // requests, handler hookup per clip). While backgrounded they are in no VM map and unsubscribed, so other tabs cannot reach
    // them (duplicate tabs share clip ids). On activation they re-sync by clip id like after an edit.

    /// <summary>
    /// A backgrounded tab's views: owning Sequence, clip views with their last LayerState, empty-track stand-ins, and reshape
    /// anchors (<see cref="KeepPlacesOnReshape"/>).
    /// </summary>
    internal sealed record KeptViews(TimelineDocument Document, Dictionary<Guid, Layer> Views, Dictionary<Guid, LayerState> Taken,
        Dictionary<Guid, Layer> EmptyTrackViews, Dictionary<Layer, PicturePlacement> PlacedFor);

    /// <summary>
    /// Detaches the current views for a backgrounding tab (after CaptureState flushed looks): unsubscribed and removed from VM
    /// maps so the next tab's setup (KeepPlacesOnReshape) cannot move them. DetachForTab clears the list afterwards.
    /// </summary>
    internal KeptViews LetGoOfViews()
    {
        var kept = new KeptViews(Document, new(_views), new(_taken), new(_emptyTrackViews), new(_placedFor, ReferenceEqualityComparer.Instance));
        foreach (var view in _views.Values.Concat(_emptyTrackViews.Values))
            Unhook(view);
        ForgetViews();
        return kept;
    }

    /// <summary>
    /// Re-attaches a tab's kept views on activation (<see cref="PutDocumentInPlace"/>), releasing the current ones. Views captured
    /// mid-probe are rebuilt and re-probed (the old probe is orphaned). Pictures are not re-requested: pending strips and
    /// waveforms target the view itself whether its tab is active or not.
    /// </summary>
    private void TakeUpViews(KeptViews kept)
    {
        foreach (var view in _views.Values.Concat(_emptyTrackViews.Values).ToArray())
            Unhook(view);
        ForgetViews();
        DrawTargetLayer = null;
        foreach (var (id, view) in kept.Views)
        {
            if (view.IsLoading)
                continue;

            Hook(view);
            _views[id] = view;
            if (kept.Taken.TryGetValue(id, out var look))
                _taken[id] = look;
            if (kept.PlacedFor.TryGetValue(view, out var placed))
                _placedFor[view] = placed;
        }

        foreach (var (id, view) in kept.EmptyTrackViews)
        {
            Hook(view);
            _emptyTrackViews[id] = view;
        }
    }

    /// <summary>Clears every view map; the caller releases the views.</summary>
    private void ForgetViews()
    {
        _views.Clear();
        _emptyTrackViews.Clear();
        _taken.Clear();
        _staleLooks.Clear();
        _placedFor.Clear();
        _cropShown = null;
    }

    /// <summary>The <see cref="Layers"/> collection, with a bulk replace that raises a single Reset instead of per-item events.</summary>
    public sealed class ViewList : System.Collections.ObjectModel.ObservableCollection<Layer>
    {
        /// <summary>True while a sync raises its Reset; the caller refreshes the rows immediately after.</summary>
        internal bool Syncing { get; private set; }

        /// <summary>Replaces the contents with <paramref name="views"/> in order as one Reset; no event if already equal.</summary>
        internal void ReplaceAll(IReadOnlyList<Layer> views, bool syncing = false)
        {
            if (Items.Count == views.Count && Items.SequenceEqual(views, ReferenceEqualityComparer.Instance))
                return;

            CheckReentrancy();
            Items.Clear();
            foreach (var view in views)
                Items.Add(view);
            Syncing = syncing;
            try
            {
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            }
            finally
            {
                Syncing = false;
            }
        }
    }

    /// <summary>
    /// Creates a clip for a new view (drop, add layer) spanning <paramref name="start"/>..<paramref name="end"/>, other fields
    /// from the view; the view is bound to it on placement (<see cref="Adopt"/>).
    /// </summary>
    private TimelineClip NewClipFor(Layer view, double start, double end) => new()
    {
        ContentKind = view.Kind,
        SourcePath = view.IsFile || view.IsAudio ? view.ImagePath : null,
        AudioStreamIndex = view.CarriesSound ? 0 : -1,
        Start = start,
        End = end,
        MediaOffset = view.IsVideoFile || view.IsAudio ? view.MediaOffset : 0,
        Speed = view.HasSpeed ? view.Speed : 1,
        SourceDuration = view.IsVideoFile || view.IsAudio ? view.MediaDuration : 0,
        AudioLinked = AudioLinked,
        AudioSlipSeconds = AudioLinked || view.IsAudio ? 0 : view.AudioOffset,
        FadeInSeconds = view.FadeInSeconds,
        FadeOutSeconds = view.FadeOutSeconds,
        FadeInStyle = view.FadeInStyle,
        FadeOutStyle = view.FadeOutStyle,
        TransitionIn = view.TransitionIn,
        GainDb = view.AudioGainDb,
        IsHidden = view.IsHidden,
        IsMuted = view.IsSoundMuted,
        ProxyChoice = view.ProxyChoice,
        State = view.ToState(),
    };

    /// <summary>Pre-binds a view to a clip about to be placed so the sync reuses that instance (and its pictures).</summary>
    private void Adopt(Layer view, TimelineClip clip)
    {
        view.ClipId = clip.Id;
        _views[clip.Id] = view;
        _taken[clip.Id] = clip.State;
        Hook(view);
    }

    /// <summary>Releases a view whose clip placement was refused.</summary>
    private void Unadopt(Layer view)
    {
        if (view.ClipId != Guid.Empty && Document.FindClip(view.ClipId) is null)
        {
            _views.Remove(view.ClipId);
            _taken.Remove(view.ClipId);
            Unhook(view);
            view.ClipId = Guid.Empty;
        }
    }

    /// <summary>
    /// Places a new view's clip by drop row: fills an empty track, joins a track when <paramref name="joins"/> and free, else a
    /// new track just above the row; with no row, a new top picture track (audio at the end).
    /// </summary>
    private TimelineClip PlaceNew(ITimelineTransaction edit, Layer view, double start, double end, Layer? onRow, bool joins)
    {
        CrashReporter.KeepOut(view.ImagePath);
        var clip = NewClipFor(view, start, end);
        var kind = TimelineTrackModel.KindFor(view.Kind);
        var row = TrackOfRow(onRow);
        Adopt(view, clip);
        try
        {
            if (row is not null && row.Accepts(clip) && (row.Clips.Count == 0 || joins))
            {
                try
                {
                    // Remember an empty track filled by a probing clip so it is restored empty if the file is not a video.
                    if (row.Clips.Count == 0 && view.IsLoading)
                        _filledEmptyTracks.Add(row.Id);
                    return edit.PlaceClip(row, clip, allowCrossfadeOverlap: false);
                }
                catch (TimelineEditException) when (row.Clips.Count > 0)
                {
                    // No room: create a new track just above.
                }
            }

            var besideRow = row is not null && (row.Kind == kind || (row.Kind == TimelineTrackKind.Switch && kind == TimelineTrackKind.Video));
            var index = besideRow ? Document.Tracks.IndexOf(row!) + 1 : kind == TimelineTrackKind.Audio ? Document.Tracks.Count : TopPictureIndex();
            return edit.PlaceClip(edit.InsertTrack(index, kind), clip, allowCrossfadeOverlap: false);
        }
        catch
        {
            Unadopt(view);
            throw;
        }
    }

    // ----- Looks: from the views into the clips -----

    /// <summary>
    /// Flushes dirty view looks into their clips as new LayerStates, plus markers and Sequence Output effects, before an edit,
    /// undo step, render or save reads the document.
    /// </summary>
    private void FlushLooks()
    {
        foreach (var view in _staleLooks)
        {
            if (ClipOf(view) is { } clip)
            {
                var look = view.ToState();
                clip.State = look;
                _taken[clip.Id] = look;
            }
        }

        _staleLooks.Clear();
        Document.Markers = [.. Markers];
        Document.SequenceEffects = CaptureSequenceEffects();
        Document.SequenceEffectsBypassed = SequenceEffectsBypassed;
        (Document.Width, Document.Height, Document.UseVerticalResolution) = (Math.Max(FrameWidth, 2), Math.Max(FrameHeight, 2), UseVerticalResolution);
    }

    /// <summary>
    /// Classifies a view change: <see cref="LayerFields.Clip"/> fields changed outside <see cref="SyncViews"/> become clip edits;
    /// <see cref="LayerFields.View"/> fields are view-local; aliases count as the field they set; anything else marks the look dirty.
    /// Returns true for clip-owned fields.
    /// </summary>
    private bool NoteViewChange(Layer view, string? name)
    {
        if (_syncingViews || _applyingKeys || name is null)
            return false;
        var owner = LayerFields.Owner(name);
        if (owner is FieldOwner.View or FieldOwner.Alias)
            return false;
        if (owner != FieldOwner.Clip)
        {
            if (view.ClipId != Guid.Empty)
                _staleLooks.Add(view);
            return false;
        }

        if (_openEdit is null && ClipOf(view) is { } clip)
        {
            // During undo/redo and the binding settle, clip-field writes are binding echoes (a box re-rounding), not requests.
            if (IsRestoringTimeline)
                ShowClipAgain(view, clip);
            else
                TakeClipField(view, clip, name);
        }

        return true;
    }

    /// <summary>Clears the Sequence markers via a document transaction (no undo step); no-op when empty.</summary>
    private void ClearDocumentMarkers()
    {
        if (Document.Markers.Count > 0)
            Edit("clear the markers", edit => edit.SetMarkers([]), undoable: false);
    }

    /// <summary>Reloads clip-owned values into a view from the document; no edit.</summary>
    private void ShowClipAgain(Layer view, TimelineClip clip)
    {
        if (Document.TrackOf(clip) is not { } track)
            return;

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

    /// <summary>
    /// A view-only change of clip-owned values (a hover preview): <paramref name="show"/> sets them on <paramref name="view"/> as a sync
    /// does, so no clip edit, Undo step or save sees them; <see cref="ShowViewAgain"/> reloads the clip's values.
    /// </summary>
    internal void PreviewOnView(Layer view, Action<Layer> show)
    {
        var syncing = _syncingViews;
        _syncingViews = true;
        try
        {
            show(view);
        }
        finally
        {
            _syncingViews = syncing;
        }
    }

    /// <summary>Reloads a view's clip-owned values from the document as it holds them now (after an Undo too); no edit.</summary>
    internal void ShowViewAgain(Layer view)
    {
        if (ClipOf(view) is { } clip)
            ShowClipAgain(view, clip);
    }

    /// <summary>
    /// Whether a box's clip-field change takes its own undo step: not right after a manual Checkpoint, nor for the same field
    /// of the same clip within 1.5 s (a dragged box writes on every mouse move).
    /// </summary>
    private bool TakesUndoStep(Layer view, string name)
    {
        var now = Environment.TickCount64;
        var again = _gesture.Fields.Continues(view, name, now, MergeWindow, newest: null);
        return now - _checkpointedAt > HandStepGrace && !again;
    }

    /// <summary>Turns a clip-field write on a view into a clip edit (clamped by timeline rules); the view then shows the result.</summary>
    private void TakeClipField(Layer view, TimelineClip clip, string name)
    {
        // Ignored during undo/redo and its settle: no edit, no step, redo history preserved.
        if (IsRestoringTimeline)
        {
            ShowClipAgain(view, clip);
            return;
        }

        var undoable = TakesUndoStep(view, name);
        Edit($"change {view.Name}", edit =>
        {
            switch (name)
            {
                case nameof(Layer.StartTime):
                    edit.MoveClip(clip, Document.TrackOf(clip)!, view.StartTime);
                    break;
                case nameof(Layer.Duration):
                    // 0 is "to the end" (the Properties dialog's box): the end of the timeline.
                    edit.TrimClipEnd(clip, view.Duration > 0.001 ? clip.Start + view.Duration : ToTheEnd(clip.Start));
                    break;
                case nameof(Layer.MediaOffset):
                    edit.SlipClip(clip, view.MediaOffset - clip.MediaOffset);
                    break;
                case nameof(Layer.Speed):
                    edit.SetClipSpeed(clip, view.Speed);
                    break;
                case nameof(Layer.TransitionIn):
                    edit.SetCrossfade(clip, view.TransitionIn);
                    break;
                case nameof(Layer.IsHidden) or nameof(Layer.IsSoundMuted):
                    edit.SetClipFlags(clip, view.IsHidden, view.IsSoundMuted);
                    break;
                case nameof(Layer.AudioGainDb):
                    edit.SetClipGain(clip, view.AudioGainDb);
                    break;
                case nameof(Layer.AudioOffset):
                    edit.SetAudioLink(clip, clip.AudioLinked, view.AudioOffset);
                    break;
                case nameof(Layer.ProxyChoice):
                    edit.SetClipProxy(clip, view.ProxyChoice);
                    break;
                default:
                    edit.SetClipFades(clip, view.FadeInSeconds, view.FadeOutSeconds, view.FadeInStyle, view.FadeOutStyle);
                    break;
            }
        }, undoable);

        // Show the clip's resulting values (clamped, or unchanged if refused).
        if (Document.FindClip(clip.Id) is not null)
            ShowClipAgain(view, clip);
    }

    // ----- The document as the graph builder is given it -----

    /// <summary>
    /// Render snapshot: a copy of the document (own tracks/clips, shared immutable looks) after FlushLooks, at the target frame.
    /// The copy omits clips still probing (no size) and mutes audio with missing files. The builder caches by this immutable copy.
    /// </summary>
    private TimelineDocument TimelineForBuilder(int frameWidth, int frameHeight)
    {
        FlushLooks();
        var copy = Document.Clone();
        (copy.Width, copy.Height) = (Math.Max(frameWidth, 2), Math.Max(frameHeight, 2));
        foreach (var track in copy.Tracks)
        {
            track.Clips.RemoveAll(c => _views.TryGetValue(c.Id, out var view) && view.IsLoading);
            foreach (var clip in track.Clips)
            {
                if (clip.HasSound && clip.ContentKind is LayerKind.VideoFile or LayerKind.Audio && clip.SourcePath is { } file && !FileState.Exists(file.Trim().Trim('"')))
                    clip.IsMuted = true;
            }
        }

        return copy;
    }

    // ----- Sequence file clips -----
    // No clip is privileged (no main video). Edit points, snapping, chapters at cuts and I-frames come from every picture clip
    // (SequenceSpans). Natural size is TimelineDocument.NaturalWidth/Height, set by the first file; LocalMediaPath is an ordinary clip.

    /// <summary>Length of the sequence in seconds: to where its last clip ends (its sound included).</summary>
    public double SequenceSeconds => Document.TotalDuration;

    /// <summary>
    /// Visible top-level picture clips by start (SequenceSpans.PictureSpans): source for edit points, snapping, chapters,
    /// cut fractions and timeline I-frames.
    /// </summary>
    public IReadOnlyList<ClipSpan> PictureSpans() => SequenceSpans.PictureSpans(Document);

    /// <summary>The file last opened, cleaned as the spans name files; "" for none.</summary>
    private string OpenedFile => LocalMediaPath.Trim().Trim('"');

    /// <summary>
    /// Whether the last opened file is the entire Sequence untouched (SequenceSpans.WholeFiles), so its soft subtitles and
    /// anchored subtitle imports apply to the Sequence.
    /// </summary>
    private bool IsOpenedFileWhole => OpenedFile.Length > 0 && SequenceSpans.WholeFiles(Document).Contains(OpenedFile);

    /// <summary>The last-ending top-level clip of the last opened file (files opened with it append after it); null when absent.</summary>
    public Layer? OpenedFileView => Document.Tracks.SelectMany(t => t.Clips)
        .Where(c => c is { ParentClipId: null, ContentKind: LayerKind.VideoFile or LayerKind.Audio } && SameFile(c.SourcePath, OpenedFile))
        .OrderBy(c => c.End).Select(ViewOf).LastOrDefault(v => v is not null);

    // A Game Preset (or preset placement/look/Cutouts) chosen with no target clip; applied to the next file opened.
    private ClipTemplate? _templateToCome;

    // Previous file whose clips await replacement until the new file's length is known from its I-frames (unfinished
    // recordings; SetSourceIFrames). Null when nothing is pending.
    private string? _openedOver;

    // Clip placed for the last opened file (receives its automatic preset); null when none.
    private Guid? _openedClip;

    /// <summary>
    /// Places the opened file like a drop: a whole-file clip (audio-only without video), fitted per Settings, blur fill per the
    /// Blur Fill row, audio streams on their own Sub-Lanes (UnpackStreams); Track 1 at 0 on an empty Sequence.
    /// Replaces the previous file's clips in place (inheriting look, placement and crop; Cutouts re-parented, their stream audio
    /// dropped). On an otherwise empty Sequence it sets frame rate and natural size; run-to-end clips are extended and a pending
    /// <see cref="_templateToCome"/> applied. No undo step.
    /// </summary>
    /// <param name="previous">The previously opened file ("" for none) whose clips are replaced.</param>
    internal void PlaceOpenedFile(string previous)
    {
        var path = OpenedFile;
        var length = MainMediaSeconds;
        if (path.Length == 0 || _mediaInfo is not { } info || length <= 0.05)
        {
            _openedOver = path.Length > 0 && _mediaInfo is not null ? previous : null;
            return;
        }

        _openedOver = null;
        var before = previous.Trim().Trim('"');
        var old = before.Length == 0 ? [] : Document.Tracks.Where(t => !t.IsLocked).SelectMany(t => t.Clips
            .Where(c => c is { ParentClipId: null, ContentKind: LayerKind.VideoFile or LayerKind.Audio } && SameFile(c.SourcePath, before))
            .Select(c => (Track: t, Clip: c))).ToList();
        // Empty except for the replaced clips and their children: treat as the first file opened.
        var oldIds = old.Select(o => o.Clip.Id).ToHashSet();
        var empty = !Document.Tracks.Any(t => t.Clips.Exists(c => !oldIds.Contains(c.Id) && !(c.ParentClipId is { } parent && oldIds.Contains(parent))));
        var picture = info.Video is { Width: > 0, Height: > 0 };
        var kind = picture ? LayerKind.VideoFile : LayerKind.Audio;

        // Sequence takes the first file's frame rate and cropped shape, set outside any transaction like other document-level
        // values. Explicit New Project choices (ExplicitFrame, ExplicitFrameRate) are kept; the colour space follows the file.
        if (empty)
        {
            var rate = Document.ExplicitFrameRate ? Document.FrameRate : Models.Timeline.FrameRate.OfSource(SourceFrameRate);
            var (width, height) = picture && !Document.ExplicitFrame ? CroppedSize(BoxCrop(), SourceWidth, SourceHeight) : (Document.NaturalWidth, Document.NaturalHeight);
            Edit("take the first file's frame", edit =>
            {
                edit.SetFrameRate(rate, Document.ExplicitFrameRate);
                edit.SetFrame(width, height, Document.ExplicitFrame);
            }, undoable: false);
            TakeProjectSettingsFrom(info);
            NoteDocumentShape();
        }

        // Look: the replaced clip's (minus its timing-bound keyframes) at its placement, else a new fitted look.
        var name = System.IO.Path.GetFileName(path);
        var replaced = old.Count > 0 ? ViewOf(old[0].Clip) : null;
        Layer view;
        if (old.Count > 0)
        {
            var look = TimelineTransaction.CopyOf(old[0].Clip.State);
            (look.KeysX, look.KeysY, look.KeysScale, look.KeysRotation, look.KeysOpacity) = (null, null, null, null, null);
            (look.Kind, look.Name, look.ImagePath, look.ImageWidth, look.ImageHeight) = (kind, name, path, info.Video?.Width ?? 0, info.Video?.Height ?? 0);
            (look.StartTime, look.Duration, look.MediaOffset, look.Speed, look.MediaDuration) = (0, length, 0, 1, length);
            look.HasAudio = info.Audio.Count > 0;
            view = Layer.FromState(look, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            if (picture && replaced is not null && FrameWidth > 0 && FrameHeight > 0)
            {
                var (x, y, width, height) = replaced.GetOutputRect(FrameWidth, FrameHeight, SourceWidth, SourceHeight);
                PlaceAbout(view, replaced.SizeWidth, (x + width / 2.0) / FrameWidth, (y + height / 2.0) / FrameHeight);
                if (view.GetOutputRect(FrameWidth, FrameHeight, SourceWidth, SourceHeight).Height > FrameHeight + 2)
                    FitToFrame(view, AppSettings.Current.NewClipsFill);
            }
        }
        else
        {
            view = new Layer
            {
                Kind = kind,
                Name = name,
                ImagePath = path,
                ImageWidth = info.Video?.Width ?? 0,
                ImageHeight = info.Video?.Height ?? 0,
                HasAudio = info.Audio.Count > 0,
                MediaDuration = length,
            };

            if (picture)
            {
                if (empty)
                    (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom) = BoxCrop();
                FitToFrame(view, AppSettings.Current.NewClipsFill);

                // Auto blur fill only when the clip leaves the frame partly bare (16:9 in 9:16) and is the Sequence's first
                // picture. A pending Game Preset may still enable its own blur (PutTemplateOn, below).
                view.BlurFill = !_backgroundRow.IsHidden && empty && LeavesFrameUncovered(view) ? true : null;
            }
        }

        DescribeSound(view, info);
        TimelineClip? made = null;
        Edit($"open {name}", edit =>
        {
            var trackKind = picture ? TimelineTrackKind.Video : TimelineTrackKind.Audio;
            var track = old.Count > 0 ? old[0].Track
                : Document.Tracks.FirstOrDefault(t => t.Kind == trackKind && t.Clips.Count == 0 && !t.IsLocked && t.ParentTrackId is null);
            var oldEnd = Document.TotalDuration;
            var toTheEnd = Document.Tracks.Where(t => !t.IsLocked).SelectMany(t => t.Clips)
                .Where(c => c.ContentKind is LayerKind.Image or LayerKind.Text or LayerKind.Adjustment && Math.Abs(c.End - oldEnd) < 0.0005)
                .ToList();

            // Remove the old clips but keep their children (Game Preset Cutouts); they are re-parented to the new clip below.
            var oldEnds = old.ToDictionary(o => o.Clip.Id, o => o.Clip.End);
            var children = Document.Tracks.Where(t => !t.IsLocked)
                .SelectMany(t => t.Clips.Where(c => c.ParentClipId is { } parent && oldEnds.ContainsKey(parent))
                    .Select(c => (Track: t, Clip: c, RanToEnd: Math.Abs(c.End - oldEnds[c.ParentClipId!.Value]) < 0.0005))).ToList();
            foreach (var (_, clip) in old)
                edit.DeleteClip(clip, withChildren: false);
            if (track is not null && (!Document.Tracks.Contains(track) || track.IsLocked))
                track = null;

            var pictureEnd = VideoSeconds(info, length);
            var clipOfFile = NewClipFor(view, 0, pictureEnd);
            clipOfFile.PictureDuration = PictureDurationOf(pictureEnd, clipOfFile.SourceDuration);
            Adopt(view, clipOfFile);
            TimelineClip placed;
            try
            {
                placed = edit.PlaceClip(track ?? edit.InsertTrack(picture ? 0 : Document.Tracks.Count, trackKind), clipOfFile, allowCrossfadeOverlap: false);
            }
            catch (TimelineEditException) when (track is not null)
            {
                placed = edit.PlaceClip(edit.InsertTrack(picture ? 0 : Document.Tracks.Count, trackKind), clipOfFile, allowCrossfadeOverlap: false);
            }

            made = placed;
            _openedClip = placed.Id;
            foreach (var clip in toTheEnd.Where(c => c.Start < placed.End - TimelineDocument.MinimumClipSeconds && Math.Abs(c.End - placed.End) > 0.0005))
            {
                try
                {
                    edit.TrimClipEnd(clip, placed.End);
                }
                catch (TimelineEditException)
                {
                    // A following clip on the track: keep the current end.
                }
            }

            foreach (var (lane, child, ranToEnd) in children)
                Repoint(edit, lane, child, ranToEnd, placed, view, before);
        }, undoable: false);

        if (made is null)
            return;

        if (!_isBackgroundWorker)
            _ = LoadLayerPicturesAsync(view);
        if (picture)
            UnpackStreams(view, info, undoable: false);

        // A Game Preset or a preset chosen before there was a clip to take it.
        if (old.Count == 0 && picture && _templateToCome is { } template)
        {
            _templateToCome = null;
            using var held = HoldLookSteps();
            PutTemplateOn(made.Id, template, $"put the Game Preset on {name}");
        }

        // Side-by-side layout is offered, never applied automatically.
        if (picture)
            OfferSideBySide(view);
    }

    /// <summary>
    /// Whether the picture's current layout leaves part of the frame uncovered (e.g. 16:9 fitted into 9:16), the condition for
    /// auto blur fill. One pixel of slack covers even-size rounding.
    /// </summary>
    private bool LeavesFrameUncovered(Layer view)
    {
        var (frameWidth, frameHeight) = (FrameWidth, FrameHeight);
        if (frameWidth <= 0 || frameHeight <= 0 || view.ImageWidth <= 0 || view.ImageHeight <= 0)
            return false;
        var (x, y, width, height) = view.GetOutputRect(frameWidth, frameHeight, view.ImageWidth, view.ImageHeight);
        return x > 1 || y > 1 || x + width < frameWidth - 1 || y + height < frameHeight - 1;
    }

    /// <summary>
    /// Initial length of a whole-file clip with no trailing black frame: the probed video-stream length (containers often pad
    /// audio past it), capped at the container length; else the container length. The file length stays the container's so
    /// trims can reach the tail. Bounded to the last Sequence frame inside the picture (placement rounds to nearest).
    /// </summary>
    private double VideoSeconds(MediaInfo info, double container)
    {
        if (info.Video is not { Width: > 0, Height: > 0, DurationSeconds: > 0.05 } video || video.DurationSeconds >= container)
            return container;

        // Probed lengths are rounded (microseconds, Matroska milliseconds) and can fall just short of the last frame: within ~2 ms
        // of a boundary counts as reaching it. Slight overshoot is safe (export samples <= half a frame before the end,
        // GpuExportTiming.SampleLimitSeconds).
        var rate = Document.FrameRate;
        var fps = rate.IsValid ? (double)rate.Numerator / rate.Denominator : 0;
        var frames = fps > 0 ? Math.Floor(video.DurationSeconds * fps + Math.Max(0.002 * fps, 1e-6)) : 0;
        return frames > 0 ? Math.Min(rate.SecondsFromFrame((long)frames), container) : video.DurationSeconds;
    }

    /// <summary>
    /// <see cref="TimelineClip.PictureDuration"/> for a whole-file clip: <see cref="VideoSeconds"/> when shorter than the file
    /// (so whole-file checks recognise the untouched clip), else 0.
    /// </summary>
    private static double PictureDurationOf(double pictureEnd, double sourceDuration) =>
        pictureEnd > 0.05 && sourceDuration > 0 && pictureEnd < sourceDuration - 0.0005 ? pictureEnd : 0;

    /// <summary>
    /// Re-parents a child of a replaced clip to the newly opened file's clip: the old file's stream audio is dropped (the new
    /// file has its own Sub-Lanes); Cutouts are re-sourced to the new picture and clamped to its span (removed if outside it);
    /// other children re-parent where they overlap the new clip and detach otherwise.
    /// </summary>
    private void Repoint(ITimelineTransaction edit, TimelineTrackModel lane, TimelineClip child, bool ranToEnd, TimelineClip made, Layer view, string before)
    {
        var inside = child.Start >= made.Start - 0.0005 && child.End <= made.End + 0.0005;
        var ofOld = SameFile(child.SourcePath, before);
        if (ofOld && child.ContentKind == LayerKind.Audio)
        {
            edit.DeleteClip(child);
            return;
        }

        if (!(ofOld && child.ContentKind == LayerKind.VideoFile))
        {
            edit.SetClipParent(child, inside ? made.Id : null);
            return;
        }

        var start = Math.Max(child.Start, made.Start);
        var end = ranToEnd ? made.End : Math.Min(child.End, made.End);
        if (end - start < TimelineDocument.MinimumClipSeconds)
        {
            edit.DeleteClip(child, withChildren: false);
            return;
        }

        // Recreate with a new id so its view rebuilds for the new file.
        var piece = CopyOfClip(child);
        (piece.Id, piece.ParentClipId, piece.SourcePath, piece.Start, piece.End) = (Guid.NewGuid(), null, made.SourcePath, start, end);
        (piece.MediaOffset, piece.Speed, piece.SourceDuration, piece.PictureDuration) = (made.MediaOffset + (start - made.Start) * made.Speed, made.Speed, made.SourceDuration, made.PictureDuration);
        piece.State = OnClockOf(TimelineTransaction.CopyOf(child.State), view, made, start);
        (piece.State.StartTime, piece.State.Duration) = (start, end - start);
        edit.DeleteClip(child, withChildren: false);
        edit.SetClipParent(edit.PlaceClip(lane, piece, allowCrossfadeOverlap: false), made.Id);
    }

    /// <summary>Whether a clip's file is a file named (both cleaned, the case ignored).</summary>
    private static bool SameFile(string? path, string file) =>
        file.Length > 0 && string.Equals((path ?? "").Trim().Trim('"'), file.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Places a picture by width and centre (frame fractions) at its locked, cropped aspect: a template's or replaced clip's placement.
    /// </summary>
    private void PlaceAbout(Layer view, double width, double middleX, double middleY)
    {
        if (ShapeOf(view) is not (> 0 and var tall) || FrameWidth <= 0 || FrameHeight <= 0)
            return;

        var (left, top, right, bottom) = view.CropFractions;
        var height = width * FrameWidth * tall * (1 - top - bottom) / (1 - left - right) / FrameHeight;
        view.LockAspectRatio = true;
        view.FromCorner(() =>
        {
            view.SizeWidth = width;
            view.PositionX = middleX - width / 2;
            view.PositionY = middleY - height / 2;
        });
    }

    /// <summary>Applies a look to one clip, preserving its timing, switches and crop (preset crops go through ApplyPresetCrop).</summary>
    private void TakeLook(Layer view, TimelineClip clip, TimelineTrackModel track, LayerState? look)
    {
        _syncingViews = true;
        try
        {
            var crop = (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom);
            var speedOptions = (view.SmoothSlowMo, view.KeepPitch, view.SpectralAudio);
            view.ApplyLook(look);
            (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom) = crop;
            (view.SmoothSlowMo, view.KeepPitch, view.SpectralAudio) = speedOptions;
            TakeClip(view, clip, track);
        }
        finally
        {
            _syncingViews = false;
        }

        _staleLooks.Add(view);
    }

    // ----- Sequence natural size -----

    // The natural size and whether the document holds any clip, as last told (NoteDocumentShape).
    private (int Width, int Height, bool Filled) _documentShown;

    /// <summary>
    /// Raises dependents of the natural size (frame sides, completed size boxes) and <see cref="HasSource"/> when either changed
    /// after an edit, undo step or document install.
    /// </summary>
    private void NoteDocumentShape()
    {
        // Master fader mirrors the Sequence gain after edits, undo and project open.
        NoteMasterGain();
        ApplySequenceRate();

        var now = (Document.NaturalWidth, Document.NaturalHeight, Document.Tracks.Any(t => t.Clips.Count > 0));
        if (now == _documentShown)
            return;

        var reshaped = (now.NaturalWidth, now.NaturalHeight) != (_documentShown.Width, _documentShown.Height);
        _documentShown = now;
        if (reshaped)
        {
            OnPropertyChanged(nameof(FrameWidth));
            OnPropertyChanged(nameof(FrameHeight));
            RelinkDimensions();
        }

        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(ShowsCopyNote));
    }

    // ----- Centre-preserving reshape -----
    // Placement is stored as corner + width in frame fractions, so a reshape would drift the centre. On reshape each picture keeps
    // its width and moves its corner to preserve its centre. The cached centre is reused while unchanged (intermediate sizes
    // while typing lose nothing); otherwise it is recomputed for the frame it was placed in.

    // Set during derived placement (reshape, template placement): records no keyframe and takes no undo step.
    private bool _placingViews;

    // Per picture: the frame it was last anchored to, its centre there (fractions), and its corner and width.
    internal readonly record struct PicturePlacement((int Width, int Height) Frame, double MiddleX, double MiddleY,
        (double SizeWidth, double PositionX, double PositionY) Wrote);

    private readonly Dictionary<Layer, PicturePlacement> _placedFor = new(ReferenceEqualityComparer.Instance);

    /// <summary>Re-anchors every picture about its centre for the new frame shape; unseen pictures are assumed placed for the current frame.</summary>
    private void KeepPlacesOnReshape()
    {
        if (_placingViews || _applyingKeys || FrameWidth <= 0 || FrameHeight <= 0)
            return;

        var now = (Width: FrameWidth, Height: FrameHeight);

        // The command is built once more after all of them are moved, not once for each.
        CommandDeferral? deferred = null;
        _placingViews = true;
        try
        {
            // Iterate a copy: view change handlers may commit an edit that rebuilds the views.
            foreach (var view in _views.Values.ToArray())
            {
                if (!view.HasPlacement || view.IsCaptions || view.IsLoading)
                    continue;

                var place = (view.SizeWidth, view.PositionX, view.PositionY);
                if (!_placedFor.TryGetValue(view, out var was))
                {
                    var (x, y) = MiddleOf(view, now);
                    _placedFor[view] = new PicturePlacement(now, x, y, place);
                    continue;
                }

                if (was.Frame == now)
                    continue;

                var (middleX, middleY) = was.Wrote == place ? (was.MiddleX, was.MiddleY) : MiddleOf(view, was.Frame);
                var (_, _, width, height) = view.GetOutputRect(now.Width, now.Height, SourceWidth, SourceHeight);
                var corner = (Math.Round(middleX * now.Width - width / 2.0) / now.Width, Math.Round(middleY * now.Height - height / 2.0) / now.Height);
                if ((view.PositionX, view.PositionY) != corner)
                {
                    deferred ??= DeferCommand();
                    (view.PositionX, view.PositionY) = corner;
                    if (view.ClipId != Guid.Empty)
                        _staleLooks.Add(view);
                }

                _placedFor[view] = new PicturePlacement(now, middleX, middleY, (view.SizeWidth, view.PositionX, view.PositionY));
            }
        }
        finally
        {
            _placingViews = false;
            deferred?.Dispose();
        }
    }

    /// <summary>A picture's centre in frame fractions on a frame of the given size, with height derived from width and aspect.</summary>
    private (double X, double Y) MiddleOf(Layer view, (int Width, int Height) frame)
    {
        var (_, _, width, height) = view.GetOutputRect(Math.Max(frame.Width, 2), Math.Max(frame.Height, 2), SourceWidth, SourceHeight);
        return (view.PositionX + width / 2.0 / Math.Max(frame.Width, 2), view.PositionY + height / 2.0 / Math.Max(frame.Height, 2));
    }

    // ----- Crop target -----
    // The Crop boxes (percent) and Interactive Crop edit one clip: the selected picture clip, else the lowest visible top-level
    // video at the playhead (or the first). UI-only: nothing renders from the boxes; with no target they hold values for the next file.

    // The clip the Crop boxes were last taken from, and whether a drag holds them to it.
    private Layer? _cropShown;
    private bool _cropHeld, _watchingCropTarget;

    /// <summary>
    /// The clip the Crop boxes and Interactive Crop edit (see section); null when none. Pinned during a drag so playback
    /// crossing into another clip cannot switch it mid-drag.
    /// </summary>
    public Layer? CropTarget =>
        _cropHeld && _cropShown is { ClipId: var heldId } held && heldId != Guid.Empty && _views.TryGetValue(heldId, out var heldView) && ReferenceEquals(heldView, held)
            ? held
            : KeyLayer is { IsFile: true, ClipId: var id } selected && id != Guid.Empty && _views.TryGetValue(id, out var view) && ReferenceEquals(view, selected)
                ? selected
                : VideoViewAt(PositionMs / 1000) ?? FirstVideoView();

    /// <summary>
    /// Clip whose raw source the player shows during Interactive Crop or Draw Target (GpuPreviewEngine.ShowSource): the Draw Target
    /// video, else the selected or drag-pinned clip; empty to follow the bottom video at the playhead.
    /// </summary>
    public Guid ShownSourceClip => DrawTargetLayer is { } piece && ClipOf(piece) is { ParentClipId: { } parent } ? parent
        : _cropHeld || KeyLayer is { IsFile: true } ? CropTarget?.ClipId ?? Guid.Empty
        : Guid.Empty;

    /// <summary>Pins CropTarget during a drag (Canvas box, gizmo, slider) or releases it to resolve normally again.</summary>
    public void HoldCropTarget(bool hold)
    {
        _cropHeld = hold;
        if (!hold && !ReferenceEquals(CropTarget, _cropShown))
            ShowCropTarget();
    }

    /// <summary>
    /// Loads <see cref="CropTarget"/>'s crop into the boxes without writing any clip; called after timeline changes and when
    /// the target changes.
    /// </summary>
    private void ShowCropTarget()
    {
        if (!_watchingCropTarget)
        {
            // Selection and playhead changes may change the target.
            _watchingCropTarget = true;
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ActiveKeyLayer) or nameof(PositionMs) && !_placingViews && !_showingTarget && !ReferenceEquals(CropTarget, _cropShown))
                    ShowCropTarget();
            };
        }

        var target = CropTarget;
        _cropShown = target;
        if (target is null)
            return;

        var crop = CropOf(target);
        using var once = DeferCommand();
        _showingTarget = true;
        try
        {
            if (BoxCrop() != crop)
                (CropLeft, CropTop, CropRight, CropBottom) = (Math.Round(crop.Left * 100, 2), Math.Round(crop.Top * 100, 2), Math.Round(crop.Right * 100, 2), Math.Round(crop.Bottom * 100, 2));
        }
        finally
        {
            _showingTarget = false;
        }
    }

    /// <summary>The lowest visible top-level video clip at a time, or null; evaluated on every playhead move.</summary>
    private Layer? VideoViewAt(double seconds)
    {
        foreach (var track in Document.Tracks)
        {
            if (track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Switch) || track.IsHidden)
                continue;

            foreach (var clip in track.Clips)
            {
                if (clip is { ContentKind: LayerKind.VideoFile, ParentClipId: null, IsHidden: false } && clip.Start <= seconds + 0.0005 && clip.End > seconds
                    && _views.TryGetValue(clip.Id, out var view))
                {
                    return view;
                }
            }
        }

        return null;
    }

    /// <summary>The view of the first top-level video clip (the lowest track's), or null while there is none.</summary>
    private Layer? FirstVideoView()
    {
        foreach (var track in Document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip is { ContentKind: LayerKind.VideoFile, ParentClipId: null } && _views.TryGetValue(clip.Id, out var view))
                    return view;
            }
        }

        return null;
    }

    // Overrides preset targeting while set: the just-opened file's clip receives its automatic preset.
    private Guid? _templateTargetOnly;

    /// <summary>
    /// A preset saved with the Background switch off removes blur fill from its targets (legacy behaviour); one saved with it
    /// on enables blur fill only together with its layout (PutTemplateOn).
    /// </summary>
    private void TakeBlurFill(bool hidden)
    {
        if (!hidden)
            return;
        foreach (var view in TemplateTargets().Where(v => v.BlurFill == true))
        {
            view.BlurFill = null;
            _staleLooks.Add(view);
        }
    }

    /// <summary>
    /// Target video clips for a Game Preset layout or preset placement/crop: the Timeline selection (a selected Cutout maps to
    /// its clip), else every top-level video clip.
    /// </summary>
    private IReadOnlyList<Layer> TemplateTargets()
    {
        if (_templateTargetOnly is { } only)
            return Document.FindClip(only) is { } clip && ViewOf(clip) is { } view ? [view] : [];
        if (TimelineSelection?.Invoke() is { Count: > 0 } selected && GamePresetTargets(selected) is { Count: > 0 } chosen)
            return chosen;

        return Document.Tracks.SelectMany(t => t.Clips).Where(c => c is { ContentKind: LayerKind.VideoFile, ParentClipId: null })
            .Select(ViewOf).OfType<Layer>().ToList();
    }
}
