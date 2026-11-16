namespace HandPegApp.Models.Timeline;

/// <summary>
/// Legacy track looks (<see cref="TimelineTrackModel.TrackLook"/>: a look shared by a track's clips, from projects made before every clip
/// carried its whole look) folded into the clips once, so nothing downstream merges looks: the editor folds a document as it is put in
/// place, and the lowering folds a copy of any document that still has one (a document built in code, a path that did not install it).
/// </summary>
public sealed partial class TimelineDocument
{
    /// <summary>Whether any track still carries a shared look.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasTrackLooks => Tracks.Exists(t => t.TrackLook is not null);

    /// <summary>
    /// Folds every track's shared look into its clips (<see cref="LookOver"/>) and clears it; true when there was one. Each clip gets a
    /// new LayerState (looks are never changed in place), so a shallow copy of a document can be folded without touching the original.
    /// </summary>
    public bool FoldTrackLooks()
    {
        var folded = false;
        foreach (var track in Tracks)
        {
            if (track.TrackLook is not { } shared)
                continue;
            foreach (var clip in track.Clips)
                clip.State = LookOver(shared, clip.State);
            track.TrackLook = null;
            folded = true;
        }

        return folded;
    }

    /// <summary>
    /// A clip's look over its track's: what the track's clips share from <paramref name="trackLook"/>; what is the clip's own (its name and
    /// file, where it sits and how large, its shape, turn and crop, its blur fill, its keyframes, its anchor, its effect stack and its
    /// speed options) from <paramref name="state"/>. Neither is changed.
    /// </summary>
    public static LayerState LookOver(LayerState trackLook, LayerState state)
    {
        var look = trackLook.ShallowCopy();
        (look.Name, look.Kind, look.ImagePath, look.ImageWidth, look.ImageHeight) = (state.Name, state.Kind, state.ImagePath, state.ImageWidth, state.ImageHeight);
        (look.MediaDuration, look.HasAudio, look.AudioCodec, look.AudioChannels) = (state.MediaDuration, state.HasAudio, state.AudioCodec, state.AudioChannels);
        (look.PositionX, look.PositionY, look.SizeWidth, look.SizeHeight) = (state.PositionX, state.PositionY, state.SizeWidth, state.SizeHeight);
        (look.LockAspectRatio, look.Rotation, look.BlurFill) = (state.LockAspectRatio, state.Rotation, state.BlurFill);
        (look.TiltX, look.TiltY, look.KeysTiltX, look.KeysTiltY) = (state.TiltX, state.TiltY, state.KeysTiltX, state.KeysTiltY);
        (look.CropLeft, look.CropTop, look.CropRight, look.CropBottom) = (state.CropLeft, state.CropTop, state.CropRight, state.CropBottom);
        (look.KeysX, look.KeysY, look.KeysScale, look.KeysRotation, look.KeysOpacity) = (state.KeysX, state.KeysY, state.KeysScale, state.KeysRotation, state.KeysOpacity);
        (look.AnchorX, look.AnchorY, look.Effects, look.EffectsBypassed) = (state.AnchorX, state.AnchorY, state.Effects, state.EffectsBypassed);

        // The Speed & Time card's options are the clip's own, as its speed is.
        (look.SmoothSlowMo, look.KeepPitch, look.SpectralAudio) = (state.SmoothSlowMo, state.KeepPitch, state.SpectralAudio);
        return look;
    }
}
