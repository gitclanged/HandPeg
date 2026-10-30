using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- The timeline lowered, as the GPU engine reads it (Pass 5.0-AudioAnd3D) -----
// The GPU scene (Services/Gpu/GpuScenePlan) is built on the very lowering the graph is written from (Lower, Lowered): the
// same primary chain, the same one piece and riders, the same blurred background and twins, the same stack in the same
// order. So the GPU engine and FFmpeg can never disagree about which layers exist. Nothing of the lowering is changed or
// made public here: this partial only hands over a read-only view of it, and the few facts of the document the graph
// does not need (which clip each layer was made from, and the chain's clips in time) that the GPU's picking and hints do.

/// <summary>
/// A state's timeline as <see cref="FilterGraphBuilder"/> lowers it, for the GPU scene (<c>GpuScenePlan.Create</c>): the
/// lowering's own lists, shared with every graph built from the same document (read them, never change them), and which
/// clip of the document each layer of the stack stands for.
/// </summary>
internal sealed class GpuSceneLowering
{
    public required double SequenceSeconds { get; init; }

    /// <summary>The primary chain's clips on the sequence, crossfades laid out: the stream every piece of the main video is cut from.</summary>
    public required IReadOnlyList<MainClip> MainClips { get; init; }

    public required IReadOnlyList<MainCrossfade> MainCrossfades { get; init; }

    /// <summary>The chain's clips its one piece (<see cref="MainLayer"/>) is drawn for.</summary>
    public required IReadOnlyList<MainClip> CenterClips { get; init; }

    public required IReadOnlySet<Layer> Riders { get; init; }

    public required IReadOnlyList<MainClip> BlurClips { get; init; }

    public required IReadOnlyList<MainClip> ForcedBlurClips { get; init; }

    public required IReadOnlyDictionary<Layer, (double Left, double Top, double Right, double Bottom)> StreamCrops { get; init; }

    public required IReadOnlySet<Layer> BlurTwins { get; init; }

    public required Layer MainLayer { get; init; }

    /// <summary>The pictures bottom first, the main layer at its track's place, each twin just under its video.</summary>
    public required IReadOnlyList<Layer> Stack { get; init; }

    public required IReadOnlyList<EffectInstance> SequenceEffects { get; init; }

    /// <summary>The clip each layer of <see cref="Stack"/> was made from (a twin: its video's clip); the main layer's is the chain's first.</summary>
    public required IReadOnlyDictionary<Layer, TimelineClip> ClipOf { get; init; }

    /// <summary>Where each layer's clip is among the document's tracks (0 the bottom), for the 3D view's labels and picking.</summary>
    public required IReadOnlyDictionary<Layer, int> TrackOf { get; init; }

    /// <summary>The primary chain's clips as the document has them, in the order they start (the clip under a moment of the one piece).</summary>
    public required IReadOnlyList<TimelineClip> ChainClips { get; init; }

    /// <summary>The track the primary chain is on, among the document's tracks; -1 with no chain.</summary>
    public required int PrimaryTrackIndex { get; init; }
}

public static partial class FilterGraphBuilder
{
    /// <summary>A state's timeline lowered (from the document's cache, as every graph of it is), as the GPU scene reads it.</summary>
    internal static GpuSceneLowering LowerForGpu(SequenceExportState state)
    {
        var lowered = Lower(state);
        var document = state.Timeline;

        // The primary chain as LowerAfresh finds it: the lowest picture track with a clip that can ride it, and the clips
        // of that track that ride it with that one.
        var primary = Clean(state.HasSource ? state.LocalMediaPath : "");
        var (primaryIndex, chain) = (-1, new List<TimelineClip>());
        if (primary.Length > 0)
        {
            for (var t = 0; t < document.Tracks.Count && primaryIndex < 0; t++)
            {
                var track = document.Tracks[t];
                if (!ShowsPictures(track))
                    continue;
                TimelineClip? anchor = null;
                foreach (var clip in track.Clips)
                {
                    if (!clip.IsHidden && CanRide(clip, primary))
                    {
                        anchor = clip;
                        break;
                    }
                }

                if (anchor is null)
                    continue;
                primaryIndex = t;
                foreach (var clip in track.Clips)
                {
                    if (ReferenceEquals(clip, anchor) || (!clip.IsHidden && CanRide(clip, primary)))
                        chain.Add(clip);
                }
            }
        }

        chain.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var (clipOf, trackOf) = MatchClips(lowered, document, chain, primaryIndex);
        return new GpuSceneLowering
        {
            SequenceSeconds = lowered.SequenceSeconds,
            MainClips = lowered.MainClips,
            MainCrossfades = lowered.MainCrossfades,
            CenterClips = lowered.CenterClips,
            Riders = lowered.Riders,
            BlurClips = lowered.BlurClips,
            ForcedBlurClips = lowered.ForcedBlurClips,
            StreamCrops = lowered.StreamCrops,
            BlurTwins = lowered.BlurTwins,
            MainLayer = lowered.MainLayer,
            Stack = lowered.Stack,
            SequenceEffects = lowered.SequenceEffects,
            ClipOf = clipOf,
            TrackOf = trackOf,
            ChainClips = chain,
            PrimaryTrackIndex = primaryIndex,
        };
    }

    /// <summary>
    /// The clip each layer of the stack was made from. A layer that carries its clip's id (<see cref="Layer.ClipId"/>) is
    /// found by it; otherwise as the lowering made it: a layer is numbered by its track (MakeLayer, MakeRider: the track's
    /// place + 1), a track's layers are stacked in the order of its clips, and a layer starts where its clip does, or up
    /// to half its crossfade sooner (LayOverAcrossCut). A twin is its video's (the layer after it). A layer none of these
    /// find is left out (its picture is still drawn; it only has no clip to be picked by).
    /// </summary>
    private static (Dictionary<Layer, TimelineClip> ClipOf, Dictionary<Layer, int> TrackOf) MatchClips(Lowered lowered, TimelineDocument document,
        List<TimelineClip> chain, int primaryIndex)
    {
        var clipOf = new Dictionary<Layer, TimelineClip>(ReferenceEqualityComparer.Instance);
        var trackOf = new Dictionary<Layer, int>(ReferenceEqualityComparer.Instance);
        var cursors = new int[document.Tracks.Count];
        var stack = lowered.Stack;

        for (var n = 0; n < stack.Count; n++)
        {
            var layer = stack[n];
            if (lowered.BlurTwins.Contains(layer))
                continue;

            if (layer.IsMainVideo)
            {
                if (chain.Count > 0)
                    clipOf[layer] = chain[0];
                trackOf[layer] = primaryIndex;
                continue;
            }

            if (layer.ClipId != Guid.Empty && document.FindClip(layer.ClipId) is { } own)
            {
                clipOf[layer] = own;
                for (var t = 0; t < document.Tracks.Count; t++)
                {
                    if (document.Tracks[t].IndexOf(own) >= 0)
                    {
                        trackOf[layer] = t;
                        break;
                    }
                }

                continue;
            }

            var index = layer.TrackId - 1;
            if (index < 0 || index >= document.Tracks.Count)
                continue;

            trackOf[layer] = index;
            var clips = document.Tracks[index].Clips;
            for (var c = cursors[index]; c < clips.Count; c++)
            {
                var clip = clips[c];
                var kindFits = clip.ContentKind == layer.Kind
                               || (clip.ContentKind == LayerKind.MainVideo && layer.Kind is LayerKind.VideoFile or LayerKind.Video);
                if (kindFits && Math.Abs(clip.Start - layer.StartTime) <= Math.Max(clip.TransitionIn / 2, 0) + 0.002)
                {
                    clipOf[layer] = clip;
                    cursors[index] = c + 1;
                    break;
                }
            }
        }

        // A twin is laid just under its video: it is that video's clip's.
        for (var n = 0; n + 1 < stack.Count; n++)
        {
            if (!lowered.BlurTwins.Contains(stack[n]))
                continue;
            var owner = stack[n + 1];
            if (clipOf.TryGetValue(owner, out var clip))
                clipOf[stack[n]] = clip;
            if (trackOf.TryGetValue(owner, out var track))
                trackOf[stack[n]] = track;
        }

        return (clipOf, trackOf);
    }
}
