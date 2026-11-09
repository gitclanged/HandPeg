using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services.Gpu;

// ----- The timeline, lowered: a TimelineDocument read once into what the picture and the sound are made from (4.3B) -----
// No track of the document is privileged (5.2-AuditB: there is no main video, no primary file and no primary chain). Every
// clip on every track is a layer made the one way (MakeLayer): placed, timed, cropped and looked as its own, read from its
// own file (SourcePath) at its own moment (MediaOffset, on the file's own clock: MediaStarts, SeekIndexes), its sound cut at
// the same origin and heard from its own stream. A Cutout is a clip of its file like any other, on its parent's clock. A
// video whose blur fill is on (BlurFill true) has a blurred copy of its whole picture covering the frame at the very bottom of
// the stack, under every picture of every track (its twin; 5.4A, the user's standing rule: an upper track's Background Blur never
// covers a lower track), drawn as the sequence's blurred background used to be; the scene plan leaves a twin out while the
// clip over it hides every pixel of it. A project of the old shape never reaches this (it is converted as it is opened,
// LegacyMainVideo; the golden tests' fixtures the same way): the legacy kinds MainVideo and Video are never made here.
//
// The preparation the view model used to make of its copies is the lowering's: a track's crossfades laid over each other
// across their cut (any two clips that have layers; a dip through what is under them otherwise), fades paired across tracks,
// and a video that repeated to the end of the timeline read as the one repeating source 4.2.0 made of it. One lowering is
// made for each document and shared by everything built from it: the GPU scene plan, the sound program and the exports.

/// <summary>
/// A timeline as <see cref="TimelineLowering"/> lowers it (<see cref="TimelineLowering.Lowering"/>): what the picture, the sound
/// and a measurement need to know of it. Shared with everything built from the same document: read it, never change it.
/// </summary>
/// <param name="SequenceSeconds">
/// How long the sequence is: the canvas's length, to its last picture (<see cref="SequenceSpans.ProgramSeconds"/>, 5.4A: an audio tail
/// past the last picture adds no black frames; a standalone sound placed past it still does).
/// </param>
/// <param name="Stack">
/// The pictures as layers, bottom first: every blur twin first (5.4A: under every picture, in the order of their tracks), then each
/// track's pictures in the order of its clips.
/// Every layer carries the id of the clip it was made from (<see cref="Layer.ClipId"/>; a twin, its video's).
/// </param>
/// <param name="LayerAudio">
/// The sounds of the clips that are heard, in the order of the tracks: video clips that carry any, and sound clips, each
/// carrying its clip's id (a J or L cut's sound, a copy moved by it, too).
/// </param>
public sealed record SequenceLowering(double SequenceSeconds, IReadOnlyList<Layer> Stack, IReadOnlyList<Layer> LayerAudio);

public static partial class TimelineLowering
{
    // ----- Keyframes -----
    // A keyframe pins a clip's place, size and turn at once, so a clip that only moves has keyframes on its
    // size and its turn that all say the same thing. Compiled as they stand, those would be worked out afresh
    // for every frame (a scale and a rotation per frame, of a picture that does neither). So the compiler
    // compares first: a property whose keyframes all hold one value is not a motion, and is written as the
    // plain number it is; and within a property that does change, a stretch between two keyframes that hold
    // the same value adds nothing to the expression.

    // Two values closer than this are the same: a fiftieth of a pixel on a 1920 frame, a hundredth of a degree.
    private const double SamePlace = 0.00001;
    private const double SameAngle = 0.01;

    // Opacity is kept in whole percent: keyframes less than one apart hold it still.
    private const double SameOpacity = 0.5;

    /// <summary>
    /// Takes the keyframes off every property of a layer that they do not change, leaving the property at
    /// the one value they hold. For a copy of a layer that is being prepared for a
    /// <see cref="SequenceExportState"/>: what is then compiled for it is only what moves.
    /// </summary>
    /// <returns>How many properties were folded into plain values.</returns>
    public static int FoldConstantKeys(Layer layer)
    {
        var folded = 0;
        var sizes = layer.GetKeys(KeyProperty.Scale);
        var resized = sizes.Count > 0 && sizes.Max(k => k.Value) - sizes.Min(k => k.Value) >= SamePlace;
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            var keys = layer.GetKeys(property);
            if (keys.Count == 0)
                continue;

            // The place of a layer whose size changes keeps its keyframes, even when they all hold one value: keyed, they
            // are the corner's; with none, the place would be the anchor point's, and the layer would grow about it.
            if (resized && property is KeyProperty.X or KeyProperty.Y)
                continue;

            var (low, high) = (keys.Min(k => k.Value), keys.Max(k => k.Value));
            if (high - low >= property switch { KeyProperty.Rotation or KeyProperty.TiltX or KeyProperty.TiltY => SameAngle, KeyProperty.Opacity => SameOpacity, _ => SamePlace })
                continue;

            var value = keys[0].Value;
            layer.SetKeys(property, null);
            layer.SetValue(property, value);
            folded++;
        }

        return folded;
    }

    /// <summary>A state's timeline as it is lowered: the layers and the sounds (see <see cref="SequenceLowering"/>), from the document's cache.</summary>
    public static SequenceLowering Lowering(SequenceExportState state) => Lower(state.Timeline).Shared;

    /// <summary>A timeline as the picture and the sound are made from it (see <see cref="Lower"/>). Shared by everything built from one document: read, never changed.</summary>
    private sealed class Lowered
    {
        /// <summary>How long the sequence is: the canvas's length, to its last picture (<see cref="SequenceSpans.ProgramSeconds"/>).</summary>
        public required double SequenceSeconds { get; init; }

        /// <summary>The blurred copies laid under the videos from files whose blur fill is on (4.4), all of them under every picture (5.4A).</summary>
        public required IReadOnlySet<Layer> BlurTwins { get; init; }

        /// <summary>The pictures in stacking order, bottom first, the twins among them. Prepared for their crossfades.</summary>
        public required IReadOnlyList<Layer> Stack { get; init; }

        /// <summary>
        /// Where the captions are drawn among the pictures (5.1-Polish, the user's Q2): under the layer of <see cref="Stack"/> at
        /// this index and every one above it (<see cref="Stack"/>'s count: over all of them, under the whole picture's look
        /// and fades); -1 over everything, the fades too (no track is <see cref="TimelineTrackModel.CaptionsUnder"/>).
        /// </summary>
        public required int CaptionsAt { get; init; }

        /// <summary>The sounds of the clips that are heard: video clips that carry any, and sound clips.</summary>
        public required IReadOnlyList<Layer> LayerAudio { get; init; }

        public required IReadOnlyList<EffectInstance> SequenceEffects { get; init; }

        /// <summary>The same as the public record everything built from the state reads (one for each lowering).</summary>
        public required SequenceLowering Shared { get; init; }
    }

    // One lowering for each document, kept as long as the document is: the GPU scene plan, the sound engine's program and
    // the exports are built from the same state again and again, and from copies of it that share its document. Nothing but
    // the document goes into it (5.2-AuditB), so the document alone is the key.
    private static readonly ConditionalWeakTable<TimelineDocument, Lowered> LoweredTimelines = new();

    /// <summary>A document, lowered: from its cache when it was lowered before.</summary>
    private static Lowered Lower(TimelineDocument document)
    {
        if (LoweredTimelines.TryGetValue(document, out var known))
            return known;

        var lowered = LowerAfresh(document);
        LoweredTimelines.AddOrUpdate(document, lowered);
        return lowered;
    }

    private static Lowered LowerAfresh(TimelineDocument document)
    {
        // The whole timeline's length tells a video that repeats "to the end" (its rounds reach it); the canvas runs to the last
        // picture (5.4A: no black frames for an audio tail past it).
        var length = document.TotalDuration;
        var program = SequenceSpans.ProgramSeconds(document);

        // Each clip's look, made once: its own State, or its track's look with what is the clip's own taken from its State.
        var looks = new Dictionary<TimelineClip, LayerState>(ReferenceEqualityComparer.Instance);
        LayerState LookOf(TimelineClip clip, TimelineTrackModel track)
        {
            if (!looks.TryGetValue(clip, out var look))
                looks[clip] = look = Merged(track.TrackLook, clip.State);
            return look;
        }

        // (One delegate for every clip that may start a repeating video, not one made for each.)
        Func<TimelineClip, TimelineTrackModel, LayerState> lookOf = LookOf;

        // The tracks by id, for each track's header's switch (IsHiddenWithParents) at one look-up a
        // step, where looking along the tracks for every clip made a document of many tracks slow to lower after every edit.
        var tracksById = new Dictionary<Guid, TimelineTrackModel>(document.Tracks.Count);
        foreach (var track in document.Tracks)
            tracksById.TryAdd(track.Id, track);

        // Every clip as a layer, numbered by its track (a crossfade across tracks pairs clips of two tracks). The caption box is
        // the sequence's (CaptionLayer), and a clip of the legacy blurred background draws nothing (a clip's blur fill is its own).
        var layers = new Dictionary<TimelineClip, Layer>(ReferenceEqualityComparer.Instance);
        for (var t = 0; t < document.Tracks.Count; t++)
        {
            var track = document.Tracks[t];
            if (track.Kind == TimelineTrackKind.Subtitle)
                continue;

            var hidden = track.Kind != TimelineTrackKind.Audio && IsHiddenWithParents(track, tracksById);
            foreach (var clip in track.Clips)
            {
                if (clip.ContentKind is not (LayerKind.Captions or LayerKind.Background))
                    layers[clip] = MakeLayer(clip, track, t + 1, LookOf(clip, track), hidden, document);
            }
        }

        // A video that repeated to the end of the timeline (4.2.0's "to the end" loop, written as its rounds): one layer that
        // repeats from its file's first frame, as 4.2.0 read it, not one decoder for each round. Before the crossfades, which
        // 4.2.0 never gave such a layer.
        foreach (var track in document.Tracks)
        {
            for (var i = 0; i < track.Clips.Count; i++)
            {
                if (!layers.TryGetValue(track.Clips[i], out var looped) || Rounds(track, i, lookOf) is not { Count: > 0 } rounds || rounds[^1].End < length - 0.001)
                    continue;

                (looped.Duration, looped.FadeOutSeconds) = (0, Math.Clamp(rounds[^1].FadeOutSeconds, 0, TimelineDocument.LongestFade));
                foreach (var round in rounds)
                    layers.Remove(round);
                i += rounds.Count;
            }
        }

        // Crossfades on a track: two clips that have layers are laid over each other across their cut, whatever they show
        // (5.2-AuditB: the former main video's clips too); one without (a clip of the legacy blurred background), and the clip
        // it meets, both dip through what is under them. Each track in the order of its clips, so a clip between two
        // crossfades is laid out for the first before the second; a clip is laid over by one later clip at most (4.2.0 looked
        // for the earlier one by its end, which the first had moved); and a sound that is not heard takes no part (4.2.0
        // never made one).
        var laidOver = new HashSet<Layer>(ReferenceEqualityComparer.Instance);
        foreach (var track in document.Tracks)
        {
            for (var i = 0; i < track.Clips.Count; i++)
            {
                var after = track.Clips[i];
                if (after.TransitionIn <= 0.01 || after.Duration <= 0.001 || Before(track, i) is not { } before)
                    continue;
                if (IsSilentSound(before, track) || IsSilentSound(after, track))
                    continue;

                if (layers.TryGetValue(before, out var earlier) && layers.TryGetValue(after, out var later))
                {
                    if (laidOver.Add(earlier))
                        LayOverAcrossCut(earlier, later, after.TransitionIn);
                    continue;
                }

                var half = Math.Min(after.TransitionIn, Math.Min(before.Duration, after.Duration)) / 2;
                if (layers.TryGetValue(before, out earlier))
                    earlier.FadeOutSeconds = Math.Max(earlier.FadeOutSeconds, half);
                if (layers.TryGetValue(after, out later))
                    later.FadeInSeconds = Math.Max(later.FadeInSeconds, half);
            }
        }

        // The pictures bottom first, each track's in the order of its clips. The captions go under the pictures of the track
        // they are put under (5.1-Polish), whatever it shows.
        var pictures = new List<Layer>();
        var tracksAt = new List<(TimelineTrackModel Track, int From, int To)>();
        var captionsAt = -1;
        foreach (var track in document.Tracks)
        {
            if (track.CaptionsUnder && captionsAt < 0)
                captionsAt = pictures.Count;
            if (!ShowsPictures(track) && track.Kind != TimelineTrackKind.Adjustment)
                continue;

            var from = pictures.Count;
            foreach (var clip in track.Clips)
            {
                if (layers.TryGetValue(clip, out var layer) && layer.IsPicture)
                    pictures.Add(layer);
            }

            tracksAt.Add((track, from, pictures.Count));
        }

        PairLayerCrossfades(pictures);

        // A video from a file whose blur fill is on (4.4; 5.2-AuditB: any clip's, the former main video's too): a blurred copy
        // of its whole picture covering the frame, while it can be seen. Every twin goes at the very bottom of the stack (5.4A,
        // the user's standing rule: Z = 0, behind every picture track; a twin under its own family only covered the tracks below
        // that family, so an upper track's Background Blur hid Track 1's video). The twins go in the order of their tracks and
        // their clips, bottom first: while two clips crossfade their fills crossfade under both, and while an upper track's
        // blurred clip plays over a lower one's, its fill is the one seen. Captions put under a track stay above every twin, as
        // they were above the blurred background.
        var twinList = new List<Layer>();
        foreach (var (track, _, _) in tracksAt)
        {
            foreach (var clip in track.Clips)
            {
                if (layers.TryGetValue(clip, out var layer) && layer is { IsVideoFile: true, IsHidden: false } && (layer.Opacity > 0 || layer.AnimatesOpacity)
                    && LookOf(clip, track).BlurFill == true)
                {
                    twinList.Add(BlurTwinOf(layer, document));
                }
            }
        }

        var twins = new HashSet<Layer>(twinList, ReferenceEqualityComparer.Instance);
        var stack = new List<Layer>(pictures.Count + twinList.Count);
        stack.AddRange(twinList);
        var stackCaptionsAt = -1;
        foreach (var (_, from, to) in tracksAt)
        {
            if (captionsAt >= 0 && stackCaptionsAt < 0 && captionsAt == from && to > from)
                stackCaptionsAt = stack.Count;
            for (var n = from; n < to; n++)
                stack.Add(pictures[n]);
        }

        // (Captions put under a track with no pictures at or above it are over every picture.)
        if (captionsAt >= 0 && stackCaptionsAt < 0)
            stackCaptionsAt = stack.Count;

        // The sounds heard, in the order of the tracks: each layer's own, moved by its J or L cut when it has one.
        var sounds = new List<Layer>();
        foreach (var track in document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (layers.TryGetValue(clip, out var layer) && layer.CarriesSound && IsHeard(clip, track))
                    sounds.Add(SoundOf(layer, clip, document));
            }
        }

        return new Lowered
        {
            SequenceSeconds = program,
            BlurTwins = twins,
            Stack = stack,
            CaptionsAt = stackCaptionsAt,
            LayerAudio = sounds,
            SequenceEffects = document.SequenceEffectsBypassed ? [] : [.. document.SequenceEffects.Select(EffectInstance.FromState)],
            Shared = new SequenceLowering(program, stack, sounds),
        };
    }

    // ----- What the tracks hold -----

    /// <summary>A track whose clips are pictures laid on the canvas (a switch track holds what a video track holds).</summary>
    private static bool ShowsPictures(TimelineTrackModel track) => track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch;

    /// <summary>
    /// The track a track's family is known by: the one it was made to go with (its ParentTrackId), up to one made for none; itself
    /// for a track of its own (and for one whose parent is gone).
    /// </summary>
    internal static TimelineTrackModel FamilyOf(TimelineTrackModel track, TimelineDocument document)
    {
        var at = track;
        for (var depth = 0; depth < 64 && at.ParentTrackId is { } parent && document.Tracks.FirstOrDefault(t => t.Id == parent) is { } over; depth++)
            at = over;
        return at;
    }

    /// <summary><see cref="TimelineDocument.IsHiddenWithParents"/>, with the document's tracks by id.</summary>
    private static bool IsHiddenWithParents(TimelineTrackModel track, Dictionary<Guid, TimelineTrackModel> tracksById)
    {
        TimelineTrackModel? at = track;
        for (var steps = 0; at is not null && steps <= tracksById.Count; steps++)
        {
            if (at.IsHidden)
                return true;
            at = at.ParentTrackId is { } parent && tracksById.TryGetValue(parent, out var over) ? over : null;
        }

        return false;
    }

    /// <summary>
    /// Whether a clip's sound reaches the output: it has some, from whichever stream of its file it plays (each a sound of its
    /// own, LayerAudio), and neither it nor its track is switched off or silenced.
    /// </summary>
    private static bool IsHeard(TimelineClip clip, TimelineTrackModel track) =>
        clip.HasSound && !clip.IsHidden && !clip.IsMuted && !track.IsMuted;

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

    /// <summary>A look as JSON without what differs from round to round of one repeating video: where it is in time, its keys, its name.</summary>
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
        (look.TiltX, look.TiltY, look.KeysTiltX, look.KeysTiltY) = (state.TiltX, state.TiltY, state.KeysTiltX, state.KeysTiltY);
        (look.CropLeft, look.CropTop, look.CropRight, look.CropBottom) = (state.CropLeft, state.CropTop, state.CropRight, state.CropBottom);
        (look.KeysX, look.KeysY, look.KeysScale, look.KeysRotation, look.KeysOpacity) = (state.KeysX, state.KeysY, state.KeysScale, state.KeysRotation, state.KeysOpacity);
        (look.AnchorX, look.AnchorY, look.Effects, look.EffectsBypassed) = (state.AnchorX, state.AnchorY, state.Effects, state.EffectsBypassed);
        return look;
    }

    // ----- Clips as layers -----

    /// <summary>
    /// A clip as the layer the picture and the sound are made from: its look, with its own kind, file, timing, fades,
    /// visibility (its track hides its picture, not its sound), sound (its track's gain added to its own, the track's filters
    /// and ducking), its clip's id and a number for its track. Every clip is made so, on whatever track (5.2-AuditB). Its crop
    /// reframes its picture (4.4-BugHunt, held by Layer.HeldCrop): the layer keeps it, the picture is cut by it where it is
    /// read, and the layer's box takes the shape it leaves (Layer.GetOutputRect, at the clip's own picture size). Keyframes
    /// that change nothing are folded (see <see cref="FoldConstantKeys"/>).
    /// </summary>
    private static Layer MakeLayer(TimelineClip clip, TimelineTrackModel track, int trackNumber, LayerState look, bool trackHidden, TimelineDocument document)
    {
        var made = look.ShallowCopy();
        made.Kind = clip.ContentKind;
        if (clip.SourcePath is { Length: > 0 } path)
            made.ImagePath = path;
        (made.StartTime, made.Duration, made.MediaOffset, made.Speed) = (clip.Start, clip.Duration, clip.MediaOffset, clip.Speed);
        if (clip.SourceDuration > 0)
            made.MediaDuration = clip.SourceDuration;
        (made.FadeInSeconds, made.FadeOutSeconds, made.FadeInStyle, made.FadeOutStyle) = (clip.FadeInSeconds, clip.FadeOutSeconds, clip.FadeInStyle, clip.FadeOutStyle);
        made.TransitionIn = clip.TransitionIn;
        made.IsHidden = clip.IsHidden || trackHidden;
        (made.IsSoundMuted, made.HasAudio, made.AudioStream) = (clip.IsMuted || track.IsMuted, clip.HasSound, Math.Max(clip.AudioStreamIndex, 0));
        made.AudioOffset = clip.AudioLinked || made.Kind == LayerKind.Audio ? 0 : clip.AudioSlipSeconds;
        made.AudioGainDb = clip.GainDb + track.GainDb;
        made.AutoDuck = look.AutoDuck || track.DuckAgainstVoice;
        if (track.AudioFilters is { } filters)
            made.AudioFilters = filters;
        (made.ProxyChoice, made.TrackId, made.IsEmptyTrack, made.IsDeleted) = (clip.ProxyChoice, trackNumber, false, false);

        var layer = Layer.FromState(made, look.ImageWidth, look.ImageHeight, document.Width, document.Height);
        layer.ClipId = clip.Id;
        FoldConstantKeys(layer);
        return layer;
    }

    /// <summary>
    /// The blurred copy a video from a file fills the frame with (4.4): the same file, timing, fades, opacity (its keys too),
    /// visibility and clip, no sound, place keys, crop, mask or effects of its own, in a box that is the frame. Its whole
    /// picture is scaled to cover the frame about its middle and blurred as the sequence's blur fill says (BackgroundZoom,
    /// BlurRadius, BlurPasses, BackgroundDim, BackgroundEffects) where it is written (GpuScenePlan's Twin, <see cref="Lowered.BlurTwins"/>).
    /// </summary>
    private static Layer BlurTwinOf(Layer layer, TimelineDocument document)
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

        var made = Layer.FromState(twin, layer.ImageWidth, layer.ImageHeight, document.Width, document.Height);
        (made.PictureFadeInOff, made.PictureFadeOutOff) = (layer.PictureFadeInOff, layer.PictureFadeOutOff);
        made.ClipId = layer.ClipId;
        return made;
    }

    /// <summary>
    /// The layer whose sound is heard for a clip: the clip's own, or, for a J or L cut, a copy (its clip's id with it) whose
    /// sound starts that much sooner (into the file from as much earlier, as far as the file has: a crossfade may have taken
    /// some of it) and lasts that much longer, so that picture and sound stay on one moment of the file. An unlinked clip's
    /// slip is its <see cref="Layer.AudioOffset"/>, which moves where its sound starts.
    /// </summary>
    private static Layer SoundOf(Layer layer, TimelineClip clip, TimelineDocument document)
    {
        var (lead, tail) = clip.AudioLinked ? (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) : (0, 0);
        if (Math.Abs(lead) < 0.0005 && Math.Abs(tail) < 0.0005)
            return layer;

        var sound = Layer.FromState(layer.ToState(), layer.ImageWidth, layer.ImageHeight, document.Width, document.Height);
        sound.ClipId = layer.ClipId;
        var speed = MediaSpeed(layer);
        var early = Math.Min(Math.Min(lead, layer.StartTime), PlaysFile(layer) ? layer.MediaOffset / speed : double.PositiveInfinity);
        sound.StartTime = layer.StartTime - early;
        sound.MediaOffset = Math.Max(layer.MediaOffset - early * speed, 0);
        sound.Duration = Math.Max(layer.Duration + early + tail, 0.001);
        return sound;
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
            // (A layer with no fade of its own pairs with none: its look along the rest of the stack is skipped.)
            var under = stack[lower];
            if (!under.IsPicture || under.Duration <= 0.001 || under.FadeOutSeconds <= 0.01 && under.FadeInSeconds <= 0.01)
                continue;

            var (underIn, underOut) = (under.StartTime, under.StartTime + under.Duration);
            for (var upper = lower + 1; upper < stack.Count; upper++)
            {
                var over = stack[upper];
                if (!over.IsPicture || over.Duration <= 0.001 || over.TrackId == under.TrackId)
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

// ----- The timeline lowered, as the GPU engine reads it (Pass 5.0-AudioAnd3D) -----
// The GPU scene (Services/Gpu/GpuScenePlan) is built on the very lowering the sound and the exports are made from (Lower,
// Lowered): the same layers, the same twins, the same stack in the same order. Nothing of the lowering is changed or made
// public here: this partial only hands over a read-only view of it, and the facts of the document the GPU's picking, labels
// and 3D places need (which clip each layer was made from, and on which track).

/// <summary>
/// A state's timeline as <see cref="TimelineLowering"/> lowers it, for the GPU scene (<c>GpuScenePlan.Create</c>): the
/// lowering's own lists, shared with everything built from the same document (read them, never change them), and which
/// clip of the document each layer of the stack stands for.
/// </summary>
internal sealed class GpuSceneLowering
{
    public required double SequenceSeconds { get; init; }

    /// <summary>How long a frame of the sequence lasts, in seconds (the shutter's measure, 5.1-GpuFx): the document's frame rate.</summary>
    public double FrameSeconds { get; init; } = 1 / 30.0;

    /// <summary>The blurred copies of the videos whose blur fill is on, all of them under every picture (5.4A).</summary>
    public required IReadOnlySet<Layer> BlurTwins { get; init; }

    /// <summary>The pictures bottom first: the twins first (5.4A), then a track's in the order of its clips.</summary>
    public required IReadOnlyList<Layer> Stack { get; init; }

    /// <summary>Where the captions are among <see cref="Stack"/> (5.1-Polish): under the layer at this index and those above; -1 over everything.</summary>
    public required int CaptionsAt { get; init; }

    public required IReadOnlyList<EffectInstance> SequenceEffects { get; init; }

    /// <summary>The clip each layer of <see cref="Stack"/> was made from, by its <see cref="Layer.ClipId"/> (a twin: its video's clip).</summary>
    public required IReadOnlyDictionary<Layer, TimelineClip> ClipOf { get; init; }

    /// <summary>Where each layer's clip is among the document's tracks (0 the bottom), for the 3D view's labels and picking.</summary>
    public required IReadOnlyDictionary<Layer, int> TrackOf { get; init; }
}

public static partial class TimelineLowering
{
    /// <summary>A state's timeline lowered (from the document's cache, as everything built from it is), as the GPU scene reads it.</summary>
    internal static GpuSceneLowering LowerForGpu(SequenceExportState state)
    {
        var document = state.Timeline;
        var lowered = Lower(document);
        var (clipOf, trackOf) = MatchClips(lowered.Stack, document);
        return new GpuSceneLowering
        {
            SequenceSeconds = lowered.SequenceSeconds,
            FrameSeconds = document.FrameRate.FrameDuration,
            BlurTwins = lowered.BlurTwins,
            Stack = lowered.Stack,
            CaptionsAt = lowered.CaptionsAt,
            SequenceEffects = lowered.SequenceEffects,
            ClipOf = clipOf,
            TrackOf = trackOf,
        };
    }

    /// <summary>
    /// The clip each layer of the stack was made from, and the track it is on, by the clip's id every layer carries
    /// (<see cref="Layer.ClipId"/>: MakeLayer's; a twin's is its video's). A layer whose clip is not found is left out (its
    /// picture is still drawn; it only has no clip to be picked by).
    /// </summary>
    private static (Dictionary<Layer, TimelineClip> ClipOf, Dictionary<Layer, int> TrackOf) MatchClips(IReadOnlyList<Layer> stack, TimelineDocument document)
    {
        var placed = new Dictionary<Guid, (TimelineClip Clip, int Track)>();
        for (var t = 0; t < document.Tracks.Count; t++)
        {
            foreach (var clip in document.Tracks[t].Clips)
                placed.TryAdd(clip.Id, (clip, t));
        }

        var clipOf = new Dictionary<Layer, TimelineClip>(ReferenceEqualityComparer.Instance);
        var trackOf = new Dictionary<Layer, int>(ReferenceEqualityComparer.Instance);
        foreach (var layer in stack)
        {
            if (layer.ClipId == Guid.Empty || !placed.TryGetValue(layer.ClipId, out var found))
                continue;
            clipOf[layer] = found.Clip;
            trackOf[layer] = found.Track;
        }

        return (clipOf, trackOf);
    }
}
