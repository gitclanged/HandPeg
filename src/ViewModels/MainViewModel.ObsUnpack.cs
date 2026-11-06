using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Unpack OBS Side-by-Side Canvas (Pass 5.2C, Services/ObsUnpacker.cs) -----
// A double-wide recording (the gameplay and the facecam side by side on one canvas) is unpacked in one step of Undo: the clip keeps its
// left half (cropped to it, placed as ObsUnpacker lays it for the Sequence's shape) and a Cutout of the same file on the same clock shows
// its right half on a Sub-Lane above, as a Game Preset's Cutouts are made (MainViewModel.Templates.cs): the clip's child, so the two
// split, trim, move and go together. A Sequence that took the double-wide canvas's own shape (the first file opened) takes one half's.
public partial class MainViewModel
{
    /// <summary>What a side-by-side Cutout is called.</summary>
    public const string FacecamName = "Facecam";

    /// <summary>Whether a clip is a side-by-side recording to unpack: a clip of a video file (not a Cutout) whose picture is double wide.</summary>
    public bool CanUnpackSideBySide(Layer view) =>
        view is { IsVideoFile: true, IsDeleted: false } && ClipOf(view) is { ContentKind: LayerKind.VideoFile } clip && !IsCutout(clip)
        && ObsUnpacker.IsSideBySide(view.ImageWidth, view.ImageHeight);

    /// <summary>Says once that a clip just placed can be unpacked (the clip's menu has it): never done unasked.</summary>
    private void OfferSideBySide(Layer view)
    {
        if (!_isBackgroundWorker && CanUnpackSideBySide(view))
            StatusText = $"{view.Name} is two pictures side by side ({view.ImageWidth} x {view.ImageHeight}): right-click its clip, Unpack OBS Side-by-Side Canvas, for the gameplay and the facecam as two clips that stay in step.";
    }

    /// <summary>
    /// Unpacks a side-by-side recording (see the top of this file): the clip shows the gameplay (the left half), a Cutout above it the facecam
    /// (the right half), placed for the Sequence's shape. One step of Undo.
    /// </summary>
    /// <returns>Whether it was unpacked.</returns>
    public bool UnpackSideBySide(Layer view)
    {
        if (!CanUnpackSideBySide(view) || ClipOf(view) is not { } clip || Document.TrackOf(clip) is not { IsLocked: false })
        {
            StatusText = "Unpack OBS Side-by-Side Canvas works on a clip of a double-wide recording (3840 x 1080, 5120 x 1440: two pictures side by side) on a track that is not locked.";
            return false;
        }

        var label = $"unpack {view.Name}'s side-by-side canvas";
        FlushLooks();
        var before = CaptureSnapshot();
        using var held = HoldLookSteps();
        using var whole = DeferCommand();

        // A Sequence shaped as the whole canvas (the first file opened gave it its shape) takes one half's: two 16:9 pictures, a 16:9 frame.
        if (GetRequestedSize() is null && ObsUnpacker.IsSideBySide(FrameWidth, FrameHeight) && Document.HasNaturalSize)
        {
            (Document.NaturalWidth, Document.NaturalHeight) = (Even(view.ImageWidth / 2.0), Even(view.ImageHeight));
            NoteDocumentShape();
        }

        var layout = ObsUnpacker.Layout(view.ImageWidth, view.ImageHeight, Math.Max(FrameWidth, 2), Math.Max(FrameHeight, 2));

        // The facecam's look is the clip's as it was (its colour, its effects), without its place's keys or its blur fill (one is enough).
        var facecam = TimelineTransaction.CopyOf(view.ToState());
        (facecam.KeysX, facecam.KeysY, facecam.KeysScale, facecam.KeysRotation, facecam.KeysTiltX, facecam.KeysTiltY) = (null, null, null, null, null, null);
        (facecam.Name, facecam.BlurFill, facecam.Rotation, facecam.TiltX, facecam.TiltY) = (FacecamName, null, 0, 0, 0);
        (facecam.CustomMask, facecam.MaskPath, facecam.MaskShapes, facecam.MaskFrame, facecam.MaskInvert, facecam.MaskGrow) = (false, "", "", false, false, 0);
        Place(facecam, layout.Facecam);

        // The clip itself: the gameplay.
        var placing = _placingViews;
        _placingViews = true;
        try
        {
            foreach (var key in new[] { KeyProperty.X, KeyProperty.Y, KeyProperty.Scale })
                view.SetKeys(key, null);
            (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom) = (layout.Gameplay.CropLeft, layout.Gameplay.CropTop, layout.Gameplay.CropRight, layout.Gameplay.CropBottom);
            (view.LockAspectRatio, view.SizeWidth, view.PositionX, view.PositionY) = (true, layout.Gameplay.Width, layout.Gameplay.X, layout.Gameplay.Y);
        }
        finally
        {
            _placingViews = placing;
        }

        _placedFor.Remove(view);
        _staleLooks.Add(view);

        var made = false;
        Edit(label, edit =>
        {
            if (Document.FindClip(clip.Id) is not { } now || Document.TrackOf(now) is not { } track)
                return;

            var pictures = Document.ChildTracksOf(track).Where(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)).Select(t => Document.Tracks.IndexOf(t)).ToList();
            var above = Math.Max(Document.Tracks.IndexOf(track), pictures.Count > 0 ? pictures.Max() : 0) + 1;
            (facecam.TrackId, facecam.IsHidden, facecam.StartTime, facecam.Duration) = (0, false, now.Start, now.End - now.Start);
            var state = OnClockOf(facecam, view, now, now.Start);
            var cam = Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            var piece = NewClipFor(cam, now.Start, now.End);
            Adopt(cam, piece);
            try
            {
                var lane = LaneFor(edit, track, cam, now.Start, now.End, under: false, ref above);
                edit.SetClipParent(edit.PlaceClip(lane, piece, allowCrossfadeOverlap: false), now.Id);
                made = true;
            }
            catch (TimelineEditException)
            {
                Unadopt(cam);
            }
        }, undoable: false);

        Checkpoint(label, before);
        StatusText = made
            ? $"{view.Name} unpacked: the gameplay on its track, the facecam a Cutout above it on the same clock. Ctrl+Z puts it back."
            : $"{view.Name}'s gameplay is cut to its half, but there was no room above it for the facecam.";
        return true;
    }

    // A half's crop and box on a state.
    private static void Place(LayerState state, UnpackedPart part)
    {
        (state.CropLeft, state.CropTop, state.CropRight, state.CropBottom) = (part.CropLeft, part.CropTop, part.CropRight, part.CropBottom);
        (state.LockAspectRatio, state.SizeWidth, state.PositionX, state.PositionY) = (true, part.Width, part.X, part.Y);
    }
}
