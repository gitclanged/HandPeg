using System.Text.Json;
using System.Text.Json.Serialization;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// The test-only adapter of 4.3B (never shipped): a fixture written as 4.2.0's timeline (a <see cref="Scene"/>: the main
/// video's clips, the layers by track id, the main video's place among them) as the
/// <see cref="TimelineDocument"/> the lowering reads (TimelineLowering), so that the fixtures written for 4.2.0 test the export
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
/// Since 5.2-AuditB no file is the main one: a document of these kinds is what a project from before holds, and it is converted
/// as 5.2-AuditB converted such a project when it was opened (<see cref="Migrate"/>: LegacyMainVideo.Migrate, the context built as
/// that view model built it), so the export's fixtures are the conversion's test too: their boxes, crops, moments of the file and
/// sound must come out as they did. Since 5.2-Arm64AndNpu the app refuses such a project instead, and LegacyMainVideo lives here,
/// beside this adapter: test-only, never shipped.
/// </summary>
public static class LegacyFixtures
{
    /// <param name="main">The main video's row as the fixture made it (its look, and its first clip's start).</param>
    /// <param name="mainClips">The main video's clips as 4.2.0 laid them out (Scene.MainClips, <see cref="LegacyClip"/>).</param>
    /// <param name="layers">The fixture's clips as layers, laid out once (their shape learned), in the order they were added.</param>
    public static TimelineDocument ToTimeline(Scene scene, Layer main, IReadOnlyList<LegacyClip> mainClips, IReadOnlyList<Layer> layers, double sequenceSeconds)
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
            if (runs.Count > 0 && !ReferenceEquals(layer, main) && !ReferenceEquals(runs[^1][0], main) && runs[^1][0].TrackId == layer.TrackId)
                runs[^1].Add(layer);
            else
                runs.Add([layer]);
        }

        var tracks = new List<(int TrackId, TimelineTrackModel Track)>();
        foreach (var run in runs)
        {
            if (ReferenceEquals(run[0], main))
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

    // ----- The conversion a project from before was opened with (5.2-AuditB; test-only since 5.2-Arm64AndNpu) -----

    /// <summary>
    /// Converts a document of the 5.1 kinds in place as a project from before was converted when it was opened (LegacyMainVideo.Migrate),
    /// its context built as the view model built it: the opened file as it was probed, the Background switch, the frame the Center
    /// box was laid in (the document's: a fixture's frame is the one asked for), the Center box, and the file's streams as the Audio
    /// tab's rows (AudioTrack) with the fixture's mixing on them. Returns the output choices those rows leave, by stream place
    /// (<see cref="Outputs"/>). A fixture that kept none of the file's streams heard nothing of the clips that rode the old chain
    /// (5.1 heard them only through the streams kept): those clips are made silent first, the fixture's way of saying so.
    /// </summary>
    public static IReadOnlyList<AudioOutputChoice> Migrate(TimelineDocument document, LegacyMain main)
    {
        if (main.Streams.Count == 0)
            SilenceChain(document, main.Path);

        var streams = main.Streams.Select(s => s.ToAudioTrack()).ToList();
        LegacyMainVideo.Migrate(document, new LegacyMainVideoContext
        {
            PrimaryPath = main.Path,
            SourceWidth = main.SourceWidth,
            SourceHeight = main.SourceHeight,
            MediaSeconds = main.MediaSeconds,
            SourceHasNoVideo = main.SourceHasNoVideo,
            BackgroundHidden = main.BackgroundHidden,
            FrameWidth = document.Width,
            FrameHeight = document.Height,
            CenterRect = main.CenterRect,
            Streams = streams,
        });
        return Outputs(streams);
    }

    /// <summary>The Audio tab's rows as the export's output choices, each at its place among them (what CompileExportState writes).</summary>
    public static IReadOnlyList<AudioOutputChoice> Outputs(IReadOnlyList<AudioTrack> tracks) =>
        [.. tracks.Select((t, place) => new AudioOutputChoice(place, t.Title, t.Codec, t.Bitrate, t.IsPassthrough, t.IsDropped))];

    /// <summary>
    /// An export state made of a 5.1 document as a project opened now compiles it: the document converted (<see cref="Migrate"/>),
    /// the output choices, and the opened file as the one the subtitle and chapter settings name.
    /// </summary>
    public static SequenceExportState Opened(SequenceExportState state, LegacyMain main) =>
        state with { AudioOutputs = Migrate(state.Timeline, main), StreamSourcePath = main.Path };

    // The clips that rode the old chain (TimelineLowering's CanRide, on the lowest picture track holding a visible one, else holding
    // any) made silent: what a 5.1 state with no stream kept heard of them.
    private static void SilenceChain(TimelineDocument document, string primary)
    {
        var file = primary.Trim().Trim('"');
        bool Rides(TimelineClip c) =>
            c.ContentKind == LayerKind.MainVideo && c.VideoStreamIndex == 0 && c.AudioStreamIndex <= 0 && Math.Abs(c.Speed - 1) < 0.0005
            && string.Equals((c.SourcePath ?? "").Trim().Trim('"'), file, StringComparison.OrdinalIgnoreCase);
        var pictures = document.Tracks.Where(t => t.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch).ToList();
        var chain = pictures.FirstOrDefault(t => t.Clips.Exists(c => !c.IsHidden && Rides(c))) ?? pictures.FirstOrDefault(t => t.Clips.Exists(Rides));
        foreach (var clip in chain?.Clips.Where(Rides) ?? [])
            clip.AudioStreamIndex = -1;
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

/// <summary>
/// One of the main video's clips as 4.2.0 laid them out (its MainViewModel.GetMainClips): where it starts and ends on the
/// sequence, how far into its file it begins (seconds), its fades and their styles, a crossfade into it.
/// </summary>
public readonly record struct LegacyClip(int Index, double Start, double End, double Offset)
{
    public double FadeIn { get; init; }
    public double FadeOut { get; init; }
    public TransitionStyle FadeInStyle { get; init; }
    public TransitionStyle FadeOutStyle { get; init; }
    public double TransitionIn { get; init; }
}

/// <summary>
/// What a 5.1 export state knew of its opened file (the main video) besides the document: what LegacyMainVideoContext is made of
/// when a project from before is opened (5.2-AuditB).
/// </summary>
public sealed record LegacyMain(string Path)
{
    /// <summary>Its probed picture size (0 unknown).</summary>
    public int SourceWidth { get; init; }

    public int SourceHeight { get; init; }

    /// <summary>Its probed length, in seconds (0 unknown).</summary>
    public double MediaSeconds { get; init; }

    public bool SourceHasNoVideo { get; init; }

    /// <summary>The Background switch off: the clips that left their blur fill to it had none.</summary>
    public bool BackgroundHidden { get; init; }

    /// <summary>The Center box in frame pixels; null to have it worked out from the first clip's place, as a project opened is.</summary>
    public (int X, int Y, int Width, int Height)? CenterRect { get; init; }

    /// <summary>Its audio streams as the Audio tab mixed them, in stream order.</summary>
    public IReadOnlyList<LegacyStream> Streams { get; init; } = [];
}

/// <summary>
/// One audio stream of the opened file as a 5.1 project mixed and exported it (what ExportAudioTrack carried: the Audio tab's row),
/// by its number among the file's audio streams ("0:a:N").
/// </summary>
public sealed record LegacyStream(int Index, string Title, string Codec = "aac", string Bitrate = "160k")
{
    /// <summary>Copied as it is (Passthrough), else encoded again.</summary>
    public bool Passthrough { get; init; }

    /// <summary>Left out (Ignore / Drop: its row muted).</summary>
    public bool Dropped { get; init; }

    public int Channels { get; init; } = 2;

    public double GainDb { get; init; }

    public TrackAudioFilters? Filters { get; init; }

    public bool AutoDuck { get; init; }

    public bool IsVoice { get; init; }

    /// <summary>Its slip against the picture, in seconds (later above 0).</summary>
    public double Offset { get; init; }

    /// <summary>The parts it was cut into, in its own time, the silenced ones Muted; empty while it is whole.</summary>
    public IReadOnlyList<AudioPiece> Pieces { get; init; } = [];

    /// <summary>Its row as the view model holds it (MainViewModel.AudioTracks): the stream as probed, its output choice and its mixing.</summary>
    public AudioTrack ToAudioTrack()
    {
        var layout = Channels switch { 1 => "mono", 2 => "stereo", _ => "" };
        var track = new AudioTrack(new AudioStreamInfo(Index, "aac", Channels, layout, 48000, 0, Title, ""))
        {
            Title = Title,
            Codec = Codec,
            Bitrate = Bitrate,
            Action = Dropped ? AudioTrack.Drop : Passthrough ? AudioTrack.Passthrough : AudioTrack.Reencode,
            GainDb = GainDb,
            Filters = Filters?.Clone() ?? new TrackAudioFilters(),
            AutoDuck = AutoDuck,
            IsVoice = IsVoice,
        };
        track.SetEdits(Offset, Pieces);
        return track;
    }
}
