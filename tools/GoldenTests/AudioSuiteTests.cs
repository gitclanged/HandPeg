using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

/// <summary>
/// The Studio Audio Suite (Pass 5.2B), through the engine's own parts: Studio Voice (left out at 0, exactly late by its latency when keyed to 0,
/// a noise floor taken down while a voice's harmonics stay), the De-Esser, the parametric equalizer's curve against its biquads, the beat
/// detector on a kick at 120 BPM in noise, the live loudness meter (EBU R128, true peak), the Sequence's master gain, the model's new steps
/// (master gain, beats) and the audio-reactive shader bus (the bands, a compositor frame lifted by them, the export's frames' bands). Every
/// line says what it proved; measured numbers go to the console only.
/// </summary>
public static class AudioSuiteTests
{
    private const int Rate = 48000;

    public static IEnumerable<GraphResult> All()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.AudioSuite", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        try
        {
            yield return Case("audio-suite-studio-voice", "# Studio Voice (5.2B): left out at a mix of 0, the input exactly Latency late when keyed to 0, a voice's noise floor down and its harmonics kept at 100 %.", (t, p) => StudioVoice(folder, t, p));
            yield return Case("audio-suite-latency", "# The chains' latency (5.2C): a processed clip's sound read 512 samples ahead, so it lands on its picture to the sample and keeps its edges.", (t, p) => Latency(folder, t, p));
            yield return Case("audio-suite-de-esser", "# The De-Esser (5.2B): a split-band compressor on 5.5-9 kHz takes a 7 kHz burst down and leaves a 1 kHz tone as it is.", DeEsser);
            yield return Case("audio-suite-equalizer", "# The parametric equalizer (5.2B): the octave bands as before when nothing moved, and EqualizerResponseDb against the chain's own biquads.", Equalizer);
            yield return Case("audio-suite-beats", "# BeatDetector (5.2B): a kick at 120 BPM in noise, every beat found within 15 ms and nothing else, a minute under 15 ms.", Beats);
            yield return Case("audio-suite-loudness", "# The live meter (5.2B): EBU R128 momentary, short-term and integrated loudness of a -23 LUFS sine, and the true peak between samples.", Loudness);
            yield return Case("audio-suite-master-gain", "# The Sequence's master gain (5.2B): +6 dB multiplies every sample by 10^(6/20), 0 dB multiplies nothing.", (t, p) => MasterGain(folder, t, p));
            yield return Case("audio-suite-model", "# The model's new parts (5.2B): the filters' new members, the master gain and beats as undoable steps, the settings' defaults.", Model);
            yield return Case("audio-suite-reactive", "# The audio-reactive shader bus (5.2B): the bands of three tones, a Glow lifted by a beat on a real worker and not without one, and the export's frames' bands.", (t, p) => Reactive(folder, t, p));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Note(FormattableString line) => Console.WriteLine("        " + line.ToString(CultureInfo.InvariantCulture));

    private static void Problem(List<string> problems, FormattableString what) => problems.Add(what.ToString(CultureInfo.InvariantCulture));

    // ----- Studio Voice -----

    private static void StudioVoice(string folder, StringBuilder text, List<string> problems)
    {
        // Left out at a static mix of 0: no effect in the chain, and a track's render the very samples it was without it.
        var left = AudioChainBuilder.From(new TrackAudioFilters { StudioVoice = true, StudioVoiceMix = 0 }, 0);
        Line(text, $"switched on at a mix of 0 with no keys: no effect in the chain");
        if (left.Effects.Length != 0)
            Problem(problems, $"Studio Voice at 0 left {left.Effects.Length} effect(s) in the chain");
        var on = AudioChainBuilder.From(new TrackAudioFilters { StudioVoice = true }, 0);
        Line(text, $"switched on as it comes: one effect, at a mix of 0.85");
        if (on.Effects is not [{ Kind: AudioEffectKind.StudioVoice, P1: 0.85, Keys: null }])
            Problem(problems, $"Studio Voice as it comes is {string.Join(", ", on.Effects)}");

        var noise = Wav(folder, "voice-noise.wav", 3, i => 0.2 * Noise(i));
        var plain = Render(TrackOf(noise, null), 0, 2);
        var withOff = Render(TrackOf(noise, new TrackAudioFilters { StudioVoice = true, StudioVoiceMix = 0 }), 0, 2);
        Line(text, $"a track's render with it at 0: the same samples, bit for bit");
        if (!plain.AsSpan().SequenceEqual(withOff))
            Problem(problems, $"the render with Studio Voice at 0 is not the render without it");

        // A clip's own filters with only Studio Voice on, at 0 with no keys (TrackAudioFilters.Processes false): the clip stays on its track's
        // strip (its gain, then the track's, as without it), so the render with a clip gain and a track gain is the very one without it.
        TimelineDocument Gained(TrackAudioFilters? own)
        {
            var document = TrackOf(noise, null);
            document.Tracks[0].GainDb = -6;
            document.Tracks[0].Clips[0].GainDb = 2;
            document.Tracks[0].Clips[0].State.AudioFilters = own;
            return document;
        }

        var ownAtZero = Gained(new TrackAudioFilters { StudioVoice = true, StudioVoiceMix = 0 });
        var ownStrips = AudioProgram.Build(State(ownAtZero)).Strips;
        Line(text, $"a clip's own Studio Voice on at 0 with no keys: the clip stays on its track's strip, its render (clip +2 dB, track -6 dB) bit for bit the one without it");
        if (ownStrips.Any(s => s.Kind == AudioStripKind.Clip) || !Render(Gained(null), 0, 2).AsSpan().SequenceEqual(Render(ownAtZero, 0, 2)))
            Problem(problems, $"a clip's own Studio Voice at 0 moved it to a strip of its own ({string.Join(", ", ownStrips.Select(s => s.Kind))}) or changed its render");

        // Keyed to 0 all along: the input, exactly Latency samples late (silence before), whatever the blocks.
        var keys = new AudioMixKeys([new Keyframe(0, 0, EasingType.Linear), new Keyframe(10, 0, EasingType.Linear)], 0);
        var keyed = AudioEffect.Create(new AudioEffectSpec(AudioEffectKind.StudioVoice, 0.85) { Keys = keys }, Rate);
        var input = new float[Rate * 2];
        for (var i = 0; i < input.Length; i++)
            input[i] = (float)(0.3 * Noise(i * 7 + 3));
        var output = (float[])input.Clone();
        Run(keyed, output, [480, 333, 1024, 77]);
        var late = StudioVoiceEffect.Latency;
        var exact = true;
        for (var n = 0; n < input.Length / 2 && exact; n++)
        {
            for (var c = 0; c < 2; c++)
                exact &= output[2 * n + c] == (n >= late ? input[2 * (n - late) + c] : 0f);
        }

        Line(text, $"keyed to 0: every sample the input's {late} samples before (silence before them), bit for bit");
        if (!exact)
            Problem(problems, $"Studio Voice keyed to 0 is not the input {late} samples late");

        // At 100 %: speech-like harmonics (150 Hz, 1/k, in syllables) in white noise. The noise in the pauses goes down; the harmonics in the
        // formant band (300-3400 Hz) stay.
        const double seconds = 6, period = 0.35, sounding = 0.2, ramp = 0.02, noiseLevel = 0.01, voiceLevel = 0.158;
        var total = (int)(seconds * Rate);
        var speech = new float[total * 2];
        for (var n = 0; n < total; n++)
        {
            var t = n / (double)Rate;
            var into = t % period;
            var envelope = into >= sounding ? 0 : Math.Min(Math.Min(into / ramp, (sounding - into) / ramp), 1);
            envelope = envelope * envelope * (3 - 2 * envelope);
            var voice = 0.0;
            for (var k = 1; k <= 25; k++)
                voice += Math.Sin(2 * Math.PI * 150 * k * t) / k;
            var sample = (float)(voiceLevel * envelope * voice + noiseLevel * Math.Sqrt(3) * Noise(n));
            (speech[2 * n], speech[2 * n + 1]) = (sample, sample);
        }

        var full = AudioEffect.Create(new AudioEffectSpec(AudioEffectKind.StudioVoice, 1), Rate);
        var cleaned = (float[])speech.Clone();
        Run(full, cleaned, [480]);

        // From 2 s on (the noise floor learnt): each syllable's middle 100 ms (15 whole cycles of 150 Hz), and each pause's middle 100 ms.
        var (harmonicIn, harmonicOut, noiseIn, noiseOut) = (0.0, 0.0, 0.0, 0.0);
        for (var start = Math.Ceiling(2 / period) * period; start + period < seconds; start += period)
        {
            var at = (int)Math.Round((start + 0.05) * Rate);
            for (var k = 2; k <= 22; k++)
            {
                harmonicIn += Goertzel(speech, at, Rate / 10, 150 * k);
                harmonicOut += Goertzel(cleaned, at + late, Rate / 10, 150 * k);
            }

            var gap = (int)Math.Round((start + sounding + 0.025) * Rate);
            noiseIn += Power(speech, gap, Rate / 10);
            noiseOut += Power(cleaned, gap + late, Rate / 10);
        }

        var harmonicDb = 10 * Math.Log10(harmonicOut / harmonicIn);
        var noiseDb = 10 * Math.Log10(noiseOut / noiseIn);
        Line(text, $"at 100 %: the pauses' noise at least 8 dB lower, the 300-3400 Hz harmonics within 2 dB of what they were");
        Note($"studio voice: noise in the pauses {noiseDb:+0.0;-0.0} dB, harmonics {harmonicDb:+0.00;-0.00} dB");
        if (!(noiseDb <= -8))
            Problem(problems, $"the pauses' noise went {noiseDb:0.0} dB, not 8 dB down or more");
        if (!(Math.Abs(harmonicDb) <= 2))
            Problem(problems, $"the harmonics moved {harmonicDb:0.00} dB");
    }

    // Processes interleaved stereo in place, in blocks of the sizes given in turn, each told its moments of the Sequence (from 0) first.
    // ----- Latency (5.2C) -----

    private static void Latency(string folder, StringBuilder text, List<string> problems)
    {
        // The limiter as the Audio tab makes it (5 ms attack, 50 ms release): its lookahead is counted since 5.2D (the user's
        // answer: no filter drifts), at the mixer's own rate.
        var limiter = new AudioEffectSpec(AudioEffectKind.Limiter, 0.9, 5, 50, 1, 1);
        var late = AudioEffect.LatencyOf(new AudioEffectSpec(AudioEffectKind.Denoise, 12), Rate);
        var chain = AudioEffect.LatencyOf([new AudioEffectSpec(AudioEffectKind.Denoise, 12), new AudioEffectSpec(AudioEffectKind.StudioVoice, 1), limiter], Rate);
        var (limited, limited44, limited96) = (AudioEffect.LatencyOf(limiter, Rate), AudioEffect.LatencyOf(limiter, 44100), AudioEffect.LatencyOf(limiter, 96000));
        Line(text, $"Noise Reduction and Studio Voice 512 samples late each, the limiter (5 ms) {limited} at 48 kHz, {limited44} at 44.1 kHz, {limited96} at 96 kHz: a chain of the three {chain}");
        if (late != 512 || chain != 1024 + 239 || limited != 239 || limited44 != 219 || limited96 != 479
            || AudioEffect.LatencyOf(new AudioEffectSpec(AudioEffectKind.StudioVoice, 1), Rate) != StudioVoiceEffect.Latency)
            Problem(problems, $"the latencies are {late} (Noise Reduction), {limited} / {limited44} / {limited96} (the limiter) and {chain} (the chain of three)");

        // Two clips cut together (the second from further into the file), each with its own Studio Voice keyed to 0 all along: the effect is its
        // input exactly 512 samples late, so read that far ahead the render is the plain one, bit for bit, at the start, at the cut and at the end.
        var noise = Wav(folder, "latency-noise.wav", 8, i => 0.2 * Noise(i * 3 + 1));
        TimelineDocument Cut(Func<TrackAudioFilters?> own)
        {
            var document = TrackOf(noise, null);
            var track = document.Tracks[0];
            (track.Clips[0].Start, track.Clips[0].End, track.Clips[0].State.AudioFilters) = (0, 1, own());
            track.Clips.Add(new TimelineClip
            {
                ContentKind = LayerKind.Audio, SourcePath = noise, AudioStreamIndex = 0, Start = 1, End = 2, MediaOffset = 4, SourceDuration = 120,
                State = new LayerState { Kind = LayerKind.Audio, Name = "second", ImagePath = noise, MediaDuration = 120, HasAudio = true, AudioFilters = own() },
            });
            return document;
        }

        TrackAudioFilters Keyed() => new() { StudioVoice = true, StudioVoiceMix = 0, StudioVoiceMixKeys = [new Keyframe(0, 0, EasingType.Linear), new Keyframe(10, 0, EasingType.Linear)] };
        var voiced = Cut(Keyed);
        Line(text, $"two clips cut at 1 s, each with its own Studio Voice keyed to 0: each on a strip of its own, the render from 0 the plain one bit for bit");
        var strips = AudioProgram.Build(State(voiced)).Strips;
        if (strips.Count(s => s.Kind == AudioStripKind.Clip) != 2)
            Problem(problems, $"the clips are on {string.Join(", ", strips.Select(s => s.Kind))}, not two strips of their own");
        CompareExactly(Render(Cut(() => null), 0, 2.2), Render(voiced, 0, 2.2), "from 0", problems);
        Line(text, $"the render from 0.5 s (a range's export) and from 0.99 s (the cut 10 ms in): the plain ones bit for bit");
        CompareExactly(Render(Cut(() => null), 0.5, 1.0), Render(voiced, 0.5, 1.0), "from 0.5 s", problems);
        CompareExactly(Render(Cut(() => null), 0.99, 0.5), Render(voiced, 0.99, 0.5), "from 0.99 s", problems);

        // A track's Noise Reduction (the Audio tab's): its sound on the plain one's clock (lag 0, as FFmpeg's afftdn never was: 512 late).
        var plain = Render(TrackOf(noise, null), 0, 2);
        var cleaned = Render(TrackOf(noise, new TrackAudioFilters { DeNoise = true }), 0, 2);
        var (lag, correlation) = BestLag(plain, cleaned, Rate / 2, Rate, 1024);
        Line(text, $"a track's Noise Reduction: lag 0 against the plain render");
        Note($"latency: a track's Noise Reduction lag {lag} samples, correlation {correlation:0.0000}");
        if (lag != 0 || correlation < 0.9)
            Problem(problems, $"a track's Noise Reduction is {lag} samples from the plain render (correlation {correlation:0.000})");

        // A clip's own Noise Reduction after a cut: its first 10 ms carry its sound (until 5.2C they were the effect's 512 samples of silence),
        // and the whole clip is on the plain one's clock.
        var own = Render(Cut(() => new TrackAudioFilters { DeNoise = true }), 0, 2.2);
        var plainCut = Render(Cut(() => null), 0, 2.2);
        var firstDb = 10 * Math.Log10(Power(own, Rate, Rate / 100) / Power(plainCut, Rate, Rate / 100));
        var (cutLag, cutCorrelation) = BestLag(plainCut, own, Rate + Rate / 4, Rate / 2, 1024);
        Line(text, $"a clip's own Noise Reduction after the cut: its first 10 ms within 3 dB of the plain sound, the clip at lag 0");
        Note($"latency: the second clip's first 10 ms {firstDb:0.00} dB, lag {cutLag}, correlation {cutCorrelation:0.0000}");
        if (!(Math.Abs(firstDb) <= 3) || cutLag != 0 || cutCorrelation < 0.9)
            Problem(problems, $"the second clip's first 10 ms are {firstDb:0.0} dB from the plain sound, its lag {cutLag} (correlation {cutCorrelation:0.000})");

        // A limiter whose ceiling the sound never reaches (-6 dB over noise that peaks under -13 dB) only delays it, as af_alimiter does:
        // read ahead by that delay, a track's and a clip's own render as the plain ones, bit for bit (until this pass: 239 samples late).
        Line(text, $"a limiter at -6 dB the sound never reaches, a track's and each clip's own: the render from 0 and from 0.5 s the plain one bit for bit");
        var ceiling = new TrackAudioFilters { Limiter = true, LimiterCeiling = -6 };
        CompareExactly(Render(TrackOf(noise, null), 0, 2), Render(TrackOf(noise, ceiling), 0, 2), "a track's limiter from 0", problems);
        CompareExactly(Render(TrackOf(noise, null), 0.5, 1), Render(TrackOf(noise, ceiling), 0.5, 1), "a track's limiter from 0.5 s", problems);
        CompareExactly(plainCut, Render(Cut(() => new TrackAudioFilters { Limiter = true, LimiterCeiling = -6 }), 0, 2.2), "each clip's own limiter from 0", problems);
    }

    private static void CompareExactly(float[] expected, float[] actual, string what, List<string> problems)
    {
        var first = -1;
        for (var i = 0; i < expected.Length && first < 0; i++)
        {
            if (expected[i] != actual[i])
                first = i / 2;
        }

        if (first >= 0)
        {
            var (lag, _) = BestLag(expected, actual, Math.Max(first, 600), Rate / 10, 1024);
            Problem(problems, $"the render {what} differs from the plain one from frame {first} ({first / (double)Rate:0.0000} s; there {lag} samples late)");
        }
    }

    // The lag (samples, positive: b late) at which b's left channel best matches a's over a window, within +/- most, and its correlation there.
    private static (int Lag, double Correlation) BestLag(float[] a, float[] b, int from, int length, int most)
    {
        var (best, bestLag) = (double.NegativeInfinity, 0);
        double energyA = 0;
        for (var n = from; n < from + length; n++)
            energyA += a[2 * n] * (double)a[2 * n];
        var correlation = 0.0;
        for (var lag = -most; lag <= most; lag++)
        {
            double sum = 0, energyB = 0;
            for (var n = from; n < from + length; n++)
            {
                var m = n + lag;
                if (m < 0 || 2 * m >= b.Length)
                    continue;
                sum += a[2 * n] * (double)b[2 * m];
                energyB += b[2 * m] * (double)b[2 * m];
            }

            if (sum > best)
                (best, bestLag, correlation) = (sum, lag, sum / Math.Sqrt(Math.Max(energyA * energyB, 1e-30)));
        }

        return (bestLag, correlation);
    }

    private static void Run(AudioEffect effect, float[] stereo, int[] blocks)
    {
        var frames = stereo.Length / 2;
        for (int done = 0, b = 0; done < frames; b++)
        {
            var count = Math.Min(blocks[b % blocks.Length], frames - done);
            effect.Moment(done / (double)Rate, (done + count) / (double)Rate);
            effect.Process(stereo.AsSpan(done * 2, count * 2), count);
            done += count;
        }
    }

    // The power of one frequency over a stretch of the left side (an exact Goertzel; a whole number of cycles in the stretch).
    private static double Goertzel(float[] stereo, int from, int length, double hz)
    {
        var (re, im) = (0.0, 0.0);
        for (var n = 0; n < length; n++)
        {
            var phase = 2 * Math.PI * hz * n / Rate;
            re += stereo[2 * (from + n)] * Math.Cos(phase);
            im -= stereo[2 * (from + n)] * Math.Sin(phase);
        }

        return (re * re + im * im) / ((double)length * length);
    }

    // The mean square of a stretch of the left side.
    private static double Power(float[] stereo, int from, int length)
    {
        var sum = 0.0;
        for (var n = 0; n < length; n++)
            sum += stereo[2 * (from + n)] * (double)stereo[2 * (from + n)];
        return sum / length;
    }

    // ----- The De-Esser -----

    private static void DeEsser(StringBuilder text, List<string> problems)
    {
        static float[] Tone(double hz, double amplitude, double seconds)
        {
            var frames = (int)(seconds * Rate);
            var stereo = new float[frames * 2];
            for (var n = 0; n < frames; n++)
            {
                var fade = Math.Min(n / (0.01 * Rate), 1);
                var sample = (float)(amplitude * fade * Math.Sin(2 * Math.PI * hz * n / Rate));
                (stereo[2 * n], stereo[2 * n + 1]) = (sample, sample);
            }

            return stereo;
        }

        var spec = new AudioEffectSpec(AudioEffectKind.DeEsser, 0.5);
        var burst = Tone(7000, 0.5, 0.6);
        var taken = (float[])burst.Clone();
        Run(AudioEffect.Create(spec, Rate), taken, [480]);
        var burstDb = 10 * Math.Log10(Power(taken, (int)(0.2 * Rate), (int)(0.3 * Rate)) / Power(burst, (int)(0.2 * Rate), (int)(0.3 * Rate)));
        Line(text, $"a 7 kHz burst at -6 dBFS, amount 50 %: at least 6 dB down");
        Note($"de-esser: 7 kHz {burstDb:+0.0;-0.0} dB");
        if (!(burstDb <= -6))
            Problem(problems, $"the 7 kHz burst went {burstDb:0.0} dB, not 6 dB down or more");

        var tone = Tone(1000, 0.5, 0.6);
        var kept = (float[])tone.Clone();
        Run(AudioEffect.Create(spec, Rate), kept, [480]);
        var toneDb = 10 * Math.Log10(Power(kept, (int)(0.2 * Rate), (int)(0.3 * Rate)) / Power(tone, (int)(0.2 * Rate), (int)(0.3 * Rate)));
        Line(text, $"a 1 kHz tone at -6 dBFS: within 0.1 dB of what it was");
        Note($"de-esser: 1 kHz {toneDb:+0.000;-0.000} dB, {(kept.AsSpan().SequenceEqual(tone) ? "bit for bit" : "changed")}");
        if (!(Math.Abs(toneDb) <= 0.1))
            Problem(problems, $"the 1 kHz tone moved {toneDb:0.000} dB");

        var none = AudioChainBuilder.From(new TrackAudioFilters { DeEsser = false, DeEsserAmount = 80 }, 0);
        Line(text, $"switched off: not in the chain");
        if (none.Effects.Length != 0)
            Problem(problems, $"the De-Esser switched off left {none.Effects.Length} effect(s)");
    }

    // ----- The parametric equalizer -----

    private static void Equalizer(StringBuilder text, List<string> problems)
    {
        // Nothing moved: each band at its octave's centre, an octave wide, as before.
        var octave = new TrackAudioFilters { Equalizer = true };
        for (var band = 0; band < 10; band++)
            octave.EqualizerGains[band] = band % 3 == 0 ? 4 : 0;
        var bands = AudioChainBuilder.From(octave, 0).Effects;
        var expected = Enumerable.Range(0, 10).Where(b => b % 3 == 0).Select(b => (double)TrackAudioFilters.EqualizerBands[b]).ToList();
        Line(text, $"no band moved: {bands.Length} peaking bands at {string.Join(", ", bands.Select(b => b.P1.ToString("0", CultureInfo.InvariantCulture)))} Hz, an octave wide");
        if (bands.Length != expected.Count || bands.Where((b, i) => b.Kind != AudioEffectKind.Peaking || b.P1 != expected[i] || b.P2 != 1).Any())
            Problem(problems, $"the octave bands are {string.Join(", ", bands)}");

        // Moved and widened, with the high-pass and the shelves on: the curve against a sine through the chain's own biquads.
        var filters = new TrackAudioFilters
        {
            Equalizer = true, HighPass = true, HighPassHz = 60, BassTreble = true, BassGain = 3, TrebleGain = -4,
            EqualizerFrequencies = [31, 62, 125, 250, 500, 1500, 2000, 4000, 8000, 16000],
            EqualizerWidths = [1, 1, 1, 1, 1, 0.5, 1, 1, 2, 1],
        };
        (filters.EqualizerGains[2], filters.EqualizerGains[5], filters.EqualizerGains[8]) = (6, -8, 4);
        var chain = AudioChainBuilder.From(filters, 0).Effects;
        var moved = chain.FirstOrDefault(e => e.Kind == AudioEffectKind.Peaking && e.P4 == -8);
        Line(text, $"band 6 moved to 1500 Hz, half an octave wide: in the chain as such");
        if (moved.P1 != 1500 || moved.P2 != 0.5)
            Problem(problems, $"the moved band is {moved}");

        var worst = 0.0;
        foreach (var hz in new[] { 50, 125, 700, 1500, 3000, 8000, 12000 })
        {
            var curve = AudioChainBuilder.EqualizerResponseDb(filters, hz);
            var stereo = new float[Rate * 2 * 2];
            for (var n = 0; n < Rate * 2; n++)
                stereo[2 * n] = stereo[2 * n + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * hz * n / Rate));
            var heard = (float[])stereo.Clone();
            foreach (var spec in chain)
                Run(AudioEffect.Create(spec, Rate), heard, [480]);
            var measured = 10 * Math.Log10(Power(heard, Rate, Rate) / Power(stereo, Rate, Rate));
            worst = Math.Max(worst, Math.Abs(measured - curve));
            Line(text, $"{hz} Hz: the curve within 0.05 dB of the biquads");
            Note($"equalizer {hz} Hz: curve {curve:+0.000;-0.000} dB, biquads {measured:+0.000;-0.000} dB");
            if (Math.Abs(measured - curve) > 0.05)
                Problem(problems, $"at {hz} Hz the curve says {curve:0.000} dB, the biquads give {measured:0.000} dB");
        }

        Note($"equalizer: worst difference {worst:0.0000} dB");
        Line(text, $"nothing on: a flat curve");
        if (AudioChainBuilder.EqualizerResponseDb(new TrackAudioFilters(), 1000) != 0)
            Problem(problems, $"a track with nothing on has a curve");
    }

    // ----- Beats -----

    private static void Beats(StringBuilder text, List<string> problems)
    {
        const int rate = BeatDetector.AnalysisRate;
        const double seconds = 60, first = 0.25, beat = 0.5;
        var mono = new float[(int)(seconds * rate)];
        var seed = 0x1234567u;
        for (var i = 0; i < mono.Length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            mono[i] = (float)(0.02 * Math.Sqrt(3) * ((seed >> 8) / (double)(1 << 24) * 2 - 1));
        }

        // A kick on every beat: a 55 Hz body falling away over 80 ms, a click over its first milliseconds.
        var beats = new List<double>();
        for (var t = first; t < seconds - 0.5; t += beat)
        {
            beats.Add(t);
            var at = (int)Math.Round(t * rate);
            for (var n = 0; n < (int)(0.45 * rate) && at + n < mono.Length; n++)
            {
                var tau = n / (double)rate;
                seed = seed * 1664525 + 1013904223;
                var click = (seed >> 8) / (double)(1 << 24) * 2 - 1;
                mono[at + n] += (float)(0.8 * Math.Sin(2 * Math.PI * 55 * tau) * Math.Exp(-tau / 0.08) + 0.5 * click * Math.Exp(-tau / 0.002));
            }
        }

        var found = BeatDetector.Detect(mono, rate);
        Line(text, $"{beats.Count} kicks at 120 BPM from {first:0.00} s in noise at -34 dBFS: {beats.Count} beats found, each within 15 ms");
        if (found.Length != beats.Count)
            Problem(problems, $"{found.Length} beats found, not {beats.Count}");
        var worst = 0.0;
        foreach (var t in beats)
        {
            var nearest = found.Length == 0 ? double.PositiveInfinity : found.Min(f => Math.Abs(f - t));
            worst = Math.Max(worst, nearest);
            if (nearest > 0.015)
                Problem(problems, $"the beat at {t:0.000} s is {nearest * 1000:0.0} ms from the nearest found");
        }

        Note($"beats: {found.Length} found, worst {worst * 1000:0.0} ms off");

        // A minute in under 15 ms: the best of five, the code warmed first.
        BeatDetector.Detect(mono.AsSpan(0, 5 * rate), rate);
        var best = double.MaxValue;
        for (var round = 0; round < 5; round++)
        {
            var clock = Stopwatch.StartNew();
            BeatDetector.Detect(mono, rate);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        // The golden tests run a debug build, whose code the JIT leaves unoptimized (three to four times slower here): 15 ms is held where
        // HandPeg's code is optimized (a release build, and the app as published), a debug build to 100 ms (a regression of the method, not of the JIT); the time is shown either way.
        var optimized = typeof(BeatDetector).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];
        var budget = optimized ? 15 : 100;
        Line(text, $"a minute of 22050 Hz mono in under 15 ms where the code is optimized (the best of five; a debug build's held to 100 ms)");
        Note($"beats: a minute in {best:0.00} ms ({(optimized ? "optimized" : "a debug build")})");
        if (best >= budget)
            Problem(problems, $"a minute took {best:0.00} ms ({(optimized ? "optimized" : "a debug build")}, over {budget} ms)");

        Line(text, $"silence: no beats");
        if (BeatDetector.Detect(new float[rate * 3], rate).Length != 0)
            Problem(problems, $"silence has beats");
    }

    // ----- The live meter -----

    private static void Loudness(StringBuilder text, List<string> problems)
    {
        // Both sides 1 kHz at 10^(-23/20): -23 LUFS (BS.1770's K-weighting is calibrated to give a sine near 1 kHz its own level).
        var meter = new LiveLoudness(Rate);
        var amplitude = Math.Pow(10, -23 / 20.0);
        var block = new float[480 * 2];
        for (var b = 0; b < 5 * Rate / 480; b++)
        {
            for (var n = 0; n < 480; n++)
                block[2 * n] = block[2 * n + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * (b * 480 + n) / Rate));
            meter.Add(block, 480);
        }

        Line(text, $"a -23 LUFS 1 kHz sine for 5 s: momentary, short-term and integrated within 0.5 LU of -23");
        Note($"loudness: momentary {meter.MomentaryLufs:0.00}, short-term {meter.ShortTermLufs:0.00}, integrated {meter.IntegratedLufs:0.00} LUFS");
        foreach (var (name, value) in new[] { ("momentary", meter.MomentaryLufs), ("short-term", meter.ShortTermLufs), ("integrated", meter.IntegratedLufs) })
        {
            if (!(Math.Abs(value + 23) <= 0.5))
                Problem(problems, $"the {name} loudness is {value:0.00} LUFS");
        }

        // A quarter of the rate at 45 degrees: every sample at 0.707 of the crest, the crest itself between them. Faded in over 10 ms (a sudden
        // start rings), and measured from half a second on.
        var peaks = new LiveLoudness(Rate);
        var (peak, clipped) = (0f, false);
        for (var b = 0; b < Rate / 480; b++)
        {
            if (b == Rate / 960)
                peaks.TakePeak();
            for (var n = 0; n < 480; n++)
            {
                var at = b * 480 + n;
                var fade = Math.Min(at / (0.01 * Rate), 1);
                block[2 * n] = block[2 * n + 1] = (float)(0.5 * fade * Math.Sin(Math.PI / 2 * at + Math.PI / 4));
            }

            peaks.Add(block, 480);
        }

        (peak, clipped) = peaks.TakePeak();
        var peakDb = 20 * Math.Log10(peak);
        Line(text, $"12 kHz at 45 degrees, samples at -9.03 dBFS: the true peak within 0.2 dB of -6.02 dBTP, not clipped");
        Note($"true peak {peakDb:0.000} dBTP");
        if (!(Math.Abs(peakDb + 6.0206) <= 0.2) || clipped)
            Problem(problems, $"the true peak is {peakDb:0.000} dBTP{(clipped ? ", clipped" : "")}");
        Line(text, $"and held over the last 3 s");
        if (!(Math.Abs(peaks.HeldPeak - peak) < 0.01))
            Problem(problems, $"the held peak is {peaks.HeldPeak:0.0000}, not {peak:0.0000}");

        peaks.Restart();
        Line(text, $"restarted (a seek): the loudness begins afresh");
        if (!float.IsNegativeInfinity(peaks.IntegratedLufs) || !float.IsNegativeInfinity(peaks.MomentaryLufs))
            Problem(problems, $"after a restart the loudness is {peaks.MomentaryLufs:0.00} / {peaks.IntegratedLufs:0.00} LUFS");
    }

    // ----- The master gain -----

    private static void MasterGain(string folder, StringBuilder text, List<string> problems)
    {
        var level = Wav(folder, "master.wav", 3, i => 0.25 * Math.Sin(2 * Math.PI * 440 * i / Rate) + 0.05 * Noise(i));
        var document = TrackOf(level, null);
        var plain = Render(document, 0, 2);

        document.MasterGainDb = 6;
        var louder = AudioProgram.Build(State(document));
        var factor = (float)Math.Pow(10, 6 / 20.0);
        var heard = Render(document, 0, 2);
        var exact = louder.MasterGain == factor;
        for (var i = 0; i < plain.Length && exact; i++)
            exact = heard[i] == plain[i] * factor;
        Line(text, $"+6 dB: every sample the plain one times 10^(6/20) = 1.9953, to the bit");
        if (!exact)
            Problem(problems, $"+6 dB is not every sample times {factor} (the program's factor {louder.MasterGain})");

        document.MasterGainDb = 0.004;
        Line(text, $"0 dB (within a hundredth of a decibel): a factor of 1, the very samples");
        if (AudioProgram.Build(State(document)).MasterGain != 1f || !Render(document, 0, 2).AsSpan().SequenceEqual(plain))
            Problem(problems, $"0 dB changed the sound");

        document.MasterGainDb = 50;
        Line(text, $"held to +12 dB");
        if (document.MasterGainDb != 12)
            Problem(problems, $"50 dB was held to {document.MasterGainDb} dB");
    }

    // ----- The model -----

    private static void Model(StringBuilder text, List<string> problems)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        var filters = new TrackAudioFilters();
        Expect(!filters.IsActive && filters.ActiveCount == 0 && !filters.StudioVoice && filters.StudioVoiceMix == 85 && !filters.DeEsser && filters.DeEsserAmount == 50
               && filters.StudioVoiceMixKeys is null && filters.EqualizerFrequencies is null && filters.EqualizerWidths is null, "new filters: Studio Voice and the De-Esser off, the mix 85 %, the amount 50 %, nothing moved");
        filters.StudioVoice = filters.DeEsser = true;
        Expect(filters.IsActive && filters.ActiveCount == 2, "switched on, both count");
        filters.StudioVoiceMixKeys = [new Keyframe(0, 20)];
        filters.EqualizerFrequencies = [100, 200, 300, 400, 500, 600, 700, 800, 900, 1000];
        filters.EqualizerWidths = [1, 1, 1, 1, 1, 1, 1, 1, 1, 2];
        var copy = filters.Clone();
        copy.StudioVoiceMixKeys![0] = new Keyframe(0, 90);
        copy.EqualizerFrequencies![0] = 50;
        copy.EqualizerWidths![9] = 4;
        Expect(filters.StudioVoiceMixKeys![0].Value == 20 && filters.EqualizerFrequencies![0] == 100 && filters.EqualizerWidths![9] == 2, "a copy's keys and bands are its own");
        var written = System.Text.Json.JsonSerializer.Serialize(new TrackAudioFilters());
        Expect(!written.Contains("StudioVoiceMixKeys", StringComparison.Ordinal) && !written.Contains("EqualizerFrequencies", StringComparison.Ordinal)
               && !written.Contains("EqualizerWidths", StringComparison.Ordinal), "keys and moved bands left out of the file while there are none");

        var document = new TimelineDocument { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, TrackNumber = 1 };
        var clip = new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/beat.wav", AudioStreamIndex = 0, Start = 0, End = 4, SourceDuration = 10 };
        track.Clips.Add(clip);
        document.Tracks.Add(track);
        var commits = new List<TimelineCommit>();
        document.Committed += commits.Add;

        using (var edit = document.BeginTransaction("Master Gain"))
        {
            edit.SetMasterGain(-3.5);
            edit.Commit();
        }

        Expect(document.MasterGainDb == -3.5 && commits.Count == 1 && commits[0].TouchedClipIds.Count == 0, "the master gain is a step: set, and committed with no clip touched");
        Expect(document.Clone().MasterGainDb == -3.5, "a copy of the Sequence (an undo step's) has it");
        using (var edit = document.BeginTransaction("Master Gain"))
            edit.SetMasterGain(9);
        Expect(document.MasterGainDb == -3.5 && commits.Count == 1, "rolled back, it is as it was, and nothing was committed");
        using (var edit = document.BeginTransaction("Master Gain"))
        {
            edit.SetMasterGain(-3.5);
            edit.Commit();
        }

        Expect(commits.Count == 1, "the same gain again is no change");

        using (var edit = document.BeginTransaction("Detect Beats"))
        {
            edit.SetClipBeats(clip, [2.5, 0.5, double.NaN, 1.0, 1.0000001, -1]);
            edit.Commit();
        }

        Expect(clip.BeatTimes is [0, 0.5, 1.0, 2.5], "the beats are set in order, finite, from 0, a microsecond apart");
        var before = clip.BeatTimes;
        using (var edit = document.BeginTransaction("Clear Beats"))
            edit.SetClipBeats(clip, null);
        Expect(ReferenceEquals(clip.BeatTimes, before), "a rollback puts the beats back");
        using (var edit = document.BeginTransaction("Clear Beats"))
        {
            edit.SetClipBeats(clip, null);
            edit.Commit();
        }

        Expect(clip.BeatTimes is null && commits.Count == 3, "cleared, they are none, a step of their own");

        // Studio Voice's mix keys on the clip's clock (they count from its start, as its motion does): a split keeps each half's automation on
        // its sound, and a start trim keeps every key on the same moment of it.
        var voiceTrack = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, TrackNumber = 2 };
        var voiced = new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/voice.wav", AudioStreamIndex = 0, Start = 10, End = 20, MediaOffset = 5, SourceDuration = 30 };
        voiced.State.AudioFilters = new TrackAudioFilters
        {
            StudioVoice = true,
            StudioVoiceMixKeys = [new Keyframe(2, 0, EasingType.Linear), new Keyframe(8, 100, EasingType.Linear)],
        };
        voiceTrack.Clips.Add(voiced);
        document.Tracks.Add(voiceTrack);
        static double MixAt(TimelineClip on, double sequenceSeconds) => EasingCurves.ValueAt(on.State.AudioFilters!.StudioVoiceMixKeys!, sequenceSeconds - on.Start);
        double[] moments = [10.5, 12, 13.5, 14.9, 15.1, 16, 17.5, 18, 19.5];
        var heard = moments.Select(m => MixAt(voiced, m)).ToArray();

        TimelineClip later;
        using (var edit = document.BeginTransaction("Split"))
        {
            later = edit.SplitClipAt(voiced, 15);
            edit.Commit();
        }

        var halves = moments.Select(m => MixAt(m < 15 ? voiced : later, m)).ToArray();
        Expect(!ReferenceEquals(voiced.State.AudioFilters!.StudioVoiceMixKeys, later.State.AudioFilters!.StudioVoiceMixKeys)
               && halves.Zip(heard).All(p => Math.Abs(p.First - p.Second) < 1e-9) && later.State.AudioFilters!.StudioVoiceMixKeys![0].Time == 0,
            "split at its middle: each half has keys of its own, the mix at every moment of the Sequence as it was");

        var keysBefore = later.State.AudioFilters!.StudioVoiceMixKeys!.Select(k => k.Time).ToArray();
        var heardLater = moments.Where(m => m >= 16).Select(m => MixAt(later, m)).ToArray();
        using (var edit = document.BeginTransaction("Trim"))
        {
            edit.TrimClipStart(later, 16);
            edit.Commit();
        }

        var keysAfter = later.State.AudioFilters!.StudioVoiceMixKeys!.Select(k => k.Time).ToArray();
        Expect(Math.Abs(later.Start - 16) < 1e-9 && keysAfter.Length == keysBefore.Length && keysAfter.Zip(keysBefore).All(p => Math.Abs(p.First - (p.Second - 1)) < 1e-6)
               && moments.Where(m => m >= 16).Select(m => MixAt(later, m)).Zip(heardLater).All(p => Math.Abs(p.First - p.Second) < 1e-9),
            "its start trimmed 1 s later: every key 1 s earlier on the clip's own clock, the mix at every moment it still plays as it was");

        var settings = new AppSettings();
        Expect(settings.SnapToBeats && settings.ShowBeatGrid, "the settings snap to beats and show the beat grid as they come");
    }

    // ----- The audio-reactive shader bus -----

    private static void Reactive(string folder, StringBuilder text, List<string> problems)
    {
        // The three bands of three tones at -12 dBFS: each its own band, the others quiet.
        foreach (var (hz, own) in new[] { (60.0, 0), (700.0, 1), (5000.0, 2) })
        {
            var follower = new AudioBandFollower(Rate);
            var stereo = new float[Rate * 2];
            for (var n = 0; n < Rate; n++)
                stereo[2 * n] = stereo[2 * n + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * hz * n / Rate));
            for (var at = 0; at < Rate; at += 480)
                follower.Process(stereo.AsSpan(at * 2, 480 * 2), 480);
            float[] bands = [follower.Bass, follower.Mid, follower.High];
            Line(text, $"{hz:0} Hz: {(own == 0 ? "Bass" : own == 1 ? "Mid" : "High")} at 0.8 or more, the other two at 0.1 or less");
            Note($"bands of {hz:0} Hz: {bands[0]:0.000} / {bands[1]:0.000} / {bands[2]:0.000}");
            for (var b = 0; b < 3; b++)
            {
                if (b == own ? bands[b] < 0.8f : bands[b] > 0.1f)
                    Problem(problems, $"{hz} Hz: band {b} reads {bands[b]:0.000}");
            }
        }

        // The cards: the pulse and its band in the lanes after the effect's own.
        var glow = EffectRegistry.Find(EffectRegistry.Glow)!.NewEffect();
        glow.Set(AudioReactive.PulseKey, 100);
        var (first, second) = (glow.EvaluateGpuPacket(0, null, 0), glow.EvaluateGpuPacket(0, null, 1));
        Line(text, $"a Glow card at 100 %: its own eight lanes as before, the pulse and Bass in a second packet");
        if (first.Slot != GpuShaderSlot.HalationGlow || second is not { Slot: GpuShaderSlot.HalationGlow, P0: 100, P1: 0 })
            Problem(problems, $"the Glow's packets are {first} and {second}");

        // The packets a card sends, as the scene plan adds them (GpuScenePlan.AddCard: in order, up to the first of none).
        static List<GpuEffectPacket> Sent(EffectInstance effect)
        {
            var sent = new List<GpuEffectPacket>();
            for (var k = 0; k < effect.Definition!.GpuPacketCount; k++)
            {
                var packet = effect.EvaluateGpuPacket(0.1, null, k);
                if (packet.Slot == GpuShaderSlot.None)
                    break;
                sent.Add(packet);
            }

            return sent;
        }

        // At its default pulse a card sends the one packet it sent before the pulse (its passes and the layer's sixteen as they were); with a
        // pulse set or keyed above 0, a second, marked as the pulse's own (its last lane GpuEffectPacket.PulseMarker), which no first packet is.
        foreach (var type in (string[])[EffectRegistry.Glow, EffectRegistry.NeonRim, EffectRegistry.Shockwave])
        {
            var definition = EffectRegistry.Find(type)!;
            var pulseCard = definition.NewEffect();
            var plain = Sent(pulseCard);
            pulseCard.SetKeys(AudioReactive.PulseKey, [new Keyframe(0, 0, EasingType.Linear), new Keyframe(1, 0, EasingType.Linear)]);
            var keyedFlat = Sent(pulseCard);
            pulseCard.SetKeys(AudioReactive.PulseKey, [new Keyframe(0, 0, EasingType.Linear), new Keyframe(1, 40, EasingType.Linear)]);
            var keyed = Sent(pulseCard);
            pulseCard.SetKeys(AudioReactive.PulseKey, null);
            pulseCard.Set(AudioReactive.PulseKey, 25);
            var pulsed = Sent(pulseCard);
            Line(text, $"{definition.Name}: one packet at its default pulse (and keyed to 0 throughout), its first not marked; two when keyed above 0 or set to 25 %, the second marked as the pulse's");
            if (plain is not [var one] || GpuEffectPacket.IsPulsePacket(one) || keyedFlat.Count != 1
                || keyed is not [_, var keyedPulse] || !GpuEffectPacket.IsPulsePacket(keyedPulse)
                || pulsed is not [var own, { P0: 25 } pulse] || GpuEffectPacket.IsPulsePacket(own) || !GpuEffectPacket.IsPulsePacket(pulse) || pulse.Slot != own.Slot)
            {
                Problem(problems, $"{definition.Name} sends {plain.Count} / {keyedFlat.Count} / {keyed.Count} / {pulsed.Count} packets: {string.Join(", ", pulsed)}");
            }
        }
        foreach (var type in AudioReactive.Effects)
        {
            var definition = EffectRegistry.Find(type)!;
            Line(text, $"{definition.Name}: an Audio Reactive Pulse of 0 to 100 %, 0 as it comes, and a Pulse Band of Bass, Mid, High or All, Bass as it comes");
            if (definition.Param(AudioReactive.PulseKey) is not { Min: 0, Max: 100, Default: 0 } || definition.Param(AudioReactive.BandKey) is not { Kind: ParamKind.Choice, TextDefault: "bass", Choices.Count: 4 })
                Problem(problems, $"{definition.Name} has no pulse as the contract has it");
        }

        // On a real worker: a pulse of 0 at full energy is the Glow as it was; at 100 % a beat lifts it, silence does not.
        using (var client = new CompositorClient(adapter: -1, keepStandby: true))
        using (var reader = new CompositorFrameReader(client))
        {
            byte[]? Frame(float bass, params GpuEffectPacket[] packets)
            {
                client.ClearScene();
                ref var scene = ref client.Scene;
                (scene.CanvasWidth, scene.CanvasHeight, scene.Flags) = (256, 144, FrameSceneHeader.FlagRawOutput | FrameSceneHeader.FlagExactFrames);
                scene.AudioBass = bass;
                ref var layer = ref client.Layer(0);
                layer.SourceKind = (int)GpuSourceKind.TestPattern;
                (layer.X, layer.Y, layer.Width, layer.Height) = (0, 0, 256, 144);
                (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
                (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
                (layer.CropRight, layer.CropBottom) = (1, 1);
                layer.Flags = GpuLayerDescriptor.FlagVisible;
                foreach (var packet in packets)
                    layer.Effects[layer.EffectCount++] = packet;
                scene.ActiveLayerCount = 1;
                var sequence = client.Publish();
                return client.WaitForFrame(sequence, 10_000) && reader.TryRead(out var bgra, out _, out _) ? bgra : null;
            }

            // The Glow's own packet, and its pulse's (marked, as EvaluateGpuPacket sends it).
            var glowPacket = new GpuEffectPacket(GpuShaderSlot.HalationGlow, 1.2f, 0.5f, 5, 0.6f, 1, 0.29f, 0.11f, 100);
            var pulseOf = (float percent) => new GpuEffectPacket(GpuShaderSlot.HalationGlow, percent, 0, 0, 0, 0, 0, 0, GpuEffectPacket.PulseMarker);
            var asItWas = Frame(0, glowPacket);
            var zeroPulse = Frame(1, glowPacket, pulseOf(0));
            var beat = Frame(1, glowPacket, pulseOf(100));
            var silence = Frame(0, glowPacket, pulseOf(100));

            // Two Glow cards side by side at their default pulse (each one packet, as before the pulse) on a full beat: each is drawn, the very
            // frame of the two drawn apart (an opacity of 100 % between them, which draws nothing), and not the one Glow's.
            var opaque = new GpuEffectPacket(GpuShaderSlot.TransformComposite, 100, 0, 0, 0, 0, 0, 0, 0);
            var sideBySide = Frame(1, glowPacket, glowPacket);
            var apart = Frame(1, glowPacket, opaque, glowPacket);
            if (asItWas is null || zeroPulse is null || beat is null || silence is null || sideBySide is null || apart is null)
            {
                Problem(problems, $"the worker drew no frame ({client.State}: {client.Message})");
            }
            else
            {
                Line(text, $"two Glow cards side by side at a pulse of 0, on a full beat: both drawn, the frame of the two drawn apart, not the one Glow's");
                if (!sideBySide.AsSpan().SequenceEqual(apart) || sideBySide.AsSpan().SequenceEqual(asItWas))
                    Problem(problems, $"two Glows side by side are not drawn as two ({(sideBySide.AsSpan().SequenceEqual(asItWas) ? "one was taken as the other's pulse" : "not the frame of the two apart")})");

                Line(text, $"pulse 0 at full energy: the very frame the Glow draws without a pulse");
                if (!zeroPulse.AsSpan().SequenceEqual(asItWas))
                    Problem(problems, $"a pulse of 0 changed the frame");
                Line(text, $"pulse 100 % in silence: the same frame");
                if (!silence.AsSpan().SequenceEqual(asItWas))
                    Problem(problems, $"a pulse in silence changed the frame");
                var changed = 0.0;
                for (var i = 0; i < beat.Length; i++)
                    changed += Math.Abs(beat[i] - asItWas[i]);
                changed /= beat.Length;
                Line(text, $"pulse 100 % on a full beat: a brighter glow (on average more than a step of 255 a channel)");
                Note($"reactive: a beat changed the frame by {changed:0.00} steps on average");
                if (!(changed > 1))
                    Problem(problems, $"a beat changed the frame by only {changed:0.00} steps");
            }
        }

        // The export's frames: the bands of its sound, a hair into each frame, as GpuExport.Publish writes them; none while nothing pulses.
        var burst = Wav(folder, "bass-burst.wav", 2.5, i => i >= Rate && i < Rate * 1.5 ? 0.25 * Math.Sin(2 * Math.PI * 60 * i / Rate) : 0);
        var document = TrackOf(burst, null);
        var video = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        var card = EffectRegistry.Find(EffectRegistry.Glow)!.NewEffect();
        video.Clips.Add(new TimelineClip
        {
            ContentKind = LayerKind.Adjustment, Start = 0, End = 2.5,
            State = new LayerState { Kind = LayerKind.Adjustment, Name = "Glow", Effects = [card.ToState()] },
        });
        document.Tracks.Add(video);
        Line(text, $"a Glow with no pulse: nothing pulses, no map is made");
        if (AudioReactive.Pulses(State(document)))
            Problem(problems, $"a Glow without a pulse pulses");

        card.Set(AudioReactive.PulseKey, 60);
        video.Clips[0].State = new LayerState { Kind = LayerKind.Adjustment, Name = "Glow", Effects = [card.ToState()] };
        var state = State(document);
        Line(text, $"at 60 %: it pulses");
        if (!AudioReactive.Pulses(state))
            Problem(problems, $"a Glow at 60 % does not pulse");

        var map = AudioReactive.MapOf(state, 0, 2.5, CancellationToken.None);
        float BassAt(double seconds)
        {
            var header = default(FrameSceneHeader);
            GpuExport.WriteBands(ref header, map, seconds);
            return header.AudioBass;
        }

        var (quiet, during, after) = (BassAt(0.5), BassAt(1.3), BassAt(2.3));
        Line(text, $"the export's frame at 0.5 s (silence): Bass 0; at 1.3 s (a 60 Hz burst at -12 dBFS): 0.8 or more; at 2.3 s (0.8 s after it): under 0.05");
        Note($"reactive: export bands {quiet:0.000} / {during:0.000} / {after:0.000}");
        if (quiet != 0 || during < 0.8f || after >= 0.05f)
            Problem(problems, $"the export's bands are {quiet:0.000} / {during:0.000} / {after:0.000}");
        Line(text, $"past the map's end: nothing is known, nothing written");
        var beyond = default(FrameSceneHeader);
        if (GpuExport.WriteBands(ref beyond, map, 10) || beyond.AudioBass != 0)
            Problem(problems, $"a frame past the map's end got bands");
    }

    // ----- Fixtures -----

    // A uniform noise from -1 to 1, the same for the same index.
    private static double Noise(int i)
    {
        var x = (uint)i * 2654435761u + 0x9E3779B9u;
        x ^= x >> 15;
        x *= 0x2C1B3C6Du;
        x ^= x >> 12;
        x *= 0x297A2D39u;
        x ^= x >> 15;
        return x / (double)uint.MaxValue * 2 - 1;
    }

    private static float[] Render(TimelineDocument document, double from, double seconds)
    {
        var output = new float[(int)Math.Round(seconds * Rate) * 2];
        AudioMixer.Render(AudioProgram.Build(State(document)), from, output, Rate);
        return output;
    }

    // A Sequence of one audio track holding the whole of a file (its filters as given).
    private static TimelineDocument TrackOf(string path, TrackAudioFilters? filters)
    {
        var document = new TimelineDocument { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, TrackNumber = 1, AudioFilters = filters };
        track.Clips.Add(new TimelineClip
        {
            ContentKind = LayerKind.Audio, SourcePath = path, AudioStreamIndex = 0, Start = 0, End = 2.5, MediaOffset = 0, SourceDuration = 120,
            State = new LayerState { Kind = LayerKind.Audio, Name = Path.GetFileNameWithoutExtension(path), ImagePath = path, MediaDuration = 120, HasAudio = true },
        });
        document.Tracks.Add(track);
        return document;
    }

    private static SequenceExportState State(TimelineDocument document) => new()
    {
        Timeline = document,
        SourceFrameRate = 30,
        RequestedSize = (1920, 1080),
        OutputSize = (1920, 1080),
        CaptionLayer = Layer.FromState(new LayerState { Kind = LayerKind.Captions, Name = "Subtitles", SizeWidth = 0.9, SizeHeight = 0.2 }, 1920, 1080, 1920, 1080),
    };

    // A stereo 16-bit WAV of a mono signal at 48 kHz.
    private static string Wav(string folder, string name, double seconds, Func<int, double> sample)
    {
        var path = Path.Combine(folder, name);
        var frames = (int)(seconds * Rate);
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + frames * 4);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)2);
        file.Write(Rate);
        file.Write(Rate * 4);
        file.Write((short)4);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            var value = (short)Math.Clamp(Math.Round(32767 * sample(i)), short.MinValue, short.MaxValue);
            file.Write(value);
            file.Write(value);
        }

        return path;
    }
}
