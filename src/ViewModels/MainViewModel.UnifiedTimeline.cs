using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Timeline pane edits -----
// Close-gap pills, A/V link toggle (U), audio slip and J/L cuts, track lock from the header, and folded dead-air seams. Each is one
// undo step via Edit.
public partial class MainViewModel
{
    /// <summary>Whether a clip's audio is linked to its picture (true when it has no audio to unlink).</summary>
    public bool IsClipAudioLinked(Layer clip) => IsClipAudioLinked(ClipOf(clip));

    /// <summary>Overload for an already-resolved clip (row rebuilds resolve each once).</summary>
    internal static bool IsClipAudioLinked(TimelineClip? model) => model is not { HasSound: true } || model.AudioLinked;

    /// <summary>Unlinked audio slip from the picture (s); 0 when linked or silent.</summary>
    public double ClipAudioSlip(Layer clip) => ClipAudioSlip(ClipOf(clip));

    internal static double ClipAudioSlip(TimelineClip? model) => model is { HasSound: true, AudioLinked: false } ? model.AudioSlipSeconds : 0;

    /// <summary>
    /// Linked-audio J/L cut: audio lead-in before and tail-out after the picture (s; negative = later/shorter). Null when silent or
    /// unlinked.
    /// </summary>
    public (double LeadIn, double TailOut)? ClipAudioSplit(Layer clip) => ClipAudioSplit(ClipOf(clip));

    internal static (double LeadIn, double TailOut)? ClipAudioSplit(TimelineClip? model) =>
        model is { HasSound: true, AudioLinked: true } ? (model.AudioLeadInSeconds, model.AudioTailOutSeconds) : null;

    /// <summary>
    /// Toggles A/V link (U, Toggle A/V Link). Unlinked audio drags independently; relinking snaps it back to the picture. One undo step.
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

    // Relink hint text: the key bound to Link / Unlink Clip, else the menu item name.
    private static string LinkKeyWords() =>
        KeybindManager.GestureText("clip.toggleLink") is { Length: > 0 } keys ? keys : "Toggle A/V Link on its menu";

    /// <summary>Slips unlinked audio to start <paramref name="slipSeconds"/> after the picture (negative = before). One undo step.</summary>
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
    /// Sets a J/L cut (Alt-drag on the lower half of a clip edge): audio starts <paramref name="leadIn"/> s before and ends
    /// <paramref name="tailOut"/> s after the picture, clamped to the file and timeline start. One undo step.
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
    /// Close-gap pill: shifts later content on the track back by the gap (all unlocked tracks with Ripple All Tracks, keeping sync).
    /// A leading gap (from 0 s) brings the track's first clip to the start. One undo step.
    /// </summary>
    /// <summary>
    /// Double-click in empty track space: ripple-closes the gap around <paramref name="seconds"/> on the row's track
    /// (ITimelineTransaction.CloseGapAt, Ripple All Tracks per the setting). One undo step.
    /// </summary>
    public bool CloseGapAt(Layer row, double seconds)
    {
        if (TrackOfRow(row) is not { } track || Document.GapAround(track, seconds) is not { } gap || !(gap.End - gap.Start > TimelineDocument.MinimumClipSeconds))
            return false;

        if (!Edit($"close a gap of {gap.End - gap.Start:0.##} s", edit => edit.CloseGapAt(track, seconds, AppSettings.Current.RippleAllTracks)))
            return false;

        StatusText = $"Closed the gap of {TimeDisplay.Format(gap.End - gap.Start)} on {track.DisplayName}.";
        return true;
    }

    /// <summary>
    /// Alt+drop of a file on a clip: it (and its Cutouts) play <paramref name="path"/> instead, keeping place, length, look and effects
    /// (ITimelineTransaction.ReplaceClipSource, checked against the file's probed <paramref name="videoStreams"/> and
    /// <paramref name="audioStreams"/>). One undo step.
    /// </summary>
    public bool SwapClipMedia(Layer clip, string path, double sourceDuration, double pictureDuration, int videoStreams, int audioStreams)
    {
        if (ClipOf(clip) is not { PlaysFile: true } model || IsRestoringTimeline)
            return false;

        if (!Edit($"swap the media of {clip.Name}", edit => edit.ReplaceClipSource(model, path, sourceDuration, pictureDuration, videoStreams, audioStreams)))
            return false;

        StatusText = $"Swapped in {System.IO.Path.GetFileName(path)}: same place, length and effects.";
        return true;
    }

    public bool CloseGapOn(Layer row, double from, double to)
    {
        from = Math.Max(from, 0);
        if (TrackOfRow(row) is not { } track || !(to - from > TimelineDocument.MinimumClipSeconds))
            return false;

        if (!Edit($"close a gap of {to - from:0.##} s", edit => edit.CloseGap(track, from, to, AppSettings.Current.RippleAllTracks)))
            return false;

        StatusText = from <= 0
            ? $"{track.DisplayName} now starts at 0 s (closed {TimeDisplay.Format(to)})."
            : $"Closed the gap of {TimeDisplay.Format(to - from)} on {track.DisplayName}.";
        return true;
    }

    /// <summary>Whether a row's track is locked (immune to move/trim/cut); false for placeholders.</summary>
    public bool IsRowLocked(Layer row) => TrackOfRow(row)?.IsLocked == true;

    /// <summary>Toggles a row's track lock (header lock). One undo step.</summary>
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
    /// Folded dead-air seams for row display: track (from the seam clip), time (s) and fold (for <see cref="UnfoldSegment"/>). Folds
    /// whose seam clip is gone are skipped.
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
