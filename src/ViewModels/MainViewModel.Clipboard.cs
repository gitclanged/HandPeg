using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- Clip clipboard: Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+D, Ctrl+Alt+V -----
// Copies are stored like recycle-bin entries (clip, track, and family: Cutouts and stream lane audio, recursively) so paste restores
// whole families. Paste places them at the playhead with relative timing preserved, each on its source track if free (else a new track
// just above, with family lanes under it), in one undo step. Duplicate places a copy after the selection without touching the clipboard.
// Paste Attributes applies chosen look parts of the first copied clip (transform, crop, opacity, effects, keyframes, mask/edges, fades, audio).
public partial class MainViewModel
{
    /// <summary>Copied clip family as bin-style entries, with each lane's stream ordinal and name.</summary>
    private sealed record CopiedFamily(DeletedClip Base, IReadOnlyList<(DeletedClip Kept, int? StreamOrdinal, string LaneName)> Kin);

    // Clipboard contents (null = empty) and the first copied clip's look and own values for Paste Attributes.
    private List<CopiedFamily>? _copied;
    private (LayerState Look, TimelineClip Clip)? _copiedAttributes;

    /// <summary>Whether the clip clipboard is non-empty.</summary>
    public bool HasCopiedClips => _copied is { Count: > 0 };

    /// <summary>Whether attributes are available for Ctrl+Alt+V.</summary>
    public bool HasCopiedAttributes => _copiedAttributes is not null;

    /// <summary>Family roots of a selection: clips whose parent is not also selected (Cutouts travel with their clip).</summary>
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

            // Captions under a clip are Sequence-level projections of its audio; never copied as clips.
            var kin = BinnedKin(clip).Where(k => k.Kept.TrackKind != TimelineTrackKind.Subtitle).Select(k => Document.Tracks.FirstOrDefault(t => t.Id == k.Kept.TrackId) is { } lane
                ? (k.Kept, lane.StreamOrdinal, lane.Name)
                : (k.Kept, (int?)null, "")).ToList();
            families.Add(new CopiedFamily(new DeletedClip(CopyOfClip(clip), track.Id, track.Kind, Document.Tracks.IndexOf(track)) { TrackParentId = track.ParentTrackId }, kin));
        }

        return families;
    }

    /// <summary>Ctrl+C: copies clips with Cutouts and stream audio for Ctrl+V, and the first one's look for Ctrl+Alt+V.</summary>
    /// <returns>Number of root clips copied (families excluded).</returns>
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

    /// <summary>Ctrl+V: pastes with the earliest clip starting at the given time (playhead). One undo step.</summary>
    public bool PasteClips(double at)
    {
        if (_copied is not { Count: > 0 } copied)
        {
            StatusText = "Nothing copied yet: select clips on the Timeline and press Ctrl+C first.";
            return false;
        }

        return PlaceFamilies(copied, Math.Max(at, 0), $"paste {(copied.Count == 1 ? copied[0].Base.Clip.State.Name : $"{copied.Count} clips")}");
    }

    /// <summary>Ctrl+D: duplicates clips right after the last one ends, on the same tracks where free; clipboard untouched. One undo step.</summary>
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
    /// Places copied families from a time with fresh ids (children remapped to copied parents). Roots go on their source track if it
    /// exists, is unlocked and free, else a new same-kind track just above; family clips go on their source lane if under the root's
    /// track and free, else a same-kind lane there (streams by ordinal), created if needed. One undo step; selects the pasted clips.
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
                    // Clone the clipboard entries (reusable for the next paste) with new ids and shifted times.
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

                    // An orphan-copied Cutout keeps its parent only if it lands within the parent's span; otherwise it becomes independent.
                    var (root, rootFrom, _, _) = clips[0];
                    // The Tethered mark follows the parent link: kept or cleared together.
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

    // Whether a track is empty over a range (seconds).
    private static bool FreeFor(TimelineTrackModel track, TimelineClip clip) => track.ClipsIntersecting(clip.Start + 1e-6, clip.End - 1e-6).IsEmpty;

    /// <summary>Target track for a pasted root: source track if present, unlocked and free, else one new same-kind track above per source.</summary>
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
    /// Target lane for a pasted family clip under its root's track: source lane if under that track and free; a lane already created in
    /// this paste; stream lane by ordinal; else a new same-kind lane (above or below, matching its source) with ordinal and name.
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

        // Cutout lanes go above the track (composited over it) unless the source lay below; audio lanes go at the end.
        var at = Document.Tracks.IndexOf(parent);
        var under = own is not null && Document.Tracks.FirstOrDefault(t => t.Id == own.ParentTrackId) is { } ownParent && Document.Tracks.IndexOf(own) < Document.Tracks.IndexOf(ownParent);
        var index = kind == TimelineTrackKind.Audio ? Document.Tracks.Count : under ? at : at + 1;
        var lane = edit.InsertTrack(index, kind == TimelineTrackKind.Subtitle ? TimelineTrackKind.Video : kind, name.Length > 0 ? name : null);
        lane.StreamOrdinal = kind == TimelineTrackKind.Audio ? ordinal : null;
        edit.SetTrackParent(lane, parent.Id);
        return made[(from.TrackId, parent.Id)] = lane;
    }

    // ----- Paste Attributes (Ctrl+Alt+V) -----

    /// <summary>Paste Attributes parts (one dialog checkbox each).</summary>
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

    /// <summary>Name of the attribute source clip for Ctrl+Alt+V; null when none.</summary>
    public string? CopiedAttributesName => _copiedAttributes?.Look.Name;

    /// <summary>Ctrl+Alt+V: applies chosen look parts and own values (fades, gain) of the copied clip; the rest is untouched. One undo step.</summary>
    /// <returns>Number of clips updated.</returns>
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
                using (OwnLookScope())
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

    // Applies chosen look parts to one clip, each via the same path as that part's own controls.
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
