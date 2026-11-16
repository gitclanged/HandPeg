using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

// ----- The timeline transaction: ripple, a clip's look copied and split, and two clips joined back (split from TimelineTransaction.cs, 5.4.1-M5) -----
internal sealed partial class TimelineTransaction
{
    // ----- Ripple -----

    /// <summary>
    /// The track a ripple starts on, or every unlocked track but the subtitles (which never ripple, as in 4.2.0); with them
    /// (4.4) the unlocked tracks that lie under any of them, the captions' lane of a mini-stack among them.
    /// </summary>
    private List<TimelineTrackModel> RippleTracks(TimelineTrackModel track, bool all) =>
        WithChildTracks(all ? _doc.Tracks.Where(t => !t.IsLocked && (ReferenceEquals(t, track) || t.Kind != TimelineTrackKind.Subtitle)).ToList() : [track]);

    /// <summary>On each track, where the clips that start at or after <paramref name="from"/> begin.</summary>
    private static int[] FirstMovers(IReadOnlyList<TimelineTrackModel> tracks, double from)
    {
        var first = new int[tracks.Count];
        for (var t = 0; t < tracks.Count; t++)
        {
            var index = tracks[t].Clips.FindIndex(c => c.Start >= from - Slack);
            first[t] = index < 0 ? tracks[t].Clips.Count : index;
        }
        return first;
    }

    /// <summary>
    /// How much of <paramref name="delta"/> the clips from <paramref name="from"/> can move: all of it later; earlier,
    /// only as far as lets none run into a clip that stays nor before the timeline's start (its sound neither).
    /// <paramref name="moving"/> is a clip whose end moves with them, so that it holds none of them back, and so are its
    /// <paramref name="riders"/> (4.4: its children that a ripple trim of it carries, <see cref="Riders"/>).
    /// </summary>
    private double RippleLimit(IReadOnlyList<TimelineTrackModel> tracks, double from, double delta, TimelineClip? moving,
                               List<(TimelineTrackModel Track, TimelineClip Clip)>? riders = null) =>
        Limit(tracks, FirstMovers(tracks, from), delta, moving, riders);

    private static double Limit(IReadOnlyList<TimelineTrackModel> tracks, int[] first, double delta, TimelineClip? moving,
                                List<(TimelineTrackModel Track, TimelineClip Clip)>? riders)
    {
        if (delta >= 0)
            return delta;

        for (var t = 0; t < tracks.Count; t++)
        {
            var clips = tracks[t].Clips;
            for (var i = first[t]; i < clips.Count; i++)
                delta = Math.Max(delta, LeadOf(clips[i]) - clips[i].Start);
            if (Staying(clips, first[t], moving, riders) is { } stays)
                delta = Math.Max(delta, stays.End - clips[first[t]].Start);
        }

        // Held back, a move earlier never turns into one later.
        return Math.Min(delta, 0);
    }

    /// <summary>
    /// The clip on a track that stays where it is just before the first that moves (<paramref name="first"/>), or null: not
    /// <paramref name="moving"/>, whose end moves with them; riders (whose ends move with it, or which go) are passed over.
    /// </summary>
    private static TimelineClip? Staying(List<TimelineClip> clips, int first, TimelineClip? moving, List<(TimelineTrackModel Track, TimelineClip Clip)>? riders)
    {
        if (first <= 0 || first >= clips.Count)
            return null;

        var before = first - 1;
        while (before >= 0 && riders is not null && Holds(riders, clips[before]))
            before--;
        return before >= 0 && !ReferenceEquals(clips[before], moving) ? clips[before] : null;
    }

    /// <summary>
    /// Moves every clip that starts at or after <paramref name="from"/> on these tracks by one amount (see
    /// <see cref="RippleLimit"/>), and looks at the crossfades of every track again. Returns the amount moved.
    /// </summary>
    private double Ripple(IReadOnlyList<TimelineTrackModel> tracks, double from, double delta, TimelineClip? moving,
                          List<(TimelineTrackModel Track, TimelineClip Clip)>? riders = null)
    {
        var first = FirstMovers(tracks, from);
        delta = Limit(tracks, first, delta, moving, riders);
        var moves = Math.Abs(delta) > Slack / 1000;
        var whole = WholeFrames(delta);
        for (var t = 0; t < tracks.Count; t++)
        {
            var clips = tracks[t].Clips;
            var stays = Staying(clips, first[t], moving, riders);
            for (var i = first[t]; moves && i < clips.Count; i++)
            {
                var clip = clips[i];
                Remember(clip);
                var start = Moved(clip.Start, delta, whole);

                // Put back on its frame, the first to move may land a microsecond before the end of a free clip that holds it back: it meets it instead.
                if (i == first[t] && stays is not null && start < stays.End)
                    start = stays.End;
                SetSpan(clip, start, Moved(clip.End, delta, whole));
            }

            // Even when nothing could move: the edit that called may have parted a crossfade's clips.
            Reconcile(tracks[t]);
        }
        return moves ? delta : 0;
    }

    // ----- A speed that ripples (5.6.0) -----

    /// <summary>
    /// A clip's speed set (<see cref="Respeed"/>), and everything that started at or after its old end on the ripple tracks moved by
    /// as much as its end moved, earlier or later (as far as a ripple can move them earlier: <see cref="Ripple"/>). Respeed itself moves
    /// what the clip grows into on its own track; when it did, the ripple moves the other tracks alone, so nothing moves twice. Grown,
    /// its speed family grows here too, after the ripple (5.6.0); shrunk, the caller has set theirs first.
    /// </summary>
    private double RespeedRippling(TimelineClip clip, double speed, bool free, bool all)
    {
        var track = Editable(TrackOf(clip));
        var (start, oldEnd) = (clip.Start, clip.End);
        var index = track.IndexOf(clip);
        var next = index >= 0 && index + 1 < track.Clips.Count ? track.Clips[index + 1] : null;
        var nextStart = next?.Start ?? 0;

        // 5.6.0: what rides it, as what rides a ripple trim of its end does (Riders): its children on its end or in the stretch it may give
        // up, taken on its old span (from its start: one that ends before its new end is never run into, as every clip that moves lands at
        // or after that end), so that none holds the ripple back. Its speed family is not among them: the caller sets their speed as its own
        // (first when it shrinks), and when it grows they are respeeded here, after the ripple (below).
        var riders = Riders(clip, head: false, lostFrom: start);
        riders?.RemoveAll(r => IsSpeedFamily(clip, r.Clip));
        if (riders is { Count: 0 })
            riders = null;

        var set = Respeed(clip, speed, free);
        var delta = clip.End - oldEnd;
        if (Math.Abs(delta) <= Slack / 1000)
            return set;

        var tracks = RippleTracks(track, all);
        if (next is not null && Math.Abs(next.Start - nextStart) > Slack / 1000)
            tracks.RemoveAll(t => ReferenceEquals(t, track));
        var whole = WholeFrames(delta);

        // 5.6.0: shrunk, how far what follows can come back (the answer Ripple's own Limit gives after the Ride: Staying passes over
        // riders, and none starts at or after its old end). Held back (another track's clips meet across the cut), nothing comes back into
        // a rider's place: one left too short stays where it is, unlinked, as on a track the ripple does not close up, and never goes.
        // Only those still linked then pass for riders, so one left where it was holds back what would slide into it on its own track.
        var held = delta < 0 && RippleLimit(tracks, oldEnd, delta, clip, riders) > delta + Slack / 1000;
        if (riders is not null && delta < 0)
            Ride(riders, start, oldEnd, clip.End, delta, whole, head: false, free, held ? [] : tracks);
        var seams = SeamsAt(tracks, oldEnd);
        var riding = held ? riders?.Where(r => r.Clip.ParentClipId is not null).ToList() : riders;
        Ripple(tracks, oldEnd, delta, clip, riding is { Count: > 0 } ? riding : null);
        if (riders is not null && delta > 0)
            Ride(riders, start, oldEnd, clip.End, delta, whole, head: false, free, tracks);

        // 5.6.0: grown, its speed family (its streams' lanes, its Cutouts) grows now, after the ripple moved what follows (so nothing
        // moves twice) and before the crossfades are looked at, so that a lane's clip meets the one after it again and keeps its crossfade.
        // The caller's own pass over them afterwards finds each at its speed already (and still sets them when its end could not move).
        if (delta > 0)
        {
            foreach (var child in _doc.Tracks.SelectMany(t => t.Clips).Where(c => IsSpeedFamily(clip, c)).ToList())
                Respeed(child, speed, free);
        }
        KeepCrossfades(seams);
        return set;
    }

    /// <summary>A clip of a speed's family (5.6.0): its child that plays its own file, at the same moment (MainViewModel.SetClipSpeed's test).</summary>
    private static bool IsSpeedFamily(TimelineClip clip, TimelineClip child) =>
        child.ParentClipId == clip.Id && child.PlaysFile && string.Equals(child.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>On each track, the clip a ripple from <paramref name="from"/> moves first and the clip just before it (5.6.0), where both exist.</summary>
    private static List<(TimelineTrackModel Track, TimelineClip Before, TimelineClip First)> SeamsAt(IReadOnlyList<TimelineTrackModel> tracks, double from)
    {
        var first = FirstMovers(tracks, from);
        var seams = new List<(TimelineTrackModel Track, TimelineClip Before, TimelineClip First)>();
        for (var t = 0; t < tracks.Count; t++)
        {
            if (first[t] > 0 && first[t] < tracks[t].Clips.Count)
                seams.Add((tracks[t], tracks[t].Clips[first[t] - 1], tracks[t].Clips[first[t]]));
        }
        return seams;
    }

    /// <summary>
    /// The crossfades a speed's ripple closed up again (5.6.0): its Respeed (and its family's, set first when it shrinks) parted each
    /// seam before the ripple brought what follows back, or (grown) the ripple moved what follows before its family grew back up to it
    /// (RespeedRippling), and Reconcile took their crossfades. Where two clips that met before this
    /// transaction meet again, the crossfade they had comes back, held to what fits (as Crossfade holds it).
    /// </summary>
    private void KeepCrossfades(List<(TimelineTrackModel Track, TimelineClip Before, TimelineClip First)> seams)
    {
        foreach (var (track, before, first) in seams)
        {
            var at = track.IndexOf(first);
            if (at <= 0 || !ReferenceEquals(track.Clips[at - 1], before) || !Meet(before, first))
                continue;

            var (was, wasFirst) = (_clipsBefore.GetValueOrDefault(before, before), _clipsBefore.GetValueOrDefault(first, first));
            if (Math.Abs(was.End - wasFirst.Start) > Slack || !(wasFirst.TransitionIn > first.TransitionIn))
                continue;

            Remember(first);
            first.TransitionIn = Math.Min(wasFirst.TransitionIn, Math.Min(TimelineDocument.LongestCrossfade, Math.Min(before.Duration, first.Duration)));
        }
    }

    // ----- A clip's look, copied before it is changed -----

    internal static readonly JsonSerializerOptions CopyOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>A deep copy of a look: what one clip may change without changing another's.</summary>
    internal static LayerState CopyOf(LayerState state) =>
        JsonSerializer.Deserialize<LayerState>(JsonSerializer.SerializeToUtf8Bytes(state, CopyOptions), CopyOptions) ?? new LayerState();

    /// <summary>
    /// A look with every keyframe, effect keyframe and effect range moved by <paramref name="seconds"/>, as
    /// <see cref="Layer.ShiftKeys"/> moves a layer's, but to the microsecond (the grain of a quantized time, so that keys
    /// stay on their frames at NTSC rates) and with ranges not held at 0 (so that a trim and its undoing give the range
    /// back whole): for a clip whose start moved while what it shows stayed. A copy, unless <paramref name="inPlace"/> or
    /// nothing moves.
    /// </summary>
    private static LayerState Shifted(LayerState state, double seconds, bool inPlace = false)
    {
        if (seconds == 0 || !IsTimed(state))
            return state;

        var look = inPlace ? state : CopyOf(state);
        foreach (var channel in LayerState.MotionChannels)
            channel.Set(look, Shift(channel.Get(look)));
        foreach (var effect in look.Effects ?? [])
        {
            if (effect.Keys is { } keys)
            {
                foreach (var name in keys.Keys.ToList())
                    keys[name] = Shift(keys[name]) ?? [];
            }

            if (effect.Range is [var from, var to] && double.IsFinite(from + seconds) && double.IsFinite(to + seconds))
                effect.Range = [Math.Round(from + seconds, 6), Math.Round(to + seconds, 6)];
        }

        // Studio Voice's mix keys (5.2B) count from the clip's start as its motion does, so they stay on the same moments of its sound.
        if (look.AudioFilters is { } filters)
            filters.StudioVoiceMixKeys = Shift(filters.StudioVoiceMixKeys);
        return look;

        List<Keyframe>? Shift(List<Keyframe>? keys) =>
            keys?.Select(k => k with { Time = Math.Round(k.Time + seconds, 6) }).ToList();
    }

    /// <summary>
    /// Whether a look has anything placed in time: keyframes, an effect's keyframes or its range, Studio Voice's mix keys (5.2B: on the
    /// clip's clock, as its motion is).
    /// </summary>
    private static bool IsTimed(LayerState state) =>
        LayerState.MotionChannels.Any(channel => channel.Get(state) is { Count: > 0 })
        || state.Effects?.Any(e => e.Keys is { Count: > 0 } || e.Range is not null) == true
        || state.AudioFilters?.StudioVoiceMixKeys is { Count: > 0 };

    // ----- A look split with its clip (4.3D): each half keeps its own keyframes, baked at the cut -----

    // How near the cut a keyframe is on it (keys are kept to the microsecond).
    private const double KeyOnCut = 5e-7;

    /// <summary>
    /// The looks of the two halves of a clip split <paramref name="cut"/> seconds into it, each a copy: every keyframe
    /// and effect keyframe goes to the half it is in (the second's counted from the cut), with a key on the cut where a
    /// stretch runs across it (<see cref="SplitKeys"/>); an effect's range that runs across the cut ends there on the first
    /// half and starts there on the second, one wholly on one side moves with it. Neither half jumps or changes its motion.
    /// </summary>
    private static (LayerState First, LayerState Second) SplitLook(LayerState state, double cut)
    {
        if (!IsTimed(state))
            return (state, CopyOf(state));

        var (first, second) = (CopyOf(state), CopyOf(state));
        foreach (var channel in LayerState.MotionChannels)
        {
            var (a, b) = SplitKeys(channel.Get(state), cut);
            channel.Set(first, a);
            channel.Set(second, b);
        }

        for (var i = 0; i < (state.Effects?.Count ?? 0); i++)
        {
            var (effect, firstEffect, secondEffect) = (state.Effects![i], first.Effects![i], second.Effects![i]);
            if (effect.Keys is { } keys)
            {
                foreach (var (name, list) in keys)
                {
                    var (a, b) = SplitKeys(list, cut);
                    (firstEffect.Keys![name], secondEffect.Keys![name]) = (a ?? [], b ?? []);
                }
            }

            if (effect.Range is [var from, var to] && double.IsFinite(from) && double.IsFinite(to))
            {
                if (from < cut - KeyOnCut && to > cut + KeyOnCut)
                    (firstEffect.Range, secondEffect.Range) = ([from, Math.Round(cut, 6)], [0, Math.Round(to - cut, 6)]);
                else
                    secondEffect.Range = [Math.Round(from - cut, 6), Math.Round(to - cut, 6)];
            }
        }

        // Studio Voice's mix keys (5.2B), on the clip's clock: each half keeps its own, so its automation stays on its sound.
        if (state.AudioFilters is { } filters && first.AudioFilters is { } firstFilters && second.AudioFilters is { } secondFilters)
            (firstFilters.StudioVoiceMixKeys, secondFilters.StudioVoiceMixKeys) = SplitKeys(filters.StudioVoiceMixKeys, cut);

        return (first, second);
    }

    /// <summary>
    /// One property's keyframes for the two halves of a clip split <paramref name="cut"/> seconds into it: each half keeps
    /// the keys on its side (the second's moved back by the cut), a key on the cut going to both. Where a stretch runs
    /// across the cut, each half gets a key on it with the value there (as <see cref="EasingCurves.ValueAt"/> works it
    /// out, as the graph does), and each half's stretch is its part of the curve (<see cref="EasingCurves.Cut"/>), so that
    /// the motion on either side is exactly what it was. A half with none of the keys holds the value it had.
    /// </summary>
    private static (List<Keyframe>? First, List<Keyframe>? Second) SplitKeys(List<Keyframe>? keys, double cut)
    {
        if (keys is not { Count: > 0 })
            return (keys, keys is null ? null : []);

        Keyframe Back(Keyframe key) => key with { Time = Math.Round(key.Time - cut, 6) };
        var before = keys.FindLastIndex(k => k.Time < cut - KeyOnCut);
        var after = keys.FindIndex(k => k.Time > cut + KeyOnCut);
        var on = keys.Where(k => Math.Abs(k.Time - cut) <= KeyOnCut).ToList();
        var later = after < 0 ? [] : keys.Skip(after).Select(Back).ToList();
        var earlier = keys.Take(before + 1).ToList();

        if (on.Count > 0)
            return ([.. earlier, .. on], [.. on.Select(Back), .. later]);
        if (before < 0)
            return ([new Keyframe(Math.Round(cut, 6), keys[0].Value, EasingType.Linear)], later);
        if (after < 0)
            return (earlier, [new Keyframe(0, keys[^1].Value, EasingType.Linear)]);

        // A stretch across the cut: a key on it on both sides, and the stretch's curve cut in two.
        var (a, b) = (keys[before], keys[after]);
        var value = EasingCurves.ValueAt(keys, cut);
        var (left, right) = EasingCurves.Cut(a, (cut - a.Time) / (b.Time - a.Time));
        earlier[^1] = left;
        earlier.Add(a.WithPart(0, 1) with { Time = Math.Round(cut, 6), Value = value });
        return (earlier, [right with { Time = 0, Value = value }, .. later]);
    }

    // ----- Two clips joined back into one (4.4-BugHunt): a split undone, where a fold opened at an edge shows its piece -----

    /// <summary>
    /// The look of two clips joined into one, the second starting <paramref name="cut"/> seconds after the first (a split
    /// undone): the first's keys and the second's moved on by the cut (<see cref="JoinKeys"/>), an effect's range that a split
    /// cut in two whole again; with nothing timed in either, the second's look (<paramref name="keepSecond"/>) or the
    /// first's. Null when the two are not one look cut in two: anything else differs, or their keys do not join.
    /// </summary>
    private static LayerState? JoinLook(LayerState first, LayerState second, double cut, bool keepSecond)
    {
        if (!ReferenceEquals(first, second) && Timeless(first) != Timeless(second))
            return null;
        if (!IsTimed(first) && !IsTimed(second))
            return keepSecond ? second : first;

        var joinedChannels = new List<Keyframe>?[LayerState.MotionChannels.Length];
        for (var c = 0; c < joinedChannels.Length; c++)
        {
            var channel = LayerState.MotionChannels[c];
            if (!JoinKeys(channel.Get(first), channel.Get(second), cut, out joinedChannels[c]))
                return null;
        }

        if (!JoinKeys(first.AudioFilters?.StudioVoiceMixKeys, second.AudioFilters?.StudioVoiceMixKeys, cut, out var mix))
            return null;

        var look = CopyOf(first);
        for (var c = 0; c < joinedChannels.Length; c++)
            LayerState.MotionChannels[c].Set(look, joinedChannels[c]);
        if (look.AudioFilters is { } filters)
            filters.StudioVoiceMixKeys = mix;
        for (var i = 0; i < (look.Effects?.Count ?? 0); i++)
        {
            var (a, b, joined) = (first.Effects![i], second.Effects![i], look.Effects![i]);
            if (a.Keys is not null || b.Keys is not null)
            {
                var keys = new Dictionary<string, List<Keyframe>>();
                foreach (var name in (a.Keys?.Keys ?? Enumerable.Empty<string>()).Union(b.Keys?.Keys ?? Enumerable.Empty<string>()))
                {
                    if (!JoinKeys(a.Keys?.GetValueOrDefault(name), b.Keys?.GetValueOrDefault(name), cut, out var list))
                        return null;
                    keys[name] = list ?? [];
                }
                joined.Keys = keys;
            }

            if (!JoinRange(a.Range, b.Range, cut, out var range))
                return null;
            joined.Range = range;
        }
        return look;
    }

    /// <summary>A look as JSON without where its keys, and its effects' keys and ranges, lie in time: what the two halves of a split share.</summary>
    private static string Timeless(LayerState look)
    {
        var node = JsonSerializer.SerializeToNode(look, CopyOptions);
        if (node is JsonObject json)
        {
            foreach (var channel in LayerState.MotionChannels)
                json.Remove(channel.Name);
            if (json[nameof(LayerState.AudioFilters)] is JsonObject filters)
                filters.Remove(nameof(TrackAudioFilters.StudioVoiceMixKeys));

            if (json[nameof(LayerState.Effects)] is JsonArray effects)
            {
                foreach (var effect in effects.OfType<JsonObject>())
                {
                    effect.Remove(nameof(EffectState.Keys));
                    effect.Remove(nameof(EffectState.Range));
                }
            }
        }
        return node?.ToJsonString() ?? "";
    }

    /// <summary>
    /// One property's keys of two clips joined into one, the second starting <paramref name="cut"/> seconds after the first:
    /// the first's keys before the cut, its key on the cut only where the second has none there, and the second's moved on
    /// by the cut; then the key a split put on the cut taken out again where the motion is the same without it
    /// (<see cref="Unsplit"/>). False when the two do not join into one motion: one has keys and the other none, a key lies
    /// past the first's end or before the second's start, or the value is not the same on both sides of the cut.
    /// </summary>
    private static bool JoinKeys(List<Keyframe>? first, List<Keyframe>? second, double cut, out List<Keyframe>? joined)
    {
        joined = first is null ? null : [.. first];
        List<Keyframe> before = first ?? [], after = second ?? [];
        if (before.Count == 0 && after.Count == 0)
            return true;
        if (before.Count == 0 || after.Count == 0 || before.Exists(k => k.Time > cut + KeyOnCut) || after.Exists(k => k.Time < -KeyOnCut))
            return false;

        var value = EasingCurves.ValueAt(before, cut);
        if (Math.Abs(value - EasingCurves.ValueAt(after, 0)) > 1e-9 * Math.Max(1, Math.Abs(value)))
            return false;

        var onCut = after.Exists(k => Math.Abs(k.Time) <= KeyOnCut);
        var keys = before.Where(k => k.Time < cut - KeyOnCut || (!onCut && Math.Abs(k.Time - cut) <= KeyOnCut)).ToList();
        keys.AddRange(after.Select(k => k with { Time = Math.Round(k.Time + cut, 6) }));
        joined = Unsplit(keys, cut);
        return true;
    }

    /// <summary>
    /// Keys joined at a cut without the key a split put on it, where the motion stays the same without it: the key before it
    /// and it leaving by parts of one curve (<see cref="EasingCurves.Cut"/> undone: the two parts one again; Linear and Hold
    /// are whole on both sides), or the key only repeating the value it is reached with. Checked on the curve itself, as
    /// <see cref="EasingCurves.ValueAt"/> (and so the graph) reads it, around the cut; the keys as they are otherwise.
    /// </summary>
    private static List<Keyframe> Unsplit(List<Keyframe> keys, double cut)
    {
        var at = keys.FindIndex(k => Math.Abs(k.Time - cut) <= KeyOnCut);
        if (at < 0)
            return keys;

        var (key, previous) = (keys[at], at > 0 ? keys[at - 1] : (Keyframe?)null);
        var candidates = new List<List<Keyframe>>(2);
        if (previous is { } left && left.Easing == key.Easing
            && (left.BezierX1, left.BezierY1, left.BezierX2, left.BezierY2) == (key.BezierX1, key.BezierY1, key.BezierX2, key.BezierY2))
        {
            var whole = left.Easing is EasingType.Linear or EasingType.Hold;
            if (whole ? !left.IsPart && !key.IsPart : Math.Abs(left.Part.End - key.Part.Start) < 1e-9)
            {
                var rejoined = new List<Keyframe>(keys);
                rejoined[at - 1] = whole ? left : left.WithPart(left.Part.Start, key.Part.End);
                rejoined.RemoveAt(at);
                candidates.Add(rejoined);
            }
        }

        var without = new List<Keyframe>(keys);
        without.RemoveAt(at);
        candidates.Add(without);

        // The motion the key on the cut takes part in: from the key before it to the key after it (a second either way where
        // there is none, past which both hold the same value).
        var (from, to) = (previous?.Time ?? cut - 1, at + 1 < keys.Count ? keys[at + 1].Time : cut + 1);
        foreach (var candidate in candidates)
        {
            if (candidate.Count > 0 && SameMotion(keys, candidate, from, to))
                return candidate;
        }
        return keys;
    }

    /// <summary>Whether two key lists give the same values from <paramref name="from"/> to <paramref name="to"/> (33 moments, to a billionth).</summary>
    private static bool SameMotion(List<Keyframe> a, List<Keyframe> b, double from, double to)
    {
        for (var i = 0; i <= 32; i++)
        {
            var t = from + (to - from) * i / 32;
            var (x, y) = (EasingCurves.ValueAt(a, t), EasingCurves.ValueAt(b, t));
            if (Math.Abs(x - y) > 1e-9 * Math.Max(1, Math.Abs(x)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// An effect's range of two clips joined into one (see <see cref="JoinLook"/>): one range again where a split cut it in two
    /// (the first's ending on the cut, the second's starting on it), or the first's where a split moved it whole into both.
    /// False when the two are not one range: only one has one, or they lie apart.
    /// </summary>
    private static bool JoinRange(double[]? first, double[]? second, double cut, out double[]? joined)
    {
        joined = first is null ? null : [.. first];
        if (first is null && second is null)
            return true;
        if (first is not [var a0, var a1] || second is not [var b0, var b1])
            return false;
        if (Math.Abs(a1 - cut) <= KeyOnCut && Math.Abs(b0) <= KeyOnCut)
        {
            joined = [a0, Math.Round(b1 + cut, 6)];
            return true;
        }
        return Math.Abs(a0 - (b0 + cut)) <= 1e-6 && Math.Abs(a1 - (b1 + cut)) <= 1e-6;
    }

    /// <summary>
    /// A look with every keyframe time, effect keyframe time and effect range scaled by <paramref name="stretch"/> (the old
    /// speed over the new), to the microsecond: a clip played at another speed, its keys staying on what they were set on.
    /// A copy, unless nothing is timed.
    /// </summary>
    private static LayerState Scaled(LayerState state, double stretch)
    {
        if (stretch == 1 || !IsTimed(state))
            return state;

        var look = CopyOf(state);
        foreach (var channel in LayerState.MotionChannels)
            channel.Set(look, Scale(channel.Get(look)));
        foreach (var effect in look.Effects ?? [])
        {
            if (effect.Keys is { } keys)
            {
                foreach (var name in keys.Keys.ToList())
                    keys[name] = Scale(keys[name]) ?? [];
            }

            if (effect.Range is [var from, var to])
                effect.Range = [Math.Round(from * stretch, 6), Math.Round(to * stretch, 6)];
        }

        // Studio Voice's mix keys (5.2B) too: on the clip's clock, they stay on the moments of its sound they were set on.
        if (look.AudioFilters is { } filters)
            filters.StudioVoiceMixKeys = Scale(filters.StudioVoiceMixKeys);
        return look;

        List<Keyframe>? Scale(List<Keyframe>? keys) =>
            keys?.Select(k => k with { Time = Math.Round(k.Time * stretch, 6) }).ToList();
    }

    private static string Seconds(double seconds) => seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
