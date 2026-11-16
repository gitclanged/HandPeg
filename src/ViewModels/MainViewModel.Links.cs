using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Clip links, placement and blur fill (timeline rows and menus) -----
// Tracks can nest under another track (mini-stack, e.g. a style's HUD Cutouts under their video track) and clips under the clip
// they overlay; parent edits carry children (TimelineTransaction). Placement, crop and blur fill are per clip like all look data;
// these helpers copy placement across a track, fit to frame and toggle blur fill, each in one undo step.
public partial class MainViewModel
{
    /// <summary>Document track id behind a row view (first clip's or empty-track view); null for placeholders.</summary>
    public Guid? TrackIdOf(Layer row) => TrackOfRow(row)?.Id;

    /// <summary>Parent track's row view, or null for top-level tracks.</summary>
    public Layer? ParentRowOf(Layer row)
    {
        if (TrackOfRow(row) is not { ParentTrackId: { } parentId } track)
            return null;

        var parent = Document.Tracks.Find(t => t.Id == parentId);
        return parent is null || ReferenceEquals(parent, track) ? null : RowOf(parent);
    }

    /// <summary>Row view for a track: its first clip's view, or the empty-track placeholder.</summary>
    private Layer? RowOf(TimelineTrackModel track) => track.Clips.Count > 0 ? ViewOf(track.Clips[0]) : _emptyTrackViews.GetValueOrDefault(track.Id);

    /// <summary>Whether a clip has a parent clip on the timeline.</summary>
    public bool HasParentClip(Layer clip) => ClipOf(clip) is { ParentClipId: { } parentId } && Document.FindClip(parentId) is not null;

    /// <summary>
    /// Unlinks a clip from its parent and its row's track (with its clips) from the parent mini-stack so parent edits no longer carry
    /// them. One undo step; no-op when neither is linked.
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

    /// <summary>Whether the clip has a blurred self-copy background fill (<see cref="Layer.BlurFill"/>; null = off).</summary>
    public bool IsBlurFilled(Layer clip) => clip.BlurFill ?? false;

    /// <summary>
    /// Toggles blur fill: a blurred full-frame copy of a file video beneath it (one extra decoder during playback, skipped when the
    /// clip covers the frame). One undo step.
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
    /// Copies a clip's placement (position, size, aspect, rotation, anchor, crop) to every other clip on its track; looks, timing
    /// and keyframes are untouched. One undo step.
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
                    // Size first via the corner (layers scale about their centre), then position.
                    other.LockAspectRatio = source.LockAspectRatio;
                    other.FromCorner(() =>
                    {
                        (other.SizeWidth, other.SizeHeight) = (source.SizeWidth, source.SizeHeight);
                        (other.PositionX, other.PositionY) = (source.PositionX, source.PositionY);
                    });
                    (other.Rotation, other.AnchorX, other.AnchorY) = (source.Rotation, source.AnchorX, source.AnchorY);
                    (other.TiltX, other.TiltY) = (source.TiltX, source.TiltY);
                    (other.CropLeft, other.CropTop, other.CropRight, other.CropBottom) = (source.CropLeft, source.CropTop, source.CropRight, source.CropBottom);

                    // Keep centred relative to the current frame (recomputed).
                    _placedFor.Remove(other);
                }
            }
            finally
            {
                (_settingQuietly, _placingViews) = (false, false);
            }

            // Refresh Crop boxes for their clip.
            _cropShown = null;
            ShowCropTarget();
            GenerateCommand();
        }

        StatusText = $"Every clip on {source.Name}'s track has its place now. Ctrl+Z puts theirs back.";
    }

    /// <summary>Centres a clip on the frame without resizing. One undo step.</summary>
    public void CenterClipOnFrame(Layer clip)
    {
        if (ClipOf(clip) is null || FrameWidth <= 0 || FrameHeight <= 0)
            return;

        Checkpoint($"center {clip.Name}");
        var (_, _, width, height) = clip.GetOutputRect(FrameWidth, FrameHeight, SourceWidth, SourceHeight);
        (clip.PositionX, clip.PositionY) = ((FrameWidth - width) / 2.0 / FrameWidth, (FrameHeight - height) / 2.0 / FrameHeight);
    }

    /// <summary>Clears a clip's crop (context menu Crop > Reset Crop). One undo step.</summary>
    public void ResetClipCrop(Layer clip)
    {
        if (ClipOf(clip) is null || (clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom) == (0, 0, 0, 0))
            return;

        Checkpoint($"reset the crop of {clip.Name}");
        (clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom) = (0, 0, 0, 0);
    }

    /// <summary>
    /// Toggles interactive Crop for a clip (context menu Crop): selects it so the Crop boxes and player rectangle target it
    /// (CropTarget) and enables the drawing mode, or disables it.
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
    /// Blur fill row eye (Background switch): on applies blur fill to every top-level video clip, off removes it. One undo step; also
    /// the default for newly opened files (PlaceOpenedFile).
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

    /// <summary>Centres and scales a clip to Fit (letterboxed, whole picture visible) or Fill (frame covered, overflow cropped). One undo step.</summary>
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
