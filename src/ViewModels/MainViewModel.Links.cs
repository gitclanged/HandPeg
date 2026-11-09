using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Links, places and blur fills of clips, for the timeline's rows and menus (4.4) -----
// A track can be a child of another (its mini-stack: a style's HUD cut-outs under their video's track), and a clip a child
// of the clip it lies over; edits of the parent carry its children along (TimelineTransaction). A clip's place, crop and
// blur fill are its own, as all of its look is: these give one clip's place to its track, fit a clip to the frame, and set
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

    /// <summary>Whether the frame around a clip is filled with a blurred copy of it: its own say (<see cref="Layer.BlurFill"/>; null is none, 5.2-AuditB).</summary>
    public bool IsBlurFilled(Layer clip) => clip.BlurFill ?? false;

    /// <summary>
    /// Fills the frame around a clip with a blurred copy of it, or stops: a video from a file is given a blurred copy of its whole
    /// picture under it (one more decoder while it plays, skipped while the clip covers the frame). One step of Undo.
    /// </summary>
    public void SetBlurFill(Layer clip, bool on)
    {
        if (ClipOf(clip) is null || IsBlurFilled(clip) == on)
            return;
        if (!clip.IsVideoFile)
        {
            StatusText = "Blur fill is for videos from files.";
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
        using (DeferCommand())
        {
            (_settingQuietly, _placingViews) = (true, true);
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
                    (other.TiltX, other.TiltY) = (source.TiltX, source.TiltY);
                    (other.CropLeft, other.CropTop, other.CropRight, other.CropBottom) = (source.CropLeft, source.CropTop, source.CropRight, source.CropBottom);

                    // Kept about its middle from the frame as it is (met afresh).
                    _placedFor.Remove(other);
                }
            }
            finally
            {
                (_settingQuietly, _placingViews) = (false, false);
            }

            // The Crop boxes show what their clip has now.
            _cropShown = null;
            ShowCropTarget();
            GenerateCommand();
        }

        StatusText = $"Every clip on {source.Name}'s track has its place now. Ctrl+Z puts theirs back.";
    }

    /// <summary>Puts a clip's middle on the frame's; its size stays. One step of Undo.</summary>
    public void CenterClipOnFrame(Layer clip)
    {
        if (ClipOf(clip) is null || FrameWidth <= 0 || FrameHeight <= 0)
            return;

        Checkpoint($"center {clip.Name}");
        var (_, _, width, height) = clip.GetOutputRect(FrameWidth, FrameHeight, SourceWidth, SourceHeight);
        (clip.PositionX, clip.PositionY) = ((FrameWidth - width) / 2.0 / FrameWidth, (FrameHeight - height) / 2.0 / FrameHeight);
    }

    /// <summary>A clip back as a new clip starts: sized to the frame in its middle, not turned, nothing cropped. This clip alone. One step of Undo.</summary>
    /// <summary>A clip's crop taken off (its menu's Crop, Reset Crop; 5.4B): one step of Undo.</summary>
    public void ResetClipCrop(Layer clip)
    {
        if (ClipOf(clip) is null || (clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom) == (0, 0, 0, 0))
            return;

        Checkpoint($"reset the crop of {clip.Name}");
        (clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom) = (0, 0, 0, 0);
    }

    /// <summary>
    /// Interactive Crop on a clip (its menu's Crop; 5.4B, the Video tab's toggle before): the clip selected, so the Crop boxes and the
    /// player's rectangle stand for it (CropTarget), and the drawing mode switched on; or off again.
    /// </summary>
    public void CropInteractively(Layer clip, bool on)
    {
        if (on && clip.IsFile && Layers.Contains(clip))
            KeyLayer = clip;
        IsInteractiveCropActive = on;
    }

    public void ResetClipTransform(Layer clip)
    {
        if (ClipOf(clip) is null)
            return;

        Checkpoint($"reset the transform of {clip.Name}");
        clip.ResetTransform();
        (clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom) = (0, 0, 0, 0);
        FitToFrame(clip, AppSettings.Current.NewClipsFill);
    }

    /// <summary>
    /// The blur fill's settings row's eye (5.2-AuditB: the Background switch): shown, every top-level video clip is given a blur
    /// fill; hidden, none is. One step of Undo. It stays what a file opened from now on is given (PlaceOpenedFile).
    /// </summary>
    public void SetBackgroundShown(bool shown)
    {
        var videos = Document.Tracks.SelectMany(t => t.Clips).Where(c => c is { ContentKind: LayerKind.VideoFile, ParentClipId: null })
            .Select(ViewOf).OfType<Layer>().Where(v => IsBlurFilled(v) != shown).ToList();
        if (videos.Count > 0)
            Checkpoint(shown ? "fill the frame around every video" : "take the blur fill from every video");

        using (HoldLookSteps())
        {
            _backgroundRow.IsHidden = !shown;
            foreach (var view in videos)
                view.BlurFill = shown ? true : null;
        }

        StatusText = shown
            ? $"Every video is filled around with a blurred copy of itself{(videos.Count > 0 ? "" : " already")}; videos opened from now on are too."
            : $"No video is filled around with a blurred copy of itself{(videos.Count > 0 ? "" : " already")}; videos opened from now on are not either.";
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
