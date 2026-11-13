using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- Child layers by drag (5.5A, the user's directive): a clip tethered to a clip, a track header nested, moved or taken out of its stack -----
// The Timeline's two drags that make a child layer, each one step of Undo. A clip let go on another clip it overlaps (UnifiedTimelineControl's
// tether) becomes that clip's child (SetClipParent): it moves, trims, splits, deletes and folds with it while they overlap, and its Link switch
// (the block's Lock, top right) unlinks it. A track's header dragged to a seam between the picture tracks moves the track there with its
// picture Sub-Lanes, together (a Sub-Lane dragged there leaves its stack as the menu's Unlink from Parent takes it out); dragged onto the
// lower half of another top-level picture track's header, the track becomes that track's Sub-Lane (SetTrackParent), its family kept
// together beside it on the side it was on, its clips' links left as they are. A tethered clip carries the mark (TimelineClip.Tethered, Pass
// 5.5A fix round 3), so it is never one of its parent's Game Preset pieces wherever it, its lane or its parent goes, and a piece stays one
// wherever it lies: no drag needs refusing for where a lane would lie. Stateless: nothing of it is kept between drags.
public partial class MainViewModel
{
    // How far up a clip's parents a loop is looked for (the model refuses one too).
    private const int MostParentSteps = 64;

    /// <summary>
    /// Whether <paramref name="child"/>, starting at <paramref name="start"/> (seconds), may become <paramref name="parent"/>'s child (5.5A, the
    /// tether): two clips on different tracks, neither on an audio or a captions track, the child on an unlocked track and no clip's child yet
    /// (a Cutout, a stream's sound and a clip already tethered keep theirs: its Link switch frees it first), not a video of the parent's own
    /// file (that would be one of its Cutouts), the parent not among the child's own family (no loop), and the two overlapping in time there
    /// (a child goes with its parent only while they overlap). Wherever the child's track lies, under the parent's own track too: it is marked
    /// tethered (Pass 5.5A), never one of the parent's Game Preset pieces.
    /// </summary>
    public bool CanAttachClip(Layer child, Layer parent, double start) => AttachRefusal(child, parent, start) is null;

    /// <summary>Why <paramref name="child"/> may not become <paramref name="parent"/>'s child at <paramref name="start"/> (5.5A, said in the status bar); null when it may (<see cref="CanAttachClip"/>).</summary>
    private string? AttachRefusal(Layer child, Layer parent, double start)
    {
        var plain = $"{child.Name} cannot become {parent.Name}'s child there: it has to overlap {parent.Name} in time on another unlocked picture track, and be no clip's child already (its Link switch frees it).";
        if (ClipOf(child) is not { } kid || ClipOf(parent) is not { } over || ReferenceEquals(kid, over) || !double.IsFinite(start))
            return plain;
        if (kid.ParentClipId is { } already && Document.FindClip(already) is not null)
            return plain;
        if (Document.TrackOf(kid) is not { IsLocked: false } track || Document.TrackOf(over) is not { } overTrack || ReferenceEquals(track, overTrack))
            return plain;
        if (track.Kind == TimelineTrackKind.Subtitle || overTrack.Kind == TimelineTrackKind.Subtitle)
            return plain;

        // A sound on an audio track stays a clip of its own (5.5A review): a tethered one would stop counting toward the Sequence's length
        // (SequenceSpans counts a loose sound only) and be taken for its picture's own sound; nor is a sound a picture's parent.
        if (track.Kind == TimelineTrackKind.Audio)
            return $"{child.Name} is on an audio track: a sound is never tethered to a clip (it counts toward the Sequence's length on its own). Select both to move them together.";
        if (overTrack.Kind == TimelineTrackKind.Audio)
            return $"{parent.Name} is on an audio track: a clip is tethered only to a picture. Select both to move them together.";

        // A video of the parent's own file tethered to it would be taken for one of its Cutouts (IsCutout: its speed, its presets), never as
        // a clip of its own (5.5A review): refused.
        if (kid.ContentKind == LayerKind.VideoFile && over.ContentKind == LayerKind.VideoFile && SameFile(kid.SourcePath, over.SourcePath ?? ""))
            return $"{child.Name} plays {parent.Name}'s own file: tethered to it, it would be one of {parent.Name}'s Cutouts. A Game Preset or the Cutout Studio makes Cutouts; select both to move them together.";

        // Not one of the child's own (followed up from the parent, the links never come back to the child).
        var up = over;
        for (var steps = 0; steps < MostParentSteps && up.ParentClipId is { } above; steps++)
        {
            if (above == kid.Id)
                return $"{parent.Name} hangs from {child.Name} already: a clip is never tethered to its own child.";
            if (Document.FindClip(above) is not { } next)
                break;
            up = next;
        }

        var from = Math.Max(start, 0);
        return Math.Min(from + kid.Duration, over.End) - Math.Max(from, over.Start) > 1e-3 ? null : plain;
    }

    /// <summary>
    /// Makes <paramref name="child"/> <paramref name="parent"/>'s child where it was let go (5.5A, the tether): moved along its own track to
    /// <paramref name="start"/> first when it was dragged in time, then linked and marked tethered (<see cref="TimelineClip.Tethered"/>: none
    /// of the parent's Game Preset pieces). One step of Undo ("attach ... to ..."); refused (why, said in the status bar) when it may not be.
    /// </summary>
    public bool AttachClip(Layer child, Layer parent, double start)
    {
        start = Math.Max(start, 0);
        if (AttachRefusal(child, parent, start) is { } why)
        {
            StatusText = why;
            return false;
        }

        if (ClipOf(child) is not { } kid || ClipOf(parent) is not { } over || Document.TrackOf(kid) is not { } track)
            return false;

        if (!Edit($"attach {child.Name} to {parent.Name}", edit =>
            {
                if (Math.Abs(start - kid.Start) > 0.0005)
                    edit.MoveClip(kid, track, start, allowCrossfadeOverlap: false);
                edit.SetClipParent(kid, over.Id, tethered: true);
            }))
        {
            return false;
        }

        RefreshLayerRows();
        StatusText = $"{child.Name} is {parent.Name}'s child now: it moves, trims and splits with it. Its Link switch (top right) unlinks it; Ctrl+Z puts it back.";
        return true;
    }

    /// <summary>Whether a track's header may be dragged (5.5A): a picture track's (Video or Switch), top-level or a picture Sub-Lane.</summary>
    public bool CanDragTrack(Layer row) => TrackOfRow(row) is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch };

    /// <summary>
    /// Whether a dragged header's track may go to the seam beside <paramref name="besideRow"/>'s top-level picture track (above it: in front):
    /// a Sub-Lane always may (it leaves its stack), a top-level track where that changes the stacking order.
    /// </summary>
    public bool CanMoveTrackToSeam(Layer row, Layer besideRow, bool above)
    {
        if (!CanDragTrack(row) || TrackOfRow(row) is not { } track || TrackOfRow(besideRow) is not { } beside || !IsTopPicture(beside) || ReferenceEquals(track, beside))
            return false;
        if (track.ParentTrackId is not null)
            return true;

        var order = OrderWith(PictureFamilyOf(track), beside, above);
        return !order.SequenceEqual(Document.Tracks);
    }

    /// <summary>
    /// Moves a dragged header's track to the seam beside <paramref name="besideRow"/>'s top-level picture track (5.5A), its picture Sub-Lanes with
    /// it, together; a Sub-Lane leaves its stack first, as the menu's Unlink from Parent takes it out (its clips' links to that track's clips go
    /// too). One step of Undo.
    /// </summary>
    public bool MoveTrackToSeam(Layer row, Layer besideRow, bool above)
    {
        if (!CanMoveTrackToSeam(row, besideRow, above) || TrackOfRow(row) is not { } track || TrackOfRow(besideRow) is not { } beside)
            return false;

        var from = track.ParentTrackId is { } parentId ? Document.Tracks.Find(t => t.Id == parentId) : null;
        var where = above ? "above" : "below";
        var label = from is not null ? $"take {row.Name} out of {from.DisplayName}'s stack" : $"move {row.Name} {where} {besideRow.Name}";
        if (!Edit(label, edit =>
            {
                // The one canonical way out of a stack: the menu's own, given the view that stands for the track's row (UnlinkFromParent folds
                // into this step: the track out of its stack, its clips' links cleared).
                if (track.ParentTrackId is not null)
                    UnlinkFromParent(RowOf(track) ?? row);
                if (track.ParentTrackId is not null)
                    throw new TimelineEditException($"{row.Name} could not be taken out of its stack.");
                Reorder(edit, OrderWith(PictureFamilyOf(track), beside, above));
            }))
        {
            return false;
        }

        StatusText = from is not null
            ? $"{row.Name} is a track of its own now, {where} {besideRow.Name}: edits of {from.DisplayName} no longer move it. Ctrl+Z puts it back."
            : $"{row.Name} moved {where} {besideRow.Name}, its Sub-Lanes with it. Ctrl+Z puts it back.";
        return true;
    }

    /// <summary>
    /// Whether a dragged header's track may become a Sub-Lane of <paramref name="parentRow"/>'s top-level picture track (5.5A): both picture
    /// tracks, not the same, not its Sub-Lane already, and the parent not under it. Its clips' tethers and pieces stay what they are (Pass 5.5A:
    /// the mark, never where a lane lies, says which a child is).
    /// </summary>
    public bool CanNestTrack(Layer row, Layer parentRow) => NestPair(row, parentRow) is not null;

    /// <summary>
    /// Makes a dragged header's track a Sub-Lane of <paramref name="parentRow"/>'s top-level picture track (5.5A): it folds, ripples and hides
    /// with it, drawn in its stack; its picture Sub-Lanes come with it, kept together beside the new parent on the side it was on (in front
    /// stays in front). Its clips' links and tethers are left as they are. One step of Undo.
    /// </summary>
    public bool NestTrackUnder(Layer row, Layer parentRow)
    {
        if (NestPair(row, parentRow) is not { } nest)
            return false;

        var (track, parent) = nest;
        var front = Document.Tracks.IndexOf(track) > Document.Tracks.IndexOf(parent);
        if (!Edit($"make {row.Name} a Sub-Lane of {parentRow.Name}", edit =>
            {
                edit.SetTrackParent(track, parent.Id);
                Reorder(edit, OrderWith(PictureFamilyOf(track), parent, front));
            }))
        {
            return false;
        }

        StatusText = $"{row.Name} is a Sub-Lane of {parentRow.Name} now: it folds, ripples and hides with it. Drag its header to a seam between the tracks to take it out again; Ctrl+Z puts it back.";
        return true;
    }

    // The dragged header's track and the top-level picture track it would be nested under (5.5A): both picture tracks, not the same, not its
    // Sub-Lane already, and the parent not under it; else null.
    private (TimelineTrackModel Track, TimelineTrackModel Parent)? NestPair(Layer row, Layer parentRow) =>
        CanDragTrack(row) && TrackOfRow(row) is { } track && TrackOfRow(parentRow) is { } parent && IsTopPicture(parent) && !ReferenceEquals(track, parent)
        && track.ParentTrackId != parent.Id && !LiesUnder(parent, track)
            ? (track, parent)
            : null;

    // Whether a track lies under another (in its stack, at any depth).
    private bool LiesUnder(TimelineTrackModel lane, TimelineTrackModel track)
    {
        var at = lane;
        for (var steps = 0; steps < MostParentSteps && at.ParentTrackId is { } id; steps++)
        {
            if (id == track.Id)
                return true;
            if (Document.Tracks.Find(t => t.Id == id) is not { } up || ReferenceEquals(up, at))
                return false;
            at = up;
        }

        return false;
    }

    /// <summary>A track and the picture lanes under it (at any depth: its Cutouts, its nested tracks), in the stacking order; sound and caption lanes are not counted (they stay at the end).</summary>
    private List<TimelineTrackModel> PictureFamilyOf(TimelineTrackModel track) =>
        Document.Tracks.Where(t => ReferenceEquals(t, track) || (t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle) && LiesUnder(t, track))).ToList();

    /// <summary>
    /// The stacking order with <paramref name="moving"/> (in its order) put together just above (in front of) or just below the whole picture
    /// family of <paramref name="beside"/>, as a new track beside it would go (NewTrackIndex); every other track keeps its order.
    /// </summary>
    private List<TimelineTrackModel> OrderWith(List<TimelineTrackModel> moving, TimelineTrackModel beside, bool above)
    {
        var rest = Document.Tracks.Where(t => !moving.Contains(t)).ToList();
        var (low, high) = (rest.IndexOf(beside), rest.IndexOf(beside));
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i].Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle) && LiesUnder(rest[i], beside))
                (low, high) = (Math.Min(low, i), Math.Max(high, i));
        }

        rest.InsertRange(above ? high + 1 : Math.Max(low, 0), moving);
        return rest;
    }

    // The tracks put in this order, within the edit (the document as it is now: each move leaves those before it in place).
    private void Reorder(ITimelineTransaction edit, IReadOnlyList<TimelineTrackModel> order)
    {
        for (var i = 0; i < order.Count && i < Document.Tracks.Count; i++)
        {
            if (!ReferenceEquals(Document.Tracks[i], order[i]))
                edit.MoveTrack(order[i], i);
        }
    }
}
