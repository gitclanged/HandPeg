using System.IO;
using System.Globalization;
using HandPegApp.Models;

namespace HandPegApp.Services.Audio;

// ----- Mixer -----
//
// One mixing core for the device and the offline render (AudioMixer.Render, tests, exports): each strip's voices are read from their rings
// and summed, then processed in export order: track strips place clips with fades and gains, duck, then apply track gain and processing
// (state carried across cuts); clip strips apply processing and gain, then fades, then ducking; the voices' sum is the side chain. Strips are
// summed and the sequence fades applied. Nothing on the mixer thread allocates or locks: a program arrives as one runtime reference, and a
// strip whose settings changed brings prebuilt effects that inherit the old ones' state.

/// <summary>Runtime state of one strip: its effects (mixer thread only) and staged replacements when settings change.</summary>
internal sealed class StripState
{
    public StripState(string key, AudioEffectSpec[] specs, AudioEffect[] effects, DuckParameters? duck, Ducker? ducker, int sampleRate)
    {
        (Key, Specs, Effects, DuckSpec, Duck, _rate) = (key, specs, effects, duck, ducker, sampleRate);
        Latency = AudioEffect.LatencyOf(specs, sampleRate);
    }

    // Mixer sample rate (the limiter's lookahead is measured in it).
    private readonly int _rate;

    public string Key { get; }
    public AudioEffectSpec[] Specs { get; private set; }
    public DuckParameters? DuckSpec { get; private set; }

    /// <summary>Mixer thread only.</summary>
    public AudioEffect[] Effects;

    public Ducker? Duck;

    /// <summary>Latency of <see cref="Effects"/> in samples (AudioEffect.LatencyOf): the strip's voices are read this far ahead. Mixer thread only.</summary>
    public int Latency;

    /// <summary>Stages new settings built on the caller's thread; the mixer adopts them before its next buffer (<see cref="Adopt"/>).</summary>
    public void Change(AudioEffectSpec[] specs, AudioEffect[] effects, DuckParameters? duck, Ducker? ducker)
    {
        (Specs, DuckSpec) = (specs, duck);
        Volatile.Write(ref _pendingBox, new PendingBox(effects, ducker, AudioEffect.LatencyOf(specs, _rate)));
    }

    private PendingBox? _pendingBox;

    private sealed record PendingBox(AudioEffect[] Effects, Ducker? Duck, int Latency);

    /// <summary>Mixer thread: adopts staged effects, each inheriting the state of the old effect of the same type at the same position.</summary>
    public void Adopt()
    {
        if (Interlocked.Exchange(ref _pendingBox, null) is not { } next)
            return;
        for (var i = 0; i < next.Effects.Length && i < Effects.Length; i++)
        {
            if (next.Effects[i].GetType() == Effects[i].GetType())
                next.Effects[i].TakeState(Effects[i]);
        }

        if (next.Duck is { } duck && Duck is { } old)
            duck.TakeState(old);
        (Effects, Duck, Latency) = (next.Effects, next.Duck, next.Latency);
    }

    public void Reset()
    {
        foreach (var effect in Effects)
            effect.Reset();
        Duck?.Reset();
    }
}

/// <summary>A program prepared for mixing: voice and strip states in program order, plus the strip mix order.</summary>
internal sealed class MixerRuntime
{
    public required AudioProgram Program { get; init; }
    public required VoiceState[] Voices { get; init; }
    public required StripState[] Strips { get; init; }
    public required int[][] StripVoices { get; init; }

    /// <summary>Voice strips first (the side chain is their sum), then ducked strips, then the rest.</summary>
    public required int[] Order { get; init; }

    public required bool Ducks { get; init; }
    public required long Version { get; init; }
}

/// <summary>Mixing core (see file header). Runs on the device thread (or the offline render's caller).</summary>
public sealed class AudioMixer : IDisposable
{
    /// <summary>Feeder lead ahead of the mixer, voice open look-ahead, and ring length, in seconds.</summary>
    internal const double LeadSeconds = 0.35, LookAheadSeconds = 1.5, RingSeconds = 1.0;

    private readonly int _rate;
    private readonly int _maxFrames;
    private readonly float[] _strip, _voice, _key;
    private readonly AudioDecoderPool _pool;
    private readonly AudioFeeders? _feeders;
    private readonly object _programGate = new();
    private readonly Dictionary<string, VoiceState> _voiceStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StripState> _stripStates = new(StringComparer.Ordinal);
    private long _versions;

    private MixerRuntime? _published;
    private MixerRuntime? _current;
    private long _observed;
    private long _liveFrame;
    private TransportLine _line = new(0, 0, 0, 1);
    private long _lines;
    private bool _resetStrips;

    internal AudioMixer(int sampleRate, int maxFrames, bool threaded)
    {
        (_rate, _maxFrames) = (sampleRate, Math.Max(maxFrames, 64));
        (_strip, _voice, _key) = (new float[_maxFrames * 2], new float[_maxFrames * 2], new float[_maxFrames * 2]);
        _pool = new AudioDecoderPool(sampleRate);
        if (threaded)
        {
            _feeders = new AudioFeeders(sampleRate, (int)(LeadSeconds * sampleRate), _maxFrames + sampleRate / 200, _pool,
                () => Volatile.Read(ref _published)?.Voices, () => Volatile.Read(ref _liveFrame), () => Volatile.Read(ref _observed));
        }
    }

    public int SampleRate => _rate;

    /// <summary>Largest block <see cref="Mix"/> accepts.</summary>
    internal int MaxFrames => _maxFrames;

    internal TransportLine Line => _line;

    /// <summary>Currently published program (possibly not yet heard); an engine reopened on another device starts from it.</summary>
    internal AudioProgram? Program => Volatile.Read(ref _published)?.Program;

    // ----- Program (any thread; serialized) -----

    /// <summary>Prepares and publishes a program; the mixer adopts it at its next buffer.</summary>
    internal void SetProgram(AudioProgram program)
    {
        lock (_programGate)
        {
            var version = ++_versions;
            var voices = new VoiceState[program.VoiceArray.Length];
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var capacity = (int)(RingSeconds * _rate);
            for (var i = 0; i < voices.Length; i++)
            {
                var voice = program.VoiceArray[i];
                var line = VoiceKey(voice);
                var n = occurrences.GetValueOrDefault(line);
                occurrences[line] = n + 1;
                var key = n == 0 ? line : string.Create(CultureInfo.InvariantCulture, $"{line}#{n}");
                if (_voiceStates.TryGetValue(key, out var state))
                    state.Voice = voice;
                else
                    _voiceStates[key] = state = new VoiceState(key, voice, capacity);
                voices[i] = state;
                keep.Add(key);
            }

            foreach (var (key, state) in _voiceStates.Where(p => !keep.Contains(p.Key)).ToList())
            {
                _voiceStates.Remove(key);
                state.RetiredAt = version;
                _feeders?.Retire(state);
            }

            var strips = new StripState[program.StripArray.Length];
            var stripKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < strips.Length; i++)
            {
                var strip = program.StripArray[i];
                var specs = strip.Effects.ToArray();
                var duck = strip.Ducked ? program.Duck : null;
                if (_stripStates.TryGetValue(strip.Key, out var state))
                {
                    if (!state.Specs.AsSpan().SequenceEqual(specs) || state.DuckSpec != duck)
                        state.Change(specs, Make(specs), duck, duck is null ? null : new Ducker(duck, _rate));
                }
                else
                {
                    _stripStates[strip.Key] = state = new StripState(strip.Key, specs, Make(specs), duck, duck is null ? null : new Ducker(duck, _rate), _rate);
                }

                strips[i] = state;
                stripKeys.Add(strip.Key);
            }

            foreach (var key in _stripStates.Keys.Where(k => !stripKeys.Contains(k)).ToList())
                _stripStates.Remove(key);

            var stripVoices = new List<int>[strips.Length];
            for (var i = 0; i < strips.Length; i++)
                stripVoices[i] = [];
            for (var i = 0; i < program.VoiceArray.Length; i++)
            {
                var at = program.VoiceArray[i].Strip;
                if (at >= 0 && at < strips.Length)
                    stripVoices[at].Add(i);
            }

            var order = Enumerable.Range(0, strips.Length)
                .OrderBy(i => program.StripArray[i].IsVoice ? 0 : program.StripArray[i].Ducked ? 1 : 2)
                .ToArray();
            var ducks = program.StripArray.Any(s => s.IsVoice) && program.StripArray.Any(s => s.Ducked);

            Volatile.Write(ref _published, new MixerRuntime
            {
                Program = program,
                Voices = voices,
                Strips = strips,
                StripVoices = stripVoices.Select(l => l.ToArray()).ToArray(),
                Order = order,
                Ducks = ducks,
                Version = version,
            });
        }

        _feeders?.Wake();
    }

    /// <summary>
    /// Disposes decoders returned by retired voices and not rented again (any thread; tab deactivated): after an empty program, every file
    /// the previous one played is closed. Voices still retiring return theirs later.
    /// </summary>
    public void ReleaseIdleDecoders() => _pool.ReleaseIdle();

    private AudioEffect[] Make(AudioEffectSpec[] specs)
    {
        var effects = new AudioEffect[specs.Length];
        for (var i = 0; i < specs.Length; i++)
            effects[i] = AudioEffect.Create(specs[i], _rate);
        return effects;
    }

    /// <summary>
    /// Key of a voice's decoded frames: file, stream, channel pair (and FFmpeg's video start), rate, pitch, stretch mode and its
    /// sequence-to-media line. Trims keep the line, so a trimmed clip keeps its decoded audio.
    /// </summary>
    private static string VoiceKey(AudioVoice voice)
    {
        var s = voice.Source;
        var intercept = voice.SequenceStart - voice.MediaStart / voice.Rate;
        return string.Create(CultureInfo.InvariantCulture,
            $"{s.Path}|{s.Stream}|{s.ChannelPair}|{s.VideoStart:R}|{voice.Rate:R}|{voice.Pitch:R}|{(voice.Spectral ? "pv" : "ws")}|{Math.Round(intercept * 1e6):0}");
    }

    // ----- Transport (mixer thread) -----

    /// <summary>
    /// Starts a new transport line at <paramref name="frame"/>: sequence time <paramref name="origin"/> at <paramref name="rate"/>. On seeks,
    /// starts and speed changes every voice is re-requested; on a jump every strip's processing restarts, as FFmpeg filters would in a graph
    /// started at that time.
    /// </summary>
    internal void SetLine(long frame, double origin, double rate, bool jump)
    {
        _line = new TransportLine(++_lines, frame, origin, rate);
        _resetStrips |= jump;
        Prime(frame);
    }

    /// <summary>Requests voices starting soon that were not requested on this line (and those whose feeding fell behind).</summary>
    internal void Prime(long nextFrame)
    {
        Volatile.Write(ref _liveFrame, nextFrame);
        var runtime = Adopt();
        if (runtime is null || _line.Rate <= 0)
            return;

        var from = _line.SoundAt(nextFrame, _rate);
        var until = from + LookAheadSeconds * _line.Rate;
        var asked = false;
        var voices = runtime.Program.VoiceArray;
        for (var i = 0; i < voices.Length; i++)
        {
            var (voice, state) = (voices[i], runtime.Voices[i]);
            if (state.StarvedFrames > _rate / 20)
                state.NeedsRequest = true;
            if (state.RequestedLine == _line.Id && !state.NeedsRequest)
                continue;
            if (!voice.Overlaps(from, Math.Min(until, runtime.Program.SequenceSeconds)))
            {
                // Fed for another line and idle on this one (a seek moved the playhead away): mark idle so its decoder is released.
                if (state.RequestedLine >= 0 && state.IdleLine != _line.Id)
                {
                    state.Idle(_line);
                    asked = true;
                }

                continue;
            }

            var start = Math.Max(nextFrame, _line.FrameAt(voice.SequenceStart, _rate));
            if (state.NeedsRequest && state.RequestedLine == _line.Id)
                start = Math.Max(start, nextFrame + _maxFrames + _rate * 3 / 100);
            state.Request(_line, start);
            asked = true;
        }

        if (asked)
            _feeders?.Wake();
    }

    /// <summary>After a buffer: wakes the feeders to keep filling.</summary>
    internal void WakeFeeders() => _feeders?.Wake();

    private MixerRuntime? Adopt()
    {
        var published = Volatile.Read(ref _published);
        if (!ReferenceEquals(published, _current) && published is not null)
        {
            foreach (var strip in published.Strips)
                strip.Adopt();
            _current = published;
            Volatile.Write(ref _observed, published.Version);
        }

        return _current;
    }

    // ----- Mix (mixer thread) -----

    /// <summary>
    /// Mixes <paramref name="frames"/> (at most <see cref="MaxFrames"/>) output frames from <paramref name="frame"/> into interleaved stereo.
    /// Silence where nothing is audible, while the transport is reversed or stopped, and past the end.
    /// </summary>
    internal void Mix(Span<float> stereo, long frame, int frames) => MixInto(stereo, null, frame, frames);

    /// <summary>
    /// <see cref="Mix"/> with each strip routed to its <see cref="AudioStrip.Bus"/> (multi-track export in one pass): bus 0 into
    /// <paramref name="stereo"/>, bus n into <paramref name="buses"/>[n] (interleaved stereo, at least frames * 2; [0] unused). Every voice
    /// still feeds the ducking side chain, and sequence fades apply on every bus.
    /// </summary>
    private void MixInto(Span<float> stereo, float[][]? buses, long frame, int frames)
    {
        var output = stereo[..(frames * 2)];
        output.Clear();
        for (var b = 1; b < (buses?.Length ?? 0); b++)
            buses![b].AsSpan(0, frames * 2).Clear();
        var runtime = Adopt();
        if (runtime is null || _line.Rate <= 0 || frames <= 0)
            return;

        if (_resetStrips)
        {
            foreach (var state in runtime.Strips)
                state.Reset();
            _resetStrips = false;
        }

        var program = runtime.Program;
        var (from, to) = (_line.SoundAt(frame, _rate), _line.SoundAt(frame + frames, _rate));
        if (from >= program.SequenceSeconds || _line.SoundAt(frame + frames + MostLatency(runtime), _rate) <= 0)
            return;

        var key = _key.AsSpan(0, frames * 2);
        key.Clear();
        var strip = _strip.AsSpan(0, frames * 2);
        foreach (var index in runtime.Order)
        {
            var spec = program.StripArray[index];
            var state = runtime.Strips[index];

            // Strips with effect latency (Noise Reduction, Studio Voice, limiter lookahead) read their voices that far ahead so their
            // output is this block's audio: a clip's sound enters that long before the clip (pre-roll) and its own strip runs on as long
            // after its last input (tail), so nothing is cut at either edge and sound lands on its picture to the sample.
            var heard = Voices(runtime, index, frame + state.Latency, frames, strip, envelope: !spec.EnvelopeAfterEffects);
            if (!heard && spec.Kind != AudioStripKind.Track && !(state.Latency > 0 && OwnVoiceHeard(runtime, index, from, to)))
                continue;

            if (spec.Kind == AudioStripKind.Track)
            {
                if (spec.IsVoice)
                    AudioMath.Add(key, strip);
                if (runtime.Ducks && spec.Ducked)
                    state.Duck?.Process(strip, key, frames);
                Process(state.Effects, strip, frames, from, to);
            }
            else
            {
                Process(state.Effects, strip, frames, from, to);
                if (runtime.StripVoices[index] is [var only])
                    Envelope(program.VoiceArray[only], strip, frame, frames);
                if (spec.IsVoice)
                    AudioMath.Add(key, strip);
                if (runtime.Ducks && spec.Ducked)
                    state.Duck?.Process(strip, key, frames);
            }

            AudioMath.Add(spec.Bus > 0 && buses is not null && spec.Bus < buses.Length ? buses[spec.Bus].AsSpan(0, frames * 2) : output, strip);
        }

        // Sequence master gain on every bus, after strip summing and before sequence fades; 0 dB is exactly 1 and skipped.
        if (program.MasterGain != 1f)
        {
            AudioMath.Scale(output, program.MasterGain);
            for (var b = 1; b < (buses?.Length ?? 0); b++)
                AudioMath.Scale(buses![b].AsSpan(0, frames * 2), program.MasterGain);
        }

        SequenceFades(program, output, frame, frames, from, to);
        for (var b = 1; b < (buses?.Length ?? 0); b++)
            SequenceFades(program, buses![b].AsSpan(0, frames * 2), frame, frames, from, to);
        _feeders?.Wake();
    }

    // Runs a strip's effects in order, giving each the block's sequence time span first (keyed effects follow the sequence).
    private static void Process(AudioEffect[] effects, Span<float> strip, int frames, double from, double to)
    {
        foreach (var effect in effects)
        {
            effect.Moment(from, to);
            effect.Process(strip, frames);
        }
    }

    // Maximum strip latency in samples: how far ahead of the transport the mix reads.
    private static int MostLatency(MixerRuntime runtime)
    {
        var most = 0;
        foreach (var strip in runtime.Strips)
            most = Math.Max(most, strip.Latency);
        return most;
    }

    // True when a clip strip's own voice is audible over from..to (its tail: the read-ahead voice has ended, its sound has not).
    private static bool OwnVoiceHeard(MixerRuntime runtime, int strip, double from, double to) =>
        runtime.StripVoices[strip] is [var only] && runtime.Program.VoiceArray[only] is var voice
        && voice.SequenceStart < to && Math.Min(voice.End, runtime.Program.SequenceSeconds) > from;

    // Sums a strip's voices from frame (read-ahead included) into its buffer, applying fades and gains for track strips. Returns true
    // when any voice was audible.
    private bool Voices(MixerRuntime runtime, int strip, long frame, int frames, Span<float> buffer, bool envelope)
    {
        buffer.Clear();
        var heard = false;
        var program = runtime.Program;
        var (from, to) = (_line.SoundAt(frame, _rate), _line.SoundAt(frame + frames, _rate));
        foreach (var index in runtime.StripVoices[strip])
        {
            var voice = program.VoiceArray[index];
            var end = Math.Min(voice.End, program.SequenceSeconds);
            if (!(voice.SequenceStart < to && end > from))
                continue;

            var a = (int)Math.Clamp(_line.FrameAt(voice.SequenceStart, _rate) - frame, 0, frames);
            var b = (int)Math.Clamp(_line.FrameAt(end, _rate) - frame, 0, frames);
            if (b <= a)
                continue;

            var part = _voice.AsSpan(0, (b - a) * 2);
            runtime.Voices[index].Take(frame + a, part, b - a);
            if (envelope)
                Envelope(voice, part, frame + a, b - a);
            AudioMath.Add(buffer[(a * 2)..(b * 2)], part);
            heard = true;
        }

        return heard;
    }

    /// <summary>Applies a voice's gain and fades over frames from <paramref name="frame"/>; silence outside the voice.</summary>
    private void Envelope(AudioVoice voice, Span<float> stereo, long frame, int frames)
    {
        var from = _line.SoundAt(frame, _rate);
        var to = _line.SoundAt(frame + frames, _rate);
        var fadeInEnd = voice.FadeIn.IsOn ? voice.SequenceStart + voice.FadeIn.Seconds : double.NegativeInfinity;
        var fadeOutStart = voice.FadeOut.IsOn ? voice.FadeOutEnd - voice.FadeOut.Seconds : double.PositiveInfinity;
        var plain = from >= voice.SequenceStart && to <= voice.End && from >= fadeInEnd && to <= fadeOutStart;
        if (plain)
        {
            AudioMath.Scale(stereo[..(frames * 2)], voice.Gain);
            return;
        }

        for (var i = 0; i < frames; i++)
        {
            var g = Gain(voice, _line.SoundAt(frame + i, _rate));
            stereo[2 * i] *= g;
            stereo[2 * i + 1] *= g;
        }
    }

    /// <summary>Voice gain at a sound time: 0 outside the voice, fade curves within its fades.</summary>
    internal static float Gain(AudioVoice voice, double sound)
    {
        if (sound < voice.SequenceStart || sound >= voice.End)
            return 0;

        double g = voice.Gain;
        if (voice.FadeIn.IsOn)
        {
            var u = (sound - voice.SequenceStart) / voice.FadeIn.Seconds;
            if (u < 1)
                g *= Curve(Math.Max(u, 0), voice.FadeIn.Easing);
        }

        if (voice.FadeOut.IsOn)
        {
            var u = (voice.FadeOutEnd - sound) / voice.FadeOut.Seconds;
            if (u < 1)
                g *= Curve(Math.Max(u, 0), voice.FadeOut.Easing);
        }

        return (float)g;
    }

    // Fade level at a fraction of its length (EasingCurves; Linear matches afade).
    private static double Curve(double u, EasingType easing) =>
        easing == EasingType.Linear ? u : EasingCurves.Evaluate(u, new Keyframe(0, 0, easing));

    // Sequence fades over the mix (one second each), and silence outside the sequence.
    private void SequenceFades(AudioProgram program, Span<float> output, long frame, int frames, double from, double to)
    {
        var length = program.SequenceSeconds;
        var fadeIn = program.FadeIn;
        var fadeOut = program.FadeOut && length > AudioProgram.FadeSeconds;
        var plain = from >= 0 && to <= length && (!fadeIn || from >= AudioProgram.FadeSeconds) && (!fadeOut || to <= length - AudioProgram.FadeSeconds);
        if (plain)
            return;

        for (var i = 0; i < frames; i++)
        {
            var t = _line.SoundAt(frame + i, _rate);
            var g = t < 0 || t >= length ? 0.0 : 1.0;
            if (fadeIn)
                g *= Math.Clamp(t / AudioProgram.FadeSeconds, 0, 1);
            if (fadeOut)
                g *= Math.Clamp((length - t) / AudioProgram.FadeSeconds, 0, 1);
            output[2 * i] *= (float)g;
            output[2 * i + 1] *= (float)g;
        }
    }

    // ----- Offline render -----

    /// <summary>
    /// Renders a program from a sequence time into interleaved stereo at a rate with the same code as device playback, feeding voices inline
    /// on the calling thread (exactly where requested). For tests and measurements.
    /// </summary>
    public static void Render(AudioProgram program, double fromSeconds, Span<float> interleavedStereo, int sampleRate)
    {
        const int block = 480;
        using var mixer = new AudioMixer(sampleRate, block, threaded: false);
        mixer.SetProgram(program);
        mixer.SetLine(0, fromSeconds, 1, jump: true);
        mixer.WarmUp(program, block);
        var total = interleavedStereo.Length / 2;
        for (long frame = 0; frame < total; frame += block)
        {
            var frames = (int)Math.Min(block, total - frame);
            mixer.Prime(frame);
            mixer.FeedInline(frame, frames);
            mixer.Mix(interleavedStereo.Slice((int)(frame * 2), frames * 2), frame, frames);
        }
    }

    // Pre-roll so an offline render's first block matches playback: strips with latency are mixed from that far before the start and the
    // output before frame 0 is discarded.
    private void WarmUp(AudioProgram program, int block)
    {
        var warm = program.MostLatencyAt(_rate);
        if (warm <= 0)
            return;
        var scratch = new float[block * 2];
        for (long frame = -warm; frame < 0; frame += block)
        {
            var frames = (int)Math.Min(block, -frame);
            Prime(frame);
            FeedInline(frame, frames);
            MixInto(scratch.AsSpan(0, frames * 2), null, frame, frames);
        }
    }

    /// <summary>
    /// Streams a long render as interleaved native little-endian stereo floats with one mixer end to end (an hour is 1.3 GB, too large for
    /// an array).
    /// </summary>
    public static void RenderTo(AudioProgram program, double fromSeconds, long frames, int sampleRate, Stream output, Action<double>? progress, CancellationToken token) =>
        RenderTo(program, fromSeconds, frames, sampleRate, [output], progress, token);

    /// <summary>
    /// <see cref="RenderTo(AudioProgram, double, long, int, Stream, Action{double}?, CancellationToken)"/> for every bus at once (multi-track
    /// export, AudioStrip.Bus): bus n into <paramref name="outputs"/>[n] as interleaved stereo floats. One mixer, one pass, so strips on one
    /// bus can duck under voices on another.
    /// </summary>
    public static void RenderTo(AudioProgram program, double fromSeconds, long frames, int sampleRate, IReadOnlyList<Stream> outputs, Action<double>? progress, CancellationToken token)
    {
        const int block = 4800, reportEvery = 100;
        using var mixer = new AudioMixer(sampleRate, block, threaded: false);
        mixer.SetProgram(program);
        mixer.SetLine(0, fromSeconds, 1, jump: true);
        mixer.WarmUp(program, block);
        var buffer = new float[block * 2];
        var buses = new float[outputs.Count][];
        for (var b = 1; b < buses.Length; b++)
            buses[b] = new float[block * 2];
        var blocks = 0;
        for (long frame = 0; frame < frames; frame += block)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(block, frames - frame);
            mixer.Prime(frame);
            mixer.FeedInline(frame, count);
            mixer.MixInto(buffer.AsSpan(0, count * 2), buses.Length > 1 ? buses : null, frame, count);
            outputs[0].Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan(0, count * 2)));
            for (var b = 1; b < buses.Length; b++)
                outputs[b].Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buses[b].AsSpan(0, count * 2)));
            if (++blocks % reportEvery == 0)
                progress?.Invoke((double)frame / Math.Max(frames, 1));
        }
    }

    // Offline feeding on this thread: every requested voice written past the block plus the strips' read-ahead.
    private void FeedInline(long frame, int frames)
    {
        if (_current is not { } runtime)
            return;
        var lead = frames * 2 + MostLatency(runtime);
        foreach (var state in runtime.Voices)
        {
            if (state.RequestSerial == 0)
                continue;
            state.Producer ??= new VoiceProducer(_rate);
            state.Producer.Service(state, _pool, frame, lead, 0, exact: true);
        }
    }

    public void Dispose()
    {
        var stopped = _feeders?.Stop() ?? true;
        var kept = 0;
        lock (_programGate)
        {
            foreach (var state in _voiceStates.Values)
            {
                // A voice a stalled feeder still holds (inside a decoder read) is left to it: releasing would free the decoder under that
                // call. With both feeders stopped every Busy is 0, so every voice is released.
                if (Interlocked.CompareExchange(ref state.Busy, 1, 0) == 0)
                    state.Release(_pool);
                else
                    kept++;
            }

            _voiceStates.Clear();
        }

        if (!stopped || kept > 0)
            AppLog.Write($"Sound engine: {kept} voice(s) left to a feeder that did not stop; their decoders are disposed when it returns them");

        // Only idle decoders are disposed here; one a stalled feeder returns later is disposed by the pool (Return after Dispose).
        _pool.Dispose();
    }
}
