using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

// ----- The timeline transaction: folds and children (split from TimelineTransaction.cs, 5.4.1-M5) -----
internal sealed partial class TimelineTransaction
{
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
}
