using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- The timeline pane's own edits (4.4) -----
// What the unified timeline offers beyond the verbs the old time bars had: closing a gap from its pill, a clip's sound
// linked to its picture or not (the U key), its sound slipped or cut J/L, a track locked from its row's header, and where
// the seams of folded dead air are. Each edit is one step of Undo, made through Edit like every other.
public partial class MainViewModel
{
    /// <summary>Whether a clip's sound follows its picture: true for one with no sound of its own (there is nothing to unlink).</summary>
    public bool IsClipAudioLinked(Layer clip) => ClipOf(clip) is not { HasSound: true } model || model.AudioLinked;

    /// <summary>How far a clip's unlinked sound was slipped from its picture, in seconds; 0 while it is linked or has none.</summary>
    public double ClipAudioSlip(Layer clip) =>
        ClipOf(clip) is { HasSound: true, AudioLinked: false } model ? model.AudioSlipSeconds : 0;

    /// <summary>
    /// A clip's J/L cut, while its sound is linked: how much sooner its sound starts than its picture, and how much longer it
    /// lasts (seconds; negative for later and shorter). Null for a clip with no sound, or an unlinked one.
    /// </summary>
    public (double LeadIn, double TailOut)? ClipAudioSplit(Layer clip) =>
        ClipOf(clip) is { HasSound: true, AudioLinked: true } model ? (model.AudioLeadInSeconds, model.AudioTailOutSeconds) : null;

    /// <summary>
    /// Links a clip's sound to its picture, or unlinks it (the U key, and Toggle A/V Link on a clip's menu). Unlinked, its
    /// sound can be dragged by itself; linked again, it goes back to its picture. One step of Undo.
    /// </summary>
    public bool ToggleClipAudioLink(Layer clip)
    {
        if (ClipOf(clip) is not { } model)
            return false;

        if (!model.HasSound)
        {
            StatusText = $"{clip.Name} has no sound of its own to link or unlink.";
            return false;
        }

        var linked = !model.AudioLinked;
        if (!Edit(linked ? $"link the sound of {clip.Name}" : $"unlink the sound of {clip.Name}", edit => edit.SetAudioLink(model, linked, 0)))
            return false;

        StatusText = linked
            ? $"The sound of {clip.Name} is linked to its picture again: they move and are cut together."
            : $"The sound of {clip.Name} is unlinked: drag it on its own lane under the clip ({LinkKeyWords()} links it again).";
        return true;
    }

    // What links a clip's sound again, in words: the key bound to Link / Unlink Clip, else its menu item.
    private static string LinkKeyWords() =>
        KeybindManager.GestureText("clip.toggleLink") is { Length: > 0 } keys ? keys : "Toggle A/V Link on its menu";

    /// <summary>Moves a clip's unlinked sound to start <paramref name="slipSeconds"/> after its picture (before it, negative). One step of Undo.</summary>
    public bool SlipClipSound(Layer clip, double slipSeconds)
    {
        if (ClipOf(clip) is not { HasSound: true, AudioLinked: false } model)
        {
            StatusText = KeybindManager.GestureText("clip.toggleLink") is { Length: > 0 } keys
                ? $"The sound of {clip.Name} is linked to its picture: unlink it first ({keys}, or Toggle A/V Link on its menu)."
                : $"The sound of {clip.Name} is linked to its picture: unlink it first (Toggle A/V Link on its menu).";
            return false;
        }

        var slip = Math.Round(double.IsFinite(slipSeconds) ? slipSeconds : 0, 3);
        return Math.Abs(slip - model.AudioSlipSeconds) < 1e-4
            || Edit($"move the sound of {clip.Name}", edit => edit.SetAudioLink(model, false, slip));
    }

    /// <summary>
    /// A J/L cut, made or changed by Alt on the lower half of a clip's edge: its sound starts <paramref name="leadIn"/> seconds
    /// before its picture and lasts <paramref name="tailOut"/> seconds after it (kept within its file and the timeline's
    /// start). One step of Undo.
    /// </summary>
    public bool SetClipAudioSplit(Layer clip, double leadIn, double tailOut)
    {
        if (ClipOf(clip) is not { HasSound: true, AudioLinked: true } model)
            return false;

        if (Math.Abs(leadIn - model.AudioLeadInSeconds) < 1e-4 && Math.Abs(tailOut - model.AudioTailOutSeconds) < 1e-4)
            return true;

        var label = leadIn > 0.001 && tailOut > 0.001 ? $"J and L cut on {clip.Name}"
            : leadIn > 0.001 ? $"J cut on {clip.Name}"
            : tailOut > 0.001 ? $"L cut on {clip.Name}"
            : $"sound of {clip.Name} back to its picture";
        return Edit(label, edit => edit.TrimAudioSplit(model, leadIn, tailOut));
    }

    /// <summary>
    /// Closes a gap on a row from its pill: what comes after it on the row's track moves back by its length (and, with
    /// Settings' Ripple All Tracks, on every unlocked track, so the tracks stay in step). One step of Undo.
    /// </summary>
    public bool CloseGapOn(Layer row, double from, double to)
    {
        if (TrackOfRow(row) is not { } track || !(to - from > TimelineDocument.MinimumClipSeconds))
            return false;

        if (!Edit($"close a gap of {to - from:0.##} s", edit => edit.CloseGap(track, from, to, AppSettings.Current.RippleAllTracks)))
            return false;

        StatusText = $"Closed the gap of {TimeDisplay.Format(to - from)} on {track.DisplayName}.";
        return true;
    }

    /// <summary>
    /// Mutes the sound of every clip on a row's track, or lets it be heard again (the mute on its header; their pictures
    /// stay). One step of Undo for the whole track. A row that stands for no track's clips has its own switch set.
    /// </summary>
    public void SetTrackSoundMuted(Layer row, bool muted)
    {
        if (TrackOfRow(row) is not { Clips.Count: > 0 } track)
        {
            row.IsMuted = muted;
            return;
        }

        Edit(muted ? $"mute the sound of {row.Name}" : $"unmute the sound of {row.Name}", edit =>
        {
            foreach (var clip in track.Clips)
                edit.SetClipFlags(clip, muted: muted);
        });
    }

    /// <summary>Whether a row's track is locked (never moved, trimmed or cut by an edit); false for the stand-ins.</summary>
    public bool IsRowLocked(Layer row) => TrackOfRow(row)?.IsLocked == true;

    /// <summary>Locks a row's track, or unlocks it (the lock on its header). One step of Undo.</summary>
    public void SetRowLocked(Layer row, bool locked)
    {
        if (TrackOfRow(row) is not { } track || track.IsLocked == locked)
            return;

        if (Edit(locked ? $"lock {track.DisplayName}" : $"unlock {track.DisplayName}", edit => edit.SetTrack(track, locked: locked)))
        {
            StatusText = locked
                ? $"{track.DisplayName} is locked: edits on the other tracks (a ripple, a cut through every track) leave it as it is."
                : $"{track.DisplayName} is unlocked.";
        }
    }

    /// <summary>
    /// The seams of folded dead air, for the rows that show them: the track each lies on (its seam clip's), where it is
    /// (seconds) and the fold, which putting back takes (<see cref="UnfoldSegment"/>). Folds whose seam clip is gone are left out.
    /// </summary>
    public IEnumerable<(Guid TrackId, double Seconds, FoldedSegment Fold)> FoldSeams()
    {
        if (Document.Folds is not { Count: > 0 } folds)
            yield break;

        foreach (var fold in folds)
        {
            if (Document.FindClip(fold.SeamClipId) is not { } clip || Document.TrackOf(clip) is not { } track)
                continue;

            yield return (track.Id, fold.AtEnd ? clip.End : clip.Start, fold);
        }
    }
}
