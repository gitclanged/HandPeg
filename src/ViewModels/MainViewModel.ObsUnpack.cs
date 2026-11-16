using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Unpack OBS Side-by-Side Canvas (Services/ObsUnpacker.cs) -----
// Splits a double-wide recording (gameplay + facecam) in one undo step: the clip keeps the left half (cropped, laid out by ObsUnpacker
// for the Sequence aspect) and a same-file, same-clock Cutout child on a Sub-Lane above shows the right half (as Game Preset Cutouts,
// MainViewModel.Templates.cs), so both split/trim/move/delete together. A Sequence sized to the double-wide canvas is resized to one half.
public partial class MainViewModel
{
    /// <summary>Name given to the side-by-side Cutout.</summary>
    public const string FacecamName = "Facecam";

    /// <summary>Whether a clip is an unpackable side-by-side recording (file video, not a Cutout, double-wide picture).</summary>
    public bool CanUnpackSideBySide(Layer view) =>
        view is { IsVideoFile: true, IsDeleted: false } && ClipOf(view) is { ContentKind: LayerKind.VideoFile } clip && !IsCutout(clip)
        && ObsUnpacker.IsSideBySide(view.ImageWidth, view.ImageHeight);

    /// <summary>One-time hint that a placed clip can be unpacked via its menu; never automatic.</summary>
    private void OfferSideBySide(Layer view)
    {
        if (!_isBackgroundWorker && CanUnpackSideBySide(view))
            StatusText = $"{view.Name} is two pictures side by side ({view.ImageWidth} x {view.ImageHeight}): right-click its clip, Unpack OBS Side-by-Side Canvas, for the gameplay and the facecam as two clips that stay in step.";
    }

    /// <summary>Unpacks a side-by-side recording: clip = gameplay (left half), Cutout above = facecam (right half). One undo step.</summary>
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

        // A Sequence matching the whole canvas (from the first opened file) is resized to one half: two 16:9 halves, 16:9 frame.
        if (GetRequestedSize() is null && ObsUnpacker.IsSideBySide(FrameWidth, FrameHeight) && Document.HasNaturalSize)
        {
            Edit(label, edit => edit.SetFrame(Even(view.ImageWidth / 2.0), Even(view.ImageHeight), Document.ExplicitFrame), undoable: false);
            NoteDocumentShape();
        }

        var layout = ObsUnpacker.Layout(view.ImageWidth, view.ImageHeight, Math.Max(FrameWidth, 2), Math.Max(FrameHeight, 2));

        // Facecam inherits the clip's look (colour, effects) minus placement keys and blur fill (one fill suffices).
        var facecam = TimelineTransaction.CopyOf(view.ToState());
        (facecam.KeysX, facecam.KeysY, facecam.KeysScale, facecam.KeysRotation, facecam.KeysTiltX, facecam.KeysTiltY) = (null, null, null, null, null, null);
        (facecam.Name, facecam.BlurFill, facecam.Rotation, facecam.TiltX, facecam.TiltY) = (FacecamName, null, 0, 0, 0);
        (facecam.CustomMask, facecam.MaskPath, facecam.MaskShapes, facecam.MaskFrame, facecam.MaskInvert, facecam.MaskGrow) = (false, "", "", false, false, 0);
        Place(facecam, layout.Facecam);

        // The original clip becomes the gameplay half.
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

    // Applies a half's crop and placement rect to a state.
    private static void Place(LayerState state, UnpackedPart part)
    {
        (state.CropLeft, state.CropTop, state.CropRight, state.CropBottom) = (part.CropLeft, part.CropTop, part.CropRight, part.CropBottom);
        (state.LockAspectRatio, state.SizeWidth, state.PositionX, state.PositionY) = (true, part.Width, part.X, part.Y);
    }
}
