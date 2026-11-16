using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

// ----- Timeline transaction: ripple, look shifting/splitting, and clip re-joining -----
internal sealed partial class TimelineTransaction
{
    // ----- Ripple -----

    /// <summary>
    /// Ripple track set: the start track, or all unlocked non-subtitle tracks, plus their unlocked child tracks (including mini-stack caption lanes).
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
    /// Achievable portion of <paramref name="delta"/> for clips from <paramref name="from"/>: all of it when later; when earlier, limited by
    /// collisions with stationary clips and time 0 (audio included). <paramref name="moving"/> and its <paramref name="riders"/>
    /// (<see cref="Riders"/>) move with them and never block.
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
    /// Last stationary clip before <paramref name="first"/>, or null; skips <paramref name="moving"/> and riders.
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
    /// Shifts all clips starting at/after <paramref name="from"/> on these tracks by one clamped amount (<see cref="RippleLimit"/>) and
    /// reconciles crossfades. Returns the amount moved.
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

                // Frame snapping can land 1 us before a free-trimmed blocker's end: abut it exactly instead.
                if (i == first[t] && stays is not null && start < stays.End)
                    start = stays.End;
                SetSpan(clip, start, Moved(clip.End, delta, whole));
            }

            // Even when nothing could move: the edit that called may have parted a crossfade's clips.
            Reconcile(tracks[t]);
        }
        return moves ? delta : 0;
    }

    // ----- Rippling speed change -----

    /// <summary>
    /// Sets speed (<see cref="Respeed"/>) and ripples everything from the old end by the end delta (limited as <see cref="Ripple"/>).
    /// If Respeed already pushed its own track, only the other tracks ripple (no double move). On growth the speed family is respeeded
    /// here after the ripple; on shrink the caller respeeds it first.
    /// </summary>
    private double RespeedRippling(TimelineClip clip, double speed, bool free, bool all)
    {
        var track = Editable(TrackOf(clip));
        var (start, oldEnd) = (clip.Start, clip.End);
        var index = track.IndexOf(clip);
        var next = index >= 0 && index + 1 < track.Clips.Count ? track.Clips[index + 1] : null;
        var nextStart = next?.Start ?? 0;

        // Riders as for a ripple trim (Riders), computed on the old span so none blocks the ripple. The speed family is excluded: the
        // caller respeeds it (first on shrink) or it is respeeded below after the ripple (on growth).
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

        // On shrink, compute how far followers can return (Ripple's limit after riding). If blocked by another track, nothing fills a rider's
        // gap: short riders are unlinked in place rather than removed, and only still-linked riders are skipped as blockers.
        var held = delta < 0 && RippleLimit(tracks, oldEnd, delta, clip, riders) > delta + Slack / 1000;
        if (riders is not null && delta < 0)
            Ride(riders, start, oldEnd, clip.End, delta, whole, head: false, free, held ? [] : tracks);
        var seams = SeamsAt(tracks, oldEnd);
        var riding = held ? riders?.Where(r => r.Clip.ParentClipId is not null).ToList() : riders;
        Ripple(tracks, oldEnd, delta, clip, riding is { Count: > 0 } ? riding : null);
        if (riders is not null && delta > 0)
            Ride(riders, start, oldEnd, clip.End, delta, whole, head: false, free, tracks);

        // On growth, respeed the speed family (Sub-Lane streams, Cutouts) after the ripple (no double move) and before crossfade reconciliation
        // so lane clips re-abut and keep crossfades. The caller's later pass is then a no-op (still applied when the end could not move).
        if (delta > 0)
        {
            foreach (var child in _doc.Tracks.SelectMany(t => t.Clips).Where(c => IsSpeedFamily(clip, c)).ToList())
                Respeed(child, speed, free);
        }
        KeepCrossfades(seams);
        return set;
    }

    /// <summary>Speed-family test: a child playing the same file at the same time (as MainViewModel.SetClipSpeed).</summary>
    private static bool IsSpeedFamily(TimelineClip clip, TimelineClip child) =>
        child.ParentClipId == clip.Id && child.PlaysFile && string.Equals(child.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Per track, the first clip a ripple from <paramref name="from"/> moves and its predecessor, when both exist.</summary>
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
    /// Restores crossfades lost while a rippling respeed temporarily separated seams (RespeedRippling, then Reconcile): pairs that abutted
    /// before the transaction and abut again regain their crossfade, clamped as Crossfade does.
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
    /// Shifts all keyframes and effect keys/ranges by <paramref name="seconds"/> like <see cref="Layer.ShiftKeys"/>, but rounded to 1 us
    /// (keeps NTSC keys on frames) and without clamping ranges at 0 (trim and undo round-trip). Used when the start moves but content stays.
    /// Returns a copy unless <paramref name="inPlace"/> or zero shift.
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

        // Studio Voice mix keys are clip-relative too; shift them to stay aligned with the audio.
        if (look.AudioFilters is { } filters)
            filters.StudioVoiceMixKeys = Shift(filters.StudioVoiceMixKeys);
        return look;

        List<Keyframe>? Shift(List<Keyframe>? keys) =>
            keys?.Select(k => k with { Time = Math.Round(k.Time + seconds, 6) }).ToList();
    }

    /// <summary>
    /// True when the look has time-based data: keyframes, effect keys or ranges, or Studio Voice mix keys.
    /// </summary>
    private static bool IsTimed(LayerState state) =>
        LayerState.MotionChannels.Any(channel => channel.Get(state) is { Count: > 0 })
        || state.Effects?.Any(e => e.Keys is { Count: > 0 } || e.Range is not null) == true
        || state.AudioFilters?.StudioVoiceMixKeys is { Count: > 0 };

    // ----- Look splitting: each half keeps its keyframes with a baked key at the cut -----

    // How near the cut a keyframe is on it (keys are kept to the microsecond).
    private const double KeyOnCut = 5e-7;

    /// <summary>
    /// Splits a look at <paramref name="cut"/> into two copies: keys go to their half (second rebased to the cut) with a baked key at the
    /// cut where a segment crosses it (<see cref="SplitKeys"/>); crossing effect ranges are cut, others follow their side. Motion is preserved.
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

        // Split Studio Voice mix keys the same way so automation stays on the audio.
        if (state.AudioFilters is { } filters && first.AudioFilters is { } firstFilters && second.AudioFilters is { } secondFilters)
            (firstFilters.StudioVoiceMixKeys, secondFilters.StudioVoiceMixKeys) = SplitKeys(filters.StudioVoiceMixKeys, cut);

        return (first, second);
    }

    /// <summary>
    /// Splits one property's keys at <paramref name="cut"/>: each half keeps its side (second rebased), a key exactly at the cut goes to
    /// both. A crossing segment gets a key at the cut (<see cref="EasingCurves.ValueAt"/>) and each half keeps its curve portion
    /// (<see cref="EasingCurves.Cut"/>), preserving motion exactly. A half with no keys holds its value.
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

    // ----- Re-joining split clips (partial fold reveal) -----

    /// <summary>
    /// Joined look for two clips where the second starts <paramref name="cut"/> after the first: keys merged (<see cref="JoinKeys"/>) and
    /// split ranges reunited; with no timed data, the second's look (<paramref name="keepSecond"/>) or the first's. Null when they are not
    /// halves of one look (other differences or non-joinable keys).
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

    /// <summary>Look JSON with timing removed (keys, effect keys, ranges): the part shared by split halves.</summary>
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
    /// Joins one property's keys: first's keys before the cut, its cut key only if the second lacks one, second's keys shifted by
    /// <paramref name="cut"/>; then removes the split-inserted cut key when redundant (<see cref="Unsplit"/>). False when not one motion
    /// (keys on only one side, keys outside the halves, or a value discontinuity at the cut).
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
    /// Removes the cut key when motion is unchanged without it: adjacent segments are sub-ranges of one curve (<see cref="EasingCurves.Cut"/>
    /// reversed; Linear/Hold trivially), or the key repeats its incoming value. Verified by sampling <see cref="EasingCurves.ValueAt"/> around the cut.
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

        // Verification window: previous to next key (1 s either side when absent; values hold beyond).
        var (from, to) = (previous?.Time ?? cut - 1, at + 1 < keys.Count ? keys[at + 1].Time : cut + 1);
        foreach (var candidate in candidates)
        {
            if (candidate.Count > 0 && SameMotion(keys, candidate, from, to))
                return candidate;
        }
        return keys;
    }

    /// <summary>True when two key lists agree over [<paramref name="from"/>, <paramref name="to"/>] at 33 samples within 1e-9.</summary>
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
    /// Joined effect range (<see cref="JoinLook"/>): reunites a range cut at the split, or keeps the first's when the split copied it whole.
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

        // Studio Voice mix keys too: clip-relative, so they stay aligned with the audio.
        if (look.AudioFilters is { } filters)
            filters.StudioVoiceMixKeys = Scale(filters.StudioVoiceMixKeys);
        return look;

        List<Keyframe>? Scale(List<Keyframe>? keys) =>
            keys?.Select(k => k with { Time = Math.Round(k.Time * stretch, 6) }).ToList();
    }

    private static string Seconds(double seconds) => seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
