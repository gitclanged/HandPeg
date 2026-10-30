using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- The timeline, lowered: a TimelineDocument read once into what the graph is written from (4.3B) -----
// No track of the document is privileged, and any clip on any track compiles as a layer does: placed, timed, and read
// from its file on the file's own clock (MediaStarts, SeekIndexes), its sound cut at the same origin. One way is kept
// faster than that, and it is only faster: the clips of the main video (ContentKind MainVideo, of the primary file:
// SequenceExportState.LocalMediaPath, what the player plays and the export opens as its first input) are read from that
// stream rather than opened again (a trim of input 0; the player's own frames, decoded on the graphics card). They are the
// primary chain: those on the lowest video track that has any, from the file's first picture stream, at their recorded
// speed. The blurred background and the cut-outs are made from them, and the file's audio tracks follow them, whatever
// each clip looks like (4.3D: a clip of Track 1 has its own keyframes and effects, as a clip of any track has). The
// chain's picture is drawn in one piece where its clips share the look of the first of them (keys count from each clip's
// own start, so one motion runs across them all); any other clip of the chain (keys or effects of its own) is drawn from
// the same stream as a layer of its own, in the main video's box. A clip of the main video that cannot ride the chain (a
// speed, another stream, another track) is a layer read from its file, in that box; a clip of the same file that is not
// the main video's (a VideoFile clip: a picture in picture of it) is a layer like any other.
//
// The preparation the view model used to make of its copies is the lowering's now: a track's crossfades laid over each
// other across their cut, the primary chain's as heads read from its stream again, fades paired across tracks, and a
// video that repeated to the end of the timeline read as the one repeating source 4.2.0 made of it.

/// <summary>
/// A timeline as <see cref="FilterGraphBuilder"/> lowers it (<see cref="FilterGraphBuilder.Lowering"/>): what the player's
/// sequence and a measurement need to know of it. Shared with every graph built from the state: read it, never change it.
/// </summary>
/// <param name="SequenceSeconds">How long the sequence is: the canvas's length.</param>
/// <param name="MainClips">The primary chain's clips in the order they come on the sequence, crossfades laid out.</param>
/// <param name="MainSound">Where the primary file's sound is heard (J and L cuts, slips and gains; silenced clips left out).</param>
/// <param name="MainHeard">
/// The chain's clips whose sound is heard, where their pictures are, with their gains: where the player's own stream of the
/// file is let through (it places the file's sound as its picture), as Live Preview's sound gates it
/// (<see cref="FilterGraphBuilder.BuildMainGate"/>, while the chain is not the whole sequence). The very list of
/// <paramref name="MainClips"/> while every clip is heard at no gain of its own.
/// </param>
/// <param name="IsMainWholeSequence">The chain is one clip that is the whole file and the whole sequence: the file can be read as it is.</param>
/// <param name="Stack">The pictures as layers, bottom first, the main video's (kind MainVideo) at its track's place.</param>
/// <param name="LayerAudio">The sounds of the clips that are heard, the primary chain's aside.</param>
public sealed record SequenceLowering(
    double SequenceSeconds, IReadOnlyList<MainClip> MainClips, IReadOnlyList<MainClip> MainSound, IReadOnlyList<MainClip> MainHeard,
    bool IsMainWholeSequence, IReadOnlyList<Layer> Stack, IReadOnlyList<Layer> LayerAudio);

public static partial class FilterGraphBuilder
{
    /// <summary>A state's timeline as the builder lowers it: the primary chain, the layers, the sounds (see <see cref="SequenceLowering"/>).</summary>
    public static SequenceLowering Lowering(SequenceExportState state)
    {
        var lowered = Lower(state);
        return new SequenceLowering(lowered.SequenceSeconds, lowered.MainClips, lowered.MainSound, lowered.MainHeard, lowered.IsMainWholeSequence, lowered.Stack, lowered.LayerAudio);
    }

    /// <summary>A timeline as the graph is written from it (see <see cref="Lower"/>). Shared by every graph built from one document: read, never changed.</summary>
    private sealed class Lowered
    {
        /// <summary>What it was lowered with besides the document: a state that shares the document but not these is lowered afresh.</summary>
        public required (string Primary, double Media, bool Engine, int SourceWidth, int SourceHeight, (int, int, int, int) Box) Inputs { get; init; }

        /// <summary>How long the sequence is: the canvas's length.</summary>
        public required double SequenceSeconds { get; init; }

        /// <summary>The primary chain's clips in the order they come on the sequence, crossfades laid out (the earlier running on to the end of one, the later starting there).</summary>
        public required IReadOnlyList<MainClip> MainClips { get; init; }

        /// <summary>The crossfades between the primary chain's clips: the later clip's head, laid over the earlier one.</summary>
        public required IReadOnlyList<MainCrossfade> MainCrossfades { get; init; }

        /// <summary>
        /// The chain's clips its picture is drawn for in one piece (<see cref="MainLayer"/>): those with the look of the
        /// first of them. The very list of <see cref="MainClips"/> while every clip has it.
        /// </summary>
        public required IReadOnlyList<MainClip> CenterClips { get; init; }

        /// <summary>The chain's other clips (keys or effects, a place or a crop of their own), each a layer cut whole from the chain's stream, in its own box.</summary>
        public required IReadOnlySet<Layer> Riders { get; init; }

        /// <summary>
        /// The chain's clips the blurred background is made under (4.4): those whose blur fill is on or left to the
        /// sequence's switch. The very list of <see cref="MainClips"/> while every clip leaves it on or to the switch.
        /// </summary>
        public required IReadOnlyList<MainClip> BlurClips { get; init; }

        /// <summary>
        /// The chain's clips whose blur fill is on whatever the sequence's switch says: the blurred background with the
        /// switch off. The very list of <see cref="MainClips"/> while every clip's is.
        /// </summary>
        public required IReadOnlyList<MainClip> ForcedBlurClips { get; init; }

        /// <summary>
        /// The layers made of the main video's clips that are cut from their stream by a crop of their own (4.4: a rider
        /// cropped otherwise than the chain's first clip, a clip of the main video laid as a file), as fractions of the
        /// picture: left, top, right, bottom. The chain's one piece and its other riders are cut by the sequence's crop
        /// (<see cref="SequenceExportState.CropFractions"/>); every other clip by its own, which its layer keeps
        /// (4.4-BugHunt: every clip's crop reframes its picture, <see cref="Layer.CropFractions"/>).
        /// </summary>
        public required IReadOnlyDictionary<Layer, (double Left, double Top, double Right, double Bottom)> StreamCrops { get; init; }

        /// <summary>The blurred copies laid under the videos from files that fill the frame with one (4.4: a clip's blur fill), each just under its clip.</summary>
        public required IReadOnlySet<Layer> BlurTwins { get; init; }

        /// <summary>
        /// Where the primary file's sound is heard: the chain's clips with their J and L cuts, their slip and their gain, the
        /// silenced ones left out. The very list of <see cref="MainClips"/> while the sound is placed just as the picture is.
        /// </summary>
        public required IReadOnlyList<MainClip> MainSound { get; init; }

        /// <summary>
        /// The chain's clips whose sound is heard, where their pictures are, with their gains: what the player's own stream
        /// carries of it (it places the file's sound as its picture), for Live Preview. The very list of <see cref="MainClips"/>
        /// while every clip is heard, at no gain of its own.
        /// </summary>
        public required IReadOnlyList<MainClip> MainHeard { get; init; }

        /// <summary>What to add to a time in the primary file to get its time on the sequence, for the chain's first clip.</summary>
        public required double MainShift { get; init; }

        public required bool IsMainSpliced { get; init; }

        /// <summary>The chain is one clip that is the whole file and the whole sequence, its sound placed as its picture: the file can be read as it is.</summary>
        public required bool IsMainWholeSequence { get; init; }

        /// <summary>The primary chain's look (keys from its first clip's start), as a layer of kind MainVideo; a hidden stand-in when there is no chain.</summary>
        public required Layer MainLayer { get; init; }

        /// <summary>The pictures in stacking order, bottom first: the main layer at its track's place among them. Prepared for their crossfades.</summary>
        public required IReadOnlyList<Layer> Stack { get; init; }

        /// <summary>The sounds of the clips that are heard, the primary chain's aside: video clips that carry any, and sound clips.</summary>
        public required IReadOnlyList<Layer> LayerAudio { get; init; }

        public required IReadOnlyList<EffectInstance> SequenceEffects { get; init; }
    }

    // One lowering for each document, kept as long as the document is: the GPU scene plan, the sound engine's program and
    // the exports are built from the same state again and again, and from copies of it that share its document.
    private static readonly ConditionalWeakTable<TimelineDocument, Lowered> LoweredTimelines = new();

    /// <summary>A state's timeline, lowered: from the document's cache when it was lowered with the same inputs.</summary>
    private static Lowered Lower(SequenceExportState state)
    {
        var inputs = (Clean(state.HasSource ? state.LocalMediaPath : ""), state.MainMediaSeconds, state.FrameEngine, state.SourceWidth, state.SourceHeight,
            (state.CenterRect.X, state.CenterRect.Y, state.CenterRect.Width, state.CenterRect.Height));
        if (LoweredTimelines.TryGetValue(state.Timeline, out var known) && known.Inputs == inputs)
            return known;

        var lowered = LowerAfresh(state, inputs);
        LoweredTimelines.AddOrUpdate(state.Timeline, lowered);
        return lowered;
    }

    private static string Clean(string path) => path.Trim().Trim('"');

    private static Lowered LowerAfresh(SequenceExportState state, (string Primary, double Media, bool Engine, int SourceWidth, int SourceHeight, (int, int, int, int) Box) inputs)
    {
        var document = state.Timeline;
        var (primary, media, engine) = (inputs.Primary, inputs.Media, inputs.Engine);
        var length = document.TotalDuration;

        // Each clip's look, made once: its own State, or its track's look with what is the clip's own taken from its State.
        var looks = new Dictionary<TimelineClip, LayerState>(ReferenceEqualityComparer.Instance);
        LayerState LookOf(TimelineClip clip, TimelineTrackModel track)
        {
            if (!looks.TryGetValue(clip, out var look))
                looks[clip] = look = Merged(track.TrackLook, clip.State);
            return look;
        }

        // The primary chain: the lowest picture track with a clip that can ride it, that clip (whose look and clock the
        // chain takes), and every clip of that track that rides it with it. Its picture in one piece: those with the first
        // one's look; the rest ride alone.
        TimelineTrackModel? primaryTrack = null;
        TimelineClip? anchor = null;
        if (primary.Length > 0)
        {
            foreach (var track in document.Tracks)
            {
                if (ShowsPictures(track) && track.Clips.FirstOrDefault(c => !c.IsHidden && CanRide(c, primary)) is { } first)
                {
                    (primaryTrack, anchor) = (track, first);
                    break;
                }
            }
        }

        var chain = new List<TimelineClip>();
        var center = new HashSet<TimelineClip>(ReferenceEqualityComparer.Instance);
        if (primaryTrack is not null && anchor is not null)
        {
            var anchorLook = LookOf(anchor, primaryTrack);
            JsonNode? anchorTimeless = null;
            foreach (var clip in primaryTrack.Clips)
            {
                if (!ReferenceEquals(clip, anchor) && (clip.IsHidden || !CanRide(clip, primary)))
                    continue;

                chain.Add(clip);
                if (ReferenceEquals(clip, anchor) || SameLook(LookOf(clip, primaryTrack), anchorLook, clip.Start - anchor.Start, ref anchorTimeless))
                    center.Add(clip);
            }
        }

        var riding = new HashSet<TimelineClip>(chain, ReferenceEqualityComparer.Instance);

        // The main video's place and crop are its clips' own (4.4): the sequence's (CenterRect, CropFractions) are its first
        // clip's, and a clip of it placed or cropped otherwise is laid in a box and cut by a crop of its own. With no chain
        // (no clip can ride it), the first clip of the main video's, as the view model sizes the sequence by then
        // (MainViewModel.AnchorMainView).
        LayerState? chainLook = null;
        if (anchor is not null)
        {
            chainLook = LookOf(anchor, primaryTrack!);
        }
        else
        {
            foreach (var track in document.Tracks)
            {
                if (track.Clips.FirstOrDefault(c => c.ContentKind == LayerKind.MainVideo) is { } first)
                {
                    chainLook = LookOf(first, track);
                    break;
                }
            }
        }
        var streamCrops = new Dictionary<Layer, (double Left, double Top, double Right, double Bottom)>(ReferenceEqualityComparer.Instance);

        // Every other clip as a layer, numbered by its track (a crossfade across tracks pairs clips of two tracks).
        var layers = new Dictionary<TimelineClip, Layer>(ReferenceEqualityComparer.Instance);
        for (var t = 0; t < document.Tracks.Count; t++)
        {
            var track = document.Tracks[t];
            if (track.Kind == TimelineTrackKind.Subtitle)
                continue;

            foreach (var clip in track.Clips)
            {
                // The caption box and the blurred background are the sequence's (CaptionLayer, BackgroundHidden). A clip of
                // the chain outside its one piece is a layer cut whole from the stream.
                if (riding.Contains(clip))
                {
                    if (!center.Contains(clip))
                        layers[clip] = MakeRider(clip, track, t + 1, LookOf(clip, track), chainLook, state, document, streamCrops);
                }
                else if (clip.ContentKind is not (LayerKind.Captions or LayerKind.Background))
                {
                    layers[clip] = MakeLayer(clip, track, t + 1, LookOf(clip, track), chainLook, state, document, streamCrops);
                }
            }
        }

        var riders = new HashSet<Layer>(chain.Where(c => !center.Contains(c)).Select(c => layers[c]), ReferenceEqualityComparer.Instance);

        // A video that repeated to the end of the timeline (4.2.0's "to the end" loop, written as its rounds): one layer that
        // repeats from its file's first frame, as 4.2.0 read it, not one decoder for each round. Before the crossfades, which
        // 4.2.0 never gave such a layer.
        foreach (var track in document.Tracks)
        {
            for (var i = 0; i < track.Clips.Count; i++)
            {
                if (!layers.TryGetValue(track.Clips[i], out var looped) || Rounds(track, i, LookOf) is not { Count: > 0 } rounds || rounds[^1].End < length - 0.001)
                    continue;

                (looped.Duration, looped.FadeOutSeconds) = (0, Math.Clamp(rounds[^1].FadeOutSeconds, 0, TimelineDocument.LongestFade));
                foreach (var round in rounds)
                    layers.Remove(round);
                i += rounds.Count;
            }
        }

        // Crossfades on a track: two layers are laid over each other across their cut; a layer and a clip of the primary
        // chain, which cannot be, both dip through what is under them, as clips with nothing beyond the cut do. Each track in
        // the order of its clips, so a clip between two crossfades is laid out for the first before the second; a clip is
        // laid over by one later clip at most (4.2.0 looked for the earlier one by its end, which the first had moved); and
        // a sound that is not heard takes no part (4.2.0 never made one).
        var dips = new Dictionary<TimelineClip, (double In, double Out)>(ReferenceEqualityComparer.Instance);
        var laidOver = new HashSet<Layer>(ReferenceEqualityComparer.Instance);
        foreach (var track in document.Tracks)
        {
            for (var i = 0; i < track.Clips.Count; i++)
            {
                var after = track.Clips[i];
                if (after.TransitionIn <= 0.01 || after.Duration <= 0.001 || Before(track, i) is not { } before)
                    continue;

                // The crossfades within the chain's one piece are heads read from its stream again (LayOutChain).
                if ((center.Contains(before) && center.Contains(after)) || IsSilentSound(before, track) || IsSilentSound(after, track))
                    continue;

                // A clip of the chain shows the stream as it is at each moment, which cannot run on past its cut.
                if (!riding.Contains(before) && !riding.Contains(after) && layers.TryGetValue(before, out var earlier) && layers.TryGetValue(after, out var later))
                {
                    if (laidOver.Add(earlier))
                        LayOverAcrossCut(earlier, later, after.TransitionIn);
                    continue;
                }

                // Otherwise both dip through what is under them (a clip of the chain, with the stream's sound).
                var half = Math.Min(after.TransitionIn, Math.Min(before.Duration, after.Duration)) / 2;
                if (layers.TryGetValue(before, out earlier))
                    earlier.FadeOutSeconds = Math.Max(earlier.FadeOutSeconds, half);
                if (riding.Contains(before))
                    dips[before] = (dips.GetValueOrDefault(before).In, Math.Max(dips.GetValueOrDefault(before).Out, half));
                if (layers.TryGetValue(after, out later))
                    later.FadeInSeconds = Math.Max(later.FadeInSeconds, half);
                if (riding.Contains(after))
                    dips[after] = (Math.Max(dips.GetValueOrDefault(after).In, half), dips.GetValueOrDefault(after).Out);
            }
        }

        // The chain's clips on the sequence, their crossfades laid out, where their sound is heard, and which want the
        // blurred background under them.
        var (mainClips, mainCrossfades, mainSound, mainHeard, centerClips, blurClips, forcedBlurClips) = engine
            ? LayOutChain(chain, center, primaryTrack, dips, media, clip => LookOf(clip, primaryTrack!).BlurFill)
            : WholeFile(media);

        var sequenceSeconds = engine ? length : mainClips.Count == 0 ? 0 : mainClips.Max(c => c.End);
        var mainLayer = MainLayerOf(anchor, primaryTrack, anchor is null ? null : LookOf(anchor, primaryTrack!), state, document, media);

        // The pictures bottom first, the main layer with its track (before that track's other clips: none of them is on with it).
        var stack = new List<Layer>();
        foreach (var track in document.Tracks)
        {
            if (!ShowsPictures(track) && track.Kind != TimelineTrackKind.Adjustment)
                continue;
            if (ReferenceEquals(track, primaryTrack) && center.Count > 0)
                stack.Add(mainLayer);
            foreach (var clip in track.Clips)
            {
                if (layers.TryGetValue(clip, out var layer) && layer.IsPicture)
                    stack.Add(layer);
            }
        }

        PairLayerCrossfades(stack);

        // A video from a file whose blur fill is on (4.4): a blurred copy of it, covering the frame, just under it, while it
        // can be seen. (The chain's clips have the blurred background, BlurClips; a piece cut from the video has no picture
        // of its own.)
        var twins = new HashSet<Layer>(ReferenceEqualityComparer.Instance);
        foreach (var track in document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (layers.TryGetValue(clip, out var layer) && layer.IsVideoFile && !layer.IsHidden && (layer.Opacity > 0 || layer.AnimatesOpacity)
                    && LookOf(clip, track).BlurFill == true && stack.IndexOf(layer) is >= 0 and var at)
                {
                    var twin = BlurTwinOf(layer, state, document);
                    stack.Insert(at, twin);
                    twins.Add(twin);
                }
            }
        }

        // The sounds heard, in the order of the tracks: each layer's own, moved by its J or L cut when it has one.
        var sounds = new List<Layer>();
        foreach (var track in document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (layers.TryGetValue(clip, out var layer) && layer.CarriesSound && IsHeard(clip, track))
                    sounds.Add(SoundOf(layer, clip, state, document));
            }
        }

        // (A main video with no sound at all has nothing to place: its file can still be read as it is.)
        var soundAsPicture = ReferenceEquals(mainSound, mainClips) || chain.All(c => !c.HasSound);
        var mainEnd = mainClips.Count > 0 ? mainClips.Max(c => c.End) : 0;
        return new Lowered
        {
            Inputs = inputs,
            SequenceSeconds = sequenceSeconds,
            MainClips = mainClips,
            MainCrossfades = mainCrossfades,
            CenterClips = centerClips,
            Riders = riders,
            BlurClips = blurClips,
            ForcedBlurClips = forcedBlurClips,
            StreamCrops = streamCrops,
            BlurTwins = twins,
            MainSound = mainSound,
            MainHeard = mainHeard,
            MainShift = engine && anchor is not null ? anchor.Start - anchor.MediaOffset : 0,
            IsMainSpliced = engine && chain.Count > 1,
            IsMainWholeSequence = !engine
                || (chain.Count == 1 && soundAsPicture
                    && (media <= 0 || (mainClips.Count == 1 && mainClips[0].Start < 0.001 && anchor!.MediaOffset < 0.001
                                       && Math.Abs(mainEnd - media) < 0.02 && sequenceSeconds - mainEnd < 0.02))),
            MainLayer = mainLayer,
            Stack = stack,
            LayerAudio = sounds,
            SequenceEffects = document.SequenceEffectsBypassed ? [] : [.. document.SequenceEffects.Select(EffectInstance.FromState)],
        };
    }

    // ----- What rides the primary chain -----

    /// <summary>A track whose clips are pictures laid on the canvas (a switch track holds what a video track holds).</summary>
    private static bool ShowsPictures(TimelineTrackModel track) => track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch;

    /// <summary>
    /// Whether a clip plays the main video as the player plays it: a clip of the main video (not merely of its file), from
    /// the primary file's first picture stream, at its recorded speed, with no sound or the file's own (its tracks follow
    /// the chain).
    /// </summary>
    private static bool CanRide(TimelineClip clip, string primary) =>
        clip.ContentKind == LayerKind.MainVideo && clip.VideoStreamIndex == 0 && clip.AudioStreamIndex <= 0
        && Math.Abs(clip.Speed - 1) < 0.0005 && clip.SourcePath is { } path && string.Equals(Clean(path), primary, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a clip's sound reaches the output: it has some, from its file's first audio stream (another stream of a file
    /// is not read yet: the primary file's streams are the output's tracks, SequenceExportState.AudioTracks, and a later
    /// pass maps any file's streams onto them), and neither it nor its track is switched off or silenced.
    /// </summary>
    private static bool IsHeard(TimelineClip clip, TimelineTrackModel track) =>
        clip.HasSound && clip.AudioStreamIndex <= 0 && !clip.IsHidden && !clip.IsMuted && !track.IsMuted;

    /// <summary>A clip that is only a sound (a sound clip, or any clip on a sound track) and is not heard.</summary>
    private static bool IsSilentSound(TimelineClip clip, TimelineTrackModel track) =>
        (clip.ContentKind == LayerKind.Audio || track.Kind == TimelineTrackKind.Audio) && !IsHeard(clip, track);

    /// <summary>
    /// The clip on a track whose end meets the start of the one at <paramref name="index"/> (as 4.2.0 counted meeting: within
    /// 11 ms), or null: the one before it, on a track whose clips are apart; any, on one whose clips were not kept apart.
    /// </summary>
    private static TimelineClip? Before(TimelineTrackModel track, int index)
    {
        var after = track.Clips[index];
        static bool Meets(TimelineClip clip, TimelineClip after) => clip.Duration > 0.001 && Math.Abs(clip.End - after.Start) < 0.011;
        if (index > 0 && Meets(track.Clips[index - 1], after))
            return track.Clips[index - 1];

        for (var j = track.Clips.Count - 1; j >= 0; j--)
        {
            if (j != index && Meets(track.Clips[j], after))
                return track.Clips[j];
        }

        return null;
    }

    /// <summary>
    /// The rounds after the clip at <paramref name="index"/> of a video that repeated (how 4.2.0's "to the end" loop is
    /// written as clips): that clip plays its file to its end, then each round, butted to the one before, plays the file
    /// again from its first frame with the same look (keys running on) and its sound silenced (4.2.0's loop played its
    /// sound once), whole but for the last, with no fade or crossfade where two meet. Empty when no round follows.
    /// </summary>
    private static List<TimelineClip> Rounds(TimelineTrackModel track, int index, Func<TimelineClip, TimelineTrackModel, LayerState> lookOf)
    {
        var rounds = new List<TimelineClip>();
        var first = track.Clips[index];
        var file = first.SourceDuration;
        bool Whole(TimelineClip clip) => Math.Abs(clip.MediaEnd - file) <= 0.001;
        if (first.ContentKind != LayerKind.VideoFile || file <= 0.05 || !Whole(first) || first.FadeOutSeconds > 0.001
            || (first.AudioLinked && (Math.Abs(first.AudioLeadInSeconds) > 0.0005 || Math.Abs(first.AudioTailOutSeconds) > 0.0005)))
        {
            return rounds;
        }

        JsonNode? firstTimeless = null;
        var look = lookOf(first, track);
        var previous = first;
        for (var j = index + 1; j < track.Clips.Count; j++)
        {
            var round = track.Clips[j];
            if (round.ContentKind != LayerKind.VideoFile || round.SourcePath != first.SourcePath || round.Speed != first.Speed
                || round.IsHidden != first.IsHidden || !round.IsMuted || round.MediaOffset > 1e-6 || round.MediaEnd > file + 0.001
                || Math.Abs(round.Start - previous.End) > 1e-6 || round.FadeInSeconds > 0.001 || round.TransitionIn > 0.001
                || !SameLook(lookOf(round, track), look, round.Start - first.Start, ref firstTimeless))
            {
                break;
            }

            rounds.Add(round);
            previous = round;

            // A round that stops before its file's end, or fades out, is the last.
            if (!Whole(round) || round.FadeOutSeconds > 0.001)
                break;
        }

        return rounds;
    }

    /// <summary>
    /// Two looks are one when everything but where they are in time is the same, and their keyframes are one motion: the
    /// later clip's keys, counted from its own start, fall <paramref name="shift"/> seconds later on the first one's clock
    /// (to a few microseconds: keys are moved to the microsecond as clips are cut and trimmed). <paramref name="firstTimeless"/>
    /// keeps the first look as JSON once it has been made, for the next clip compared with it.
    /// </summary>
    private static bool SameLook(LayerState look, LayerState first, double shift, ref JsonNode? firstTimeless)
    {
        if (ReferenceEquals(look, first))
            return shift == 0 || !IsTimed(look);

        if (!SameKeys(look.KeysX, first.KeysX, shift) || !SameKeys(look.KeysY, first.KeysY, shift) || !SameKeys(look.KeysScale, first.KeysScale, shift)
            || !SameKeys(look.KeysRotation, first.KeysRotation, shift) || !SameKeys(look.KeysOpacity, first.KeysOpacity, shift))
        {
            return false;
        }

        var (effects, firstEffects) = (look.Effects ?? [], first.Effects ?? []);
        if (effects.Count != firstEffects.Count)
            return false;
        for (var i = 0; i < effects.Count; i++)
        {
            var (keys, firstKeys) = (effects[i].Keys ?? [], firstEffects[i].Keys ?? []);
            if (keys.Count != firstKeys.Count || keys.Any(k => !firstKeys.TryGetValue(k.Key, out var other) || !SameKeys(k.Value, other, shift)))
                return false;

            var (range, firstRange) = (effects[i].Range, firstEffects[i].Range);
            if (range?.Length != firstRange?.Length)
                return false;
            for (var r = 0; r < (range?.Length ?? 0); r++)
            {
                if (Math.Abs(range![r] + shift - firstRange![r]) > KeySlack)
                    return false;
            }
        }

        firstTimeless ??= Timeless(first);
        return JsonNode.DeepEquals(Timeless(look), firstTimeless);
    }

    private const double KeySlack = 5e-6;

    /// <summary>Whether a look has anything that is placed in time: keys, or an effect's keys or range.</summary>
    private static bool IsTimed(LayerState look) =>
        look.KeysX is { Count: > 0 } || look.KeysY is { Count: > 0 } || look.KeysScale is { Count: > 0 } || look.KeysRotation is { Count: > 0 }
        || look.KeysOpacity is { Count: > 0 } || look.Effects?.Any(e => e.Keys is { Count: > 0 } || e.Range is not null) == true;

    private static bool SameKeys(List<Keyframe>? keys, List<Keyframe>? first, double shift)
    {
        if ((keys?.Count ?? 0) != (first?.Count ?? 0))
            return false;
        for (var i = 0; i < (keys?.Count ?? 0); i++)
        {
            var (key, other) = (keys![i], first![i]);
            if (Math.Abs(key.Time + shift - other.Time) > KeySlack || key with { Time = 0 } != other with { Time = 0 })
                return false;
        }

        return true;
    }

    /// <summary>A look as JSON without what differs from clip to clip of one chain: where it is in time, its keys, its name.</summary>
    private static JsonNode? Timeless(LayerState look)
    {
        var node = JsonSerializer.SerializeToNode(look, TimelessOptions);
        if (node is not JsonObject json)
            return node;

        foreach (var name in (string[])[nameof(LayerState.Name), nameof(LayerState.Kind), nameof(LayerState.ImagePath), nameof(LayerState.StartTime),
                     nameof(LayerState.Duration), nameof(LayerState.MediaOffset), nameof(LayerState.Speed), nameof(LayerState.MediaDuration),
                     nameof(LayerState.IsHidden), nameof(LayerState.ProxyChoice), nameof(LayerState.AudioOffset), nameof(LayerState.TrackId),
                     nameof(LayerState.AudioGainDb), nameof(LayerState.IsSoundMuted), nameof(LayerState.FadeInSeconds), nameof(LayerState.FadeOutSeconds),
                     nameof(LayerState.FadeInStyle), nameof(LayerState.FadeOutStyle), nameof(LayerState.TransitionIn), nameof(LayerState.KeysX),
                     nameof(LayerState.KeysY), nameof(LayerState.KeysScale), nameof(LayerState.KeysRotation), nameof(LayerState.KeysOpacity),
                     nameof(LayerState.BlurFill)])
        {
            json.Remove(name);
        }

        if (json[nameof(LayerState.Effects)] is JsonArray effects)
        {
            foreach (var effect in effects.OfType<JsonObject>())
            {
                effect.Remove(nameof(EffectState.Keys));
                effect.Remove(nameof(EffectState.Range));
            }
        }

        return json;
    }

    private static readonly JsonSerializerOptions TimelessOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>
    /// A clip's look over its track's: what the track's clips share from <paramref name="trackLook"/>, what is the clip's
    /// own (its name and file, where it sits and how large, its shape, turn and crop, its blur fill, its keyframes, its
    /// anchor and its effect stack) from <paramref name="state"/>. Just the clip's State when the track has no look. Neither
    /// is changed.
    /// </summary>
    private static LayerState Merged(LayerState? trackLook, LayerState state)
    {
        if (trackLook is null)
            return state;

        var look = trackLook.ShallowCopy();
        (look.Name, look.Kind, look.ImagePath, look.ImageWidth, look.ImageHeight) = (state.Name, state.Kind, state.ImagePath, state.ImageWidth, state.ImageHeight);
        (look.MediaDuration, look.HasAudio, look.AudioCodec, look.AudioChannels) = (state.MediaDuration, state.HasAudio, state.AudioCodec, state.AudioChannels);
        (look.PositionX, look.PositionY, look.SizeWidth, look.SizeHeight) = (state.PositionX, state.PositionY, state.SizeWidth, state.SizeHeight);
        (look.LockAspectRatio, look.Rotation, look.BlurFill) = (state.LockAspectRatio, state.Rotation, state.BlurFill);
        (look.CropLeft, look.CropTop, look.CropRight, look.CropBottom) = (state.CropLeft, state.CropTop, state.CropRight, state.CropBottom);
        (look.KeysX, look.KeysY, look.KeysScale, look.KeysRotation, look.KeysOpacity) = (state.KeysX, state.KeysY, state.KeysScale, state.KeysRotation, state.KeysOpacity);
        (look.AnchorX, look.AnchorY, look.Effects, look.EffectsBypassed) = (state.AnchorX, state.AnchorY, state.Effects, state.EffectsBypassed);
        return look;
    }

    // ----- Clips as layers -----

    /// <summary>
    /// A clip as the layer the graph is written from: its look, with its own timing, fades, file, visibility (its track
    /// hides its picture, not its sound), sound (its track's gain added to its own, the track's filters and ducking) and
    /// a number for its track. A clip of the main video that does not ride the chain sits in the main video's box while it
    /// is placed and cropped as the chain's first clip is (uncropped while that is), in a box of its own otherwise (4.4); its
    /// crop cuts its picture (<paramref name="streamCrops"/>), never a mask of it. So does every other clip's (4.4-BugHunt),
    /// which its layer keeps. Keyframes that change nothing are folded (see <see cref="FoldConstantKeys"/>).
    /// </summary>
    /// <param name="anchorLook">The look of the chain's first clip (with no chain, the main video's first clip), whose place and crop are the sequence's; null with no clip of the main video.</param>
    private static Layer MakeLayer(TimelineClip clip, TimelineTrackModel track, int trackNumber, LayerState look, LayerState? anchorLook, SequenceExportState state,
        TimelineDocument document, Dictionary<Layer, (double Left, double Top, double Right, double Bottom)> streamCrops)
    {
        var kind = clip.ContentKind == LayerKind.MainVideo ? LayerKind.VideoFile : clip.ContentKind;
        var made = look.ShallowCopy();
        made.Kind = kind;
        if (clip.SourcePath is { Length: > 0 } path)
            made.ImagePath = path;
        (made.StartTime, made.Duration, made.MediaOffset, made.Speed) = (clip.Start, clip.Duration, clip.MediaOffset, clip.Speed);
        if (clip.SourceDuration > 0)
            made.MediaDuration = clip.SourceDuration;
        (made.FadeInSeconds, made.FadeOutSeconds, made.FadeInStyle, made.FadeOutStyle) = (clip.FadeInSeconds, clip.FadeOutSeconds, clip.FadeInStyle, clip.FadeOutStyle);
        made.TransitionIn = clip.TransitionIn;
        made.IsHidden = clip.IsHidden || (track.IsHidden && track.Kind != TimelineTrackKind.Audio);
        (made.IsSoundMuted, made.HasAudio) = (clip.IsMuted || track.IsMuted, clip.HasSound);
        made.AudioOffset = clip.AudioLinked || kind == LayerKind.Audio ? 0 : clip.AudioSlipSeconds;
        made.AudioGainDb = clip.GainDb + track.GainDb;
        made.AutoDuck = look.AutoDuck || track.DuckAgainstVoice;
        if (track.AudioFilters is { } filters)
            made.AudioFilters = filters;
        (made.ProxyChoice, made.TrackId, made.IsEmptyTrack, made.IsDeleted) = (clip.ProxyChoice, trackNumber, false, false);

        // Every clip's crop reframes its picture (4.4-BugHunt, the user's Q2), held by the one rule (Layer.HeldCrop). A clip of
        // the main video is cut from its stream by it (streamCrops, its box at the shape it leaves), so its layer is not cut
        // again; any other clip's layer keeps it, and the pass cuts its picture by it where it is read (Pass.AppendLayer), its
        // box taking the shape it leaves (Layer.GetOutputRect).
        var main = clip.ContentKind == LayerKind.MainVideo;
        var crop = main ? Layer.HeldCrop(look) : default;
        if (main)
            (made.CropLeft, made.CropTop, made.CropRight, made.CropBottom) = (0, 0, 0, 0);

        var (x, y, width, height) = state.CenterRect;
        var (ownPlace, ownCrop) = main ? OwnOf(look, anchorLook) : (false, false);
        if (main && width > 0 && document.Width > 0 && document.Height > 0)
        {
            if (!ownPlace && !ownCrop && !IsCropped(crop))
            {
                // The box at the source's own shape.
                var tall = state.SourceWidth > 0 && state.SourceHeight > 0 ? width * (double)state.SourceHeight / state.SourceWidth : height;
                (made.PositionX, made.PositionY) = (x / (double)document.Width, (y + (height - tall) / 2) / document.Height);
                (made.SizeWidth, made.SizeHeight, made.LockAspectRatio) = (width / (double)document.Width, tall / document.Height, false);
            }
            else
            {
                // Its own box, or the main video's at the shape the same crop gives it; its picture cut by its crop.
                (x, y, width, height) = ownPlace || ownCrop ? MainRectOf(look, state, document) : (x, y, width, height);
                (made.PositionX, made.PositionY) = (x / (double)document.Width, y / (double)document.Height);
                (made.SizeWidth, made.SizeHeight, made.LockAspectRatio) = (width / (double)document.Width, height / (double)document.Height, false);
            }
        }

        var layer = Layer.FromState(made, state.SourceWidth, state.SourceHeight, document.Width, document.Height);
        layer.ClipId = clip.Id;
        FoldConstantKeys(layer);
        if (main && IsCropped(crop))
            streamCrops[layer] = crop;
        return layer;
    }

    /// <summary>
    /// A clip of the primary chain outside its one piece (keys or effects of its own) as the layer the graph is written
    /// from: its look, timing, fades and visibility, cut whole from the chain's stream (with the main video's crop,
    /// <see cref="Lowered.Riders"/>) and laid in the main video's box; or, placed or cropped otherwise than the chain's first
    /// clip (4.4), laid in its own box and cut by its own crop (<paramref name="streamCrops"/>), never a mask of it. Kind
    /// Video, so it is there while the stream is; no sound, which the stream carries.
    /// </summary>
    /// <param name="anchorLook">The look of the chain's first clip, whose place and crop are the sequence's.</param>
    private static Layer MakeRider(TimelineClip clip, TimelineTrackModel track, int trackNumber, LayerState look, LayerState? anchorLook, SequenceExportState state,
        TimelineDocument document, Dictionary<Layer, (double Left, double Top, double Right, double Bottom)> streamCrops)
    {
        var made = look.ShallowCopy();
        made.Kind = LayerKind.Video;
        (made.StartTime, made.Duration, made.MediaOffset, made.Speed, made.MediaDuration) = (clip.Start, clip.Duration, clip.MediaOffset, 1, clip.SourceDuration);
        (made.FadeInSeconds, made.FadeOutSeconds, made.FadeInStyle, made.FadeOutStyle) = (clip.FadeInSeconds, clip.FadeOutSeconds, clip.FadeInStyle, clip.FadeOutStyle);
        (made.TransitionIn, made.IsHidden, made.ProxyChoice, made.TrackId) = (clip.TransitionIn, track.IsHidden, clip.ProxyChoice, trackNumber);
        (made.HasAudio, made.IsSoundMuted, made.AudioOffset, made.IsEmptyTrack, made.IsDeleted) = (false, true, 0, false, false);
        (made.SourceX, made.SourceY, made.SourceWidth, made.SourceHeight) = (0, 0, 1, 1);
        (made.CropLeft, made.CropTop, made.CropRight, made.CropBottom) = (0, 0, 0, 0);

        var (ownPlace, ownCrop) = OwnOf(look, anchorLook);
        var (x, y, width, height) = ownPlace || ownCrop ? MainRectOf(look, state, document) : state.CenterRect;
        if (width > 0 && document.Width > 0 && document.Height > 0)
        {
            (made.PositionX, made.PositionY) = (x / (double)document.Width, y / (double)document.Height);
            (made.SizeWidth, made.SizeHeight, made.LockAspectRatio) = (width / (double)document.Width, height / (double)document.Height, false);
        }

        var layer = Layer.FromState(made, state.SourceWidth, state.SourceHeight, document.Width, document.Height);
        layer.ClipId = clip.Id;
        FoldConstantKeys(layer);
        if (ownCrop)
            streamCrops[layer] = Layer.HeldCrop(look);
        return layer;
    }

    private static bool IsCropped((double Left, double Top, double Right, double Bottom) crop) => crop.Left + crop.Top + crop.Right + crop.Bottom > 1e-9;

    /// <summary>Whether a main video clip is placed (its corner, its width) and cropped otherwise than the clip the sequence's place and crop are (the chain's first, else the main video's first).</summary>
    private static (bool Place, bool Crop) OwnOf(LayerState look, LayerState? anchorLook)
    {
        if (anchorLook is null || ReferenceEquals(look, anchorLook))
            return (false, false);

        static bool Differ(double a, double b) => Math.Abs(a - b) > 1e-9;
        var place = Differ(look.PositionX, anchorLook.PositionX) || Differ(look.PositionY, anchorLook.PositionY) || Differ(look.SizeWidth, anchorLook.SizeWidth);
        var (crop, anchorCrop) = (Layer.HeldCrop(look), Layer.HeldCrop(anchorLook));
        var cropped = Differ(crop.Left, anchorCrop.Left) || Differ(crop.Top, anchorCrop.Top) || Differ(crop.Right, anchorCrop.Right) || Differ(crop.Bottom, anchorCrop.Bottom);
        return (place, cropped);
    }

    /// <summary>
    /// Where a main video clip with a place of its own sits on the frame, in the frame's pixels: its corner and its width
    /// as its look has them, its height at the shape its crop leaves of the source, as the view model places it about its
    /// middle (MainViewModel.GetCenterRect: the same evens and roundings, so a clip placed as the first one is in its box).
    /// </summary>
    private static (int X, int Y, int Width, int Height) MainRectOf(LayerState look, SequenceExportState state, TimelineDocument document)
    {
        var (frameWidth, frameHeight) = (document.Width, document.Height);
        var (sourceWidth, sourceHeight) = state.SourceWidth > 0 && state.SourceHeight > 0 ? (state.SourceWidth, state.SourceHeight) : (1920, 1080);
        var (left, top, right, bottom) = Layer.HeldCrop(look);
        var croppedWidth = Math.Max((int)(sourceWidth * (1 - left - right) / 2) * 2, 2);
        var croppedHeight = Math.Max((int)(sourceHeight * (1 - top - bottom) / 2) * 2, 2);
        var width = Even(frameWidth * (double.IsFinite(look.SizeWidth) ? look.SizeWidth : 1));
        var height = Even((double)width * croppedHeight / croppedWidth);
        return ((int)Math.Round(look.PositionX * frameWidth), (int)Math.Round(look.PositionY * frameHeight), width, height);
    }

    /// <summary>
    /// The blurred copy a video from a file fills the frame with (4.4): the same file, timing, fades, opacity (its keys
    /// too) and visibility, no sound, place keys, mask or effects of its own, in a box that is the frame. Its picture is
    /// scaled to cover the frame about its middle, cut to it and blurred as the sequence's background is (BlurRadius,
    /// BlurPasses, BackgroundDim), where it is written (<see cref="Lowered.BlurTwins"/>).
    /// </summary>
    private static Layer BlurTwinOf(Layer layer, SequenceExportState state, TimelineDocument document)
    {
        var twin = new LayerState
        {
            Name = $"{layer.Name} (blur fill)",
            Kind = LayerKind.VideoFile,
            ImagePath = layer.ImagePath,
            ImageWidth = layer.ImageWidth,
            ImageHeight = layer.ImageHeight,
            MediaDuration = layer.MediaDuration,
            StartTime = layer.StartTime,
            Duration = layer.Duration,
            MediaOffset = layer.MediaOffset,
            Speed = layer.Speed,
            FadeInSeconds = layer.FadeInSeconds,
            FadeOutSeconds = layer.FadeOutSeconds,
            FadeInStyle = layer.FadeInStyle,
            FadeOutStyle = layer.FadeOutStyle,
            IsHidden = layer.IsHidden,
            ProxyChoice = layer.ProxyChoice,
            TrackId = layer.TrackId,
            HasAudio = false,
            IsSoundMuted = true,
            Opacity = layer.Opacity,
            KeysOpacity = layer.GetKeys(KeyProperty.Opacity) is { Count: > 0 } keys ? [.. keys] : null,
            PositionX = 0,
            PositionY = 0,
            SizeWidth = 1,
            SizeHeight = 1,
            LockAspectRatio = false,
            Effects = [],
        };

        var made = Layer.FromState(twin, state.SourceWidth, state.SourceHeight, document.Width, document.Height);
        (made.PictureFadeInOff, made.PictureFadeOutOff) = (layer.PictureFadeInOff, layer.PictureFadeOutOff);
        made.ClipId = layer.ClipId;
        return made;
    }

    /// <summary>
    /// The layer whose sound is heard for a clip: the clip's own, or, for a J or L cut, a copy whose sound starts that much
    /// sooner (into the file from as much earlier, as far as the file has: a crossfade may have taken some of it) and lasts
    /// that much longer, so that picture and sound stay on one moment of the file. An unlinked clip's slip is its
    /// <see cref="Layer.AudioOffset"/>, which moves where its sound starts (Pass.LayerAudioStart).
    /// </summary>
    private static Layer SoundOf(Layer layer, TimelineClip clip, SequenceExportState state, TimelineDocument document)
    {
        var (lead, tail) = clip.AudioLinked ? (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) : (0, 0);
        if (Math.Abs(lead) < 0.0005 && Math.Abs(tail) < 0.0005)
            return layer;

        var sound = Layer.FromState(layer.ToState(), state.SourceWidth, state.SourceHeight, document.Width, document.Height);
        var speed = MediaSpeed(layer);
        var early = Math.Min(Math.Min(lead, layer.StartTime), PlaysFile(layer) ? layer.MediaOffset / speed : double.PositiveInfinity);
        sound.StartTime = layer.StartTime - early;
        sound.MediaOffset = Math.Max(layer.MediaOffset - early * speed, 0);
        sound.Duration = Math.Max(layer.Duration + early + tail, 0.001);
        return sound;
    }

    /// <summary>
    /// The primary chain's look as one layer of kind MainVideo, on the clock of its first clip (its keys and its effects'
    /// keys count from that clip's start); hidden while its track is. A hidden stand-in when there is no chain.
    /// </summary>
    private static Layer MainLayerOf(TimelineClip? anchor, TimelineTrackModel? track, LayerState? look, SequenceExportState state, TimelineDocument document, double media)
    {
        var made = look?.ShallowCopy() ?? new LayerState { Name = "Main Video", IsHidden = true };
        made.Kind = LayerKind.MainVideo;

        // Its crop reframes the stream (the sequence's CropFractions, its first clip's), as every clip's crop reframes its
        // picture (4.4-BugHunt): the stream is cut once, so the layer has none of its own.
        (made.CropLeft, made.CropTop, made.CropRight, made.CropBottom) = (0, 0, 0, 0);
        if (anchor is not null)
        {
            (made.StartTime, made.Duration, made.MediaOffset, made.Speed) = (anchor.Start, anchor.Duration, anchor.MediaOffset, 1);
            (made.FadeInSeconds, made.FadeOutSeconds, made.FadeInStyle, made.FadeOutStyle) = (anchor.FadeInSeconds, anchor.FadeOutSeconds, anchor.FadeInStyle, anchor.FadeOutStyle);
            (made.TransitionIn, made.IsHidden, made.ProxyChoice) = (anchor.TransitionIn, track!.IsHidden, anchor.ProxyChoice);
            (made.AudioOffset, made.TrackId, made.IsSoundMuted) = (0, 0, false);
        }

        made.MediaDuration = media;
        var layer = Layer.FromState(made, state.SourceWidth, state.SourceHeight, document.Width, document.Height);
        FoldConstantKeys(layer);
        return layer;
    }

    // ----- The primary chain on the sequence -----

    /// <summary>
    /// The chain's clips as the graph lays them: each cut to what its file has (a clip past the file's end ends with it),
    /// a clip that runs into the next ending where it starts, and each pair that meets with a crossfade laid over each
    /// other across the cut (<see cref="LayOutCrossfades"/>). Then where its sound is heard (<see cref="SoundOfChain"/>), and
    /// which of them the blurred background is made under (4.4, by each clip's <paramref name="blurFill"/>): those that leave
    /// it on or to the sequence's switch (<see cref="Lowered.BlurClips"/>), and those that have it on whatever the switch says.
    /// </summary>
    private static (IReadOnlyList<MainClip> Clips, IReadOnlyList<MainCrossfade> Crossfades, IReadOnlyList<MainClip> Sound, IReadOnlyList<MainClip> Heard,
        IReadOnlyList<MainClip> Center, IReadOnlyList<MainClip> Blur, IReadOnlyList<MainClip> ForcedBlur) LayOutChain(
        List<TimelineClip> chain, HashSet<TimelineClip> center, TimelineTrackModel? track, Dictionary<TimelineClip, (double In, double Out)> dips, double media,
        Func<TimelineClip, bool?> blurFill)
    {
        if (chain.Count == 0 || track is null)
        {
            List<MainClip> none = [];
            return (none, [], none, none, none, none, none);
        }

        // (A crossfade is only made between two of them that meet within the one piece: one with any other clip between, or
        // with a clip that rides alone, is a dip, made above.)
        var placed = new List<(MainClip Clip, TimelineClip From)>(chain.Count);
        foreach (var clip in chain.OrderBy(c => c.Start))
        {
            var left = Math.Max(media - clip.MediaOffset, 0);
            var length = media > 0 ? Math.Min(clip.Duration, left) : clip.Duration;
            var dip = dips.GetValueOrDefault(clip);
            var fadesAcross = center.Contains(clip) && (placed.Count == 0 || center.Contains(placed[^1].From));
            if (length > 0.001 || media <= 0)
            {
                placed.Add((new MainClip(placed.Count, clip.Start, clip.Start + length, clip.MediaOffset)
                {
                    FadeIn = Math.Max(clip.FadeInSeconds, dip.In), FadeOut = Math.Max(clip.FadeOutSeconds, dip.Out),
                    FadeInStyle = clip.FadeInStyle, FadeOutStyle = clip.FadeOutStyle, TransitionIn = fadesAcross ? clip.TransitionIn : 0,
                }, clip));
            }
        }

        // A clip that runs into the next ends where the next begins: the later one wins, as on a track of 4.2.0.
        for (var i = placed.Count - 2; i >= 0; i--)
        {
            if (placed[i].Clip.End > placed[i + 1].Clip.Start)
                placed[i] = placed[i] with { Clip = placed[i].Clip with { End = Math.Max(placed[i + 1].Clip.Start, placed[i].Clip.Start) } };
            if (placed[i].Clip.End - placed[i].Clip.Start < 0.001 && media > 0)
                placed.RemoveAt(i);
        }

        var clips = placed.ConvertAll(p => p.Clip);
        var crossfades = LayOutCrossfades(clips, media);
        var from = placed.ConvertAll(p => p.From);
        var (sound, heard) = SoundOfChain(clips, from, track);
        var drawn = from.TrueForAll(center.Contains) ? clips : clips.Where((_, i) => center.Contains(from[i])).ToList();
        var fills = from.ConvertAll(c => blurFill(c));
        var blur = fills.TrueForAll(f => f != false) ? clips : clips.Where((_, i) => fills[i] != false).ToList();
        var forced = fills.TrueForAll(f => f == true) ? clips : clips.Where((_, i) => fills[i] == true).ToList();
        return (clips, crossfades, sound, heard, drawn, blur, forced);
    }

    /// <summary>Without the Layer Engine the file is the whole timeline, from its first frame to its last, its sound with it.</summary>
    private static (IReadOnlyList<MainClip> Clips, IReadOnlyList<MainCrossfade> Crossfades, IReadOnlyList<MainClip> Sound, IReadOnlyList<MainClip> Heard,
        IReadOnlyList<MainClip> Center, IReadOnlyList<MainClip> Blur, IReadOnlyList<MainClip> ForcedBlur) WholeFile(double media)
    {
        List<MainClip> clips = [new MainClip(0, 0, media, 0)];
        return (clips, [], clips, clips, clips, clips, []);
    }

    /// <summary>
    /// The chain's crossfades. Each pair that meets with one is laid over each other across the cut: the earlier plays on
    /// past it and the later starts before it, into what the file has there (up to half the crossfade each). In the one
    /// stream the chain is cut from, the earlier clip runs on to the end of the crossfade and the later begins there; the
    /// later's head is laid over it from the crossfade's start, coming up by an opacity ramp (<see cref="MainCrossfade"/>).
    /// Where the file has nothing beyond the cut, both fade through what is under them. <paramref name="clips"/> are changed.
    /// </summary>
    private static List<MainCrossfade> LayOutCrossfades(List<MainClip> clips, double media)
    {
        var crossfades = new List<MainCrossfade>();
        for (var i = 1; i < clips.Count; i++)
        {
            var (before, after) = (clips[i - 1], clips[i]);
            if (after.TransitionIn <= 0.01 || Math.Abs(before.End - after.Start) >= 0.011)
                continue;

            var length = Math.Min(after.TransitionIn, Math.Min(before.End - before.Start, after.End - after.Start));
            var late = Math.Min(length / 2, media > 0 ? Math.Max(media - (before.Offset + before.End - before.Start), 0) : 0);
            var early = Math.Min(length / 2, after.Offset);
            if (late + early < 0.04)
            {
                clips[i - 1] = before with { FadeOut = Math.Max(before.FadeOut, length / 2) };
                clips[i] = after with { FadeIn = Math.Max(after.FadeIn, length / 2) };
                continue;
            }

            var cut = after.Start;
            crossfades.Add(new MainCrossfade(Math.Round(cut - early, 3), Math.Round(cut + late, 3), Math.Round(after.Offset - early, 3)));
            clips[i - 1] = before with { End = Math.Round(cut + late, 3) };
            clips[i] = after with { Start = Math.Round(cut + late, 3), Offset = Math.Round(after.Offset + late, 3) };
        }

        return crossfades;
    }

    /// <summary>
    /// Where the chain's sound is heard, clip by clip (<paramref name="from"/> holds the clip each was made from), and where
    /// its heard clips' pictures are (what the player's stream carries of it, with their gains). A J cut starts a clip's
    /// sound that much before the cut it starts at (its own start, before a crossfade moved it) and that much earlier in
    /// the file, an L cut runs it on past its end, an unlinked clip's slip moves it, its gain and its track's are its own;
    /// a silenced clip, or one without sound, is not heard. Each the very list of <paramref name="clips"/> when nothing
    /// moves or changes, which is what lets the file's tracks be read as they are.
    /// </summary>
    private static (IReadOnlyList<MainClip> Sound, IReadOnlyList<MainClip> Heard) SoundOfChain(List<MainClip> clips, List<TimelineClip> from, TimelineTrackModel track)
    {
        var (sound, heard) = (new List<MainClip>(clips.Count), new List<MainClip>(clips.Count));
        var (soundSame, heardSame) = (true, true);
        for (var i = 0; i < clips.Count; i++)
        {
            var (clip, made) = (clips[i], from[i]);
            if (!IsHeard(made, track))
            {
                (soundSame, heardSame) = (false, false);
                continue;
            }

            var gain = made.GainDb + track.GainDb;
            var played = Math.Abs(gain) < 0.05 ? clip : clip with { GainDb = gain };
            heardSame &= Math.Abs(gain) < 0.05;
            heard.Add(played);

            var (lead, tail, slip) = made.AudioLinked ? (made.AudioLeadInSeconds, made.AudioTailOutSeconds, 0.0) : (0, 0, made.AudioSlipSeconds);
            if (Math.Abs(lead) < 0.0005 && Math.Abs(tail) < 0.0005 && Math.Abs(slip) < 0.0005)
            {
                soundSame &= Math.Abs(gain) < 0.05;
                sound.Add(played);
                continue;
            }

            // From the clip's own edges (a crossfade moves where its picture is cut, not where its sound is meant to go),
            // never short of where its picture now runs; the file moment at each sequence moment as the picture has it.
            soundSame = false;
            var start = lead >= 0 ? Math.Min(made.Start - lead, clip.Start) : Math.Max(made.Start - lead, clip.Start);
            var end = tail >= 0 ? Math.Max(made.End + tail, clip.End) : Math.Min(made.End + tail, clip.End);
            var offset = clip.Offset + (start - clip.Start);
            (start, end) = (start + slip, end + slip);

            // Never before the file's start, nor the sequence's: what would play there is left out.
            if (offset < 0)
                (start, offset) = (start - offset, 0);
            if (start < 0)
                (offset, start) = (offset - start, 0);
            if (end - start > 0.001)
                sound.Add(played with { Start = start, End = end, Offset = offset });
        }

        return (soundSame ? clips : sound, heardSame ? clips : heard);
    }

    // ----- Crossfades on a track and across tracks -----

    /// <summary>How fast a layer plays its file, as the graph plays it.</summary>
    private static double MediaSpeed(Layer clip) => clip.HasSpeed ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

    /// <summary>Whether a layer plays a stretch of a file (a video or a sound), so that its start is a moment of that file.</summary>
    private static bool PlaysFile(Layer clip) => clip.IsVideoFile || clip.IsAudio;

    /// <summary>How much more of its file there is after a layer's end, in seconds of the timeline; endless for a picture or words.</summary>
    private static double MediaAfter(Layer clip) => PlaysFile(clip) && clip.MediaDuration > 0.05
        ? Math.Max((clip.MediaDuration - clip.MediaOffset - clip.Duration * MediaSpeed(clip)) / MediaSpeed(clip), 0)
        : double.PositiveInfinity;

    /// <summary>How much of its file there is before a layer's start, in seconds of the timeline; endless for a picture or words.</summary>
    private static double MediaBefore(Layer clip) => PlaysFile(clip) ? clip.MediaOffset / MediaSpeed(clip) : double.PositiveInfinity;

    /// <summary>
    /// Two layers of a track that meet with a crossfade, laid over each other across the cut: the earlier runs on past it
    /// and the later starts before it, into what their files have there (as much as they have, up to half the crossfade
    /// each); the later one's picture comes up over the earlier's by its own fade (it lies above it: a track's clips are
    /// stacked in the order they start), and their sounds fade out and in across the same stretch. Where neither file has
    /// anything beyond the cut, both dip, picture and sound, through what is under them.
    /// </summary>
    private static void LayOverAcrossCut(Layer before, Layer after, double transition)
    {
        if (after.Duration <= 0.001 || before.Duration <= 0.001)
            return;

        var length = Math.Min(transition, Math.Min(before.Duration, after.Duration));
        var (late, early) = (Math.Min(length / 2, MediaAfter(before)), Math.Min(length / 2, MediaBefore(after)));
        if (late + early < 0.04)
        {
            before.FadeOutSeconds = Math.Max(before.FadeOutSeconds, length / 2);
            after.FadeInSeconds = Math.Max(after.FadeInSeconds, length / 2);
            return;
        }

        before.Duration += late;
        if (PlaysFile(after))
            after.MediaOffset = Math.Max(after.MediaOffset - early * MediaSpeed(after), 0);
        ShiftKeys(after, early);
        (after.StartTime, after.Duration) = (after.StartTime - early, after.Duration + early);
        var window = late + early;
        after.FadeInSeconds = Math.Max(after.FadeInSeconds, window);
        before.FadeOutSeconds = Math.Max(before.FadeOutSeconds, window);
        before.PictureFadeOutOff = true;
    }

    /// <summary>
    /// A layer's keys, and its effects' keys and ranges, moved by the seconds given (to the millisecond, as
    /// <see cref="Layer.ShiftKeys"/> moves them), for a clip whose start moved while what it shows stayed: a range is not held
    /// at 0 (one that lies before the clip, as the document allows, stays there and never applies).
    /// </summary>
    private static void ShiftKeys(Layer layer, double seconds)
    {
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            if (layer.GetKeys(property) is { Count: > 0 } keys)
                layer.SetKeys(property, keys.Select(k => k with { Time = Math.Round(k.Time + seconds, 3) }).ToList());
        }

        foreach (var effect in layer.Effects)
        {
            foreach (var (name, keys) in effect.Keys.ToList())
                effect.SetKeys(name, keys.Select(k => k with { Time = Math.Round(k.Time + seconds, 3) }).ToList());
            if (effect.Range is { } range)
                effect.SetRange((Math.Round(range.Start + seconds, 3), Math.Round(range.End + seconds, 3)));
        }
    }

    /// <summary>
    /// Fades paired across tracks (Crossfade with Overlapping Clip, or two fades dragged to the same stretch): where one
    /// clip fades out while another, laid above it, fades in over exactly the same stretch, the lower one keeps its picture
    /// through it and only its sound fades. The upper one's fade is the picture's side of the crossfade, so the two never
    /// dip through what is under both. <paramref name="stack"/> is bottom first.
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
}
