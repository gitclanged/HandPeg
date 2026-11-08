using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.ViewModels;

// ----- The timeline is a TimelineDocument (4.3C) -----
// The view model's timeline is one document: its tracks bottom first, the clips on them (5.2-AuditB: every video is a clip
// of its own file on any track, the file last opened as well, and no track is privileged), its markers and the Sequence
// Output's effects. Nothing else says where a clip is. Every change to where clips are, how long they last and what of
// their files they play, to their fades, crossfades, gain, speed, switches and tracks is an edit of the document made in
// one transaction (Edit), which Undo keeps one step of and after which the rows are brought into line (OnDocumentCommitted).
//
// What the window works on is a Layer for each clip, its view: the Layers tab's rows and blocks, the Canvas's boxes, the
// Effects and Keyframes panes all use it as they used a 4.2.0 layer, and a row is a track, its first clip standing for it.
// A view's timing and switches are its clip's, written into it by SyncViews and nowhere else; a change made to them any
// other way (a box bound to one) is taken as a request and made an edit of the clip (TakeClipField). A view's look (its
// place, size, filters, keyframes, effects, name) goes the other way: it is changed on the view, which is then the newer of
// the two, and written into its clip as a new LayerState (FlushLooks; a look is never changed in place) before anything
// reads the document: an edit, an undo step, a graph, a save. A track's clips share their look as in 4.2.0 (OnLayerChanged
// copies it along the track), so every clip's own State holds all of it and no track carries a TrackLook.
public partial class MainViewModel
{
    /// <summary>The timeline: every track and clip, its markers, the Sequence Output's effects. See the section above.</summary>
    public TimelineDocument Document { get; private set; } = new();

    // Each clip's view, by the clip's id; the look a view last took from its clip (taken again only when the clip has
    // another one: an edit moved its keys, or an undo step put an older one back); each track's number as its views carry
    // it in Layer.TrackId (given once, never reused in a session), and the stand-in view of a track with no clips.
    private readonly Dictionary<Guid, Layer> _views = [];
    private readonly Dictionary<Guid, LayerState> _taken = [];
    private readonly Dictionary<Guid, int> _trackNumbers = [];
    private readonly Dictionary<int, TimelineTrackModel> _tracksByNumber = [];
    private readonly Dictionary<Guid, Layer> _emptyTrackViews = [];
    private int _lastTrackNumber;

    // Views whose look is newer than their clip's.
    private readonly HashSet<Layer> _staleLooks = new(ReferenceEqualityComparer.Instance);

    // How deep the views are being written from the document (SyncViews, a clip's values shown again, a look put on the main
    // video): what they are told then is the document's, not a request. A depth (4.4-BugHunt), so that a sync inside another
    // (an edit a handler commits while the views are written) cannot end the outer one early.
    private int _syncDepth;

    /// <summary>
    /// Whether the views are being written from the document. Set true as a sync starts and false as it ends (in a finally),
    /// which goes one deeper and one back out: true while any sync is under way.
    /// </summary>
    private bool _syncingViews
    {
        get => _syncDepth > 0;
        set => _syncDepth = Math.Max(_syncDepth + (value ? 1 : -1), 0);
    }

    // The edit under way (Edit), and what the timeline was before it, for its undo step.
    private ITimelineTransaction? _openEdit;
    private TimelineSnapshot? _beforeEdit;

    /// <summary>When the last undo step was taken by hand (Checkpoint), so a box's change right after it makes no second one.</summary>
    private long _checkpointedAt;

    /// <summary>The last of a clip's values set on its view (which view, which value, when): a box dragged makes one undo step.</summary>
    private (Layer View, string Name, long At)? _lastFieldEdit;

    /// <summary>A timeline with nothing on it, of the frame and rate the settings give now.</summary>
    private TimelineDocument NewDocument() => new()
    {
        Width = Math.Max(FrameWidth, 2),
        Height = Math.Max(FrameHeight, 2),
        UseVerticalResolution = UseVerticalResolution,
        FrameRate = Models.Timeline.FrameRate.OfSource(SourceFrameRate),
    };

    /// <summary>
    /// Makes a document the timeline (a new project, an undo step, a project opened): its views are made afresh (the
    /// selection following its clip into its new view), its markers and the Sequence Output's effects are taken into the
    /// panes that edit them. No undo step of its own. A restore (4.4-BugHunt): nothing it sets going takes a step or makes an
    /// edit, and the captions are projected once, as the outermost restore ends (<see cref="EndRestore"/>).
    /// </summary>
    private void InstallDocument(TimelineDocument document)
    {
        using var restoring = BeginRestore();
        Document.Committed -= OnDocumentCommitted;
        Document = document;
        Document.Committed += OnDocumentCommitted;

        using (DeferCommand())
        {
            SyncViews(afresh: true);
            FollowSelection();
            SetMarkers(document.Markers);
            SetSequenceEffects(document.SequenceEffects, document.SequenceEffectsBypassed);
            AudioLinked = AllAudioLinked();
            NoteDocumentShape();
            _previewState = null;
            RefreshLayerRows();
            TimelineChanged?.Invoke();
            GenerateCommand();
        }

        // The captions follow the clips that play their source (4.4): projected once the anchors put back are in place too.
        _reprojectPending = true;
    }

    // ----- Finding a clip's view, a view's clip, a row's track -----

    /// <summary>The clip a view is the view of; null for one that is none (a stand-in: the caption box, the background, an empty track).</summary>
    public TimelineClip? ClipOf(Layer? view) => view is { ClipId: var id } && id != Guid.Empty ? Document.FindClip(id) : null;

    /// <summary>
    /// Whether a layer's picture is drawn at a moment: never a clip hidden, on a hidden track (a view says only its own switch), or
    /// see-through there. What is not drawn cannot be clicked or dragged, in the player or on the Canvas.
    /// </summary>
    public bool IsPictureDrawn(Layer layer, double seconds)
    {
        if (layer.IsHidden || (ClipOf(layer) is { } clip && (clip.IsHidden || Document.TrackOf(clip) is { IsHidden: true })))
            return false;

        // Opacity is in percent: 0.1 is one thousandth of the picture.
        return layer.Evaluate(KeyProperty.Opacity, seconds - layer.StartTime) > 0.1;
    }

    /// <summary>The track a row stands for: its clip's, or the empty track's; null for the stand-ins.</summary>
    /// <summary>Gives a track's row a height of its own on the Timeline, or the one Track Height again with null (one step of Undo; saved with the project).</summary>
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

    /// <summary>Where a new picture track goes to be on top of the pictures: after the last track that is not a sound's.</summary>
    private int TopPictureIndex()
    {
        var index = Document.Tracks.FindLastIndex(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle));
        return index + 1;
    }

    /// <summary>
    /// Whether every clip with sound has it linked to its picture (the Layers tab's one switch for them all); as the switch
    /// is while no clip has sound (it is then what the next one is given).
    /// </summary>
    private bool AllAudioLinked()
    {
        var sounds = Document.Tracks.SelectMany(t => t.Clips).Where(c => c.HasSound).ToList();
        return sounds.Count == 0 ? AudioLinked : sounds.All(c => c.AudioLinked);
    }

    // ----- Edits -----

    /// <summary>
    /// Makes one change to the timeline, as one transaction: the change is made through <paramref name="change"/>'s
    /// transaction, a track it leaves with no clips goes (a row lasts as long as it has clips, as in 4.2.0; an empty track
    /// made as one stays), and on commit Undo keeps a step of it (named <paramref name="label"/>) and the rows follow
    /// (<see cref="OnDocumentCommitted"/>). A change the timeline refuses (<see cref="TimelineEditException"/>) changes
    /// nothing and says why in the status bar; returns false then. One made inside another folds into it. Since 4.4-BugHunt
    /// no exception escapes it: a fault in the change (never the user's doing) is rolled back with it, written down and said,
    /// and returns false; and none is made while a document is being put in place (an undo step, a project opened).
    /// </summary>
    /// <param name="undoable">False for what is not the user's doing (a file's length learned after it was dropped): no undo step.</param>
    /// <param name="removeEmptied">False to keep a track the change leaves with no clips (an empty track given back).</param>
    private bool Edit(string label, Action<ITimelineTransaction> change, bool undoable = true, bool removeEmptied = true)
    {
        if (_openEdit is { } open)
        {
            change(open);
            return true;
        }

        // What asks for an edit while a document is being put in place is the restore itself, or something it set going: an
        // edit would land in the middle of it (and on a document about to be another).
        if (_restoring > 0)
        {
            AppLog.Write($"Edit \"{label}\" refused while a step was being put back");
            return false;
        }

        _editCommitted = false;
        try
        {
            FlushLooks();
            var before = undoable && !_isBackgroundWorker ? CaptureSnapshot() : null;
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

    // Whether the edit under way has been committed (OnDocumentCommitted ran): a fault after that is not one that changed nothing.
    private bool _editCommitted;

    /// <summary>
    /// A change committed: an undo step of what was before it, the views brought into line, the graphs built again. Raised
    /// inside the transaction's Commit, so nothing escapes it (4.4-BugHunt): a fault in bringing the views into line is
    /// written down and the views are made afresh from the document; the edit keeps its undo step all the same.
    /// </summary>
    private void OnDocumentCommitted(TimelineCommit commit)
    {
        // The edit is over: what is done from here on (an edit a handler makes) is an edit of its own. Undo waits until its
        // step is taken (_committing), or the step would land on the history after the one Undo moved.
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
                    TimelineChanged?.Invoke();
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

        // The captions follow the clips that play their source (4.4).
        try
        {
            ReprojectCaptions();
        }
        catch (Exception ex)
        {
            NoteFault("edit", $"Showing the captions after \"{commit.Label}\"", $"captions after {NoteOf(commit.Label)}", ex);
        }
    }

    // How many commits are bringing the views into line and taking their steps (OnDocumentCommitted): Undo waits for them.
    private int _committing;

    /// <summary>
    /// The views made afresh from the document after they could not be brought into line: as a document put in place is
    /// (the selection following its clip, the window told once by <see cref="TimelineRestored"/>). A fault in that too is
    /// written down: the document is what renders and is saved, whatever its views show.
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
                TimelineChanged?.Invoke();
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
    /// Brings <see cref="Layers"/> into line with the document: a view for each clip, in stacking order (the pictures
    /// track by track from the bottom, then the sounds), each track's clips in time, a stand-in for a track with no clips;
    /// views of clips that are gone let go of. A view keeps being the same object for as long as its clip is there, so what
    /// holds it (a selection, a pane) keeps it; <paramref name="afresh"/> makes every view anew (a document put in place).
    /// </summary>
    private void SyncViews(bool afresh = false)
    {
        if (afresh)
        {
            foreach (var view in _views.Values.Concat(_emptyTrackViews.Values).ToArray())
                Unhook(view);
            _views.Clear();
            _emptyTrackViews.Clear();
            _taken.Clear();
            _staleLooks.Clear();
            _placedFor.Clear();
            _cropShown = null;
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

        // Into the list in that order, moving as little as can be (each move is a redraw of the rows).
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = i < Layers.Count && ReferenceEquals(Layers[i], wanted[i]) ? i : Layers.IndexOf(wanted[i]);
            if (at == i)
                continue;
            if (at < 0)
                Layers.Insert(i, wanted[i]);
            else
                Layers.Move(at, i);
        }

        while (Layers.Count > wanted.Count)
            Layers.RemoveAt(Layers.Count - 1);

        if (DrawTargetLayer is { } target && !Layers.Contains(target))
            DrawTargetLayer = null;
        RefreshAnyKeys();

        // The Crop boxes show the clip they stand for as the document has it now (an undo step puts back its crop, 4.4).
        ShowCropTarget();
    }

    /// <summary>A clip's view: the one it has, its look taken again when the clip has another; or a new one made from its look.</summary>
    private Layer ViewFor(TimelineClip clip, TimelineTrackModel track)
    {
        if (_views.TryGetValue(clip.Id, out var view))
        {
            if (!ReferenceEquals(_taken.GetValueOrDefault(clip.Id), clip.State))
            {
                // Its keys moved by an edit (a trim of its start, a split, a speed), or an older look put back, or a place
                // given to it (4.4: its place, crop and blur fill are its own, so an edit or an undo step can change them).
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

            // A clip made from another (a split's second half): kept in place by the middle that one was kept by, while its
            // corner, width and crop are that one's, so a frame of another shape keeps the two one look.
            if (_placedFor.FirstOrDefault(p => p.Value.Wrote == (view.SizeWidth, view.PositionX, view.PositionY) && CropOf(p.Key) == CropOf(view)
                    && p.Key.ImagePath == view.ImagePath) is { Key: not null } same)
            {
                _placedFor[view] = same.Value;
            }

            // A video whose file was still being looked at when this clip was kept (an undo step, a save taken during a
            // drop): looked at again, its length and sound filled in, its place kept (4.2.0's SetLayers did the same).
            if (view is { IsVideoFile: true } && (view.ImageWidth <= 0 || clip.SourceDuration <= 0) && !_isBackgroundWorker)
            {
                view.IsLoading = true;
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

    /// <summary>A clip's look as a layer is made from: its State, with the kind and the file the clip itself says.</summary>
    private static LayerState LookOf(TimelineClip clip)
    {
        var look = clip.State.ShallowCopy();
        look.Kind = clip.ContentKind;
        if (clip.SourcePath is { Length: > 0 } path && clip.ContentKind is LayerKind.VideoFile or LayerKind.Image or LayerKind.Audio)
            look.ImagePath = path;
        return look;
    }

    /// <summary>
    /// Writes a clip's own values (its timing, fades, switches, gain, slip, proxy, track) into its view. A picture is hidden by
    /// its track's switch (and its parents'), which its views show as their own.
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

    /// <summary>The stand-in row of a track with no clips (+ Add Layer's): it takes the first clip dropped onto it.</summary>
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
    /// A view taken into use: its changes are listened to (and, 4.4, a change of its look by the user takes an undo step just
    /// before it is made, OnViewChanging), and from now on it grows about its middle.
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

    /// <summary>
    /// A new clip for a view made for it (a file dropped, a layer added): its timing from <paramref name="start"/> to
    /// <paramref name="end"/>, the rest from the view. The view is taken as the clip's own (<see cref="Adopt"/>) once it is placed.
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

    /// <summary>Makes a view the view of a clip about to be placed, so the rows keep that very view (and its pictures) for it.</summary>
    private void Adopt(Layer view, TimelineClip clip)
    {
        view.ClipId = clip.Id;
        _views[clip.Id] = view;
        _taken[clip.Id] = clip.State;
        Hook(view);
    }

    /// <summary>Lets go of a view whose clip was refused.</summary>
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
    /// Puts a new clip of a view onto the timeline, by the row it was dropped on: an empty track takes it; a track it
    /// joins (<paramref name="joins"/>) takes it where it is free; otherwise it gets a track of its own just above that row
    /// (Track 1's included), and with no row, a track on top of the pictures (a sound's at the end).
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
                    // An empty track filled by a file still being looked at is given back, empty, if it is not a video after all.
                    if (row.Clips.Count == 0 && view.IsLoading)
                        _filledEmptyTracks.Add(row.Id);
                    return edit.PlaceClip(row, clip, allowCrossfadeOverlap: false);
                }
                catch (TimelineEditException) when (row.Clips.Count > 0)
                {
                    // No room there: a track of its own, just above.
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
    /// Writes every view whose look is newer than its clip's into the clip, as a new LayerState, and the markers and the
    /// Sequence Output's effects into the document: before an edit, an undo step, a graph or a save reads it.
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

    // The values of a view that are its clip's own, and what each is called on the view.
    private static readonly HashSet<string> ClipFields =
    [
        nameof(Layer.StartTime), nameof(Layer.Duration), nameof(Layer.MediaOffset), nameof(Layer.Speed), nameof(Layer.FadeInSeconds),
        nameof(Layer.FadeOutSeconds), nameof(Layer.FadeInStyle), nameof(Layer.FadeOutStyle), nameof(Layer.TransitionIn), nameof(Layer.IsHidden),
        nameof(Layer.IsSoundMuted), nameof(Layer.AudioGainDb), nameof(Layer.AudioOffset), nameof(Layer.ProxyChoice),
    ];

    // Values of a view that are its own and not its clip's look: what it shows, what is being done with it.
    private static readonly HashSet<string> ViewOnly =
    [
        nameof(Layer.Waveform), nameof(Layer.Filmstrip), nameof(Layer.IsLoading), nameof(Layer.TrackId), nameof(Layer.IsDeleted),
        nameof(Layer.MediaDuration), nameof(Layer.IsEmptyTrack),
    ];

    /// <summary>
    /// A view was changed. Its clip's own values come from the document, so a change to one of them made outside
    /// <see cref="SyncViews"/> (a box bound to the view) is a request, made an edit of the clip; anything else is its look,
    /// newer now than its clip's. Returns whether it was one of the clip's own values.
    /// </summary>
    private bool NoteViewChange(Layer view, string? name)
    {
        if (_syncingViews || _applyingKeys || name is null || ViewOnly.Contains(name))
            return false;
        if (!ClipFields.Contains(name))
        {
            if (view.ClipId != Guid.Empty)
                _staleLooks.Add(view);
            return false;
        }

        if (_openEdit is null && ClipOf(view) is { } clip)
        {
            // While a step is being put back, and its bindings settle on the new views, a clip's value set on its view is
            // their echo (a box re-rounding what it shows), never the user's request (4.4-BugHunt): the view shows its clip's.
            if (IsRestoringTimeline)
                ShowClipAgain(view, clip);
            else
                TakeClipField(view, clip, name);
        }

        return true;
    }

    /// <summary>A view shows its clip's own values again, as the document has them: nothing is edited.</summary>
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
    /// Whether a box's change of a clip's value takes an undo step of its own: not when it is set right after a step taken by
    /// hand, nor when it is the same value of the same clip again within a second and a half (a box dragged sets one on every
    /// move of the mouse: one step for the drag).
    /// </summary>
    private bool TakesUndoStep(Layer view, string name)
    {
        var now = Environment.TickCount64;
        var again = _lastFieldEdit is (var lastView, var lastName, var at) && ReferenceEquals(lastView, view) && lastName == name && now - at < 1500;
        _lastFieldEdit = (view, name, now);
        return now - _checkpointedAt > 500 && !again;
    }

    /// <summary>One of a clip's own values set on its view: the clip is edited to it (within what the timeline allows), and the view shows what came of it.</summary>
    private void TakeClipField(Layer view, TimelineClip clip, string name)
    {
        // Never while a step is being put back or settles (4.4-BugHunt): no edit, no step, the redo history kept.
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

        // What the timeline made of it (held to its limits, or refused): the view shows the clip as it is.
        if (Document.FindClip(clip.Id) is not null)
            ShowClipAgain(view, clip);
    }

    // ----- The document as the graph builder is given it -----

    /// <summary>
    /// The timeline for one state of the graph builder: a copy of the document (its own tracks and clips, the looks shared,
    /// since none is ever changed in place), the looks written in first, at the frame the graph is built for. On the copy
    /// only: a clip whose file is still being looked at is left out (it has no size yet), and a sound whose file is missing
    /// is silenced (4.2.0 left it out). The builder keeps what it lowers by the copy, which nothing changes afterwards.
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

    // ----- The Sequence's clips of files (5.2-AuditB) -----
    // No clip is privileged: there is no main video and no primary chain. What the timeline draws along it (the playback bar's
    // edit points, snapping, chapters at cuts, the I-frames) is every clip that plays a file's picture (SequenceSpans), each
    // of its own file; the Sequence's natural size is its own (TimelineDocument.NaturalWidth/Height), given as the first
    // file's picture lands on it. The file last opened (LocalMediaPath) is a clip like any dropped one once it is placed.

    /// <summary>Length of the sequence in seconds: to where its last clip ends (its sound included).</summary>
    public double SequenceSeconds => Document.TotalDuration;

    /// <summary>
    /// Every shown top-level clip that plays a file's picture, by where it starts (SequenceSpans.PictureSpans): what the
    /// playback bar's edit points, snapping, chapters at cuts, cut fractions and the I-frames along the timeline are made of.
    /// </summary>
    public IReadOnlyList<ClipSpan> PictureSpans() => SequenceSpans.PictureSpans(Document);

    /// <summary>The file last opened, cleaned as the spans name files; "" for none.</summary>
    private string OpenedFile => LocalMediaPath.Trim().Trim('"');

    /// <summary>
    /// Whether the file last opened is the whole Sequence (one clip of it from its start to its end, untouched,
    /// SequenceSpans.WholeFiles): then what is its own (soft subtitles, an anchored subtitle import) goes with the Sequence.
    /// </summary>
    private bool IsOpenedFileWhole => OpenedFile.Length > 0 && SequenceSpans.WholeFiles(Document).Contains(OpenedFile);

    /// <summary>The view of the top-level clip of the file last opened that ends last (what files opened with it follow, in line); null while none is on the timeline.</summary>
    public Layer? OpenedFileView => Document.Tracks.SelectMany(t => t.Clips)
        .Where(c => c is { ParentClipId: null, ContentKind: LayerKind.VideoFile or LayerKind.Audio } && SameFile(c.SourcePath, OpenedFile))
        .OrderBy(c => c.End).Select(ViewOf).LastOrDefault(v => v is not null);

    // A Game Preset or a preset's place, look and Cutouts chosen while no clip could take it (4.3D): the next file opened takes it.
    private ClipTemplate? _templateToCome;

    // The file opened before one whose length is not known yet (a recording never finished): its clips are replaced once the
    // new file's I-frames say how long it is (SetSourceIFrames). Null while nothing waits.
    private string? _openedOver;

    // The clip the file opened last was placed as (its automatic preset goes on it); null when it placed none.
    private Guid? _openedClip;

    /// <summary>
    /// Puts a clip of the file just opened on the timeline, made as a dropped file is (5.2-AuditB: no clip is privileged):
    /// a video-file clip of the whole file (a sound clip for a file with no picture), fitted to the frame as Settings say, its
    /// blur fill on unless the Blur Fill row's switch is off, its audio streams on Sub-Lanes of their own (UnpackStreams). On an
    /// empty Sequence it goes on Track 1 at 0. Another file opened over clips of the one opened before takes their place: they
    /// go, it lies at the start of the first one's track with its look, place and crop (fitted to the frame when that leaves it
    /// taller than the frame), and what hung from them hangs from it (a Cutout cut from their picture cut from its picture, on
    /// its clock; their own streams' sound goes with them). On a Sequence holding nothing else it gives the Sequence its rate
    /// and natural size. What ran to the end of the timeline (a picture, words, an adjustment) runs to its end (4.3D). A Game
    /// Preset chosen before any clip (<see cref="_templateToCome"/>) goes on it. No undo step: opening a file is not an edit.
    /// </summary>
    /// <param name="previous">The file opened before this one ("" for none): its clips are the ones replaced.</param>
    private void PlaceOpenedFile(string previous)
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
        // Empty but for the clips this file takes the place of and what hangs from them: it is as the first file opened.
        var oldIds = old.Select(o => o.Clip.Id).ToHashSet();
        var empty = !Document.Tracks.Any(t => t.Clips.Exists(c => !oldIds.Contains(c.Id) && !(c.ParentClipId is { } parent && oldIds.Contains(parent))));
        var picture = info.Video is { Width: > 0, Height: > 0 };
        var kind = picture ? LayerKind.VideoFile : LayerKind.Audio;

        // The Sequence ticks at the first file's own rate (as 4.2.0's canvas did), and takes its shape (as the Crop boxes cut
        // it, a preset's chosen before any video): outside any transaction, as a project's document-level values are.
        if (empty)
        {
            Document.FrameRate = Models.Timeline.FrameRate.OfSource(SourceFrameRate);
            if (picture)
                (Document.NaturalWidth, Document.NaturalHeight) = CroppedSize(BoxCrop(), SourceWidth, SourceHeight);
            NoteDocumentShape();
        }

        // Its look: the replaced clip's (without the keys, which were its timing's), at its place; or a new clip's, fitted.
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
                view.BlurFill = _backgroundRow.IsHidden ? null : true;
                FitToFrame(view, AppSettings.Current.NewClipsFill);
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

            // The old clips go, but not what hangs from them (a Game Preset's Cutouts): those are given to the new clip below.
            var oldEnds = old.ToDictionary(o => o.Clip.Id, o => o.Clip.End);
            var children = Document.Tracks.Where(t => !t.IsLocked)
                .SelectMany(t => t.Clips.Where(c => c.ParentClipId is { } parent && oldEnds.ContainsKey(parent))
                    .Select(c => (Track: t, Clip: c, RanToEnd: Math.Abs(c.End - oldEnds[c.ParentClipId!.Value]) < 0.0005))).ToList();
            foreach (var (_, clip) in old)
                edit.DeleteClip(clip, withChildren: false);
            if (track is not null && (!Document.Tracks.Contains(track) || track.IsLocked))
                track = null;

            var clipOfFile = NewClipFor(view, 0, length);
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
                    // Something after it on its track: it keeps its end.
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

        // Two pictures side by side on one canvas (5.2C): offered, never done unasked.
        if (picture)
            OfferSideBySide(view);
    }

    /// <summary>
    /// A clip that hung from a clip of the file opened before, as the file opened now takes that clip's place: that file's own
    /// sound goes (the new file's streams are its own Sub-Lanes); a Cutout cut from its picture is cut from the new picture, on
    /// the new clip's clock, held to the new clip's span (to its end when it ran to the old clip's end; none when nothing of it
    /// lies over it: a piece of the old file never stays on its own); anything else hangs from the new clip where it lies over
    /// it, and lets go otherwise.
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

        // Made anew (a new id, so its view is made anew with its new file).
        var piece = CopyOfClip(child);
        (piece.Id, piece.ParentClipId, piece.SourcePath, piece.Start, piece.End) = (Guid.NewGuid(), null, made.SourcePath, start, end);
        (piece.MediaOffset, piece.Speed, piece.SourceDuration) = (made.MediaOffset + (start - made.Start) * made.Speed, made.Speed, made.SourceDuration);
        piece.State = OnClockOf(TimelineTransaction.CopyOf(child.State), view, made, start);
        (piece.State.StartTime, piece.State.Duration) = (start, end - start);
        edit.DeleteClip(child, withChildren: false);
        edit.SetClipParent(edit.PlaceClip(lane, piece, allowCrossfadeOverlap: false), made.Id);
    }

    /// <summary>Whether a clip's file is a file named (both cleaned, the case ignored).</summary>
    private static bool SameFile(string? path, string file) =>
        file.Length > 0 && string.Equals((path ?? "").Trim().Trim('"'), file.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts a picture at a width (a fraction of the frame's) with its middle at a place (fractions of the frame), at its own
    /// shape as its crop leaves it, its aspect locked: a template's place, or a replaced clip's.
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

    /// <summary>A look put on one clip, which keeps its own timing, switches and crop (a preset's crop is put on its own, ApplyPresetCrop).</summary>
    private void TakeLook(Layer view, TimelineClip clip, TimelineTrackModel track, LayerState? look)
    {
        _syncingViews = true;
        try
        {
            var crop = (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom);
            view.ApplyLook(look);
            (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom) = crop;
            TakeClip(view, clip, track);
        }
        finally
        {
            _syncingViews = false;
        }

        _staleLooks.Add(view);
    }

    // ----- The Sequence's natural size (5.2-AuditB) -----

    // The natural size and whether the document holds any clip, as last told (NoteDocumentShape).
    private (int Width, int Height, bool Filled) _documentShown;

    /// <summary>
    /// Tells what follows the document's natural size (the frame's sides, the other side of a size typed on one) and whether it
    /// holds any clip (<see cref="HasSource"/>) when either has changed: after an edit, an undo step, a document put in place.
    /// </summary>
    private void NoteDocumentShape()
    {
        // The master fader follows the Sequence's gain (5.2B): an edit, an undo, a project opened.
        NoteMasterGain();

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

    // ----- Every picture keeps its middle as the frame changes shape (5.2-AuditB) -----
    // A picture's place is its corner and width as fractions of the frame: a frame of another shape would leave the corner where
    // it was and move the middle. So every picture is kept about its middle (what the main video's Center controls did for it
    // alone until 5.2-AuditB): its width stays, and its corner moves to put its middle where it was at its height on the new
    // frame. The middle it was kept at is used as it is while nothing has moved it since (a frame being typed, a few pixels wide
    // on the way, loses nothing); otherwise it is worked out for the frame it was placed for.

    // Set while pictures are being put in place by what follows from a change (a frame reshaped, a template's place): what that
    // does to them records no keyframe and takes no undo step.
    private bool _placingViews;

    // How each picture was last kept: for what frame, its middle there (fractions of the frame), and the corner and width it had.
    private readonly record struct PicturePlacement((int Width, int Height) Frame, double MiddleX, double MiddleY,
        (double SizeWidth, double PositionX, double PositionY) Wrote);

    private readonly Dictionary<Layer, PicturePlacement> _placedFor = new(ReferenceEqualityComparer.Instance);

    /// <summary>Keeps every picture about its middle as the frame changes shape (see the section above); a picture met for the first time was placed for the frame as it is.</summary>
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
            // (Over a copy: a view's change tells the window, whose handlers may commit an edit, which makes the views again.)
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

    /// <summary>Where a picture's middle is on a frame of a size, as fractions of it, its height at its width on that frame.</summary>
    private (double X, double Y) MiddleOf(Layer view, (int Width, int Height) frame)
    {
        var (_, _, width, height) = view.GetOutputRect(Math.Max(frame.Width, 2), Math.Max(frame.Height, 2), SourceWidth, SourceHeight);
        return (view.PositionX + width / 2.0 / Math.Max(frame.Width, 2), view.PositionY + height / 2.0 / Math.Max(frame.Height, 2));
    }

    // ----- The clip the Crop boxes stand for (5.2-AuditB) -----
    // The Crop boxes (CropLeft..CropBottom, percentages of a picture) and Interactive Crop show and set one clip's own crop: the
    // selected clip of a file's picture, else the lowest shown top-level video clip at the playhead (or the first). A default of
    // the interface only: nothing renders by it, and with no clip to stand for they hold what they say for the next file opened.

    // The clip the Crop boxes were last taken from, and whether a drag holds them to it.
    private Layer? _cropShown;
    private bool _cropHeld, _watchingCropTarget;

    /// <summary>
    /// The clip the Crop boxes and Interactive Crop show and set: the selected clip of a file's picture, else the lowest shown
    /// top-level video clip at the playhead (or the first); null while there is none. While a drag is under way, the one they
    /// were taken from, so the playhead crossing into another clip during playback does not switch it partway through.
    /// </summary>
    public Layer? CropTarget =>
        _cropHeld && _cropShown is { ClipId: var heldId } held && heldId != Guid.Empty && _views.TryGetValue(heldId, out var heldView) && ReferenceEquals(heldView, held)
            ? held
            : KeyLayer is { IsFile: true, ClipId: var id } selected && id != Guid.Empty && _views.TryGetValue(id, out var view) && ReferenceEquals(view, selected)
                ? selected
                : VideoViewAt(PositionMs / 1000) ?? FirstVideoView();

    /// <summary>
    /// The clip whose own picture the player shows while a rectangle is drawn on it (Interactive Crop or a Draw Target: GpuPreviewEngine.ShowSource):
    /// a region being marked (Draw Target) its video's, else a selected clip's or the one a crop drag holds; otherwise empty, for the
    /// bottom video at each moment (the clip the Crop boxes stand for then, so the view follows the playhead from clip to clip).
    /// </summary>
    public Guid ShownSourceClip => DrawTargetLayer is { } piece && ClipOf(piece) is { ParentClipId: { } parent } ? parent
        : _cropHeld || KeyLayer is { IsFile: true } ? CropTarget?.ClipId ?? Guid.Empty
        : Guid.Empty;

    /// <summary>Holds the clip the Crop boxes stand for while a drag is under way (a Canvas box, a gizmo, a slider), or lets it go: they then show the clip they stand for now.</summary>
    public void HoldCropTarget(bool hold)
    {
        _cropHeld = hold;
        if (!hold && !ReferenceEquals(CropTarget, _cropShown))
            ShowCropTarget();
    }

    /// <summary>
    /// Shows the crop of the clip the Crop boxes stand for (<see cref="CropTarget"/>) in them; nothing is written into any clip.
    /// After the timeline changed (an edit, an undo step, a project opened) and whenever they come to stand for another clip.
    /// </summary>
    private void ShowCropTarget()
    {
        if (!_watchingCropTarget)
        {
            // The view model's own changes tell when the clip they stand for may be another: the selection's, the playhead's.
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

    /// <summary>The view of the lowest shown top-level video clip on screen at a moment, or null when none is. Looked for at every move of the playhead.</summary>
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

    // The one clip a preset goes on while it is set, whatever is selected: the clip of a file just opened, for its automatic preset.
    private Guid? _templateTargetOnly;

    /// <summary>
    /// A preset or Game Preset saved with its blur fill hidden (the Background switch off) takes it from the clips it goes on, as
    /// it hid the blurred background before 5.2-AuditB; one saved with it shown gives it only with its layout (PutTemplateOn).
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
    /// The video clips a Game Preset's layout or a preset's place and crop go on (5.2-AuditB, the per-clip template
    /// path): those selected on the Timeline (a selected Cutout's clip), or with none selected every top-level video clip.
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
