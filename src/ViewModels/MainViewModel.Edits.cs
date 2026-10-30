using System.Windows.Input;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The time bars' edits beyond moving a clip and trimming its end (see TimelineTrack): trimming its start, ripple (the
// later clips follow), roll (two clips' cut moved together), slip (what of its file a clip plays), ripple delete; a
// clip's gain line; its fades and their transition styles (picture and sound); crossfades between clips that meet on a
// track and between clips on separate tracks that overlap; and solo as a set of tracks. Each is an edit of the timeline
// document (Edit, 4.3C), the main video's clips the same as any other: no track is privileged. The video's own audio
// tracks keep their own edits (the Audio tab's).
public partial class MainViewModel
{
    /// <summary>
    /// The left edge of a clip dragged: where it starts, and (a video or a sound) where in its file it starts, with what it
    /// shows staying where it was on the timeline. Rippled, its start stays and the clips after it follow its end. At a seam
    /// of folded dead air (Q11, 4.4-BugHunt), a plain drag opens the fold or folds more of the clip into it instead
    /// (<see cref="TrimIntoSeam"/>); a ripple trim there stays a ripple trim.
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

    /// <summary>The right edge of a clip dragged with Ctrl: its new length, and the clips after it follow its end.</summary>
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

    /// <summary>
    /// A roll: the cut between two clips that meet moved to a new moment; the first ends there and the second starts there,
    /// each playing more or less of its file, and nothing else on the timeline moves.
    /// </summary>
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

    /// <summary>
    /// A slip: what of its file a clip plays moved by so many seconds of the timeline (later in the file for a drag to the
    /// left), the clip staying where it is, as long as it is.
    /// </summary>
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
    /// Shift+Delete: the clip goes (to the recycle bin), and what came after it on its track (or every track) closes the gap.
    /// One of a group of clips selected together (5.0): the whole group, as one step (<see cref="DeleteClips"/>).
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
            // Held back: something that stays (a clip of another track across the stretch, with Ripple affects all tracks)
            // would have run into what follows, or out of step with it.
            StatusText = moved < 0.001
                ? $"Deleted {view.Name} (in the recycle bin), but the gap stays: a clip of another track lies across it. Delete that part too (split it with [ and ]), then close the gap."
                : $"Deleted {view.Name} (in the recycle bin); what follows moved back {TimeDisplay.Format(moved)}, as far as the clips of other tracks let it.";
            return true;
        }

        Log($"Ripple deleted clip: {view.Name} (in the recycle bin; Ctrl+Z brings it back, and the clips after it)");
        return true;
    }

    /// <summary>
    /// A clip let go after a move along its track, or after its right edge was dragged (a new length). Let go a little over
    /// a clip it meets there (from a frame up to two seconds, and up to half of either), the two are put end to end with a
    /// crossfade of that much at their cut instead. A right edge at a seam of folded dead air (Q11, 4.4-BugHunt) opens the
    /// fold or folds more of the clip into it (<see cref="TrimIntoSeam"/>).
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
    /// "Show for the Whole Video" (a sound's "Play from Its Start to Its End"): the clip from the start of the timeline to
    /// its end, and one that plays a file from its file's start, for as much of the timeline as its file has. One step of Undo.
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
    /// Takes a stretch out of the timeline (what 4.2.0's cut list did to the stretches it left out): every clip of every
    /// unlocked track is split at its two ends, what lies between goes, and everything after it on every unlocked track moves
    /// back by its length. A clip whose edge lies too near an end to be split there goes whole with the stretch. In the
    /// edit given; refused as a whole when a track cannot follow (a locked one, which never moves, may then lie across it).
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
                    // Its sound too short on one side to be split (a J or L cut): it goes whole.
                }

                edit.DeleteClip(inside);
            }
        }

        if (tracks.FirstOrDefault() is { } first)
            edit.CloseGap(first, from, to, rippleAllUnlockedTracks: true);
    }

    /// <summary>The clip after one on its track that starts where it ends; null when there is none.</summary>
    private TimelineClip? After(TimelineClip clip)
    {
        if (Document.TrackOf(clip) is not { } track)
            return null;

        var index = track.IndexOf(clip);
        return index + 1 < track.Clips.Count && Math.Abs(track.Clips[index + 1].Start - clip.End) < 1e-6 ? track.Clips[index + 1] : null;
    }

    /// <summary>The clip just before a clip on its track: the one whose end meets its start; null when there is none.</summary>
    public Layer? ClipBefore(Layer clip)
    {
        if (ClipOf(clip) is not { } model || Document.TrackOf(model) is not { } track)
            return null;

        var index = track.IndexOf(model);
        return index > 0 && Math.Abs(track.Clips[index - 1].End - model.Start) < 1e-6 ? ViewOf(track.Clips[index - 1]) : null;
    }

    /// <summary>A crossfade of so many seconds from the clip before this one (0: a plain cut again).</summary>
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

    /// <summary>A clip's gain line dragged: its gain (dB). Its fades are its corner handles' (SetClipFade).</summary>
    public void SetClipGain(Layer clip, double gainDb)
    {
        if (ClipOf(clip) is not { } model)
            return;

        if (Edit($"change the gain of {clip.Name}", edit => edit.SetClipGain(model, Math.Round(gainDb, 1))))
            StatusText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{clip.Name}: {model.GainDb:+0.0;-0.0;0} dB");
    }

    // ----- Fades and transitions -----
    // Every clip fades on its own, picture and sound, over whatever is under it: a clip on a track of its own (where a
    // dropped clip goes) needs no neighbour on its track to come on or go. The picture's side has a style
    // (TransitionStyle: dissolve, wipe, slide, zoom, blur); its sound fades with it whatever the style.

    /// <summary>A clip's fade handle let go: its fade in (or out) is so many seconds now, no more than the clip leaves beside its other fade.</summary>
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

    /// <summary>How the picture of a clip comes on (or goes) over its fade. Chosen for a clip with no fade there, it gets one of the default length.</summary>
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

    /// <summary>The length a fade gets when a style is chosen for a clip with none: the default transition length (Settings), in what room there is.</summary>
    private static double DefaultFade(double room) => Math.Round(Math.Clamp(AppSettings.Current.DefaultTransitionSeconds, 0, Math.Max(room, 0)), 2);

    private static string FadeText(string name, bool fadeIn, double seconds, TransitionStyle style) => seconds < 0.005
        ? $"{name}: no fade {(fadeIn ? "in" : "out")}"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}: fade {(fadeIn ? "in" : "out")} {seconds:0.##} s, {TransitionStyles.Label(style)}");

    /// <summary>
    /// The clip on another track that is on while this clip's start (or end) comes, and for how long the two are on
    /// together there: the stretch a crossfade between them spans. Pictures pair with pictures and sounds with sounds;
    /// of several, the longest stretch. Null when no clip of another track lies across that moment.
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
    /// Crossfades a clip with the one on another track it overlaps at its start (or end): its fade there, and the other's
    /// fade on the opposite side, both made as long as the overlap. Whichever lies above carries the picture's side (its
    /// style); the one under it keeps its picture until it ends (see PairLayerCrossfades), and their sounds cross.
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

    // ----- Solo: a set of tracks -----

    private readonly HashSet<AudioTrack> _soloed = [];
    private bool _applyingSolo;

    /// <summary>The video's audio tracks played on their own: none, one (the player plays it), or several (mixed live without the rest).</summary>
    public IReadOnlyCollection<AudioTrack> SoloedTracks => _soloed;

    /// <summary>
    /// A track's Solo button: the track joins the set of those played on their own, or leaves it. Alt-click (or Settings,
    /// Solo is exclusive) plays it alone, the others' solo going off; pressed again, it goes off too.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleSolo(AudioTrack? track)
    {
        var alone = AppSettings.Current.SoloIsExclusive || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (track is null)
        {
            _soloed.Clear();
        }
        else if (alone)
        {
            var only = _soloed.Count == 1 && _soloed.Contains(track);
            _soloed.Clear();
            if (!only)
                _soloed.Add(track);
        }
        else if (!_soloed.Remove(track))
        {
            _soloed.Add(track);
        }

        ApplySolo();
    }

    /// <summary>Puts the set of soloed tracks into effect: the buttons, the player's track (one) or the live mix (several).</summary>
    private void ApplySolo()
    {
        _applyingSolo = true;
        try
        {
            SoloTrack = _soloed.Count == 1 ? _soloed.First() : null;
        }
        finally
        {
            _applyingSolo = false;
        }

        foreach (var track in AudioTracks)
            track.IsSolo = _soloed.Contains(track);
        OnPropertyChanged(nameof(SoloedTracks));
        InvalidateLiveFilter();
        if (_soloed.Count > 1)
            StatusText = $"Playing {string.Join(", ", _soloed.Select(t => t.Title))} together, without the rest. Alt-click a track's button to hear it alone.";
    }

    /// <summary>The live state with only the soloed tracks' sound, while several are soloed: the rest is left out of the mix.</summary>
    private SequenceExportState Soloed(SequenceExportState state)
    {
        if (_soloed.Count < 2)
            return state;

        var indexes = _soloed.Select(t => t.Index).ToHashSet();
        return state with { AudioTracks = [.. state.AudioTracks.Where(t => indexes.Contains(t.Index))], MixClipSound = false, IncludeVoiceover = false };
    }
}
