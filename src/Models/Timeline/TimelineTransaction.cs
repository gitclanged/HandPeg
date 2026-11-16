using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

// ----- Transactional editing: one transaction per change -----
// All tracks edit uniformly. While quantizing, requested times snap to frames and bounds to the frames inside them, unless the
// edit is free (freeUnquantized). Rules (with file bounds, no same-track overlap, locked tracks and J/L cuts):
// - Each edit is atomic: a refused edit (TimelineEditException) changes nothing and the transaction stays usable.
// - Edges clamp: trim/roll/slip stop at file ends, neighbours, time 0 and the minimum clip length, never reverse, and return
//   the distance moved (0 = no change).
// - Placements refuse: overlap, a locked track or a wrong track kind throws.
// - Ripple shifts every clip at/after the edit point (own track or all unlocked tracks; subtitle tracks never ripple) by one
//   amount; any collision or move before 0 limits the shift for all, ripple trims included.
// - Look changes (e.g. keyframes when a start moves) replace State with a modified copy, so rollback snapshots stay intact.
// - Children (TimelineClip.ParentClipId, TimelineTrackModel.ParentTrackId): a parent edit carries overlapping children within
//   the same step. Split cuts them; trim carries children on the edge and unlinks those left wholly outside (a ripple trim still
//   closes up and removes children shorter than the minimum); roll = two trims; move shifts them on their own tracks (refused
//   if any lacks room); delete and fold take them; ripple includes child tracks. Children on locked tracks stay. The tether mark
//   follows the link: kept by split (both halves), move and re-parenting; cleared with the link.
// - Folds keep their id when unfolded (RestoredFold), re-folded, or resized at the seam (ResizeFold). Folds and RestoredFolds
//   are immutable values shared with document copies (undo steps); edits replace them.

/// <summary>Refused edit: no room, locked track, wrong clip kind, or time outside the clip.</summary>
public sealed class TimelineEditException(string message) : InvalidOperationException(message);

/// <summary>
/// Transaction on a <see cref="TimelineDocument"/> (<see cref="TimelineDocument.BeginTransaction"/>): edit, then <see cref="Commit"/>.
/// Dispose without commit rolls back. Nested transactions merge on commit and roll back only their own part.
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

    /// <summary>Commits; the outermost commit raises <see cref="TimelineDocument.Committed"/> once if anything changed.</summary>
    void Commit();

    /// <summary>Inserts an empty track at stacking <paramref name="index"/> (0 = bottom) and renumbers its kind.</summary>
    TimelineTrackModel InsertTrack(int index, TimelineTrackKind kind, string? name = null);

    /// <summary>
    /// Places an unplaced clip on <paramref name="track"/> at its <see cref="TimelineClip.Start"/> (not before its audio lead-in
    /// allows), length clamped to the remaining source. No overlap, except with <paramref name="allowCrossfadeOverlap"/>: an overlap
    /// of one frame up to <see cref="TimelineDocument.LongestDragCrossfade"/> (and half of either) abuts and crossfades. Refusal leaves the clip unchanged.
    /// </summary>
    TimelineClip PlaceClip(TimelineTrackModel track, TimelineClip clip, bool allowCrossfadeOverlap = true, bool freeUnquantized = false);

    /// <summary>
    /// Moves a clip to <paramref name="newStart"/> on <paramref name="targetTrack"/>, or (<paramref name="insertTrackAtIndex"/> >= 0) onto a
    /// new track of that kind inserted there. Keyframes move with it; the crossfade survives if it lands against the same neighbour.
    /// With <paramref name="ripple"/>: the old track closes up, then clips from <paramref name="newStart"/> on the target make room.
    /// </summary>
    void MoveClip(TimelineClip clip, TimelineTrackModel targetTrack, double newStart, bool ripple = false, int insertTrackAtIndex = -1,
                  bool allowCrossfadeOverlap = true, bool freeUnquantized = false);

    /// <summary>
    /// Splits at <paramref name="sequenceTime"/> and returns the second part. The first keeps fade-in, crossfade and audio lead-in;
    /// the second takes fade-out and audio tail, with keyframes rebased to stay on their frames. Throws if either part (or its audio)
    /// would be shorter than the minimum clip.
    /// </summary>
    TimelineClip SplitClipAt(TimelineClip clip, double sequenceTime, bool freeUnquantized = false);

    /// <summary>
    /// Trims the start to <paramref name="newStart"/> keeping the end and keyframe frames. With <paramref name="ripple"/> the clip keeps
    /// its position, trims its head, and later clips follow its end. Returns the start delta (positive = later).
    /// </summary>
    double TrimClipStart(TimelineClip clip, double newStart, bool ripple = false, bool rippleAllUnlockedTracks = true, bool freeUnquantized = false);

    /// <summary>Trims the end to <paramref name="newEnd"/>; with <paramref name="ripple"/> later clips follow. Returns the delta.</summary>
    double TrimClipEnd(TimelineClip clip, double newEnd, bool ripple = false, bool rippleAllUnlockedTracks = true, bool freeUnquantized = false);

    /// <summary>
    /// Sets J/L cut on a linked A/V clip: audio starts <paramref name="newAudioLeadIn"/> before and ends <paramref name="newAudioTailOut"/>
    /// after the video (negative = inside), clamped to the file, time 0 and the minimum length. NaN leaves a side unchanged. Returns the values set.
    /// </summary>
    (double LeadIn, double TailOut) TrimAudioSplit(TimelineClip clip, double newAudioLeadIn, double newAudioTailOut, bool freeUnquantized = false);

    /// <summary>
    /// Roll edit: moves the cut between two abutting clips to <paramref name="newCutTime"/>; only they and their children change.
    /// Returns the cut delta.
    /// </summary>
    double RollCut(TimelineClip earlier, TimelineClip later, double newCutTime, bool freeUnquantized = false);

    /// <summary>
    /// Slip edit: offsets the source by <paramref name="mediaDeltaSeconds"/> within the file; timeline position, keyframes and fades
    /// stay. Returns the applied delta.
    /// </summary>
    double SlipClip(TimelineClip clip, double mediaDeltaSeconds);

    /// <summary>
    /// Removes a clip leaving a gap; <paramref name="withChildren"/> also removes overlapping descendants. Otherwise children stay as
    /// orphans (re-adopted if the parent is restored).
    /// </summary>
    void DeleteClip(TimelineClip clip, bool withChildren = true);

    /// <summary>
    /// Lifts [<paramref name="from"/>, <paramref name="to"/>] (Sequence seconds) out of <paramref name="clip"/> atomically: splits at each
    /// end more than <see cref="TimelineDocument.MinimumClipSeconds"/> inside (else extends to that edge), then removes the middle with
    /// its children. Returns the length removed. Throws <see cref="TimelineEditException"/> with no change if either split is refused.
    /// </summary>
    double DeleteStretchOf(TimelineClip clip, double from, double to);

    /// <summary>Ripple delete; returns how far later clips moved back.</summary>
    double RippleDelete(TimelineClip clip, bool rippleAllUnlockedTracks);

    /// <summary>
    /// Close Gap: shifts clips from <paramref name="gapEnd"/> back by up to the gap length. Returns the distance moved.
    /// </summary>
    double CloseGap(TimelineTrackModel track, double gapStart, double gapEnd, bool rippleAllUnlockedTracks);

    // ----- Clip and track properties (atomic, clamped, refused on locked tracks) -----

    /// <summary>
    /// Sets fades, each 0..<see cref="TimelineDocument.LongestFade"/>, together at most the clip length (fade-in has priority); null
    /// styles are unchanged. Returns the lengths set.
    /// </summary>
    (double In, double Out) SetClipFades(TimelineClip clip, double fadeInSeconds, double fadeOutSeconds, TransitionStyle? fadeInStyle = null, TransitionStyle? fadeOutStyle = null);

    /// <summary>
    /// Sets the crossfade from the abutting previous clip (0 removes), capped at <see cref="TimelineDocument.LongestCrossfade"/> and
    /// either clip's length. Returns the value set.
    /// </summary>
    double SetCrossfade(TimelineClip clip, double seconds);

    /// <summary>The gain of its sound, in decibels, from -40 to +24 (a layer's range).</summary>
    void SetClipGain(TimelineClip clip, double gainDb);

    /// <summary>Sets disabled (video and audio) and muted flags; null leaves a flag unchanged.</summary>
    void SetClipFlags(TimelineClip clip, bool? hidden = null, bool? muted = null);

    /// <summary>Which picture of its file the player shows: its proxy, the original, or as the setting says.</summary>
    void SetClipProxy(TimelineClip clip, ProxyChoice choice);

    /// <summary>
    /// Sets <see cref="TimelineClip.BeatTimes"/> (file seconds, finite, non-negative, strictly ascending); null clears. Identical beats are a no-op.
    /// </summary>
    void SetClipBeats(TimelineClip clip, double[]? beats);

    /// <summary>
    /// Sets <see cref="TimelineDocument.MasterGainDb"/> (-40..+12 dB; NaN = 0). A change alone still raises
    /// <see cref="TimelineDocument.Committed"/>, so it is an Undo step.
    /// </summary>
    void SetMasterGain(double decibels);

    /// <summary>
    /// Sets colour space, pixel aspect and sample rate (<see cref="TimelineDocument.ProjectSettings"/>; unsupported rates become 48000).
    /// A change alone raises <see cref="TimelineDocument.Committed"/> (an Undo step); identical values are a no-op.
    /// </summary>
    void SetProjectSettings(ProjectColorSpace colorSpace, ProjectPixelAspect pixelAspect, int sampleRate);

    /// <summary>Replaces Sequence markers with a copy of <paramref name="markers"/>; commits on its own like the master gain.</summary>
    void SetMarkers(IReadOnlyList<TimelineMarker> markers);

    /// <summary>Sets the Sequence Output effect stack (list copied, states shared) and its bypass flag.</summary>
    void SetSequenceEffects(IReadOnlyList<EffectState> effects, bool bypassed);

    /// <summary>
    /// Sets the natural (landscape) frame size and whether it was explicitly chosen (<see cref="TimelineDocument.ExplicitFrame"/>).
    /// </summary>
    void SetFrame(int naturalWidth, int naturalHeight, bool explicitFrame);

    /// <summary>
    /// Sets the frame rate and explicit flag (<see cref="TimelineDocument.ExplicitFrameRate"/>); throws <see cref="TimelineEditException"/>
    /// when not positive. Clips are not requantized: callers guarantee no clips remain on the old grid.
    /// </summary>
    void SetFrameRate(FrameRate rate, bool explicitRate);

    /// <summary>
    /// Appends files to <see cref="TimelineDocument.MediaPool"/> once each (case-insensitive), skipping empty paths and URLs. A change alone
    /// raises <see cref="TimelineDocument.Committed"/>. Returns false when all were present.
    /// </summary>
    bool AddToMediaPool(IEnumerable<string> paths);

    /// <summary>Removes files from Project Media; false when none was present. Callers check clip references.</summary>
    bool RemoveFromMediaPool(IEnumerable<string> paths);

    /// <summary>
    /// Sets playback speed (<see cref="Layer.SlowestSpeed"/>..<see cref="Layer.FastestSpeed"/>) over the same source range, so the end
    /// moves; colliding same-track clips are pushed by the growth; keyframes, effect ranges and J/L cuts scale. With
    /// <paramref name="ripple"/>, everything after the old end shifts by the delta on all unlocked tracks (<paramref name="rippleAll"/>)
    /// or its own track and Sub-Lanes. Returns the speed set (unchanged for non-media clips).
    /// </summary>
    double SetClipSpeed(TimelineClip clip, double speed, bool freeUnquantized = false, bool ripple = false, bool rippleAll = true);

    /// <summary>Sets A/V link and the unlinked audio offset (seconds, positive = later; not quantized).</summary>
    void SetAudioLink(TimelineClip clip, bool linked, double slipSeconds);

    /// <summary>
    /// Sets track switches, audio and name (null = unchanged). Allowed on locked tracks: locking protects clips, not switches.
    /// </summary>
    void SetTrack(TimelineTrackModel track, bool? hidden = null, bool? muted = null, bool? soloed = null, bool? locked = null,
                  double? gainDb = null, string? name = null);

    /// <summary>
    /// Removes a track and its clips and renumbers its kind. Child tracks are re-parented to its parent (or top level).
    /// </summary>
    void RemoveTrack(TimelineTrackModel track);

    /// <summary>Moves a track to stacking <paramref name="index"/> (0 = bottom) and renumbers.</summary>
    void MoveTrack(TimelineTrackModel track, int index);

    // ----- Folds: ranges removed from the Sequence and stored at a seam for restoration -----

    /// <summary>
    /// Folds a range out (Remove Dead Air): splits clips at both ends (on frames), stores the middle in a <see cref="FoldedSegment"/>,
    /// and ripples the rest closed, on <paramref name="seamTrack"/> or all unlocked tracks (<paramref name="rippleAllUnlockedTracks"/>).
    /// The seam is the following clip on <paramref name="seamTrack"/> (else the preceding). Covered <see cref="TimelineDocument.RestoredFolds"/>
    /// are dropped. Returns the fold, or null when the range was empty.
    /// </summary>
    FoldedSegment? FoldStretch(TimelineTrackModel seamTrack, double from, double to, bool rippleAllUnlockedTracks);

    /// <summary>
    /// Restores a fold at its seam: splits clips crossing the seam, shifts everything after it by the fold length, and reinserts
    /// the stored clips. Records a <see cref="RestoredFold"/> with the same id for re-folding. Resolved by id, so a fold from an
    /// older document copy (undo step, view) still matches.
    /// </summary>
    void UnfoldSegment(FoldedSegment fold);

    /// <summary>
    /// Re-folds a <see cref="RestoredFold"/> (Hide pill) at its current span on its original tracks, reusing the id, as
    /// <see cref="FoldStretch"/>. Removes the record. Refused when the range no longer exists.
    /// </summary>
    FoldedSegment FoldRestored(RestoredFold restored);

    /// <summary>
    /// Resizes a fold at one side of its seam (dragging the seam clip's edge). <paramref name="seconds"/> > 0 reveals that much at
    /// <paramref name="side"/>; &lt; 0 folds more of the adjacent clip (head: clip before, tail: clip after). Frame-aligned, at least
    /// the minimum clip, never leaving the clip below the minimum; applies on every covered track with children. Revealed parts rejoin
    /// their split partner (keyframes merged) and the edge clip keeps its id. Revealing all (or all but less than a minimum clip)
    /// unfolds like <see cref="UnfoldSegment"/>. Returns the resized fold (same id) or null if fully restored; under half a frame is a
    /// no-op. Refused when a covered track is locked/missing, no clip meets the seam, or a clip would drop below the minimum.
    /// </summary>
    FoldedSegment? ResizeFold(FoldedSegment fold, FoldSide side, double seconds);

    // ----- Parent/child clips and tracks -----

    /// <summary>
    /// Sets the parent clip (<paramref name="parentClipId"/>; null unlinks). Refused for self, parents not in this document, descendants
    /// (cycles) and locked tracks. <paramref name="tethered"/> sets <see cref="TimelineClip.Tethered"/> (true only for user tethers); null
    /// keeps the current mark while a parent remains. Unlinking clears it.
    /// </summary>
    void SetClipParent(TimelineClip clip, Guid? parentClipId, bool? tethered = null);

    /// <summary>
    /// Sets the parent track (<paramref name="parentTrackId"/>; null = top level) for rippling, folding and draw order. Allowed on
    /// locked tracks. Refused for self, unknown tracks and descendants.
    /// </summary>
    void SetTrackParent(TimelineTrackModel track, Guid? parentTrackId);

    /// <summary>
    /// Renders captions beneath a track (<see cref="TimelineTrackModel.CaptionsUnder"/>) or, with null, on top. Allowed on locked
    /// tracks (draw order only). Refused for unknown tracks.
    /// </summary>
    void SetCaptionsUnder(TimelineTrackModel? track);

    /// <summary>
    /// Sets a per-track row height (<see cref="TimelineTrackModel.Height"/>); null reverts to the global height. Allowed on locked tracks.
    /// </summary>
    void SetTrackHeight(TimelineTrackModel track, double? height);

    /// <summary>
    /// Sets a picture track's Track Preset (<see cref="TimelineTrackModel.PresetNodeName"/>; null clears). The caller applies the preset
    /// to existing clips. Allowed on locked tracks. Refused for unknown or non-picture tracks.
    /// </summary>
    void SetTrackPreset(TimelineTrackModel track, string? presetName);
}

internal sealed partial class TimelineTransaction : ITimelineTransaction
{
    /// <summary>How near two times count as the same: a microsecond, the grain of a quantized time.</summary>
    private const double Slack = 1e-6;

    /// <summary>Clamp for edit times (~30 years) so frame indices never overflow.</summary>
    private const double Farthest = 1e9;

    private readonly TimelineDocument _doc;
    private readonly Dictionary<TimelineClip, TimelineClip> _clipsBefore = [];
    private readonly Dictionary<TimelineTrackModel, (List<TimelineClip> Clips, TimelineTrackModel Fields)> _tracksBefore = [];
    private List<TimelineTrackModel>? _trackOrderBefore;
    private List<FoldedSegment>? _foldsBefore;
    private List<RestoredFold>? _restoredBefore;
    private bool _foldsRemembered;

    // Pre-change Sequence-level values (master gain) for rollback, and whether any changed.
    private double? _masterGainBefore;
    private (ProjectColorSpace ColorSpace, ProjectPixelAspect PixelAspect, int SampleRate)? _projectSettingsBefore;
    private List<string>? _mediaPoolBefore;
    private bool _documentTouched;

    /// <summary>When set, splits record both halves (ResizeFold swaps their ids when folding more at the tail).</summary>
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

        // Outermost commit prunes stale RestoredFolds, only when something else changed (empty commits stay silent; earlier undo
        // copies keep theirs).
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
        RestoreDocumentFields();
        Close();
    }

    private void Close()
    {
        _closed = true;
        _doc.OpenTransactions.Remove(this);
        if (_doc.OpenTransactions.Count == 0)
            _doc.InvalidateClipIndex(); // outermost commit/rollback: clips may have moved, split or changed ids
    }

    private void EnsureInnermost()
    {
        if (_closed)
            throw new InvalidOperationException($"The transaction \"{Label}\" is already committed or rolled back.");
        if (!ReferenceEquals(_doc.OpenTransactions[^1], this))
            throw new InvalidOperationException($"A transaction opened inside \"{Label}\" is still open.");
    }

    // ----- Rollback snapshots: first-touch state per transaction level -----

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

    /// <summary>Snapshots both fold lists before the first write (records are immutable).</summary>
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

    /// <summary>Snapshots master gain before the first write; the innermost transaction reports the change.</summary>
    private void RememberMasterGain()
    {
        foreach (var open in _doc.OpenTransactions)
            open._masterGainBefore ??= _doc.MasterGainDb;
        _doc.OpenTransactions[^1]._documentTouched = true;
    }

    /// <summary>Snapshots colour space, pixel aspect and sample rate before the first write.</summary>
    private void RememberProjectSettings()
    {
        foreach (var open in _doc.OpenTransactions)
            open._projectSettingsBefore ??= (_doc.ColorSpace, _doc.PixelAspect, _doc.SampleRate);
        _doc.OpenTransactions[^1]._documentTouched = true;
    }

    /// <summary>Snapshots Project Media (list copy) before in-place mutation.</summary>
    private void RememberMediaPool()
    {
        foreach (var open in _doc.OpenTransactions)
            open._mediaPoolBefore ??= [.. _doc.MediaPool];
        _doc.OpenTransactions[^1]._documentTouched = true;
    }

    /// <summary>New distinct files not yet in Project Media, in order, excluding empty paths and URLs.</summary>
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

    /// <summary>Adds a newly placed clip's file to Project Media when pooling is enabled and the file is new.</summary>
    private void PoolPlaced(TimelineClip clip)
    {
        if (!_doc.PoolsPlacedFiles || TimelineDocument.PoolFileOf(clip) is not { } path || _doc.PoolHolds(path))
            return;

        RememberMediaPool();
        _doc.MediaPool.Add(path);
    }

    /// <summary>Removes RestoredFolds whose range no longer exists (<see cref="RestoredFold.SpanIn"/> null).</summary>
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
    /// Runs one edit atomically within the transaction: if it throws, its changes are rolled back and the transaction remains usable.
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

    // Frame snapping: Near, Down (at/before) or Up (at/after); identity when free. Signed so negative J/L leads snap too.
    // Times already on a frame stay put (tolerance in seconds matches quantization grain).
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
    /// Shifts a time by <paramref name="delta"/> (ripple, held-back trim). For whole-frame deltas, on-frame times re-snap to the exact
    /// frame time; otherwise raw addition, so edges that were equal doubles stay equal.
    /// </summary>
    private double Moved(double seconds, double delta, bool wholeFrames)
    {
        var moved = Math.Max(seconds + delta, 0);
        return wholeFrames && Rate.IsOnFrame(seconds) ? Rate.Quantize(moved) : moved;
    }

    private static void SetSpan(TimelineClip clip, double start, double end) => (clip.Start, clip.End) = (start, end);

    private double MinimumLength(bool free) => Up(TimelineDocument.MinimumClipSeconds, free);

    /// <summary>
    /// Snaps a requested edge to the nearest frame, unless that reverses the request direction (sub-frame move from off-frame), then
    /// returns <paramref name="current"/>.
    /// </summary>
    private double Wanted(double asked, double current, bool free)
    {
        var wanted = Near(asked, free);
        return asked == current || Math.Sign(wanted - current) != Math.Sign(asked - current) ? current : wanted;
    }

    /// <summary>
    /// Clamps <paramref name="wanted"/> to [<paramref name="earliest"/>, <paramref name="latest"/>] without overshooting or reversing the
    /// request (else returns <paramref name="current"/>).
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

    /// <summary>Head handle in timeline seconds (minus J-cut audio lead).</summary>
    private static double MediaBefore(TimelineClip clip) =>
        clip.PlaysFile ? Math.Max(clip.MediaOffset / clip.Speed - LeadOf(clip), 0) : double.PositiveInfinity;

    /// <summary>Tail handle in timeline seconds (minus L-cut audio tail).</summary>
    private static double MediaAfter(TimelineClip clip) =>
        Bounded(clip) ? Math.Max((clip.SourceDuration - clip.MediaEnd) / clip.Speed - TailOf(clip), 0) : double.PositiveInfinity;

    /// <summary>Latest end for a clip starting at <paramref name="start"/>, bounded by the source and L-cut audio.</summary>
    private double FitEnd(TimelineClip clip, double start, double end, bool free) =>
        Bounded(clip) ? Math.Min(end, Down(start + (clip.SourceDuration - clip.MediaOffset) / clip.Speed - TailOf(clip), free)) : end;

    /// <summary>
    /// Minimum video length: the minimum clip, extended so the audio span (lead-in to tail, either possibly negative) also meets it.
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

    /// <summary>Removes a clip from a track list; throws a refusal (not a fault) when absent.</summary>
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
    /// Drops crossfades whose clip no longer abuts its predecessor and caps the rest at either clip's length (trims and speed
    /// changes can leave them too long).
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

    public double DeleteStretchOf(TimelineClip clip, double from, double to) => Step(() =>
    {
        var piece = clip;
        if (to < piece.End - TimelineDocument.MinimumClipSeconds)
            Split(piece, to, free: false);
        if (from > piece.Start + TimelineDocument.MinimumClipSeconds)
            piece = Split(piece, from, free: false);
        var removed = piece.Duration;
        Lift(piece, family: true);
        return removed;
    });

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
        // Fresh array (never mutate the clip's): finite, non-negative, ascending, at least 1 us apart.
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

    public double SetClipSpeed(TimelineClip clip, double speed, bool freeUnquantized = false, bool ripple = false, bool rippleAll = true) =>
        Step(() => ripple ? RespeedRippling(clip, speed, freeUnquantized, rippleAll) : Respeed(clip, speed, freeUnquantized));

    public void SetAudioLink(TimelineClip clip, bool linked, double slipSeconds) => Step(() => Change(clip, c =>
    {
        (c.AudioLinked, c.AudioSlipSeconds) = (linked, double.IsFinite(slipSeconds) ? slipSeconds : 0);

        // Relinking: re-clamp any J/L cut kept while unlinked to time 0 and the file, on frames, as a new cut would be.
        if (linked && c.HasSound)
        {
            if (c.AudioLeadInSeconds > 0)
                c.AudioLeadInSeconds = Math.Max(Down(Math.Min(c.AudioLeadInSeconds, Math.Min(c.Start, c.PlaysFile ? c.MediaOffset / c.Speed : double.PositiveInfinity)), false), 0);
            if (c.AudioTailOutSeconds > 0 && Bounded(c))
                c.AudioTailOutSeconds = Math.Max(Down(Math.Min(c.AudioTailOutSeconds, Math.Max((c.SourceDuration - c.MediaEnd) / c.Speed, 0)), false), 0);

            // An inside cut on a since-shortened clip may leave audio below the minimum: fall back to the video span.
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

        // Keep caption depth: move CaptionsUnder to the next picture track down (or the lowest remaining); none left renders on top.
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

        // Child tracks survive (auto-removing an emptied track must not cascade): re-parent them to its parent or top level.
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

    public void SetClipParent(TimelineClip clip, Guid? parentClipId, bool? tethered = null) => Step(() =>
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
        return Change(clip, c => (c.ParentClipId, c.Tethered) = (parentClipId, parentClipId is not null && (tethered ?? c.Tethered)));
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
    /// Applies a property change on an unlocked track after snapshotting. A no-op change leaves the clip untouched, so an otherwise
    /// empty commit raises no Committed and records no Undo step.
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

        // Same source range, so the end moves (frame-aligned, clamped to the file). J/L lead and tail scale, then re-clamp to time 0
        // and the file on frames. A refusal rolls the step back.
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

        // Set the new end, then push colliding same-track clips by the growth (an outgoing crossfade survives: they still abut).
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
            // Sanitize first: speed in range, offset inside the file, fades/crossfade within limits, J-cut audio inside the file.
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

            // Pool the file in Project Media; it stays after the clip is deleted until removed from the bin.
            PoolPlaced(clip);
            return clip;
        }
        catch
        {
            clip.Restore(given);
            throw;
        }
    }

    /// <summary>Places an unplaced clip over [<paramref name="start"/>, <paramref name="end"/>], or abutting a neighbour as a crossfade.</summary>
    private void Land(TimelineTrackModel track, TimelineClip clip, double start, double end, bool allowCrossfadeOverlap, bool free)
    {
        TimelineClip? later = null;
        var crossfade = 0.0;
        if (Overlapped(track, start, end, null) is { } hit)
        {
            // Drag-over crossfade (overlap of at least one frame): abut, with the later clip crossfading from the earlier.
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

        // Overlapping descendants move by the same delta on their own tracks.
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

        // Collect children past the cut before splitting.
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

        // Children crossing the cut are split too (second part re-parented to the second half); children wholly after it move to
        // the second half. Unsplittable children (edge too close, audio too short, locked) stay whole with the first half.
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

        // Ripple riders: children on the start or end, or within the range the end may release.
        var riders = ripple ? Riders(clip, head: true, lostFrom: oldEnd - Math.Max(delta, 0)) : null;
        if (ripple && delta > 0)
        {
            // Limit by how far following clips can move back with the end (never leave a gap).
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

        // Ripple riders: children on the end or within the range it may release.
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

        // Enforce minimum audio length: expand an over-inset side (largest move first) up to the video edge, snapping each side to
        // the frame toward the video.
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

        // Children follow as for two trims: those on the cut move, those left outside their parent unlink. The shrinking side goes
        // first so the growing side finds room (children abutting at the cut abut at the new cut).
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

    /// <summary>Removes a clip; with <paramref name="family"/>, also its <see cref="Family"/>.</summary>
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

    // Crossfades are re-evaluated after closing the gap (pairs are by adjacency). The family is removed too and its tracks close up.
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

        // Gap edges at a clip use its exact edge (possibly off-frame after a free trim); elsewhere they snap to frames.
        var start = Edge(track, Math.Max(Finite(gapStart, "The gap's start"), 0));
        var end = Edge(track, Math.Min(Finite(gapEnd, "The gap's end"), Farthest));
        if (!(end - start > Slack))
            return 0;
        if (Overlapped(track, start, end, null) is { } inside)
            throw new TimelineEditException($"{Seconds(start)} to {Seconds(end)} s on {track.DisplayName} is not empty: a clip lies from {Seconds(inside.Start)} to {Seconds(inside.End)} s.");

        return Back(Ripple(RippleTracks(track, rippleAll), end, -(end - start), null));
    }
}
