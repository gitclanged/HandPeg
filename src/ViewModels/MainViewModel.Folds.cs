using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// Dead air both ways (4.4-BugHunt): a folded stretch put back stays marked (TimelineDocument.RestoredFolds) and can be
// folded out again as the same fold (the Hide pill, "Hide / Fold Dead Air Segment"), and a clip's edge at a folded seam
// opens the fold by as far as it is dragged away from the clip, or folds as much more of the clip into it when dragged
// into the clip (Q11). Each is one step of Undo. A fold keeps one id through fold, put back, fold again and every resize,
// so the timeline pairs FoldSeams() with RestoredStretches() by it to slide the one into the other. A ripple trim at a
// seam stays a ripple trim: only a plain edge drag is fold-aware (TrimClipStart, CommitClipMove).
public partial class MainViewModel
{
    /// <summary>
    /// The folded stretches put back that can be folded again, for the timeline's Hide pills: the track each lies on, where
    /// (seconds), and its record (whose <see cref="RestoredFold.Id"/> is the fold it was and will be). Those no longer there
    /// are left out. One look through the clips for them all.
    /// </summary>
    public IEnumerable<(Guid TrackId, double From, double To, RestoredFold Restored)> RestoredStretches()
    {
        foreach (var (restored, trackId, from, to) in Document.RestoredSpans())
            yield return (trackId, from, to, restored);
    }

    /// <summary>
    /// The stretches put back that a clip lies across (its right-click "Hide / Fold Dead Air Segment"): on its own track, on
    /// a track it lies under, or folded out of every track before.
    /// </summary>
    public IReadOnlyList<RestoredFold> RestoredFoldsOn(Layer clip)
    {
        if (Document.RestoredFolds is not { Count: > 0 } || ClipOf(clip) is not { } model || Document.TrackOf(model) is not { } track)
            return [];

        var on = new List<RestoredFold>();
        foreach (var (restored, trackId, from, to) in Document.RestoredSpans())
        {
            if (from < model.End - 1e-6 && to > model.Start + 1e-6 && (restored.AllTracks || trackId == track.Id || LiesUnder(track, trackId)))
                on.Add(restored);
        }
        return on;
    }

    /// <summary>Folds a stretch that was put back out of the timeline again, as the fold it was: one step of Undo.</summary>
    public bool HideRestoredStretch(RestoredFold restored)
    {
        FoldedSegment? folded = null;
        if (!Edit(string.Create(CultureInfo.InvariantCulture, $"hide {restored.Length:0.#} s of dead air again"), edit => folded = edit.FoldRestored(restored))
            || folded is null)
        {
            return false;
        }

        Log(string.Create(CultureInfo.InvariantCulture, $"Folded {folded.Length:0.#} s of dead air out again at {TimeDisplay.Format(folded.SeamIn(Document) ?? 0)}"));
        return true;
    }

    /// <summary>
    /// The fold at a clip's edge, and the side of it that edge opens or folds into: a clip's start at a seam that is its start
    /// (the fold's tail), or a clip's end at a seam that is its end, or that is the start of the clip after it on its track
    /// (the fold's head). Null when no fold lies there, or the clip's track is locked.
    /// </summary>
    public (FoldedSegment Fold, FoldSide Side)? FoldAtEdge(Layer clip, bool startEdge)
    {
        if (Document.Folds is not { Count: > 0 } folds || ClipOf(clip) is not { } model || Document.TrackOf(model) is not { IsLocked: false } track)
            return null;

        // The clip after this one on its track, where it starts as this one ends: its seam is this one's end too.
        var index = track.IndexOf(model);
        var next = !startEdge && index >= 0 && index + 1 < track.Clips.Count && Math.Abs(track.Clips[index + 1].Start - model.End) <= 1e-6
            ? track.Clips[index + 1]
            : null;
        foreach (var fold in folds)
        {
            if (startEdge ? !fold.AtEnd && fold.SeamClipId == model.Id
                          : fold.AtEnd ? fold.SeamClipId == model.Id : next is not null && fold.SeamClipId == next.Id)
            {
                return (fold, startEdge ? FoldSide.Tail : FoldSide.Head);
            }
        }
        return null;
    }

    /// <summary>
    /// How far a clip's edge at a fold may go, for the drag's live picture: outward, away from the clip (<c>Reveal</c>: the
    /// fold's length, which shows it all), and inward (<c>Fold</c>: the clip's length but the shortest clip, on frames; 0
    /// when it is that short already). Null when the edge is at no fold (<see cref="FoldAtEdge"/>). The edit holds an edge
    /// let go anywhere to these, and shows or folds at least the shortest clip.
    /// </summary>
    public (double Reveal, double Fold)? SeamEdgeRange(Layer clip, bool startEdge)
    {
        if (FoldAtEdge(clip, startEdge) is not { } found || ClipOf(clip) is not { } model)
            return null;

        var minimum = OnFrameAtLeast(TimelineDocument.MinimumClipSeconds);
        var sound = model.HasSound && model.AudioLinked ? model.AudioLeadInSeconds + model.AudioTailOutSeconds : 0;
        var room = model.Duration - OnFrameAtLeast(Math.Max(minimum, minimum - sound));
        return (found.Fold.Length, room >= minimum - 1e-6 ? room : 0);
    }

    /// <summary>
    /// A clip's edge at a fold let go at <paramref name="newEdge"/> seconds (Q11): dragged away from the clip, it shows as
    /// much of the fold there (all of it puts the fold back); dragged into the clip, it folds as much more of the clip into
    /// it. The clip keeps its id. One step of Undo; false when nothing changed (the edge is at no fold, it moved less than
    /// half a frame, or the edit was refused, with the reason in the status line).
    /// </summary>
    public bool TrimIntoSeam(Layer clip, bool startEdge, double newEdge)
    {
        if (!double.IsFinite(newEdge) || ClipOf(clip) is not { } model || FoldAtEdge(clip, startEdge) is not { } found)
            return false;

        var (fold, side) = found;
        var seconds = startEdge ? model.Start - newEdge : newEdge - model.End;
        if (Math.Abs(seconds) < 1e-6)
            return false;

        var (length, at) = (fold.Length, fold.SeamIn(Document) ?? 0);
        FoldedSegment? after = fold;
        var label = seconds > 0
            ? string.Create(CultureInfo.InvariantCulture, $"show {seconds:0.##} s of folded silence")
            : string.Create(CultureInfo.InvariantCulture, $"fold {-seconds:0.##} s more silence");
        if (!Edit(label, edit => after = edit.ResizeFold(fold, side, seconds)))
            return false;

        if (after is null)
        {
            Log(string.Create(CultureInfo.InvariantCulture, $"Put back {length:0.#} s at the cut at {TimeDisplay.Format(at)} (the edge of {clip.Name} dragged past the whole fold)"));
            return true;
        }

        // Less than half a frame is no change (a clip too short to fold more of is a refusal, which Edit has said).
        var changed = after.Length - length;
        if (Math.Abs(changed) < 1e-6)
        {
            StatusText = $"The edge of {clip.Name} moved less than half a frame: the folded silence stays as it is.";
            return false;
        }

        Log(changed < 0
            ? string.Create(CultureInfo.InvariantCulture, $"Showed {-changed:0.##} s of the silence folded at {(side == FoldSide.Tail ? "the start" : "the end")} of {clip.Name}; {after.Length:0.##} s stay folded")
            : string.Create(CultureInfo.InvariantCulture, $"Folded {changed:0.##} s more of {clip.Name} into the silence at its {(side == FoldSide.Tail ? "start" : "end")}; {after.Length:0.##} s are folded there"));
        return true;
    }

    /// <summary>A length on the frames of the document at or after it (as it is while the document does not quantize).</summary>
    private double OnFrameAtLeast(double seconds)
    {
        if (!Document.QuantizeToFrames)
            return seconds;

        var rate = Document.FrameRate;
        return rate.Quantize(rate.SecondsFromFrame((long)Math.Ceiling(seconds * rate.Fps - 1e-6)));
    }

    /// <summary>Whether a track lies under another (by id), through the tracks it lies under.</summary>
    private bool LiesUnder(TimelineTrackModel track, Guid ancestorId)
    {
        var (up, steps) = (track, 0);
        while (up.ParentTrackId is { } above && steps++ < Document.Tracks.Count)
        {
            if (above == ancestorId)
                return true;
            if (Document.Tracks.FirstOrDefault(t => t.Id == above) is not { } next)
                return false;
            up = next;
        }
        return false;
    }
}
