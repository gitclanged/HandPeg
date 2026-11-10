using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

// ----- Editing the document: one transaction per change -----
// Every edit works the same on every track, track 1 included. While the document quantizes, the times an edit is given
// land on the nearest frame, and the bounds it is held to on the frames inside them, unless the edit is asked to be
// free (freeUnquantized: a precision edit off the frames). The rules are 4.2.0's layer edits (MainViewModel.Edits.cs),
// with file bounds on every edge, no overlap on a track, locked tracks and J/L cuts added:
// - Each edit is a step of its own: one that is refused (TimelineEditException) has changed nothing, and the
//   transaction can go on.
// - Edges clamp: a trim, roll or slip stops at the file's ends, at the clip before or after, at the timeline's start
//   and at the shortest clip, never moves the other way, and says how far it went (0: nothing changed).
// - Placements refuse: a clip that would overlap another, or land on a locked track or a track of another kind, throws.
// - A ripple moves every clip that starts at or after the edit point, on the clip's own track or on every unlocked one
//   (the subtitle tracks never ripple), by one amount. A clip that would run into one that stays, or before 0, holds it
//   back for all, and a ripple trim is held back with it, so nothing that should follow is left behind.
// - An edit that changes a clip's look (its keyframes, when its start moves) puts a changed copy of its State in place,
//   so the State a rollback puts back was never touched.
// - Children (4.4): a clip may name another as its parent (TimelineClip.ParentClipId), and a track may lie under another
//   (TimelineTrackModel.ParentTrackId). A parent's edit carries, inside its own step, the children that lie with it in
//   time (overlap it), and theirs: a split cuts them too, a trim takes those on its edge along and leaves those it leaves
//   wholly outside where they are, unlinked (Q3, 4.4-BugHunt: a clip of its own, its own children still its; on a track a
//   ripple trim closes up, one left shorter than the shortest clip still goes), a roll is the two trims it is, a move moves
//   them by as much on their own tracks (refused whole when one has no room), a delete and a fold take them too, and a
//   ripple ripples the tracks under the rippled ones. A child on a locked track stays as it is.
// - Folds (4.3D, 4.4-BugHunt): a fold keeps its id when it is put back (RestoredFold: the stretch stays marked, to be folded
//   again as the same fold), when it is folded again, and when a clip's edge at its seam opens part of it or folds more
//   into it (ResizeFold). Folds and the records of stretches put back are values nobody changes: a copy of the document
//   (an undo step's) shares them, so an edit replaces them, never changes one.

/// <summary>An edit the document cannot make: no room, a locked track, a clip of the wrong kind, a time outside the clip.</summary>
public sealed class TimelineEditException(string message) : InvalidOperationException(message);

/// <summary>
/// One change to a <see cref="TimelineDocument"/> (<see cref="TimelineDocument.BeginTransaction"/>): edit through it, then
/// <see cref="Commit"/>. Disposed without a commit, it puts back everything it changed. Opened inside another, it folds
/// into it on commit, and on rollback puts back only its own part.
/// </summary>
public interface ITimelineTransaction : IDisposable
{
    TimelineDocument Document { get; }

    /// <summary>What the change is called (an undo step's name).</summary>
    string Label { get; }

    /// <summary>The clips made, changed or removed so far, in the order they were first touched.</summary>
    IReadOnlyList<Guid> TouchedClipIds { get; }

    /// <summary>The tracks made, numbered again or whose clips changed so far.</summary>
    IReadOnlyList<Guid> TouchedTrackIds { get; }

    /// <summary>Keeps the change. The outermost transaction then raises <see cref="TimelineDocument.Committed"/>, once, if anything changed.</summary>
    void Commit();

    /// <summary>A new, empty track at <paramref name="index"/> in the stacking order (0: the bottom); the tracks of its kind are numbered again.</summary>
    TimelineTrackModel InsertTrack(int index, TimelineTrackKind kind, string? name = null);

    /// <summary>
    /// Puts a clip that is on no track yet onto <paramref name="track"/> at its own <see cref="TimelineClip.Start"/> (no
    /// sooner than its sound's lead-in allows), for its own length, shortened to what its file has from its offset. It
    /// must not overlap another clip, except that with <paramref name="allowCrossfadeOverlap"/> one dropped over a
    /// neighbour by a frame or more, up to <see cref="TimelineDocument.LongestDragCrossfade"/> and half of either, is put
    /// against it and crossfades with it. A refused clip is left as it was given.
    /// </summary>
    TimelineClip PlaceClip(TimelineTrackModel track, TimelineClip clip, bool allowCrossfadeOverlap = true, bool freeUnquantized = false);

    /// <summary>
    /// Moves a clip to <paramref name="newStart"/> on <paramref name="targetTrack"/> (its own or another that takes it,
    /// track 1 included), or with <paramref name="insertTrackAtIndex"/> at or above 0, onto a new track of that track's
    /// kind inserted there. Keyframes go with it, and its crossfade too when it lands against the clip it met before.
    /// With <paramref name="ripple"/>: the clips after it on its old track close up behind it, then (in the times as
    /// they are after that) the clips from <paramref name="newStart"/> on the target track make room.
    /// </summary>
    void MoveClip(TimelineClip clip, TimelineTrackModel targetTrack, double newStart, bool ripple = false, int insertTrackAtIndex = -1,
                  bool allowCrossfadeOverlap = true, bool freeUnquantized = false);

    /// <summary>
    /// Cuts a clip in two at <paramref name="sequenceTime"/> and returns the second part. The first keeps the fade in, the
    /// crossfade in and the sound's lead-in; the second takes the fade out and the sound's tail, and its keyframes are
    /// moved so that they stay on their frames. Throws unless both parts, and their sound, are at least the shortest clip.
    /// </summary>
    TimelineClip SplitClipAt(TimelineClip clip, double sequenceTime, bool freeUnquantized = false);

    /// <summary>
    /// Moves the clip's start to <paramref name="newStart"/>, its end staying, its keyframes on their frames. With
    /// <paramref name="ripple"/> the clip stays where it starts and the change comes off (or onto) its head instead: the
    /// clips after it follow its end. Returns how far the start moved (later is positive).
    /// </summary>
    double TrimClipStart(TimelineClip clip, double newStart, bool ripple = false, bool rippleAllUnlockedTracks = true, bool freeUnquantized = false);

    /// <summary>Moves the clip's end to <paramref name="newEnd"/>; with <paramref name="ripple"/> the clips after it follow. Returns how far it moved.</summary>
    double TrimClipEnd(TimelineClip clip, double newEnd, bool ripple = false, bool rippleAllUnlockedTracks = true, bool freeUnquantized = false);

    /// <summary>
    /// A J or L cut of a linked clip with picture and sound: its sound starts <paramref name="newAudioLeadIn"/> before
    /// its picture and ends <paramref name="newAudioTailOut"/> after it (negative: inside it), within its file and the
    /// timeline's start, and at least the shortest clip long. A value that is not a number leaves that side as it is.
    /// Returns what was set.
    /// </summary>
    (double LeadIn, double TailOut) TrimAudioSplit(TimelineClip clip, double newAudioLeadIn, double newAudioTailOut, bool freeUnquantized = false);

    /// <summary>
    /// Moves the cut between two clips that meet on a track to <paramref name="newCutTime"/>: one gets longer as the other
    /// gets shorter, and nothing else moves but their children (4.4-BugHunt), which follow the cut as the two trims' do.
    /// Returns how far the cut moved.
    /// </summary>
    double RollCut(TimelineClip earlier, TimelineClip later, double newCutTime, bool freeUnquantized = false);

    /// <summary>
    /// Shows another part of the clip's file in the same place: <paramref name="mediaDeltaSeconds"/> of the file later
    /// (negative: earlier), within the file. Times on the timeline, keyframes and fades stay. Returns how far it went.
    /// </summary>
    double SlipClip(TimelineClip clip, double mediaDeltaSeconds);

    /// <summary>
    /// Takes a clip off its track, leaving its time empty; with <paramref name="withChildren"/> (4.4), its children that lie
    /// with it (and theirs) too. Without, they stay where they are, each a clip of its own while it names a parent that is
    /// gone (a parent put back in its place takes them up again).
    /// </summary>
    void DeleteClip(TimelineClip clip, bool withChildren = true);

    /// <summary>Takes a clip off its track and closes the gap it leaves. Returns how far the clips after it moved back.</summary>
    double RippleDelete(TimelineClip clip, bool rippleAllUnlockedTracks);

    /// <summary>
    /// Closes the empty stretch from <paramref name="gapStart"/> to <paramref name="gapEnd"/> on a track (the Close Gap
    /// pill): the clips from its end move back by its length, or as far as they can. Returns how far they moved.
    /// </summary>
    double CloseGap(TimelineTrackModel track, double gapStart, double gapEnd, bool rippleAllUnlockedTracks);

    // ----- What a clip or a track is, beside where it is (4.3C): each a step, held to its limits, a locked track refused -----

    /// <summary>
    /// The clip's fades: each from 0 to <see cref="TimelineDocument.LongestFade"/>, the two together no longer than the
    /// clip (the fade in first); a style left null stays as it is. Returns the lengths set.
    /// </summary>
    (double In, double Out) SetClipFades(TimelineClip clip, double fadeInSeconds, double fadeOutSeconds, TransitionStyle? fadeInStyle = null, TransitionStyle? fadeOutStyle = null);

    /// <summary>
    /// A crossfade from the clip before it on its track, which it must meet (0 takes it away), at most
    /// <see cref="TimelineDocument.LongestCrossfade"/> and the length of either clip. Returns what was set.
    /// </summary>
    double SetCrossfade(TimelineClip clip, double seconds);

    /// <summary>The gain of its sound, in decibels, from -40 to +24 (a layer's range).</summary>
    void SetClipGain(TimelineClip clip, double gainDb);

    /// <summary>Whether it is switched off (picture and sound), and whether its sound is silenced; null leaves either as it is.</summary>
    void SetClipFlags(TimelineClip clip, bool? hidden = null, bool? muted = null);

    /// <summary>Which picture of its file the player shows: its proxy, the original, or as the setting says.</summary>
    void SetClipProxy(TimelineClip clip, ProxyChoice choice);

    /// <summary>
    /// The beats found in a clip's sound (5.2B, <see cref="TimelineClip.BeatTimes"/>): seconds of its file, kept finite, at or after 0, in order
    /// and apart; null takes them away (Clear Audio Beats). The same beats again change nothing.
    /// </summary>
    void SetClipBeats(TimelineClip clip, double[]? beats);

    /// <summary>
    /// The Sequence's master output fader (5.2B, <see cref="TimelineDocument.MasterGainDb"/>), in decibels from -40 to +12; a value that is not a
    /// number is 0. Not a clip's or a track's: a change of it alone still raises <see cref="TimelineDocument.Committed"/> (with no clip or
    /// track touched), so it is a step of Undo like any other.
    /// </summary>
    void SetMasterGain(double decibels);

    /// <summary>
    /// The project's colour space, pixel aspect and sample rate (5.4A, <see cref="TimelineDocument.ProjectSettings"/>); a sample rate
    /// other than 44100, 48000 or 96000 is 48000. Like the master gain, a change of them alone still raises
    /// <see cref="TimelineDocument.Committed"/>, so it is a step of Undo; the same values again change nothing.
    /// </summary>
    void SetProjectSettings(ProjectColorSpace colorSpace, ProjectPixelAspect pixelAspect, int sampleRate);

    /// <summary>
    /// Puts files into Project Media (5.4C, <see cref="TimelineDocument.MediaPool"/>), each once (compared without case), at the end, in
    /// the order given; an empty path or a web address is left out. Like the master gain, a change of it alone still raises
    /// <see cref="TimelineDocument.Committed"/>. False when every one was there already.
    /// </summary>
    bool AddToMediaPool(IEnumerable<string> paths);

    /// <summary>Takes files out of Project Media (5.4C); false when none of them was there. Whether a clip still plays one is the caller's to ask.</summary>
    bool RemoveFromMediaPool(IEnumerable<string> paths);

    /// <summary>
    /// How fast a clip that plays its file runs (<see cref="Layer.SlowestSpeed"/> to <see cref="Layer.FastestSpeed"/>):
    /// it plays the same stretch of its file, so its end moves; the clips after it on its track that it would run into
    /// move on as far as it grew; its keyframes, its effects' keyframes and ranges and its J/L cut are scaled with it.
    /// Returns the speed set (the one it had, for a clip that does not play a file).
    /// </summary>
    double SetClipSpeed(TimelineClip clip, double speed, bool freeUnquantized = false);

    /// <summary>Whether its sound moves and trims with its picture, and how far it is slipped while it does not (seconds, later when positive; not on frames).</summary>
    void SetAudioLink(TimelineClip clip, bool linked, double slipSeconds);

    /// <summary>
    /// A track's switches, sound and name; null leaves each as it is. A locked track takes these too: locking keeps its
    /// clips where they are, not its switches.
    /// </summary>
    void SetTrack(TimelineTrackModel track, bool? hidden = null, bool? muted = null, bool? soloed = null, bool? locked = null,
                  double? gainDb = null, string? name = null);

    /// <summary>
    /// Takes a track off the timeline with its clips; the tracks of its kind are numbered again. The tracks under it stay
    /// (4.4), under its own parent track now, or under none.
    /// </summary>
    void RemoveTrack(TimelineTrackModel track);

    /// <summary>Moves a track to <paramref name="index"/> in the stacking order (0: the bottom); the tracks are numbered again.</summary>
    void MoveTrack(TimelineTrackModel track, int index);

    // ----- Folds (4.3D): a stretch taken out of the timeline and kept at its seam, to be put back -----

    /// <summary>
    /// Folds a stretch out of the timeline (Remove Dead Air's Split &amp; Mark): on the tracks it is taken out of, every clip is
    /// split at its two ends (on frames), what lies between is kept in a fold (<see cref="FoldedSegment"/>) and taken off,
    /// and what follows closes up behind it. The tracks: <paramref name="seamTrack"/> alone, or every unlocked track
    /// (<paramref name="rippleAllUnlockedTracks"/>, as the main video's dead air is taken out of all of them). The fold's
    /// seam is the clip that follows on <paramref name="seamTrack"/> (or the one before, when none follows). A stretch put back
    /// (<see cref="TimelineDocument.RestoredFolds"/>) that it covers is no longer marked. Returns the fold, or null when nothing
    /// lay there to fold.
    /// </summary>
    FoldedSegment? FoldStretch(TimelineTrackModel seamTrack, double from, double to, bool rippleAllUnlockedTracks);

    /// <summary>
    /// Puts a fold back where it was folded out: what lies across its seam on its tracks is split there, everything from the
    /// seam on moves later by its length, and its clips come back into the room made, as they were. The fold goes, and the
    /// stretch stays marked (4.4-BugHunt: a <see cref="RestoredFold"/> with its id, to fold it again). The fold is found by
    /// its id, so one taken from an older copy of the document (an undo step's, a view's) is still this one.
    /// </summary>
    void UnfoldSegment(FoldedSegment fold);

    /// <summary>
    /// Folds a stretch that was put back (<see cref="RestoredFold"/>, 4.4-BugHunt: the Hide pill, "Hide / Fold Dead Air
    /// Segment") out of the timeline again, as <see cref="FoldStretch"/> does: where it lies now, out of the tracks it was
    /// folded out of before, as the fold it was (the same id). Its record goes. Refused when its stretch is no longer there.
    /// </summary>
    FoldedSegment FoldRestored(RestoredFold restored);

    /// <summary>
    /// Opens a fold by part of its length at one side of its seam, or folds more into it (Q11, 4.4-BugHunt: a clip's edge at
    /// the seam dragged away from the clip, or into it). <paramref name="seconds"/> above 0 shows that much of the fold at
    /// <paramref name="side"/>, below 0 folds that much more of the clip at that side on its track (the clip before the seam
    /// for the head, the one after it for the tail); on frames, at least the shortest clip, and no more than leaves that clip
    /// the shortest clip. On every track the fold covers, children with their parents. What is shown joins the clip it
    /// continues where the two are one clip cut in two (its keyframes joined back as a split cut them), and the clip at the
    /// edge keeps its id (folding more at the tail leaves it in another object). Showing it all, or all but less than the
    /// shortest clip, puts the fold back as <see cref="UnfoldSegment"/> does. Returns the fold as it is afterwards (its id
    /// the same), or null when it was put back whole; less than half a frame changes nothing. Refused, changing nothing,
    /// when a track it covers is locked or gone, when no clip meets the seam at that side to fold more of, and when a clip
    /// would be cut shorter than the shortest clip at the new edge.
    /// </summary>
    FoldedSegment? ResizeFold(FoldedSegment fold, FoldSide side, double seconds);

    // ----- Children (4.4): a clip or a track that goes with another -----

    /// <summary>
    /// Makes a clip the child of another (<paramref name="parentClipId"/>; null: of none, "Unlink from Parent"): while it
    /// lies with its parent in time, the parent's splits, trims, moves, deletes and folds carry it along. Refused for the
    /// clip itself, a parent not on this timeline, one that goes with this clip already (its child, or theirs), and a clip
    /// on a locked track.
    /// </summary>
    void SetClipParent(TimelineClip clip, Guid? parentClipId);

    /// <summary>
    /// Puts a track under another (<paramref name="parentTrackId"/>; null: under none): it ripples and folds with it, and
    /// is drawn under it. A locked track takes this too. Refused for the track itself, a track not on this timeline, and
    /// one that lies under this track already.
    /// </summary>
    void SetTrackParent(TimelineTrackModel track, Guid? parentTrackId);

    /// <summary>
    /// Puts the captions right under a track's pictures in the stack (5.1-Polish, the user's Q2:
    /// <see cref="TimelineTrackModel.CaptionsUnder"/>), or, with none, over everything. A locked track takes this too (it is
    /// where the captions are drawn, not a change to its clips). Refused for a track not on this timeline.
    /// </summary>
    void SetCaptionsUnder(TimelineTrackModel? track);

    /// <summary>
    /// Gives a track's row a height of its own (<see cref="TimelineTrackModel.Height"/>), or, with null, the one Track Height
    /// again. A locked track takes this too (it is how the track is looked at, not what it holds).
    /// </summary>
    void SetTrackHeight(TimelineTrackModel track, double? height);

    /// <summary>
    /// Gives a picture track a Game Preset of its own (5.2A, a Track Preset Node: <see cref="TimelineTrackModel.PresetNodeName"/>), or
    /// none with null. Only the node: the preset is put on the track's clips by the caller. A locked track takes this too (it is what the
    /// track gives clips that come onto it, not a change to the ones it holds). Refused for a track not on this timeline, and for one
    /// that holds no pictures.
    /// </summary>
    void SetTrackPreset(TimelineTrackModel track, string? presetName);
}

internal sealed partial class TimelineTransaction : ITimelineTransaction
{
    /// <summary>How near two times count as the same: a microsecond, the grain of a quantized time.</summary>
    private const double Slack = 1e-6;

    /// <summary>The farthest a time given to an edit is taken to be (some 30 years), so that no frame number overflows.</summary>
    private const double Farthest = 1e9;

    private readonly TimelineDocument _doc;
    private readonly Dictionary<TimelineClip, TimelineClip> _clipsBefore = [];
    private readonly Dictionary<TimelineTrackModel, (List<TimelineClip> Clips, TimelineTrackModel Fields)> _tracksBefore = [];
    private List<TimelineTrackModel>? _trackOrderBefore;
    private List<FoldedSegment>? _foldsBefore;
    private List<RestoredFold>? _restoredBefore;
    private bool _foldsRemembered;

    // The Sequence's own values (5.2B: its master gain), as they were before this transaction first changed them, and whether it changed any.
    private double? _masterGainBefore;
    private (ProjectColorSpace ColorSpace, ProjectPixelAspect PixelAspect, int SampleRate)? _projectSettingsBefore;
    private List<string>? _mediaPoolBefore;
    private bool _documentTouched;

    /// <summary>While set, every split made records its two halves (ResizeFold, folding more at a fold's tail, swaps their ids).</summary>
    private List<(TimelineClip First, TimelineClip Second)>? _splits;
    private readonly List<Guid> _touched = [], _touchedTracks = [];
    private readonly HashSet<Guid> _touchedSet = [], _touchedTrackSet = [];
    private bool _closed;

    public TimelineTransaction(TimelineDocument document, string label)
    {
        (_doc, Label) = (document, label);
        _doc.OpenTransactions.Add(this);
    }

    public TimelineDocument Document => _doc;
    public string Label { get; }
    public IReadOnlyList<Guid> TouchedClipIds => _touched;
    public IReadOnlyList<Guid> TouchedTrackIds => _touchedTracks;

    public void Commit()
    {
        EnsureInnermost();

        // The outermost commit of a change lets go of the stretches put back that are no longer there (4.4-BugHunt): only
        // after something else changed, so a commit of nothing still raises nothing, and an undo step's copy from before the
        // change keeps them.
        if (_doc.OpenTransactions.Count == 1 && (_touched.Count > 0 || _touchedTracks.Count > 0))
            PruneRestored();
        Close();
        if (_doc.OpenTransactions.Count > 0)
        {
            var outer = _doc.OpenTransactions[^1];
            foreach (var id in _touched)
                outer.TouchHere(id);
            foreach (var id in _touchedTracks)
                outer.TouchTrackHere(id);
            outer._documentTouched |= _documentTouched;
        }
        else if (_touched.Count > 0 || _touchedTracks.Count > 0 || _documentTouched)
        {
            _doc.RaiseCommitted(new TimelineCommit(Label, [.. _touched], [.. _touchedTracks]));
        }
    }

    public void Dispose()
    {
        if (_closed)
            return;

        // Transactions opened inside this one and still open are rolled back first.
        while (_doc.OpenTransactions.Count > 0 && !ReferenceEquals(_doc.OpenTransactions[^1], this))
            _doc.OpenTransactions[^1].Dispose();

        if (_trackOrderBefore is not null)
        {
            _doc.Tracks.Clear();
            _doc.Tracks.AddRange(_trackOrderBefore);
        }
        foreach (var (track, before) in _tracksBefore)
        {
            track.Clips.Clear();
            track.Clips.AddRange(before.Clips);
            track.RestoreFields(before.Fields);
        }
        foreach (var (clip, before) in _clipsBefore)
            clip.Restore(before);
        if (_foldsRemembered)
            (_doc.Folds, _doc.RestoredFolds) = (_foldsBefore, _restoredBefore);
        if (_masterGainBefore is { } gain)
            _doc.MasterGainDb = gain;
        if (_projectSettingsBefore is { } settings)
            (_doc.ColorSpace, _doc.PixelAspect, _doc.SampleRate) = settings;
        if (_mediaPoolBefore is { } pool)
            _doc.MediaPool = pool;
        Close();
    }

    private void Close()
    {
        _closed = true;
        _doc.OpenTransactions.Remove(this);
    }

    private void EnsureInnermost()
    {
        if (_closed)
            throw new InvalidOperationException($"The transaction \"{Label}\" is already committed or rolled back.");
        if (!ReferenceEquals(_doc.OpenTransactions[^1], this))
            throw new InvalidOperationException($"A transaction opened inside \"{Label}\" is still open.");
    }

    // ----- What a rollback puts back: each thing as it was when this transaction (and each one around it) first changed it -----

    private void Remember(TimelineClip clip)
    {
        foreach (var open in _doc.OpenTransactions)
        {
            if (!open._clipsBefore.ContainsKey(clip))
                open._clipsBefore[clip] = clip.Snapshot();
        }

        Touch(clip.Id);
    }

    private void Remember(TimelineTrackModel track)
    {
        foreach (var open in _doc.OpenTransactions)
            open._tracksBefore.TryAdd(track, ([.. track.Clips], track.Snapshot()));
        _doc.OpenTransactions[^1].TouchTrackHere(track.Id);
    }

    private void RememberTrackOrder()
    {
        foreach (var open in _doc.OpenTransactions)
            open._trackOrderBefore ??= [.. _doc.Tracks];
    }

    /// <summary>Both lists of folds, as they are before this change writes either (their records are never changed in place).</summary>
    private void RememberFolds()
    {
        foreach (var open in _doc.OpenTransactions)
        {
            if (!open._foldsRemembered)
            {
                (open._foldsBefore, open._restoredBefore) = (_doc.Folds is null ? null : [.. _doc.Folds], _doc.RestoredFolds is null ? null : [.. _doc.RestoredFolds]);
                open._foldsRemembered = true;
            }
        }
    }

    /// <summary>The Sequence's master gain as it is before this change writes it (5.2B); the change is the innermost transaction's to report.</summary>
    private void RememberMasterGain()
    {
        foreach (var open in _doc.OpenTransactions)
            open._masterGainBefore ??= _doc.MasterGainDb;
        _doc.OpenTransactions[^1]._documentTouched = true;
    }

    /// <summary>The project's colour space, pixel aspect and sample rate as they are before this change writes them (5.4A).</summary>
    private void RememberProjectSettings()
    {
        foreach (var open in _doc.OpenTransactions)
            open._projectSettingsBefore ??= (_doc.ColorSpace, _doc.PixelAspect, _doc.SampleRate);
        _doc.OpenTransactions[^1]._documentTouched = true;
    }

    /// <summary>Project Media as it is before this change writes it (5.4C): a copy of the list, which is then changed in place.</summary>
    private void RememberMediaPool()
    {
        foreach (var open in _doc.OpenTransactions)
            open._mediaPoolBefore ??= [.. _doc.MediaPool];
        _doc.OpenTransactions[^1]._documentTouched = true;
    }

    /// <summary>The files given that Project Media does not hold yet, each once, in order (empty paths and web addresses left out).</summary>
    private List<string> NewToPool(IEnumerable<string> paths)
    {
        var added = new List<string>();
        foreach (var given in paths)
        {
            var path = TimelineDocument.PoolPath(given);
            if (path.Length > 0 && !path.Contains("://", StringComparison.Ordinal) && !_doc.PoolHolds(path)
                && !added.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                added.Add(path);
            }
        }

        return added;
    }

    /// <summary>A clip just placed (5.4C): its file goes into Project Media, when the document pools placed files and does not hold it yet.</summary>
    private void PoolPlaced(TimelineClip clip)
    {
        if (!_doc.PoolsPlacedFiles || TimelineDocument.PoolFileOf(clip) is not { } path || _doc.PoolHolds(path))
            return;

        RememberMediaPool();
        _doc.MediaPool.Add(path);
    }

    /// <summary>Lets go of the stretches put back whose stretch is no longer there (<see cref="RestoredFold.SpanIn"/> null).</summary>
    private void PruneRestored()
    {
        if (_doc.RestoredFolds is not { Count: > 0 } restored)
            return;

        var there = _doc.RestoredSpans();
        if (there.Count == restored.Count)
            return;

        RememberFolds();
        _doc.RestoredFolds = there.Count == 0 ? null : [.. there.Select(s => s.Restored)];
    }

    // What was touched is the innermost open transaction's to report: a step that fails takes it back with it.
    private void Touch(Guid id) => _doc.OpenTransactions[^1].TouchHere(id);

    private void TouchHere(Guid id)
    {
        if (_touchedSet.Add(id))
            _touched.Add(id);
    }

    private void TouchTrackHere(Guid id)
    {
        if (_touchedTrackSet.Add(id))
            _touchedTracks.Add(id);
    }

    /// <summary>
    /// Runs one edit as a step of its own inside this transaction: an edit that throws has changed nothing, so a caller
    /// that catches the refusal can go on with the transaction.
    /// </summary>
    private T Step<T>(Func<T> edit)
    {
        EnsureInnermost();
        var step = new TimelineTransaction(_doc, Label);
        try
        {
            var result = edit();
            step.Commit();
            return result;
        }
        catch
        {
            step.Dispose();
            throw;
        }
    }

    private static double Finite(double seconds, string what) =>
        double.IsFinite(seconds) ? seconds : throw new TimelineEditException($"{what} is not a time ({seconds}).");

    // ----- Time on frames -----

    private FrameRate Rate => _doc.FrameRate;

    private bool Quantizing(bool free) => _doc.QuantizeToFrames && !free;

    // A time put on the nearest frame (Near), the frame at or before it (Down) or at or after it (Up); as it is when free.
    // Signed, so that a J/L cut's negative lead lands on frames too. A time already on a frame stays on it: the slack is
    // in seconds, the grain of a quantized time.
    private double Near(double seconds, bool free) => Snap(seconds, free, 0);
    private double Down(double seconds, bool free) => Snap(seconds, free, -1);
    private double Up(double seconds, bool free) => Snap(seconds, free, 1);

    private double Snap(double seconds, bool free, int way)
    {
        if (double.IsNaN(seconds))
            return seconds;

        seconds = Math.Clamp(seconds, -Farthest, Farthest);
        if (!Quantizing(free))
            return seconds;

        var frame = Math.Round(seconds * Rate.Fps, MidpointRounding.AwayFromZero);
        var at = FrameAt(frame);
        if (way < 0 && at > seconds + Slack)
            at = FrameAt(frame - 1);
        else if (way > 0 && at < seconds - Slack)
            at = FrameAt(frame + 1);
        return at;
    }

    /// <summary>A whole number of frames as its quantized time, negative ones too.</summary>
    private double FrameAt(double frame) =>
        frame < 0 ? -Rate.Quantize(-frame * Rate.FrameDuration) : Rate.Quantize(frame * Rate.FrameDuration);

    /// <summary>Whether a change of time is a whole number of frames of the document (while it quantizes).</summary>
    private bool WholeFrames(double delta) =>
        _doc.QuantizeToFrames && Math.Abs(delta - FrameAt(Math.Round(delta * Rate.Fps, MidpointRounding.AwayFromZero))) <= 2 * Slack;

    /// <summary>
    /// A time moved by <paramref name="delta"/> (a ripple, a held-back trim): moved by whole frames, one on a frame lands
    /// on that frame's exact time again; otherwise it moves as it is, as every other edge moved with it does, so two
    /// edges that were the same double stay the same double.
    /// </summary>
    private double Moved(double seconds, double delta, bool wholeFrames)
    {
        var moved = Math.Max(seconds + delta, 0);
        return wholeFrames && Rate.IsOnFrame(seconds) ? Rate.Quantize(moved) : moved;
    }

    private static void SetSpan(TimelineClip clip, double start, double end) => (clip.Start, clip.End) = (start, end);

    private double MinimumLength(bool free) => Up(TimelineDocument.MinimumClipSeconds, free);

    /// <summary>
    /// Where an edge is asked to go, on a frame: the nearest, unless that is the other way from the request (an edge off
    /// the frames asked to move by less than a frame), when it stays at <paramref name="current"/>.
    /// </summary>
    private double Wanted(double asked, double current, bool free)
    {
        var wanted = Near(asked, free);
        return asked == current || Math.Sign(wanted - current) != Math.Sign(asked - current) ? current : wanted;
    }

    /// <summary>
    /// Where an edge goes: <paramref name="wanted"/> held to <paramref name="earliest"/>..<paramref name="latest"/>, but
    /// never past the request nor the other way from it (then it stays at <paramref name="current"/>).
    /// </summary>
    private static double Toward(double current, double wanted, double earliest, double latest)
    {
        var to = Math.Clamp(wanted, earliest, Math.Max(latest, earliest));
        var (low, high) = wanted < current ? (wanted, current) : (current, wanted);
        return to >= low - 1e-12 && to <= high + 1e-12 ? to : current;
    }

    // ----- A clip's file and sound -----

    private static double LeadOf(TimelineClip clip) => clip.HasSound && clip.AudioLinked ? Math.Max(clip.AudioLeadInSeconds, 0) : 0;
    private static double TailOf(TimelineClip clip) => clip.HasSound && clip.AudioLinked ? Math.Max(clip.AudioTailOutSeconds, 0) : 0;

    private static bool Bounded(TimelineClip clip) => clip.PlaysFile && clip.SourceDuration > 0;

    /// <summary>Seconds of the timeline its file has before what it shows (less what a J-cut's sound already takes).</summary>
    private static double MediaBefore(TimelineClip clip) =>
        clip.PlaysFile ? Math.Max(clip.MediaOffset / clip.Speed - LeadOf(clip), 0) : double.PositiveInfinity;

    /// <summary>Seconds of the timeline its file has after what it shows (less what an L-cut's sound already takes).</summary>
    private static double MediaAfter(TimelineClip clip) =>
        Bounded(clip) ? Math.Max((clip.SourceDuration - clip.MediaEnd) / clip.Speed - TailOf(clip), 0) : double.PositiveInfinity;

    /// <summary>The latest a clip starting at <paramref name="start"/> may end, its file (and its L-cut's sound) permitting.</summary>
    private double FitEnd(TimelineClip clip, double start, double end, bool free) =>
        Bounded(clip) ? Math.Min(end, Down(start + (clip.SourceDuration - clip.MediaOffset) / clip.Speed - TailOf(clip), free)) : end;

    /// <summary>
    /// The shortest the clip's picture can be: the shortest clip, and long enough that its sound (from its lead-in to its
    /// tail, either of them negative when cut inside the picture) lasts the shortest clip too.
    /// </summary>
    private double ShortestFor(TimelineClip clip, bool free)
    {
        var minimum = MinimumLength(free);
        var sound = clip.HasSound && clip.AudioLinked ? clip.AudioLeadInSeconds + clip.AudioTailOutSeconds : 0;
        return Up(Math.Max(minimum, minimum - sound), free);
    }

    // ----- Tracks -----

    private TimelineTrackModel TrackOf(TimelineClip clip) =>
        _doc.TrackOf(clip) ?? throw new TimelineEditException("The clip is not on a track of this timeline.");

    private TimelineTrackModel Editable(TimelineTrackModel track)
    {
        if (!_doc.Tracks.Contains(track))
            throw new TimelineEditException($"{track.DisplayName} is not a track of this timeline.");
        if (track.IsLocked)
            throw new TimelineEditException($"{track.DisplayName} is locked.");
        return track;
    }

    /// <summary>Takes a clip out of a track's list: refused (4.4 BugHunt) rather than a fault when it is not there.</summary>
    private static void RemoveFrom(TimelineTrackModel track, TimelineClip clip)
    {
        var index = track.IndexOf(clip);
        if (index < 0)
            throw new TimelineEditException($"The clip is no longer on {track.DisplayName}.");
        track.Clips.RemoveAt(index);
    }

    private static bool Meet(TimelineClip earlier, TimelineClip later) => Math.Abs(earlier.End - later.Start) <= Slack;

    /// <summary>A clip of the track other than <paramref name="ignore"/> that the stretch would overlap, or null.</summary>
    private static TimelineClip? Overlapped(TimelineTrackModel track, double start, double end, TimelineClip? ignore)
    {
        foreach (var clip in track.ClipsIntersecting(start + Slack, end - Slack))
        {
            if (!ReferenceEquals(clip, ignore))
                return clip;
        }
        return null;
    }

    private static void Insert(TimelineTrackModel track, TimelineClip clip)
    {
        int low = 0, high = track.Clips.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (track.Clips[middle].Start <= clip.Start)
                low = middle + 1;
            else
                high = middle;
        }
        track.Clips.Insert(low, clip);
    }

    /// <summary>
    /// A crossfade stays only where its clip still meets the one before it, and is never longer than either of the two
    /// (4.3C: a trim or a speed could leave one longer than its clips).
    /// </summary>
    private void Reconcile(TimelineTrackModel track)
    {
        for (var i = 0; i < track.Clips.Count; i++)
        {
            var clip = track.Clips[i];
            if (clip.TransitionIn == 0)
                continue;

            var longest = i == 0 || !Meet(track.Clips[i - 1], clip) ? 0 : Math.Min(track.Clips[i - 1].Duration, clip.Duration);
            if (clip.TransitionIn > longest)
            {
                Remember(clip);
                clip.TransitionIn = longest;
            }
        }
    }

    private void Renumber()
    {
        foreach (var kind in Enum.GetValues<TimelineTrackKind>())
        {
            var number = 0;
            foreach (var track in _doc.Tracks)
            {
                if (track.Kind != kind || track.TrackNumber == ++number)
                    continue;
                Remember(track);
                track.TrackNumber = number;
            }
        }
    }

    // ----- The edits, each one a step (Step) -----

    public TimelineTrackModel InsertTrack(int index, TimelineTrackKind kind, string? name = null) =>
        Step(() => AddTrack(index, kind, name));

    public TimelineClip PlaceClip(TimelineTrackModel track, TimelineClip clip, bool allowCrossfadeOverlap = true, bool freeUnquantized = false) =>
        Step(() => Place(track, clip, allowCrossfadeOverlap, freeUnquantized));

    public void MoveClip(TimelineClip clip, TimelineTrackModel targetTrack, double newStart, bool ripple = false, int insertTrackAtIndex = -1,
                         bool allowCrossfadeOverlap = true, bool freeUnquantized = false) =>
        Step(() => Move(clip, targetTrack, newStart, ripple, insertTrackAtIndex, allowCrossfadeOverlap, freeUnquantized));

    public TimelineClip SplitClipAt(TimelineClip clip, double sequenceTime, bool freeUnquantized = false) =>
        Step(() => Split(clip, sequenceTime, freeUnquantized));

    public double TrimClipStart(TimelineClip clip, double newStart, bool ripple = false, bool rippleAllUnlockedTracks = true, bool freeUnquantized = false) =>
        Step(() => TrimStart(clip, newStart, ripple, rippleAllUnlockedTracks, freeUnquantized));

    public double TrimClipEnd(TimelineClip clip, double newEnd, bool ripple = false, bool rippleAllUnlockedTracks = true, bool freeUnquantized = false) =>
        Step(() => TrimEnd(clip, newEnd, ripple, rippleAllUnlockedTracks, freeUnquantized));

    public (double LeadIn, double TailOut) TrimAudioSplit(TimelineClip clip, double newAudioLeadIn, double newAudioTailOut, bool freeUnquantized = false) =>
        Step(() => SplitSound(clip, newAudioLeadIn, newAudioTailOut, freeUnquantized));

    public double RollCut(TimelineClip earlier, TimelineClip later, double newCutTime, bool freeUnquantized = false) =>
        Step(() => Roll(earlier, later, newCutTime, freeUnquantized));

    public double SlipClip(TimelineClip clip, double mediaDeltaSeconds) => Step(() => Slip(clip, mediaDeltaSeconds));

    public void DeleteClip(TimelineClip clip, bool withChildren = true) => Step(() => Lift(clip, family: withChildren));

    public double RippleDelete(TimelineClip clip, bool rippleAllUnlockedTracks) =>
        Step(() => LiftRippling(clip, rippleAllUnlockedTracks));

    public double CloseGap(TimelineTrackModel track, double gapStart, double gapEnd, bool rippleAllUnlockedTracks) =>
        Step(() => CloseUp(track, gapStart, gapEnd, rippleAllUnlockedTracks));

    public (double In, double Out) SetClipFades(TimelineClip clip, double fadeInSeconds, double fadeOutSeconds, TransitionStyle? fadeInStyle = null,
                                                TransitionStyle? fadeOutStyle = null) =>
        Step(() => Fades(clip, fadeInSeconds, fadeOutSeconds, fadeInStyle, fadeOutStyle));

    public double SetCrossfade(TimelineClip clip, double seconds) => Step(() => Crossfade(clip, seconds));

    public void SetClipGain(TimelineClip clip, double gainDb) => Step(() => Change(clip, c =>
        c.GainDb = Math.Clamp(double.IsFinite(gainDb) ? gainDb : 0, -40, 24)));

    public void SetClipFlags(TimelineClip clip, bool? hidden = null, bool? muted = null) => Step(() => Change(clip, c =>
        (c.IsHidden, c.IsMuted) = (hidden ?? c.IsHidden, muted ?? c.IsMuted)));

    public void SetClipProxy(TimelineClip clip, ProxyChoice choice) => Step(() => Change(clip, c => c.ProxyChoice = choice));

    public void SetClipBeats(TimelineClip clip, double[]? beats) => Step(() =>
    {
        // A new array of its own (the clip's is never changed in place): finite, from 0, in order, a microsecond apart at least.
        double[]? kept = null;
        if (beats is not null)
        {
            var times = new List<double>(beats.Length);
            foreach (var time in beats.Where(double.IsFinite).Select(t => Math.Max(t, 0)).Order())
            {
                if (times.Count == 0 || time - times[^1] >= Slack)
                    times.Add(time);
            }

            kept = [.. times];
        }

        if (kept is null ? clip.BeatTimes is null : clip.BeatTimes is { } had && had.AsSpan().SequenceEqual(kept.AsSpan()))
            return false;
        return Change(clip, c => c.BeatTimes = kept);
    });

    public void SetMasterGain(double decibels) => Step(() =>
    {
        var gain = double.IsFinite(decibels) ? Math.Clamp(decibels, TimelineDocument.QuietestMasterGainDb, TimelineDocument.LoudestMasterGainDb) : 0;
        if (gain == _doc.MasterGainDb)
            return false;

        RememberMasterGain();
        _doc.MasterGainDb = gain;
        return true;
    });

    public void SetProjectSettings(ProjectColorSpace colorSpace, ProjectPixelAspect pixelAspect, int sampleRate) => Step(() =>
    {
        var rate = sampleRate is 44100 or 48000 or 96000 ? sampleRate : TimelineDocument.DefaultSampleRate;
        if (colorSpace == _doc.ColorSpace && pixelAspect == _doc.PixelAspect && rate == _doc.SampleRate)
            return false;

        RememberProjectSettings();
        (_doc.ColorSpace, _doc.PixelAspect, _doc.SampleRate) = (colorSpace, pixelAspect, rate);
        return true;
    });

    public bool AddToMediaPool(IEnumerable<string> paths) => Step(() =>
    {
        var added = NewToPool(paths);
        if (added.Count == 0)
            return false;

        RememberMediaPool();
        _doc.MediaPool.AddRange(added);
        return true;
    });

    public bool RemoveFromMediaPool(IEnumerable<string> paths) => Step(() =>
    {
        var gone = paths.Select(TimelineDocument.PoolPath).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_doc.MediaPool.Exists(p => gone.Contains(TimelineDocument.PoolPath(p))))
            return false;

        RememberMediaPool();
        _doc.MediaPool.RemoveAll(p => gone.Contains(TimelineDocument.PoolPath(p)));
        return true;
    });

    public double SetClipSpeed(TimelineClip clip, double speed, bool freeUnquantized = false) => Step(() => Respeed(clip, speed, freeUnquantized));

    public void SetAudioLink(TimelineClip clip, bool linked, double slipSeconds) => Step(() => Change(clip, c =>
    {
        (c.AudioLinked, c.AudioSlipSeconds) = (linked, double.IsFinite(slipSeconds) ? slipSeconds : 0);

        // Linked again: a J or L cut kept while it was not (moves and trims left it alone) is held, on frames, to the
        // timeline's start and to its file, as one made now would be.
        if (linked && c.HasSound)
        {
            if (c.AudioLeadInSeconds > 0)
                c.AudioLeadInSeconds = Math.Max(Down(Math.Min(c.AudioLeadInSeconds, Math.Min(c.Start, c.PlaysFile ? c.MediaOffset / c.Speed : double.PositiveInfinity)), false), 0);
            if (c.AudioTailOutSeconds > 0 && Bounded(c))
                c.AudioTailOutSeconds = Math.Max(Down(Math.Min(c.AudioTailOutSeconds, Math.Max((c.SourceDuration - c.MediaEnd) / c.Speed, 0)), false), 0);

            // A cut inside a picture trimmed shorter meanwhile may leave its sound too short: then the sound is the picture's.
            if (c.Duration + c.AudioLeadInSeconds + c.AudioTailOutSeconds < MinimumLength(false) - Slack)
                (c.AudioLeadInSeconds, c.AudioTailOutSeconds) = (Math.Max(c.AudioLeadInSeconds, 0), Math.Max(c.AudioTailOutSeconds, 0));
        }
    }));

    public void SetTrack(TimelineTrackModel track, bool? hidden = null, bool? muted = null, bool? soloed = null, bool? locked = null,
                         double? gainDb = null, string? name = null) => Step(() =>
    {
        if (!_doc.Tracks.Contains(track))
            throw new TimelineEditException($"{track.DisplayName} is not a track of this timeline.");

        var flags = (hidden ?? track.IsHidden, muted ?? track.IsMuted, soloed ?? track.IsSoloed, locked ?? track.IsLocked);
        var setGain = gainDb is { } gain ? Math.Clamp(double.IsFinite(gain) ? gain : 0, -40, 24) : track.GainDb;
        var setName = name?.Trim() ?? track.Name;
        if (flags == (track.IsHidden, track.IsMuted, track.IsSoloed, track.IsLocked) && setGain == track.GainDb && setName == track.Name)
            return false;

        Remember(track);
        (track.IsHidden, track.IsMuted, track.IsSoloed, track.IsLocked) = flags;
        (track.GainDb, track.Name) = (setGain, setName);
        return true;
    });

    public void RemoveTrack(TimelineTrackModel track) => Step(() =>
    {
        Editable(track);
        foreach (var clip in track.Clips)
            Remember(clip);
        Remember(track);
        RememberTrackOrder();
        var at = _doc.Tracks.IndexOf(track);
        _doc.Tracks.Remove(track);

        // The captions under it keep their depth (5.1-AutoHud-Part1, the user's Q4): under the next picture track down; at the
        // bottom of the stack, under the lowest that is left. With no picture track left they are over everything.
        if (track.CaptionsUnder)
        {
            track.CaptionsUnder = false;
            var below = at <= 0 ? null : _doc.Tracks.Take(at).LastOrDefault(ShowsPictures);
            if ((below ?? _doc.Tracks.FirstOrDefault(ShowsPictures)) is { } next)
            {
                Remember(next);
                next.CaptionsUnder = true;
            }
        }

        // The tracks under it stay (4.4: a track whose last clip moved away is taken off, and theirs must not go with it):
        // they lie under its own parent track now, or under none.
        foreach (var child in _doc.Tracks)
        {
            if (child.ParentTrackId != track.Id)
                continue;
            Remember(child);
            child.ParentTrackId = track.ParentTrackId == child.Id ? null : track.ParentTrackId;
        }
        Renumber();
        return true;
    });

    public FoldedSegment? FoldStretch(TimelineTrackModel seamTrack, double from, double to, bool rippleAllUnlockedTracks) =>
        Step(() => Fold(seamTrack, from, to, rippleAllUnlockedTracks));

    public void UnfoldSegment(FoldedSegment fold) => Step(() => Unfold(fold));

    public FoldedSegment FoldRestored(RestoredFold restored) => Step(() => FoldAgain(restored));

    public FoldedSegment? ResizeFold(FoldedSegment fold, FoldSide side, double seconds) => Step(() => Resize(fold, side, seconds));

    public void MoveTrack(TimelineTrackModel track, int index) => Step(() =>
    {
        if (!_doc.Tracks.Contains(track))
            throw new TimelineEditException($"{track.DisplayName} is not a track of this timeline.");
        index = Math.Clamp(index, 0, _doc.Tracks.Count - 1);
        if (_doc.Tracks.IndexOf(track) == index)
            return false;
        RememberTrackOrder();
        Remember(track);
        _doc.Tracks.Remove(track);
        _doc.Tracks.Insert(index, track);
        Renumber();
        return true;
    });

    public void SetClipParent(TimelineClip clip, Guid? parentClipId) => Step(() =>
    {
        if (parentClipId is { } id)
        {
            if (id == clip.Id)
                throw new TimelineEditException("A clip cannot be its own parent.");
            var parent = _doc.FindClip(id) ?? throw new TimelineEditException("The parent clip is not on this timeline.");

            // Not one that goes with this clip already: followed up from the parent, the links never come back to it.
            var (up, seen) = (parent, new HashSet<Guid>());
            while (up.ParentClipId is { } above && seen.Add(above))
            {
                if (above == clip.Id)
                    throw new TimelineEditException("That clip goes with this one already: a clip cannot go with what goes with it.");
                if (_doc.FindClip(above) is not { } next)
                    break;
                up = next;
            }
        }
        return Change(clip, c => c.ParentClipId = parentClipId);
    });

    public void SetTrackParent(TimelineTrackModel track, Guid? parentTrackId) => Step(() =>
    {
        if (!_doc.Tracks.Contains(track))
            throw new TimelineEditException($"{track.DisplayName} is not a track of this timeline.");
        if (parentTrackId is { } id)
        {
            if (id == track.Id)
                throw new TimelineEditException($"{track.DisplayName} cannot lie under itself.");
            var parent = TrackById(id) ?? throw new TimelineEditException("The parent track is not on this timeline.");
            if (Under(parent, track))
                throw new TimelineEditException($"{parent.DisplayName} lies under {track.DisplayName} already.");
        }
        if (track.ParentTrackId == parentTrackId)
            return false;

        Remember(track);
        track.ParentTrackId = parentTrackId;
        return true;
    });

    private static bool ShowsPictures(TimelineTrackModel track) => track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch;

    public void SetTrackHeight(TimelineTrackModel track, double? height) => Step(() =>
    {
        if (!_doc.Tracks.Contains(track))
            throw new TimelineEditException($"{track.DisplayName} is not a track of this timeline.");
        if (track.Height == height)
            return false;

        Remember(track);
        track.Height = height;
        return true;
    });

    public void SetTrackPreset(TimelineTrackModel track, string? presetName) => Step(() =>
    {
        if (!_doc.Tracks.Contains(track))
            throw new TimelineEditException($"{track.DisplayName} is not a track of this timeline.");
        if (!ShowsPictures(track))
            throw new TimelineEditException($"{track.DisplayName} holds no pictures: a Game Preset goes on a video track.");

        var name = string.IsNullOrWhiteSpace(presetName) ? null : presetName.Trim();
        if (track.PresetNodeName == name)
            return false;

        Remember(track);
        track.PresetNodeName = name;
        return true;
    });

    public void SetCaptionsUnder(TimelineTrackModel? track) => Step(() =>
    {
        if (track is not null && !_doc.Tracks.Contains(track))
            throw new TimelineEditException($"{track.DisplayName} is not a track of this timeline.");

        var changed = false;
        foreach (var other in _doc.Tracks)
        {
            var under = ReferenceEquals(other, track);
            if (other.CaptionsUnder == under)
                continue;
            Remember(other);
            other.CaptionsUnder = under;
            changed = true;
        }

        return changed;
    });

    /// <summary>
    /// A change to a clip's own values on an unlocked track, remembered first. One that changes nothing is no change: the
    /// clip is not touched, so a commit of nothing else raises no Committed (and Undo takes no step).
    /// </summary>
    private bool Change(TimelineClip clip, Action<TimelineClip> change)
    {
        Editable(TrackOf(clip));
        var before = clip.Snapshot();
        change(clip);
        if (clip.SameValues(before))
            return false;

        var after = clip.Snapshot();
        clip.Restore(before);
        Remember(clip);
        clip.Restore(after);
        return true;
    }

    private (double In, double Out) Fades(TimelineClip clip, double fadeIn, double fadeOut, TransitionStyle? inStyle, TransitionStyle? outStyle)
    {
        Editable(TrackOf(clip));
        var room = Math.Min(clip.Duration, TimelineDocument.LongestFade);
        var fadeInSet = Math.Clamp(double.IsFinite(fadeIn) ? fadeIn : clip.FadeInSeconds, 0, room);
        var fadeOutSet = Math.Clamp(double.IsFinite(fadeOut) ? fadeOut : clip.FadeOutSeconds, 0, Math.Min(room, clip.Duration - fadeInSet));
        Change(clip, c =>
        {
            (c.FadeInSeconds, c.FadeOutSeconds) = (fadeInSet, fadeOutSet);
            (c.FadeInStyle, c.FadeOutStyle) = (inStyle ?? c.FadeInStyle, outStyle ?? c.FadeOutStyle);
        });
        return (fadeInSet, fadeOutSet);
    }

    private double Crossfade(TimelineClip clip, double seconds)
    {
        var track = Editable(TrackOf(clip));
        var index = track.IndexOf(clip);
        var wanted = double.IsFinite(seconds) ? Math.Max(seconds, 0) : 0;
        if (wanted > 0 && (index <= 0 || !Meet(track.Clips[index - 1], clip)))
            throw new TimelineEditException("A crossfade needs the clip before it on the track to end where this one starts.");

        var set = index <= 0 ? 0 : Math.Min(wanted, Math.Min(TimelineDocument.LongestCrossfade, Math.Min(track.Clips[index - 1].Duration, clip.Duration)));
        Change(clip, c => c.TransitionIn = set);
        return set;
    }

    private double Respeed(TimelineClip clip, double speed, bool free)
    {
        var track = Editable(TrackOf(clip));
        if (!clip.PlaysFile || !double.IsFinite(speed))
            return clip.Speed;

        speed = Math.Round(Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed), 3);
        var stretch = clip.Speed / speed;
        if (Math.Abs(stretch - 1) < 1e-6)
            return clip.Speed;

        // The same stretch of the file, so the end moves; on a frame, and never past the file's end.
        // A J/L cut's sound reads the same stretch of its file too: its lead and tail are scaled, then held (on frames) to
        // the timeline's start and the file, as a J/L cut is. A refusal is a step's: the clip is put back.
        var media = clip.MediaEnd - clip.MediaOffset;
        var oldEnd = clip.End;
        var (lead, tail) = (clip.AudioLeadInSeconds * stretch, clip.AudioTailOutSeconds * stretch);
        Remember(clip);
        clip.Speed = speed;
        if (lead > 0)
            lead = Down(Math.Min(lead, Math.Min(clip.Start, clip.PlaysFile ? clip.MediaOffset / speed : double.PositiveInfinity)), free);
        if (tail > 0 && Bounded(clip))
            tail = Down(Math.Min(tail, Math.Max((clip.SourceDuration - clip.MediaOffset - media) / speed, 0)), free);
        (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) = (lead > 0 ? Math.Max(lead, 0) : Near(lead, free), tail > 0 ? Math.Max(tail, 0) : Near(tail, free));
        var end = FitEnd(clip, clip.Start, Near(clip.Start + media / speed, free), free);
        if (!(end - clip.Start >= ShortestFor(clip, free) - Slack))
            throw new TimelineEditException("At that speed the clip would be shorter than the shortest clip.");

        // Its new end first, so that what follows meets it again; then what it grows into on its track moves on with it,
        // as far as it grew (a crossfade into the next clip stays: they still meet).
        var grows = end > oldEnd + Slack && track.IndexOf(clip) is var index && index + 1 < track.Clips.Count && track.Clips[index + 1].Start < end - Slack;
        SetSpan(clip, clip.Start, end);
        if (grows)
            Ripple([track], oldEnd, end - oldEnd, clip);
        clip.FadeInSeconds = Math.Min(clip.FadeInSeconds, clip.Duration);
        clip.FadeOutSeconds = Math.Min(clip.FadeOutSeconds, clip.Duration - clip.FadeInSeconds);
        clip.State = Scaled(clip.State, stretch);
        Reconcile(track);
        return speed;
    }

    private TimelineTrackModel AddTrack(int index, TimelineTrackKind kind, string? name)
    {
        RememberTrackOrder();
        var track = new TimelineTrackModel { Kind = kind, Name = name ?? "" };
        _doc.Tracks.Insert(Math.Clamp(index, 0, _doc.Tracks.Count), track);
        Remember(track);
        Renumber();
        return track;
    }

    private TimelineClip Place(TimelineTrackModel track, TimelineClip clip, bool allowCrossfadeOverlap, bool free)
    {
        Editable(track);
        if (!track.Accepts(clip))
            throw new TimelineEditException($"A {clip.ContentKind} clip does not go on {track.DisplayName}.");
        if (_doc.TrackOf(clip) is not null || _doc.FindClip(clip.Id) is not null)
            throw new TimelineEditException("The clip is on the timeline already.");
        Finite(clip.Start, "The clip's start");
        Finite(clip.End, "The clip's end");

        var given = clip.Snapshot();
        try
        {
            // Made usable first: a speed in range, an offset inside the file, fades and a crossfade within their
            // limits, a J-cut's sound inside its file.
            clip.Speed = double.IsFinite(clip.Speed) && clip.Speed > 0 ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;
            if (clip.PlaysFile)
                clip.MediaOffset = double.IsFinite(clip.MediaOffset) ? Math.Max(clip.MediaOffset, 0) : 0;
            clip.FadeInSeconds = Math.Clamp(double.IsFinite(clip.FadeInSeconds) ? clip.FadeInSeconds : 0, 0, TimelineDocument.LongestFade);
            clip.FadeOutSeconds = Math.Clamp(double.IsFinite(clip.FadeOutSeconds) ? clip.FadeOutSeconds : 0, 0, TimelineDocument.LongestFade);
            clip.TransitionIn = Math.Clamp(double.IsFinite(clip.TransitionIn) ? clip.TransitionIn : 0, 0, TimelineDocument.LongestCrossfade);
            clip.AudioSlipSeconds = double.IsFinite(clip.AudioSlipSeconds) ? clip.AudioSlipSeconds : 0;
            clip.AudioLeadInSeconds = double.IsFinite(clip.AudioLeadInSeconds) ? Near(clip.AudioLeadInSeconds, free) : 0;
            clip.AudioTailOutSeconds = double.IsFinite(clip.AudioTailOutSeconds) ? Near(clip.AudioTailOutSeconds, free) : 0;
            if (clip.PlaysFile && clip.AudioLeadInSeconds > 0)
                clip.AudioLeadInSeconds = Math.Min(clip.AudioLeadInSeconds, Down(clip.MediaOffset / clip.Speed, free));

            var length = clip.End - clip.Start;
            var start = Math.Max(Near(Math.Max(clip.Start, 0), free), Up(LeadOf(clip), free));
            var end = FitEnd(clip, start, Near(start + length, free), free);
            if (!(end - start >= ShortestFor(clip, free) - Slack))
                throw new TimelineEditException("The clip would be shorter than the shortest clip (or its file has nothing left from its offset).");

            Land(track, clip, start, end, allowCrossfadeOverlap, free);

            // Its file is in Project Media from now on (5.4C), until taken out of the bin: deleting the clip leaves it there.
            PoolPlaced(clip);
            return clip;
        }
        catch
        {
            clip.Restore(given);
            throw;
        }
    }

    /// <summary>Puts a clip that is on no track onto one, from <paramref name="start"/> to <paramref name="end"/>, or over a neighbour into a crossfade.</summary>
    private void Land(TimelineTrackModel track, TimelineClip clip, double start, double end, bool allowCrossfadeOverlap, bool free)
    {
        TimelineClip? later = null;
        var crossfade = 0.0;
        if (Overlapped(track, start, end, null) is { } hit)
        {
            // Dropped over a neighbour by a little (4.2.0's drag-over crossfade, from a frame on): put against it, the
            // later of the two crossfading from the earlier.
            var length = end - start;
            var overlap = Math.Min(end, hit.End) - Math.Max(start, hit.Start);
            var least = Quantizing(free) ? Rate.FrameDuration - 2 * Slack : 0.02;
            var dragOver = allowCrossfadeOverlap && overlap > least
                           && overlap <= Math.Min(TimelineDocument.LongestDragCrossfade, Math.Min(length, hit.Duration) / 2) + Slack;
            if (dragOver)
            {
                // Put against it on the frames inside the clip's own length, so it never reads further into its file.
                (start, later) = start > hit.Start ? (hit.End, clip) : (Up(hit.Start - length, free), hit);
                end = !ReferenceEquals(later, clip) ? hit.Start : FitEnd(clip, hit.End, Down(hit.End + length, free), free);
                crossfade = Math.Min(Quantizing(free) ? Near(overlap, free) : Math.Round(overlap, 3), TimelineDocument.LongestCrossfade);
            }

            if (!dragOver || start < LeadOf(clip) - Slack || !(end - start >= ShortestFor(clip, free) - Slack) || Overlapped(track, start, end, null) is not null)
                throw new TimelineEditException($"There is no room on {track.DisplayName} from {Seconds(start)} to {Seconds(end)} s.");
        }

        Remember(track);
        SetSpan(clip, start, end);
        Insert(track, clip);
        Touch(clip.Id);
        if (later is not null)
        {
            Remember(later);
            later.TransitionIn = crossfade;
        }
        Reconcile(track);
    }

    private bool Move(TimelineClip clip, TimelineTrackModel targetTrack, double newStart, bool ripple, int insertTrackAtIndex,
                      bool allowCrossfadeOverlap, bool free)
    {
        Finite(newStart, "The new start");
        var source = Editable(TrackOf(clip));
        if (!targetTrack.Accepts(clip))
            throw new TimelineEditException($"A {clip.ContentKind} clip does not go on {targetTrack.DisplayName}.");

        // Its sound (a J-cut's lead-in) does not start before the timeline either.
        var start = Math.Max(Near(Math.Max(newStart, 0), free), Up(LeadOf(clip), free));
        if (!ripple && insertTrackAtIndex < 0 && ReferenceEquals(targetTrack, source) && start == clip.Start)
            return false;

        if (insertTrackAtIndex >= 0)
            targetTrack = AddTrack(insertTrackAtIndex, targetTrack.Kind, null);
        Editable(targetTrack);

        // What goes with it (4.4): its children that lie with it, and theirs, each to move by as much on its own track.
        var family = Family(clip);

        // Lifted. The crossfade it had belongs to the cut it leaves, unless it lands against the same clip again.
        var index = source.IndexOf(clip);
        var met = index > 0 && Meet(source.Clips[index - 1], clip) ? source.Clips[index - 1] : null;
        var (oldStart, oldEnd, length, crossfade) = (clip.Start, clip.End, clip.Duration, clip.TransitionIn);
        Remember(source);
        Remember(clip);
        source.Clips.RemoveAt(index);
        clip.TransitionIn = 0;
        var riding = family is null ? null : TakeOff(family);
        if (ripple)
            Ripple(WithFamilyTracks(source, family), oldEnd, -(oldEnd - oldStart), null);

        if (ripple)
        {
            if (targetTrack.ClipsIntersecting(start + Slack, start + Slack * 2) is [var under, ..] && under.Start < start - Slack)
                throw new TimelineEditException($"An inserting move cannot land inside a clip ({Seconds(under.Start)} to {Seconds(under.End)} s on {targetTrack.DisplayName}).");
            Ripple(WithFamilyTracks(targetTrack, family), start, length, null);
        }

        var end = FitEnd(clip, start, Quantizing(free) ? Near(start + length, free) : start + length, free);
        if (!(end - start >= ShortestFor(clip, free) - Slack))
            throw new TimelineEditException("The clip would be shorter than the shortest clip there.");
        Land(targetTrack, clip, start, end, allowCrossfadeOverlap, free);

        var at = targetTrack.IndexOf(clip);
        if (clip.TransitionIn == 0 && crossfade > 0 && met is not null && at > 0 && ReferenceEquals(targetTrack.Clips[at - 1], met) && Meet(met, clip))
            clip.TransitionIn = crossfade;
        if (riding is not null)
            LandFamily(riding, clip.Start - oldStart);
        Reconcile(source);
        if (!ReferenceEquals(source, targetTrack))
            FollowTrack(source, targetTrack);
        return true;
    }

    private TimelineClip Split(TimelineClip clip, double sequenceTime, bool free)
    {
        var track = Editable(TrackOf(clip));
        var at = Near(Finite(sequenceTime, "The split time"), free);
        var minimum = MinimumLength(free);
        var linked = clip.HasSound && clip.AudioLinked;
        var firstSound = at - clip.Start + (linked ? clip.AudioLeadInSeconds : 0);
        var secondSound = clip.End - at + (linked ? clip.AudioTailOutSeconds : 0);
        if (!(at - clip.Start >= minimum - Slack && clip.End - at >= minimum - Slack && firstSound >= minimum - Slack && secondSound >= minimum - Slack))
            throw new TimelineEditException($"{Seconds(at)} s is not inside the clip ({Seconds(clip.Start)} to {Seconds(clip.End)} s) by the shortest clip.");

        // Its children past the cut (4.4), found before it is cut.
        var children = ChildrenWith(clip, at, clip.End);

        Remember(track);
        Remember(clip);
        var end = clip.End;

        var second = clip.Snapshot();
        second.Id = Guid.NewGuid();
        (clip.State, second.State) = SplitLook(clip.State, at - clip.Start);
        SetSpan(second, at, end);
        if (clip.PlaysFile)
            second.MediaOffset = clip.MediaOffset + (at - clip.Start) * clip.Speed;
        (second.FadeInSeconds, second.TransitionIn, second.AudioLeadInSeconds) = (0, 0, 0);
        second.FadeOutSeconds = Math.Min(clip.FadeOutSeconds, second.Duration);

        SetSpan(clip, clip.Start, at);
        (clip.FadeOutSeconds, clip.AudioTailOutSeconds) = (0, 0);
        clip.FadeInSeconds = Math.Min(clip.FadeInSeconds, clip.Duration);

        track.Clips.Insert(track.IndexOf(clip) + 1, second);
        Touch(second.Id);
        _splits?.Add((clip, second));

        // Its children go with the halves (4.4): one across the cut is cut there too, its second part the second half's, and
        // one wholly after the cut is the second half's. One that cannot be cut there (too near its own edge, its sound too
        // short, on a locked track) stays whole with the first half, where it starts.
        if (children is not null)
        {
            foreach (var (_, child) in children)
            {
                if (child.Start >= at - Slack)
                {
                    Remember(child);
                    child.ParentClipId = second.Id;
                    continue;
                }

                try
                {
                    Split(child, at, free).ParentClipId = second.Id;
                }
                catch (TimelineEditException)
                {
                }
            }
        }
        return second;
    }

    private double TrimStart(TimelineClip clip, double newStart, bool ripple, bool rippleAll, bool free)
    {
        var track = Editable(TrackOf(clip));
        if (!double.IsFinite(newStart))
            return 0;

        var wanted = Wanted(newStart, clip.Start, free);
        var latest = Down(clip.End - ShortestFor(clip, free), free);
        var earliest = Up(clip.Start - MediaBefore(clip), free);
        if (!ripple)
        {
            // Not before the timeline's start (nor its sound, a J-cut's lead-in), nor into the clip before it.
            earliest = Math.Max(earliest, Up(LeadOf(clip), free));
            var index = track.IndexOf(clip);
            if (index > 0)
                earliest = Math.Max(earliest, track.Clips[index - 1].End);
        }
        if (earliest > latest + Slack)
            return 0;

        var start = Toward(clip.Start, wanted, earliest, latest);
        var delta = start - clip.Start;
        var tracks = ripple ? RippleTracks(track, rippleAll) : [];
        var (oldStart, oldEnd) = (clip.Start, clip.End);

        // What rides a ripple of it (4.4): its children on its start or its end, or in the stretch its end may give up.
        var riders = ripple ? Riders(clip, head: true, lostFrom: oldEnd - Math.Max(delta, 0)) : null;
        if (ripple && delta > 0)
        {
            // What follows must be able to come back with the clip's end (4.2.0 trimmed it all and left the gap).
            var follow = RippleLimit(tracks, clip.End, -delta, clip, riders);
            if (-follow < delta)
                delta = -follow;
        }
        if (Math.Abs(delta) <= Slack / 1000)
            return 0;

        Remember(clip);
        clip.State = Shifted(clip.State, -delta);
        if (clip.PlaysFile)
            clip.MediaOffset = Math.Max(clip.MediaOffset + delta * clip.Speed, 0);

        if (!ripple)
        {
            // The very double it was held to: the end of the clip before it, when that is what stopped it.
            SetSpan(clip, start, clip.End);
            Reconcile(track);
            FollowStart(clip, oldStart, oldEnd);
            return delta;
        }

        // Ripple: the clip keeps its start and loses (or gains) the change at its head; what follows its old end follows it.
        var whole = WholeFrames(delta);
        SetSpan(clip, clip.Start, Moved(oldEnd, -delta, whole));
        if (riders is not null && delta > 0)
            Ride(riders, oldStart, oldEnd, clip.End, -delta, whole, head: true, free, tracks);
        Ripple(tracks, oldEnd, -delta, clip, riders);
        if (riders is not null && delta < 0)
            Ride(riders, oldStart, oldEnd, clip.End, -delta, whole, head: true, free, tracks);
        return delta;
    }

    private double TrimEnd(TimelineClip clip, double newEnd, bool ripple, bool rippleAll, bool free)
    {
        var track = Editable(TrackOf(clip));
        if (!double.IsFinite(newEnd))
            return 0;

        var wanted = Wanted(newEnd, clip.End, free);
        var earliest = Up(clip.Start + ShortestFor(clip, free), free);
        var latest = Down(clip.End + MediaAfter(clip), free);
        if (!ripple)
        {
            var index = track.IndexOf(clip);
            if (index < track.Clips.Count - 1)
                latest = Math.Min(latest, track.Clips[index + 1].Start);
        }
        if (earliest > latest + Slack)
            return 0;

        var oldEnd = clip.End;
        var end = Toward(oldEnd, wanted, earliest, latest);
        var delta = end - oldEnd;
        var tracks = ripple ? RippleTracks(track, rippleAll) : [];

        // What rides a ripple of it (4.4): its children on its end, or in the stretch it may give up.
        var riders = ripple ? Riders(clip, head: false, lostFrom: Math.Min(end, oldEnd)) : null;
        if (ripple && delta < 0)
            delta = Math.Max(delta, RippleLimit(tracks, oldEnd, delta, clip, riders));
        if (Math.Abs(delta) <= Slack / 1000)
            return 0;

        // Rippling, the end moves as the clips after it will, so the one that met it meets it again on the same double.
        Remember(clip);
        var whole = WholeFrames(delta);
        SetSpan(clip, clip.Start, ripple ? Moved(oldEnd, delta, whole) : end);
        if (ripple)
        {
            if (riders is not null && delta < 0)
                Ride(riders, clip.Start, oldEnd, clip.End, delta, whole, head: false, free, tracks);
            Ripple(tracks, oldEnd, delta, clip, riders);
            if (riders is not null && delta > 0)
                Ride(riders, clip.Start, oldEnd, clip.End, delta, whole, head: false, free, tracks);
        }
        else
        {
            Reconcile(track);
            FollowEnd(clip, oldEnd);
        }
        return delta;
    }

    private (double LeadIn, double TailOut) SplitSound(TimelineClip clip, double newLead, double newTail, bool free)
    {
        Editable(TrackOf(clip));
        if (!clip.HasSound || clip.ContentKind == LayerKind.Audio)
            throw new TimelineEditException("Only a clip with both picture and sound has a J or L cut.");
        if (!clip.AudioLinked)
            throw new TimelineEditException("The clip's sound is unlinked: it is slipped, not split.");

        // The sound starts no sooner than the timeline or its file, and ends no later than its file.
        var leadMost = Down(clip.Start, free);
        var tailMost = double.PositiveInfinity;
        if (clip.PlaysFile)
            leadMost = Math.Min(leadMost, Down(clip.MediaOffset / clip.Speed, free));
        if (Bounded(clip))
            tailMost = Down(Math.Max((clip.SourceDuration - clip.MediaEnd) / clip.Speed, 0), free);

        var (oldLead, oldTail) = (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds);
        var lead = double.IsFinite(newLead) ? Math.Min(Near(newLead, free), leadMost) : oldLead;
        var tail = double.IsFinite(newTail) ? Math.Min(Near(newTail, free), tailMost) : oldTail;

        // And it lasts at least the shortest clip: a sound cut too far inside its picture is let out again, on the side
        // that moved the most first, as far as its picture's edge (the picture is at least the shortest clip), each side
        // to the frame on the picture's side of it.
        var missing = MinimumLength(free) - (clip.Duration + lead + tail);
        if (missing > Slack)
        {
            if (Math.Abs(tail - oldTail) >= Math.Abs(lead - oldLead))
                (tail, lead) = (LetOut(tail, ref missing), LetOut(lead, ref missing));
            else
                (lead, tail) = (LetOut(lead, ref missing), LetOut(tail, ref missing));
            (lead, tail) = (Up(lead, free), Up(tail, free));
        }

        if (lead != oldLead || tail != oldTail)
        {
            Remember(clip);
            (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) = (lead, tail);
        }
        return (lead, tail);

        static double LetOut(double side, ref double missing)
        {
            if (side >= 0 || missing <= 0)
                return side;
            var back = Math.Min(missing, -side);
            missing -= back;
            return side + back;
        }
    }

    private double Roll(TimelineClip earlier, TimelineClip later, double newCutTime, bool free)
    {
        var track = Editable(TrackOf(earlier));
        var index = track.IndexOf(earlier);
        if (index < 0 || index + 1 >= track.Clips.Count || !ReferenceEquals(track.Clips[index + 1], later) || !Meet(earlier, later))
            throw new TimelineEditException("A roll needs two clips that meet on one track.");
        if (!double.IsFinite(newCutTime))
            return 0;

        var wanted = Wanted(newCutTime, later.Start, free);
        var earliest = Math.Max(Up(earlier.Start + ShortestFor(earlier, free), free),
                                Math.Max(Up(later.Start - MediaBefore(later), free), Up(LeadOf(later), free)));
        var latest = Math.Min(Down(later.End - ShortestFor(later, free), free), Down(earlier.End + MediaAfter(earlier), free));
        if (earliest > latest + Slack)
            return 0;

        var cut = Toward(later.Start, wanted, earliest, latest);
        var delta = cut - later.Start;
        if (Math.Abs(delta) <= Slack / 1000)
            return 0;

        var oldCut = later.Start;
        Remember(earlier);
        Remember(later);
        SetSpan(earlier, earlier.Start, cut);
        later.State = Shifted(later.State, -delta);
        if (later.PlaysFile)
            later.MediaOffset = Math.Max(later.MediaOffset + delta * later.Speed, 0);
        SetSpan(later, cut, later.End);

        // Their children follow the cut as the two trims' would (4.4-BugHunt): those on it move with it, those it leaves wholly
        // outside their parent are unlinked. The side that gives up time goes first, so that the side taking it finds room
        // (two children that met on the cut meet on the new one).
        if (delta > 0)
        {
            FollowStart(later, oldCut, later.End);
            FollowEnd(earlier, oldCut);
        }
        else
        {
            FollowEnd(earlier, oldCut);
            FollowStart(later, oldCut, later.End);
        }
        return delta;
    }

    private double Slip(TimelineClip clip, double mediaDelta)
    {
        Editable(TrackOf(clip));
        if (!clip.PlaysFile || !double.IsFinite(mediaDelta))
            return 0;

        // What it shows, its J/L sound included, stays inside the file.
        var lowest = LeadOf(clip) * clip.Speed;
        var highest = Bounded(clip) ? clip.SourceDuration - (clip.Duration + TailOf(clip)) * clip.Speed : double.PositiveInfinity;
        if (lowest > highest + TimelineDocument.FileSlack(clip))
            return 0;

        var offset = Toward(clip.MediaOffset, clip.MediaOffset + mediaDelta, lowest, highest);
        var delta = offset - clip.MediaOffset;
        if (Math.Abs(delta) <= Slack / 1000)
            return 0;

        Remember(clip);
        clip.MediaOffset = offset;
        return delta;
    }

    /// <summary>Takes a clip off its track; with <paramref name="family"/> (4.4), what goes with it (<see cref="Family"/>) too.</summary>
    private TimelineTrackModel Lift(TimelineClip clip, bool reconcile = true, bool family = true)
    {
        var track = Editable(TrackOf(clip));
        var members = family ? Family(clip) : null;
        Remember(track);
        Remember(clip);
        RemoveFrom(track, clip);
        if (reconcile)
            Reconcile(track);

        if (members is not null)
        {
            foreach (var (_, member) in members)
                Lift(member, reconcile: true, family: false);
        }
        return track;
    }

    // The crossfades are looked at once the gap is closed: the clip after the deleted one keeps its crossfade when it
    // comes to meet the one before (pairs are by who meets whom, as in 4.2.0). What goes with it goes too (4.4), and the
    // tracks it was on close up with the clip's.
    private double LiftRippling(TimelineClip clip, bool rippleAll)
    {
        var (start, end) = (clip.Start, clip.End);
        var family = Family(clip);
        var track = Lift(clip, reconcile: false, family: false);
        var tracks = RippleTracks(track, rippleAll);
        if (family is not null)
        {
            foreach (var (memberTrack, member) in family)
            {
                Lift(member, reconcile: false, family: false);
                if (!tracks.Contains(memberTrack))
                    tracks.Add(memberTrack);
            }
        }
        return Back(Ripple(tracks, end, -(end - start), null));
    }

    private double Edge(TimelineTrackModel track, double seconds)
    {
        foreach (var clip in track.ClipsIntersecting(seconds - Slack, seconds + Slack))
        {
            if (Math.Abs(clip.End - seconds) <= Slack)
                return clip.End;
            if (Math.Abs(clip.Start - seconds) <= Slack)
                return clip.Start;
        }
        return Near(seconds, false);
    }

    /// <summary>How far clips moved back, as a positive amount (and 0, not -0, when they could not move).</summary>
    private static double Back(double moved) => moved < 0 ? -moved : 0;

    private double CloseUp(TimelineTrackModel track, double gapStart, double gapEnd, bool rippleAll)
    {
        Editable(track);

        // A gap's edge is a clip's edge where one is there, on a frame or not (a free trim's); anywhere else, on a frame.
        var start = Edge(track, Math.Max(Finite(gapStart, "The gap's start"), 0));
        var end = Edge(track, Math.Min(Finite(gapEnd, "The gap's end"), Farthest));
        if (!(end - start > Slack))
            return 0;
        if (Overlapped(track, start, end, null) is { } inside)
            throw new TimelineEditException($"{Seconds(start)} to {Seconds(end)} s on {track.DisplayName} is not empty: a clip lies from {Seconds(inside.Start)} to {Seconds(inside.End)} s.");

        return Back(Ripple(RippleTracks(track, rippleAll), end, -(end - start), null));
    }
}
