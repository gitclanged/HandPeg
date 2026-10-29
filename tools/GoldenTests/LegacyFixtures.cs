using System.Text.Json;
using System.Text.Json.Serialization;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegGoldenTests;

/// <summary>
/// The test-only adapter of 4.3B (never shipped): a fixture written as 4.2.0's timeline (a <see cref="Scene"/>: the main
/// video's clips, the layers by track id, the main video's place among them) as the
/// <see cref="TimelineDocument"/> FilterGraphBuilder compiles, so that the fixtures written for 4.2.0 test the builder
/// through the document. It makes the document as the view model did until 4.3C (4.3B's MainViewModel.TimelineBridge.cs)
/// and as FORMAT5_MANUAL_TRANSLATION.md section 7 translates a project:
///   - the main video is clips of kind MainVideo (the primary chain) on a video track at its place in the stack;
///   - a track is a run of the stack's clips with one track id (the main video sitting inside a run makes it two, and a
///     crossfade between the two is a dip), its clips in the order they start; the sounds are a track for each track id;
///   - every key counts from its own clip's start (4.2.0 counted the main video's motion keys from its first clip's
///     start, and its effects' keys in the sequence's seconds);
///   - "to the end" is the sequence's end, and a video that ran out before it repeats from its file's first frame, its
///     sound once, as 4.2.0's loop did;
///   - the canvas ticks at the main video's own rate (FrameRate.OfSource).
/// </summary>
public static class LegacyFixtures
{
    /// <param name="main">The main video's row as the fixture made it (its look, and its first clip's start).</param>
    /// <param name="mainClips">The main video's clips as 4.2.0 laid them out (Scene.MainClips), in the builder's lowered form (<see cref="MainClip"/>).</param>
    /// <param name="layers">The fixture's clips as layers, laid out once (their shape learned), in the order they were added.</param>
    public static TimelineDocument ToTimeline(Scene scene, Layer main, IReadOnlyList<MainClip> mainClips, IReadOnlyList<Layer> layers, double sequenceSeconds)
    {
        var document = new TimelineDocument
        {
            Width = scene.FrameWidth,
            Height = scene.FrameHeight,
            UseVerticalResolution = scene.FrameHeight > scene.FrameWidth,
            FrameRate = FrameRate.OfSource(scene.FrameRate),
            QuantizeToFrames = false,
            SequenceEffects = [.. scene.SequenceEffects.Select(e => e.ToState())],
        };

        TimelineTrackModel Add(TimelineTrackKind kind)
        {
            var track = new TimelineTrackModel { Kind = kind, TrackNumber = document.Tracks.Count(t => t.Kind == kind) + 1 };
            document.Tracks.Add(track);
            return track;
        }

        // The stack bottom first, the main video at its index, cut into runs of one track id.
        var order = layers.Where(l => !l.IsAudio).ToList();
        order.Insert(Math.Clamp(scene.MainVideoIndex, 0, order.Count), main);
        var runs = new List<List<Layer>>();
        foreach (var layer in order)
        {
            if (runs.Count > 0 && !layer.IsMainVideo && runs[^1][0] is { IsMainVideo: false } last && last.TrackId == layer.TrackId)
                runs[^1].Add(layer);
            else
                runs.Add([layer]);
        }

        var tracks = new List<(int TrackId, TimelineTrackModel Track)>();
        foreach (var run in runs)
        {
            if (run[0].IsMainVideo)
            {
                var track = Add(TimelineTrackKind.Video);
                track.IsHidden = main.IsHidden;
                var row = main.ToState();
                foreach (var clip in mainClips)
                {
                    track.Clips.Add(new TimelineClip
                    {
                        ContentKind = LayerKind.MainVideo,
                        SourcePath = scene.Main.Path,
                        AudioStreamIndex = scene.Main.HasAudio ? 0 : -1,
                        Start = clip.Start,
                        End = clip.End,
                        MediaOffset = clip.Offset,
                        SourceDuration = scene.Main.Seconds,
                        FadeInSeconds = clip.FadeIn,
                        FadeOutSeconds = clip.FadeOut,
                        FadeInStyle = clip.FadeInStyle,
                        FadeOutStyle = clip.FadeOutStyle,
                        TransitionIn = clip.TransitionIn,
                        ProxyChoice = main.ProxyChoice,
                        State = KeysFrom(row, motion: main.StartTime - clip.Start, effects: -clip.Start),
                    });
                }

                continue;
            }

            var pictures = Add(run.All(l => l.IsAdjustment) ? TimelineTrackKind.Adjustment : TimelineTrackKind.Video);
            foreach (var layer in run.OrderBy(l => l.StartTime))
                pictures.Clips.AddRange(ClipsOf(layer, sequenceSeconds, scene));
            tracks.Add((run[0].TrackId, pictures));
        }

        // A crossfade between clips of one track that the main video sits between (two tracks here): laid over each other
        // across the cut in the clips themselves, as 4.2.0 laid them (the earlier one's sound runs on rather than fading).
        foreach (var (id, track) in tracks)
        {
            foreach (var after in track.Clips.Where(c => c.TransitionIn > 0.01 && !track.Clips.Any(b => Math.Abs(b.End - c.Start) < 0.011 && b.Duration > 0.001)).ToList())
            {
                var before = tracks.Where(r => r.TrackId == id && !ReferenceEquals(r.Track, track))
                    .SelectMany(r => r.Track.Clips).FirstOrDefault(b => b.Duration > 0.001 && Math.Abs(b.End - after.Start) < 0.011);
                if (before is null)
                    continue;

                var length = Math.Min(after.TransitionIn, Math.Min(before.Duration, after.Duration));
                var late = Math.Min(length / 2, before.PlaysFile && before.SourceDuration > 0.05 ? Math.Max((before.SourceDuration - before.MediaEnd) / before.Speed, 0) : double.PositiveInfinity);
                var early = Math.Min(length / 2, after.PlaysFile ? after.MediaOffset / after.Speed : double.PositiveInfinity);
                after.TransitionIn = 0;
                if (late + early < 0.04)
                {
                    (before.FadeOutSeconds, after.FadeInSeconds) = (Math.Max(before.FadeOutSeconds, length / 2), Math.Max(after.FadeInSeconds, length / 2));
                    continue;
                }

                before.End += late;
                if (after.PlaysFile)
                    after.MediaOffset = Math.Max(after.MediaOffset - early * after.Speed, 0);
                (after.Start, after.State) = (after.Start - early, KeysFrom(after.State, motion: early, effects: early));
                after.FadeInSeconds = Math.Max(after.FadeInSeconds, late + early);
            }
        }

        foreach (var group in layers.Where(l => l.IsAudio).GroupBy(l => l.TrackId))
        {
            var sounds = Add(TimelineTrackKind.Audio);
            foreach (var layer in group.OrderBy(l => l.StartTime))
                sounds.Clips.AddRange(ClipsOf(layer, sequenceSeconds, scene));
        }

        return document;
    }

    /// <summary>A layer as the clips it is on the timeline: one, or one for each time round for a video that repeated.</summary>
    private static IEnumerable<TimelineClip> ClipsOf(Layer layer, double sequence, Scene scene)
    {
        var (start, end) = layer.GetSpan(sequence);
        if (end - start < 0.001)
            yield break;

        var speed = layer.HasSpeed ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;
        var first = layer.IsVideoFile && layer.Duration <= 0.001 && layer.MediaDuration > 0.05
            ? Math.Max((layer.MediaDuration - layer.MediaOffset) / speed, 0.001)
            : end - start;

        var look = layer.ToState();
        var (at, offset, round) = (start, layer.MediaOffset, 0);
        while (end - at >= 0.001)
        {
            var length = Math.Min(round == 0 ? first : layer.MediaDuration / speed, end - at);
            var (isFirst, isLast) = (round == 0, at + length >= end - 0.001);
            yield return new TimelineClip
            {
                ContentKind = layer.Kind,
                SourcePath = layer.IsFile || layer.IsAudio ? layer.ImagePath : layer.IsVideo ? scene.Main.Path : null,
                AudioStreamIndex = layer.CarriesSound ? 0 : -1,
                Start = at,
                End = isLast ? end : at + length,
                MediaOffset = offset,
                Speed = speed,
                SourceDuration = layer.IsVideoFile || layer.IsAudio ? layer.MediaDuration : 0,
                AudioLinked = scene.AudioLinked,
                AudioSlipSeconds = scene.AudioLinked || layer.IsAudio ? 0 : layer.AudioOffset,
                FadeInSeconds = isFirst ? layer.FadeInSeconds : 0,
                FadeOutSeconds = isLast ? layer.FadeOutSeconds : 0,
                FadeInStyle = layer.FadeInStyle,
                FadeOutStyle = layer.FadeOutStyle,
                TransitionIn = isFirst && layer.Duration > 0.001 ? layer.TransitionIn : 0, // (4.2.0 crossfaded no "to the end" layer)
                GainDb = layer.AudioGainDb,
                IsHidden = layer.IsHidden,
                IsMuted = layer.IsSoundMuted || !isFirst,
                ProxyChoice = layer.ProxyChoice,
                State = isFirst ? look : KeysFrom(look, motion: start - at, effects: start - at),
            };

            (at, offset, round) = (at + length, 0, round + 1);
        }
    }

    private static readonly JsonSerializerOptions CopyOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>
    /// A look with its motion keys and its effects' keys and ranges moved by the seconds given: the very state when nothing in
    /// it is placed in time or nothing moves, else a deep copy (never the state given, which may be shared).
    /// </summary>
    public static LayerState KeysFrom(LayerState state, double motion, double effects)
    {
        var timed = state.KeysX is { Count: > 0 } || state.KeysY is { Count: > 0 } || state.KeysScale is { Count: > 0 }
                    || state.KeysRotation is { Count: > 0 } || state.KeysOpacity is { Count: > 0 };
        var effectsTimed = state.Effects?.Any(e => e.Keys is { Count: > 0 } || e.Range is not null) == true;
        if (!(timed && motion != 0) && !(effectsTimed && effects != 0))
            return state;

        List<Keyframe>? Shift(List<Keyframe>? keys, double seconds) =>
            seconds == 0 ? keys : keys?.Select(k => k with { Time = Math.Round(k.Time + seconds, 6) }).ToList();

        var look = JsonSerializer.Deserialize<LayerState>(JsonSerializer.SerializeToUtf8Bytes(state, CopyOptions), CopyOptions)!;
        (look.KeysX, look.KeysY, look.KeysScale) = (Shift(look.KeysX, motion), Shift(look.KeysY, motion), Shift(look.KeysScale, motion));
        (look.KeysRotation, look.KeysOpacity) = (Shift(look.KeysRotation, motion), Shift(look.KeysOpacity, motion));
        foreach (var effect in effects == 0 ? [] : look.Effects ?? [])
        {
            if (effect.Keys is { } keys)
            {
                foreach (var name in keys.Keys.ToList())
                    keys[name] = Shift(keys[name], effects) ?? [];
            }

            if (effect.Range is [var from, var to])
                effect.Range = [Math.Round(from + effects, 6), Math.Round(to + effects, 6)];
        }

        return look;
    }
}
