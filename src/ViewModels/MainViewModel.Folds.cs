using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// Reversible dead-air folds: a restored stretch stays tracked (TimelineDocument.RestoredFolds) and can be re-folded as the same fold
// (Hide pill, "Hide / Fold Dead Air Segment"); a clip edge at a folded seam reveals the fold when dragged outward or folds more of
// the clip when dragged inward. One undo step each. A fold keeps a stable id through fold/restore/resize so the timeline pairs
// FoldSeams() with RestoredStretches() for animation. Ripple trims at a seam stay ripple; only plain edge drags are fold-aware
// (TrimClipStart, CommitClipMove).
public partial class MainViewModel
{
    /// <summary>
    /// Re-foldable restored stretches for Hide pills: track, range (s) and record (<see cref="RestoredFold.Id"/> = stable fold id).
    /// Stale entries are skipped. Single pass over clips.
    /// </summary>
    public IEnumerable<(Guid TrackId, double From, double To, RestoredFold Restored)> RestoredStretches()
    {
        foreach (var (restored, trackId, from, to) in Document.RestoredSpans())
            yield return (trackId, from, to, restored);
    }

    /// <summary>
    /// Restored stretches a clip spans (context menu "Hide / Fold Dead Air Segment"): on its track, an ancestor track, or previously
    /// folded from all tracks.
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

    /// <summary>Re-folds a restored stretch under its original fold id. One undo step.</summary>
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
    /// Fold at a clip edge and its side: clip start at a seam = fold tail; clip end at a seam (or at the next adjacent clip's start)
    /// = fold head. Null when none or the track is locked.
    /// </summary>
    public (FoldedSegment Fold, FoldSide Side)? FoldAtEdge(Layer clip, bool startEdge)
    {
        if (Document.Folds is not { Count: > 0 } folds || ClipOf(clip) is not { } model || Document.TrackOf(model) is not { IsLocked: false } track)
            return null;

        // An adjacent following clip shares this clip's end seam.
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
    /// Drag limits for a fold edge (live preview): outward <c>Reveal</c> up to the fold length, inward <c>Fold</c> up to clip length
    /// minus the minimum clip (frame-quantized; 0 if already minimal). Null when not at a fold (<see cref="FoldAtEdge"/>). The commit
    /// clamps to these.
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
    /// Commits a fold-edge drag to <paramref name="newEdge"/> s: outward reveals that much of the fold (fully = restore), inward folds
    /// more of the clip. Clip id preserved. One undo step; false when unchanged (no fold, under half a frame, or refused with reason).
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

        // Under half a frame is a no-op (a too-short clip is an Edit refusal, already reported).
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

    /// <summary>Rounds a length up to the document frame grid (unchanged when not quantizing).</summary>
    private double OnFrameAtLeast(double seconds)
    {
        if (!Document.QuantizeToFrames)
            return seconds;

        var rate = Document.FrameRate;
        return rate.Quantize(rate.SecondsFromFrame((long)Math.Ceiling(seconds * rate.Fps - 1e-6)));
    }

    /// <summary>Whether a track (by id) is nested under another at any depth.</summary>
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
