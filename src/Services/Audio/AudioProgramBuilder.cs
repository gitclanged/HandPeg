using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services.Audio;

/// <summary>
/// One sound the lowering hears (SequenceLowering.LayerAudio) with what it is on the Sequence (5.2-AuditB): its clip and its track,
/// found by the layer's ClipId (null for a layer that is no clip of the document), and its stream place: the
/// <see cref="TimelineTrackModel.StreamOrdinal"/> of the Sub-Lane it is on, or -1 for a sound on none (a clip's own sound, an Audio
/// track's clip, a lane a stream was moved to on its own), which goes to the first output.
/// </summary>
/// <param name="Path">Its file, cleaned of spaces and quotes.</param>
internal readonly record struct HeardSound(Layer Layer, string Path, TimelineClip? Clip, TimelineTrackModel? Track, int Ordinal)
{
    /// <summary>A voice: what the ducked sounds make room for.</summary>
    public bool IsVoice => Layer.IsVoice;

    /// <summary>Turned down while a voice speaks: its own Auto-Duck, or its track's Duck Against Voice.</summary>
    public bool Ducked => (Layer.AutoDuck || Track?.DuckAgainstVoice == true) && !Layer.IsVoice;
}

/// <summary>
/// A SequenceExportState read into an <see cref="AudioProgram"/> with the FFmpeg path's semantics, from the very lowering
/// the GPU scene is made from (TimelineLowering.Lowering, cached by document, so this costs little on every edit):
/// <list type="bullet">
/// <item>Every clip that is heard (Lowered.LayerAudio): its own file and stream, read on the file's own clock (the cut of
/// BuildLayerAudio: origin + MediaOffset), at its speed, its slip moving where it starts (LayerAudioStart), its J or L cut as the
/// lowering laid it, its fades. One clock for every clip: no file is the "main" one (5.2-AuditB).</item>
/// <item>A track's clips (a Sub-Lane's streams, an Audio track's sounds, a video track's own sound) are one <see cref="AudioStripKind.Track"/>
/// strip for each part they play in the ducking (5.2-AuditB, as each of the main file's streams was): each clip's gain and fades on its
/// sound, summed, ducked when Auto-Duck (a voice is the side chain), then the track's gain and processing
/// (<see cref="AudioChainBuilder.From"/> of its filters), whose state runs on across the cuts. As the export ordered a stream: the
/// processing after the ducking.</item>
/// <item>A clip with processing of its own (its filters, on a track with none): a <see cref="AudioStripKind.Clip"/> strip of its own,
/// its processing and gain (the clip's with its track's), then its fades, then ducked.</item>
/// <item>The session's voiceover (IncludeVoiceover): as a clip, a voice for the ducking, as the export has it.</item>
/// <item>The sequence's fades (FadeIn, FadeOut: a second each) over the whole; nothing past the sequence's end.</item>
/// </list>
/// While a track is soloed (TimelineTrackModel.IsSoloed), only the soloed tracks and the Sub-Lanes under them are heard
/// (AudioBuildOptions.IgnoresSolo: an export hears everything). Not here: loudness normalization (it needs the whole sound first:
/// an export's is ExportSoundPlan's, measured on the render). An export's output tracks are laid on buses by stream place
/// (AudioBuildOptions.Buses).
/// </summary>
internal static class AudioProgramBuilder
{
    public static AudioProgram Build(SequenceExportState state, AudioBuildOptions options)
    {
        SequenceLowering lowering;
        try
        {
            lowering = TimelineLowering.Lowering(state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Sound engine: the timeline could not be lowered; nothing is heard", ex);
            return AudioProgram.Empty;
        }

        var voices = new List<AudioVoice>();
        var strips = new List<AudioStrip>();
        var duck = options.Duck ?? DuckParameters.ForAmount(state.DuckAmountDb);
        var layout = options.Buses ?? AudioBusLayout.AllOnOne;
        var solo = !options.IgnoresSolo && state.Timeline.Tracks.Exists(t => t.IsSoloed);

        AddSounds(state, Heard(state, lowering, solo), layout, voices, strips);
        if (!solo)
            AddVoiceover(state, layout.DefaultBus, voices, strips);
        return new AudioProgram(lowering.SequenceSeconds, [.. voices], [.. strips], state.FadeIn, state.FadeOut, duck) { MasterGain = MasterFactor(state.MasterGainDb) };
    }

    /// <summary>The master fader's decibels as a factor (5.2B): exactly 1 within a hundredth of a decibel of 0, so 0 dB multiplies nothing.</summary>
    internal static float MasterFactor(double decibels) =>
        !double.IsFinite(decibels) || Math.Abs(decibels) < 0.005 ? 1f : (float)Math.Pow(10, Math.Clamp(decibels, -40, 12) / 20);

    /// <summary>
    /// The sounds the lowering hears, in its order (the tracks bottom first), each with its clip, its track and its stream place;
    /// with <paramref name="solo"/>, only those of the soloed tracks and of the tracks that lie under them.
    /// </summary>
    internal static List<HeardSound> Heard(SequenceExportState state, SequenceLowering lowering, bool solo)
    {
        var document = state.Timeline;
        var where = new Dictionary<Guid, (TimelineClip Clip, TimelineTrackModel Track)>();
        foreach (var track in document.Tracks)
        {
            foreach (var clip in track.Clips)
                where.TryAdd(clip.Id, (clip, track));
        }

        var soloed = solo ? Soloed(document) : null;
        var heard = new List<HeardSound>(lowering.LayerAudio.Count);
        foreach (var layer in lowering.LayerAudio)
        {
            var path = Clean(layer.ImagePath);
            if (path.Length == 0)
                continue;

            TimelineClip? clip = null;
            TimelineTrackModel? track = null;
            if (layer.ClipId != Guid.Empty && where.TryGetValue(layer.ClipId, out var found))
                (clip, track) = found;
            if (soloed is not null && (track is null || !soloed.Contains(track.Id)))
                continue;

            heard.Add(new HeardSound(layer, path, clip, track, track?.StreamOrdinal is >= 0 and var ordinal ? ordinal : -1));
        }

        return heard;
    }

    // The tracks heard while some are soloed: each soloed track, and every track that lies under one (its Sub-Lanes).
    private static HashSet<Guid> Soloed(TimelineDocument document)
    {
        var byId = new Dictionary<Guid, TimelineTrackModel>();
        foreach (var track in document.Tracks)
            byId.TryAdd(track.Id, track);

        var heard = new HashSet<Guid>();
        foreach (var track in document.Tracks)
        {
            TimelineTrackModel? at = track;
            for (var steps = 0; at is not null && steps <= document.Tracks.Count; steps++)
            {
                if (at.IsSoloed)
                {
                    heard.Add(track.Id);
                    break;
                }

                at = at.ParentTrackId is { } parent && byId.TryGetValue(parent, out var above) ? above : null;
            }
        }

        return heard;
    }

    // Every sound heard, on the buses the layout gives its stream place (an export's surround output: a strip for each pair of its
    // channels; none, a place the export copies or leaves out): on its track's strip, one for each track, pair and part in the ducking;
    // or, a clip with processing of its own (or a layer that is no clip of the Sequence), on a strip of its own.
    private static void AddSounds(SequenceExportState state, List<HeardSound> sounds, AudioBusLayout layout, List<AudioVoice> voices, List<AudioStrip> strips)
    {
        var trackStrips = new Dictionary<string, int>(StringComparer.Ordinal);
        var trackChains = new Dictionary<Guid, AudioChain>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sound in sounds)
        {
            if (layout.BusesOf(sound.Ordinal) is not { Length: > 0 } buses)
                continue;

            var (layer, clip, track) = (sound.Layer, sound.Clip, sound.Track);
            var own = track is { AudioFilters: null } && layer.AudioFilters is { Processes: true } filters ? filters : null;
            if (clip is null || track is null || own is not null)
            {
                // Its processing and gain, its fades after them (BuildLayerAudio's order). A strip's key is its clip's line on the
                // timeline and its track: its processing keeps its state while its settings change; a clip moved is a new strip, as
                // FFmpeg's chain for it was new.
                var gainDb = clip is not null && track is not null ? clip.GainDb + track.GainDb : layer.AudioGainDb;
                // (Its filters' keyframes count from the clip's start on the Sequence, 5.2B: a track's from the Sequence's.)
                var chain = AudioChainBuilder.From(own ?? layer.AudioFilters, Math.Clamp(gainDb, -40, 24), clip?.Start ?? layer.StartTime);
                var (speed, startsAt) = Placed(layer);
                var line = string.Create(CultureInfo.InvariantCulture, $"clip:{sound.Path}|{layer.TrackId}|{Math.Round((startsAt - layer.MediaOffset / speed) * 1e6):0}");
                var n = occurrences.GetValueOrDefault(line);
                occurrences[line] = n + 1;
                var name = n == 0 ? line : string.Create(CultureInfo.InvariantCulture, $"{line}#{n}");
                for (var pair = 0; pair < buses.Length; pair++)
                {
                    var strip = strips.Count;
                    strips.Add(new AudioStrip
                    {
                        Key = name + PairKey(pair, buses.Length),
                        Kind = AudioStripKind.Clip,
                        IsVoice = sound.IsVoice,
                        Ducked = sound.Ducked,
                        Effects = chain.Effects,
                        Label = System.IO.Path.GetFileName(sound.Path),
                        Bus = buses[pair],
                    });
                    voices.Add(VoiceOf(state, sound, chain, buses.Length > 1 ? pair : -1, 1f, strip));
                }

                continue;
            }

            // Its track's strip: the clip's gain and fades on its sound, the track's gain and processing after the sum.
            if (!trackChains.TryGetValue(track.Id, out var trackChain))
                trackChains[track.Id] = trackChain = AudioChainBuilder.From(track.AudioFilters, Math.Clamp(track.GainDb, -40, 24));
            var gain = Math.Abs(clip.GainDb) >= 0.05 ? (float)Math.Pow(10, Math.Clamp(clip.GainDb, -40, 24) / 20) : 1f;
            var role = sound.IsVoice ? ":voice" : sound.Ducked ? ":ducked" : "";
            for (var pair = 0; pair < buses.Length; pair++)
            {
                var key = string.Create(CultureInfo.InvariantCulture, $"track:{track.Id:N}{PairKey(pair, buses.Length)}{role}");
                if (!trackStrips.TryGetValue(key, out var strip))
                {
                    trackStrips[key] = strip = strips.Count;
                    strips.Add(new AudioStrip
                    {
                        Key = key,
                        Kind = AudioStripKind.Track,
                        IsVoice = sound.IsVoice,
                        Ducked = sound.Ducked,
                        Effects = trackChain.Effects,
                        Label = track.DisplayName,
                        Bus = buses[pair],
                    });
                }

                voices.Add(VoiceOf(state, sound, trackChain, buses.Length > 1 ? pair : -1, gain, strip));
            }
        }
    }

    // A strip's pair of a surround output's channels, in its key ("" for a strip that is the whole sound).
    private static string PairKey(int pair, int pairs) => pairs > 1 ? string.Create(CultureInfo.InvariantCulture, $":pair{pair}") : "";

    // A layer's speed (1 for one without a running time of its own) and where its sound starts on the sequence: its slip moves it.
    private static (double Speed, double StartsAt) Placed(Layer layer) =>
        (layer.HasSpeed && double.IsFinite(layer.Speed) ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1,
         layer.StartTime + (layer.IsAudio ? 0 : layer.AudioOffset));

    // A clip's sound as a voice of a strip: its file and stream (a surround stream's pair of channels, or -1 for the whole), at its
    // speed and its strip's tempo and pitch, with its fades; its own gain (a track strip's voice: its clip's, else 1).
    private static AudioVoice VoiceOf(SequenceExportState state, HeardSound sound, AudioChain chain, int pair, float gain, int strip)
    {
        var (layer, path) = (sound.Layer, sound.Path);
        var (speed, startsAt) = Placed(layer);
        var timed = layer.Duration > 0.001;

        // A sound clip's file is read on its own clock from 0, as the graph read it (a sound clip is not in MediaStarts); a video's
        // from its first frame (MediaStarts, else what the decoder finds), as its picture is. A stream unpacked from a video is on
        // the video's clock: its file is in MediaStarts.
        var source = new AudioSource(path, Math.Max(layer.AudioStream, 0))
        {
            VideoStart = layer.IsAudio && !state.MediaStarts.ContainsKey(path) ? 0 : VideoStart(state, path),
            ChannelPair = pair,
        };

        var fadeIn = Math.Clamp(layer.FadeInSeconds, 0, 60);
        var fadeOut = Math.Clamp(layer.FadeOutSeconds, 0, 60);
        return new AudioVoice
        {
            Source = source,
            SequenceStart = startsAt,
            SequenceEnd = timed ? startsAt + layer.Duration : double.PositiveInfinity,
            MediaStart = layer.MediaOffset,
            MediaEnd = timed ? layer.MediaOffset + layer.Duration * speed : double.PositiveInfinity,
            Rate = speed * Math.Clamp(chain.Tempo, 0.25, 4),
            Pitch = chain.Pitch,
            Gain = gain,
            FadeIn = new AudioFade(fadeIn > 0.01 ? fadeIn : 0),
            FadeOut = new AudioFade(fadeOut > 0.01 && timed ? Math.Min(fadeOut, layer.Duration) : 0),
            FadeOutEnd = timed ? startsAt + layer.Duration : double.PositiveInfinity,
            Strip = strip,
            Label = $"clip {System.IO.Path.GetFileName(path)}",
        };
    }

    private static void AddVoiceover(SequenceExportState state, int bus, List<AudioVoice> voices, List<AudioStrip> strips)
    {
        var path = Clean(state.VoiceoverPath);
        if (!state.IncludeVoiceover || path.Length == 0)
            return;

        // As BuildVoiceoverChain: cut to its handles when trimmed, moved to where it starts, at its gain.
        var trimmed = state.VoiceoverTrimStart > 0.05
                      || (state.VoiceoverDuration > 0 && state.VoiceoverTrimEnd < state.VoiceoverDuration - 0.05 && state.VoiceoverTrimEnd > state.VoiceoverTrimStart);
        var mediaStart = trimmed ? Math.Max(state.VoiceoverTrimStart, 0) : 0;
        var mediaEnd = trimmed ? state.VoiceoverTrimEnd : double.PositiveInfinity;
        var start = state.VoiceoverStartSeconds >= 0.01 ? state.VoiceoverStartSeconds : 0;
        var strip = strips.Count;
        strips.Add(new AudioStrip
        {
            Key = "voiceover",
            Kind = AudioStripKind.Voiceover,
            IsVoice = true,
            Effects = Math.Abs(state.VoiceoverGainDb) >= 0.05 ? [AudioEffectSpec.Gain(Math.Pow(10, state.VoiceoverGainDb / 20))] : [],
            Label = "Voiceover",
            Bus = bus,
        });
        voices.Add(new AudioVoice
        {
            Source = new AudioSource(path, 0) { VideoStart = 0 },
            SequenceStart = start,
            SequenceEnd = double.IsPositiveInfinity(mediaEnd) ? double.PositiveInfinity : start + Math.Max(mediaEnd - mediaStart, 0),
            MediaStart = mediaStart,
            MediaEnd = mediaEnd,
            Strip = strip,
            Label = "voiceover",
        });
    }

    internal static string Clean(string path) => path.Trim().Trim('"');

    // Where FFmpeg found a file's picture to start, as the GPU scene looks it up: the state's, else what MediaProbe learnt this
    // session; NaN when neither knows (the decoder then reads the file itself).
    private static double VideoStart(SequenceExportState state, string path) =>
        state.MediaStarts.TryGetValue(path, out var start) ? start : MediaProbe.KnownVideoStart(path) ?? double.NaN;
}
