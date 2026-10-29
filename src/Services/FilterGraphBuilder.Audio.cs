using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;

namespace HandPegApp.Services;

// ----- The graph builder's sound: the live sound graph, the main video's sound, voiceover, layers' and tracks'
// sound, clip fades, tempo and retiming, and the export's audio options -----
public static partial class FilterGraphBuilder
{
    private sealed partial class Pass
    {
        /// <summary>
        /// The sound half of the live graph, from the player's audio tracks to its speakers: every audio track
        /// of the video that is kept (not muted), with what the export does to it, and the sound of the layers
        /// (video layers that carry sound, and audio clips), mixed. Empty when there is nothing to it but one
        /// track played as it is: the player is then given no audio graph, and plays that track itself.
        ///
        /// Each track, as the export has it: its silenced stretches, its slip, ducked under the voices when it is
        /// set to duck, then its own gain and processing (filters, EQ, compressor...). The fades in and out are
        /// heard too. Not loudness normalization, which holds seconds of sound back and would fall out of step
        /// with the picture.
        ///
        /// A layer's sound is read from its file inside the graph (amovie), as the export reads it, and mpv
        /// does not seek such a source: after every jump it begins again at its beginning. Nor does the mix look
        /// at time stamps; it adds up whatever samples arrive. So the sound is laid out from one moment, the one
        /// playback starts at: every layer is cut and held back as that moment needs, each track of the video is
        /// made to begin exactly there (aresample's first_pts drops what comes before it and fills in what is
        /// missing), and all of them are then counted sample by sample from that moment (asetpts) and start level.
        /// A jump to another moment needs the graph built again for that moment, which is for whoever plays it to ask for.
        ///
        /// The count is kept on the sequence's clock, the one the picture ([vo]) is on: the sound's first sample is
        /// stamped with that moment, not 0. mpv lines the two up by their time stamps when playback starts, and drops
        /// the sound it holds before the picture's first frame: a sound counted from 0 was dropped for as long as the
        /// moment was into the sequence, and then played that far ahead of the picture (seen at 300 s: the layer's file
        /// 418 s in under the frame of 118 s in).
        /// </summary>
        /// <param name="from">The moment playback starts from, in sequence seconds.</param>
        /// <param name="mainTrack">Negative when the video has no sound of its own; otherwise every kept track is heard.</param>
        public string BuildLiveAudioGraph(double from, int mainTrack)
        {
            if (!HasSource)
                return "";

            // Only what still has something to play: a clip that ended before this moment is not opened at all,
            // nor one that starts after the window (the graph is built again before it does).
            var layers = GetLayerAudioSources()
                .Where(l => (l.Duration <= 0.001 || LayerAudioStart(l) + l.Duration > from + 0.05)
                            && InWindow(LayerAudioStart(l), l.Duration > 0.001 ? LayerAudioStart(l) + l.Duration : double.PositiveInfinity))
                .ToList();
            var tracks = mainTrack < 0 ? [] : GetOutputAudioTracks().Where(t => !t.IsSilentBase).ToList();

            // Nothing that could be heard at all. Otherwise the sound always goes through the graph, even one plain
            // track: once a graph has taken mpv's audio tracks, mpv does not give them back to plain playback, so a
            // graph without sound after one with sound is silence (muting down to one track did exactly that).
            // With every track muted, the graph plays silence rather than mpv playing a muted track.
            if (layers.Count == 0 && mainTrack < 0)
                return "";

            // Every input in the one form, and counted from the moment playback starts, on the sequence's clock: what
            // is mixed, and what is ducked under what, then lines up sample for sample, and with the picture.
            const string form = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
            var first = ((long)Math.Round(from * 48000)).ToString(CultureInfo.InvariantCulture);
            var fromHere = $"asetpts=(N+{first})/SR/TB";
            var graph = new List<string>();
            var inputs = new List<(string Name, bool Voice, bool Ducked, IReadOnlyList<string> Filters)>();

            // Where the main video is not on the sequence the player plays a stand-in stretch of its file
            // (see MainViewModel.PlayerSource), whose sound is not to be heard; nor where its clips are silenced. (The
            // player places the file's sound as it places its picture, so that is what is let through: a J or L cut, or a
            // slip, of one of its clips is exact in the export, and heard here as its picture is placed, until the player
            // is given the sound's own places.)
            var gate = IsMainWholeSequence ? "" : $"volume=0:enable='not({FilterGraphBuilder.BuildMainGate(GetMainHeard(), 0, SequenceSeconds)})',";

            // And it fades in and out with its clips' pictures, at each clip's gain.
            if (MainSoundEnvelope() is { } envelope)
                gate += $"volume='{envelope}':eval=frame,";
            for (var k = 0; k < tracks.Count; k++)
            {
                var track = tracks[k];
                var steps = new List<string>();

                // Silenced where it was silenced on the timeline: the times are the track's own, as the player reads it.
                foreach (var (start, end) in track.MutedSpans)
                    steps.Add($"volume=0:enable='between(t,{Number(start)},{Number(end)})'");

                // Slipped later: held back by as much. Slipped earlier: what comes before the moment it now plays
                // at is dropped, and the rest moved up. (Slipped later, the first moments after a jump are silent:
                // what would play there comes from before the point the player reads the track from.)
                if (track.Offset > 0.001)
                    steps.Add($"adelay={(int)Math.Round(track.Offset * 1000)}:all=1");
                else if (track.Offset < -0.001)
                    steps.Add($"atrim=start={Number(from - track.Offset)},asetpts=PTS-{Number(-track.Offset)}/TB");

                graph.Add($"[aid{(track.Index + 1).ToString(CultureInfo.InvariantCulture)}]{gate}{string.Concat(steps.Select(step => step + ","))}"
                          + $"aresample=48000:async=1:first_pts={first},{fromHere},{form}[lt{k}]");
                inputs.Add(($"lt{k}", track.IsVoice, track.AutoDuck && !track.IsVoice, track.GetFilterChain()));
            }

            if (tracks.Count == 0)
            {
                // No sound of the video's own: silence to the end of the sequence, for the rest to be mixed into.
                graph.Add($"anullsrc=r=48000:cl=stereo,atrim=end={Number(Math.Max(SequenceSeconds - from, 0.1))},{fromHere},{form}[lt_base]");
                inputs.Add(("lt_base", false, false, []));
            }

            // The layers bring their own gain and processing (see BuildLayerAudio).
            for (var n = 0; n < layers.Count; n++)
            {
                graph.Add($"{BuildLayerAudio(layers[n], from)},{fromHere},{form}[ll{n}]");
                inputs.Add(($"ll{n}", layers[n].IsVoice, layers[n] is { AutoDuck: true, IsVoice: false }, []));
            }

            // Auto-duck: the voices are mixed into one signal, which presses each ducked sound down while it is loud.
            var voices = inputs.Where(i => i.Voice).ToList();
            var ducked = inputs.Where(i => i.Ducked).ToList();
            if (voices.Count > 0 && ducked.Count > 0)
            {
                var keys = new List<string>();
                foreach (var voice in voices)
                {
                    graph.Add($"[{voice.Name}]asplit=2[{voice.Name}_hear][{voice.Name}_key]");
                    keys.Add($"[{voice.Name}_key]");
                    Rename(voice.Name, $"{voice.Name}_hear");
                }

                var key = keys[0];
                if (keys.Count > 1)
                {
                    graph.Add($"{string.Concat(keys)}amix=inputs={keys.Count}:duration=longest:normalize=0[dkey]");
                    key = "[dkey]";
                }

                if (ducked.Count > 1)
                {
                    graph.Add($"{key}asplit={ducked.Count}{string.Concat(ducked.Select((_, n) => $"[dkey{n}]"))}");
                    for (var n = 0; n < ducked.Count; n++)
                        Duck(ducked[n].Name, $"[dkey{n}]");
                }
                else
                {
                    Duck(ducked[0].Name, key);
                }

                // (A ducked sound is never a voice, so it still has the name it was given.)
                void Duck(string name, string keyLabel)
                {
                    graph.Add($"[{name}]{keyLabel}{DuckFilter}[{name}_ducked]");
                    Rename(name, $"{name}_ducked");
                }
            }

            // Each track's own gain and processing, after the ducking, as in the export.
            foreach (var input in inputs.Where(i => i.Filters.Count > 0).ToList())
            {
                graph.Add($"[{input.Name}]{string.Join(",", input.Filters)}[{input.Name}_fx]");
                Rename(input.Name, $"{input.Name}_fx");
            }

            // The fades over the whole, on the sequence's clock, which the mix is on.
            var finish = new List<string>();
            if (FadeIn && from < FadeSeconds)
                finish.Add($"volume='clip(t/{Number(FadeSeconds)},0,1)':eval=frame");
            if (FadeOut && SequenceSeconds > FadeSeconds)
                finish.Add($"volume='clip(({Number(SequenceSeconds)}-t)/{Number(FadeSeconds)},0,1)':eval=frame");

            // The first input sets how long the mix lasts: a track of the video, or the silence standing in for one.
            var mix = inputs.Count == 1 ? $"[{inputs[0].Name}]anull" : $"{string.Concat(inputs.Select(i => $"[{i.Name}]"))}amix=inputs={inputs.Count}:duration=first:normalize=0:dropout_transition=0";
            graph.Add($"{mix}{string.Concat(finish.Select(f => "," + f))}{LiveAudioOutput}");
            return string.Join(";", graph);

            void Rename(string name, string to)
            {
                var at = inputs.FindIndex(i => i.Name == name);
                inputs[at] = inputs[at] with { Name = to };
            }
        }

        /// <summary>
        /// What puts the main video's own sound where its picture is on the sequence: cut to the part of the
        /// file that is used, and held back until the main video comes in. Empty while the main video is the
        /// whole sequence.
        /// </summary>
        /// <param name="label">Where the track's sound comes from.</param>
        /// <param name="edits">What was done to the track itself (silenced parts, a slip), applied before it is placed.</param>
        /// <param name="mono">
        /// Whether the track has one channel. One that comes out beginning with silence is made two-channel:
        /// FFmpeg's AAC encoder has been seen to stop dead on exactly that (one channel, digital silence first,
        /// 192k or more), and the same sound on two channels goes through.
        /// </param>
        /// <param name="tag">The label the placed sound gets, when anything had to be done to it.</param>
        /// <returns>The label to carry on from.</returns>
        private string PlaceMainAudio(List<string> graph, string label, List<string> edits, bool mono, string tag)
        {
            var all = GetMainSound();
            var clips = IsMainWholeSequence ? [] : all;

            // No clip of the file is heard anywhere: silence as long as the sequence, for the rest to be mixed into.
            if (!IsMainWholeSequence && all.Count == 0)
            {
                graph.Add($"anullsrc=r={MixRate}:cl=stereo:d={Number(Math.Max(SequenceSeconds, 0.1))}[{tag}]");
                return $"[{tag}]";
            }

            if (clips.Count <= 1)
            {
                var steps = new List<string>(edits);
                if (clips.Count == 1)
                {
                    var clip = clips[0];
                    if (clip.Offset > 0.001 || clip.End - clip.Start < MainMediaSeconds - clip.Offset - 0.02)
                        steps.Add($"atrim={(clip.Offset > 0.001 ? $"start={Number(clip.Offset)}:" : "")}end={Number(clip.Offset + clip.End - clip.Start)},asetpts=PTS-STARTPTS");
                    if (clip.Start > 0.001)
                        steps.Add($"adelay={(int)Math.Round(clip.Start * 1000)}:all=1");
                }

                // Its fades, where it now lies on the sequence (the whole file, untouched, lies from 0), and its gain.
                if (all.Count == 1)
                {
                    steps.AddRange(MainSoundFades(all[0], at: all[0].Start));
                    if (MainSoundGain(all[0]) is { } gain)
                        steps.Add(gain);
                }

                if (mono && steps.Any(e => e.StartsWith("adelay", StringComparison.Ordinal)))
                    steps.Add("aformat=channel_layouts=stereo");
                if (clips.Count == 1 && LastsTo(clips) is { } pad)
                    steps.Add(pad);
                if (steps.Count == 0)
                    return label;

                graph.Add($"{label}{string.Join(",", steps)}[{tag}]");
                return $"[{tag}]";
            }

            // Several clips: the track is copied once for each, every copy cut to its clip and held back to where
            // the clip is, and the copies are laid over each other. Sound, unlike pictures, is cheap to read through.
            graph.Add($"{label}{(edits.Count > 0 ? string.Join(",", edits) + "," : "")}asplit={clips.Count}{string.Concat(clips.Select((_, j) => $"[{tag}_c{j}]"))}");
            for (var j = 0; j < clips.Count; j++)
            {
                var clip = clips[j];
                graph.Add($"[{tag}_c{j}]atrim=start={Number(clip.Offset)}:end={Number(clip.Offset + clip.End - clip.Start)},asetpts=PTS-STARTPTS"
                          + string.Concat(MainSoundFades(clip, at: 0).Select(fade => "," + fade))
                          + (MainSoundGain(clip) is { } gain ? "," + gain : "")
                          + (clip.Start > 0.001 ? $",adelay={(int)Math.Round(clip.Start * 1000)}:all=1" : "") + (mono ? ",aformat=channel_layouts=stereo" : "") + $"[{tag}_p{j}]");
            }

            // The mix is given a clock of its own, counted from its samples: what it inherits from clips taken out of
            // order is not one an encoder can follow, and the track came out empty.
            graph.Add($"{string.Concat(clips.Select((_, j) => $"[{tag}_p{j}]"))}amix=inputs={clips.Count}:normalize=0:dropout_transition=0,asetpts=N/SR/TB"
                      + (LastsTo(clips) is { } padded ? "," + padded : "") + $"[{tag}]");
            return $"[{tag}]";
        }

        /// <summary>
        /// The step that makes a placed track last to the sequence's end when its last clip ends before it: silence after.
        /// What is mixed into the first track lasts only as long as it does (amix, duration=first), so a track that stopped
        /// with the main video's last clip cut off every sound after it (music under the end titles, a clip on another track);
        /// in 4.2.0 the main video was rarely not the last thing on the timeline. Null when it lasts to the end already.
        /// </summary>
        private string? LastsTo(IReadOnlyList<MainClip> clips) =>
            clips.Count > 0 && SequenceSeconds - clips.Max(c => c.End) > 0.001 ? $"apad=whole_dur={Number(SequenceSeconds)}" : null;

        /// <summary>A main video clip's own gain as a step (within a layer's limits, -40 to +24 dB), or null for none.</summary>
        private static string? MainSoundGain(MainClip clip) =>
            Math.Abs(clip.GainDb) >= 0.05 ? $"volume={Number(Math.Clamp(clip.GainDb, -40, 24))}dB" : null;

        /// <summary>A main video clip's sound fading in and out with its picture, on a clock where the clip starts at <paramref name="at"/>.</summary>
        private static IEnumerable<string> MainSoundFades(MainClip clip, double at)
        {
            var length = clip.End - clip.Start;
            var fadeIn = Math.Min(clip.FadeIn, length);
            if (fadeIn > 0.01)
                yield return $"afade=t=in:st={Number(at)}:d={Ratio(fadeIn)}";
            var fadeOut = Math.Min(clip.FadeOut, Math.Max(length - fadeIn, 0));
            if (fadeOut > 0.01)
                yield return $"afade=t=out:st={Number(at + length - fadeOut)}:d={Ratio(fadeOut)}";
        }

        /// <summary>
        /// The main video's sound as its clips' fades and gains shape it, on the sequence's clock, for the live mix: each
        /// fade's ramp and each clip's gain inside the clip, one everywhere else, so that a clip's fade or gain leaves the
        /// clips beside it alone. Null when no clip fades or has a gain.
        /// </summary>
        private string? MainSoundEnvelope()
        {
            var terms = new StringBuilder();
            foreach (var clip in GetMainHeard())
            {
                var length = clip.End - clip.Start;
                var fadeIn = Math.Min(clip.FadeIn, length);
                if (fadeIn > 0.01)
                    terms.Append($"*({Progress("t", clip.Start, fadeIn)}+lt(t,{Number(clip.Start)}))");
                var fadeOut = Math.Min(clip.FadeOut, Math.Max(length - fadeIn, 0));
                if (fadeOut > 0.01)
                    terms.Append($"*(clip(({Number(clip.End)}-t)/{Ratio(fadeOut)},0,1)+gte(t,{Number(clip.End)}))");
                if (MainSoundGain(clip) is not null)
                {
                    var factor = Math.Pow(10, Math.Clamp(clip.GainDb, -40, 24) / 20) - 1;
                    terms.Append($"*(1{(factor < 0 ? "-" : "+")}{Ratio(Math.Abs(factor))}*gte(t,{Number(clip.Start)})*lt(t,{Number(clip.End)}))");
                }
            }

            return terms.Length > 0 ? "1" + terms : null;
        }

        /// <summary>The voiceover's own steps: cut to its trim handles, moved to where it starts, set to its gain.</summary>
        private string BuildVoiceoverChain()
        {
            var steps = new List<string>();
            if (VoiceoverTrimStart > 0.05 || (VoiceoverDuration > 0 && VoiceoverTrimEnd < VoiceoverDuration - 0.05 && VoiceoverTrimEnd > VoiceoverTrimStart))
                steps.Add($"atrim=start={Number(Math.Max(VoiceoverTrimStart, 0))}:end={Number(VoiceoverTrimEnd)},asetpts=PTS-STARTPTS");
            if (VoiceoverStartSeconds >= 0.01)
                steps.Add($"adelay={(int)Math.Round(VoiceoverStartSeconds * 1000)}:all=1");
            if (Math.Abs(VoiceoverGainDb) >= 0.05)
                steps.Add($"volume={Number(VoiceoverGainDb)}dB");
            return steps.Count > 0 ? string.Join(",", steps) : "anull";
        }

        /// <summary>
        /// Adds the audio side of the command: graph pieces for mixing, ducking and the voiceover, the maps, and
        /// (returned) the per-stream encoder options. Tracks the graph does not need are mapped directly.
        /// </summary>
        /// <param name="duration">How long the output is, in seconds: where the fade out ends.</param>
        /// <param name="voiceoverInput">Which input (-i) the voiceover is, or null when there is none to mix in.</param>
        /// <param name="preparedAudio">The label each track continues from that was made ready in the graph (see BuildFfmpegCommand).</param>
        private List<string> BuildAudio(
            List<ExportAudioTrack> tracks, bool mix, bool duck, double duration, int? voiceoverInput,
            Dictionary<int, string> preparedAudio, List<string> graph, List<string> maps)
        {
            var options = new List<string>();

            // Each track continues from what was made ready for it, or else straight from the input.
            string Source(ExportAudioTrack track) =>
                preparedAudio.TryGetValue(track.Index, out var ready) ? ready : $"[0:a:{track.Index}]";

            // A track with a gain or processing of its own (the Audio tab) gets that first, as a step of the
            // graph, and whatever is done with the track afterwards continues from there.
            var prepared = new Dictionary<int, string>();
            string Prepared(ExportAudioTrack track)
            {
                if (!track.HasProcessing || track.GetFilterChain() is not { Count: > 0 } chain)
                    return Source(track);

                if (!prepared.TryGetValue(track.Index, out var label))
                {
                    label = $"[at{track.Index}]";
                    graph.Add($"{Source(track)}{string.Join(",", chain)}{label}");
                    prepared[track.Index] = label;
                }

                return label;
            }

            // Applied to whatever is encoded: loudness first, so the fades are not undone by it.
            var finishing = new List<string>();
            if (NormalizeAudio)
                finishing.Add(NormalizeFilters);
            if (FadeIn)
                finishing.Add($"afade=t=in:st=0:d={Number(FadeSeconds)}");
            if (FadeOut && duration > FadeSeconds)
                finishing.Add($"afade=t=out:st={Number(duration - FadeSeconds)}:d={Number(FadeSeconds)}");
            var finishingChain = string.Join(",", finishing);
            var thenFinishing = finishing.Count > 0 ? "," + finishingChain : "";

            // The voiceover: its own input, cut to its trim handles, moved to where it starts and set to its
            // gain. It was made against the finished timeline, whose clock is the sequence's: its times are
            // times in the output.
            string? voice = null;
            if (voiceoverInput is { } input)
            {
                graph.Add($"[{input}:a:0]{BuildVoiceoverChain()}[voiceover]");
                voice = "[voiceover]";
            }

            // Lays the voiceover over a track. The track decides the length; neither is turned down to make room.
            string WithVoice(string label)
            {
                if (voice is null)
                    return label;

                graph.Add($"{label}{voice}amix=inputs=2:duration=first:normalize=0[a_voiced]");
                return "[a_voiced]";
            }

            // A video without sound of its own: the voiceover is its audio, ending where the picture does.
            if (tracks.Count == 0)
            {
                if (voice is not null)
                {
                    maps.Add($"-map \"{voice}\"");
                    options.Add(BuildAudioEncodeOptions(0, MixedAudioCodec, AudioBitrate));
                    options.Add("-shortest");
                }

                return options;
            }

            if (mix)
            {
                var inputs = tracks.Select(Prepared).ToList();
                if (duck)
                {
                    // The microphone both steers the compressor and is heard, so it is split in two.
                    graph.Add($"{inputs[1]}asplit=2[mic_key][mic_out]");
                    graph.Add($"{inputs[0]}[mic_key]{DuckingFilter}[ducked]");
                    (inputs[0], inputs[1]) = ("[ducked]", "[mic_out]");
                }

                graph.Add($"{string.Concat(inputs)}amix=inputs={inputs.Count}:duration=longest{thenFinishing}[a_mixed]");
                maps.Add($"-map \"{WithVoice("[a_mixed]")}\"");
                options.Add(BuildAudioEncodeOptions(0, MixedAudioCodec, AudioBitrate, tracks.Max(t => t.Channels)));
                return options;
            }

            for (var k = 0; k < tracks.Count; k++)
            {
                var track = tracks[k];
                string? label = null;

                // The name from the track list, written as the stream's title. Quotes would end the argument early.
                if (!string.IsNullOrWhiteSpace(track.Title))
                    options.Add($"-metadata:s:a:{k} title=\"{track.Title.Trim().Replace('"', '\'')}\"");

                if (duck && k == 0)
                {
                    graph.Add($"{Prepared(tracks[1])}asplit=2[mic_key][mic_out]");
                    graph.Add($"{Prepared(track)}[mic_key]{DuckingFilter}{thenFinishing}[ducked]");
                    label = "[ducked]";
                }
                else if (duck && k == 1)
                {
                    label = "[mic_out]";
                    if (finishing.Count > 0)
                    {
                        graph.Add($"[mic_out]{finishingChain}[mic_done]");
                        label = "[mic_done]";
                    }
                }
                else if (preparedAudio.ContainsKey(track.Index))
                {
                    label = Prepared(track);
                    if (finishing.Count > 0)
                    {
                        graph.Add($"{label}{finishingChain}[an{track.Index}]");
                        label = $"[an{track.Index}]";
                    }
                }

                // The voiceover goes into the first track. That track is then decoded and encoded whatever it
                // was set to: sound cannot be mixed into a stream that is only being copied.
                if (k == 0 && voice is not null)
                {
                    if (label is null)
                    {
                        var own = track.GetFilterChain().Concat(finishing).ToList();
                        label = Source(track);
                        if (own.Count > 0)
                        {
                            graph.Add($"{label}{string.Join(",", own)}[a_first]");
                            label = "[a_first]";
                        }
                    }

                    maps.Add($"-map \"{WithVoice(label)}\"");
                    options.Add(track.IsPassthrough
                        ? BuildAudioEncodeOptions(k, MixedAudioCodec, AudioBitrate, track.Channels)
                        : BuildAudioEncodeOptions(k, track.Codec, track.Bitrate, track.Channels));
                    continue;
                }

                if (label is not null)
                {
                    maps.Add($"-map \"{label}\"");
                    options.Add(BuildAudioEncodeOptions(k, track.Codec, track.Bitrate, track.Channels));
                    continue;
                }

                // Straight from the input. "?" keeps the command valid for a source without such a stream.
                maps.Add($"-map 0:a:{track.Index}?");
                if (track.IsPassthrough)
                {
                    options.Add($"-c:a:{k} copy");
                    continue;
                }

                // Straight from the input, so the track's own processing and the finishing go on as a simple filter.
                options.Add(BuildAudioEncodeOptions(k, track.Codec, track.Bitrate, track.Channels));
                var simple = track.GetFilterChain().Concat(finishing).ToList();
                if (simple.Count > 0)
                    options.Add($"-filter:a:{k} \"{string.Join(",", simple)}\"");
            }

            return options;
        }
        /// <summary>
        /// The sound of a video layer, as a chain that ends where the caller labels it: read from the layer's file
        /// inside the graph, started where the layer's picture starts (or as far from there as it was slipped),
        /// and no longer than the layer is on screen.
        /// </summary>
        /// <param name="from">
        /// The moment of the sequence the chain's sound begins at: 0 for the export, which plays the whole
        /// sequence, and for Live Preview the moment playback starts from. What the layer would have played
        /// before then is skipped in its file, and it is held back only by what is left until it starts.
        /// </param>
        private string BuildLayerAudio(Layer layer, double from = 0)
        {
            // The clip's length is in the timeline's seconds; at its speed it plays that many times as much of its file.
            var speed = GetSpeed(layer);
            var startsAt = LayerAudioStart(layer);
            var into = layer.MediaOffset + Math.Max(from - startsAt, 0) * speed;

            // Cut on the file's own time stamps from where its picture starts, as the picture is (see AppendVideoFile):
            // a sample is heard with the frame that has its time stamp. Counted from the file's first sample instead,
            // the sound was out by as much as the picture starts after it (7 ms in a webm, over a second in some
            // recordings). A file not inspected yet is counted from 0, as its picture is in a live graph.
            var origin = S.MediaStarts.TryGetValue(layer.ImagePath.Trim().Trim('"'), out var videoStart) ? videoStart : 0;
            var cut = origin + into;

            // Read from a little before where it is wanted, when that is well into the file: the cut that follows is exact.
            var source = $"amovie='{EscapeFilterPath(layer.ImagePath)}'";
            if (from > 0 && into > 2)
                source += $":seek_point={Number(into - 1)}";

            var chain = new List<string> { source };
            var trim = cut > 0.0005 ? $"atrim=start={Number(cut)}" : "";
            if (layer.Duration > 0.001)
                trim = (trim.Length > 0 ? trim + ":" : "atrim=") + $"end={Number(origin + layer.MediaOffset + layer.Duration * speed)}";
            if (trim.Length > 0)
                chain.Add(trim + (cut > 0.0005 ? $",asetpts=PTS-{Number(cut)}/TB" : ""));

            // Begun exactly at the cut: a sound that starts after it (a file's sound starting after its picture) is
            // preceded by silence, what comes before it (an encoder's priming) is dropped.
            chain.Add("aresample=48000:async=1:first_pts=0");
            chain.AddRange(BuildTempo(speed));
            // Its own processing and gain, as a track of the video has them.
            chain.AddRange(layer.AudioFilters.BuildChain(Math.Clamp(layer.AudioGainDb, -40, 24)));
            chain.AddRange(BuildClipFades(layer, Math.Max(from - startsAt, 0)));
            if (startsAt - from > 0.01)
                chain.Add($"adelay={(int)Math.Round((startsAt - from) * 1000)}:all=1");
            return string.Join(",", chain);
        }

        /// <summary>
        /// A clip's sound fades (its gain line's handles, and crossfades): in over its first seconds, out over its last, on
        /// the clock of the chain, which starts <paramref name="into"/> seconds into the clip (Live Preview, played from
        /// the middle of it). Started inside a fade, the fade runs from there on, from silence (a preview's approximation).
        /// </summary>
        private static IEnumerable<string> BuildClipFades(Layer layer, double into)
        {
            var fadeIn = Math.Clamp(layer.FadeInSeconds, 0, 60);
            if (fadeIn > 0.01 && into < fadeIn - 0.01)
                yield return $"afade=t=in:st=0:d={Number(fadeIn - into)}";

            var fadeOut = Math.Clamp(layer.FadeOutSeconds, 0, 60);
            if (fadeOut > 0.01 && layer.Duration > 0.001 && into < layer.Duration - 0.01)
            {
                var start = Math.Max(layer.Duration - fadeOut - into, 0);
                yield return $"afade=t=out:st={Number(start)}:d={Number(Math.Min(fadeOut, layer.Duration - into))}";
            }
        }

        /// <summary>
        /// Where on the sequence a layer's sound starts: with its picture, or as far from there as its clip's unlinked sound
        /// was slipped (the lowering leaves <see cref="Layer.AudioOffset"/> at 0 for a linked clip, and a sound clip has no
        /// picture to slip from). Before the sequence's start for one slipped that far: what falls before it is skipped in
        /// its file (BuildLayerAudio), so the rest stays on its moment (4.2.0 held it at 0, a step out of time).
        /// </summary>
        private static double LayerAudioStart(Layer layer) => layer.StartTime + (layer.IsAudio ? 0 : layer.AudioOffset);

        /// <summary>A clip's speed as the graph uses it: within its limits, and exactly 1 for a clip that has none.</summary>
        private static double GetSpeed(Layer layer) =>
            layer.HasSpeed && double.IsFinite(layer.Speed) ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

        /// <summary>Whether a speed is anything other than as recorded.</summary>
        private static bool IsRetimed(double speed) => Math.Abs(speed - 1) >= 0.0005;

        /// <summary>
        /// The filters that play sound at a speed without changing its pitch. One atempo takes the sound from half
        /// speed to double; anything beyond is reached by several in a row, each within that range, whose
        /// speeds multiply out to the one asked for (0.1 is 0.5 x 0.5 x 0.5 x 0.8; 10 is 2 x 2 x 2 x 1.25).
        /// </summary>
        private static List<string> BuildTempo(double speed)
        {
            var steps = new List<string>();
            if (!IsRetimed(speed))
                return steps;

            var left = Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed);
            for (; left > 2.0 + 1e-9; left /= 2.0)
                steps.Add("atempo=2.0");
            for (; left < 0.5 - 1e-9; left /= 0.5)
                steps.Add("atempo=0.5");
            if (Math.Abs(left - 1) >= 0.0005)
                steps.Add($"atempo={left.ToString("0.######", CultureInfo.InvariantCulture)}");
            return steps;
        }

        /// <summary>The filter that plays a picture at a speed: every frame's time divided by it.</summary>
        private static string BuildRetime(double speed) => $"setpts=(1.0/{speed.ToString("0.######", CultureInfo.InvariantCulture)})*PTS";

        /// <param name="outputIndex">Position of the stream among the output's audio streams.</param>
        /// <param name="sourceChannels">How many channels the sound has before it is encoded (the widest of a mix).</param>
        /// <remarks>On Auto and Stereo, for a sound of two channels or fewer, nothing is added: the command is as it always was.</remarks>
        private string BuildAudioEncodeOptions(int outputIndex, string codec, string bitrate, int sourceChannels = 2)
        {
            var options = codec.StartsWith("pcm_", StringComparison.Ordinal)
                ? $"-c:a:{outputIndex} {codec}"
                : $"-c:a:{outputIndex} {codec} -b:a:{outputIndex} {bitrate}";
            if (AudioSampleRate > 0)
                options += $" -ar:a:{outputIndex} {AudioSampleRate.ToString(CultureInfo.InvariantCulture)}";
            if (AudioChannels == AudioChannelChoice.Mono)
                options += $" -ac:a:{outputIndex} 1";
            else if (AudioChannels == AudioChannelChoice.Stereo && sourceChannels > 2)
                options += $" -ac:a:{outputIndex} 2";
            return options;
        }
    }
}
