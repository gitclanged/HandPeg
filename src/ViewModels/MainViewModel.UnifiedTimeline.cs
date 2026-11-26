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

        // The shift the transaction made (Ripple All Tracks can stop it at a clip); none is refused, so no empty undo step is kept.
        var moved = 0.0;
        if (!Edit($"close a gap of {gap.End - gap.Start:0.##} s", edit =>
            {
                moved = edit.CloseGapAt(track, seconds, AppSettings.Current.RippleAllTracks);
                if (!(moved > 1e-9))
                    throw new TimelineEditException($"The gap on {track.DisplayName} cannot close: a clip on another track is in the way.");
            }))
            return false;

        StatusText = moved < gap.End - gap.Start - 1e-6
            ? $"Closed {TimeDisplay.Format(moved)} of the {TimeDisplay.Format(gap.End - gap.Start)} gap on {track.DisplayName}."
            : $"Closed the gap of {TimeDisplay.Format(moved)} on {track.DisplayName}.";
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

// ----- Pass 5.7.1 timeline ergonomics: cut seams (through-edit heal, crossfade gain law), Top & Tail and Ctrl+drag clones -----
// Each is one undo step via Edit. A through-edit is one take cut in two (AudioProgramBuilder.IsThroughEdit) whose halves look the same;
// healing removes the later half and extends the earlier over it (its fade out and audio tail carried over, its children re-parented).
public partial class MainViewModel
{
    /// <summary>
    /// One take cut in two on one track: file, streams and speed the same, file time continuous across the cut, no crossfade, fade or J/L
    /// at the cut, same gain, flags and parent, and the same look (keyframed halves never heal, as their keys differ). Delete heals it.
    /// </summary>
    public bool IsThroughEdit(Layer left, Layer right) => ClipOf(left) is { } a && ClipOf(right) is { } b && IsThroughEdit(a, b);

    /// <summary>The model test behind <see cref="IsThroughEdit(Layer, Layer)"/>.</summary>
    internal bool IsThroughEdit(TimelineClip a, TimelineClip b) =>
        Document.TrackOf(a) is { } track && ReferenceEquals(track, Document.TrackOf(b)) && b.TransitionIn <= 0.01 && a.FadeOutSeconds <= 0.001
        && b.FadeInSeconds <= 0.001 && a.AudioTailOutSeconds == 0 && b.AudioLeadInSeconds == 0 && a.GainDb == b.GainDb && a.IsHidden == b.IsHidden
        && a.IsMuted == b.IsMuted && a.ParentClipId == b.ParentClipId && a.AudioLinked == b.AudioLinked && a.AudioSlipSeconds == b.AudioSlipSeconds
        && Services.Audio.AudioProgramBuilder.IsThroughEdit(a, b) && LookKey(a.State) == LookKey(b.State);

    // A look as JSON without the fields each half of a split owns.
    private static string LookKey(LayerState state)
    {
        if (System.Text.Json.JsonSerializer.SerializeToNode(state, TimelineTransaction.CopyOptions) is not System.Text.Json.Nodes.JsonObject json)
            return "";
        foreach (var name in json.Select(p => p.Key).Where(HalfOwned).ToList())
            json.Remove(name);
        return json.ToJsonString();
    }

    // Look fields a split gives each half its own value of (timing, fades, name); everything else must match for a heal.
    private static bool HalfOwned(string name) =>
        name is "Name" or "StartTime" or "Duration" or "MediaOffset" or "FadeInSeconds" or "FadeOutSeconds" or "TransitionIn" or "FadeInStyle" or "FadeOutStyle";

    /// <summary>
    /// Heals a through-edit (Delete on its seam): the later half goes and the earlier runs on to its end, taking its fade out and audio
    /// tail; the later half's children become the earlier's. False (with a status line) when the cut is no through-edit or the edit is refused.
    /// </summary>
    public bool HealThroughEdit(Layer left, Layer right)
    {
        FlushLooks();
        if (ClipOf(left) is not { } a || ClipOf(right) is not { } b || !IsThroughEdit(a, b))
        {
            StatusText = "Only a through-edit heals: one take cut in two, both halves alike (no crossfade, fade or keyframes at the cut).";
            return false;
        }

        var (end, fadeOut, fadeOutStyle, tail) = (b.End, b.FadeOutSeconds, b.FadeOutStyle, b.AudioTailOutSeconds);
        var children = Document.ChildrenOf(b).Select(c => c.Clip).ToList();
        if (!Edit($"heal the cut in {left.Name}", edit =>
            {
                edit.DeleteClip(b, withChildren: false);
                edit.TrimClipEnd(a, end, ripple: false, rippleAllUnlockedTracks: false, freeUnquantized: true);
                if (fadeOut > 0.001)
                    edit.SetClipFades(a, a.FadeInSeconds, fadeOut, null, fadeOutStyle);
                if (tail != 0)
                    edit.TrimAudioSplit(a, a.AudioLeadInSeconds, tail, freeUnquantized: true);
                foreach (var child in children)
                    edit.SetClipParent(child, a.Id);
            }))
        {
            return false;
        }

        Log($"Healed the cut in {left.Name}: one clip again, {TimeDisplay.Format(a.Start)} to {TimeDisplay.Format(a.End)}");
        return true;
    }

    /// <summary>
    /// Sets the gain law of the crossfade across the cut where <paramref name="left"/> meets <paramref name="right"/> (the Seam menu): the later
    /// clip's sound fades in by it and the earlier's out (Layer.CrossfadeInCurve / CrossfadeOutCurve). One undo step.
    /// </summary>
    public bool SetCrossfadeCurve(Layer left, Layer right, CrossfadeCurve curve)
    {
        curve = CrossfadeCurves.Known(curve);
        if (ClipOf(left) is null || ClipOf(right) is null)
            return false;
        if (right.CrossfadeInCurve == curve && left.CrossfadeOutCurve == curve)
            return true;

        // Look fields: set on the views and flushed into the clips' looks, one step (as Paste Attributes).
        FlushLooks();
        var label = $"{CrossfadeCurves.Label(curve)} crossfade into {right.Name}";
        var before = CaptureSnapshot();
        using (HoldLookSteps())
        {
            (right.CrossfadeInCurve, left.CrossfadeOutCurve) = (curve, curve);
            _staleLooks.Add(right);
            _staleLooks.Add(left);
        }

        // Into the clips now, so the mix (read from the document) follows at once.
        FlushLooks();
        Checkpoint(label, before);
        StatusText = $"The crossfade into {right.Name}: {CrossfadeCurves.Label(curve)}.";
        return true;
    }

    /// <summary>
    /// Top &amp; Tail (Shift+Q / Shift+E, [ and ]): ripple-trims the clip's start (<paramref name="start"/>) or end to the playhead
    /// <paramref name="seconds"/>, closing the gap so later clips follow (all unlocked tracks with Ripple All Tracks). The playhead must be
    /// inside the clip, a minimum clip length from both edges. One undo step.
    /// </summary>
    public bool RippleTrimToPlayhead(Layer clip, bool start, double seconds)
    {
        if (ClipOf(clip) is not { } model)
        {
            StatusText = "Select a clip on the Timeline to top or tail it.";
            return false;
        }

        if (!(seconds > model.Start + TimelineDocument.MinimumClipSeconds && seconds < model.End - TimelineDocument.MinimumClipSeconds))
        {
            StatusText = $"Put the playhead inside {clip.Name} to {(start ? "top" : "tail")} it there.";
            return false;
        }

        var (wasStart, wasEnd) = (model.Start, model.End);
        var moved = 0.0;
        if (!Edit(start ? $"top {clip.Name}" : $"tail {clip.Name}", edit => moved = start
                ? TopTo(edit, model, seconds)
                : edit.TrimClipEnd(model, seconds, ripple: true, AppSettings.Current.RippleAllTracks)))
        {
            return false;
        }

        if (Math.Abs(moved) < 1e-6)
        {
            StatusText = $"{clip.Name} cannot be {(start ? "topped" : "tailed")} there: what follows cannot move.";
            return false;
        }

        Log(start
            ? $"Topped {clip.Name}: {TimeDisplay.Format(seconds - wasStart)} cut from its start, what follows moved up"
            : $"Tailed {clip.Name}: {TimeDisplay.Format(wasEnd - seconds)} cut from its end, what follows moved up");
        return true;
    }

    // A ripple start trim to the playhead: the clip keeps its start and loses its first (playhead - start) seconds; what follows moves up.
    private static double TopTo(ITimelineTransaction edit, TimelineClip model, double seconds) =>
        edit.TrimClipStart(model, seconds, ripple: true, AppSettings.Current.RippleAllTracks);

    /// <summary>
    /// Ctrl+drag clone: a copy of the clip (with its family: Cutouts, linked sound, Sub-Lanes) starting at <paramref name="seconds"/>, on its
    /// track where free, else a new track just above (as Paste). The original stays. One undo step; the copy is selected.
    /// </summary>
    public bool CloneClipTo(Layer clip, double seconds)
    {
        FlushLooks();
        var roots = CopyRoots([clip]);
        if (roots.Count == 0)
        {
            StatusText = $"{clip.Name} cannot be cloned.";
            return false;
        }

        return PlaceFamilies(Families(roots), Math.Max(seconds, 0), $"clone {clip.Name}");
    }
}
