using System.Windows.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The time bars' edits beyond moving a clip and trimming its end (see TimelineTrack): trimming its start, ripple (the
// later clips follow), roll (two clips' cut moved together), slip (what of its file a clip plays), ripple delete; a
// clip's gain line; its fades and their transition styles (picture and sound, the main video's clips too); crossfades
// between clips that meet on a track and between clips on separate tracks that overlap; and solo as a set of tracks.
// None of them touches the main video's clips or the video's own audio tracks, which keep their own edits.
public partial class MainViewModel
{
    /// <summary>The longest crossfade made by letting a dragged clip go over the one it meets, in seconds; more is a move over it.</summary>
    private const double LongestDragCrossfade = 2;

    /// <summary>Whether a clip plays a stretch of a file (a video or a sound), so that trimming its start moves into that file.</summary>
    private static bool PlaysFile(Layer clip) => clip.IsVideoFile || clip.IsAudio;

    private static double MediaSpeed(Layer clip) => clip.HasSpeed ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

    /// <summary>How much more of its file there is after a clip's end, in seconds of the timeline; endless for a picture or words.</summary>
    private static double MediaAfter(Layer clip) => PlaysFile(clip) && clip.MediaDuration > 0.05
        ? Math.Max((clip.MediaDuration - clip.MediaOffset - clip.Duration * MediaSpeed(clip)) / MediaSpeed(clip), 0)
        : double.PositiveInfinity;

    /// <summary>How much of its file there is before a clip's start, in seconds of the timeline; endless for a picture or words.</summary>
    private static double MediaBefore(Layer clip) => PlaysFile(clip) ? clip.MediaOffset / MediaSpeed(clip) : double.PositiveInfinity;

    /// <summary>
    /// The clips a ripple from a moment moves along: those that begin at or after it, on the clip's own track or (Settings,
    /// Ripple affects all tracks) on every track. The main video and the video's own sound are never among them.
    /// </summary>
    private List<Layer> RippleClips(Layer clip, double from) =>
        [.. Layers.Where(l => !ReferenceEquals(l, clip) && l is { HasTiming: true, IsMainVideo: false, IsEmptyTrack: false, IsDeleted: false }
            && (AppSettings.Current.RippleAllTracks || l.TrackId == clip.TrackId) && l.StartTime >= from - 0.001)];

    /// <summary>
    /// Moves clips along the timeline by the same amount; held, going earlier, so that none goes before the start or
    /// onto a clip of its track that stays where it is. Returns how far they went.
    /// </summary>
    private double RippleShift(List<Layer> moving, double delta)
    {
        if (delta < 0)
        {
            foreach (var clip in moving)
            {
                delta = Math.Max(delta, -clip.StartTime);
                foreach (var still in GetTrackClips(clip))
                {
                    if (!moving.Contains(still) && still is { IsEmptyTrack: false } && still.StartTime < clip.StartTime && still.Duration > 0.001)
                        delta = Math.Max(delta, still.StartTime + still.Duration - clip.StartTime);
                }
            }
        }

        foreach (var clip in moving)
            clip.StartTime = Math.Round(clip.StartTime + delta, 3);
        return delta;
    }

    /// <summary>
    /// The left edge of a clip dragged: where it starts, and (a video or a sound) where in its file it starts, with what it
    /// shows staying where it was on the timeline. Rippled (Shift), its start stays and the clips after it follow its end.
    /// </summary>
    public void TrimClipStart(Layer clip, double start, bool ripple)
    {
        if (clip.Duration <= 0.001)
        {
            StatusText = $"{clip.Name} runs to the end of the video: give it a length (drag its right edge) before trimming its start.";
            return;
        }

        var delta = start - clip.StartTime;
        delta = Math.Max(delta, -MediaBefore(clip));
        delta = Math.Min(delta, clip.Duration - 0.05);
        if (!ripple)
        {
            // Not before the timeline's start, nor onto the clip before it on its track.
            delta = Math.Max(delta, -clip.StartTime);
            foreach (var other in GetTrackClips(clip))
            {
                if (!ReferenceEquals(other, clip) && other.StartTime < clip.StartTime && other.Duration > 0.001)
                    delta = Math.Max(delta, other.StartTime + other.Duration - clip.StartTime);
            }
        }

        if (Math.Abs(delta) < 0.0005)
        {
            if (MediaBefore(clip) < 0.001)
                StatusText = $"{clip.Name} already starts at the beginning of its file.";
            return;
        }

        Checkpoint($"trim the start of {clip.Name}");
        using (DeferCommand())
        {
            var moving = ripple ? RippleClips(clip, clip.StartTime + clip.Duration) : [];
            if (PlaysFile(clip))
                clip.MediaOffset = Math.Max(Math.Round(clip.MediaOffset + delta * MediaSpeed(clip), 3), 0);

            // Its keyframes are counted from where it begins: they stay on the frames they were set on.
            clip.ShiftKeys(-delta);
            if (ripple)
            {
                clip.Duration = Math.Round(clip.Duration - delta, 3);
                RippleShift(moving, -delta);
            }
            else
            {
                (clip.StartTime, clip.Duration) = (Math.Round(clip.StartTime + delta, 3), Math.Round(clip.Duration - delta, 3));
            }

            GenerateCommand();
        }

        Log($"Trimmed the start of {clip.Name}{(ripple ? ", rippled" : "")}: it now runs {TimeDisplay.Format(clip.StartTime)} to {TimeDisplay.Format(clip.StartTime + clip.Duration)}");
    }

    /// <summary>The right edge of a clip dragged with Shift: its new length, and the clips after it follow its end.</summary>
    public void RippleClipEnd(Layer clip, double length)
    {
        if (clip.Duration <= 0.001)
            return;

        var change = Math.Min(Math.Max(length, 0.05) - clip.Duration, MediaAfter(clip));
        if (Math.Abs(change) < 0.0005)
            return;

        Checkpoint($"ripple the end of {clip.Name}");
        using (DeferCommand())
        {
            var moving = RippleClips(clip, clip.StartTime + clip.Duration);
            clip.Duration = Math.Round(clip.Duration + change, 3);
            var moved = RippleShift(moving, change);
            if (Math.Abs(moved - change) > 0.001)
                StatusText = "Some clips could not follow all the way: they would have run onto clips of their tracks that stay.";
            GenerateCommand();
        }

        Log($"Rippled the end of {clip.Name}: {Followed(change)}");

        static string Followed(double by) => by > 0 ? $"what follows moved {TimeDisplay.Format(by)} later" : $"what follows moved {TimeDisplay.Format(-by)} earlier";
    }

    /// <summary>
    /// A roll: the cut between two clips that meet moved to a new moment; the first ends there and the second starts there,
    /// each playing more or less of its file, and nothing else on the timeline moves.
    /// </summary>
    public void RollCut(Layer before, Layer after, double cut)
    {
        var delta = cut - (before.StartTime + before.Duration);
        delta = Math.Clamp(delta, -(before.Duration - 0.05), after.Duration - 0.05);
        delta = Math.Min(delta, MediaAfter(before));
        delta = Math.Max(delta, -MediaBefore(after));
        if (Math.Abs(delta) < 0.0005)
        {
            StatusText = "The cut cannot move that way: one of the clips has no more of its file to show.";
            return;
        }

        Checkpoint($"roll the cut between {before.Name} and {after.Name}");
        using (DeferCommand())
        {
            before.Duration = Math.Round(before.Duration + delta, 3);
            if (PlaysFile(after))
                after.MediaOffset = Math.Max(Math.Round(after.MediaOffset + delta * MediaSpeed(after), 3), 0);
            after.ShiftKeys(-delta);
            (after.StartTime, after.Duration) = (Math.Round(after.StartTime + delta, 3), Math.Round(after.Duration - delta, 3));
            GenerateCommand();
        }

        Log($"Rolled the cut between {before.Name} and {after.Name} to {TimeDisplay.Format(after.StartTime)}");
    }

    /// <summary>
    /// A slip: what of its file a clip plays moved by so many seconds of the timeline (later in the file for a drag to the
    /// left), the clip staying where it is, as long as it is.
    /// </summary>
    public void SlipClip(Layer clip, double seconds)
    {
        if (!PlaysFile(clip))
            return;

        var speed = MediaSpeed(clip);
        var offset = clip.MediaOffset - seconds * speed;
        var latest = clip.MediaDuration > 0.05 && clip.Duration > 0.001 ? Math.Max(clip.MediaDuration - clip.Duration * speed, 0) : double.PositiveInfinity;
        offset = Math.Round(Math.Clamp(offset, 0, latest), 3);
        if (Math.Abs(offset - clip.MediaOffset) < 0.0005)
        {
            StatusText = $"{clip.Name} cannot slip further: it would run past {(offset <= 0.0005 ? "the start" : "the end")} of its file.";
            return;
        }

        Checkpoint($"slip {clip.Name}");
        clip.MediaOffset = offset;
        Log($"Slipped {clip.Name}: it now plays its file from {TimeDisplay.Format(offset)}");
    }

    /// <summary>Shift+Delete: the clip goes (to the recycle bin), and what came after it on its track (or every track) closes the gap.</summary>
    public bool RippleDelete(object? selected)
    {
        if (selected is not Layer { IsRemovable: true } clip || !Layers.Contains(clip))
            return DeleteClip(selected);

        var (start, end) = clip.GetSpan(SequenceSeconds);
        Checkpoint($"ripple delete {clip.Name}");
        using (DeferCommand())
        {
            var moving = RippleClips(clip, end);
            MoveToBin(clip);
            RippleShift(moving, -(end - start));
            GenerateCommand();
        }

        Log($"Ripple deleted clip: {clip.Name} (in the recycle bin; Ctrl+Z brings it back, and the clips after it)");
        return true;
    }

    /// <summary>
    /// A clip let go after a move along its track. Let go a little over a clip it meets there (up to two seconds, and up
    /// to half of either), the two are put end to end with a crossfade of that much at their cut instead.
    /// </summary>
    public void CommitClipMove(Layer clip, double start, double length)
    {
        Checkpoint($"move {clip.Name}");
        string? crossfade = null;
        if (Math.Abs(length - clip.Duration) < 0.011 && clip.Duration > 0.001)
        {
            foreach (var other in GetTrackClips(clip))
            {
                if (ReferenceEquals(other, clip) || other.Duration <= 0.001 || other.IsEmptyTrack)
                    continue;

                var overlap = Math.Min(start + length, other.StartTime + other.Duration) - Math.Max(start, other.StartTime);
                if (overlap <= 0.02 || overlap > Math.Min(LongestDragCrossfade, Math.Min(length, other.Duration) / 2))
                    continue;

                Layer later;
                if (start > other.StartTime)
                    (start, later) = (other.StartTime + other.Duration, clip);
                else
                    (start, later) = (other.StartTime - length, other);
                later.TransitionIn = Math.Round(overlap, 2);
                crossfade = $"Crossfade of {TimeDisplay.Format(overlap)} between {clip.Name} and {other.Name}";
                break;
            }
        }

        (clip.StartTime, clip.Duration) = (Math.Round(Math.Max(start, 0), 2), Math.Round(length, 2));
        if (crossfade is not null)
            Log(crossfade + " (let go over it: they now meet, and fade into each other at the cut)");
    }

    /// <summary>The clip just before a clip on its track: the one whose end meets its start; null when there is none.</summary>
    public Layer? ClipBefore(Layer clip) => clip.Duration <= 0.001 ? null
        : GetTrackClips(clip).FirstOrDefault(c => !ReferenceEquals(c, clip) && c.Duration > 0.001 && !c.IsEmptyTrack && Math.Abs(c.StartTime + c.Duration - clip.StartTime) < 0.011);

    /// <summary>A crossfade of so many seconds from the clip before this one (0: a plain cut again).</summary>
    public void SetCrossfade(Layer clip, double seconds)
    {
        seconds = Math.Round(Math.Clamp(seconds, 0, 10), 2);
        if (seconds > 0 && ClipBefore(clip) is null)
        {
            StatusText = $"{clip.Name} does not meet the clip before it: put them end to end first (snapping puts one where the other ends).";
            return;
        }

        Checkpoint(seconds > 0 ? $"crossfade into {clip.Name}" : $"remove the crossfade into {clip.Name}");
        clip.TransitionIn = seconds;
        Log(seconds > 0 ? $"Crossfade of {TimeDisplay.Format(seconds)} into {clip.Name}, centred on the cut" : $"Removed the crossfade into {clip.Name}");
    }

    /// <summary>A clip's gain line dragged: its gain (dB). Its fades are its corner handles' (SetClipFade).</summary>
    public void SetClipGain(Layer clip, double gainDb)
    {
        Checkpoint($"change the gain of {clip.Name}");
        clip.GainDb = Math.Round(gainDb, 1);
        StatusText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{clip.Name}: {clip.GainDb:+0.0;-0.0;0} dB");
    }

    // ----- Fades and transitions -----
    // Every clip fades on its own, picture and sound, over whatever is under it: a clip on a track of its own (where a
    // dropped clip goes) needs no neighbour on its track to come on or go. The picture's side has a style
    // (TransitionStyle: dissolve, wipe, slide, zoom, blur); its sound fades with it whatever the style.

    /// <summary>How long a clip is on the timeline, in seconds (one with no length of its own: to the end of the sequence).</summary>
    private double ClipLength(Layer clip) => clip.Duration > 0.001 ? clip.Duration : Math.Max(SequenceSeconds - clip.StartTime, 0);

    /// <summary>A clip's fade handle let go: its fade in (or out) is so many seconds now, no more than the clip leaves beside its other fade.</summary>
    public void SetClipFade(Layer clip, bool fadeIn, double seconds)
    {
        var room = Math.Max(ClipLength(clip) - (fadeIn ? clip.FadeOutSeconds : clip.FadeInSeconds), 0);
        seconds = Math.Round(Math.Clamp(seconds, 0, Math.Min(room, 60)), 2);
        if (Math.Abs(seconds - (fadeIn ? clip.FadeInSeconds : clip.FadeOutSeconds)) < 0.005)
            return;

        Checkpoint($"fade {(fadeIn ? "in" : "out")} {clip.Name}");
        if (fadeIn)
            clip.FadeInSeconds = seconds;
        else
            clip.FadeOutSeconds = seconds;
        StatusText = FadeText(clip.Name, fadeIn, seconds, fadeIn ? clip.FadeInStyle : clip.FadeOutStyle);
    }

    /// <summary>How the picture of a clip comes on (or goes) over its fade. Chosen for a clip with no fade there, it gets one of the default length.</summary>
    public void SetClipFadeStyle(Layer clip, bool fadeIn, TransitionStyle style)
    {
        var seconds = fadeIn ? clip.FadeInSeconds : clip.FadeOutSeconds;
        if ((fadeIn ? clip.FadeInStyle : clip.FadeOutStyle) == style && seconds >= 0.01)
            return;

        Checkpoint($"{TransitionStyles.Label(style)} for the fade {(fadeIn ? "in" : "out")} of {clip.Name}");
        if (seconds < 0.01)
            seconds = DefaultFade(ClipLength(clip) - (fadeIn ? clip.FadeOutSeconds : clip.FadeInSeconds));
        if (fadeIn)
            (clip.FadeInStyle, clip.FadeInSeconds) = (style, seconds);
        else
            (clip.FadeOutStyle, clip.FadeOutSeconds) = (style, seconds);
        Log(FadeText(clip.Name, fadeIn, seconds, style));
    }

    /// <summary>The length a fade gets when a style is chosen for a clip with none: the default transition length (Settings), in what room there is.</summary>
    private static double DefaultFade(double room) => Math.Round(Math.Clamp(AppSettings.Current.DefaultTransitionSeconds, 0, Math.Max(room, 0)), 2);

    /// <summary>A main video clip's fade handle let go (its index as GetMainClips has it).</summary>
    public void SetMainClipFade(int index, bool fadeIn, double seconds)
    {
        if (FindMainClip(index) is not { } clip)
            return;

        var room = Math.Max(clip.End - clip.Start - (fadeIn ? clip.FadeOut : clip.FadeIn), 0);
        seconds = Math.Round(Math.Clamp(seconds, 0, Math.Min(room, 60)), 2);
        if (Math.Abs(seconds - (fadeIn ? clip.FadeIn : clip.FadeOut)) < 0.005)
            return;

        Checkpoint($"fade {(fadeIn ? "in" : "out")} the main video");
        using (DeferCommand())
        {
            SetMainFades(index, fadeIn ? seconds : clip.FadeIn, fadeIn ? clip.FadeOut : seconds);
            GenerateCommand();
        }

        StatusText = FadeText(_mainVideoRow.Name, fadeIn, seconds, fadeIn ? clip.FadeInStyle : clip.FadeOutStyle);
    }

    /// <summary>How the main video's picture comes on (or goes) over a clip's fade.</summary>
    public void SetMainClipFadeStyle(int index, bool fadeIn, TransitionStyle style)
    {
        if (FindMainClip(index) is not { } clip)
            return;

        var length = clip.End - clip.Start;
        var (fadeInSeconds, fadeOutSeconds) = (clip.FadeIn, clip.FadeOut);
        if (fadeIn && fadeInSeconds < 0.01)
            fadeInSeconds = DefaultFade(length - fadeOutSeconds);
        else if (!fadeIn && fadeOutSeconds < 0.01)
            fadeOutSeconds = DefaultFade(length - fadeInSeconds);

        Checkpoint($"{TransitionStyles.Label(style)} for the fade {(fadeIn ? "in" : "out")} of the main video");
        using (DeferCommand())
        {
            SetMainFades(index, fadeInSeconds, fadeOutSeconds, fadeIn ? style : clip.FadeInStyle, fadeIn ? clip.FadeOutStyle : style);
            GenerateCommand();
        }

        Log(FadeText(_mainVideoRow.Name, fadeIn, fadeIn ? fadeInSeconds : fadeOutSeconds, style));
    }

    /// <summary>Sets a main video clip's fades (and styles, when given) without an undo step of its own.</summary>
    private void SetMainFades(int index, double fadeIn, double fadeOut, TransitionStyle? inStyle = null, TransitionStyle? outStyle = null)
    {
        if (index == 0)
        {
            var main = _mainVideoRow;
            (main.FadeInSeconds, main.FadeOutSeconds) = (fadeIn, fadeOut);
            (main.FadeInStyle, main.FadeOutStyle) = (inStyle ?? main.FadeInStyle, outStyle ?? main.FadeOutStyle);
        }
        else if (index - 1 < _mainPieces.Count)
        {
            var piece = _mainPieces[index - 1];
            _mainPieces[index - 1] = piece with
            {
                FadeIn = fadeIn, FadeOut = fadeOut, FadeInStyle = inStyle ?? piece.FadeInStyle, FadeOutStyle = outStyle ?? piece.FadeOutStyle,
            };
        }

        InvalidateMainClips();
    }

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
        var sequence = SequenceSeconds;
        var (from, to) = clip.GetSpan(sequence);
        (Layer Other, double Seconds)? best = null;
        foreach (var other in Layers)
        {
            if (ReferenceEquals(other, clip) || other.TrackId == clip.TrackId || !other.HasTiming || other.IsDeleted || other.IsEmptyTrack
                || other.IsHidden || other.IsAudio != clip.IsAudio || other.IsAdjustment != clip.IsAdjustment)
            {
                continue;
            }

            var (otherFrom, otherTo) = other.GetSpan(sequence);
            var seconds = atStart
                ? otherFrom < from - 0.01 && otherTo > from + 0.02 ? Math.Min(otherTo, to) - from : 0
                : otherTo > to + 0.01 && otherFrom < to - 0.02 ? to - Math.Max(otherFrom, from) : 0;
            if (seconds > 0.02 && seconds > (best?.Seconds ?? 0))
                best = (other, Math.Round(seconds, 2));
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
        if (OverlapAt(clip, atStart) is not var (other, seconds))
        {
            StatusText = $"No clip on another track is on while {clip.Name} {(atStart ? "starts" : "ends")}: put one there first.";
            return;
        }

        Checkpoint($"crossfade {clip.Name} with {other.Name}");
        double Room(Layer layer, bool fadeIn) => Math.Min(seconds, Math.Max(ClipLength(layer) - (fadeIn ? layer.FadeOutSeconds : layer.FadeInSeconds), 0));
        if (atStart)
            (clip.FadeInSeconds, other.FadeOutSeconds) = (Room(clip, true), Room(other, false));
        else
            (clip.FadeOutSeconds, other.FadeInSeconds) = (Room(clip, false), Room(other, true));
        Log(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Crossfade of {seconds:0.##} s between {(atStart ? other.Name : clip.Name)} and {(atStart ? clip.Name : other.Name)}, across separate tracks"));
    }

    /// <summary>
    /// Readies the copies a graph is built from for their crossfades (never the clips themselves). Each pair that meets
    /// with one is laid over each other across the cut: the earlier runs on past it and the later starts before it, into
    /// what their files have there (as much as they have, up to half the crossfade each); the later one's picture comes
    /// up over the earlier's (an opacity ramp, as keyframes; the later is laid above), and their sounds fade out and in
    /// across the same stretch. Where neither file has anything beyond the cut, both dip through what is under them.
    /// </summary>
    private static void ApplyCrossfades(IReadOnlyCollection<Layer> copies)
    {
        foreach (var after in copies.Where(c => c.TransitionIn > 0.01 && c.Duration > 0.001).ToList())
        {
            var before = copies.FirstOrDefault(c => !ReferenceEquals(c, after) && c is { IsRemovable: true, IsEmptyTrack: false } && c.TrackId == after.TrackId
                && c.IsAudio == after.IsAudio && c.Duration > 0.001 && Math.Abs(c.StartTime + c.Duration - after.StartTime) < 0.011);
            if (before is null)
                continue;

            var length = Math.Min(after.TransitionIn, Math.Min(before.Duration, after.Duration));
            var (late, early) = (Math.Min(length / 2, MediaAfter(before)), Math.Min(length / 2, MediaBefore(after)));
            if (late + early < 0.04)
            {
                // Neither file has anything beyond the cut: both fade, picture and sound, through what is under them.
                before.FadeOutSeconds = Math.Max(before.FadeOutSeconds, length / 2);
                after.FadeInSeconds = Math.Max(after.FadeInSeconds, length / 2);
                continue;
            }

            before.Duration += late;
            if (PlaysFile(after))
                after.MediaOffset = Math.Max(after.MediaOffset - early * MediaSpeed(after), 0);
            after.ShiftKeys(early);
            (after.StartTime, after.Duration) = (after.StartTime - early, after.Duration + early);
            // The later one lies over the earlier (LayLaterClipsOver) and comes on over it by its own fade, in its own
            // style; the earlier's picture stays until it ends under it, only its sound fading out.
            var window = late + early;
            after.FadeInSeconds = Math.Max(after.FadeInSeconds, window);
            before.FadeOutSeconds = Math.Max(before.FadeOutSeconds, window);
            before.PictureFadeOutOff = true;
        }
    }

    /// <summary>
    /// Readies the copies for crossfades between clips on separate tracks (Crossfade with Overlapping Clip, or two fades
    /// dragged to the same stretch): where one clip fades out while another fades in over exactly the same stretch, the
    /// one laid lower keeps its picture through it and only its sound fades. The upper one's fade is the picture's side
    /// of the crossfade, so the two never dip through what is under both. <paramref name="stack"/> is bottom first.
    /// </summary>
    private static void PairLayerCrossfades(List<Layer> stack)
    {
        static bool Same(double a, double b) => Math.Abs(a - b) < 0.02;
        for (var lower = 0; lower < stack.Count; lower++)
        {
            var under = stack[lower];
            if (under is not { IsMainVideo: false, IsPicture: true } || under.Duration <= 0.001)
                continue;

            var (underIn, underOut) = (under.StartTime, under.StartTime + under.Duration);
            for (var upper = lower + 1; upper < stack.Count; upper++)
            {
                var over = stack[upper];
                if (over is not { IsMainVideo: false, IsPicture: true } || over.Duration <= 0.001 || over.TrackId == under.TrackId)
                    continue;

                var (overIn, overOut) = (over.StartTime, over.StartTime + over.Duration);

                // The upper one comes on as the lower one goes: the lower stays until it ends under it.
                if (under.FadeOutSeconds > 0.01 && Same(over.FadeInSeconds, under.FadeOutSeconds) && Same(overIn, underOut - under.FadeOutSeconds))
                    under.PictureFadeOutOff = true;

                // The upper one goes as the lower one comes on: the lower is there already, under it.
                if (under.FadeInSeconds > 0.01 && Same(over.FadeOutSeconds, under.FadeInSeconds) && Same(underIn, overOut - over.FadeOutSeconds))
                    under.PictureFadeInOff = true;
            }
        }
    }

    /// <summary>The copies of a stack, with each track's clips in the order they start: a later clip lies over an earlier one.</summary>
    private static void LayLaterClipsOver(List<Layer> stack)
    {
        for (var i = 0; i < stack.Count;)
        {
            var j = i + 1;
            while (j < stack.Count && !stack[i].IsMainVideo && !stack[j].IsMainVideo && stack[j].TrackId == stack[i].TrackId)
                j++;
            if (j - i > 1)
            {
                var run = stack.GetRange(i, j - i).OrderBy(l => l.StartTime).ToList();
                for (var k = 0; k < run.Count; k++)
                    stack[i + k] = run[k];
            }

            i = j;
        }
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
        return state with { AudioTracks = [.. state.AudioTracks.Where(t => indexes.Contains(t.Index))], LayerAudio = [], IncludeVoiceover = false };
    }
}
