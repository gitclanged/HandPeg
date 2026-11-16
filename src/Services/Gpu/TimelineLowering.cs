using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services.Gpu;

// ----- Timeline lowering: a TimelineDocument compiled once into the inputs for picture and sound -----
// No track is privileged (no main video, primary file or primary chain). Every clip on every track becomes a layer the same way
// (MakeLayer): placed, timed, cropped and styled independently, decoded from its own file (SourcePath) at its own time (MediaOffset
// on the file's clock: MediaStarts, SeekIndexes), with its audio cut at the same origin from its own stream. A Cutout is a clip of
// its file like any other, on its parent's clock. A video with blur fill on (BlurFill) gets a blurred full-frame twin at the bottom
// of the stack, under every picture of every track (an upper track's blur fill never covers a lower track); the scene plan skips a
// twin while the clip above hides it completely. Old-format projects are converted on open (LegacyMainVideo, golden fixtures too),
// so the legacy kinds MainVideo and Video are never produced here.
//
// The lowering also prepares the copies: overlapping crossfades across a track's cuts (between any two clips with layers; otherwise a
// dip through what is underneath), fades paired across tracks, and a video looped to the timeline end read as one repeating source.
// One lowering per document, shared by everything built from it: GPU scene plan, audio program and exports.

/// <summary>
/// A lowered timeline (<see cref="TimelineLowering.Lowering"/>): what picture, sound and measurements need. Shared by everything built
/// from the same document: read-only.
/// </summary>
/// <param name="SequenceSeconds">
/// Sequence length: the canvas runs to its last picture (<see cref="SequenceSpans.ProgramSeconds"/>; an audio tail past the last
/// picture adds no black frames, a standalone sound placed past it still does).
/// </param>
/// <param name="Stack">
/// Pictures as layers, bottom first: all blur twins (in track order), then each track's pictures in clip order. Every layer carries
/// its source clip id (<see cref="Layer.ClipId"/>; a twin carries its video's).
/// </param>
/// <param name="LayerAudio">
/// Audible clip sounds in track order (video clips with audio, and audio clips), each carrying its clip id (including J/L cut sound
/// and the copies it moves).
/// </param>
public sealed record SequenceLowering(double SequenceSeconds, IReadOnlyList<Layer> Stack, IReadOnlyList<Layer> LayerAudio);

public static partial class TimelineLowering
{
    // ----- Keyframes -----
    // A keyframe pins position, size and rotation together, so a clip that only moves has constant size and rotation keys.
    // Compiled as-is they would be evaluated every frame. Constant-valued properties are therefore folded to plain values, and
    // within a changing property, segments between equal keys add nothing to the expression.

    // Equality tolerances: ~1/50 px on a 1920 frame, 1/100 degree.
    private const double SamePlace = 0.00001;
    private const double SameAngle = 0.01;

    // Opacity is whole percent: keys less than one apart are constant.
    private const double SameOpacity = 0.5;

    /// <summary>
    /// Removes keyframes from every property they do not change, setting the property to their single value. Applied to layer copies
    /// prepared for a <see cref="SequenceExportState"/> so only real motion is compiled.
    /// </summary>
    /// <returns>Number of properties folded into plain values.</returns>
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

            // Keep position keys on a resizing layer even when constant: keyed positions are the corner's; unkeyed, the position
            // becomes the anchor's and the layer would scale about it.
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

    /// <summary>A state's lowered timeline (layers and sounds, see <see cref="SequenceLowering"/>) from the per-document cache.</summary>
    public static SequenceLowering Lowering(SequenceExportState state) => Lower(state.Timeline).Shared;

    /// <summary>Internal lowering result (see <see cref="Lower"/>). Shared by everything built from one document: read-only.</summary>
    private sealed class Lowered
    {
        /// <summary>Sequence length: the canvas runs to its last picture (<see cref="SequenceSpans.ProgramSeconds"/>).</summary>
        public required double SequenceSeconds { get; init; }

        /// <summary>Blurred twins of file videos with blur fill on, all placed under every picture.</summary>
        public required IReadOnlySet<Layer> BlurTwins { get; init; }

        /// <summary>Pictures in stacking order, bottom first, including twins; prepared for crossfades.</summary>
        public required IReadOnlyList<Layer> Stack { get; init; }

        /// <summary>
        /// Caption position in the stack: drawn under the <see cref="Stack"/> layer at this index and all above it (count = above all
        /// layers, under whole-frame look and fades); -1 = on top of everything including fades (no track is
        /// <see cref="TimelineTrackModel.CaptionsUnder"/>).
        /// </summary>
        public required int CaptionsAt { get; init; }

        /// <summary>Audible clip sounds: video clips with audio, and audio clips.</summary>
        public required IReadOnlyList<Layer> LayerAudio { get; init; }

        public required IReadOnlyList<EffectInstance> SequenceEffects { get; init; }

        /// <summary>The public record read by everything built from the state (one per lowering).</summary>
        public required SequenceLowering Shared { get; init; }
    }

    // One lowering per document, kept for the document's lifetime: the scene plan, audio program and exports are rebuilt from the
    // same state (and copies sharing its document) repeatedly. Only the document feeds it, so the document alone is the key.
    private static readonly ConditionalWeakTable<TimelineDocument, Lowered> LoweredTimelines = new();

    /// <summary>Lowers a document, from cache when already lowered.</summary>
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
        // A document still carrying legacy track looks (built in code, or never installed) is lowered from a copy with the looks
        // folded into its clips (TimelineDocument.FoldTrackLooks); editor documents are already folded.
        if (document.HasTrackLooks)
        {
            document = document.Clone();
            document.FoldTrackLooks();
        }

        // Total timeline length bounds videos looped "to the end"; the canvas runs to the last picture (no black frames for an
        // audio tail past it).
        var length = document.TotalDuration;
        var program = SequenceSpans.ProgramSeconds(document);

        // Tracks by id so IsHiddenWithParents is one lookup per step (a linear scan per clip made many-track documents slow to
        // lower after every edit).
        var tracksById = new Dictionary<Guid, TimelineTrackModel>(document.Tracks.Count);
        foreach (var track in document.Tracks)
            tracksById.TryAdd(track.Id, track);

        // Every clip as a layer, numbered by track (cross-track crossfades pair clips of two tracks). The caption box is the
        // sequence's (CaptionLayer), and legacy blurred-background clips draw nothing (blur fill is per clip).
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
                    layers[clip] = MakeLayer(clip, track, t + 1, clip.State, hidden, document);
            }
        }

        // A video looped to the timeline end (legacy "to the end" loop stored as rounds) becomes one layer repeating from the file's
        // first frame, not one decoder per round. Done before crossfades, which never apply to such a layer.
        foreach (var track in document.Tracks)
        {
            for (var i = 0; i < track.Clips.Count; i++)
            {
                if (!layers.TryGetValue(track.Clips[i], out var looped) || Rounds(track, i) is not { Count: > 0 } rounds || rounds[^1].End < length - 0.001)
                    continue;

                (looped.Duration, looped.FadeOutSeconds) = (0, Math.Clamp(rounds[^1].FadeOutSeconds, 0, TimelineDocument.LongestFade));
                foreach (var round in rounds)
                    layers.Remove(round);
                i += rounds.Count;
            }
        }

        // Same-track crossfades: two clips with layers overlap across their cut regardless of content; if either has no layer
        // (legacy blurred-background clip), both dip through what is underneath. Clips are processed in order, so a clip between
        // two crossfades is laid out for the first before the second; each clip is overlapped by at most one later clip; inaudible
        // sounds take no part.
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

        // Pictures bottom first, each track's in clip order. Captions go under the pictures of their CaptionsUnder track, whatever
        // it holds.
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

        // Blur fill: each visible file video with BlurFill gets a blurred full-frame twin. All twins go at the very bottom (Z = 0,
        // behind every picture track; a twin placed under its own track family hid lower tracks' videos). Twins are ordered by track
        // and clip, bottom first: crossfading clips crossfade their fills, and an upper track's fill wins over a lower one's.
        // Captions placed under a track stay above every twin.
        var twinList = new List<Layer>();
        foreach (var (track, _, _) in tracksAt)
        {
            foreach (var clip in track.Clips)
            {
                if (layers.TryGetValue(clip, out var layer) && layer is { IsVideoFile: true, IsHidden: false } && (layer.Opacity > 0 || layer.AnimatesOpacity)
                    && clip.State.BlurFill == true)
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

        // Captions under a track with no pictures at or above it go above every picture.
        if (captionsAt >= 0 && stackCaptionsAt < 0)
            stackCaptionsAt = stack.Count;

        // Audible sounds in track order: each layer's own, shifted by its J/L cut if any.
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

    // ----- Track contents -----

    /// <summary>True for tracks whose clips are canvas pictures (switch tracks hold what video tracks hold).</summary>
    private static bool ShowsPictures(TimelineTrackModel track) => track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch;

    /// <summary>
    /// Root of a track's family: follows ParentTrackId up to a track with no parent; the track itself when standalone (or its parent is
    /// missing).
    /// </summary>
    internal static TimelineTrackModel FamilyOf(TimelineTrackModel track, TimelineDocument document)
    {
        var at = track;
        for (var depth = 0; depth < 64 && at.ParentTrackId is { } parent && document.Tracks.FirstOrDefault(t => t.Id == parent) is { } over; depth++)
            at = over;
        return at;
    }

    /// <summary><see cref="TimelineDocument.IsHiddenWithParents"/> using a track-by-id dictionary.</summary>
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
    /// True when a clip's audio reaches the output: it has sound (from whichever stream it plays; each is its own LayerAudio entry), and
    /// neither it nor its track is hidden or muted.
    /// </summary>
    private static bool IsHeard(TimelineClip clip, TimelineTrackModel track) =>
        clip.HasSound && !clip.IsHidden && !clip.IsMuted && !track.IsMuted;

    /// <summary>True for an audio-only clip (audio clip, or any clip on an audio track) that is not audible.</summary>
    private static bool IsSilentSound(TimelineClip clip, TimelineTrackModel track) =>
        (clip.ContentKind == LayerKind.Audio || track.Kind == TimelineTrackKind.Audio) && !IsHeard(clip, track);

    /// <summary>
    /// The clip whose end meets the start of clip <paramref name="index"/> (within 11 ms), or null: the previous clip on a track without
    /// overlaps, otherwise any matching clip.
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
    /// Loop rounds following clip <paramref name="index"/> (legacy "to the end" loop stored as clips): the first clip plays its file to the
    /// end, then each butted round replays it from frame 0 with the same look (keys continuing) and muted sound, all whole except the last,
    /// with no fade or crossfade at the joins. Empty when no round follows.
    /// </summary>
    private static List<TimelineClip> Rounds(TimelineTrackModel track, int index)
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
        var look = first.State;
        var previous = first;
        for (var j = index + 1; j < track.Clips.Count; j++)
        {
            var round = track.Clips[j];
            if (round.ContentKind != LayerKind.VideoFile || round.SourcePath != first.SourcePath || round.Speed != first.Speed
                || round.IsHidden != first.IsHidden || !round.IsMuted || round.MediaOffset > 1e-6 || round.MediaEnd > file + 0.001
                || Math.Abs(round.Start - previous.End) > 1e-6 || round.FadeInSeconds > 0.001 || round.TransitionIn > 0.001
                || !SameLook(round.State, look, round.Start - first.Start, ref firstTimeless))
            {
                break;
            }

            rounds.Add(round);
            previous = round;

            // A round ending before the file's end, or fading out, is the last.
            if (!Whole(round) || round.FadeOutSeconds > 0.001)
                break;
        }

        return rounds;
    }

    /// <summary>
    /// True when two looks match except for timing and their keys form one motion: the later clip's keys fall <paramref name="shift"/>
    /// seconds later on the first clip's clock (within a few microseconds; cuts and trims round keys to the microsecond).
    /// <paramref name="firstTimeless"/> caches the first look's JSON for subsequent comparisons.
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

    /// <summary>True when a look has time-anchored data: keys, or effect keys or ranges.</summary>
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

    /// <summary>A look as JSON minus the fields that differ between loop rounds (timing, keys, name, etc.).</summary>
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

    // ----- Clips as layers -----

    /// <summary>
    /// Builds the layer for a clip on any track: its look plus kind, file, timing, fades, visibility (a hidden track hides the picture, not
    /// the sound), audio (track gain added to clip gain, track filters and ducking), clip id and track number. The crop reframes the picture
    /// (Layer.HeldCrop): applied at decode, with the box taking the remaining aspect (Layer.GetOutputRect at native size). Constant keys are
    /// folded (<see cref="FoldConstantKeys"/>).
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
    /// Blur fill twin of a file video: same file, timing, fades, opacity (with keys), visibility and clip; no sound, position keys, crop,
    /// mask or effects; box = frame. The whole picture is scaled to cover the frame about its centre and blurred per the sequence's blur
    /// fill settings (BackgroundZoom, BlurRadius, BlurPasses, BackgroundDim, BackgroundEffects) in GpuScenePlan's Twin
    /// (<see cref="Lowered.BlurTwins"/>).
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
    /// The audio layer for a clip: the clip's own, or for a J/L cut a copy (same clip id) starting earlier by the lead (reading the file
    /// from correspondingly earlier, as far as it allows; a crossfade may have consumed some) and lasting longer by the tail, keeping
    /// picture and sound on the same file time. An unlinked clip's slip is its <see cref="Layer.AudioOffset"/>.
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

    // ----- Same-track and cross-track crossfades -----

    /// <summary>Playback speed of a layer's media (clamped).</summary>
    private static double MediaSpeed(Layer clip) => clip.HasSpeed ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

    /// <summary>True when a layer plays a span of a file (video or audio), so its start maps to a file time.</summary>
    private static bool PlaysFile(Layer clip) => clip.IsVideoFile || clip.IsAudio;

    /// <summary>Media remaining after a layer's end, in timeline seconds; infinite for stills and text.</summary>
    private static double MediaAfter(Layer clip) => PlaysFile(clip) && clip.MediaDuration > 0.05
        ? Math.Max((clip.MediaDuration - clip.MediaOffset - clip.Duration * MediaSpeed(clip)) / MediaSpeed(clip), 0)
        : double.PositiveInfinity;

    /// <summary>Media available before a layer's start, in timeline seconds; infinite for stills and text.</summary>
    private static double MediaBefore(Layer clip) => PlaysFile(clip) ? clip.MediaOffset / MediaSpeed(clip) : double.PositiveInfinity;

    /// <summary>
    /// Overlaps two same-track layers across a crossfade cut: the earlier extends past the cut and the later starts before it, into the
    /// available media (up to half the crossfade each). The later picture fades in over the earlier (clips stack in start order) while the
    /// sounds cross-fade over the same span. When neither file has media beyond the cut, both dip picture and sound through what is below.
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
    /// Shifts a layer's keys and its effects' keys and ranges by <paramref name="seconds"/> (rounded to the millisecond like
    /// <see cref="Layer.ShiftKeys"/>) when its start moved but its content did not. Ranges are not clamped at 0 (a range before the clip
    /// stays there and never applies).
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
    /// Pairs cross-track fades (Crossfade with Overlapping Clip, or matching fades): when a clip fades out while a clip above fades in over
    /// the same span, the lower keeps its picture and only its sound fades, so the upper fade carries the picture transition and nothing dips
    /// through to what is below both. <paramref name="stack"/> is bottom first.
    /// </summary>
    private static void PairLayerCrossfades(List<Layer> stack)
    {
        static bool Same(double a, double b) => Math.Abs(a - b) < 0.02;
        for (var lower = 0; lower < stack.Count; lower++)
        {
            // A layer without fades cannot pair: skip scanning the rest of the stack.
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

                // Upper fades in as lower fades out: the lower stays opaque until it ends underneath.
                if (under.FadeOutSeconds > 0.01 && Same(over.FadeInSeconds, under.FadeOutSeconds) && Same(overIn, underOut - under.FadeOutSeconds))
                    under.PictureFadeOutOff = true;

                // Upper fades out as lower fades in: the lower is already opaque underneath.
                if (under.FadeInSeconds > 0.01 && Same(over.FadeOutSeconds, under.FadeInSeconds) && Same(underIn, overOut - over.FadeOutSeconds))
                    under.PictureFadeInOff = true;
            }
        }
    }
}

// ----- GPU view of the lowering -----
// The GPU scene (Services/Gpu/GpuScenePlan) is built on the same lowering as audio and exports (Lower, Lowered): same layers, twins
// and stack order. This partial exposes a read-only view plus the document facts needed for GPU picking, labels and 3D placement
// (source clip and track of each layer).

/// <summary>
/// A state's lowered timeline for the GPU scene (<c>GpuScenePlan.Create</c>): the lowering's shared lists (read-only) and the source clip
/// of each stack layer.
/// </summary>
internal sealed class GpuSceneLowering
{
    public required double SequenceSeconds { get; init; }

    /// <summary>Sequence frame duration in seconds (from the document frame rate; used for shutter timing).</summary>
    public double FrameSeconds { get; init; } = 1 / 30.0;

    /// <summary>Blurred twins of videos with blur fill on, all under every picture.</summary>
    public required IReadOnlySet<Layer> BlurTwins { get; init; }

    /// <summary>Pictures bottom first: twins, then each track's layers in clip order.</summary>
    public required IReadOnlyList<Layer> Stack { get; init; }

    /// <summary>Caption position in <see cref="Stack"/>: under the layer at this index and those above; -1 = on top of everything.</summary>
    public required int CaptionsAt { get; init; }

    public required IReadOnlyList<EffectInstance> SequenceEffects { get; init; }

    /// <summary>Source clip of each <see cref="Stack"/> layer by <see cref="Layer.ClipId"/> (a twin maps to its video's clip).</summary>
    public required IReadOnlyDictionary<Layer, TimelineClip> ClipOf { get; init; }

    /// <summary>Track index of each layer's clip (0 = bottom), for 3D view labels and picking.</summary>
    public required IReadOnlyDictionary<Layer, int> TrackOf { get; init; }
}

public static partial class TimelineLowering
{
    /// <summary>A state's lowered timeline (from the per-document cache) as the GPU scene reads it.</summary>
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
    /// Maps each stack layer to its source clip and track via <see cref="Layer.ClipId"/> (set by MakeLayer; a twin carries its video's).
    /// Layers whose clip is not found are omitted (still drawn, just not pickable).
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
