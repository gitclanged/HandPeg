using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- Drag parenting: clip tethers and track nesting -----
// Two Timeline drags, each one undo step. Clip tether: dropping a clip onto an overlapping clip makes it a child (SetClipParent) that
// moves, trims, splits, deletes and folds with the parent while overlapping; the block's Lock (Link) switch unlinks it.
// Header drag to a seam between picture tracks moves the track with its picture Sub-Lanes (a Sub-Lane leaves its stack like Unlink
// from Parent); onto the lower half of another top-level picture header it becomes that track's Sub-Lane (SetTrackParent), family kept
// together on its original side, clip links unchanged. Tethered clips carry TimelineClip.Tethered so they are never Game Preset pieces
// regardless of lane position, and pieces remain pieces anywhere. Stateless between drags.
public partial class MainViewModel
{
    // Max parent-chain depth checked for cycles (the model also rejects cycles).
    private const int MostParentSteps = 64;

    /// <summary>
    /// Whether <paramref name="child"/> at <paramref name="start"/> (s) may be tethered to <paramref name="parent"/>: different tracks, neither
    /// audio nor captions, child unlocked and unparented (Cutouts, stream audio and existing tethers must be unlinked first), not a video of
    /// the parent's file (would read as a Cutout), no cycle, and overlapping in time. Allowed under the parent's own track; always marked tethered.
    /// </summary>
    public bool CanAttachClip(Layer child, Layer parent, double start) => AttachRefusal(child, parent, start) is null;

    /// <summary>Status-bar reason a tether is refused, or null when allowed (<see cref="CanAttachClip"/>).</summary>
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

        // Audio-track clips stay independent: a tethered one would drop out of the Sequence length (SequenceSpans counts only loose audio)
        // and read as the picture's own audio; audio is never a picture's parent either.
        if (track.Kind == TimelineTrackKind.Audio)
            return $"{child.Name} is on an audio track: a sound is never tethered to a clip (it counts toward the Sequence's length on its own). Select both to move them together.";
        if (overTrack.Kind == TimelineTrackKind.Audio)
            return $"{parent.Name} is on an audio track: a clip is tethered only to a picture. Select both to move them together.";

        // A same-file video child would be classified as a Cutout (IsCutout: speed, presets): refuse.
        if (kid.ContentKind == LayerKind.VideoFile && over.ContentKind == LayerKind.VideoFile && SameFile(kid.SourcePath, over.SourcePath ?? ""))
            return $"{child.Name} plays {parent.Name}'s own file: tethered to it, it would be one of {parent.Name}'s Cutouts. A Game Preset or the Cutout Studio makes Cutouts; select both to move them together.";

        // Cycle check: walking up from the parent must never reach the child.
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
    /// Tethers <paramref name="child"/> to <paramref name="parent"/>: moves it to <paramref name="start"/> on its track if dragged in time, then
    /// links it and sets <see cref="TimelineClip.Tethered"/> (never a Game Preset piece). One undo step; refusals post the reason.
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

    /// <summary>Whether a track header is draggable: picture tracks (Video or Switch), top-level or Sub-Lane.</summary>
    public bool CanDragTrack(Layer row) => TrackOfRow(row) is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch };

    /// <summary>
    /// Whether a dragged track may move to the seam beside <paramref name="besideRow"/>'s top-level picture track (above = composited over it):
    /// always for a Sub-Lane (leaves its stack); for a top-level track only if the order changes.
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
    /// Moves a dragged track (with its picture Sub-Lanes) to the seam beside <paramref name="besideRow"/>'s track; a Sub-Lane first leaves
    /// its stack like Unlink from Parent (clearing clip links to that track). One undo step.
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
                // Reuse the canonical unlink path (UnlinkFromParent, folded into this step): leave the stack and clear clip links.
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
    /// Whether a dragged track may nest under <paramref name="parentRow"/>'s top-level picture track: both picture tracks, distinct, not
    /// already its Sub-Lane, no cycle. Tether/piece classification is unaffected (the Tethered mark decides, not lane position).
    /// </summary>
    public bool CanNestTrack(Layer row, Layer parentRow) => NestPair(row, parentRow) is not null;

    /// <summary>
    /// Nests a dragged track (with its picture Sub-Lanes) under <paramref name="parentRow"/>'s track so it folds, ripples and hides with it,
    /// keeping its original side (front stays front). Clip links and tethers unchanged. One undo step.
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

    // Resolves (track, parent) for nesting when valid (both picture, distinct, not already nested, no cycle); else null.
    private (TimelineTrackModel Track, TimelineTrackModel Parent)? NestPair(Layer row, Layer parentRow) =>
        CanDragTrack(row) && TrackOfRow(row) is { } track && TrackOfRow(parentRow) is { } parent && IsTopPicture(parent) && !ReferenceEquals(track, parent)
        && track.ParentTrackId != parent.Id && !LiesUnder(parent, track)
            ? (track, parent)
            : null;

    // Whether a track is nested under another at any depth.
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

    /// <summary>A track plus all nested picture lanes (Cutouts, nested tracks) in stack order; audio and caption lanes excluded.</summary>
    private List<TimelineTrackModel> PictureFamilyOf(TimelineTrackModel track) =>
        Document.Tracks.Where(t => ReferenceEquals(t, track) || (t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle) && LiesUnder(t, track))).ToList();

    /// <summary>
    /// New stack order with <paramref name="moving"/> placed contiguously just above or below <paramref name="beside"/>'s whole picture
    /// family (as NewTrackIndex); other tracks keep their order.
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

    // Applies the order within the edit; each move leaves earlier positions intact.
    private void Reorder(ITimelineTransaction edit, IReadOnlyList<TimelineTrackModel> order)
    {
        for (var i = 0; i < order.Count && i < Document.Tracks.Count; i++)
        {
            if (!ReferenceEquals(Document.Tracks[i], order[i]))
                edit.MoveTrack(order[i], i);
        }
    }
}
