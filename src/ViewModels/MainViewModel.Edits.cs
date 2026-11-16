using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Timeline edits beyond move/end-trim (UnifiedTimelineControl): start trim, ripple, roll, slip, ripple delete, gain line, fades
// and transition styles, same-track and cross-track crossfades, and track solo. Each is one document Edit; all clips (including
// stream Sub-Lanes) are treated uniformly.
public partial class MainViewModel
{
    /// <summary>
    /// Start-trim: moves the clip start and its source in-point so content stays in place; with ripple, the start stays and later
    /// clips follow the end. At a folded dead-air seam a plain drag unfolds/folds instead (<see cref="TrimIntoSeam"/>); ripple stays ripple.
    /// </summary>
    public void TrimClipStart(Layer clip, double start, bool ripple)
    {
        if (ClipOf(clip) is not { } model)
            return;

        if (!ripple && FoldAtEdge(clip, startEdge: true) is not null)
        {
            TrimIntoSeam(clip, startEdge: true, start);
            return;
        }

        var moved = 0.0;
        if (Edit($"trim the start of {clip.Name}", edit => moved = edit.TrimClipStart(model, start, ripple, AppSettings.Current.RippleAllTracks)) && Math.Abs(moved) < 1e-6)
        {
            StatusText = model.PlaysFile && model.MediaOffset < 0.001 && start < model.Start
                ? $"{clip.Name} already starts at the beginning of its file."
                : $"The start of {clip.Name} cannot move that way: it is held by the clip before it, the timeline's start or its own length.";
            return;
        }

        if (Math.Abs(moved) >= 1e-6)
            Log($"Trimmed the start of {clip.Name}{(ripple ? ", rippled" : "")}: it now runs {TimeDisplay.Format(model.Start)} to {TimeDisplay.Format(model.End)}");
    }

    /// <summary>Ctrl end-trim (ripple): sets the length and later clips follow the end.</summary>
    public void RippleClipEnd(Layer clip, double length)
    {
        if (ClipOf(clip) is not { } model)
            return;

        var moved = 0.0;
        var asked = Math.Max(length, TimelineDocument.MinimumClipSeconds) - model.Duration;
        if (!Edit($"ripple the end of {clip.Name}", edit => moved = edit.TrimClipEnd(model, model.Start + Math.Max(length, TimelineDocument.MinimumClipSeconds), ripple: true, AppSettings.Current.RippleAllTracks)))
            return;

        if (Math.Abs(moved) < 1e-6)
        {
            StatusText = $"The end of {clip.Name} cannot move that way: its file has no more to show, or what follows (on this track or, with Ripple affects all tracks, another) cannot follow it.";
            return;
        }

        if (Math.Abs(moved - asked) > 0.002)
            StatusText = "Some clips could not follow all the way: they would have run onto clips of their tracks that stay.";
        Log($"Rippled the end of {clip.Name}: {(moved > 0 ? $"what follows moved {TimeDisplay.Format(moved)} later" : $"what follows moved {TimeDisplay.Format(-moved)} earlier")}");
    }

    /// <summary>Roll edit: moves the cut between two adjacent clips, adjusting both source ranges; nothing else moves.</summary>
    public void RollCut(Layer before, Layer after, double cut)
    {
        if (ClipOf(before) is not { } earlier || ClipOf(after) is not { } later)
            return;

        var moved = 0.0;
        if (!Edit($"roll the cut between {before.Name} and {after.Name}", edit => moved = edit.RollCut(earlier, later, cut)))
            return;

        if (Math.Abs(moved) < 1e-6)
            StatusText = "The cut cannot move that way: one of the clips has no more of its file to show, or would be too short.";
        else
            Log($"Rolled the cut between {before.Name} and {after.Name} to {TimeDisplay.Format(later.Start)}");
    }

    /// <summary>Slip edit: shifts the source range by timeline seconds (drag left = later in file); position and length unchanged.</summary>
    public void SlipClip(Layer clip, double seconds)
    {
        if (ClipOf(clip) is not { PlaysFile: true } model)
            return;

        var slipped = 0.0;
        if (!Edit($"slip {clip.Name}", edit => slipped = edit.SlipClip(model, -seconds * model.Speed)))
            return;

        if (Math.Abs(slipped) < 1e-6)
            StatusText = $"{clip.Name} cannot slip further: it would run past {(model.MediaOffset <= 0.0005 ? "the start" : "the end")} of its file.";
        else
            Log($"Slipped {clip.Name}: it now plays its file from {TimeDisplay.Format(model.MediaOffset)}");
    }

    /// <summary>
    /// Shift+Delete ripple delete to the recycle bin, closing the gap on its track (or all tracks). For a multi-selection, deletes the
    /// group in one step (<see cref="DeleteClips"/>).
    /// </summary>
    public bool RippleDelete(object? selected)
    {
        if (selected is Layer member && IsInClipGroup(member))
            return DeleteClips(SelectedClips, ripple: true);

        if (selected is not Layer view || ClipOf(view) is not { } clip || Document.TrackOf(clip) is not { } track)
            return DeleteClip(selected);

        var deleted = Binned(clip, track, view);
        var length = clip.Duration;
        var moved = 0.0;
        if (!Edit($"ripple delete {view.Name}", edit => moved = edit.RippleDelete(clip, AppSettings.Current.RippleAllTracks)))
            return false;

        AddToBin(deleted.Clip, view.Name, deleted.Preview);
        if (moved < length - 0.001)
        {
            // Refused: a non-moving clip on another track (with ripple-all) would collide with or desync from following content.
            StatusText = moved < 0.001
                ? $"Deleted {view.Name} (in the recycle bin), but the gap stays: a clip of another track lies across it. Delete that part too (split it with [ and ]), then close the gap."
                : $"Deleted {view.Name} (in the recycle bin); what follows moved back {TimeDisplay.Format(moved)}, as far as the clips of other tracks let it.";
            return true;
        }

        Log($"Ripple deleted clip: {view.Name} (in the recycle bin; Ctrl+Z brings it back, and the clips after it)");
        return true;
    }

    /// <summary>
    /// Commits a move or end-trim. A small overlap with a neighbour (one frame to 2 s, at most half of either) becomes a butt joint
    /// with a crossfade of that length. An end at a folded dead-air seam unfolds/folds instead (<see cref="TrimIntoSeam"/>).
    /// </summary>
    public void CommitClipMove(Layer clip, double start, double length)
    {
        if (ClipOf(clip) is not { } model || Document.TrackOf(model) is not { } track)
            return;

        var moves = Math.Abs(start - model.Start) > 0.0005;
        var resizes = Math.Abs(length - model.Duration) > 0.0005;
        if (!moves && !resizes)
            return;

        if (resizes && !moves && FoldAtEdge(clip, startEdge: false) is not null)
        {
            TrimIntoSeam(clip, startEdge: false, model.Start + Math.Max(length, TimelineDocument.MinimumClipSeconds));
            return;
        }

        var crossfades = track.Clips.Sum(c => c.TransitionIn);
        if (!Edit($"{(moves ? "move" : "trim the end of")} {clip.Name}", edit =>
            {
                if (moves)
                    edit.MoveClip(model, track, Math.Max(start, 0));
                if (resizes)
                    edit.TrimClipEnd(model, model.Start + Math.Max(length, TimelineDocument.MinimumClipSeconds));
            }))
        {
            return;
        }

        if (Document.TrackOf(model) is { } now && Math.Abs(now.Clips.Sum(c => c.TransitionIn) - crossfades) > 0.0005 && model.TransitionIn + (After(model)?.TransitionIn ?? 0) > 0)
            Log($"Crossfade into {(model.TransitionIn > 0 ? clip.Name : ViewOf(After(model)!)?.Name ?? "the next clip")} (let go over it: they now meet, and fade into each other at the cut)");
    }

    /// <summary>
    /// "Show for the Whole Video" / "Play from Its Start to Its End": spans the clip over the whole timeline (file clips from file
    /// start, limited by file length). One undo step.
    /// </summary>
    public void ShowThroughout(Layer view)
    {
        if (ClipOf(view) is not { } clip || Document.TrackOf(clip) is not { } track)
            return;

        var end = Math.Max(SequenceSeconds, clip.End);
        if (!Edit($"show {view.Name} throughout", edit =>
            {
                if (clip.PlaysFile && clip.MediaOffset > 0)
                    edit.SlipClip(clip, -clip.MediaOffset);
                if (clip.Start > 0)
                    edit.MoveClip(clip, track, 0, allowCrossfadeOverlap: false);
                edit.TrimClipEnd(clip, end);
            }))
        {
            return;
        }

        Log($"{view.Name} now runs {TimeDisplay.Format(clip.Start)} to {TimeDisplay.Format(clip.End)}");
    }

    /// <summary>
    /// Ripple-deletes a time range across all unlocked tracks: split at both ends, remove the middle, shift later content back.
    /// Clips with an edge too near an end go whole. Runs in the given edit; refused entirely if a track cannot follow (a locked
    /// track spanning it).
    /// </summary>
    private void TakeOutStretch(ITimelineTransaction edit, double from, double to)
    {
        if (!(to - from >= TimelineDocument.MinimumClipSeconds))
            return;

        var tracks = Document.Tracks.Where(t => !t.IsLocked && t.Kind != TimelineTrackKind.Subtitle).ToList();
        foreach (var track in tracks)
        {
            foreach (var clip in track.ClipsIntersecting(from, to).ToArray())
            {
                var inside = clip;
                try
                {
                    if (inside.End > to + TimelineDocument.MinimumClipSeconds && to > inside.Start + TimelineDocument.MinimumClipSeconds)
                        edit.SplitClipAt(inside, to);
                    if (inside.Start < from - TimelineDocument.MinimumClipSeconds && from < inside.End - TimelineDocument.MinimumClipSeconds)
                        inside = edit.SplitClipAt(inside, from);
                }
                catch (TimelineEditException)
                {
                    // Audio too short on one side to split (J/L cut): remove it whole.
                }

                edit.DeleteClip(inside);
            }
        }

        if (tracks.FirstOrDefault() is { } first)
            edit.CloseGap(first, from, to, rippleAllUnlockedTracks: true);
    }

    /// <summary>Adjacent following clip on the same track (starts at this clip's end); null when none.</summary>
    private TimelineClip? After(TimelineClip clip)
    {
        if (Document.TrackOf(clip) is not { } track)
            return null;

        var index = track.IndexOf(clip);
        return index + 1 < track.Clips.Count && Math.Abs(track.Clips[index + 1].Start - clip.End) < 1e-6 ? track.Clips[index + 1] : null;
    }

    /// <summary>Adjacent preceding clip on the same track (ends at this clip's start); null when none.</summary>
    public Layer? ClipBefore(Layer clip)
    {
        if (ClipOf(clip) is not { } model || Document.TrackOf(model) is not { } track)
            return null;

        var index = track.IndexOf(model);
        return index > 0 && Math.Abs(track.Clips[index - 1].End - model.Start) < 1e-6 ? ViewOf(track.Clips[index - 1]) : null;
    }

    /// <summary>Sets the crossfade length from the preceding clip (0 = hard cut).</summary>
    public void SetCrossfade(Layer clip, double seconds)
    {
        if (ClipOf(clip) is not { } model)
            return;

        var set = 0.0;
        seconds = Math.Clamp(seconds, 0, TimelineDocument.LongestCrossfade);
        if (Edit(seconds > 0 ? $"crossfade into {clip.Name}" : $"remove the crossfade into {clip.Name}", edit => set = edit.SetCrossfade(model, seconds)))
            Log(set > 0 ? $"Crossfade of {TimeDisplay.Format(set)} into {clip.Name}, centred on the cut" : $"Removed the crossfade into {clip.Name}");
        else if (seconds > 0)
            StatusText = $"{clip.Name} does not meet the clip before it: put them end to end first (snapping puts one where the other ends).";
    }

    /// <summary>Gain line drag: sets clip gain (dB); fades use the corner handles (SetClipFade).</summary>
    public void SetClipGain(Layer clip, double gainDb)
    {
        if (ClipOf(clip) is not { } model)
            return;

        if (Edit($"change the gain of {clip.Name}", edit => edit.SetClipGain(model, Math.Round(gainDb, 1))))
            StatusText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{clip.Name}: {model.GainDb:+0.0;-0.0;0} dB");
    }

    // ----- Fades and transitions -----
    // Each clip fades independently (picture and audio) over whatever lies beneath, so no same-track neighbour is required. The
    // picture uses a TransitionStyle (dissolve, wipe, slide, zoom, blur); audio fades regardless of style.

    /// <summary>Fade handle release: sets fade in/out seconds, clamped so both fades fit within the clip.</summary>
    public void SetClipFade(Layer clip, bool fadeIn, double seconds)
    {
        if (ClipOf(clip) is not { } model)
            return;

        var room = Math.Max(model.Duration - (fadeIn ? model.FadeOutSeconds : model.FadeInSeconds), 0);
        seconds = Math.Round(Math.Clamp(seconds, 0, Math.Min(room, TimelineDocument.LongestFade)), 3);
        if (Math.Abs(seconds - (fadeIn ? model.FadeInSeconds : model.FadeOutSeconds)) < 0.005)
            return;

        if (Edit($"fade {(fadeIn ? "in" : "out")} {clip.Name}", edit =>
                edit.SetClipFades(model, fadeIn ? seconds : model.FadeInSeconds, fadeIn ? model.FadeOutSeconds : seconds)))
            StatusText = FadeText(clip.Name, fadeIn, seconds, fadeIn ? model.FadeInStyle : model.FadeOutStyle);
    }

    /// <summary>Sets a fade's picture transition style; adds a default-length fade if none exists.</summary>
    public void SetClipFadeStyle(Layer clip, bool fadeIn, TransitionStyle style)
    {
        if (ClipOf(clip) is not { } model)
            return;

        var seconds = fadeIn ? model.FadeInSeconds : model.FadeOutSeconds;
        if ((fadeIn ? model.FadeInStyle : model.FadeOutStyle) == style && seconds >= 0.01)
            return;

        if (seconds < 0.01)
            seconds = DefaultFade(model.Duration - (fadeIn ? model.FadeOutSeconds : model.FadeInSeconds));
        if (Edit($"{TransitionStyles.Label(style)} for the fade {(fadeIn ? "in" : "out")} of {clip.Name}", edit =>
                edit.SetClipFades(model, fadeIn ? seconds : model.FadeInSeconds, fadeIn ? model.FadeOutSeconds : seconds,
                    fadeIn ? style : null, fadeIn ? null : style)))
            Log(FadeText(clip.Name, fadeIn, seconds, style));
    }

    /// <summary>Default fade length for a new style (Settings default transition length, clamped to available room).</summary>
    private static double DefaultFade(double room) => Math.Round(Math.Clamp(AppSettings.Current.DefaultTransitionSeconds, 0, Math.Max(room, 0)), 2);

    private static string FadeText(string name, bool fadeIn, double seconds, TransitionStyle style) => seconds < 0.005
        ? $"{name}: no fade {(fadeIn ? "in" : "out")}"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}: fade {(fadeIn ? "in" : "out")} {seconds:0.##} s, {TransitionStyles.Label(style)}");

    /// <summary>
    /// Cross-track overlap partner at this clip's start (or end) and the overlap duration (the crossfade span); same media kind only,
    /// longest overlap wins. Null when none.
    /// </summary>
    public (Layer Other, double Seconds)? OverlapAt(Layer clip, bool atStart)
    {
        if (ClipOf(clip) is not { } model || Document.TrackOf(model) is not { } own)
            return null;

        var (from, to) = (model.Start, model.End);
        (Layer Other, double Seconds)? best = null;
        foreach (var track in Document.Tracks)
        {
            if (ReferenceEquals(track, own) || track.Kind == TimelineTrackKind.Subtitle || (track.Kind == TimelineTrackKind.Audio) != (own.Kind == TimelineTrackKind.Audio)
                || (track.Kind == TimelineTrackKind.Adjustment) != (own.Kind == TimelineTrackKind.Adjustment))
            {
                continue;
            }

            foreach (var other in track.ClipsIntersecting(from - 0.02, to + 0.02))
            {
                if (other.IsHidden || ViewOf(other) is not { } view)
                    continue;

                var seconds = atStart
                    ? other.Start < from - 0.01 && other.End > from + 0.02 ? Math.Min(other.End, to) - from : 0
                    : other.End > to + 0.01 && other.Start < to - 0.02 ? to - Math.Max(other.Start, from) : 0;
                if (seconds > 0.02 && seconds > (best?.Seconds ?? 0))
                    best = (view, Math.Round(seconds, 3));
            }
        }

        return best;
    }

    /// <summary>
    /// Cross-track crossfade: sets this clip's fade and the partner's opposite fade to the overlap length. The upper clip carries the
    /// picture transition style; the lower keeps its picture until it ends (PairLayerCrossfades); audio crossfades.
    /// </summary>
    public void CrossfadeWithOverlap(Layer clip, bool atStart)
    {
        if (OverlapAt(clip, atStart) is not var (other, seconds) || ClipOf(clip) is not { } model || ClipOf(other) is not { } otherModel)
        {
            StatusText = $"No clip on another track is on while {clip.Name} {(atStart ? "starts" : "ends")}: put one there first.";
            return;
        }

        static double Room(TimelineClip layer, bool fadeIn, double seconds) =>
            Math.Min(seconds, Math.Max(layer.Duration - (fadeIn ? layer.FadeOutSeconds : layer.FadeInSeconds), 0));
        if (!Edit($"crossfade {clip.Name} with {other.Name}", edit =>
            {
                if (atStart)
                {
                    edit.SetClipFades(model, Room(model, true, seconds), model.FadeOutSeconds);
                    edit.SetClipFades(otherModel, otherModel.FadeInSeconds, Room(otherModel, false, seconds));
                }
                else
                {
                    edit.SetClipFades(model, model.FadeInSeconds, Room(model, false, seconds));
                    edit.SetClipFades(otherModel, Room(otherModel, true, seconds), otherModel.FadeOutSeconds);
                }
            }))
        {
            return;
        }

        Log(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Crossfade of {seconds:0.##} s between {(atStart ? other.Name : clip.Name)} and {(atStart ? clip.Name : other.Name)}, across separate tracks"));
    }

    // ----- Track solo (TimelineTrackModel.IsSoloed) -----

    /// <summary>
    /// Soloed tracks (zero or more). While any exist, preview audio plays only them and their Sub-Lanes (no voiceover); exports
    /// ignore solo.
    /// </summary>
    public IReadOnlyList<TimelineTrackModel> SoloedTracks => Document.Tracks.Where(t => t.IsSoloed).ToList();

    /// <summary>
    /// Toggles a track's solo. Alt-click (or exclusive solo in Settings) solos it alone, clearing others; again turns it off. Null row
    /// clears all solos. One undo step.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleSolo(Layer? row)
    {
        var alone = AppSettings.Current.SoloIsExclusive || IsAltHeld();
        var track = TrackOfRow(row);
        var soloed = Document.Tracks.Where(t => t.IsSoloed).ToList();
        if (track is null && soloed.Count == 0)
            return;

        var only = track is not null && soloed.Count == 1 && ReferenceEquals(soloed[0], track);
        var on = track is not null && (alone ? !only : !track.IsSoloed);
        if (!Edit(track is null ? "take every solo off" : on ? $"solo {track.DisplayName}" : $"take the solo off {track.DisplayName}", edit =>
            {
                foreach (var other in soloed.Where(t => track is null || (alone && !ReferenceEquals(t, track))))
                    edit.SetTrack(other, soloed: false);
                if (track is not null)
                    edit.SetTrack(track, soloed: on);
            }))
        {
            return;
        }

        OnPropertyChanged(nameof(SoloedTracks));
        var now = SoloedTracks;
        StatusText = now.Count == 0 ? "Every track is heard again."
            : now.Count == 1 ? $"Playing {now[0].DisplayName} on its own. Press its button again to stop; click another's to hear them together (Alt-click: alone)."
            : $"Playing {string.Join(", ", now.Select(t => t.DisplayName))} together, without the rest. Alt-click a track's button to hear it alone.";
    }
}
