using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- Links, places and blur fills of clips, for the timeline's rows and menus (4.4) -----
// A track can be a child of another (its mini-stack: a style's HUD cut-outs under the main video's track), and a clip a child
// of the clip it lies over; edits of the parent carry its children along (TimelineTransaction). A clip's place, crop and
// blur fill are its own (Layer.IsClipProperty): these give one clip's place to its track, fit a clip to the frame, and set
// its blur fill, each as one step of Undo.
public partial class MainViewModel
{
    /// <summary>The document track's id behind a row (a track's view: its first clip's, or an empty track's); null for the stand-ins.</summary>
    public Guid? TrackIdOf(Layer row) => TrackOfRow(row)?.Id;

    /// <summary>The row (the view standing for a track) a track's row is a child of, or null when its track is no track's child.</summary>
    public Layer? ParentRowOf(Layer row)
    {
        if (TrackOfRow(row) is not { ParentTrackId: { } parentId } track)
            return null;

        var parent = Document.Tracks.Find(t => t.Id == parentId);
        return parent is null || ReferenceEquals(parent, track) ? null : RowOf(parent);
    }

    /// <summary>The view standing for a track as its row: its first clip's, or the stand-in of a track with no clips.</summary>
    private Layer? RowOf(TimelineTrackModel track) => track.Clips.Count > 0 ? ViewOf(track.Clips[0]) : _emptyTrackViews.GetValueOrDefault(track.Id);

    /// <summary>Whether a clip is a child of another clip that is on the timeline.</summary>
    public bool HasParentClip(Layer clip) => ClipOf(clip) is { ParentClipId: { } parentId } && Document.FindClip(parentId) is not null;

    /// <summary>
    /// Takes a clip out of its parent's keeping, and a row (the view standing for its track: its first clip's, an empty
    /// track's) out of its parent track's mini-stack, its clips with it: edits of the parent no longer carry them. One step
    /// of Undo; nothing when neither is linked.
    /// </summary>
    public void UnlinkFromParent(Layer clipOrRow)
    {
        var clip = ClipOf(clipOrRow);
        var track = TrackOfRow(clipOrRow);
        var isRow = track is not null && (clipOrRow.IsEmptyTrack || (track.Clips.Count > 0 && ReferenceEquals(ViewOf(track.Clips[0]), clipOrRow)));
        var clipLinked = clip is { ParentClipId: not null };
        var rowLinked = isRow && track!.ParentTrackId is not null;
        if (!clipLinked && !rowLinked)
            return;

        if (Edit($"unlink {clipOrRow.Name}", edit =>
            {
                if (clipLinked)
                    edit.SetClipParent(clip!, null);
                if (rowLinked)
                {
                    edit.SetTrackParent(track!, null);
                    foreach (var other in track!.Clips)
                    {
                        if (other.ParentClipId is not null)
                            edit.SetClipParent(other, null);
                    }
                }
            }))
        {
            StatusText = rowLinked
                ? $"{clipOrRow.Name}'s track is on its own now: edits of the track it was under no longer move it. Ctrl+Z links it again."
                : $"{clipOrRow.Name} is on its own now: edits of the clip it lay over no longer move it. Ctrl+Z links it again.";
        }
    }

    /// <summary>
    /// Whether the frame around a clip is filled with a blurred copy of it: its own say (<see cref="Layer.BlurFill"/>), or its
    /// kind's (the main video's clips follow the sequence's Background Blur switch; no other clip has one).
    /// </summary>
    public bool IsBlurFilled(Layer clip) => clip.BlurFill ?? (clip.IsMainVideo && !_backgroundRow.IsHidden);

    /// <summary>
    /// Fills the frame around a clip with a blurred copy of it, or stops: a clip of the main video shows the blurred
    /// background under it, a video from a file is given a blurred copy of itself under it (one more decoder while it plays).
    /// One step of Undo.
    /// </summary>
    public void SetBlurFill(Layer clip, bool on)
    {
        if (ClipOf(clip) is null || clip.BlurFill == on)
            return;
        if (!clip.IsMainVideo && !clip.IsVideoFile)
        {
            StatusText = "Blur fill is for videos: the main video's clips and videos from files.";
            return;
        }

        Checkpoint(on ? $"fill the frame around {clip.Name}" : $"take the blur fill from {clip.Name}");
        clip.BlurFill = on;
        StatusText = on
            ? $"{clip.Name}: the frame around it is filled with a blurred copy of it. Ctrl+Z takes it away."
            : $"{clip.Name}: no blurred copy around it. Ctrl+Z puts it back.";
    }

    /// <summary>
    /// Gives every other clip on a clip's track its place (4.4: a clip's place is its own): its corner, size and shape, its
    /// turn and anchor, and its crop. Their looks, timing and keyframes stay theirs. One step of Undo.
    /// </summary>
    public void ApplyPlacementToTrack(Layer source)
    {
        var others = GetTrackClips(source).Where(l => !ReferenceEquals(l, source) && l.ClipId != Guid.Empty).ToList();
        if (ClipOf(source) is null || others.Count == 0)
        {
            StatusText = $"{source.Name} is the only clip on its track.";
            return;
        }

        Checkpoint($"apply the placement of {source.Name} to its track");
        var placement = _placedFor.TryGetValue(source, out var placed) ? placed : (MainPlacement?)null;
        using (DeferCommand())
        {
            (_copyingToTrack, _placingMain) = (true, true);
            try
            {
                foreach (var other in others)
                {
                    // The size first, by the corner (a layer grows about its middle), then the place.
                    other.LockAspectRatio = source.LockAspectRatio;
                    other.FromCorner(() =>
                    {
                        (other.SizeWidth, other.SizeHeight) = (source.SizeWidth, source.SizeHeight);
                        (other.PositionX, other.PositionY) = (source.PositionX, source.PositionY);
                    });
                    (other.Rotation, other.AnchorX, other.AnchorY) = (source.Rotation, source.AnchorX, source.AnchorY);
                    (other.CropLeft, other.CropTop, other.CropRight, other.CropBottom) = (source.CropLeft, source.CropTop, source.CropRight, source.CropBottom);
                    // A clip of the main video placed as the source was, for the same frame (or met afresh).
                    if (other.IsMainVideo && placement is { } same)
                        _placedFor[other] = same;
                    else
                        _placedFor.Remove(other);
                }
            }
            finally
            {
                (_copyingToTrack, _placingMain) = (false, false);
            }

            // The Center controls and the Crop boxes show what their clip has now.
            _centerShown = null;
            ShowCenterTarget();
            GenerateCommand();
        }

        StatusText = $"Every clip on {source.Name}'s track has its place now. Ctrl+Z puts theirs back.";
    }

    /// <summary>
    /// Sizes a clip to the frame and puts its middle on the frame's: fitted (the whole picture shows, touching the frame on
    /// one side) or filling it (the frame covered, what reaches past it off its edges). One step of Undo.
    /// </summary>
    public void FitClipToFrame(Layer clip, bool fill)
    {
        if (ClipOf(clip) is null || FrameWidth <= 0 || FrameHeight <= 0)
            return;

        Checkpoint(fill ? $"fill the frame with {clip.Name}" : $"fit {clip.Name} to the frame");
        FitToFrame(clip, fill);
        StatusText = fill
            ? $"{clip.Name} fills the frame, in its middle. Ctrl+Z puts it back."
            : $"{clip.Name} fits the frame, in its middle. Ctrl+Z puts it back.";
    }
}
