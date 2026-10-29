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
    /// How fast a clip that plays its file runs (<see cref="Layer.SlowestSpeed"/> to <see cref="Layer.FastestSpeed"/>):
    /// it plays the same stretch of its file, so its end moves; the clips after it on its track that it would run into
    /// move on as far as it grew; its keyframes, its effects' keyframes and ranges and its J/L cut are scaled with it.
    /// Returns the speed set (the one it had, for a clip that does not play a file).
    /// </summary>
    double SetClipSpeed(TimelineClip clip, double speed, bool freeUnquantized = false);

    /// <summary>Whether its sound moves and trims with its picture, and how far it is slipped while it does not (seconds, later when positive; not on frames).</summary>
    void SetAudioLink(TimelineClip clip, bool linked, double slipSeconds);

    /// <summary>A new look for the clip: the very object given, which nobody may change afterwards (no edit changes a look in place).</summary>
    void SetClipState(TimelineClip clip, LayerState state);

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
}

internal sealed class TimelineTransaction : ITimelineTransaction
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
        }
        else if (_touched.Count > 0 || _touchedTracks.Count > 0)
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

    public void SetClipState(TimelineClip clip, LayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Step(() => Change(clip, c => c.State = state));
    }

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
        _doc.Tracks.Remove(track);

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

    // ----- Folds (4.3D; both ways and at their edges, 4.4-BugHunt) -----

    /// <summary>
    /// Folds the stretch from <paramref name="from"/> to <paramref name="to"/> out of the timeline at <paramref name="seamTrack"/>
    /// (<see cref="ITimelineTransaction.FoldStretch"/>): as the fold <paramref name="id"/> when one is given (a stretch put back,
    /// folded again), and out of <paramref name="onTracks"/> when they are given (the tracks a fold was just put back on, so
    /// that it closes up on the very tracks it opened on).
    /// </summary>
    private FoldedSegment? Fold(TimelineTrackModel seamTrack, double from, double to, bool rippleAll, Guid? id = null, List<TimelineTrackModel>? onTracks = null)
    {
        Editable(seamTrack);
        var (start, end) = (Near(Math.Max(Finite(from, "The stretch's start"), 0), false), Near(Math.Min(Finite(to, "The stretch's end"), Farthest), false));
        var minimum = MinimumLength(false);
        if (!(end - start >= minimum - Slack))
            return null;
        if (id is { } given && _doc.Folds?.Exists(f => f.Id == given) == true)
            throw new TimelineEditException("That stretch is folded out already.");

        // What lies in the stretch on each track, cut out at its ends (a clip whose edge is too near an end to be split
        // there, or whose sound is, goes whole), kept with its times counted from the stretch's start, and taken off. The
        // tracks (4.4): the ripple's, those under them (the captions' lane among them), and the tracks of what goes with the
        // clips in the stretch. Every track is cut before anything is taken off, so that a child's piece in the stretch is
        // its parent's piece there (Split) whichever of their tracks comes first.
        var tracks = onTracks ?? FoldTracks(RippleTracks(seamTrack, rippleAll), start, end);

        // The stretches put back that this one covers (4.4-BugHunt), found while their clips are where they were: folded
        // again, or in part by Remove Dead Air, they are no longer marked (and the one with this fold's id in any case).
        var covered = Covered(tracks, start, end);
        foreach (var track in tracks)
        {
            foreach (var clip in track.ClipsIntersecting(start, end).ToArray())
            {
                try
                {
                    if (clip.End > end + minimum && end > clip.Start + minimum)
                        Split(clip, end, false);
                    if (clip.Start < start - minimum && start < clip.End - minimum)
                        Split(clip, start, false);
                }
                catch (TimelineEditException)
                {
                }
            }
        }

        var kept = new List<FoldedClip>();
        foreach (var track in tracks)
        {
            foreach (var inside in track.ClipsIntersecting(start, end).ToArray())
            {
                var copy = inside.Snapshot();
                (copy.Start, copy.End) = (Math.Round(inside.Start - start, 6), Math.Round(inside.End - start, 6));
                kept.Add(new FoldedClip(track.Id, track.Kind, copy));
                Lift(inside, family: false);
            }
        }

        if (kept.Count == 0)
            return null;

        // Closed up whole, or not folded at all (a track that cannot follow): the fold opens again by just its length.
        if (Ripple(tracks, end, -(end - start), null) > -(end - start - Slack))
            throw new TimelineEditException($"{Seconds(start)} to {Seconds(end)} s cannot close up: a clip after it cannot move back that far.");

        // The seam: the clip that now follows on its track (or another of the tracks), or else the one before it.
        var seam = tracks.Prepend(seamTrack).Select(t => t.Clips.FirstOrDefault(c => Math.Abs(c.Start - start) <= Slack)).FirstOrDefault(c => c is not null);
        var atEnd = seam is null;
        seam ??= tracks.Prepend(seamTrack).Select(t => t.Clips.LastOrDefault(c => Math.Abs(c.End - start) <= Slack)).FirstOrDefault(c => c is not null);
        if (seam is null)
            throw new TimelineEditException($"Nothing is left at {Seconds(start)} s to keep the fold at.");

        var fold = new FoldedSegment(id ?? Guid.NewGuid(), seam.Id, atEnd, rippleAll, Math.Round(end - start, 6), kept);
        RememberFolds();
        _doc.Folds = [.. _doc.Folds ?? [], fold];
        if (_doc.RestoredFolds is { Count: > 0 } marked)
        {
            var left = marked.Where(r => r.Id != fold.Id && covered?.Contains(r) != true).ToList();
            if (left.Count != marked.Count)
                _doc.RestoredFolds = left.Count == 0 ? null : left;
        }
        Touch(seam.Id);
        return fold;
    }

    /// <summary>
    /// The stretches put back (<see cref="TimelineDocument.RestoredFolds"/>) that lie on one of <paramref name="tracks"/>
    /// across the stretch from <paramref name="start"/> to <paramref name="end"/>; null when none does.
    /// </summary>
    private HashSet<RestoredFold>? Covered(List<TimelineTrackModel> tracks, double start, double end)
    {
        if (_doc.RestoredFolds is not { Count: > 0 })
            return null;

        HashSet<RestoredFold>? covered = null;
        foreach (var (restored, trackId, from, to) in _doc.RestoredSpans())
        {
            if (from < end - Slack && to > start + Slack && tracks.Exists(t => t.Id == trackId))
                (covered ??= new HashSet<RestoredFold>(ReferenceEqualityComparer.Instance)).Add(restored);
        }
        return covered;
    }

    private bool Unfold(FoldedSegment given)
    {
        // Found by its id (4.4-BugHunt): one taken from an older copy of the document (an undo step's, a view's) is this one.
        var fold = _doc.Folds?.Find(f => f.Id == given.Id) ?? throw new TimelineEditException("That fold is not on this timeline.");
        if (fold.SeamIn(_doc) is not { } at || _doc.FindClip(fold.SeamClipId) is not { } seam)
            throw new TimelineEditException("The cut the stretch was folded at is gone.");

        // Room at the seam on the tracks it was folded out of (as they are now), those under them among them, and on the
        // tracks its clips go back to (4.4: its children's): what lies across it is split there.
        var seamTrack = Editable(TrackOf(seam));
        var tracks = UnfoldTracks(fold, seamTrack);
        foreach (var track in tracks)
        {
            // (A view around the seam: one of no width shows nothing.)
            foreach (var clip in track.ClipsIntersecting(at - Slack, at + Slack).ToArray().Where(c => c.Start < at - Slack && c.End > at + Slack))
            {
                try
                {
                    Split(clip, at, true);
                }
                catch (TimelineEditException)
                {
                    // Too near its edge, or its sound is: it stays whole, and what was folded lies over it.
                }
            }
        }

        Ripple(tracks, at, fold.Length, null);

        // Its clips where they were, on their own tracks (a track gone since: the first unlocked one of its kind). A copy
        // whose id has been taken since gets a new one, and the copies that named it as their parent name that (4.4).
        Dictionary<Guid, Guid>? renamed = null;
        var copies = new List<(TimelineTrackModel Track, TimelineClip Copy)>(fold.Clips.Count);
        foreach (var kept in fold.Clips)
        {
            var track = _doc.Tracks.FirstOrDefault(t => t.Id == kept.TrackId && !t.IsLocked)
                        ?? _doc.Tracks.FirstOrDefault(t => t.Kind == kept.TrackKind && !t.IsLocked);
            if (track is null)
                continue;

            var copy = kept.Clip.Snapshot();
            (copy.Start, copy.End) = (at + kept.Clip.Start, at + kept.Clip.End);
            if (_doc.FindClip(copy.Id) is not null)
            {
                var id = Guid.NewGuid();
                (renamed ??= new Dictionary<Guid, Guid>())[copy.Id] = id;
                copy.Id = id;
            }
            copies.Add((track, copy));
        }

        foreach (var (track, copy) in copies)
        {
            if (renamed is not null && copy.ParentClipId is { } parent && renamed.TryGetValue(parent, out var now))
                copy.ParentClipId = now;
            Place(track, copy, allowCrossfadeOverlap: true, free: true);
        }

        // The stretch stays marked, to be folded again as this very fold (4.4-BugHunt): from the start of what came back on
        // the seam's track (or the end of the clip before it there) to the seam clip's start, or for a seam that was a clip's
        // end, to the end of what came back.
        var from = EdgeAt(seamTrack, at, start: true) ?? EdgeAt(seamTrack, at, start: false);
        var to = fold.AtEnd ? EdgeAt(seamTrack, at + fold.Length, start: false) : new FoldEdge(seam.Id, false);
        RememberFolds();
        if (from is not null || to is not null)
            _doc.RestoredFolds = [.. (_doc.RestoredFolds ?? []).Where(r => r.Id != fold.Id), new RestoredFold(fold.Id, seamTrack.Id, from, to, fold.AllTracks, fold.Length)];
        _doc.Folds = _doc.Folds!.Count == 1 ? null : [.. _doc.Folds.Where(f => !ReferenceEquals(f, fold))];
        Touch(seam.Id);
        return true;
    }

    /// <summary>
    /// The tracks a fold is put back on (and, opened at an edge, folded out of again): its ripple's at the seam's track as
    /// they are now, but the subtitles that lie under none (as a fold leaves them), and the unlocked tracks its clips go back
    /// to (4.4: its children's).
    /// </summary>
    private List<TimelineTrackModel> UnfoldTracks(FoldedSegment fold, TimelineTrackModel seamTrack)
    {
        var tracks = RippleTracks(seamTrack, fold.AllTracks).Where(t => t.Kind != TimelineTrackKind.Subtitle || t.ParentTrackId is not null).ToList();
        foreach (var kept in fold.Clips)
        {
            if (TrackById(kept.TrackId) is { IsLocked: false } keptTrack && !tracks.Contains(keptTrack))
                tracks.Add(keptTrack);
        }
        return tracks;
    }

    /// <summary>The clip of a track whose start (or, not <paramref name="start"/>, whose end) is at <paramref name="seconds"/>; null when none is.</summary>
    private static TimelineClip? ClipWithEdgeAt(TimelineTrackModel track, double seconds, bool start)
    {
        foreach (var clip in track.ClipsIntersecting(seconds - Slack, seconds + Slack))
        {
            if (Math.Abs((start ? clip.Start : clip.End) - seconds) <= Slack)
                return clip;
        }
        return null;
    }

    private static FoldEdge? EdgeAt(TimelineTrackModel track, double seconds, bool start) =>
        ClipWithEdgeAt(track, seconds, start) is { } clip ? new FoldEdge(clip.Id, !start) : null;

    private FoldedSegment FoldAgain(RestoredFold given)
    {
        ArgumentNullException.ThrowIfNull(given);
        var restored = _doc.RestoredFolds?.Find(r => r.Id == given.Id)
                       ?? throw new TimelineEditException("That stretch is no longer marked as put back on this timeline.");
        if (restored.SpanIn(_doc) is not { } span)
            throw new TimelineEditException("The stretch that was put back is not there any more.");
        var track = TrackById(restored.TrackId) ?? throw new TimelineEditException("The track the stretch was put back on is gone.");

        // Folded as it was the first time, as the same fold (whose record Fold lets go of).
        return Fold(track, span.From, span.To, restored.AllTracks, restored.Id)
               ?? throw new TimelineEditException("Nothing lies there to fold.");
    }

    /// <summary>
    /// <see cref="ITimelineTransaction.ResizeFold"/>: the whole stretch put back first (on the tracks UnfoldSegment puts it
    /// back on), then the new stretch folded out of the same tracks as the same fold: from the seam to short of the tail
    /// shown, from past the head shown to the end, or with as much more of the clip at the edge. What is shown then joins
    /// the clip it continues (<see cref="JoinShown"/>). One step: a refusal anywhere puts everything back.
    /// </summary>
    private FoldedSegment? Resize(FoldedSegment given, FoldSide side, double seconds)
    {
        ArgumentNullException.ThrowIfNull(given);
        var fold = _doc.Folds?.Find(f => f.Id == given.Id) ?? throw new TimelineEditException("That fold is not on this timeline.");
        if (fold.SeamIn(_doc) is not { } at || _doc.FindClip(fold.SeamClipId) is not { } seam)
            throw new TimelineEditException("The cut the stretch was folded at is gone.");
        Finite(seconds, "How far the fold's edge moves");
        var seamTrack = Editable(TrackOf(seam));

        // Less than half a frame is no change.
        if (Math.Abs(seconds) < (Quantizing(false) ? Rate.FrameDuration / 2 : Slack))
            return fold;

        // Every track it was folded out of is there and open: a part of a fold goes back to its own track or nowhere (only a
        // whole fold put back finds another track of its kind).
        foreach (var kept in fold.Clips)
        {
            if (TrackById(kept.TrackId) is not { IsLocked: false })
                throw new TimelineEditException("A track the stretch was folded out of is locked or gone: unlock it, or put the stretch back whole.");
        }

        var (minimum, length, reveal, tail) = (MinimumLength(false), fold.Length, seconds > 0, side == FoldSide.Tail);
        double amount;
        if (reveal)
        {
            // At least the shortest clip, so that what is shown and what stays folded can both be clips; all but less than
            // that is all of it.
            amount = Math.Max(Near(Math.Min(seconds, length), false), minimum);
            if (length - amount < minimum - Slack)
            {
                Unfold(fold);
                return null;
            }
        }
        else
        {
            // The clip at that side of the seam on its track gives what is folded, and keeps the shortest clip of itself.
            var edge = ClipWithEdgeAt(seamTrack, at, start: tail)
                       ?? throw new TimelineEditException($"No clip meets the fold at its {(tail ? "end" : "start")} to fold more of.");
            amount = Math.Min(Math.Max(Near(-seconds, false), minimum), Down(edge.Duration - ShortestFor(edge, false), false));
            if (amount < minimum - Slack)
                throw new TimelineEditException("The clip at the fold is as short as a clip can be: there is no more of it to fold.");
        }

        var tracks = UnfoldTracks(fold, seamTrack);
        Unfold(fold);
        var end = fold.AtEnd ? Near(at + length, false) : seam.Start;
        var cut = Near(tail ? (reveal ? end - amount : end + amount) : (reveal ? at + amount : at - amount), false);
        var (from, to) = tail ? (at, cut) : (cut, end);

        // Cut at the new edge first, where FoldStretch would leave a clip whole: on the seam's track the clip there must be
        // cut, or the fold would take all of it. Folding more at the tail leaves the clip at the edge the part after the cut,
        // which keeps its id (and its children theirs).
        _splits = tail && !reveal ? new List<(TimelineClip First, TimelineClip Second)>() : null;
        try
        {
            foreach (var track in tracks)
            {
                foreach (var clip in track.ClipsIntersecting(cut - Slack, cut + Slack).ToArray())
                {
                    if (!(clip.Start < cut - Slack && clip.End > cut + Slack))
                        continue;

                    try
                    {
                        Split(clip, cut, false);
                    }
                    catch (TimelineEditException) when (!ReferenceEquals(track, seamTrack))
                    {
                        // Too near its own edge there, or its sound is: it stays whole, as FoldStretch leaves one.
                    }
                    catch (TimelineEditException)
                    {
                        throw new TimelineEditException("The fold's edge cannot go there: a clip would be cut shorter than the shortest clip. Try a little further.");
                    }
                }
            }

            if (_splits is { Count: > 0 } halves)
                KeepIdsAfter(halves);
        }
        finally
        {
            _splits = null;
        }

        // Nothing left in the stretch to fold: it stays put back whole, as UnfoldSegment leaves it.
        if (Fold(seamTrack, from, to, fold.AllTracks, fold.Id, tracks) is not { } refolded)
            return null;
        if (!reveal)
            return refolded;

        // What is shown joins the clip it continues: the tail's the clip after the seam, the head's the clip before it. A seam
        // that was a piece joined into a clip is that clip's edge now.
        var shown = Near(at + amount, false);
        var joined = JoinShown(tracks, tail ? shown : at, tail, at, shown, fold.Id);
        if (!joined.TryGetValue(refolded.SeamClipId, out var keeper))
            return refolded;

        var result = refolded with { SeamClipId = keeper };
        RememberFolds();
        _doc.Folds = [.. _doc.Folds!.Select(f => ReferenceEquals(f, refolded) ? result : f)];
        return result;
    }

    /// <summary>
    /// Gives each split's second half the id its clip had, and the first half the new one (a fold grown at its tail takes a
    /// clip's head and leaves the rest, which is still that clip, its children still its): the links to either half follow.
    /// </summary>
    private void KeepIdsAfter(List<(TimelineClip First, TimelineClip Second)> halves)
    {
        var swapped = new Dictionary<Guid, Guid>(halves.Count * 2);
        foreach (var (first, second) in halves)
        {
            Remember(first);
            Remember(second);
            (first.Id, second.Id) = (second.Id, first.Id);
            (swapped[first.Id], swapped[second.Id]) = (second.Id, first.Id);
            Touch(first.Id);
            Touch(second.Id);
        }

        foreach (var track in _doc.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.ParentClipId is { } parent && swapped.TryGetValue(parent, out var now))
                {
                    Remember(clip);
                    clip.ParentClipId = now;
                }
            }
        }
    }

    /// <summary>
    /// After a fold was opened by part of its length (<see cref="Resize"/>): on each of its tracks, the piece shown (lying
    /// within <paramref name="shownFrom"/> to <paramref name="shownTo"/>) that meets a clip at <paramref name="junction"/>
    /// joins that clip when the two are one clip cut in two (<see cref="Joinable"/>), the clip keeping its id: the clip
    /// after the seam takes in the piece before it (<paramref name="tail"/>), or the clip before the seam the piece after it.
    /// The joined piece's children name the clip it joined, so that theirs join in turn (passes until none does; the
    /// seam's track, a parent, comes first). A piece that is the seam of another fold (than <paramref name="foldId"/>), or
    /// an edge of a stretch put back, stays a clip of its own, so that neither loses its place. Returns each joined piece's
    /// id with the id of the clip that took it in.
    /// </summary>
    private Dictionary<Guid, Guid> JoinShown(List<TimelineTrackModel> tracks, double junction, bool tail, double shownFrom, double shownTo, Guid foldId)
    {
        var joined = new Dictionary<Guid, Guid>();
        bool Anchors(Guid id) =>
            _doc.Folds?.Exists(f => f.SeamClipId == id && f.Id != foldId) == true
            || _doc.RestoredFolds?.Exists(r => r.From?.ClipId == id || r.To?.ClipId == id) == true;

        for (var pass = 0; pass <= tracks.Count; pass++)
        {
            var any = false;
            foreach (var track in tracks)
            {
                var (before, after) = (ClipWithEdgeAt(track, junction, start: false), ClipWithEdgeAt(track, junction, start: true));
                if (before is null || after is null || ReferenceEquals(before, after))
                    continue;

                TimelineClip piece = tail ? before : after, keeper = tail ? after : before;
                if (piece.Start < shownFrom - Slack || piece.End > shownTo + Slack || Anchors(piece.Id) || !Joinable(before, after)
                    || JoinLook(before.State, after.State, before.Duration, keepSecond: tail) is not { } look)
                {
                    continue;
                }

                Remember(track);
                Remember(before);
                Remember(after);
                var (start, end) = (before.Start, after.End);
                RemoveFrom(track, piece);
                if (tail)
                {
                    if (keeper.PlaysFile)
                        keeper.MediaOffset = piece.MediaOffset;
                    (keeper.FadeInSeconds, keeper.FadeInStyle, keeper.TransitionIn, keeper.AudioLeadInSeconds) = (piece.FadeInSeconds, piece.FadeInStyle, piece.TransitionIn, piece.AudioLeadInSeconds);
                }
                else
                {
                    (keeper.FadeOutSeconds, keeper.FadeOutStyle, keeper.AudioTailOutSeconds) = (piece.FadeOutSeconds, piece.FadeOutStyle, piece.AudioTailOutSeconds);
                }
                keeper.State = look;
                SetSpan(keeper, start, end);
                Reconcile(track);
                joined[piece.Id] = keeper.Id;
                Relink(piece.Id, keeper.Id);
                any = true;
            }

            if (!any)
                break;
        }
        return joined;
    }

    /// <summary>The clips that named <paramref name="from"/> as their parent name <paramref name="to"/> (a piece joined into a clip).</summary>
    private void Relink(Guid from, Guid to)
    {
        foreach (var track in _doc.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.ParentClipId == from && clip.Id != to)
                {
                    Remember(clip);
                    clip.ParentClipId = to;
                }
            }
        }
    }

    /// <summary>
    /// Whether two clips that meet are one clip cut in two, as a split's halves are: the same thing from the same file and
    /// streams at the same speed, its file running on from one into the other, the same switches, sound and parent, and
    /// nothing at the cut (a fade, a crossfade, a J or L cut). Their looks are <see cref="JoinLook"/>'s to compare.
    /// </summary>
    private static bool Joinable(TimelineClip a, TimelineClip b) =>
        Meet(a, b) && a.ContentKind == b.ContentKind && string.Equals(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase)
        && a.VideoStreamIndex == b.VideoStreamIndex && a.AudioStreamIndex == b.AudioStreamIndex && a.Speed == b.Speed
        && a.SourceDuration == b.SourceDuration && a.AudioLinked == b.AudioLinked && a.AudioSlipSeconds == b.AudioSlipSeconds
        && a.GainDb == b.GainDb && a.IsHidden == b.IsHidden && a.IsMuted == b.IsMuted && a.ProxyChoice == b.ProxyChoice
        && a.ParentClipId == b.ParentClipId && a.FadeOutSeconds == 0 && b.FadeInSeconds == 0 && b.TransitionIn == 0
        && a.AudioTailOutSeconds == 0 && b.AudioLeadInSeconds == 0
        && (!a.PlaysFile || Math.Abs(b.MediaOffset - a.MediaEnd) <= TimelineDocument.FileSlack(a));

    // ----- Children (4.4): what goes with a clip, found where it lies -----

    private static bool Holds(List<(TimelineTrackModel Track, TimelineClip Clip)>? clips, TimelineClip clip)
    {
        if (clips is not null)
        {
            foreach (var (_, held) in clips)
            {
                if (ReferenceEquals(held, clip))
                    return true;
            }
        }
        return false;
    }

    private TimelineTrackModel? TrackById(Guid id)
    {
        foreach (var track in _doc.Tracks)
        {
            if (track.Id == id)
                return track;
        }
        return null;
    }

    /// <summary>Whether <paramref name="track"/> lies under <paramref name="ancestor"/>, through the tracks it lies under (a chain that loops is followed once round).</summary>
    private bool Under(TimelineTrackModel track, TimelineTrackModel ancestor)
    {
        var (up, steps) = (track, 0);
        while (up.ParentTrackId is { } above && steps++ < _doc.Tracks.Count)
        {
            if (above == ancestor.Id)
                return true;
            if (TrackById(above) is not { } next)
                return false;
            up = next;
        }
        return false;
    }

    /// <summary>
    /// The clips on any track that name <paramref name="parent"/> as theirs and lie in the stretch from <paramref name="from"/>
    /// to <paramref name="to"/> (by more than the slack: one that only touches it does not), with their tracks; null when
    /// there are none. Two binary searches on each track, and nothing made unless one is found.
    /// </summary>
    private List<(TimelineTrackModel Track, TimelineClip Clip)>? ChildrenWith(TimelineClip parent, double from, double to)
    {
        List<(TimelineTrackModel Track, TimelineClip Clip)>? children = null;
        foreach (var track in _doc.Tracks)
        {
            foreach (var clip in track.ClipsIntersecting(from + Slack, to - Slack))
            {
                if (clip.ParentClipId == parent.Id && !ReferenceEquals(clip, parent))
                    (children ??= []).Add((track, clip));
            }
        }
        return children;
    }

    /// <summary>
    /// What goes with a clip when it moves or goes: its children that lie with it in time, theirs that lie with them, and so
    /// on, on unlocked tracks (a locked track's clips stay as they are, as a ripple passes it by), each once; null when none.
    /// </summary>
    private List<(TimelineTrackModel Track, TimelineClip Clip)>? Family(TimelineClip clip)
    {
        List<(TimelineTrackModel Track, TimelineClip Clip)>? family = null;
        var (parent, next) = (clip, 0);
        while (true)
        {
            foreach (var track in _doc.Tracks)
            {
                if (track.IsLocked)
                    continue;
                foreach (var child in track.ClipsIntersecting(parent.Start + Slack, parent.End - Slack))
                {
                    if (child.ParentClipId == parent.Id && !ReferenceEquals(child, parent) && !ReferenceEquals(child, clip) && !Holds(family, child))
                        (family ??= []).Add((track, child));
                }
            }

            if (family is null || next >= family.Count)
                return family;
            parent = family[next++].Clip;
        }
    }

    /// <summary>
    /// What rides a ripple trim of a clip (whose end the ripple moves): of what goes with it (its children that lie with it,
    /// theirs, on unlocked tracks), those whose end is on its end or in the stretch it may give up (from
    /// <paramref name="lostFrom"/> to its end), and with <paramref name="head"/> (a trim of its start, which it keeps) those
    /// whose start is on its start. Their ends move with its end, or they go (<see cref="Ride"/>), so none holds the ripple
    /// back. Null when none.
    /// </summary>
    private List<(TimelineTrackModel Track, TimelineClip Clip)>? Riders(TimelineClip clip, bool head, double lostFrom)
    {
        var (start, end) = (clip.Start, clip.End);
        List<(TimelineTrackModel Track, TimelineClip Clip)>? riders = null;
        var (parent, next) = (clip, 0);
        while (true)
        {
            foreach (var track in _doc.Tracks)
            {
                if (track.IsLocked)
                    continue;
                foreach (var child in track.ClipsIntersecting(Math.Max(parent.Start, start) + Slack, Math.Min(parent.End, end) - Slack))
                {
                    if (child.ParentClipId != parent.Id || ReferenceEquals(child, parent) || ReferenceEquals(child, clip) || Holds(riders, child))
                        continue;
                    if ((head && Math.Abs(child.Start - start) <= Slack) || Math.Abs(child.End - end) <= Slack || (child.End <= end + Slack && child.End > lostFrom + Slack))
                        (riders ??= []).Add((track, child));
                }
            }

            if (riders is null || next >= riders.Count)
                return riders;
            parent = riders[next++].Clip;
        }
    }

    /// <summary>
    /// Moves what rides a ripple trim (<see cref="Riders"/>) once the clip's end has moved from <paramref name="oldEnd"/> by
    /// <paramref name="by"/> to <paramref name="newEnd"/> (by whole frames, or not: <paramref name="whole"/>). One on the clip's
    /// start (<paramref name="head"/>) has its head trimmed as the clip's was, its end moving with the clip's; one on the clip's
    /// end has its end moved with it (when it grows, as far as its file and the clip after it let it); one ending in the stretch
    /// the clip gave up is cut back to its new end. One that would be shorter than the shortest clip goes when its track is
    /// one the ripple closes up (<paramref name="rippled"/>: what follows comes back into its place), and is otherwise left
    /// where it is, unlinked (Q3, 4.4-BugHunt); one whose head cannot grow (its file, a clip in its way) stays as it is.
    /// Shrinking, this comes before the ripple; growing, after it.
    /// </summary>
    private void Ride(List<(TimelineTrackModel Track, TimelineClip Clip)> riders, double oldStart, double oldEnd, double newEnd,
                      double by, bool whole, bool head, bool free, List<TimelineTrackModel> rippled)
    {
        foreach (var (track, rider) in riders)
        {
            var shortest = ShortestFor(rider, free) - Slack;
            var onEnd = Math.Abs(rider.End - oldEnd) <= Slack;
            if (head && Math.Abs(rider.Start - oldStart) <= Slack)
            {
                var end = Moved(rider.End, by, whole);
                if (by < 0 && end - rider.Start < shortest)
                {
                    LeaveOrLift(track, rider, rippled);
                    continue;
                }
                if (by > 0 && (!(MediaBefore(rider) >= by - Slack) || Overlapped(track, rider.Start, end, rider) is not null))
                    continue;

                Remember(rider);
                rider.State = Shifted(rider.State, by);
                if (rider.PlaysFile)
                    rider.MediaOffset = Math.Max(rider.MediaOffset - by * rider.Speed, 0);
                SetSpan(rider, rider.Start, end);
            }
            else if (onEnd || (by < 0 && rider.End > newEnd + Slack))
            {
                var end = onEnd ? Moved(rider.End, by, whole) : newEnd;
                if (by > 0)
                {
                    TrimEnd(rider, end, ripple: false, rippleAll: false, free: true);
                }
                else if (end - rider.Start < shortest)
                {
                    LeaveOrLift(track, rider, rippled);
                }
                else
                {
                    Remember(rider);
                    SetSpan(rider, rider.Start, end);
                }
            }
        }

        foreach (var (track, _) in riders)
            Reconcile(track);
    }

    /// <summary>
    /// A rider a ripple trim leaves shorter than the shortest clip: on a track the ripple closes up it goes (what follows
    /// comes back into its place); on any other it stays where it is, unlinked (Q3, 4.4-BugHunt).
    /// </summary>
    private void LeaveOrLift(TimelineTrackModel track, TimelineClip rider, List<TimelineTrackModel> rippled)
    {
        if (rippled.Contains(track))
            Lift(rider, reconcile: false, family: false);
        else
            Unlink(rider);
    }

    /// <summary>
    /// A child a trim leaves wholly outside its parent (Q3, 4.4-BugHunt): it stays where it is, a clip of its own, and its own
    /// children stay with it.
    /// </summary>
    private void Unlink(TimelineClip child)
    {
        if (child.ParentClipId is null)
            return;
        Remember(child);
        child.ParentClipId = null;
    }

    /// <summary>
    /// After a trim of a clip's start without a ripple (or a roll of the cut before it): its children (on unlocked tracks)
    /// whose start was on its old start follow it there, as far as each can go, and those it left wholly before its new start
    /// stay where they are, unlinked (Q3, 4.4-BugHunt: they went, with what went with them, before).
    /// </summary>
    private void FollowStart(TimelineClip clip, double oldStart, double oldEnd)
    {
        if (ChildrenWith(clip, oldStart, oldEnd) is not { } children)
            return;

        foreach (var (track, child) in children)
        {
            if (track.IsLocked)
                continue;
            if (clip.Start > oldStart && child.End <= clip.Start + Slack)
                Unlink(child);
            else if (Math.Abs(child.Start - oldStart) <= Slack)
                TrimStart(child, clip.Start, ripple: false, rippleAll: false, free: true);
        }
    }

    /// <summary>
    /// After a trim of a clip's end without a ripple (or a roll of the cut after it): its children (on unlocked tracks) whose
    /// end was on its old end follow it there, as far as each can go, and those it left wholly after its new end stay where
    /// they are, unlinked (Q3, 4.4-BugHunt).
    /// </summary>
    private void FollowEnd(TimelineClip clip, double oldEnd)
    {
        if (ChildrenWith(clip, clip.Start, oldEnd) is not { } children)
            return;

        foreach (var (track, child) in children)
        {
            if (track.IsLocked)
                continue;
            if (clip.End < oldEnd && child.Start >= clip.End - Slack)
                Unlink(child);
            else if (Math.Abs(child.End - oldEnd) <= Slack)
                TrimEnd(child, clip.End, ripple: false, rippleAll: false, free: true);
        }
    }

    /// <summary>Takes what goes with a moving clip off its tracks, each with the clip it met (and its crossfade from it), to land again.</summary>
    private List<(TimelineTrackModel Track, TimelineClip Clip, TimelineClip? Met, double Crossfade)> TakeOff(List<(TimelineTrackModel Track, TimelineClip Clip)> family)
    {
        // Who meets whom first, before any of them is taken off: two that meet may both go with it.
        var riding = new List<(TimelineTrackModel Track, TimelineClip Clip, TimelineClip? Met, double Crossfade)>(family.Count);
        foreach (var (track, member) in family)
        {
            var index = track.IndexOf(member);
            riding.Add((track, member, index > 0 && Meet(track.Clips[index - 1], member) ? track.Clips[index - 1] : null, member.TransitionIn));
        }

        foreach (var (track, member, _, _) in riding)
        {
            Remember(track);
            Remember(member);
            RemoveFrom(track, member);
            member.TransitionIn = 0;
        }
        return riding;
    }

    /// <summary>
    /// Lands what went with a moved clip, each by <paramref name="delta"/> on its own track, where nothing may be in its way
    /// (a clip of another's is refused, and so the move). A crossfade stays with one that lands against the same clip again.
    /// </summary>
    private void LandFamily(List<(TimelineTrackModel Track, TimelineClip Clip, TimelineClip? Met, double Crossfade)> riding, double delta)
    {
        var whole = WholeFrames(delta);
        foreach (var (track, member, _, _) in riding)
        {
            if (member.Start + delta < LeadOf(member) - Slack)
                throw new TimelineEditException($"What goes with the clip would start before the timeline ({Seconds(member.Start + delta)} s on {track.DisplayName}).");
            Land(track, member, Moved(member.Start, delta, whole), Moved(member.End, delta, whole), allowCrossfadeOverlap: false, free: true);
        }

        foreach (var (track, member, met, crossfade) in riding)
        {
            var at = track.IndexOf(member);
            if (member.TransitionIn == 0 && crossfade > 0 && met is not null && at > 0 && ReferenceEquals(track.Clips[at - 1], met) && Meet(met, member))
                member.TransitionIn = crossfade;
        }
    }

    /// <summary>
    /// After a clip moved to another track: a track that lay under its old track, and whose clips' parents (each one on
    /// either track) now all lie on the new one, lies under the new one (a mini-stack follows its clips).
    /// </summary>
    private void FollowTrack(TimelineTrackModel source, TimelineTrackModel target)
    {
        HashSet<Guid>? onSource = null, onTarget = null;
        foreach (var track in _doc.Tracks)
        {
            if (track.ParentTrackId != source.Id || ReferenceEquals(track, source) || ReferenceEquals(track, target) || Under(target, track))
                continue;

            onSource ??= new HashSet<Guid>(source.Clips.Select(c => c.Id));
            onTarget ??= new HashSet<Guid>(target.Clips.Select(c => c.Id));
            var (fromSource, fromTarget) = (false, false);
            foreach (var clip in track.Clips)
            {
                if (clip.ParentClipId is not { } parent)
                    continue;
                fromSource |= onSource.Contains(parent);
                fromTarget |= onTarget.Contains(parent);
            }

            if (fromTarget && !fromSource)
            {
                Remember(track);
                track.ParentTrackId = target.Id;
            }
        }
    }

    /// <summary>
    /// The tracks a fold takes a stretch out of: the ripple's (<paramref name="rippled"/>) but the subtitles that lie under
    /// none (4.4: the captions' lane of a mini-stack folds with it), then the unlocked tracks of the children of what lies in
    /// the stretch on any of them, and of theirs, in that order.
    /// </summary>
    private List<TimelineTrackModel> FoldTracks(List<TimelineTrackModel> rippled, double start, double end)
    {
        var tracks = rippled.Where(t => t.Kind != TimelineTrackKind.Subtitle || t.ParentTrackId is not null).ToList();
        for (var i = 0; i < tracks.Count; i++)
        {
            foreach (var clip in tracks[i].ClipsIntersecting(start, end))
            {
                if (ChildrenWith(clip, Math.Max(clip.Start, start), Math.Min(clip.End, end)) is not { } children)
                    continue;
                foreach (var (track, _) in children)
                {
                    if (!track.IsLocked && !tracks.Contains(track))
                        tracks.Add(track);
                }
            }
        }
        return tracks;
    }

    /// <summary>A ripple's tracks for a move: the clip's track, the unlocked tracks under it, and the tracks of what goes with the clip.</summary>
    private List<TimelineTrackModel> WithFamilyTracks(TimelineTrackModel track, List<(TimelineTrackModel Track, TimelineClip Clip)>? family)
    {
        var tracks = WithChildTracks([track]);
        if (family is not null)
        {
            foreach (var (memberTrack, _) in family)
            {
                if (!tracks.Contains(memberTrack))
                    tracks.Add(memberTrack);
            }
        }
        return tracks;
    }

    /// <summary>Adds to <paramref name="tracks"/> the unlocked tracks that lie under any of them (4.4), and theirs: a mini-stack ripples with its track.</summary>
    private List<TimelineTrackModel> WithChildTracks(List<TimelineTrackModel> tracks)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            foreach (var child in _doc.Tracks)
            {
                if (child.ParentTrackId == tracks[i].Id && !child.IsLocked && !tracks.Contains(child))
                    tracks.Add(child);
            }
        }
        return tracks;
    }

    // ----- Ripple -----

    /// <summary>
    /// The track a ripple starts on, or every unlocked track but the subtitles (which never ripple, as in 4.2.0); with them
    /// (4.4) the unlocked tracks that lie under any of them, the captions' lane of a mini-stack among them.
    /// </summary>
    private List<TimelineTrackModel> RippleTracks(TimelineTrackModel track, bool all) =>
        WithChildTracks(all ? _doc.Tracks.Where(t => !t.IsLocked && (ReferenceEquals(t, track) || t.Kind != TimelineTrackKind.Subtitle)).ToList() : [track]);

    /// <summary>On each track, where the clips that start at or after <paramref name="from"/> begin.</summary>
    private static int[] FirstMovers(IReadOnlyList<TimelineTrackModel> tracks, double from)
    {
        var first = new int[tracks.Count];
        for (var t = 0; t < tracks.Count; t++)
        {
            var index = tracks[t].Clips.FindIndex(c => c.Start >= from - Slack);
            first[t] = index < 0 ? tracks[t].Clips.Count : index;
        }
        return first;
    }

    /// <summary>
    /// How much of <paramref name="delta"/> the clips from <paramref name="from"/> can move: all of it later; earlier,
    /// only as far as lets none run into a clip that stays nor before the timeline's start (its sound neither).
    /// <paramref name="moving"/> is a clip whose end moves with them, so that it holds none of them back, and so are its
    /// <paramref name="riders"/> (4.4: its children that a ripple trim of it carries, <see cref="Riders"/>).
    /// </summary>
    private double RippleLimit(IReadOnlyList<TimelineTrackModel> tracks, double from, double delta, TimelineClip? moving,
                               List<(TimelineTrackModel Track, TimelineClip Clip)>? riders = null) =>
        Limit(tracks, FirstMovers(tracks, from), delta, moving, riders);

    private static double Limit(IReadOnlyList<TimelineTrackModel> tracks, int[] first, double delta, TimelineClip? moving,
                                List<(TimelineTrackModel Track, TimelineClip Clip)>? riders)
    {
        if (delta >= 0)
            return delta;

        for (var t = 0; t < tracks.Count; t++)
        {
            var clips = tracks[t].Clips;
            for (var i = first[t]; i < clips.Count; i++)
                delta = Math.Max(delta, LeadOf(clips[i]) - clips[i].Start);
            if (Staying(clips, first[t], moving, riders) is { } stays)
                delta = Math.Max(delta, stays.End - clips[first[t]].Start);
        }

        // Held back, a move earlier never turns into one later.
        return Math.Min(delta, 0);
    }

    /// <summary>
    /// The clip on a track that stays where it is just before the first that moves (<paramref name="first"/>), or null: not
    /// <paramref name="moving"/>, whose end moves with them; riders (whose ends move with it, or which go) are passed over.
    /// </summary>
    private static TimelineClip? Staying(List<TimelineClip> clips, int first, TimelineClip? moving, List<(TimelineTrackModel Track, TimelineClip Clip)>? riders)
    {
        if (first <= 0 || first >= clips.Count)
            return null;

        var before = first - 1;
        while (before >= 0 && riders is not null && Holds(riders, clips[before]))
            before--;
        return before >= 0 && !ReferenceEquals(clips[before], moving) ? clips[before] : null;
    }

    /// <summary>
    /// Moves every clip that starts at or after <paramref name="from"/> on these tracks by one amount (see
    /// <see cref="RippleLimit"/>), and looks at the crossfades of every track again. Returns the amount moved.
    /// </summary>
    private double Ripple(IReadOnlyList<TimelineTrackModel> tracks, double from, double delta, TimelineClip? moving,
                          List<(TimelineTrackModel Track, TimelineClip Clip)>? riders = null)
    {
        var first = FirstMovers(tracks, from);
        delta = Limit(tracks, first, delta, moving, riders);
        var moves = Math.Abs(delta) > Slack / 1000;
        var whole = WholeFrames(delta);
        for (var t = 0; t < tracks.Count; t++)
        {
            var clips = tracks[t].Clips;
            var stays = Staying(clips, first[t], moving, riders);
            for (var i = first[t]; moves && i < clips.Count; i++)
            {
                var clip = clips[i];
                Remember(clip);
                var start = Moved(clip.Start, delta, whole);

                // Put back on its frame, the first to move may land a microsecond before the end of a free clip that holds it back: it meets it instead.
                if (i == first[t] && stays is not null && start < stays.End)
                    start = stays.End;
                SetSpan(clip, start, Moved(clip.End, delta, whole));
            }

            // Even when nothing could move: the edit that called may have parted a crossfade's clips.
            Reconcile(tracks[t]);
        }
        return moves ? delta : 0;
    }

    // ----- A clip's look, copied before it is changed -----

    internal static readonly JsonSerializerOptions CopyOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>A deep copy of a look: what one clip may change without changing another's.</summary>
    internal static LayerState CopyOf(LayerState state) =>
        JsonSerializer.Deserialize<LayerState>(JsonSerializer.SerializeToUtf8Bytes(state, CopyOptions), CopyOptions) ?? new LayerState();

    /// <summary>
    /// A look with every keyframe, effect keyframe and effect range moved by <paramref name="seconds"/>, as
    /// <see cref="Layer.ShiftKeys"/> moves a layer's, but to the microsecond (the grain of a quantized time, so that keys
    /// stay on their frames at NTSC rates) and with ranges not held at 0 (so that a trim and its undoing give the range
    /// back whole): for a clip whose start moved while what it shows stayed. A copy, unless <paramref name="inPlace"/> or
    /// nothing moves.
    /// </summary>
    private static LayerState Shifted(LayerState state, double seconds, bool inPlace = false)
    {
        if (seconds == 0 || !IsTimed(state))
            return state;

        var look = inPlace ? state : CopyOf(state);
        (look.KeysX, look.KeysY, look.KeysScale) = (Shift(look.KeysX), Shift(look.KeysY), Shift(look.KeysScale));
        (look.KeysRotation, look.KeysOpacity) = (Shift(look.KeysRotation), Shift(look.KeysOpacity));
        foreach (var effect in look.Effects ?? [])
        {
            if (effect.Keys is { } keys)
            {
                foreach (var name in keys.Keys.ToList())
                    keys[name] = Shift(keys[name]) ?? [];
            }

            if (effect.Range is [var from, var to] && double.IsFinite(from + seconds) && double.IsFinite(to + seconds))
                effect.Range = [Math.Round(from + seconds, 6), Math.Round(to + seconds, 6)];
        }
        return look;

        List<Keyframe>? Shift(List<Keyframe>? keys) =>
            keys?.Select(k => k with { Time = Math.Round(k.Time + seconds, 6) }).ToList();
    }

    /// <summary>Whether a look has anything placed in time: keyframes, an effect's keyframes or its range.</summary>
    private static bool IsTimed(LayerState state) =>
        state.KeysX is { Count: > 0 } || state.KeysY is { Count: > 0 } || state.KeysScale is { Count: > 0 }
        || state.KeysRotation is { Count: > 0 } || state.KeysOpacity is { Count: > 0 }
        || state.Effects?.Any(e => e.Keys is { Count: > 0 } || e.Range is not null) == true;

    // ----- A look split with its clip (4.3D): each half keeps its own keyframes, baked at the cut -----

    // How near the cut a keyframe is on it (keys are kept to the microsecond).
    private const double KeyOnCut = 5e-7;

    /// <summary>
    /// The looks of the two halves of a clip split <paramref name="cut"/> seconds into it, each a copy: every keyframe
    /// and effect keyframe goes to the half it is in (the second's counted from the cut), with a key on the cut where a
    /// stretch runs across it (<see cref="SplitKeys"/>); an effect's range that runs across the cut ends there on the first
    /// half and starts there on the second, one wholly on one side moves with it. Neither half jumps or changes its motion.
    /// </summary>
    private static (LayerState First, LayerState Second) SplitLook(LayerState state, double cut)
    {
        if (!IsTimed(state))
            return (state, CopyOf(state));

        var (first, second) = (CopyOf(state), CopyOf(state));
        (first.KeysX, second.KeysX) = SplitKeys(state.KeysX, cut);
        (first.KeysY, second.KeysY) = SplitKeys(state.KeysY, cut);
        (first.KeysScale, second.KeysScale) = SplitKeys(state.KeysScale, cut);
        (first.KeysRotation, second.KeysRotation) = SplitKeys(state.KeysRotation, cut);
        (first.KeysOpacity, second.KeysOpacity) = SplitKeys(state.KeysOpacity, cut);
        for (var i = 0; i < (state.Effects?.Count ?? 0); i++)
        {
            var (effect, firstEffect, secondEffect) = (state.Effects![i], first.Effects![i], second.Effects![i]);
            if (effect.Keys is { } keys)
            {
                foreach (var (name, list) in keys)
                {
                    var (a, b) = SplitKeys(list, cut);
                    (firstEffect.Keys![name], secondEffect.Keys![name]) = (a ?? [], b ?? []);
                }
            }

            if (effect.Range is [var from, var to] && double.IsFinite(from) && double.IsFinite(to))
            {
                if (from < cut - KeyOnCut && to > cut + KeyOnCut)
                    (firstEffect.Range, secondEffect.Range) = ([from, Math.Round(cut, 6)], [0, Math.Round(to - cut, 6)]);
                else
                    secondEffect.Range = [Math.Round(from - cut, 6), Math.Round(to - cut, 6)];
            }
        }

        return (first, second);
    }

    /// <summary>
    /// One property's keyframes for the two halves of a clip split <paramref name="cut"/> seconds into it: each half keeps
    /// the keys on its side (the second's moved back by the cut), a key on the cut going to both. Where a stretch runs
    /// across the cut, each half gets a key on it with the value there (as <see cref="EasingCurves.ValueAt"/> works it
    /// out, as the graph does), and each half's stretch is its part of the curve (<see cref="EasingCurves.Cut"/>), so that
    /// the motion on either side is exactly what it was. A half with none of the keys holds the value it had.
    /// </summary>
    private static (List<Keyframe>? First, List<Keyframe>? Second) SplitKeys(List<Keyframe>? keys, double cut)
    {
        if (keys is not { Count: > 0 })
            return (keys, keys is null ? null : []);

        Keyframe Back(Keyframe key) => key with { Time = Math.Round(key.Time - cut, 6) };
        var before = keys.FindLastIndex(k => k.Time < cut - KeyOnCut);
        var after = keys.FindIndex(k => k.Time > cut + KeyOnCut);
        var on = keys.Where(k => Math.Abs(k.Time - cut) <= KeyOnCut).ToList();
        var later = after < 0 ? [] : keys.Skip(after).Select(Back).ToList();
        var earlier = keys.Take(before + 1).ToList();

        if (on.Count > 0)
            return ([.. earlier, .. on], [.. on.Select(Back), .. later]);
        if (before < 0)
            return ([new Keyframe(Math.Round(cut, 6), keys[0].Value, EasingType.Linear)], later);
        if (after < 0)
            return (earlier, [new Keyframe(0, keys[^1].Value, EasingType.Linear)]);

        // A stretch across the cut: a key on it on both sides, and the stretch's curve cut in two.
        var (a, b) = (keys[before], keys[after]);
        var value = EasingCurves.ValueAt(keys, cut);
        var (left, right) = EasingCurves.Cut(a, (cut - a.Time) / (b.Time - a.Time));
        earlier[^1] = left;
        earlier.Add(a.WithPart(0, 1) with { Time = Math.Round(cut, 6), Value = value });
        return (earlier, [right with { Time = 0, Value = value }, .. later]);
    }

    // ----- Two clips joined back into one (4.4-BugHunt): a split undone, where a fold opened at an edge shows its piece -----

    /// <summary>
    /// The look of two clips joined into one, the second starting <paramref name="cut"/> seconds after the first (a split
    /// undone): the first's keys and the second's moved on by the cut (<see cref="JoinKeys"/>), an effect's range that a split
    /// cut in two whole again; with nothing timed in either, the second's look (<paramref name="keepSecond"/>) or the
    /// first's. Null when the two are not one look cut in two: anything else differs, or their keys do not join.
    /// </summary>
    private static LayerState? JoinLook(LayerState first, LayerState second, double cut, bool keepSecond)
    {
        if (!ReferenceEquals(first, second) && Timeless(first) != Timeless(second))
            return null;
        if (!IsTimed(first) && !IsTimed(second))
            return keepSecond ? second : first;

        if (!JoinKeys(first.KeysX, second.KeysX, cut, out var x) || !JoinKeys(first.KeysY, second.KeysY, cut, out var y)
            || !JoinKeys(first.KeysScale, second.KeysScale, cut, out var scale) || !JoinKeys(first.KeysRotation, second.KeysRotation, cut, out var rotation)
            || !JoinKeys(first.KeysOpacity, second.KeysOpacity, cut, out var opacity))
        {
            return null;
        }

        var look = CopyOf(first);
        (look.KeysX, look.KeysY, look.KeysScale, look.KeysRotation, look.KeysOpacity) = (x, y, scale, rotation, opacity);
        for (var i = 0; i < (look.Effects?.Count ?? 0); i++)
        {
            var (a, b, joined) = (first.Effects![i], second.Effects![i], look.Effects![i]);
            if (a.Keys is not null || b.Keys is not null)
            {
                var keys = new Dictionary<string, List<Keyframe>>();
                foreach (var name in (a.Keys?.Keys ?? Enumerable.Empty<string>()).Union(b.Keys?.Keys ?? Enumerable.Empty<string>()))
                {
                    if (!JoinKeys(a.Keys?.GetValueOrDefault(name), b.Keys?.GetValueOrDefault(name), cut, out var list))
                        return null;
                    keys[name] = list ?? [];
                }
                joined.Keys = keys;
            }

            if (!JoinRange(a.Range, b.Range, cut, out var range))
                return null;
            joined.Range = range;
        }
        return look;
    }

    /// <summary>A look as JSON without where its keys, and its effects' keys and ranges, lie in time: what the two halves of a split share.</summary>
    private static string Timeless(LayerState look)
    {
        var node = JsonSerializer.SerializeToNode(look, CopyOptions);
        if (node is JsonObject json)
        {
            foreach (var name in (string[])[nameof(LayerState.KeysX), nameof(LayerState.KeysY), nameof(LayerState.KeysScale), nameof(LayerState.KeysRotation), nameof(LayerState.KeysOpacity)])
                json.Remove(name);

            if (json[nameof(LayerState.Effects)] is JsonArray effects)
            {
                foreach (var effect in effects.OfType<JsonObject>())
                {
                    effect.Remove(nameof(EffectState.Keys));
                    effect.Remove(nameof(EffectState.Range));
                }
            }
        }
        return node?.ToJsonString() ?? "";
    }

    /// <summary>
    /// One property's keys of two clips joined into one, the second starting <paramref name="cut"/> seconds after the first:
    /// the first's keys before the cut, its key on the cut only where the second has none there, and the second's moved on
    /// by the cut; then the key a split put on the cut taken out again where the motion is the same without it
    /// (<see cref="Unsplit"/>). False when the two do not join into one motion: one has keys and the other none, a key lies
    /// past the first's end or before the second's start, or the value is not the same on both sides of the cut.
    /// </summary>
    private static bool JoinKeys(List<Keyframe>? first, List<Keyframe>? second, double cut, out List<Keyframe>? joined)
    {
        joined = first is null ? null : [.. first];
        List<Keyframe> before = first ?? [], after = second ?? [];
        if (before.Count == 0 && after.Count == 0)
            return true;
        if (before.Count == 0 || after.Count == 0 || before.Exists(k => k.Time > cut + KeyOnCut) || after.Exists(k => k.Time < -KeyOnCut))
            return false;

        var value = EasingCurves.ValueAt(before, cut);
        if (Math.Abs(value - EasingCurves.ValueAt(after, 0)) > 1e-9 * Math.Max(1, Math.Abs(value)))
            return false;

        var onCut = after.Exists(k => Math.Abs(k.Time) <= KeyOnCut);
        var keys = before.Where(k => k.Time < cut - KeyOnCut || (!onCut && Math.Abs(k.Time - cut) <= KeyOnCut)).ToList();
        keys.AddRange(after.Select(k => k with { Time = Math.Round(k.Time + cut, 6) }));
        joined = Unsplit(keys, cut);
        return true;
    }

    /// <summary>
    /// Keys joined at a cut without the key a split put on it, where the motion stays the same without it: the key before it
    /// and it leaving by parts of one curve (<see cref="EasingCurves.Cut"/> undone: the two parts one again; Linear and Hold
    /// are whole on both sides), or the key only repeating the value it is reached with. Checked on the curve itself, as
    /// <see cref="EasingCurves.ValueAt"/> (and so the graph) reads it, around the cut; the keys as they are otherwise.
    /// </summary>
    private static List<Keyframe> Unsplit(List<Keyframe> keys, double cut)
    {
        var at = keys.FindIndex(k => Math.Abs(k.Time - cut) <= KeyOnCut);
        if (at < 0)
            return keys;

        var (key, previous) = (keys[at], at > 0 ? keys[at - 1] : (Keyframe?)null);
        var candidates = new List<List<Keyframe>>(2);
        if (previous is { } left && left.Easing == key.Easing
            && (left.BezierX1, left.BezierY1, left.BezierX2, left.BezierY2) == (key.BezierX1, key.BezierY1, key.BezierX2, key.BezierY2))
        {
            var whole = left.Easing is EasingType.Linear or EasingType.Hold;
            if (whole ? !left.IsPart && !key.IsPart : Math.Abs(left.Part.End - key.Part.Start) < 1e-9)
            {
                var rejoined = new List<Keyframe>(keys);
                rejoined[at - 1] = whole ? left : left.WithPart(left.Part.Start, key.Part.End);
                rejoined.RemoveAt(at);
                candidates.Add(rejoined);
            }
        }

        var without = new List<Keyframe>(keys);
        without.RemoveAt(at);
        candidates.Add(without);

        // The motion the key on the cut takes part in: from the key before it to the key after it (a second either way where
        // there is none, past which both hold the same value).
        var (from, to) = (previous?.Time ?? cut - 1, at + 1 < keys.Count ? keys[at + 1].Time : cut + 1);
        foreach (var candidate in candidates)
        {
            if (candidate.Count > 0 && SameMotion(keys, candidate, from, to))
                return candidate;
        }
        return keys;
    }

    /// <summary>Whether two key lists give the same values from <paramref name="from"/> to <paramref name="to"/> (33 moments, to a billionth).</summary>
    private static bool SameMotion(List<Keyframe> a, List<Keyframe> b, double from, double to)
    {
        for (var i = 0; i <= 32; i++)
        {
            var t = from + (to - from) * i / 32;
            var (x, y) = (EasingCurves.ValueAt(a, t), EasingCurves.ValueAt(b, t));
            if (Math.Abs(x - y) > 1e-9 * Math.Max(1, Math.Abs(x)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// An effect's range of two clips joined into one (see <see cref="JoinLook"/>): one range again where a split cut it in two
    /// (the first's ending on the cut, the second's starting on it), or the first's where a split moved it whole into both.
    /// False when the two are not one range: only one has one, or they lie apart.
    /// </summary>
    private static bool JoinRange(double[]? first, double[]? second, double cut, out double[]? joined)
    {
        joined = first is null ? null : [.. first];
        if (first is null && second is null)
            return true;
        if (first is not [var a0, var a1] || second is not [var b0, var b1])
            return false;
        if (Math.Abs(a1 - cut) <= KeyOnCut && Math.Abs(b0) <= KeyOnCut)
        {
            joined = [a0, Math.Round(b1 + cut, 6)];
            return true;
        }
        return Math.Abs(a0 - (b0 + cut)) <= 1e-6 && Math.Abs(a1 - (b1 + cut)) <= 1e-6;
    }

    /// <summary>
    /// A look with every keyframe time, effect keyframe time and effect range scaled by <paramref name="stretch"/> (the old
    /// speed over the new), to the microsecond: a clip played at another speed, its keys staying on what they were set on.
    /// A copy, unless nothing is timed.
    /// </summary>
    private static LayerState Scaled(LayerState state, double stretch)
    {
        if (stretch == 1 || !IsTimed(state))
            return state;

        var look = CopyOf(state);
        (look.KeysX, look.KeysY, look.KeysScale) = (Scale(look.KeysX), Scale(look.KeysY), Scale(look.KeysScale));
        (look.KeysRotation, look.KeysOpacity) = (Scale(look.KeysRotation), Scale(look.KeysOpacity));
        foreach (var effect in look.Effects ?? [])
        {
            if (effect.Keys is { } keys)
            {
                foreach (var name in keys.Keys.ToList())
                    keys[name] = Scale(keys[name]) ?? [];
            }

            if (effect.Range is [var from, var to])
                effect.Range = [Math.Round(from * stretch, 6), Math.Round(to * stretch, 6)];
        }
        return look;

        List<Keyframe>? Scale(List<Keyframe>? keys) =>
            keys?.Select(k => k with { Time = Math.Round(k.Time * stretch, 6) }).ToList();
    }

    private static string Seconds(double seconds) => seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
