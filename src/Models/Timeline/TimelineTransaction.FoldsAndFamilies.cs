using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

// ----- Timeline transaction: folds and parent/child families -----
internal sealed partial class TimelineTransaction
{
    // ----- Folds: fold, unfold, resize -----

    /// <summary>
    /// Folds [<paramref name="from"/>, <paramref name="to"/>] at <paramref name="seamTrack"/> (<see cref="ITimelineTransaction.FoldStretch"/>),
    /// reusing <paramref name="id"/> when re-folding and restricting to <paramref name="onTracks"/> when given (the tracks it was unfolded on).
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

        // Split each affected track at both ends (clips too close to an end to split go whole), store pieces relative to the range
        // start, then remove them. Tracks: ripple tracks, their child tracks (caption lane included) and tracks of family members.
        // All tracks are split before removal so child pieces re-parent to the parent's piece regardless of track order.
        var tracks = onTracks ?? FoldTracks(RippleTracks(seamTrack, rippleAll), start, end);

        // Find covered RestoredFolds before clips move; they (and any with this fold's id) are unmarked.
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
    /// <see cref="TimelineDocument.RestoredFolds"/> on <paramref name="tracks"/> overlapping [<paramref name="start"/>, <paramref name="end"/>]; null when none.
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
        // Resolve by id so folds from older document copies (undo, views) still match.
        var fold = _doc.Folds?.Find(f => f.Id == given.Id) ?? throw new TimelineEditException("That fold is not on this timeline.");
        if (fold.SeamIn(_doc) is not { } at || _doc.FindClip(fold.SeamClipId) is not { } seam)
            throw new TimelineEditException("The cut the stretch was folded at is gone.");

        // Make room at the seam on its fold tracks (current state, children included) and destination tracks, splitting crossing clips.
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

        // Reinsert clips on their tracks (deleted track: first unlocked of that kind). Re-id copies whose id is now taken and remap
        // their children's parent links.
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

        // Record a RestoredFold with this id: from the restored content's start on the seam track (or the preceding clip's end) to the
        // seam clip's start, or for an end seam, to the restored content's end.
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
    /// Tracks a fold restores onto (and re-folds from when resized): current ripple set at the seam track excluding top-level subtitle
    /// tracks, plus unlocked destination tracks of its clips and children.
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

    /// <summary>Clip whose start (or end when !<paramref name="start"/>) is at <paramref name="seconds"/>; null when none.</summary>
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

        // Re-fold with the original id (Fold removes the RestoredFold record).
        return Fold(track, span.From, span.To, restored.AllTracks, restored.Id)
               ?? throw new TimelineEditException("Nothing lies there to fold.");
    }

    /// <summary>
    /// <see cref="ITimelineTransaction.ResizeFold"/>: unfold fully (UnfoldSegment tracks), then re-fold the adjusted range on the same tracks
    /// with the same id (tail reveal, head reveal, or extended into the edge clip), then rejoin revealed pieces (<see cref="JoinShown"/>).
    /// Atomic: any refusal rolls back.
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

        // All fold tracks must exist and be unlocked: partial reveals return to their own track only (only full unfolds may substitute).
        foreach (var kept in fold.Clips)
        {
            if (TrackById(kept.TrackId) is not { IsLocked: false })
                throw new TimelineEditException("A track the stretch was folded out of is locked or gone: unlock it, or put the stretch back whole.");
        }

        var (minimum, length, reveal, tail) = (MinimumLength(false), fold.Length, seconds > 0, side == FoldSide.Tail);
        double amount;
        if (reveal)
        {
            // Enforce the minimum clip length on both sides; leaving less than that folded reveals everything.
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

        // Split at the new edge first (FoldStretch would otherwise take the whole seam clip). When folding more at the tail, the part
        // after the cut keeps the clip's id and children.
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

        // Empty range: remain fully unfolded.
        if (Fold(seamTrack, from, to, fold.AllTracks, fold.Id, tracks) is not { } refolded)
            return null;
        if (!reveal)
            return refolded;

        // Rejoin revealed pieces (tail into the clip after the seam, head into the clip before); a joined seam piece becomes that clip's edge.
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
    /// Swaps split ids so the second half keeps the original id (tail growth folds the head away; the remainder is still that clip with its
    /// children); parent links follow.
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
    /// After a partial reveal (<see cref="Resize"/>), merges each revealed piece in [<paramref name="shownFrom"/>, <paramref name="shownTo"/>]
    /// abutting a clip at <paramref name="junction"/> into it when <see cref="Joinable"/> (clip keeps its id; <paramref name="tail"/> selects the
    /// clip after vs before the seam). Children are re-parented so they can join in later passes (seam track first). Pieces anchoring another
    /// fold's seam or a RestoredFold edge stay separate. Returns (joined piece id, absorbing clip id) pairs.
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

    /// <summary>Re-parents children of <paramref name="from"/> to <paramref name="to"/> (piece merged into a clip).</summary>
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
    /// True when two abutting clips are halves of one split: same kind, file, streams and speed with contiguous source, same switches,
    /// audio, parent and tether flag, and nothing at the cut (fade, crossfade, J/L). Looks are compared by <see cref="JoinLook"/>.
    /// </summary>
    private static bool Joinable(TimelineClip a, TimelineClip b) =>
        Meet(a, b) && a.ContentKind == b.ContentKind && string.Equals(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase)
        && a.VideoStreamIndex == b.VideoStreamIndex && a.AudioStreamIndex == b.AudioStreamIndex && a.Speed == b.Speed
        && a.SourceDuration == b.SourceDuration && a.AudioLinked == b.AudioLinked && a.AudioSlipSeconds == b.AudioSlipSeconds
        && a.GainDb == b.GainDb && a.IsHidden == b.IsHidden && a.IsMuted == b.IsMuted && a.ProxyChoice == b.ProxyChoice
        && a.ParentClipId == b.ParentClipId && a.Tethered == b.Tethered && a.FadeOutSeconds == 0 && b.FadeInSeconds == 0 && b.TransitionIn == 0
        && a.AudioTailOutSeconds == 0 && b.AudioLeadInSeconds == 0
        && (!a.PlaysFile || Math.Abs(b.MediaOffset - a.MediaEnd) <= TimelineDocument.FileSlack(a));

    // ----- Families: locating children that travel with a clip -----

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

    /// <summary>True when <paramref name="track"/> descends from <paramref name="ancestor"/> (cycles traversed once).</summary>
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
    /// Children of <paramref name="parent"/> overlapping (by more than the slack) [<paramref name="from"/>, <paramref name="to"/>] on any track,
    /// with tracks; null when none. Two binary searches per track; allocates only on a hit.
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
    /// Transitive overlapping descendants on unlocked tracks (locked tracks are skipped like ripples), each once; null when none.
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
    /// Ripple-trim riders: family members ending at the clip end or within [<paramref name="lostFrom"/>, end], plus with <paramref name="head"/>
    /// those starting at the clip start. They move with the end or are removed (<see cref="Ride"/>), so none blocks the ripple. Null when none.
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
    /// Applies a ripple trim to <see cref="Riders"/> after the end moved from <paramref name="oldEnd"/> by <paramref name="by"/> to
    /// <paramref name="newEnd"/> (<paramref name="whole"/> = frame-aligned). Head riders (<paramref name="head"/>) are head-trimmed likewise;
    /// end riders extend (bounded by file and neighbour) or contract; riders inside the released range are cut. Riders below minimum length
    /// are removed on <paramref name="rippled"/> tracks, else unlinked in place; riders whose head cannot grow stay. Runs before a shrinking
    /// ripple and after a growing one.
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
    /// Handles a rider shortened below minimum: removed on rippled tracks (the gap closes), otherwise unlinked in place.
    /// </summary>
    private void LeaveOrLift(TimelineTrackModel track, TimelineClip rider, List<TimelineTrackModel> rippled)
    {
        if (rippled.Contains(track))
            Lift(rider, reconcile: false, family: false);
        else
            Unlink(rider);
    }

    /// <summary>
    /// Unlinks a child left wholly outside its trimmed parent; it stays in place with its own children.
    /// </summary>
    private void Unlink(TimelineClip child)
    {
        if (child.ParentClipId is null)
            return;
        Remember(child);
        (child.ParentClipId, child.Tethered) = (null, false);
    }

    /// <summary>
    /// After a non-ripple head trim (or roll): children starting at the old start follow as far as possible; children wholly before the
    /// new start are unlinked in place.
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
    /// After a non-ripple tail trim (or roll): children ending at the old end follow as far as possible; children wholly after the new
    /// end are unlinked in place.
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

    /// <summary>Lifts a moving clip's family off their tracks, remembering each one's abutting neighbour and crossfade for landing.</summary>
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
    /// Lands the family shifted by <paramref name="delta"/> on their own tracks; any collision refuses the whole move. Crossfades survive
    /// when landing against the same neighbour.
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
    /// After a cross-track move: re-parents a child track of the old track to the new one when all its clips' parents now live there
    /// (mini-stacks follow their clips).
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
    /// Fold tracks: <paramref name="rippled"/> minus top-level subtitle tracks (a mini-stack's caption lane folds with it), then unlocked
    /// tracks of descendants of clips in the range, in that order.
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

    /// <summary>Ripple tracks for a move: the clip's track, its unlocked child tracks, and its family's tracks.</summary>
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

    /// <summary>Adds unlocked descendant tracks of <paramref name="tracks"/> (mini-stacks ripple with their parent).</summary>
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
}

// ----- Gap close at a time and media swap in place (Pass 5.6.5: timeline double-click and Alt+drop) -----
internal sealed partial class TimelineTransaction
{
    public double CloseGapAt(TimelineTrackModel track, double sequenceTime, bool rippleAllUnlockedTracks) => Step(() =>
        _doc.GapAround(track, sequenceTime) is { } gap ? CloseUp(track, gap.Start, gap.End, rippleAllUnlockedTracks) : 0);

    public void ReplaceClipSource(TimelineClip clip, string path, double sourceDuration, double pictureDuration, int videoStreams, int audioStreams) => Step(() =>
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new TimelineEditException("There is no file to swap in.");

        // The family is found before the clip changes file: its children follow the clip's old path.
        List<TimelineClip> family = [clip, .. _doc.Tracks.SelectMany(t => t.Clips)
            .Where(c => c.ParentClipId == clip.Id && c.PlaysFile && string.Equals(c.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase))];
        var whole = double.IsFinite(sourceDuration) ? Math.Max(sourceDuration, 0) : 0;
        var picture = double.IsFinite(pictureDuration) && pictureDuration > 0 && pictureDuration < whole ? pictureDuration : 0;

        // Every member's streams in the new file, before anything changes: a refusal leaves the whole family as it was.
        var streams = family.ConvertAll(piece => SwapStreams(piece, ReferenceEquals(piece, clip), videoStreams, audioStreams));
        var changed = false;
        List<(TimelineClip Clip, double OldEnd)>? shortened = null;
        for (var i = 0; i < family.Count; i++)
        {
            var (piece, (video, audio), oldEnd) = (family[i], streams[i], family[i].End);
            changed |= Change(piece, c =>
            {
                (c.SourcePath, c.SourceDuration, c.PictureDuration, c.BeatTimes) = (path, whole, picture, null);
                (c.VideoStreamIndex, c.AudioStreamIndex) = (video, audio);
                if (!c.PlaysFile || c.WholeDuration <= 0)
                    return;

                // Too short for the clip's source span: the in-point moves back first, then the end comes in (never below the minimum).
                var span = c.Duration * c.Speed;
                if (c.MediaOffset + span > c.WholeDuration)
                    c.MediaOffset = Math.Max(c.WholeDuration - span, 0);
                if (c.MediaOffset + span > c.WholeDuration)
                    c.End = c.Start + Math.Max((c.WholeDuration - c.MediaOffset) / c.Speed, TimelineDocument.MinimumClipSeconds);

                // The J/L cut re-clamped to the new file and the final span (SplitSound's bounds, on frames): the lead to time 0 and the
                // in-point, the tail to the file's end; sound left shorter than the shortest clip falls back to the picture's span.
                if (c.HasSound && c.AudioLinked)
                {
                    var leadMost = Math.Min(c.Start, c.MediaOffset / c.Speed);
                    if (c.AudioLeadInSeconds > leadMost + Slack)
                        c.AudioLeadInSeconds = Math.Max(Down(leadMost, false), 0);
                    var tailMost = Math.Max((c.SourceDuration - c.MediaEnd) / c.Speed, 0);
                    if (c.AudioTailOutSeconds > tailMost + Slack)
                        c.AudioTailOutSeconds = Math.Max(Down(tailMost, false), 0);
                    if (c.Duration + c.AudioLeadInSeconds + c.AudioTailOutSeconds < MinimumLength(false) - Slack)
                        (c.AudioLeadInSeconds, c.AudioTailOutSeconds) = (Math.Max(c.AudioLeadInSeconds, 0), Math.Max(c.AudioTailOutSeconds, 0));
                }
            });

            if (piece.End < oldEnd)
                (shortened ??= []).Add((piece, oldEnd));
        }

        // Once every span is final: a shortened clip's crossfades that no longer meet go, and its children ending with it follow it in.
        if (shortened is not null)
        {
            foreach (var track in shortened.Select(s => TrackOf(s.Clip)).Distinct())
                Reconcile(track);
            foreach (var (piece, oldEnd) in shortened)
                FollowEnd(piece, oldEnd);
        }

        return changed;
    });

    /// <summary>
    /// A swap member's (video, audio) stream picks in a new file of <paramref name="videoStreams"/> picture and
    /// <paramref name="audioStreams"/> sound streams (probed). A picture member needs a picture and a sound member sound; a pick the file
    /// lacks falls back to its first stream (a picture's sound to none in a silent file), but a child playing one sound stream (a stream
    /// lane) is refused rather than doubled onto another stream.
    /// </summary>
    private static (int Video, int Audio) SwapStreams(TimelineClip piece, bool swapped, int videoStreams, int audioStreams)
    {
        var sound = piece.ContentKind == LayerKind.Audio;
        if (sound ? audioStreams <= 0 : videoStreams <= 0)
            throw new TimelineEditException(sound ? "The new file has no sound to swap in." : "The new file has no picture to swap in.");

        var video = sound || piece.VideoStreamIndex < videoStreams ? piece.VideoStreamIndex : 0;
        var audio = piece.AudioStreamIndex;
        if (audio >= audioStreams)
        {
            if (sound && !swapped)
                throw new TimelineEditException($"The new file has {audioStreams} sound stream{(audioStreams == 1 ? "" : "s")}, but a stream lane of the clip plays stream {audio + 1}.");
            audio = audioStreams > 0 ? 0 : -1;
        }

        return (video, audio);
    }
}
