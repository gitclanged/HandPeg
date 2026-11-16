namespace HandPegApp.Models.Timeline;

/// <summary>
/// Folds legacy shared track looks (<see cref="TimelineTrackModel.TrackLook"/>) into their clips once so nothing downstream merges looks:
/// the editor folds on install, and lowering folds a copy of any document still carrying one (code-built or uninstalled documents).
/// </summary>
public sealed partial class TimelineDocument
{
    /// <summary>Whether any track still carries a shared look.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasTrackLooks => Tracks.Exists(t => t.TrackLook is not null);

    /// <summary>
    /// Folds each track look into its clips (<see cref="LookOver"/>) and clears it; true if any existed. Clips get new LayerStates (never
    /// mutated), so shallow document copies fold without affecting the original.
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
    /// Merges a clip look over its track look: shared fields from <paramref name="trackLook"/>, clip-owned fields (name, file, placement,
    /// shape, rotation, crop, blur fill, keyframes, anchor, effects, speed options) from <paramref name="state"/>. Inputs are not mutated.
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
