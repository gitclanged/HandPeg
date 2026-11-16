using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace HandPegApp.Services.Audio;

// ----- Studio Voice and De-Esser -----
//
// Studio Voice: one-click vocal chain, a strip effect (AudioChainBuilder: after cleaning, before dynamics) heard in playback and export
// alike, running on the mixer thread with all state preallocated. Wet path per channel: voice band (80 Hz high-pass, gentle 13 kHz low-pass);
// spectral stage (DenoiseEffect's STFT and minimum-statistics noise floor with a decision-directed Wiener gain) that also treats the room's
// late reverb (the band 60 ms earlier, decayed like a small room) as interference, removing hiss and room tail; presence (4 kHz peak plus
// soft 3-5 kHz harmonics); then a stereo-linked soft-knee broadcast leveller (slow RMS toward speech level, held in pauses, plus a fast peak
// catch). Dry is delayed by Latency to stay aligned and blended by mix (TrackAudioFilters.StudioVoiceMix, or its keyframes on the sequence
// clock via AudioEffect.Moment), ramped per block so changes never click. Mix 0 with keys is the delayed input, bit-exact; without keys the
// builder omits the effect.
//
// A voice model can replace the spectral stage (IVoiceModel, e.g. DeepFilterNet3 on an NPU from deps\models). None ships: VoiceModels
// checks once, off the hot threads, on first use, logs the result, and the native chain runs.
//
// De-Esser: split-band compressor on 5.5-9 kHz. The band (constant-0-dB-peak band-pass at ~7 kHz, unity and in phase at its centre) passed
// through the same band-pass again is the side chain (1 ms attack, 60 ms release); only the band is reduced (signal - band x (1 - gain), so
// the cut at the centre equals the computed reduction). Amount sets threshold and ratio. Zero latency; bit-exact below the knee.

/// <summary>RBJ cookbook biquad in double precision with its own state (building block of the vocal effects and the band follower).</summary>
internal struct RbjSection
{
    private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

    private static RbjSection Of(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new() { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };

    // Cookbook w0 and alpha, with the frequency clamped below Nyquist.
    private static (double Cos, double Alpha) Shape(double hz, double q, int sampleRate)
    {
        var w0 = 2 * Math.PI * Math.Clamp(hz, 1, sampleRate * 0.49) / sampleRate;
        var (sin, cos) = Math.SinCos(w0);
        return (cos, sin / (2 * Math.Max(q, 1e-3)));
    }

    public static RbjSection HighPass(double hz, double q, int sampleRate)
    {
        var (cos, alpha) = Shape(hz, q, sampleRate);
        return Of((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static RbjSection LowPass(double hz, double q, int sampleRate)
    {
        var (cos, alpha) = Shape(hz, q, sampleRate);
        return Of((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Band-pass with 0 dB peak gain (cookbook constant 0 dB peak).</summary>
    public static RbjSection BandPass(double hz, double q, int sampleRate)
    {
        var (cos, alpha) = Shape(hz, q, sampleRate);
        return Of(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static RbjSection Peaking(double hz, double q, double gainDb, int sampleRate)
    {
        var (cos, alpha) = Shape(hz, q, sampleRate);
        var a = Math.Pow(10, gainDb / 40);
        return Of(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
    }

    public double Next(double x)
    {
        var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
        return y;
    }

    /// <summary>Flushes denormals (the silence after a sound would otherwise slow the CPU drastically).</summary>
    public void Flush()
    {
        if (Math.Abs(_x1) < 1e-25)
            _x1 = 0;
        if (Math.Abs(_x2) < 1e-25)
            _x2 = 0;
        if (Math.Abs(_y1) < 1e-25)
            _y1 = 0;
        if (Math.Abs(_y2) < 1e-25)
            _y2 = 0;
    }

    /// <summary>Clears history, keeping coefficients (seek).</summary>
    public void Clear() => (_x1, _x2, _y1, _y2) = (0, 0, 0, 0);

    /// <summary>The same section on both stereo channels at once (<see cref="BiquadPair"/>), history cleared.</summary>
    public readonly BiquadPair Paired() => new(_b0, _b1, _b2, _a1, _a2);
}

/// <summary>
/// Two direct-form biquads in one SIMD vector (both stereo channels at once, for Studio Voice and the loudness meters): each lane follows
/// <see cref="RbjSection"/>'s and <see cref="KFilter"/>'s arithmetic in order, never fused, so each channel is bit-identical to its own filter.
/// </summary>
internal struct BiquadPair(double b0, double b1, double b2, double a1, double a2)
{
    private readonly Vector128<double> _b0 = Vector128.Create(b0), _b1 = Vector128.Create(b1), _b2 = Vector128.Create(b2), _a1 = Vector128.Create(a1), _a2 = Vector128.Create(a2);
    private Vector128<double> _x1, _x2, _y1, _y2;

    public Vector128<double> Next(Vector128<double> x)
    {
        var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
        return y;
    }

    /// <summary>Flushes denormals per lane.</summary>
    public void Flush() => (_x1, _x2, _y1, _y2) = (Flushed(_x1), Flushed(_x2), Flushed(_y1), Flushed(_y2));

    /// <summary>Clears history, keeping coefficients (seek).</summary>
    public void Clear() => (_x1, _x2, _y1, _y2) = (default, default, default, default);

    private static Vector128<double> Flushed(Vector128<double> value) =>
        Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(value), Vector128.Create(1e-25)), Vector128<double>.Zero, value);
}

/// <summary>Studio Voice (see file header): the vocal chain blended with the delayed dry signal; constant latency.</summary>
internal sealed class StudioVoiceEffect : AudioEffect
{
    /// <summary>Chain latency in samples (the spectral stage frame, DenoiseEffect.Size); the dry signal is delayed by the same amount.</summary>
    public const int Latency = DenoiseEffect.Size;

    private const int Size = DenoiseEffect.Size, Hop = DenoiseEffect.Hop, Bins = DenoiseEffect.Bins;

    // Internal processing block (mixer blocks are split into these); all buffers preallocated.
    private const int Chunk = 256;

    // Maximum spectral attenuation per bin: 18 dB.
    private const float Floor = 0.125f;

    // Late-reverb model: a bin's value this long ago, decayed per this reverberation time.
    private const double ReverbDelaySeconds = 0.06, ReverbTimeSeconds = 0.3;

    // Presence: 4 kHz peak, plus the added share of soft 3-5 kHz harmonics.
    private const double PresenceHz = 4000, PresenceDb = 2.5, ExciterHz = 3500, ExciterAmount = 0.6, ExciterDrive = 3;

    // Leveller: target speech level (dBFS RMS), correction share and knee, boost/cut limits, and the gate below which it holds (pauses are
    // not raised); peak catch threshold (dBFS), ratio and knee.
    private const double TargetDb = -20, Levelling = 0.8, LevelKneeDb = 4, MostBoostDb = 9, MostCutDb = 12, GateDb = -45;
    private const double PeakThresholdDb = -4, PeakRatio = 6, PeakKneeDb = 4;

    private readonly float _mix;
    private readonly AudioMixKeys? _keys;
    private readonly int _halfWindow, _reverbFrames;
    private readonly float _roomDecay;
    private readonly double _levelFollow, _cutFollow, _boostFollow, _peakAttack, _peakRelease, _gateSquare, _peakKneeStart;
    private float _blockFrom, _blockTo, _last = float.NaN;
    private Chain _chain;

    public StudioVoiceEffect(in AudioEffectSpec spec, int sampleRate)
    {
        _mix = (float)Math.Clamp(spec.P1, 0, 1);
        _keys = spec.Keys is { Keys.Count: > 0 } keys ? keys : null;
        _halfWindow = Math.Max((int)Math.Round(sampleRate * 0.6 / Hop), 8);
        _reverbFrames = Math.Max((int)Math.Round(ReverbDelaySeconds * sampleRate / Hop), 4);
        _roomDecay = (float)Math.Pow(10, -6 * (_reverbFrames * (double)Hop / sampleRate) / ReverbTimeSeconds);
        _levelFollow = 1 - Math.Exp(-1 / (0.4 * sampleRate));
        _cutFollow = 1 - Math.Exp(-1 / (0.3 * sampleRate));
        _boostFollow = 1 - Math.Exp(-1 / (1.5 * sampleRate));
        _peakAttack = 1 - Math.Exp(-1 / (0.001 * sampleRate));
        _peakRelease = Math.Exp(-1 / (0.08 * sampleRate));
        _gateSquare = Math.Pow(10, GateDb / 10);
        _peakKneeStart = Math.Pow(10, (PeakThresholdDb - PeakKneeDb / 2) / 20);
        _blockFrom = _blockTo = MixAt(0);
        _chain = new Chain(sampleRate, _reverbFrames);
        VoiceModels.LookOnce();
    }

    /// <summary>All chain state carried between blocks; handed to a replacement effect with new settings (TakeState).</summary>
    private sealed class Chain
    {
        public Chain(int sampleRate, int reverbFrames)
        {
            for (var c = 0; c < 2; c++)
                Channels[c] = new Channel(reverbFrames);
            (HighPass, LowPass) = (RbjSection.HighPass(80, 0.7071, sampleRate).Paired(), RbjSection.LowPass(13000, 0.5, sampleRate).Paired());
            (Presence, Exciter) = (RbjSection.Peaking(PresenceHz, 0.9, PresenceDb, sampleRate).Paired(), RbjSection.BandPass(ExciterHz, 0.7, sampleRate).Paired());
        }

        public readonly Channel[] Channels = new Channel[2];

        // Both channels' filters, one lane each.
        public BiquadPair HighPass, LowPass, Presence, Exciter;

        // Delayed dry signal (interleaved, Latency frames); the block's dry/wet (interleaved) and band/cleaned buffers (per channel: Chunk
        // left samples, then Chunk right).
        public readonly float[] Dry = new float[2 * Latency];
        public readonly float[] Held = new float[2 * Chunk], Wet = new float[2 * Chunk], Band = new float[2 * Chunk], Cleaned = new float[2 * Chunk];
        public int DryAt;

        // Leveller: mean square, current and target gain (dB), peak envelope, and a counter for target updates.
        public double Level, GainDb, Heading, Peak;
        public int Tick;

        public void Clear()
        {
            for (var c = 0; c < 2; c++)
                Channels[c].Clear();
            HighPass.Clear();
            LowPass.Clear();
            Presence.Clear();
            Exciter.Clear();
            Array.Clear(Dry);
            (DryAt, Level, GainDb, Heading, Peak, Tick) = (0, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>One channel's spectral stage (DenoiseEffect frames) plus each bin's history over the reverb delay.</summary>
    private sealed class Channel(int reverbFrames)
    {
        public readonly float[] Input = new float[Size], Output = new float[Size], Ready = new float[Hop];
        public readonly float[] Re = new float[Size], Im = new float[Size];
        public readonly float[] Power = new float[Bins], Minimum = new float[Bins], Previous = new float[Bins], Gain = new float[Bins], Post = new float[Bins];
        public readonly float[] Late = new float[reverbFrames * Bins];
        public int Filled, Frames;
        public bool Started;

        public void Clear()
        {
            Array.Clear(Input);
            Array.Clear(Output);
            Array.Clear(Ready);
            Array.Clear(Late);
            (Filled, Frames, Started) = (0, 0, false);
        }
    }

    // Mix at a sequence time, 0..1: keyframes (percent) when present, else the static value.
    private float MixAt(double sequenceSeconds) => _keys is { } keys ? (float)Math.Clamp(keys.ValueAt(sequenceSeconds) / 100, 0, 1) : _mix;

    public override void Moment(double fromSeconds, double toSeconds) => (_blockFrom, _blockTo) = (MixAt(fromSeconds), MixAt(toSeconds));

    public override void Process(Span<float> stereo, int frames)
    {
        if (frames <= 0)
            return;

        // Ramp from where the last block ended (a replacement effect inherits it via TakeState) to this block's end.
        var from = float.IsNaN(_last) ? _blockFrom : _last;
        var to = _blockTo;
        _last = to;
        for (var done = 0; done < frames; done += Chunk)
        {
            var count = Math.Min(Chunk, frames - done);
            Run(stereo.Slice(done * 2, count * 2), count, done, frames, from, to);
        }
    }

    private void Run(Span<float> block, int count, int offset, int frames, float from, float to)
    {
        var chain = _chain;
        var (held, wet, dry) = (chain.Held, chain.Wet, chain.Dry);

        // Delay the dry signal by the chain latency.
        var at = chain.DryAt;
        for (var i = 0; i < count; i++)
        {
            held[2 * i] = dry[2 * at];
            held[2 * i + 1] = dry[2 * at + 1];
            dry[2 * at] = block[2 * i];
            dry[2 * at + 1] = block[2 * i + 1];
            at = at + 1 == Latency ? 0 : at + 1;
        }

        chain.DryAt = at;

        // Voice band on both channels at once (one lane each); then each channel's spectral stage (a voice model of equal latency when
        // loaded); then presence, both channels at once.
        ref var highPass = ref chain.HighPass;
        ref var lowPass = ref chain.LowPass;
        var (band, cleaned) = (chain.Band, chain.Cleaned);
        for (var i = 0; i < count; i++)
        {
            var y = lowPass.Next(highPass.Next(Vector128.Create((double)block[2 * i], block[2 * i + 1])));
            (band[i], band[Chunk + i]) = ((float)y.GetElement(0), (float)y.GetElement(1));
        }

        highPass.Flush();
        lowPass.Flush();

        var model = VoiceModels.Current;
        for (var c = 0; c < 2; c++)
        {
            if (model is { Latency: StudioVoiceEffect.Latency })
            {
                model.Clean(band.AsSpan(c * Chunk, count), cleaned.AsSpan(c * Chunk, count), c);
            }
            else
            {
                var channel = chain.Channels[c];
                for (var i = c * Chunk; i < c * Chunk + count; i++)
                {
                    channel.Input[Size - Hop + channel.Filled] = band[i];
                    cleaned[i] = channel.Ready[channel.Filled];
                    if (++channel.Filled == Hop)
                    {
                        Frame(channel);
                        channel.Filled = 0;
                    }
                }
            }
        }

        ref var presence = ref chain.Presence;
        ref var exciter = ref chain.Exciter;
        var (drive, amount) = (Vector128.Create(ExciterDrive), Vector128.Create(ExciterAmount));
        for (var i = 0; i < count; i++)
        {
            var y = Vector128.Create((double)cleaned[i], cleaned[Chunk + i]);
            var lifted = presence.Next(y);
            var e = exciter.Next(y);
            var driven = drive * e;
            var shaped = Vector128.Create(Math.Tanh(driven.GetElement(0)), Math.Tanh(driven.GetElement(1)));
            var sound = lifted + amount * (shaped / drive - e);
            (wet[2 * i], wet[2 * i + 1]) = ((float)sound.GetElement(0), (float)sound.GetElement(1));
        }

        presence.Flush();
        exciter.Flush();

        Level(chain, wet, count);

        // Blend ramped across the block: dry only at 0 (bit-exact), wet only at 1.
        if (from == 0 && to == 0)
        {
            held.AsSpan(0, count * 2).CopyTo(block);
            return;
        }

        if (from == 1 && to == 1)
        {
            wet.AsSpan(0, count * 2).CopyTo(block);
            return;
        }

        var step = (to - from) / frames;
        for (var i = 0; i < count; i++)
        {
            var mix = from + step * (offset + i + 1);
            block[2 * i] = held[2 * i] + (wet[2 * i] - held[2 * i]) * mix;
            block[2 * i + 1] = held[2 * i + 1] + (wet[2 * i + 1] - held[2 * i + 1]) * mix;
        }
    }

    // One spectral-stage frame: DenoiseEffect's gain with the room's late reverb as a second interference term. Bins 0..Size/2-1 four at a
    // time (per-lane arithmetic matches the scalar loop: bit-identical gains), the last bin scalar; bins above Size/2 take their mirror's gains.
    private void Frame(Channel channel)
    {
        var (re, im) = (channel.Re, channel.Im);
        DenoiseEffect.Windowed(channel.Input, re, im);
        DenoiseEffect.Transform(re, im, inverse: false);

        var newWindow = channel.Frames % _halfWindow == 0;
        var (late, lateAt, hasLate) = (channel.Late, channel.Frames % _reverbFrames * Bins, channel.Frames >= _reverbFrames);
        var started = channel.Started;
        Span<Vector128<float>> vectorRe = DenoiseEffect.Vectors(re), vectorIm = DenoiseEffect.Vectors(im);
        Span<Vector128<float>> powers = DenoiseEffect.Vectors(channel.Power), minimums = DenoiseEffect.Vectors(channel.Minimum), previouses = DenoiseEffect.Vectors(channel.Previous);
        Span<Vector128<float>> gains = DenoiseEffect.Vectors(channel.Gain), posts = DenoiseEffect.Vectors(channel.Post);
        var lates = MemoryMarshal.Cast<float, Vector128<float>>(late.AsSpan(lateAt, Size / 2));
        var (decay, one) = (Vector128.Create(_roomDecay), Vector128<float>.One);
        for (var v = 0; v < vectorRe.Length && v < vectorIm.Length && v < powers.Length && v < minimums.Length && v < previouses.Length && v < gains.Length && v < posts.Length && v < lates.Length; v++)
        {
            var (r, i) = (vectorRe[v], vectorIm[v]);
            var power = r * r + i * i;
            var smooth = started ? Vector128.Create(0.7f) * powers[v] + Vector128.Create(0.3f) * power : power;
            powers[v] = smooth;
            if (!started)
                (minimums[v], previouses[v], gains[v], posts[v]) = (smooth, smooth, one, one);
            else if (newWindow)
                (previouses[v], minimums[v]) = (minimums[v], smooth);
            else
                minimums[v] = Vector128.Min(minimums[v], smooth);

            var noise = Vector128.Min(Vector128.Max(Vector128.Min(minimums[v], previouses[v]) * DenoiseEffect.Bias, Vector128.Create(1e-12f)), Vector128.Create(DenoiseEffect.MostNoise));
            var room = hasLate ? decay * lates[v] : Vector128<float>.Zero;
            lates[v] = smooth;
            var post = power / (noise + room);
            var prior = Vector128.Create(0.98f) * gains[v] * gains[v] * posts[v] + Vector128.Create(0.02f) * Vector128.Max(post - one, Vector128<float>.Zero);
            var gain = Vector128.Max(prior / (one + prior), Vector128.Create(Floor));
            (gains[v], posts[v]) = (gain, post);
            (vectorRe[v], vectorIm[v]) = (r * gain, i * gain);
        }

        for (var b = Size / 2; b < Bins; b++)
        {
            var power = re[b] * re[b] + im[b] * im[b];
            var smooth = channel.Started ? 0.7f * channel.Power[b] + 0.3f * power : power;
            channel.Power[b] = smooth;
            if (!channel.Started)
                (channel.Minimum[b], channel.Previous[b], channel.Gain[b], channel.Post[b]) = (smooth, smooth, 1, 1);
            else if (newWindow)
                (channel.Previous[b], channel.Minimum[b]) = (channel.Minimum[b], smooth);
            else
                channel.Minimum[b] = Math.Min(channel.Minimum[b], smooth);

            // Interference: steady noise plus room (the bin's value one reverb delay ago, decayed).
            var noise = Math.Clamp(Math.Min(channel.Minimum[b], channel.Previous[b]) * DenoiseEffect.Bias, 1e-12f, DenoiseEffect.MostNoise);
            var room = hasLate ? _roomDecay * late[lateAt + b] : 0;
            late[lateAt + b] = smooth;
            var post = power / (noise + room);
            var prior = 0.98f * channel.Gain[b] * channel.Gain[b] * channel.Post[b] + 0.02f * Math.Max(post - 1, 0);
            var gain = Math.Max(prior / (1 + prior), Floor);
            (channel.Gain[b], channel.Post[b]) = (gain, post);
            re[b] *= gain;
            im[b] *= gain;
        }

        DenoiseEffect.Mirror(re, im, channel.Gain);
        channel.Started = true;
        channel.Frames++;
        DenoiseEffect.Transform(re, im, inverse: true);

        // Overlap-add as in DenoiseEffect.
        DenoiseEffect.OverlapAdd(re, channel.Output);
        Array.Copy(channel.Output, channel.Ready, Hop);
        Array.Copy(channel.Output, Hop, channel.Output, 0, Size - Hop);
        Array.Clear(channel.Output, Size - Hop, Hop);
        Array.Copy(channel.Input, Hop, channel.Input, 0, Size - Hop);
    }

    /// <summary>
    /// Stereo-linked broadcast leveller: slow RMS leveller (target updated every 32 samples, held during pauses) followed by a fast peak catch.
    /// </summary>
    private void Level(Chain chain, float[] wet, int count)
    {
        const double ln10Over20 = 0.11512925464970229;
        var (level, gainDb, targetDb, peak, tick) = (chain.Level, chain.GainDb, chain.Heading, chain.Peak, chain.Tick);
        for (var i = 0; i < count; i++)
        {
            double l = wet[2 * i], r = wet[2 * i + 1];
            level += (0.5 * (l * l + r * r) - level) * _levelFollow;
            if ((tick++ & 31) == 0 && level > _gateSquare)
                targetDb = Correction(TargetDb - 10 * Math.Log10(level));
            gainDb += (targetDb - gainDb) * (targetDb < gainDb ? _cutFollow : _boostFollow);
            var gain = Math.Exp(gainDb * ln10Over20);
            (l, r) = (l * gain, r * gain);

            // Peak catch: 1 ms attack, 80 ms release, soft knee around the threshold.
            var height = Math.Max(Math.Abs(l), Math.Abs(r));
            peak = height > peak ? peak + (height - peak) * _peakAttack : peak * _peakRelease;
            if (peak > _peakKneeStart)
            {
                var over = 20 * Math.Log10(peak) - PeakThresholdDb;
                var slope = 1 - 1 / PeakRatio;
                var reduction = over >= PeakKneeDb / 2 ? slope * over : slope * (over + PeakKneeDb / 2) * (over + PeakKneeDb / 2) / (2 * PeakKneeDb);
                var caught = Math.Exp(-reduction * ln10Over20);
                (l, r) = (l * caught, r * caught);
            }

            (wet[2 * i], wet[2 * i + 1]) = ((float)l, (float)r);
        }

        if (level < 1e-30)
            level = 0;
        if (peak < 1e-30)
            peak = 0;
        (chain.Level, chain.GainDb, chain.Heading, chain.Peak, chain.Tick) = (level, gainDb, targetDb, peak, tick);
    }

    // Leveller correction for an offset in dB: most of the way, soft near the target (knee), within the boost/cut limits.
    private static double Correction(double offDb)
    {
        var size = Math.Abs(offDb);
        var moved = size <= LevelKneeDb ? size * size / (2 * LevelKneeDb) : size - LevelKneeDb / 2;
        return Math.Clamp(Math.Sign(offDb) * moved * Levelling, -MostCutDb, MostBoostDb);
    }

    public override void TakeState(AudioEffect previous)
    {
        if (previous is StudioVoiceEffect old)
            (_chain, _last) = (old._chain, old._last);
    }

    public override void Reset()
    {
        _chain.Clear();
        VoiceModels.Current?.Reset();
        _last = float.NaN;
    }
}

/// <summary>De-Esser (see file header): split-band compressor on 5.5-9 kHz with zero latency.</summary>
internal sealed class DeEsserEffect : AudioEffect
{
    /// <summary>Band edges in Hz (the band-pass -3 dB points).</summary>
    public const double LowHz = 5500, HighHz = 9000;

    private const double KneeDb = 6, MostReductionDb = 24;

    private readonly double _thresholdDb, _slope, _kneeStart, _attack, _release;
    private RbjSection _bandLeft, _bandRight, _sideLeft, _sideRight;
    private double _envelope;

    /// <summary>P1 amount (0..1) sets threshold (-18 to -40 dBFS) and ratio (1 to 8); 0 is a no-op.</summary>
    public DeEsserEffect(in AudioEffectSpec spec, int sampleRate)
    {
        var amount = Math.Clamp(spec.P1, 0, 1);
        _thresholdDb = -18 - 22 * amount;
        _slope = 1 - 1 / (1 + 7 * amount);
        _kneeStart = Math.Pow(10, (_thresholdDb - KneeDb / 2) / 20);
        _attack = 1 - Math.Exp(-1 / (0.001 * sampleRate));
        _release = 1 - Math.Exp(-1 / (0.060 * sampleRate));

        // Band: constant-0-dB-peak band-pass at the edges' geometric centre (~7 kHz), Q = centre / band width, so at the centre it is unity and
        // in phase and the cut there equals the computed reduction. The side chain applies the same band-pass again (unity at centre, steeper
        // skirts) so loud vocal mids stay below the knee.
        var centre = Math.Sqrt(LowHz * HighHz);
        _bandLeft = _bandRight = _sideLeft = _sideRight = RbjSection.BandPass(centre, centre / (HighHz - LowHz), sampleRate);
    }

    public override void Process(Span<float> stereo, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            double l = stereo[2 * i], r = stereo[2 * i + 1];
            var bandLeft = _bandLeft.Next(l);
            var bandRight = _bandRight.Next(r);
            var level = Math.Max(Math.Abs(_sideLeft.Next(bandLeft)), Math.Abs(_sideRight.Next(bandRight)));
            _envelope += (level - _envelope) * (level > _envelope ? _attack : _release);

            // Below the knee the signal passes bit-exact.
            if (_slope <= 0 || _envelope <= _kneeStart)
                continue;

            var over = 20 * Math.Log10(_envelope) - _thresholdDb;
            var reduction = Math.Min(over >= KneeDb / 2 ? _slope * over : _slope * (over + KneeDb / 2) * (over + KneeDb / 2) / (2 * KneeDb), MostReductionDb);
            if (reduction <= 0)
                continue;

            var taken = 1 - Math.Pow(10, -reduction / 20);
            stereo[2 * i] = (float)(l - bandLeft * taken);
            stereo[2 * i + 1] = (float)(r - bandRight * taken);
        }

        _bandLeft.Flush();
        _bandRight.Flush();
        _sideLeft.Flush();
        _sideRight.Flush();
        if (_envelope < 1e-30)
            _envelope = 0;
    }

    public override void TakeState(AudioEffect previous)
    {
        if (previous is DeEsserEffect old)
            (_bandLeft, _bandRight, _sideLeft, _sideRight, _envelope) = (old._bandLeft, old._bandRight, old._sideLeft, old._sideRight, old._envelope);
    }

    public override void Reset()
    {
        _bandLeft.Clear();
        _bandRight.Clear();
        _sideLeft.Clear();
        _sideRight.Clear();
        _envelope = 0;
    }
}

/// <summary>
/// Voice model that can replace Studio Voice's spectral stage (e.g. DeepFilterNet3 on an NPU): streams one channel's band-limited audio with
/// <see cref="Latency"/> samples of delay at 48 kHz (used only when equal to StudioVoiceEffect.Latency, keeping the dry path aligned). Called
/// on the mixer thread: must never block, wait or allocate.
/// </summary>
internal interface IVoiceModel
{
    string Name { get; }

    int Latency { get; }

    /// <summary>Cleans one block of one channel (<paramref name="channel"/> 0 left, 1 right) into <paramref name="output"/> (same length).</summary>
    void Clean(ReadOnlySpan<float> input, Span<float> output, int channel);

    /// <summary>Discontinuity (seek): clears history.</summary>
    void Reset();
}

/// <summary>
/// Voice-model discovery: a DeepFilterNet3 model in deps\models plus an ONNX Runtime next to HandPeg. None ships: on first Studio Voice use it
/// checks once on the thread pool (nothing at startup or on the mixer thread), logs the result, and the native chain runs
/// (<see cref="Current"/> stays null).
/// </summary>
internal static class VoiceModels
{
    /// <summary>Model file name pattern in deps\models.</summary>
    public const string ModelPattern = "DeepFilterNet3*.onnx";

    private static int _looked;
    // Explicit null: the .NET 10 compiler does not treat Volatile.Read's ref as a write (CS0649); no model ever loads in this build.
    private static IVoiceModel? _model = null;

    /// <summary>Active voice model; always null in this build (the native chain runs).</summary>
    public static IVoiceModel? Current => Volatile.Read(ref _model);

    /// <summary>Checks for a model once, on the thread pool.</summary>
    public static void LookOnce()
    {
        if (Interlocked.Exchange(ref _looked, 1) == 0)
            ThreadPool.QueueUserWorkItem(_ => Look());
    }

    private static void Look()
    {
        try
        {
            var (model, runtime) = Locate();
            AppLog.Write(model is null
                ? $"Studio Voice: no voice model in {Path.Combine(AppPaths.Deps, "models")} ({ModelPattern}); the native chain runs."
                : runtime is null
                    ? $"Studio Voice: {Path.GetFileName(model)} is there, but no ONNX Runtime is (HandPeg ships none); the native chain runs."
                    : $"Studio Voice: {Path.GetFileName(model)} and {Path.GetFileName(runtime)} are there, but this HandPeg does not load a voice model; the native chain runs.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            AppLog.Write("Studio Voice: the voice model could not be looked for; the native chain runs", ex);
        }
    }

    /// <summary>Model file (deps\models\DeepFilterNet3*.onnx) and ONNX Runtime (onnxruntime.dll in deps, deps\onnxruntime or the app folder); each null when absent.</summary>
    internal static (string? Model, string? Runtime) Locate()
    {
        var models = Path.Combine(AppPaths.Deps, "models");
        var model = Directory.Exists(models) ? Directory.EnumerateFiles(models, ModelPattern).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
        foreach (var folder in new[] { AppPaths.Deps, Path.Combine(AppPaths.Deps, "onnxruntime"), AppContext.BaseDirectory })
        {
            var candidate = Path.Combine(folder, "onnxruntime.dll");
            if (File.Exists(candidate))
                return (model, candidate);
        }

        return (model, null);
    }
}
