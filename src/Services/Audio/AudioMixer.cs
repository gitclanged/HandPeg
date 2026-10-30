using System.Globalization;
using HandPegApp.Models;

namespace HandPegApp.Services.Audio;

// ----- The mix (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// One mixing core for the device and for the offline render (AudioMixer.Render, the tests): the voices of each strip are
// read from their rings and summed, each strip is processed as the export's graph processes that sound (FilterGraphBuilder
// .Audio): a main track's clips placed with their fades and gains, ducked, then its track's processing; a
// clip's processing and gain, then its fades, then ducked; the voices' sum is the side chain. The strips are summed and the
// sequence's fades laid over the whole. Nothing on the mixer's thread allocates or locks: the program arrives whole as a
// new runtime (one reference), and a strip whose settings changed brings its new effects made, taking the old ones' state.

/// <summary>A strip at work: its effects (the mixer's alone), and the next ones when its settings change.</summary>
internal sealed class StripState
{
    public StripState(string key, AudioEffectSpec[] specs, AudioEffect[] effects, DuckParameters? duck, Ducker? ducker)
    {
        (Key, Specs, Effects, DuckSpec, Duck) = (key, specs, effects, duck, ducker);
    }

    public string Key { get; }
    public AudioEffectSpec[] Specs { get; private set; }
    public DuckParameters? DuckSpec { get; private set; }

    /// <summary>The mixer's thread only.</summary>
    public AudioEffect[] Effects;

    public Ducker? Duck;

    /// <summary>New settings, made on the caller's thread; the mixer takes them up before its next buffer (<see cref="Adopt"/>).</summary>
    public void Change(AudioEffectSpec[] specs, AudioEffect[] effects, DuckParameters? duck, Ducker? ducker)
    {
        (Specs, DuckSpec) = (specs, duck);
        Volatile.Write(ref _pendingBox, new PendingBox(effects, ducker));
    }

    private PendingBox? _pendingBox;

    private sealed record PendingBox(AudioEffect[] Effects, Ducker? Duck);

    /// <summary>The mixer's thread: new effects take the state of the old ones of the same kind, at the same place.</summary>
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
        (Effects, Duck) = (next.Effects, next.Duck);
    }

    public void Reset()
    {
        foreach (var effect in Effects)
            effect.Reset();
        Duck?.Reset();
    }
}

/// <summary>A program made ready to mix: its voices' and strips' states, in its own order, and the order strips are mixed in.</summary>
internal sealed class MixerRuntime
{
    public required AudioProgram Program { get; init; }
    public required VoiceState[] Voices { get; init; }
    public required StripState[] Strips { get; init; }
    public required int[][] StripVoices { get; init; }

    /// <summary>The voices' strips first (the side chain is their sum), then the ducked, then the rest.</summary>
    public required int[] Order { get; init; }

    public required bool Ducks { get; init; }
    public required long Version { get; init; }
}

/// <summary>The mixing core (see the top of this file). Its thread is the device's (or the offline render's caller).</summary>
public sealed class AudioMixer : IDisposable
{
    /// <summary>How far ahead of the mixer the feeders fill, and how soon a voice is opened before it comes on.</summary>
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

    /// <summary>The largest block <see cref="Mix"/> takes.</summary>
    internal int MaxFrames => _maxFrames;

    internal TransportLine Line => _line;

    /// <summary>The program now published (not yet heard, perhaps): what an engine reopened on another device starts with.</summary>
    internal AudioProgram? Program => Volatile.Read(ref _published)?.Program;

    // ----- The program (any thread; one at a time) -----

    /// <summary>Makes a program ready and publishes it: the mixer takes it up at its next buffer.</summary>
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
                    _stripStates[strip.Key] = state = new StripState(strip.Key, specs, Make(specs), duck, duck is null ? null : new Ducker(duck, _rate));
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

    private AudioEffect[] Make(AudioEffectSpec[] specs)
    {
        var effects = new AudioEffect[specs.Length];
        for (var i = 0; i < specs.Length; i++)
            effects[i] = AudioEffect.Create(specs[i], _rate);
        return effects;
    }

    /// <summary>
    /// What a voice's decoded frames depend on: its file and stream (and where FFmpeg put its start), its rate and pitch, and
    /// its line from sequence to media time. A trim keeps the line, so a trimmed clip keeps what was decoded for it.
    /// </summary>
    private static string VoiceKey(AudioVoice voice)
    {
        var s = voice.Source;
        var intercept = voice.SequenceStart - voice.MediaStart / voice.Rate;
        return string.Create(CultureInfo.InvariantCulture,
            $"{s.Path}|{s.Stream}|{s.VideoStart:R}|{voice.Rate:R}|{voice.Pitch:R}|{Math.Round(intercept * 1e6):0}");
    }

    // ----- The transport (the mixer's thread) -----

    /// <summary>
    /// A new transport line from <paramref name="frame"/>: the sound at <paramref name="origin"/> (sequence seconds) at
    /// <paramref name="rate"/>. A seek, a start, a change of speed: every voice is asked for again from there, and (a jump)
    /// every strip's processing starts afresh, as FFmpeg's filters do in a graph begun at that moment.
    /// </summary>
    internal void SetLine(long frame, double origin, double rate, bool jump)
    {
        _line = new TransportLine(++_lines, frame, origin, rate);
        _resetStrips |= jump;
        Prime(frame);
    }

    /// <summary>Asks for the voices that come on soon and were not asked for on this line (and those whose feeding fell behind).</summary>
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
                // Fed for another line and not wanted on this one (a seek took the playhead away): told to let go.
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

    /// <summary>After a buffer: the feeders go on filling.</summary>
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

    // ----- The mix (the mixer's thread) -----

    /// <summary>
    /// Mixes <paramref name="frames"/> (at most <see cref="MaxFrames"/>) output frames from <paramref name="frame"/> into
    /// interleaved stereo. Silence where nothing is heard, while the transport runs backward or stands, and past the end.
    /// </summary>
    internal void Mix(Span<float> stereo, long frame, int frames)
    {
        var output = stereo[..(frames * 2)];
        output.Clear();
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
        if (from >= program.SequenceSeconds || to <= 0)
            return;

        var key = _key.AsSpan(0, frames * 2);
        key.Clear();
        var strip = _strip.AsSpan(0, frames * 2);
        foreach (var index in runtime.Order)
        {
            var spec = program.StripArray[index];
            var state = runtime.Strips[index];
            var heard = Voices(runtime, index, frame, frames, from, to, strip, envelope: !spec.EnvelopeAfterEffects);
            if (!heard && spec.Kind != AudioStripKind.MainTrack)
                continue;

            if (spec.Kind == AudioStripKind.MainTrack)
            {
                if (spec.IsVoice)
                    AudioMath.Add(key, strip);
                if (runtime.Ducks && spec.Ducked)
                    state.Duck?.Process(strip, key, frames);
                foreach (var effect in state.Effects)
                    effect.Process(strip, frames);
            }
            else
            {
                foreach (var effect in state.Effects)
                    effect.Process(strip, frames);
                if (runtime.StripVoices[index] is [var only])
                    Envelope(program.VoiceArray[only], strip, frame, frames);
                if (spec.IsVoice)
                    AudioMath.Add(key, strip);
                if (runtime.Ducks && spec.Ducked)
                    state.Duck?.Process(strip, key, frames);
            }

            AudioMath.Add(output, strip);
        }

        SequenceFades(program, output, frame, frames, from, to);
        _feeders?.Wake();
    }

    // A strip's voices summed into its buffer (their fades and gains on them, for a main track). Whether any was heard.
    private bool Voices(MixerRuntime runtime, int strip, long frame, int frames, double from, double to, Span<float> buffer, bool envelope)
    {
        buffer.Clear();
        var heard = false;
        var program = runtime.Program;
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

    /// <summary>A voice's gain, fades and silenced stretches over frames of its sound, from <paramref name="frame"/>; nothing outside it.</summary>
    private void Envelope(AudioVoice voice, Span<float> stereo, long frame, int frames)
    {
        var from = _line.SoundAt(frame, _rate);
        var to = _line.SoundAt(frame + frames, _rate);
        var fadeInEnd = voice.FadeIn.IsOn ? voice.SequenceStart + voice.FadeIn.Seconds : double.NegativeInfinity;
        var fadeOutStart = voice.FadeOut.IsOn ? voice.FadeOutEnd - voice.FadeOut.Seconds : double.PositiveInfinity;
        var plain = from >= voice.SequenceStart && to <= voice.End && from >= fadeInEnd && to <= fadeOutStart;
        if (plain)
        {
            foreach (var (start, end) in voice.Silenced)
            {
                if (start <= to && end >= from)
                {
                    plain = false;
                    break;
                }
            }
        }

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

    /// <summary>A voice's gain at a sound moment: 0 outside it and where it is silenced, its fades' curves within them.</summary>
    internal static float Gain(AudioVoice voice, double sound)
    {
        if (sound < voice.SequenceStart || sound >= voice.End)
            return 0;
        foreach (var (start, end) in voice.Silenced)
        {
            if (sound >= start && sound <= end)
                return 0;
        }

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

    // A fade's level at a fraction of its way (the one curve math, EasingCurves; Linear is afade's).
    private static double Curve(double u, EasingType easing) =>
        easing == EasingType.Linear ? u : EasingCurves.Evaluate(u, new Keyframe(0, 0, easing));

    // The sequence's own fades over the mix (a second each), and nothing outside the sequence.
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

    // ----- The offline render -----

    /// <summary>
    /// Renders a program from a sequence moment into interleaved stereo at a rate, with the very code the device plays
    /// through, its voices fed in step on the calling thread (exactly where asked). For tests and measurements.
    /// </summary>
    public static void Render(AudioProgram program, double fromSeconds, Span<float> interleavedStereo, int sampleRate)
    {
        const int block = 480;
        using var mixer = new AudioMixer(sampleRate, block, threaded: false);
        mixer.SetProgram(program);
        mixer.SetLine(0, fromSeconds, 1, jump: true);
        var total = interleavedStereo.Length / 2;
        for (long frame = 0; frame < total; frame += block)
        {
            var frames = (int)Math.Min(block, total - frame);
            mixer.Prime(frame);
            mixer.FeedInline(frame, frames);
            mixer.Mix(interleavedStereo.Slice((int)(frame * 2), frames * 2), frame, frames);
        }
    }

    // The offline render's feeding: every voice asked for, written up to past the block, on this thread.
    private void FeedInline(long frame, int frames)
    {
        if (_current is not { } runtime)
            return;
        foreach (var state in runtime.Voices)
        {
            if (state.RequestSerial == 0)
                continue;
            state.Producer ??= new VoiceProducer(_rate);
            state.Producer.Service(state, _pool, frame, frames * 2, 0, exact: true);
        }
    }

    public void Dispose()
    {
        _feeders?.Dispose();
        lock (_programGate)
        {
            foreach (var state in _voiceStates.Values)
                state.Release(_pool);
            _voiceStates.Clear();
        }

        _pool.Dispose();
    }
}
