using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- The clip clipboard (5.2A, the user's directive): Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+D and Ctrl+Alt+V -----
// Copied clips are kept as the recycle bin keeps a deleted one (a copy of each, its track, and what goes with it: its Cutouts, its stream
// lanes' sounds, theirs), so a paste puts whole families down. Paste lays them at the playhead, as far apart in time as they were, each on
// the track it came from where that is free (a new track just above it where it is not, its family's lanes under that new track), one
// step of Undo; Duplicate lays a copy right after the selection without touching the clipboard. Paste Attributes gives the selected clips
// the chosen parts of the first copied clip's look (its transform, crop, opacity, effects, keyframes, mask and edges, fades, sound).
public partial class MainViewModel
{
    /// <summary>A copied clip with its family: each kept as the bin keeps a deleted one, and each lane's stream place and name.</summary>
    private sealed record CopiedFamily(DeletedClip Base, IReadOnlyList<(DeletedClip Kept, int? StreamOrdinal, string LaneName)> Kin);

    // What Ctrl+C or Ctrl+X kept (null: nothing yet), and the first copied clip's look and its own values, for Paste Attributes.
    private List<CopiedFamily>? _copied;
    private (LayerState Look, TimelineClip Clip)? _copiedAttributes;

    /// <summary>Whether Ctrl+V has anything to paste.</summary>
    public bool HasCopiedClips => _copied is { Count: > 0 };

    /// <summary>Whether Ctrl+Alt+V has a clip's attributes to paste.</summary>
    public bool HasCopiedAttributes => _copiedAttributes is not null;

    /// <summary>The clips of a selection that are copied whole: those whose parent is not copied too (a Cutout goes with its clip).</summary>
    private List<TimelineClip> CopyRoots(IEnumerable<Layer> views)
    {
        var clips = views.Select(ClipOf).OfType<TimelineClip>().Distinct().ToList();
        var ids = clips.Select(c => c.Id).ToHashSet();
        return clips.Where(c => c.ParentClipId is not { } parent || !ids.Contains(parent)).OrderBy(c => c.Start).ToList();
    }

    private List<CopiedFamily> Families(IEnumerable<TimelineClip> roots)
    {
        FlushLooks();
        var families = new List<CopiedFamily>();
        foreach (var clip in roots)
        {
            if (Document.TrackOf(clip) is not { } track)
                continue;

            // The captions under a clip are the sequence's, projected from the sound they were heard in: never pasted as clips.
            var kin = BinnedKin(clip).Where(k => k.Kept.TrackKind != TimelineTrackKind.Subtitle).Select(k => Document.Tracks.FirstOrDefault(t => t.Id == k.Kept.TrackId) is { } lane
                ? (k.Kept, lane.StreamOrdinal, lane.Name)
                : (k.Kept, (int?)null, "")).ToList();
            families.Add(new CopiedFamily(new DeletedClip(CopyOfClip(clip), track.Id, track.Kind, Document.Tracks.IndexOf(track)) { TrackParentId = track.ParentTrackId }, kin));
        }

        return families;
    }

    /// <summary>Ctrl+C: keeps copies of these clips (with their Cutouts and stream sounds) for Ctrl+V, and the first one's look for Ctrl+Alt+V.</summary>
    /// <returns>How many clips were copied (their families not counted).</returns>
    public int CopyClips(IReadOnlyList<Layer> views)
    {
        var roots = CopyRoots(views.Where(CanGroup));
        if (roots.Count == 0)
        {
            StatusText = "Select a clip on the Timeline (or several) to copy.";
            return 0;
        }

        _copied = Families(roots);
        var first = views.Select(ClipOf).OfType<TimelineClip>().FirstOrDefault() ?? roots[0];
        _copiedAttributes = (TimelineTransaction.CopyOf(first.State), CopyOfClip(first));
        StatusText = roots.Count == 1
            ? $"Copied {first.State.Name}: Ctrl+V pastes it at the playhead, Ctrl+Alt+V pastes its attributes onto the selected clips."
            : $"Copied {roots.Count} clips: Ctrl+V pastes them at the playhead, as far apart as they are.";
        return roots.Count;
    }

    /// <summary>Ctrl+V: the copied clips at a moment of the Sequence (the playhead), the first one starting there. One step of Undo.</summary>
    public bool PasteClips(double at)
    {
        if (_copied is not { Count: > 0 } copied)
        {
            StatusText = "Nothing copied yet: select clips on the Timeline and press Ctrl+C first.";
            return false;
        }

        return PlaceFamilies(copied, Math.Max(at, 0), $"paste {(copied.Count == 1 ? copied[0].Base.Clip.State.Name : $"{copied.Count} clips")}");
    }

    /// <summary>Ctrl+D: a copy of these clips right after the last of them ends, on the same tracks where they are free. The clipboard is left as it is. One step of Undo.</summary>
    public bool DuplicateClips(IReadOnlyList<Layer> views)
    {
        var roots = CopyRoots(views.Where(CanGroup));
        if (roots.Count == 0)
        {
            StatusText = "Select a clip on the Timeline (or several) to duplicate.";
            return false;
        }

        return PlaceFamilies(Families(roots), roots.Max(c => c.End), $"duplicate {(roots.Count == 1 ? roots[0].State.Name : $"{roots.Count} clips")}");
    }

    /// <summary>
    /// Lays copied families down from a moment: new ids for every clip (a child naming its copied parent), each base clip on its own track
    /// where that is still there, unlocked and free for it, otherwise on a new track of its kind just above; each of its family on the lane it
    /// came from when that lies under the base clip's track and is free, otherwise on a lane of its kind under that track (a stream's lane by
    /// its stream place), made when there is none. One step of Undo; the pasted clips are selected.
    /// </summary>
    private bool PlaceFamilies(IReadOnlyList<CopiedFamily> families, double at, string label)
    {
        var shift = at - families.Min(f => f.Base.Clip.Start);
        var placed = new List<Guid>();
        var made = new Dictionary<(Guid OldLane, Guid Parent), TimelineTrackModel>();
        var newTracks = new Dictionary<Guid, TimelineTrackModel>();
        if (!Edit(label, edit =>
            {
                foreach (var family in families)
                {
                    // Copies of the copies (the clipboard stays as it is for the next paste), new ids, moved in time.
                    var renamed = new Dictionary<Guid, Guid>();
                    var clips = new List<(TimelineClip Clip, DeletedClip From, int? Ordinal, string Lane)> { (CopyOfClip(family.Base.Clip), family.Base, null, "") };
                    clips.AddRange(family.Kin.Select(k => (CopyOfClip(k.Kept.Clip), k.Kept, k.StreamOrdinal, k.LaneName)));
                    foreach (var (clip, _, _, _) in clips)
                    {
                        renamed[clip.Id] = Guid.NewGuid();
                        clip.Id = renamed[clip.Id];
                        (clip.Start, clip.End) = (clip.Start + shift, clip.End + shift);
                    }

                    foreach (var (clip, _, _, _) in clips)
                    {
                        if (clip.ParentClipId is { } parent && renamed.TryGetValue(parent, out var now))
                            clip.ParentClipId = now;
                    }

                    // A Cutout copied without its clip stays its clip's only where it lands within that clip's span; elsewhere it is a clip of its own.
                    var (root, rootFrom, _, _) = clips[0];
                    // Pass 5.5A: a tethered clip keeps its mark where it keeps its link, and loses both together.
                    if (root.ParentClipId is { } over && !(Document.FindClip(over) is { } parentClip && root.Start >= parentClip.Start - 1e-6 && root.End <= parentClip.End + 1e-6))
                        (root.ParentClipId, root.Tethered) = (null, false);
                    var track = PasteTrack(edit, root, rootFrom, newTracks);
                    edit.PlaceClip(track, root, allowCrossfadeOverlap: false);
                    placed.Add(root.Id);
                    foreach (var (clip, from, ordinal, lane) in clips.Skip(1))
                        edit.PlaceClip(PasteLane(edit, clip, from, ordinal, lane, track, made), clip, allowCrossfadeOverlap: false);
                }
            }))
        {
            return false;
        }

        var views = placed.Select(id => Document.FindClip(id)).OfType<TimelineClip>().Select(ViewOf).OfType<Layer>().ToList();
        if (views.Count > 0)
            SetClipSelection(views);
        StatusText = $"{char.ToUpperInvariant(label[0])}{label[1..]}: at {TimeDisplay.Format(at)}{(newTracks.Count > 0 ? ", on a new track where its own was taken" : "")}. Ctrl+Z takes it back.";
        return true;
    }

    // Whether a track has nothing in a stretch (seconds), so a clip can be laid there.
    private static bool FreeFor(TimelineTrackModel track, TimelineClip clip) => track.ClipsIntersecting(clip.Start + 1e-6, clip.End - 1e-6).IsEmpty;

    /// <summary>The track a pasted base clip goes on: its own when that is there, unlocked and free, else a new one of its kind just above it (one per old track).</summary>
    private TimelineTrackModel PasteTrack(ITimelineTransaction edit, TimelineClip clip, DeletedClip from, Dictionary<Guid, TimelineTrackModel> newTracks)
    {
        var own = Document.Tracks.FirstOrDefault(t => t.Id == from.TrackId);
        if (own is { IsLocked: false } && own.Accepts(clip) && FreeFor(own, clip))
            return own;
        if (newTracks.TryGetValue(from.TrackId, out var earlier) && FreeFor(earlier, clip))
            return earlier;

        var kind = TimelineTrackModel.KindFor(clip.ContentKind);
        var index = own is not null ? Document.Tracks.IndexOf(own) + 1 : kind == TimelineTrackKind.Audio ? Document.Tracks.Count : TopPictureIndex();
        var made = edit.InsertTrack(Math.Clamp(index, 0, Document.Tracks.Count), kind == TimelineTrackKind.Subtitle ? TimelineTrackKind.Video : kind);
        if (own?.ParentTrackId is { } parent && Document.Tracks.Any(t => t.Id == parent))
            edit.SetTrackParent(made, parent);
        newTracks[from.TrackId] = made;
        return made;
    }

    /// <summary>
    /// The lane a pasted clip of a family goes on, under its base clip's track: the lane it came from when that lies under that track and is
    /// free; a lane already made for it in this paste; a stream's lane by its stream place; else a new lane of its kind (under the track, or
    /// above it, as its own lay), given its stream place and name.
    /// </summary>
    private TimelineTrackModel PasteLane(ITimelineTransaction edit, TimelineClip clip, DeletedClip from, int? ordinal, string name, TimelineTrackModel parent,
        Dictionary<(Guid OldLane, Guid Parent), TimelineTrackModel> made)
    {
        var own = Document.Tracks.FirstOrDefault(t => t.Id == from.TrackId);
        if (own is { IsLocked: false } && own.ParentTrackId == parent.Id && own.Accepts(clip) && FreeFor(own, clip))
            return own;
        if (made.TryGetValue((from.TrackId, parent.Id), out var earlier) && FreeFor(earlier, clip))
            return earlier;

        var kind = TimelineTrackModel.KindFor(clip.ContentKind);
        if (kind == TimelineTrackKind.Audio && ordinal is >= 0 and var place && StreamLaneOf(parent, place, clip.AudioStreamIndex, clip.Start, clip.End) is { } stream)
            return made[(from.TrackId, parent.Id)] = stream;

        // A Cutout's lane above its clip's track (drawn over it), a lane of sound at the end with the other sounds; under it when its own lay under its parent.
        var at = Document.Tracks.IndexOf(parent);
        var under = own is not null && Document.Tracks.FirstOrDefault(t => t.Id == own.ParentTrackId) is { } ownParent && Document.Tracks.IndexOf(own) < Document.Tracks.IndexOf(ownParent);
        var index = kind == TimelineTrackKind.Audio ? Document.Tracks.Count : under ? at : at + 1;
        var lane = edit.InsertTrack(index, kind == TimelineTrackKind.Subtitle ? TimelineTrackKind.Video : kind, name.Length > 0 ? name : null);
        lane.StreamOrdinal = kind == TimelineTrackKind.Audio ? ordinal : null;
        edit.SetTrackParent(lane, parent.Id);
        return made[(from.TrackId, parent.Id)] = lane;
    }

    // ----- Paste Attributes (Ctrl+Alt+V) -----

    /// <summary>What Paste Attributes can give a clip of the copied one's (each a box in its dialog).</summary>
    [Flags]
    public enum ClipAttributes
    {
        None = 0,
        Transform = 1,
        Crop = 2,
        Opacity = 4,
        Effects = 8,
        Keyframes = 16,
        MaskAndEdges = 32,
        Fades = 64,
        Sound = 128,
    }

    /// <summary>The name of the clip whose attributes Ctrl+Alt+V pastes; null for none copied.</summary>
    public string? CopiedAttributesName => _copiedAttributes?.Look.Name;

    /// <summary>
    /// Ctrl+Alt+V: gives each of these clips the chosen parts of the copied clip's look and its own values (its fades, its gain), and leaves the
    /// rest of each as it is. One step of Undo.
    /// </summary>
    /// <returns>How many clips took them.</returns>
    public int PasteAttributes(IReadOnlyList<Layer> views, ClipAttributes parts)
    {
        if (_copiedAttributes is not { } copied || parts == ClipAttributes.None)
            return 0;

        var targets = views.Where(v => CanGroup(v) && ClipOf(v) is not null).Distinct().ToList();
        if (targets.Count == 0)
        {
            StatusText = "Select the clips to paste attributes onto first.";
            return 0;
        }

        var (look, own) = copied;
        var source = Layer.FromState(look, look.ImageWidth, look.ImageHeight, Math.Max(FrameWidth, 2), Math.Max(FrameHeight, 2));
        FlushLooks();
        var label = $"paste the attributes of {look.Name} onto {(targets.Count == 1 ? targets[0].Name : $"{targets.Count} clips")}";
        var before = CaptureSnapshot();
        using (HoldLookSteps())
        using (DeferCommand())
        {
            foreach (var view in targets)
            {
                using (Layer.Echo())
                    PasteAttributesOn(view, source, parts);
                _staleLooks.Add(view);
            }

            if ((parts & (ClipAttributes.Fades | ClipAttributes.Sound)) != 0)
            {
                Edit(label, edit =>
                {
                    foreach (var clip in targets.Select(ClipOf).OfType<TimelineClip>())
                    {
                        if ((parts & ClipAttributes.Fades) != 0)
                            edit.SetClipFades(clip, own.FadeInSeconds, own.FadeOutSeconds, own.FadeInStyle, own.FadeOutStyle);
                        if ((parts & ClipAttributes.Sound) != 0 && clip.HasSound && own.HasSound)
                            edit.SetClipGain(clip, own.GainDb);
                    }
                }, undoable: false);
            }
        }

        Checkpoint(label, before);
        ApplyKeyframes();
        StatusText = $"Pasted the attributes of {look.Name} onto {(targets.Count == 1 ? targets[0].Name : $"{targets.Count} clips")}. Ctrl+Z takes them back.";
        return targets.Count;
    }

    // One clip given the chosen parts of another's look (Paste Attributes), each as that part's own controls set it.
    private static void PasteAttributesOn(Layer view, Layer source, ClipAttributes parts)
    {
        if ((parts & ClipAttributes.Transform) != 0 && view.HasPlacement)
        {
            view.LockAspectRatio = source.LockAspectRatio;
            view.FromCorner(() =>
            {
                (view.SizeWidth, view.SizeHeight) = (source.SizeWidth, source.SizeHeight);
                (view.PositionX, view.PositionY) = (source.PositionX, source.PositionY);
            });
            if (view.HasFilters)
                (view.Rotation, view.TiltX, view.TiltY) = (source.Rotation, source.TiltX, source.TiltY);
            (view.AnchorX, view.AnchorY) = (source.AnchorX, source.AnchorY);
        }

        if ((parts & ClipAttributes.Crop) != 0 && view.HasPlacement)
            (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom) = (source.CropLeft, source.CropTop, source.CropRight, source.CropBottom);

        if ((parts & ClipAttributes.Opacity) != 0 && view.IsPicture)
            view.Opacity = source.Opacity;

        if ((parts & ClipAttributes.Effects) != 0 && view.HasFilters)
            view.SetEffects([.. source.Effects.Select(e => view.AdoptEffect(e.ToState()))], source.EffectsBypassed);

        if ((parts & ClipAttributes.Keyframes) != 0)
        {
            foreach (var property in Enum.GetValues<KeyProperty>())
                view.SetKeys(property, source.GetKeys(property) is { Count: > 0 } keys ? [.. keys] : null);
        }

        if ((parts & ClipAttributes.MaskAndEdges) != 0 && view.HasMask)
        {
            (view.CustomMask, view.MaskPath, view.MaskFrame, view.MaskInvert, view.MaskShapes) = (source.CustomMask, source.MaskPath, source.MaskFrame, source.MaskInvert, source.MaskShapes);
            (view.MaskGrow, view.MaskOffsetX, view.MaskOffsetY) = (source.MaskGrow, source.MaskOffsetX, source.MaskOffsetY);
            (view.CornerRadius, view.Feather, view.FeatherRadius) = (source.CornerRadius, source.Feather, source.FeatherRadius);
        }

        if ((parts & ClipAttributes.Sound) != 0 && view.CarriesSound && source.CarriesSound)
            view.AudioFilters = source.AudioFilters.Clone();
    }
}
