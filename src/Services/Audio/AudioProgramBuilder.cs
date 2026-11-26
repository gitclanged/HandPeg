using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services.Audio;

/// <summary>
/// One sound heard by the lowering (SequenceLowering.LayerAudio) with its sequence context: its clip and track (by the layer's ClipId;
/// null when the layer is not a document clip) and its stream slot: the Sub-Lane's <see cref="TimelineTrackModel.StreamOrdinal"/>, or -1
/// when not on a Sub-Lane (a clip's own sound, an Audio-track clip, a separately moved stream), which goes to the first output.
/// </summary>
/// <param name="Path">File path, trimmed of spaces and quotes.</param>
internal readonly record struct HeardSound(Layer Layer, string Path, TimelineClip? Clip, TimelineTrackModel? Track, int Ordinal)
{
    /// <summary>Voice: the side chain that ducked sounds make room for.</summary>
    public bool IsVoice => Layer.IsVoice;

    /// <summary>Attenuated while a voice is active: its own Auto-Duck or its track's Duck Against Voice.</summary>
    public bool Ducked => (Layer.AutoDuck || Track?.DuckAgainstVoice == true) && !Layer.IsVoice;
}

/// <summary>
/// Builds an <see cref="AudioProgram"/> from a SequenceExportState using the same lowering as the GPU scene (TimelineLowering.Lowering,
/// cached per document, so it is cheap on every edit):
/// <list type="bullet">
/// <item>Every heard clip (Lowered.LayerAudio): its own file and stream on the file's clock (origin + MediaOffset, as BuildLayerAudio
/// cuts), at its speed, shifted by its slip (LayerAudioStart), with its J/L cut and fades. One clock for all clips; no file is "main".</item>
/// <item>A track's clips (Sub-Lane streams, Audio-track sounds, a video track's own sound) form one <see cref="AudioStripKind.Track"/>
/// strip per ducking role: clip gain and fades per sound, summed, ducked when Auto-Duck (voices are the side chain), then the track's gain
/// and processing (<see cref="AudioChainBuilder.From"/>), whose state carries across cuts.</item>
/// <item>A clip with its own processing (on a track without filters) gets its own <see cref="AudioStripKind.Clip"/> strip: processing
/// and gain (clip plus track), then fades, then ducking.</item>
/// <item>The session voiceover (IncludeVoiceover): processed like a clip and acting as a voice for ducking.</item>
/// <item>Sequence fades (FadeIn, FadeOut: one second each) over the mix; nothing past the sequence end.</item>
/// </list>
/// While any track is soloed (TimelineTrackModel.IsSoloed) only soloed tracks and their Sub-Lanes are heard (exports set
/// AudioBuildOptions.IgnoresSolo). Loudness normalization is not here (it needs the full render: ExportSoundPlan). Export output tracks
/// map to buses by stream slot (AudioBuildOptions.Buses).
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

    /// <summary>Master fader dB as a factor: exactly 1 within 0.005 dB of 0; clamped to -40..+12 dB.</summary>
    internal static float MasterFactor(double decibels) =>
        !double.IsFinite(decibels) || Math.Abs(decibels) < 0.005 ? 1f : (float)Math.Pow(10, Math.Clamp(decibels, -40, 12) / 20);

    /// <summary>
    /// Sounds heard by the lowering in its order (tracks bottom first), each with clip, track and stream slot; with <paramref name="solo"/>,
    /// only those on soloed tracks and their Sub-Lanes.
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

    // Tracks audible under solo: each soloed track and every track nested beneath one (its Sub-Lanes).
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

    // Adds every heard sound on the buses its stream slot maps to (surround export: one strip per channel pair; no buses = copied or
    // omitted by the export): on its track's strip (one per track, pair and ducking role), or on its own strip for a clip with its own
    // processing (or a layer that is not a sequence clip).
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
                // Processing and gain, then fades (BuildLayerAudio order). The key is the clip's timeline line plus track, so processing
                // keeps its state while settings change; a moved clip gets a new strip.
                var gainDb = clip is not null && track is not null ? clip.GainDb + track.GainDb : layer.AudioGainDb;
                // Clip filter keyframes are relative to the clip's sequence start (track filters to the sequence start).
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
                    voices.Add(VoiceOf(state, sound, chain, null, buses.Length > 1 ? pair : -1, 1f, strip));
                }

                continue;
            }

            // Track strip: clip gain and fades per sound; track gain and processing after summing.
            if (!trackChains.TryGetValue(track.Id, out var trackChain))
                trackChains[track.Id] = trackChain = AudioChainBuilder.From(track.AudioFilters, Math.Clamp(track.GainDb, -40, 24));
            var gain = Math.Abs(clip.GainDb) >= 0.05 ? (float)Math.Pow(10, Math.Clamp(clip.GainDb, -40, 24) / 20) : 1f;
            var role = sound.IsVoice ? ":voice" : sound.Ducked ? ":ducked" : "";

            // The clip's own Pitch/Tempo (Pitch Shift) also applies on track strips: the lowered layer carries the track's filters, the
            // clip's come from its State.
            var mine = clip.State.AudioFilters is { PitchTempo: true } clipFilters ? AudioChainBuilder.From(clipFilters, 0) : null;
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

                voices.Add(VoiceOf(state, sound, trackChain, mine, buses.Length > 1 ? pair : -1, gain, strip));
            }
        }
    }

    // Channel-pair suffix for a strip key ("" when the strip carries the whole stream).
    private static string PairKey(int pair, int pairs) => pairs > 1 ? string.Create(CultureInfo.InvariantCulture, $":pair{pair}") : "";

    // Layer speed (1 without its own timing) and its sound's sequence start, shifted by its slip.
    private static (double Speed, double StartsAt) Placed(Layer layer) =>
        (layer.HasSpeed && double.IsFinite(layer.Speed) ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1,
         layer.StartTime + (layer.IsAudio ? 0 : layer.AudioOffset));

    // A clip's sound as a strip voice: file and stream (a surround pair, or -1 for the whole stream) at its speed times the strip's
    // tempo and pitch (times the clip's own <paramref name="own"/> on a track strip), with fades and its own gain (track strip: the
    // clip's, else 1). Keep Pitch off plays it as tape (pitch follows speed); Spectral Audio uses the phase vocoder; both follow the layer.
    private static AudioVoice VoiceOf(SequenceExportState state, HeardSound sound, AudioChain chain, AudioChain? own, int pair, float gain, int strip)
    {
        var (layer, path) = (sound.Layer, sound.Path);
        var (speed, startsAt) = Placed(layer);
        var timed = layer.Duration > 0.001;
        var (pitch, tempo) = (chain.Pitch * (own?.Pitch ?? 1), chain.Tempo * (own?.Tempo ?? 1));

        // Rate = speed x net Pitch/Tempo: FFmpeg's asetrate (scales pitch and tempo) then atempo (tempo / pitch) nets the Tempo alone.
        // The engine divides the pitch back out (VoiceProducer stretch = rate / pitch), so the rate also carries the pitch factor
        // (golden audio-engine-pitch-timing).
        var net = Math.Clamp(tempo * pitch, 0.25, 4);

        // Audio-only clips read on their own clock from 0 (not in MediaStarts); videos from their first frame (MediaStarts, else
        // decoder discovery), matching the picture. Streams extracted from a video use the video clock (their file is in MediaStarts).
        var source = new AudioSource(path, Math.Max(layer.AudioStream, 0))
        {
            VideoStart = layer.IsAudio && !state.MediaStarts.ContainsKey(path) ? 0 : VideoStart(state, path),
            ChannelPair = pair,
        };

        // A keyframed speed ramp (Layer.SpeedKeys) times the sound through the contract's mapping (SpeedRamp) on the voice's own clock (a slip
        // only shifts it): media from the clip's start is its integral, the voice's Rate its mean. A ramp keeps pitch (the engine stretches
        // by a varying tempo; Keep Pitch off has no tape glide on a ramp).
        var ramp = layer.HasSpeed && layer.HasSpeedRamp ? new AudioSpeedRamp(layer.SpeedKeys, layer.Speed, net) : null;
        var mediaSpan = !timed ? double.PositiveInfinity : ramp is not null ? layer.MediaSecondsAt(layer.Duration) : layer.Duration * speed;
        var rate = ramp is not null && timed && mediaSpan > 0 ? mediaSpan / layer.Duration * net : speed * net;

        // A hand-drawn ducking curve (Layer.DuckOverrideKeys) only shapes a ducked sound, from the clip's sequence start like its keys.
        var drawn = sound.Ducked && layer.HasDuckOverrides ? new DuckOverrideCurve(layer.DuckOverrideKeys, sound.Clip?.Start ?? layer.StartTime) : null;

        var fadeIn = Math.Clamp(layer.FadeInSeconds, 0, 60);
        var fadeOut = Math.Clamp(layer.FadeOutSeconds, 0, 60);

        // A fade that is one side of a same-track crossfade follows the seam's gain law (Pass 5.7.1): equal power (quarter sine and
        // cosine, AudioFade EaseOut) or linear; any other fade stays linear (afade).
        var (inCurve, outCurve) = CrossfadeSides(sound);
        return new AudioVoice
        {
            Source = source,
            SequenceStart = startsAt,
            SequenceEnd = timed ? startsAt + layer.Duration : double.PositiveInfinity,
            MediaStart = layer.MediaOffset,
            MediaEnd = timed ? layer.MediaOffset + mediaSpan : double.PositiveInfinity,
            Rate = rate,
            Ramp = ramp,
            DuckOverride = drawn,
            Pitch = layer.KeepPitch || ramp is not null ? pitch : pitch * speed,
            Spectral = layer.SpectralAudio,
            Gain = gain,
            FadeIn = new AudioFade(fadeIn > 0.01 ? fadeIn : 0, EasingOf(inCurve)),
            FadeOut = new AudioFade(fadeOut > 0.01 && timed ? Math.Min(fadeOut, layer.Duration) : 0, EasingOf(outCurve)),
            FadeOutEnd = timed ? startsAt + layer.Duration : double.PositiveInfinity,
            Strip = strip,
            Label = $"clip {System.IO.Path.GetFileName(path)}",
        };
    }

    // ----- Crossfade gain laws (Pass 5.7.1) -----

    /// <summary>
    /// The gain law of each fade of a sound that is a side of a same-track crossfade (the later clip's TransitionIn across a cut): the fade
    /// in when its clip crossfades from the clip before, the fade out when the next clip crossfades from it; null for a fade that is no
    /// crossfade (or a sound that is no sequence clip).
    /// </summary>
    internal static (CrossfadeCurve? In, CrossfadeCurve? Out) CrossfadeSides(HeardSound sound)
    {
        if (sound.Clip is not { } clip || sound.Track is not { } track)
            return (null, null);

        var index = track.IndexOf(clip);
        if (index < 0)
            return (null, null);

        CrossfadeCurve? into = null, outOf = null;
        if (clip.TransitionIn > 0.01 && index > 0 && Meet(track.Clips[index - 1], clip))
            into = SeamCurve(track.Clips[index - 1], clip);
        if (index + 1 < track.Clips.Count && track.Clips[index + 1] is { TransitionIn: > 0.01 } next && Meet(clip, next))
            outOf = SeamCurve(clip, next);
        return (into, outOf);
    }

    /// <summary>
    /// The gain law across the cut where <paramref name="before"/> meets <paramref name="after"/>: the one the Seam menu set on the later clip
    /// (the crossfade's owner); left at equal power, a through-edit (one sound cut in two, its file time continuous) keeps its level with
    /// linear ramps, as two correlated halves need.
    /// </summary>
    internal static CrossfadeCurve SeamCurve(TimelineClip before, TimelineClip after)
    {
        var curve = CrossfadeCurves.Known(after.State.CrossfadeInCurve);
        return curve == CrossfadeCurve.EqualPower && IsThroughEdit(before, after) ? CrossfadeCurve.Linear : curve;
    }

    /// <summary>One take cut in two: same file and streams at one speed, the later clip's file time continuing the earlier's at the cut.</summary>
    internal static bool IsThroughEdit(TimelineClip before, TimelineClip after) =>
        before.PlaysFile && after.PlaysFile && Meet(before, after) && before.ContentKind == after.ContentKind
        && string.Equals(before.SourcePath, after.SourcePath, StringComparison.OrdinalIgnoreCase) && before.AudioStreamIndex == after.AudioStreamIndex
        && before.VideoStreamIndex == after.VideoStreamIndex && Math.Abs(before.Speed - after.Speed) < 1e-9
        && Math.Abs(after.MediaOffset - before.MediaEnd) <= 1e-3 * Math.Max(1, before.Speed);

    // Two clips meeting at a cut (abutting within a microsecond).
    private static bool Meet(TimelineClip before, TimelineClip after) => Math.Abs(after.Start - before.End) < 1e-6;

    /// <summary>The fade shape for a crossfade side: equal power is the quarter sine (EaseOut: sin(u * PI / 2)), else linear.</summary>
    internal static EasingType EasingOf(CrossfadeCurve? curve) => curve == CrossfadeCurve.EqualPower ? EasingType.EaseOut : EasingType.Linear;

    private static void AddVoiceover(SequenceExportState state, int bus, List<AudioVoice> voices, List<AudioStrip> strips)
    {
        var path = Clean(state.VoiceoverPath);
        if (!state.IncludeVoiceover || path.Length == 0)
            return;

        // Mirrors BuildVoiceoverChain: trimmed to its handles, placed at its start, at its gain.
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

    // Video start found by FFmpeg, resolved as the GPU scene does: the state's, else MediaProbe's session cache; NaN when unknown
    // (the decoder then reads the file itself).
    private static double VideoStart(SequenceExportState state, string path) =>
        state.MediaStarts.TryGetValue(path, out var start) ? start : MediaProbe.KnownVideoStart(path) ?? double.NaN;
}
