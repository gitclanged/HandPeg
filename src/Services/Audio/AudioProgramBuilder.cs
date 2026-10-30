using System.Collections.Concurrent;
using System.Globalization;
using HandPegApp.Models;

namespace HandPegApp.Services.Audio;

/// <summary>
/// A SequenceExportState read into an <see cref="AudioProgram"/> with the FFmpeg path's semantics, from the very lowering
/// the graphs are written from (FilterGraphBuilder.Lowering, cached by document, so this costs little on every edit):
/// <list type="bullet">
/// <item>The main file's kept audio streams (SequenceExportState.AudioTracks): each a strip, its clips' sound where
/// Lowered.MainSound puts it (J and L cuts, slips, gains, fades: PlaceMainAudio, MainSoundFades, MainSoundGain), the track's
/// slip and silenced pieces (its edit chain), ducked when Auto-Duck (a voice track is the side chain), then its gain and
/// processing (its FilterChain). As the export orders them: the track's processing after the ducking.</item>
/// <item>Every other clip that is heard (Lowered.LayerAudio, when MixClipSound): a strip of its own, read from its file on the
/// file's own clock (the cut of BuildLayerAudio: origin + MediaOffset), at its speed, its slip moving where it starts
/// (LayerAudioStart), its processing and gain (TrackAudioFilters.BuildChain), then its fades (BuildClipFades), then ducked.</item>
/// <item>The session's voiceover (IncludeVoiceover): as a clip, a voice for the ducking, as the export has it.</item>
/// <item>The sequence's fades (FadeIn, FadeOut: a second each) over the whole; nothing past the sequence's end.</item>
/// </list>
/// Not here, as editing has never heard them: loudness normalization (it holds seconds back), and the old one-box
/// ducking of presets from before ducking was per track (DuckAudio).
/// </summary>
internal static class AudioProgramBuilder
{
    private static readonly ConcurrentDictionary<string, byte> NotedUnsupported = new(StringComparer.Ordinal);

    public static AudioProgram Build(SequenceExportState state, AudioBuildOptions options)
    {
        SequenceLowering lowering;
        try
        {
            lowering = FilterGraphBuilder.Lowering(state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Sound engine: the timeline could not be lowered; nothing is heard", ex);
            return AudioProgram.Empty;
        }

        var voices = new List<AudioVoice>();
        var strips = new List<AudioStrip>();
        var solo = options.SoloStreams is { Count: > 0 } streams ? streams : null;
        var duck = options.Duck ?? DuckParameters.ForAmount(state.DuckAmountDb);

        AddMainTracks(state, lowering, options, solo, voices, strips);
        if (state.MixClipSound && solo is null)
        {
            AddClips(state, lowering, voices, strips);
            AddVoiceover(state, voices, strips);
        }

        foreach (var strip in strips)
        {
            foreach (var name in strip.Unsupported)
            {
                if (NotedUnsupported.TryAdd(name, 0))
                    AppLog.Write($"Sound engine: the {name} filter is not heard while editing (the export has it)");
            }
        }

        return new AudioProgram(lowering.SequenceSeconds, [.. voices], [.. strips], state.FadeIn, state.FadeOut, duck);
    }

    private static void AddMainTracks(SequenceExportState state, SequenceLowering lowering, AudioBuildOptions options, IReadOnlyCollection<int>? solo,
        List<AudioVoice> voices, List<AudioStrip> strips)
    {
        var path = Clean(state.LocalMediaPath);
        if (!state.HasSource || path.Length == 0 || !state.SourceMayHaveAudio)
            return;

        foreach (var track in state.AudioTracks)
        {
            if (track.IsSilentBase || (solo is not null && !solo.Contains(track.Index)))
                continue;

            var chain = AudioChainParser.Parse(track.FilterChain);
            var strip = strips.Count;
            strips.Add(new AudioStrip
            {
                Key = string.Create(CultureInfo.InvariantCulture, $"main:{track.Index}"),
                Kind = AudioStripKind.MainTrack,
                IsVoice = track.IsVoice,
                Ducked = track.AutoDuck && !track.IsVoice,
                Effects = chain.Effects,
                Unsupported = chain.Unsupported,
                Label = track.Title.Length > 0 ? track.Title : string.Create(CultureInfo.InvariantCulture, $"Track {track.Index + 1}"),
            });

            // A track's tempo (its Pitch & Tempo filter) runs after the track is placed, so it stretches the whole placed
            // track: every place and length on it is divided by it, as atempo divides the time of what it is given.
            var tempo = Math.Clamp(chain.Tempo, 0.25, 4);
            var (videoStart, containerStart) = (VideoStart(state, path), ContainerStart(state, path));
            var source = new AudioSource(path, track.Index) { VideoStart = videoStart };

            // The main clips' offsets count from the container's start (the export's first input, the player's time-pos);
            // media time counts from the first video frame. The GPU scene shifts the chain's picture by the same amount
            // (GpuScenePlan.ChainShift), so a sound and its picture stay one moment of the file.
            var shift = double.IsFinite(videoStart - containerStart) ? videoStart - containerStart : 0;
            var n = 0;
            foreach (var clip in lowering.MainSound)
            {
                var length = clip.End - clip.Start;
                if (length <= 0.0005)
                    continue;

                // The track's slip moves its sound later (the edit chain's adelay, or its atrim when earlier), before it is
                // placed: what plays at the clip's start is that much earlier in the file.
                var mediaStart = clip.Offset - track.Offset - shift + options.MainMediaShift;
                var fadeIn = Math.Min(clip.FadeIn, length);
                var fadeOut = Math.Min(clip.FadeOut, Math.Max(length - fadeIn, 0));
                voices.Add(new AudioVoice
                {
                    Source = source,
                    SequenceStart = clip.Start / tempo,
                    SequenceEnd = clip.End / tempo,
                    MediaStart = mediaStart,
                    Rate = tempo,
                    Pitch = chain.Pitch,
                    Gain = Math.Abs(clip.GainDb) >= 0.05 ? (float)Math.Pow(10, Math.Clamp(clip.GainDb, -40, 24) / 20) : 1f,
                    FadeIn = new AudioFade(fadeIn > 0.01 ? fadeIn / tempo : 0),
                    FadeOut = new AudioFade(fadeOut > 0.01 ? fadeOut / tempo : 0),
                    FadeOutEnd = clip.End / tempo,
                    // The track's silenced pieces are in its own (container) time, as the export's volume=0 reads them.
                    Silenced = Silenced(track.MutedSpans, clip.Start, mediaStart + shift, clip.Start, clip.End, tempo),
                    Strip = strip,
                    Label = string.Create(CultureInfo.InvariantCulture, $"main a:{track.Index} clip {n++}"),
                });
            }
        }
    }

    // A track's silenced pieces (its own time, the file's: before the slip) where they fall on the sequence within a clip.
    private static IReadOnlyList<(double Start, double End)> Silenced(IReadOnlyList<(double Start, double End)> muted, double clipStart, double mediaStart,
        double from, double to, double tempo)
    {
        if (muted.Count == 0)
            return [];
        var list = new List<(double, double)>();
        foreach (var (start, end) in muted)
        {
            var (a, b) = (start - mediaStart + clipStart, end - mediaStart + clipStart);
            if (b > from && a < to)
                list.Add((Math.Max(a, from) / tempo, Math.Min(b, to) / tempo));
        }

        return list;
    }

    private static void AddClips(SequenceExportState state, SequenceLowering lowering, List<AudioVoice> voices, List<AudioStrip> strips)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var layer in lowering.LayerAudio)
        {
            var path = Clean(layer.ImagePath);
            if (path.Length == 0)
                continue;

            var speed = layer.HasSpeed && double.IsFinite(layer.Speed) ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;
            var startsAt = layer.StartTime + (layer.IsAudio ? 0 : layer.AudioOffset);
            var chain = AudioChainParser.Parse(layer.AudioFilters.BuildChain(Math.Clamp(layer.AudioGainDb, -40, 24)));
            var rate = speed * Math.Clamp(chain.Tempo, 0.25, 4);
            var timed = layer.Duration > 0.001;

            // A strip's key is its clip's line on the timeline and its track: its processing keeps its state while its
            // settings change; a clip moved is a new strip, as FFmpeg's chain for it is new.
            var line = string.Create(CultureInfo.InvariantCulture, $"clip:{path}|{layer.TrackId}|{Math.Round((startsAt - layer.MediaOffset / speed) * 1e6):0}");
            var n = occurrences.GetValueOrDefault(line);
            occurrences[line] = n + 1;

            var strip = strips.Count;
            strips.Add(new AudioStrip
            {
                Key = n == 0 ? line : string.Create(CultureInfo.InvariantCulture, $"{line}#{n}"),
                Kind = AudioStripKind.Clip,
                IsVoice = layer.IsVoice,
                Ducked = layer.AutoDuck && !layer.IsVoice,
                Effects = chain.Effects,
                Unsupported = chain.Unsupported,
                Label = System.IO.Path.GetFileName(path),
            });

            // A sound clip's file is read on its own clock from 0, as the graph reads it (a sound clip is not in MediaStarts);
            // a video's from its first frame (MediaStarts, else what the decoder finds), as its picture is.
            var source = new AudioSource(path, 0)
            {
                VideoStart = layer.IsAudio ? 0 : VideoStart(state, path),
            };

            var fadeIn = Math.Clamp(layer.FadeInSeconds, 0, 60);
            var fadeOut = Math.Clamp(layer.FadeOutSeconds, 0, 60);
            voices.Add(new AudioVoice
            {
                Source = source,
                SequenceStart = startsAt,
                SequenceEnd = timed ? startsAt + layer.Duration : double.PositiveInfinity,
                MediaStart = layer.MediaOffset,
                MediaEnd = timed ? layer.MediaOffset + layer.Duration * speed : double.PositiveInfinity,
                Rate = rate,
                Pitch = chain.Pitch,
                FadeIn = new AudioFade(fadeIn > 0.01 ? fadeIn : 0),
                FadeOut = new AudioFade(fadeOut > 0.01 && timed ? Math.Min(fadeOut, layer.Duration) : 0),
                FadeOutEnd = timed ? startsAt + layer.Duration : double.PositiveInfinity,
                Strip = strip,
                Label = $"clip {System.IO.Path.GetFileName(path)}",
            });
        }
    }

    private static void AddVoiceover(SequenceExportState state, List<AudioVoice> voices, List<AudioStrip> strips)
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

    private static string Clean(string path) => path.Trim().Trim('"');

    // Where FFmpeg found a file's picture and its container to start, as the GPU scene looks them up (GpuScenePlan.ChainShiftOf):
    // the state's, else what MediaProbe learnt this session; NaN when neither knows (the decoder then reads the file itself).
    private static double VideoStart(SequenceExportState state, string path) =>
        state.MediaStarts.TryGetValue(path, out var start) ? start : MediaProbe.KnownVideoStart(path) ?? double.NaN;

    private static double ContainerStart(SequenceExportState state, string path) =>
        state.SeekIndexes.TryGetValue(path, out var index) ? index.ContainerStart : MediaProbe.KnownSeekIndex(path)?.ContainerStart ?? double.NaN;
}
