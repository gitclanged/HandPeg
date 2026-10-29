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

    // Set while the views are being written from the document: what they are told then is the document's, not a request.
    private bool _syncingViews;

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
    /// Makes a document the timeline (a new project, an undo step, a project opened): its views are made afresh, its
    /// markers and the Sequence Output's effects are taken into the panes that edit them. No undo step of its own.
    /// </summary>
    private void InstallDocument(TimelineDocument document)
    {
        Document.Committed -= OnDocumentCommitted;
        Document = document;
        Document.Committed += OnDocumentCommitted;

        using (DeferCommand())
        {
            SyncViews(afresh: true);
            SetMarkers(document.Markers);
            SetSequenceEffects(document.SequenceEffects, document.SequenceEffectsBypassed);
            AudioLinked = AllAudioLinked();
            _liveState = null;
            RefreshLayerRows();
            TimelineChanged?.Invoke();
            GenerateCommand();
        }
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
    /// nothing and says why in the status bar; returns false then. One made inside another folds into it.
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

        FlushLooks();
        var before = undoable && !_isBackgroundWorker ? CaptureSnapshot() : null;
        var filled = removeEmptied ? Document.Tracks.Where(t => t.Clips.Count > 0).ToList() : [];
        try
        {
            using var edit = Document.BeginTransaction(label);
            (_openEdit, _beforeEdit) = (edit, before);
            change(edit);
            foreach (var track in filled)
            {
                if (track.Clips.Count == 0 && !track.IsLocked && Document.Tracks.Contains(track))
                    edit.RemoveTrack(track);
            }

            edit.Commit();
            return true;
        }
        catch (TimelineEditException refused)
        {
            StatusText = refused.Message;
            return false;
        }
        finally
        {
            (_openEdit, _beforeEdit) = (null, null);
        }
    }

    /// <summary>A change committed: an undo step of what was before it, the views brought into line, the graphs built again.</summary>
    private void OnDocumentCommitted(TimelineCommit commit)
    {
        // The edit is over: what is done from here on (an edit a handler makes) is an edit of its own.
        var before = _beforeEdit;
        (_openEdit, _beforeEdit) = (null, null);

        using (DeferCommand())
        {
            _liveState = null;
            SyncViews();
            AudioLinked = AllAudioLinked();
            RefreshLayerRows();
            TimelineChanged?.Invoke();
            GenerateCommand();
        }

        if (before is not null)
            Checkpoint(commit.Label, before);
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
            foreach (var view in _views.Values.Concat(_emptyTrackViews.Values))
                Unhook(view);
            _views.Clear();
            _emptyTrackViews.Clear();
            _taken.Clear();
            _staleLooks.Clear();
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

            PlaceMainViews();
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
    }

    /// <summary>A clip's view: the one it has, its look taken again when the clip has another; or a new one made from its look.</summary>
    private Layer ViewFor(TimelineClip clip, TimelineTrackModel track)
    {
        if (_views.TryGetValue(clip.Id, out var view))
        {
            if (!ReferenceEquals(_taken.GetValueOrDefault(clip.Id), clip.State))
            {
                // Its keys moved by an edit (a trim of its start, a split, a speed), or an older look put back.
                view.ApplyLook(clip.State);
                view.ApplyTiming(clip.State);
                _taken[clip.Id] = clip.State;
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

    /// <summary>A view taken into use: its changes are listened to, and from now on it grows about its middle.</summary>
    private void Hook(Layer view)
    {
        view.PropertyChanged -= OnLayerChanged;
        view.PropertyChanged += OnLayerChanged;
        view.SpeedEditor = SetClipSpeed;
        view.AnchorCenter = true;
    }

    private void Unhook(Layer view)
    {
        view.PropertyChanged -= OnLayerChanged;
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
            TakeClipField(view, clip, name);
        return true;
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
        if (Document.FindClip(clip.Id) is not null && Document.TrackOf(clip) is { } track)
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
            foreach (var (_, clip) in old)
                edit.DeleteClip(clip);
            if (track is not null && (!Document.Tracks.Contains(track) || track.IsLocked))
                track = null;

            var view = look is null ? new Layer { Kind = LayerKind.MainVideo, Name = MainVideoName() } : Layer.FromState(look, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            view.MediaDuration = length;
            var main = NewClipFor(view, 0, length);
            Adopt(view, main);
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

        foreach (var view in Layers.Where(l => l.IsMainVideo))
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
                view.ApplyLook(look);
                TakeClip(view, clip, track);
            }
            finally
            {
                _syncingViews = false;
            }

            _staleLooks.Add(view);
        }

        PlaceMainViews();
    }

    /// <summary>What the main video's clips are called: its file's name, or "Main Video".</summary>
    private string MainVideoName() => _mediaInfo is not null && LocalMediaPath.Length > 0 ? System.IO.Path.GetFileName(LocalMediaPath) : "Main Video";

    // ----- The main video's place: the Center controls -----
    // The main video is placed about its middle (zoom and offsets), which keeps it centred when the frame changes shape;
    // the graph places the primary chain there (CenterRect). Each of its clips' views shows that place as every other
    // layer does, a corner and a width (PlaceMainViews), and a change of one of them (a keyframe applied, a box typed into)
    // is the main video moved (TakeMainPlace). 4.2.0 kept one row of the main video in step this way; it is every clip now.

    private bool _placingMain;

    /// <summary>Writes the main video's place into its clips' views, from the zoom and offsets.</summary>
    /// <param name="moved">Moved by hand (dragged on the Canvas): what a keyframe may have to record, on the clip at the playhead.</param>
    private void PlaceMainViews(bool moved = false)
    {
        if (_placingMain || _applyingKeys || FrameWidth <= 0 || FrameHeight <= 0)
            return;

        var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
        var (x, y, width, _) = GetCenterRect();
        var active = moved ? MainViewAt(PositionMs / 1000) : null;
        var before = active is null ? default : (active.SizeWidth, active.PositionX, active.PositionY);
        _placingMain = true;
        try
        {
            // Written into their clips as well (FlushLooks), so that every clip of the main video keeps one place: the graph
            // places them by the Center controls, but tells their looks apart by everything a look holds.
            foreach (var view in _views.Values.Where(v => v.IsMainVideo))
            {
                var place = (width / frameWidth, x / frameWidth, y / frameHeight);
                if ((view.SizeWidth, view.PositionX, view.PositionY) == place)
                    continue;
                (view.SizeWidth, view.PositionX, view.PositionY) = place;
                _staleLooks.Add(view);
            }
        }
        finally
        {
            _placingMain = false;
        }

        // Not while a preset or a project is being put in place: that is not the user moving it.
        if (active is null || _commandDeferrals > 0)
            return;

        if (active.SizeWidth != before.SizeWidth)
            RecordKey(active, nameof(Layer.SizeWidth));
        if (active.PositionX != before.PositionX)
            RecordKey(active, nameof(Layer.PositionX));
        if (active.PositionY != before.PositionY)
            RecordKey(active, nameof(Layer.PositionY));
    }

    partial void OnCenterZoomChanged(double value) => PlaceMainViews(moved: true);
    partial void OnCenterOffsetXChanged(double value) => PlaceMainViews(moved: true);
    partial void OnCenterOffsetYChanged(double value) => PlaceMainViews(moved: true);

    /// <summary>The view of the main video's clip on screen at a moment (the lowest, when two are), or null when none is.</summary>
    private Layer? MainViewAt(double seconds) =>
        MainVideoClips().Where(m => m.Clip.Start <= seconds + 0.0005 && m.Clip.End > seconds).Select(m => ViewOf(m.Clip)).FirstOrDefault(v => v is not null);

    /// <summary>
    /// A view of the main video set to another place or size (by a keyframe applied, or a box): the main video is moved
    /// there, which is the zoom and offsets. While keyframes are being applied, only the clip on screen at the playhead
    /// speaks for it; a box typed into (its row's, which is the track's first clip) moves it whichever clip it is.
    /// </summary>
    private void TakeMainPlace(Layer view, KeyProperty property)
    {
        if (_placingMain || (_applyingKeys && !ReferenceEquals(view, MainViewAt(PositionMs / 1000))))
            return;

        _placingMain = true;
        try
        {
            var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
            var (sourceWidth, sourceHeight) = GetCroppedSourceSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
            var zoom = Math.Clamp(view.SizeWidth, SmallestCenterZoom, LargestCenterZoom);
            if (property == KeyProperty.Scale)
                CenterZoom = zoom;
            else if (property == KeyProperty.X)
                CenterOffsetX = Math.Clamp((view.PositionX - (1 - zoom) / 2) * 100, -100, 100);
            else
                CenterOffsetY = Math.Clamp((view.PositionY - (1 - zoom * frameWidth * sourceHeight / sourceWidth / frameHeight) / 2) * 100, -100, 100);
        }
        finally
        {
            _placingMain = false;
        }

        // Resized, it grows about its middle: its corner has moved, and so have the keyframes that hold it.
        if (property == KeyProperty.Scale && !_applyingKeys)
        {
            PlaceMainViews();
            RecordKey(view, nameof(Layer.PositionX));
            RecordKey(view, nameof(Layer.PositionY));
        }
    }

    /// <summary>The main video as a layer: the view of its clip at the playhead (or its first), its look, filters and turn; a hidden stand-in while it has none.</summary>
    public Layer MainLayer =>
        MainViewAt(PositionMs / 1000) ?? MainVideoClips().Select(m => ViewOf(m.Clip)).FirstOrDefault(v => v is not null) ?? _mainStandIn;

    // What stands for the main video while it has no clip on the timeline.
    private readonly Layer _mainStandIn = new() { Kind = LayerKind.MainVideo, Name = "Main Video", IsHidden = true };
}
