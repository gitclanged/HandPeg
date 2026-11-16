using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- Move clip to a new track -----
// Seam drag (UnifiedTimelineControl insertion bar), Alt+Up/Down, or Move to New Track Above/Below puts a clip on a new top-level picture
// track in one undo step. Its family (stream audio and Cutouts on old-track Sub-Lanes) moves to lanes under the new track (shared
// for multi-clip moves); a lone clip's self-following lanes (TimelineTransaction.FollowTrack) are gathered beside it. New tracks go
// past the neighbour's whole family so nothing separates a track from its Cutouts. Only unlocked top-level picture clips qualify.
public partial class MainViewModel
{
    /// <summary>Whether a track is a top-level (unnested) Video or Switch track.</summary>
    private static bool IsTopPicture(TimelineTrackModel? track) =>
        track is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, ParentTrackId: null };

    /// <summary>Source track for a move-to-new-track (unlocked top-level picture track of a top-level picture clip); else null.</summary>
    private TimelineTrackModel? NewTrackSource(TimelineClip? model) =>
        model is { ParentClipId: null } && model.ContentKind != LayerKind.Audio && Document.TrackOf(model) is { IsLocked: false } track && IsTopPicture(track) ? track : null;

    /// <summary>Top-level picture tracks bottom-first (defines insertion seams).</summary>
    private List<TimelineTrackModel> TopPictureTracks() => Document.Tracks.Where(IsTopPicture).ToList();

    /// <summary>Min/max stack index of a track and its nested picture lanes (audio lanes, kept at the end, excluded).</summary>
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

    /// <summary>Insertion index just above or below a track's whole family.</summary>
    private int NewTrackIndex(TimelineTrackModel beside, bool above)
    {
        var (low, high) = FamilySpan(beside);
        return above ? high + 1 : low;
    }

    /// <summary>
    /// Whether a seam drag beside <paramref name="besideRow"/>'s track is valid: an eligible clip, a top-level picture neighbour, and an
    /// actual change (a lone clip at its own track's seams is a no-op).
    /// </summary>
    public bool CanMoveClipToNewTrack(Layer clip, Layer besideRow, bool above, double start)
    {
        if (ClipOf(clip) is not { } model || NewTrackSource(model) is not { } source || TrackOfRow(besideRow) is not { } beside || !IsTopPicture(beside) || !double.IsFinite(start))
            return false;

        // Seams are indexed by the count of top-level picture tracks below; a lone clip's own track touches two.
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
    /// Moves a clip (and family) to a new track above/below <paramref name="besideRow"/>'s track family at <paramref name="start"/>;
    /// an emptied source track is removed. One undo step.
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

    /// <summary>Whether <see cref="MoveClipsToNewTrack"/> has at least one eligible clip.</summary>
    public bool CanMoveClipsToNewTrack(IReadOnlyList<Layer> views) =>
        CopyRoots(views.Where(CanGroup)).Any(c => NewTrackSource(c) is not null);

    /// <summary>
    /// Moves selected clips up/down one track with their families in one undo step, keeping the selection. Per source track, clips go to
    /// one new track above/below its family, or with <paramref name="ontoNeighbour"/> onto the next unlocked, compatible, free neighbour.
    /// Clips filling their whole track move past the next track instead; at the stack edge nothing moves (status message).
    /// </summary>
    /// <param name="ontoNeighbour">Prefer the next track when free (keys); false always creates a new track (menu).</param>
    public bool MoveClipsToNewTrack(IReadOnlyList<Layer> views, bool above, bool ontoNeighbour = false)
    {
        var roots = CopyRoots(views.Where(CanGroup)).Where(c => NewTrackSource(c) is not null).ToList();
        if (roots.Count == 0)
        {
            StatusText = "Select a video, picture or title on an unlocked picture track to move it onto a track of its own.";
            return false;
        }

        var where = above ? "above" : "below";
        // Group by track and process from the far end of the move direction so each insertion targets an unmoved neighbour.
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

                // New track beside the source, or past the next track when the clips fill their source track.
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
    /// Inserts a track of <paramref name="template"/>'s kind at <paramref name="index"/>, moves the clip to <paramref name="start"/> on it and
    /// relanes its family (<see cref="Relane"/>). Returns the new track.
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

    /// <summary>Current picture lanes (Cutouts) under a track; those following a lone clip are regrouped beside its new track.</summary>
    private List<TimelineTrackModel> PictureLanesOf(TimelineTrackModel track) =>
        Document.ChildTracksOf(track).Where(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)).ToList();

    /// <summary>
    /// After moving a clip from <paramref name="from"/> to <paramref name="to"/>: repositions self-following lanes (<paramref name="followed"/>)
    /// beside the new track preserving above/below sides, then moves remaining family clips from old lanes to lanes under the new track
    /// (<see cref="LaneUnder"/>). Part of the move step.
    /// </summary>
    private void Relane(ITimelineTransaction edit, TimelineClip clip, TimelineTrackModel from, TimelineTrackModel to, List<TimelineTrackModel> followed,
        Dictionary<Guid, TimelineTrackModel> lanes)
    {
        if (ReferenceEquals(from, to) || !Document.Tracks.Contains(from))
            return;

        // Classify followed lanes as above/below before moving any (moving first misclassified lanes placed lower down).
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

        // Family clips still on old-track lanes (all depths, track order).
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
    /// Target lane under the new parent for a moved family clip (as PasteLane): a lane already mapped in this move; stream audio's
    /// ordinal lane (StreamLaneOf); a lane holding the same Cutout of another clip; else a new same-kind lane named like the old one
    /// (audio at the end with its ordinal; picture just above or below the family, matching its old side).
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
