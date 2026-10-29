using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- The timeline is a TimelineDocument (4.3C) -----
// The view model's timeline is one document: its tracks bottom first, the clips on them (the main video's are clips of
// kind MainVideo on a track like any other, the lowest video track to begin with), its markers and the Sequence
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
            _liveState = null;
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

    /// <summary>The track a row stands for: its clip's, or the empty track's; null for the stand-ins.</summary>
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
                    _liveState = null;
                    SyncViews();
                    AudioLinked = AllAudioLinked();
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
                _liveState = null;
                SyncViews(afresh: true);
                FollowSelection();
                AudioLinked = AllAudioLinked();
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
            _centerShown = null;
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

        // The Center controls and the Crop boxes show the main video's clip they stand for as the document has it now (an
        // undo step puts back its place and crop, 4.4).
        ShowCenterTarget();
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
            if (view.IsMainVideo)
                view.Filmstrip = _layerFilmstrips.GetValueOrDefault(LocalMediaPath);

            // A clip of the main video made from another (a split's second half): placed by the zoom and offsets that one
            // was placed by, while its corner, width and crop are that one's, so a frame of another shape keeps the two one
            // look (worked back from its even-pixel width, it could land two pixels off).
            if (view.IsMainVideo && _placedFor.FirstOrDefault(p => p.Key.IsMainVideo && p.Value.Wrote == (view.SizeWidth, view.PositionX, view.PositionY) && CropOf(p.Key) == CropOf(view)) is { Key: not null } same)
                _placedFor[view] = same.Value;

            // A video whose file was still being looked at when this clip was kept (an undo step, a save taken during a
            // drop): looked at again, its length and sound filled in, its place kept (4.2.0's SetLayers did the same).
            if (view is { IsVideoFile: true } && (view.ImageWidth <= 0 || clip.SourceDuration <= 0) && !_isBackgroundWorker)
            {
                view.IsLoading = true;
                _ = CompleteVideoLayerAsync(view, playOnce: true, keepLength: true);
            }
            else if (!_isBackgroundWorker)
            {
                _ = view.IsMainVideo ? LoadMainFilmstripAsync() : LoadLayerPicturesAsync(view);
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
    /// Writes a clip's own values (its timing, fades, switches, gain, slip, proxy, track) into its view. The main video's
    /// picture is hidden by its track's switch (its sound plays on), which its views show as their own.
    /// </summary>
    private void TakeClip(Layer view, TimelineClip clip, TimelineTrackModel track)
    {
        view.TrackId = NumberOf(track);
        (view.StartTime, view.Duration, view.MediaOffset) = (clip.Start, clip.Duration, clip.MediaOffset);
        view.Speed = clip.Speed;
        (view.FadeInSeconds, view.FadeOutSeconds, view.FadeInStyle, view.FadeOutStyle) = (clip.FadeInSeconds, clip.FadeOutSeconds, clip.FadeInStyle, clip.FadeOutStyle);
        view.TransitionIn = clip.TransitionIn;
        (view.IsHidden, view.IsSoundMuted, view.AudioGainDb) = (clip.IsHidden || (view.IsMainVideo && track.IsHidden), clip.IsMuted, clip.GainDb);
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
        SourcePath = view.IsFile || view.IsAudio ? view.ImagePath : view.IsVideo || view.IsMainVideo ? LocalMediaPath : null,
        AudioStreamIndex = view.CarriesSound || (view.IsMainVideo && _mediaInfo is not { Audio.Count: 0 }) ? 0 : -1,
        Start = start,
        End = end,
        MediaOffset = view.IsVideoFile || view.IsAudio || view.IsMainVideo ? view.MediaOffset : 0,
        Speed = view.HasSpeed ? view.Speed : 1,
        SourceDuration = view.IsVideoFile || view.IsAudio ? view.MediaDuration : view.IsMainVideo ? MainMediaSeconds : 0,
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
    /// (the main video's included), and with no row, a track on top of the pictures (a sound's at the end).
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

    // ----- The primary chain, as the builder reads it from the document -----

    // The lowering of the live state, made once for each state of the timeline (see SequenceLowering).
    private SequenceLowering? _lowering;

    /// <summary>The timeline as the graph builder lowers it now: its primary chain (the main video's clips it plays from the main file's stream), its length.</summary>
    private SequenceLowering Lowering
    {
        get
        {
            var state = _liveState ??= CompileExportState();
            if (_lowering is null || !ReferenceEquals(_loweredState, state))
                (_lowering, _loweredState) = (FilterGraphBuilder.Lowering(state), state);
            return _lowering;
        }
    }

    private SequenceExportState? _loweredState;

    /// <summary>
    /// The main video's clips the player plays, in the order they come on the sequence (the graph builder's primary chain,
    /// crossfades laid out): what its sequence for the player, the I-frames on the timeline and the playback bar's edit points
    /// are made from. Without the Layer Engine the file is the whole timeline.
    /// </summary>
    public IReadOnlyList<MainClip> GetMainClips() =>
        !FrameEngine ? [new MainClip(0, 0, MainMediaSeconds, 0)] : HasSource ? Lowering.MainClips : [];

    /// <summary>From where the main video first appears on the sequence to where it is last seen, in seconds.</summary>
    public (double Start, double End) GetMainSpan()
    {
        var clips = GetMainClips();
        return clips.Count == 0 ? (0, 0) : (clips[0].Start, clips.Max(c => c.End));
    }

    /// <summary>Whether the main video plays as more than one stretch of its file, or not from its start: what a whole file is not.</summary>
    private bool IsMainSpliced => FrameEngine && !IsMainWholeSequence;

    /// <summary>Whether the main video is the whole sequence, untouched: then the file can be played and read as it is.</summary>
    private bool IsMainWholeSequence => !FrameEngine || MainMediaSeconds <= 0 || Lowering.IsMainWholeSequence;

    /// <summary>Length of the sequence in seconds: to where its last clip ends (its sound included).</summary>
    public double SequenceSeconds => FrameEngine ? Document.TotalDuration : MainMediaSeconds;

    /// <summary>The clips of the main video (kind MainVideo), each with its track: the main video wherever it is put.</summary>
    private IEnumerable<(TimelineTrackModel Track, TimelineClip Clip)> MainVideoClips() =>
        Document.Tracks.SelectMany(t => t.Clips.Where(c => c.ContentKind == LayerKind.MainVideo).Select(c => (t, c)));

    /// <summary>
    /// Puts the main video on the timeline for a source just loaded: its clips of another file go, and one clip of the
    /// whole file goes where they were (on their track, at the start), or on the lowest video track when that is free at
    /// the start, or on a new track at the bottom. What ran to the end of the timeline and has no length of its own (a
    /// piece cut from the video, a picture, words, an adjustment: what 4.2.0 left "to the end", a style's layout chosen
    /// before any video among them) runs to the new video's end (4.3D). Not an undo step: loading the source is not an edit.
    /// </summary>
    private void PlaceMainVideo()
    {
        var length = MainMediaSeconds;
        if (!HasSource || length <= 0.05)
            return;

        // The timeline ticks at the main video's own rate (as 4.2.0's canvas did): its edits land on its frames.
        Document.FrameRate = Models.Timeline.FrameRate.OfSource(SourceFrameRate);
        Edit("put the main video on the timeline", edit =>
        {
            // Its look stays (4.2.0 kept the main video's row through a new source, and "Whole" put back its timing only): its
            // first clip's, without the keyframes, which were the old timing's. Where it was, or the lowest video track that
            // holds nothing else (so no layer starts sharing its look), or a new track at the bottom.
            var old = MainVideoClips().ToList();
            var look = old.Count > 0 ? TimelineTransaction.CopyOf(old[0].Clip.State)
                : _mainLookToCome is { } waiting ? TimelineTransaction.CopyOf(waiting) : null;
            _mainLookToCome = null;
            if (look is not null)
            {
                (look.KeysX, look.KeysY, look.KeysScale, look.KeysRotation, look.KeysOpacity) = (null, null, null, null, null);
                (look.Kind, look.Name) = (LayerKind.MainVideo, MainVideoName());
            }

            var track = old.Count > 0 ? old[0].Track : Document.VideoTracks.FirstOrDefault(t => t.Clips.Count == 0);
            var oldEnd = Document.TotalDuration;
            var toTheEnd = Document.Tracks.Where(t => !t.IsLocked).SelectMany(t => t.Clips)
                .Where(c => c.ContentKind is LayerKind.Video or LayerKind.Image or LayerKind.Text or LayerKind.Adjustment && Math.Abs(c.End - oldEnd) < 0.0005)
                .ToList();
            // The old clips go, but not what hangs from them (a style's HUD pieces cut from the video): those stay, and are
            // given to the new clip below.
            var oldIds = old.Select(o => o.Clip.Id).ToHashSet();
            foreach (var (_, clip) in old)
                edit.DeleteClip(clip, withChildren: false);
            if (track is not null && (!Document.Tracks.Contains(track) || track.IsLocked))
                track = null;

            var view = look is null ? new Layer { Kind = LayerKind.MainVideo, Name = MainVideoName() } : Layer.FromState(look, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            view.MediaDuration = length;

            // Where the Center controls say (the clip it replaces, or a style's or preset's), at this file's shape (4.4: a
            // clip's place is its own, so it is written into it before it is made); the first clip of the main video cropped
            // as the Crop boxes say (a preset's, chosen before any video).
            if (old.Count == 0)
                (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom) = BoxCrop();
            if (FrameWidth > 0 && FrameHeight > 0)
            {
                _placingMain = true;
                try
                {
                    WriteMainPlace(view, CenterZoom, CenterOffsetX, CenterOffsetY);
                }
                finally
                {
                    _placingMain = false;
                }
            }

            var main = NewClipFor(view, 0, length);
            Adopt(view, main);

            // Its frames on the timeline (an adopted view is not made by ViewFor, which fetches them for a new one).
            view.Filmstrip = _layerFilmstrips.GetValueOrDefault(LocalMediaPath);
            if (!_isBackgroundWorker)
                _ = LoadMainFilmstripAsync();
            try
            {
                edit.PlaceClip(track ?? edit.InsertTrack(0, TimelineTrackKind.Video), main, allowCrossfadeOverlap: false);
            }
            catch (TimelineEditException) when (track is not null)
            {
                edit.PlaceClip(edit.InsertTrack(0, TimelineTrackKind.Video), main, allowCrossfadeOverlap: false);
            }

            foreach (var clip in toTheEnd.Where(c => c.Start < main.End - TimelineDocument.MinimumClipSeconds && Math.Abs(c.End - main.End) > 0.0005))
            {
                try
                {
                    edit.TrimClipEnd(clip, main.End);
                }
                catch (TimelineEditException)
                {
                    // Something after it on its track: it keeps its end.
                }
            }

            // What hung from the old clips hangs from the new one now, where it lies over it; anything else lets go. A clip on a
            // locked track is left as it is (an edit of it would refuse the whole load): a parent that is gone is harmless.
            if (oldIds.Count > 0)
            {
                foreach (var orphan in Document.Tracks.Where(t => !t.IsLocked).SelectMany(t => t.Clips).Where(c => c.ParentClipId is { } parent && oldIds.Contains(parent)).ToList())
                    edit.SetClipParent(orphan, orphan.Start >= main.Start - 0.0005 && orphan.End <= main.End + 0.0005 ? main.Id : null);
            }
        }, undoable: false);
    }

    /// <summary>
    /// What the main video's file is (how long, whether it has sound), written into its clips of that file and their views
    /// once it has been looked at: what the file is, not an edit, so no undo step.
    /// </summary>
    private void TakeMainFile(MediaInfo? info)
    {
        var length = Math.Max(info?.DurationSeconds ?? 0, 0);
        foreach (var (_, clip) in MainVideoClips())
        {
            if (!string.Equals(clip.SourcePath, LocalMediaPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (length > 0)
                clip.SourceDuration = length;
            clip.AudioStreamIndex = info is { Audio.Count: 0 } ? -1 : 0;
        }

        // (Over a copy: what a view's change sets going may change the rows.)
        foreach (var view in Layers.Where(l => l.IsMainVideo).ToArray())
        {
            view.Name = MainVideoName();
            if (length > 0)
                view.MediaDuration = length;
        }

        _liveState = null;
    }

    // A preset's or a style's look for the main video while it has no clip (a style chosen before a video is opened, 4.3D):
    // the next video opened takes it (PlaceMainVideo), as 4.2.0's main row kept it.
    private LayerState? _mainLookToCome;

    /// <summary>A look put on every clip of the main video (a preset's, a style's), each keeping its own timing and switches; kept for the next video while there is none.</summary>
    private void TakeMainLook(LayerState? look)
    {
        _mainLookToCome = MainVideoClips().Any() || look is null ? null : TimelineTransaction.CopyOf(look);
        foreach (var (track, clip) in MainVideoClips().ToList())
        {
            if (ViewOf(clip) is not { } view)
                continue;

            _syncingViews = true;
            try
            {
                // Its crop is the clip's own (4.4), as its place is: a look takes neither (a preset's crop is put on its own, ApplyPresetCrop).
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

        // A style's or a preset's: every clip of the main video takes the Center controls it set.
        PlaceMainViews(all: true);
        ShowCenterTarget();
    }

    /// <summary>What the main video's clips are called: its file's name, or "Main Video".</summary>
    private string MainVideoName() => _mediaInfo is not null && LocalMediaPath.Length > 0 ? System.IO.Path.GetFileName(LocalMediaPath) : "Main Video";

    // ----- The main video's place: the Center controls -----
    // The main video is placed about its middle (zoom and offsets), which keeps it centred when the frame changes shape.
    // Its place and its crop are each clip's own (4.4): every clip's view holds them as every other layer does, a corner
    // and a width (and its crop, which reframes its stream), written into its clip as its look (FlushLooks), so an undo step
    // or a project keeps them. The Center controls and the Crop boxes are a view of one clip, CenterTarget (the selected
    // one, else the one at the playhead): they are taken from it (ShowCenterTarget) and written into it (PlaceMainViews); a
    // style, a preset or Reset writes them into every clip. The graph places the chain by its first clip's (CenterRect,
    // GetCenterRect) and lays a clip placed or cropped otherwise in a box of its own.

    private bool _placingMain;

    // Set while a style, a preset, Reset or a project being opened sets the Center controls: every clip of the main video takes them.
    private bool _centerForAll;

    // The view the Center controls and the Crop boxes were last taken from (the CenterTarget of then).
    private Layer? _centerShown;

    // How each view of the main video was last placed: for what frame and source, at what zoom and offsets, and the corner
    // and width that gave. A frame of another shape places it again about its middle at that zoom and those offsets (worked
    // back from its corner and width only when they were changed since: a frame being typed, a few pixels wide on the way,
    // loses nothing). A view not here is taken as placed for the frame as it is when it is first met.
    private readonly record struct MainPlacement((int FrameWidth, int FrameHeight, int SourceWidth, int SourceHeight) For,
        double Zoom, double OffsetX, double OffsetY, (double SizeWidth, double PositionX, double PositionY) Wrote);

    private readonly Dictionary<Layer, MainPlacement> _placedFor = new(ReferenceEqualityComparer.Instance);

    // Whether the view model listens to itself for the Center controls' clip changing (ShowCenterTarget).
    private bool _watchingCenterTarget;

    // Set while a box, a gizmo or a slider is dragged (MainWindow, HoldCenterTarget): the clip the Center controls stand for
    // stays the one they were taken from.
    private bool _centerHeld;

    /// <summary>
    /// The main video's clip the Center controls and the Crop boxes show and set (4.4): the selected one, else the one at
    /// the playhead (or its first); the hidden stand-in while it has none. While a drag is under way, the one they were
    /// taken from, so the playhead crossing into another clip during playback does not switch it partway through.
    /// </summary>
    public Layer CenterTarget =>
        _centerHeld && _centerShown is { ClipId: var heldId } held && heldId != Guid.Empty && _views.TryGetValue(heldId, out var heldView) && ReferenceEquals(heldView, held)
            ? held
            : KeyLayer is { IsMainVideo: true, ClipId: var id } selected && id != Guid.Empty && _views.TryGetValue(id, out var view) && ReferenceEquals(view, selected)
                ? selected
                : MainLayer;

    /// <summary>
    /// Holds the clip the Center controls stand for while a drag is under way (a Canvas box, a gizmo, a slider), or lets it
    /// go: they then show the clip they stand for now.
    /// </summary>
    public void HoldCenterTarget(bool hold)
    {
        _centerHeld = hold;
        if (!hold && !ReferenceEquals(CenterTarget, _centerShown))
            ShowCenterTarget();
    }

    /// <summary>
    /// The view of the main video's clip the sequence's place and crop are (the graph's CenterRect and crop): the first shown
    /// clip of the lowest picture track that plays the main file as the player does, the graph builder's primary chain's
    /// first clip (FilterGraphBuilder.CanRide); while no clip can ride the chain (each at another speed, another sound
    /// stream), the main video's first clip, as the builder takes it then, never the one at the playhead; null while it has none.
    /// </summary>
    private Layer? AnchorMainView()
    {
        if (HasSource)
        {
            var primary = LocalMediaPath.Trim().Trim('"');
            foreach (var track in Document.Tracks)
            {
                if (track.Kind is not (TimelineTrackKind.Video or TimelineTrackKind.Switch))
                    continue;

                foreach (var clip in track.Clips)
                {
                    if (!clip.IsHidden && clip.ContentKind == LayerKind.MainVideo && clip.VideoStreamIndex == 0 && clip.AudioStreamIndex <= 0
                        && Math.Abs(clip.Speed - 1) < 0.0005 && clip.SourcePath is { } path && string.Equals(path.Trim().Trim('"'), primary, StringComparison.OrdinalIgnoreCase))
                    {
                        return _views.GetValueOrDefault(clip.Id);
                    }
                }
            }
        }

        return FirstMainView();
    }

    /// <summary>
    /// The zoom and offsets a view of the main video is placed by (the Center controls' values for it), worked back from its
    /// corner and width at the shape its crop leaves of the source: on the frame and source given, or as they are now.
    /// </summary>
    private (double Zoom, double OffsetX, double OffsetY) MainPlaceOf(Layer view, (int FrameWidth, int FrameHeight, int SourceWidth, int SourceHeight)? on = null)
    {
        var (frameWidth, frameHeight, sourceWidth, sourceHeight) = on ?? (FrameWidth, FrameHeight, SourceWidth, SourceHeight);
        var (croppedWidth, croppedHeight) = CroppedSize(CropOf(view), sourceWidth, sourceHeight);
        var zoom = Math.Clamp(view.SizeWidth, SmallestCenterZoom, LargestCenterZoom);
        return (zoom, Math.Clamp((view.PositionX - (1 - zoom) / 2) * 100, -100, 100),
            Math.Clamp((view.PositionY - (1 - zoom * Math.Max(frameWidth, 1) * croppedHeight / croppedWidth / Math.Max(frameHeight, 1)) / 2) * 100, -100, 100));
    }

    /// <summary>
    /// The zoom and offsets a view of the main video is at: those it was last placed by, while nothing has moved it since
    /// (exact, whatever frame it was placed for); else worked back from its corner and width, for the frame it was placed for.
    /// </summary>
    private (double Zoom, double OffsetX, double OffsetY) KeptPlaceOf(Layer view)
    {
        if (!_placedFor.TryGetValue(view, out var was))
            return MainPlaceOf(view);

        return was.Wrote == (view.SizeWidth, view.PositionX, view.PositionY) ? (was.Zoom, was.OffsetX, was.OffsetY) : MainPlaceOf(view, was.For);
    }

    /// <summary>
    /// Writes a place (a zoom and offsets) into a view of the main video as a corner and a width, at the shape its own crop
    /// leaves of the source; its look is then newer than its clip's. Callers hold <see cref="_placingMain"/>.
    /// </summary>
    private void WriteMainPlace(Layer view, double zoom, double offsetX, double offsetY)
    {
        var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
        var (x, y, width, _) = CenterRectOf(zoom, offsetX, offsetY, CroppedSizeOf(CropOf(view)));
        var place = (width / frameWidth, x / frameWidth, y / frameHeight);
        _placedFor[view] = new MainPlacement((FrameWidth, FrameHeight, SourceWidth, SourceHeight), zoom, offsetX, offsetY, place);
        if ((view.SizeWidth, view.PositionX, view.PositionY) == place)
            return;

        (view.SizeWidth, view.PositionX, view.PositionY) = place;
        if (view.ClipId != Guid.Empty)
            _staleLooks.Add(view);
    }

    /// <summary>
    /// Keeps the main video's views in place as the frame changes shape (each about its middle), and writes the Center
    /// controls into the clip they stand for, or into every clip of the main video (<paramref name="all"/>: a style, a
    /// preset, Reset). Without either, only the first.
    /// </summary>
    /// <param name="moved">The Center controls were set (dragged on the Canvas, typed): what a keyframe may have to record, on that clip at the playhead (or, selected with the playhead outside it, at its nearest moment).</param>
    private void PlaceMainViews(bool moved = false, bool all = false)
    {
        if (_placingMain || _applyingKeys || FrameWidth <= 0 || FrameHeight <= 0)
            return;

        // A step being put back (4.4-BugHunt) brings every clip's place with it: the Center controls set meanwhile (outside
        // ShowCenterTarget) write into no clip. Keeping the views in place as the frame changes shape goes on.
        if ((moved || all) && _restoring > 0)
            return;

        var target = CenterTarget;
        var now = (FrameWidth, FrameHeight, SourceWidth, SourceHeight);
        var active = moved ? MainViewAt(PositionMs / 1000) : null;
        if (active is not null && !all && !ReferenceEquals(active, target))
            active = null;

        // The selected clip, with the playhead outside it: its keys are recorded at its moment nearest the playhead (its
        // first or its last), or they would put it back where they say and the controls would seem to do nothing.
        double? keyTime = null;
        if (moved && !all && active is null && target is { IsMainVideo: true } && target.ClipId != Guid.Empty && ReferenceEquals(target, KeyLayer))
            (active, keyTime) = (target, Math.Clamp(PositionMs / 1000 - target.StartTime, 0, Math.Max(target.Duration, 0)));
        var before = active is null ? default : (active.SizeWidth, active.PositionX, active.PositionY);
        _placingMain = true;
        try
        {
            // A frame (or source) of another shape: each clip keeps its zoom and offsets (those about to take the controls'
            // values aside). A view met for the first time was placed for the frame as it is. (Over a copy: a view's change
            // tells the window, whose handlers may commit an edit, which makes the views again.)
            foreach (var view in _views.Values.ToArray())
            {
                if (!view.IsMainVideo || all || (moved && ReferenceEquals(view, target)))
                    continue;

                if (!_placedFor.TryGetValue(view, out var was))
                {
                    var (zoom, offsetX, offsetY) = MainPlaceOf(view);
                    _placedFor[view] = new MainPlacement(now, zoom, offsetX, offsetY, (view.SizeWidth, view.PositionX, view.PositionY));
                }
                else if (was.For != now)
                {
                    var (zoom, offsetX, offsetY) = KeptPlaceOf(view);
                    WriteMainPlace(view, zoom, offsetX, offsetY);
                }
            }

            // Written into their clips as well (FlushLooks): the graph tells their looks apart by everything a look holds.
            if (all)
            {
                foreach (var view in _views.Values.ToArray())
                {
                    if (view.IsMainVideo)
                        WriteMainPlace(view, CenterZoom, CenterOffsetX, CenterOffsetY);
                }
            }
            else if (moved && target.ClipId != Guid.Empty && target.IsMainVideo)
            {
                WriteMainPlace(target, CenterZoom, CenterOffsetX, CenterOffsetY);
            }
        }
        finally
        {
            _placingMain = false;
        }

        // The controls stand for another clip than the one they were taken from (the playhead crossed into another while
        // a layer was selected): they show it now.
        if (!moved && !all && !ReferenceEquals(target, _centerShown))
            ShowCenterTarget();

        // Not while a preset or a project is being put in place: that is not the user moving it.
        if (active is null || _commandDeferrals > 0)
            return;

        if (active.SizeWidth != before.SizeWidth)
            RecordKey(active, nameof(Layer.SizeWidth), keyTime);
        if (active.PositionX != before.PositionX)
            RecordKey(active, nameof(Layer.PositionX), keyTime);
        if (active.PositionY != before.PositionY)
            RecordKey(active, nameof(Layer.PositionY), keyTime);
    }

    // Set by a style or a preset (deferred while it is put in place), by Reset (PlaceEveryMainClip): every clip of the main
    // video takes them; set by hand, the clip the controls stand for. (An undo step defers the command too, but is no preset.)
    partial void OnCenterZoomChanged(double value) => PlaceMainViews(moved: true, all: _centerForAll || PresetBeingApplied);
    partial void OnCenterOffsetXChanged(double value) => PlaceMainViews(moved: true, all: _centerForAll || PresetBeingApplied);
    partial void OnCenterOffsetYChanged(double value) => PlaceMainViews(moved: true, all: _centerForAll || PresetBeingApplied);

    /// <summary>Sets the Center controls and puts every clip of the main video there (a style's layout, Reset): not only the clip they stand for.</summary>
    private void PlaceEveryMainClip(double zoom, double offsetX, double offsetY)
    {
        _centerForAll = true;
        try
        {
            (CenterZoom, CenterOffsetX, CenterOffsetY) = (zoom, offsetX, offsetY);
        }
        finally
        {
            _centerForAll = false;
        }

        // (Values the controls had already are no change of theirs: the clips are placed all the same.)
        PlaceMainViews(all: true);
    }

    /// <summary>
    /// Shows the clip the Center controls and the Crop boxes stand for (<see cref="CenterTarget"/>) in them: its zoom and
    /// offsets worked back from its place, its crop. Nothing is written into any clip. After the timeline changed (an edit,
    /// an undo step, a project opened) and whenever they come to stand for another clip (another selected, the playhead in
    /// another).
    /// </summary>
    private void ShowCenterTarget()
    {
        if (!_watchingCenterTarget)
        {
            // The view model's own changes tell when the clip they stand for may be another: the selection's, the playhead's.
            _watchingCenterTarget = true;
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ActiveKeyLayer) or nameof(PositionMs) && !_placingMain && !_showingTarget && !ReferenceEquals(CenterTarget, _centerShown))
                    ShowCenterTarget();
            };
        }

        var target = CenterTarget;
        _centerShown = target;
        if (target.ClipId == Guid.Empty || !target.IsMainVideo || FrameWidth <= 0 || FrameHeight <= 0)
            return;

        var (zoom, offsetX, offsetY) = KeptPlaceOf(target);
        var crop = CropOf(target);
        var cropped = CroppedSizeOf(crop);
        using var once = DeferCommand();
        (_placingMain, _showingTarget) = (true, true);
        try
        {
            // Values that put it where it is already stay as they were typed (worked back, a place is to the pixel).
            if (CenterRectOf(CenterZoom, CenterOffsetX, CenterOffsetY, cropped) != CenterRectOf(zoom, offsetX, offsetY, cropped))
                (CenterZoom, CenterOffsetX, CenterOffsetY) = (zoom, offsetX, offsetY);
            if (BoxCrop() != crop)
                (CropLeft, CropTop, CropRight, CropBottom) = (Math.Round(crop.Left * 100, 2), Math.Round(crop.Top * 100, 2), Math.Round(crop.Right * 100, 2), Math.Round(crop.Bottom * 100, 2));
        }
        finally
        {
            (_placingMain, _showingTarget) = (false, false);
        }
    }

    /// <summary>The view of the main video's clip on screen at a moment (the lowest, when two are), or null when none is.</summary>
    private Layer? MainViewAt(double seconds)
    {
        // Looked for at every move of the playhead: no allocation.
        foreach (var track in Document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.ContentKind == LayerKind.MainVideo && clip.Start <= seconds + 0.0005 && clip.End > seconds && _views.TryGetValue(clip.Id, out var view))
                    return view;
            }
        }

        return null;
    }

    /// <summary>The view of the main video's first clip (the lowest track's), or null while it has none.</summary>
    private Layer? FirstMainView()
    {
        foreach (var track in Document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.ContentKind == LayerKind.MainVideo && _views.TryGetValue(clip.Id, out var view))
                    return view;
            }
        }

        return null;
    }

    /// <summary>
    /// A view of the main video set to another place or size (by a keyframe applied, or a box): when it is the clip the
    /// Center controls stand for, they follow it (any other clip's place is its own, 4.4). While keyframes are being
    /// applied, only the clip on screen at the playhead speaks for it.
    /// </summary>
    private void TakeMainPlace(Layer view, KeyProperty property)
    {
        if (_placingMain || !ReferenceEquals(view, CenterTarget) || (_applyingKeys && !ReferenceEquals(view, MainViewAt(PositionMs / 1000))))
            return;

        _placingMain = true;
        try
        {
            var (zoom, offsetX, offsetY) = MainPlaceOf(view);
            if (property == KeyProperty.Scale)
                CenterZoom = zoom;
            else if (property == KeyProperty.X)
                CenterOffsetX = offsetX;
            else
                CenterOffsetY = offsetY;

            // Resized, it grows about its middle: its corner has moved, and so have the keyframes that hold it.
            if (property == KeyProperty.Scale && !_applyingKeys)
                WriteMainPlace(view, CenterZoom, CenterOffsetX, CenterOffsetY);
        }
        finally
        {
            _placingMain = false;
        }

        if (property == KeyProperty.Scale && !_applyingKeys)
        {
            RecordKey(view, nameof(Layer.PositionX));
            RecordKey(view, nameof(Layer.PositionY));
        }
    }

    /// <summary>The main video as a layer: the view of its clip at the playhead (or its first), its look, filters and turn; a hidden stand-in while it has none.</summary>
    public Layer MainLayer => MainViewAt(PositionMs / 1000) ?? FirstMainView() ?? _mainStandIn;

    // What stands for the main video while it has no clip on the timeline.
    private readonly Layer _mainStandIn = new() { Kind = LayerKind.MainVideo, Name = "Main Video", IsHidden = true };
}
