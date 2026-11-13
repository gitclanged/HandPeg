using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- A clip onto a new track of its own (5.4A, the user's directive) -----
// A clip dragged to the seam above or below a picture track (UnifiedTimelineControl's insertion bar), moved by Alt+Up / Alt+Down, or
// by the clip menu's Move to New Track Above / Below, goes onto a new top-level picture track at that place, in one step of Undo. Its
// family comes with it: the stream sounds and Cutouts it has on its old track's Sub-Lanes go onto lanes under its new track (the same
// lanes for several clips moved together), so they are drawn, heard and stacked with it; a lone clip's lanes, which follow it by
// themselves (TimelineTransaction.FollowTrack), are gathered beside it. A new track goes past the whole family of the track it is
// put beside (its Cutout lanes too), so nothing comes between a track and its Cutouts. Only top-level picture clips move this way:
// never a sound, a Cutout or a stream (a child), a clip on a locked track, nor one on a Sub-Lane.
public partial class MainViewModel
{
    /// <summary>A top-level picture track: a Video or Switch track that lies under no other.</summary>
    private static bool IsTopPicture(TimelineTrackModel? track) =>
        track is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, ParentTrackId: null };

    /// <summary>The track a clip would leave for a new one: its own, when it is a top-level picture clip on an unlocked top-level picture track; else null.</summary>
    private TimelineTrackModel? NewTrackSource(TimelineClip? model) =>
        model is { ParentClipId: null } && model.ContentKind != LayerKind.Audio && Document.TrackOf(model) is { IsLocked: false } track && IsTopPicture(track) ? track : null;

    /// <summary>The top-level picture tracks, bottom first: the order of the seams a new track can go into.</summary>
    private List<TimelineTrackModel> TopPictureTracks() => Document.Tracks.Where(IsTopPicture).ToList();

    /// <summary>
    /// Where a track's family lies in the stacking order: the lowest and highest index of the track and the picture lanes that lie under
    /// it (its Cutouts, at any depth; the sound lanes, kept at the end of the tracks, are not counted).
    /// </summary>
    private (int Low, int High) FamilySpan(TimelineTrackModel track)
    {
        var at = Document.Tracks.IndexOf(track);
        var (low, high) = (at, at);
        for (var i = 0; i < Document.Tracks.Count; i++)
        {
            var lane = Document.Tracks[i];
            if (lane.Kind is TimelineTrackKind.Audio or TimelineTrackKind.Subtitle || ReferenceEquals(lane, track) || lane.ParentTrackId is null)
                continue;
            if (ReferenceEquals(Services.Gpu.TimelineLowering.FamilyOf(lane, Document), track))
                (low, high) = (Math.Min(low, i), Math.Max(high, i));
        }

        return (low, high);
    }

    /// <summary>The index a new track goes in to lie just above (past its whole family) or just below (under its whole family) a track.</summary>
    private int NewTrackIndex(TimelineTrackModel beside, bool above)
    {
        var (low, high) = FamilySpan(beside);
        return above ? high + 1 : low;
    }

    /// <summary>
    /// Whether a clip can go onto a new track above or below <paramref name="besideRow"/>'s track (a drag to a seam): a top-level picture
    /// clip on an unlocked top-level picture track, beside a top-level picture track, where that changes something: a clip alone on its
    /// track goes nowhere new at either seam of its own track.
    /// </summary>
    public bool CanMoveClipToNewTrack(Layer clip, Layer besideRow, bool above, double start)
    {
        if (ClipOf(clip) is not { } model || NewTrackSource(model) is not { } source || TrackOfRow(besideRow) is not { } beside || !IsTopPicture(beside) || !double.IsFinite(start))
            return false;

        // A seam is known by how many top-level picture tracks lie under it; a lone clip's own track touches two of them.
        if (source.Clips.Count == 1)
        {
            var roots = TopPictureTracks();
            var (seam, own) = (roots.IndexOf(beside) + (above ? 1 : 0), roots.IndexOf(source));
            if (seam == own || seam == own + 1)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Moves a clip onto a new track just above or just below <paramref name="besideRow"/>'s track (past its Cutout lanes), beginning at
    /// <paramref name="start"/>, its family with it (its stream sounds and Cutouts onto lanes under the new track). A track it leaves empty
    /// goes. One step of Undo.
    /// </summary>
    public bool MoveClipToNewTrack(Layer clip, Layer besideRow, bool above, double start)
    {
        start = Math.Max(start, 0);
        if (!CanMoveClipToNewTrack(clip, besideRow, above, start) || ClipOf(clip) is not { } model || Document.TrackOf(model) is not { } source
            || TrackOfRow(besideRow) is not { } beside)
        {
            StatusText = $"{clip.Name} cannot go onto a new track there: only a video, picture or title on an unlocked track moves onto a track of its own, and a clip alone on its track is already on one.";
            return false;
        }

        var where = above ? "above" : "below";
        if (!Edit($"move {clip.Name} to a new track {where}", edit => OntoNewTrack(edit, model, source, beside.Kind == TimelineTrackKind.Video ? beside : source, NewTrackIndex(beside, above), start,
            new Dictionary<Guid, TimelineTrackModel>())))
            return false;

        StatusText = $"{clip.Name} is on a new track {where} {beside.DisplayName}, at {TimeSpan.FromSeconds(model.Start):h\\:mm\\:ss\\.ff}. Ctrl+Z puts it back.";
        return true;
    }

    /// <summary>Whether <see cref="MoveClipsToNewTrack"/> has a clip to move: one of them a top-level picture clip on an unlocked top-level picture track.</summary>
    public bool CanMoveClipsToNewTrack(IReadOnlyList<Layer> views) =>
        CopyRoots(views.Where(CanGroup)).Any(c => NewTrackSource(c) is not null);

    /// <summary>
    /// Moves clips (the selection: Alt+Up / Alt+Down, the clip menu's Move to New Track Above / Below) up or down one track, in one step of
    /// Undo, each with its family, the selection kept. The clips of one track move together onto one new track just above (past its
    /// Cutout lanes) or just below it; with <paramref name="ontoNeighbour"/> (the keys) onto the next top-level picture track that way
    /// instead, when it is unlocked, takes them and is free for them. Clips that are all their track holds would go nowhere on a new
    /// track beside their own: they go onto a new track past the next track that way (they visibly move one level), and at the top or
    /// bottom of the stack nowhere (said in the status bar).
    /// </summary>
    /// <param name="ontoNeighbour">Onto the next track that way where it is free (the keys); false always makes a new track (the menu).</param>
    public bool MoveClipsToNewTrack(IReadOnlyList<Layer> views, bool above, bool ontoNeighbour = false)
    {
        var roots = CopyRoots(views.Where(CanGroup)).Where(c => NewTrackSource(c) is not null).ToList();
        if (roots.Count == 0)
        {
            StatusText = "Select a video, picture or title on an unlocked picture track to move it onto a track of its own.";
            return false;
        }

        var where = above ? "above" : "below";
        // One track's clips together, the tracks taken from the far end of the way they go (the top first going up), so each new track
        // goes in beside a track no earlier one has moved.
        var groups = roots.GroupBy(c => Document.TrackOf(c)!).OrderBy(g => (above ? -1 : 1) * Document.Tracks.IndexOf(g.Key)).ToList();
        var (moved, edge) = (0, 0);
        var label = roots.Count == 1 ? $"move {ViewOf(roots[0])?.Name ?? "a clip"} to a track {where}" : $"move {roots.Count} clips to a track {where}";
        var onto = new List<string>();
        var done = Edit(label, edit =>
        {
            foreach (var group in groups)
            {
                var source = group.Key;
                var clips = group.OrderBy(c => c.Start).ToList();
                if (!Document.Tracks.Contains(source))
                    continue;

                var tracks = TopPictureTracks();
                var at = tracks.IndexOf(source) + (above ? 1 : -1);
                var neighbour = at >= 0 && at < tracks.Count ? tracks[at] : null;
                var lone = clips.Count == source.Clips.Count;
                var lanes = new Dictionary<Guid, TimelineTrackModel>();

                if (ontoNeighbour && neighbour is { IsLocked: false } next && clips.All(c => next.Accepts(c) && next.ClipsIntersecting(c.Start + 1e-6, c.End - 1e-6).IsEmpty))
                {
                    foreach (var clip in clips)
                    {
                        var followed = PictureLanesOf(source);
                        edit.MoveClip(clip, next, clip.Start, allowCrossfadeOverlap: false);
                        Relane(edit, clip, source, next, followed, lanes);
                    }

                    onto.Add(next.DisplayName);
                    moved += clips.Count;
                    continue;
                }

                if (lone && neighbour is null)
                {
                    edge += clips.Count;
                    continue;
                }

                // A new track beside its own, or for clips that are all their track holds, past the next track that way.
                var beside = lone ? neighbour! : source;
                var template = beside.Kind == TimelineTrackKind.Video ? beside : source;
                TimelineTrackModel? made = null;
                foreach (var clip in clips)
                {
                    if (made is null)
                    {
                        made = OntoNewTrack(edit, clip, source, template, NewTrackIndex(beside, above), clip.Start, lanes);
                        continue;
                    }

                    var followed = PictureLanesOf(source);
                    edit.MoveClip(clip, made, clip.Start, allowCrossfadeOverlap: false);
                    Relane(edit, clip, source, made, followed, lanes);
                }

                onto.Add("a new track");
                moved += clips.Count;
            }
        });

        if (!done)
            return false;
        if (moved == 0)
        {
            StatusText = roots.Count == 1
                ? $"{ViewOf(roots[0])?.Name ?? "The clip"} is alone on the {(above ? "top" : "bottom")} track already: there is no track {where} it to move to."
                : $"The clips are alone on the {(above ? "top" : "bottom")} track already: there is no track {where} them to move to.";
            return false;
        }

        var kept = roots.Select(ViewOf).OfType<Layer>().ToList();
        if (kept.Count > 1)
            SetClipSelection(kept);
        var targets = string.Join(", ", onto.Distinct());
        StatusText = (moved == 1 ? $"{kept.FirstOrDefault()?.Name ?? "The clip"} moved {where} onto {targets}" : $"{moved} clips moved {where} onto {targets}")
            + (edge > 0 ? $" ({edge} already on the {(above ? "top" : "bottom")} track stayed)" : "") + ". Ctrl+Z puts it back.";
        return true;
    }

    /// <summary>
    /// A clip moved onto a new track inserted at <paramref name="index"/> (of <paramref name="template"/>'s kind), beginning at
    /// <paramref name="start"/>, its family re-laned under it (<see cref="Relane"/>). Returns the new track.
    /// </summary>
    private TimelineTrackModel OntoNewTrack(ITimelineTransaction edit, TimelineClip clip, TimelineTrackModel source, TimelineTrackModel template, int index, double start,
        Dictionary<Guid, TimelineTrackModel> lanes)
    {
        var followed = PictureLanesOf(source);
        edit.MoveClip(clip, template, start, insertTrackAtIndex: index, allowCrossfadeOverlap: false);
        var made = Document.TrackOf(clip)!;
        Relane(edit, clip, source, made, followed, lanes);
        return made;
    }

    /// <summary>The picture lanes (Cutouts) that lie under a track now: those of them that follow a lone clip to its new track are gathered beside it.</summary>
    private List<TimelineTrackModel> PictureLanesOf(TimelineTrackModel track) =>
        Document.ChildTracksOf(track).Where(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)).ToList();

    /// <summary>
    /// After a clip moved from <paramref name="from"/> to <paramref name="to"/> (5.4A): the lanes that followed it by themselves (its old
    /// track's picture lanes, <paramref name="followed"/>, now under its new track: it was alone on its old one) are put beside its new track,
    /// those over the old track over it and those under under it; then each of its family still on a lane of the old track goes onto a lane
    /// under the new one (<see cref="LaneUnder"/>), at its time. Part of the step that moved the clip.
    /// </summary>
    private void Relane(ITimelineTransaction edit, TimelineClip clip, TimelineTrackModel from, TimelineTrackModel to, List<TimelineTrackModel> followed,
        Dictionary<Guid, TimelineTrackModel> lanes)
    {
        if (ReferenceEquals(from, to) || !Document.Tracks.Contains(from))
            return;

        // The lanes that followed: over the old track, stacked just over the new one in their order; under it, just under it. Pass 5.5A
        // review: both sides are told apart before either moves (a lane put over the new track lower down was then taken for one under).
        var (over, under) = (0, 0);
        var placed = followed.Where(l => l.ParentTrackId == to.Id && !l.IsLocked).ToList();
        var oldAt = Document.Tracks.IndexOf(from);
        var overLanes = placed.Where(l => Document.Tracks.IndexOf(l) > oldAt).ToList();
        var underLanes = placed.Where(l => Document.Tracks.IndexOf(l) < oldAt).Reverse().ToList();
        foreach (var lane in overLanes)
        {
            var parent = Document.Tracks.IndexOf(to);
            edit.MoveTrack(lane, (Document.Tracks.IndexOf(lane) < parent ? parent - 1 : parent) + ++over);
        }

        foreach (var lane in underLanes)
        {
            var parent = Document.Tracks.IndexOf(to);
            edit.MoveTrack(lane, (Document.Tracks.IndexOf(lane) < parent ? parent - 1 : parent) - under++);
        }

        // Its family still on the old track's lanes (every depth, in the order of the tracks).
        var kin = new List<(TimelineTrackModel Lane, TimelineClip Clip)>();
        var parents = new Queue<TimelineClip>([clip]);
        var seen = new HashSet<Guid> { clip.Id };
        while (parents.Count > 0)
        {
            foreach (var (lane, child) in Document.ChildrenOf(parents.Dequeue()).ToList())
            {
                if (!seen.Add(child.Id))
                    continue;
                parents.Enqueue(child);
                if (lane.ParentTrackId == from.Id && !lane.IsLocked)
                    kin.Add((lane, child));
            }
        }

        foreach (var (lane, child) in kin)
        {
            var target = LaneUnder(edit, child, lane, from, to, lanes);
            if (!ReferenceEquals(target, lane))
                edit.MoveClip(child, target, child.Start, allowCrossfadeOverlap: false);
        }
    }

    /// <summary>
    /// The lane a clip of a moved family goes on under its new parent track (5.4A; as a pasted family's, PasteLane): one already made for
    /// its old lane in this move; for a stream's sound, the new track's lane of that stream place (StreamLaneOf); for a Cutout, a lane of
    /// the new track's holding the same Cutout of another clip; else a new lane of its kind, named as its old one (a sound lane at the end
    /// with the other sounds, its stream place kept; a picture lane just over the new track's family, or just under it as its old lay).
    /// </summary>
    private TimelineTrackModel LaneUnder(ITimelineTransaction edit, TimelineClip child, TimelineTrackModel oldLane, TimelineTrackModel from, TimelineTrackModel parent,
        Dictionary<Guid, TimelineTrackModel> made)
    {
        if (made.TryGetValue(oldLane.Id, out var earlier) && earlier.ParentTrackId == parent.Id && Document.Tracks.Contains(earlier) && FreeFor(earlier, child))
            return earlier;

        if (oldLane.Kind == TimelineTrackKind.Audio)
        {
            if (oldLane.StreamOrdinal is >= 0 and var ordinal && StreamLaneOf(parent, ordinal, child.AudioStreamIndex, child.Start, child.End) is { } stream)
                return made[oldLane.Id] = stream;

            var sound = edit.InsertTrack(Document.Tracks.Count, TimelineTrackKind.Audio, oldLane.Name.Length > 0 ? oldLane.Name : null);
            sound.StreamOrdinal = oldLane.StreamOrdinal;
            edit.SetTrackParent(sound, parent.Id);
            return made[oldLane.Id] = sound;
        }

        var name = child.State?.Name;
        foreach (var lane in Document.ChildTracksOf(parent))
        {
            if (lane.Kind == oldLane.Kind && !lane.IsLocked && !string.IsNullOrEmpty(name) && lane.Clips.Any(c => c.State?.Name == name) && FreeFor(lane, child))
                return made[oldLane.Id] = lane;
        }

        var under = Document.Tracks.IndexOf(oldLane) < Document.Tracks.IndexOf(from);
        var (low, high) = FamilySpan(parent);
        var index = under ? low : high + 1;
        var picture = edit.InsertTrack(index, oldLane.Kind, oldLane.Name.Length > 0 ? oldLane.Name : null);
        edit.SetTrackParent(picture, parent.Id);
        return made[oldLane.Id] = picture;
    }
}
