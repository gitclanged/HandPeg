using System.IO;

namespace HandPegApp.Services.Audio;

// ----- Studio Voice and the De-Esser (Pass 5.2B) -----
//
// Studio Voice is the one-click vocal chain, a strip's effect like the others (AudioChainBuilder: after the cleaning, before the dynamics),
// heard while editing and exported alike, run on the mixer's thread with everything it holds made beforehand. Its wet sound, per side:
// the voice's band (a high-pass at 80 Hz, a gentle low-pass at 13 kHz); the spectral stage, DenoiseEffect's short-time Fourier transform and
// noise floor (minimum statistics, a decision-directed Wiener gain) with a second thing in the way, the room's late reverb (what the band
// held 60 ms before, decayed as a small room decays it), so the steady hiss and the tail of the room go and the voice stays; a presence lift
// (a gentle peak at 4 kHz, and the soft harmonics of the 3-5 kHz band); then, both sides together, a soft-knee broadcast leveller (a slow RMS
// leveller toward a speech level, held in pauses, and a fast catch of the peaks). The dry sound is held back as long as the spectral stage
// holds the wet one (Latency), and the two are blended by the mix (TrackAudioFilters.StudioVoiceMix, or its keyframes on the Sequence's
// clock: the mixer hands every block its moments, AudioEffect.Moment), ramped across each block so a change never clicks. A mix of 0 with
// keys is the input held back by Latency, to the bit; without keys the builder leaves the effect out.
//
// The spectral stage can be a voice model's instead (IVoiceModel: DeepFilterNet3 on an NPU, its file in deps\models). HandPeg ships no
// runtime for one: VoiceModels looks once, off every thread that matters, the first time Studio Voice is used, says so in the log, and the
// native chain runs.
//
// The De-Esser is a split-band compressor on 5.5 to 9 kHz: the band (a constant-0-dB-peak band-pass of the sound at about 7 kHz, unity and in
// phase at its centre) through the same band-pass again is the side chain, followed fast (1 ms) and let go of slowly (60 ms), and only the
// band is taken down (the sound less the band times 1 - gain, so at the centre the cut is the reduction computed); its amount sets the
// threshold and the ratio. No latency: below its knee it leaves the sound as it is, to the bit.

/// <summary>One RBJ cookbook biquad in double precision with its own state (5.2B: the vocal effects' and the band follower's building block).</summary>
internal struct RbjSection
{
    private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

    private static RbjSection Of(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new() { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };

    // The cookbook's w0 and alpha, the frequency held under the rate's Nyquist.
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

    /// <summary>A band-pass whose peak is 0 dB (the cookbook's constant 0 dB peak gain).</summary>
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

    /// <summary>Denormals let go of (the silence after a sound would slow the processor to a crawl).</summary>
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

    /// <summary>Its history cleared, its coefficients kept (a seek).</summary>
    public void Clear() => (_x1, _x2, _y1, _y2) = (0, 0, 0, 0);
}

/// <summary>Studio Voice (see the top of this file): the vocal chain blended with the dry sound, its latency constant.</summary>
internal sealed class StudioVoiceEffect : AudioEffect
{
    /// <summary>How late the chain's sound comes out, in samples (the spectral stage's frame, DenoiseEffect's): the dry sound is held back as long.</summary>
    public const int Latency = DenoiseEffect.Size;

    private const int Size = DenoiseEffect.Size, Hop = DenoiseEffect.Hop, Bins = DenoiseEffect.Bins;

    // The block the chain works in (any block the mixer gives is cut into these): what it holds is made once.
    private const int Chunk = 256;

    // The most the spectral stage takes away from a bin: 18 dB.
    private const float Floor = 0.125f;

    // The room's late reverb: what a bin held this long before, decayed as a room of this reverberation time decays it.
    private const double ReverbDelaySeconds = 0.06, ReverbTimeSeconds = 0.3;

    // The presence: a peak at 4 kHz, and how much of the 3-5 kHz band's soft harmonics is added.
    private const double PresenceHz = 4000, PresenceDb = 2.5, ExciterHz = 3500, ExciterAmount = 0.6, ExciterDrive = 3;

    // The leveller: the speech level it brings the voice toward (dBFS RMS), how much of the way (and its knee), how far either way, and
    // below what it holds (a pause is not made louder); the peak catch's threshold (dBFS), ratio and knee.
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

    /// <summary>Everything the chain holds from block to block: what a new effect of new settings takes over (TakeState).</summary>
    private sealed class Chain
    {
        public Chain(int sampleRate, int reverbFrames)
        {
            for (var c = 0; c < 2; c++)
            {
                Channels[c] = new Channel(reverbFrames);
                (HighPass[c], LowPass[c]) = (RbjSection.HighPass(80, 0.7071, sampleRate), RbjSection.LowPass(13000, 0.5, sampleRate));
                (Presence[c], Exciter[c]) = (RbjSection.Peaking(PresenceHz, 0.9, PresenceDb, sampleRate), RbjSection.BandPass(ExciterHz, 0.7, sampleRate));
            }
        }

        public readonly Channel[] Channels = new Channel[2];
        public readonly RbjSection[] HighPass = new RbjSection[2], LowPass = new RbjSection[2], Presence = new RbjSection[2], Exciter = new RbjSection[2];

        // The dry sound held back (interleaved, Latency frames), and the block's dry, band, cleaned and wet sound.
        public readonly float[] Dry = new float[2 * Latency];
        public readonly float[] Held = new float[2 * Chunk], Wet = new float[2 * Chunk], Band = new float[Chunk], Cleaned = new float[Chunk];
        public int DryAt;

        // The leveller: its mean square, its gain in dB and the one it is heading for, the peak catch's envelope, and a count for how often
        // the target is worked out.
        public double Level, GainDb, Heading, Peak;
        public int Tick;

        public void Clear()
        {
            for (var c = 0; c < 2; c++)
            {
                Channels[c].Clear();
                HighPass[c].Clear();
                LowPass[c].Clear();
                Presence[c].Clear();
                Exciter[c].Clear();
            }

            Array.Clear(Dry);
            (DryAt, Level, GainDb, Heading, Peak, Tick) = (0, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>One side's spectral stage (DenoiseEffect's frames), and what each bin held over the last reverb delay.</summary>
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

    // The mix at a moment of the Sequence, 0 to 1: its keys' (percent), else the plain one.
    private float MixAt(double sequenceSeconds) => _keys is { } keys ? (float)Math.Clamp(keys.ValueAt(sequenceSeconds) / 100, 0, 1) : _mix;

    public override void Moment(double fromSeconds, double toSeconds) => (_blockFrom, _blockTo) = (MixAt(fromSeconds), MixAt(toSeconds));

    public override void Process(Span<float> stereo, int frames)
    {
        if (frames <= 0)
            return;

        // The block's mix, from where the last block ended (a slider moved: this effect took over the last one's) to where this one ends.
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

        // The dry sound, held back as long as the chain holds its own.
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

        // Each side: the voice's band, the spectral stage (a voice model's when one is there, of the same latency), the presence.
        var model = VoiceModels.Current;
        for (var c = 0; c < 2; c++)
        {
            ref var highPass = ref chain.HighPass[c];
            ref var lowPass = ref chain.LowPass[c];
            var band = chain.Band;
            for (var i = 0; i < count; i++)
                band[i] = (float)lowPass.Next(highPass.Next(block[2 * i + c]));
            highPass.Flush();
            lowPass.Flush();

            var cleaned = chain.Cleaned;
            if (model is { Latency: StudioVoiceEffect.Latency })
            {
                model.Clean(band.AsSpan(0, count), cleaned.AsSpan(0, count), c);
            }
            else
            {
                var channel = chain.Channels[c];
                for (var i = 0; i < count; i++)
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

            ref var presence = ref chain.Presence[c];
            ref var exciter = ref chain.Exciter[c];
            for (var i = 0; i < count; i++)
            {
                var y = cleaned[i];
                var lifted = presence.Next(y);
                var e = exciter.Next(y);
                wet[2 * i + c] = (float)(lifted + ExciterAmount * (Math.Tanh(ExciterDrive * e) / ExciterDrive - e));
            }

            presence.Flush();
            exciter.Flush();
        }

        Level(chain, wet, count);

        // The blend, ramped across the whole block: the dry sound alone at 0, to the bit; the chain's alone at 1.
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

    // One frame of the spectral stage: DenoiseEffect's (a sound above the floor passes), with the room's late reverb as a second interference.
    private void Frame(Channel channel)
    {
        var (re, im) = (channel.Re, channel.Im);
        var window = DenoiseEffect.Window;
        for (var n = 0; n < Size; n++)
            (re[n], im[n]) = (channel.Input[n] * window[n], 0);
        DenoiseEffect.Transform(re, im, inverse: false);

        var newWindow = channel.Frames % _halfWindow == 0;
        var (late, lateAt, hasLate) = (channel.Late, channel.Frames % _reverbFrames * Bins, channel.Frames >= _reverbFrames);
        for (var b = 0; b < Bins; b++)
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

            // What is in the way: the steady noise, and the room (what the bin held a reverb delay ago, decayed).
            var noise = Math.Clamp(Math.Min(channel.Minimum[b], channel.Previous[b]) * DenoiseEffect.Bias, 1e-12f, DenoiseEffect.MostNoise);
            var room = hasLate ? _roomDecay * late[lateAt + b] : 0;
            late[lateAt + b] = smooth;
            var post = power / (noise + room);
            var prior = 0.98f * channel.Gain[b] * channel.Gain[b] * channel.Post[b] + 0.02f * Math.Max(post - 1, 0);
            var gain = Math.Max(prior / (1 + prior), Floor);
            (channel.Gain[b], channel.Post[b]) = (gain, post);
            re[b] *= gain;
            im[b] *= gain;
            if (b > 0 && b < Size / 2)
            {
                re[Size - b] *= gain;
                im[Size - b] *= gain;
            }
        }

        channel.Started = true;
        channel.Frames++;
        DenoiseEffect.Transform(re, im, inverse: true);

        // Overlap-added as DenoiseEffect's frames are: square-root Hann twice is Hann, four a quarter apart sum to 2.
        var output = channel.Output;
        for (var n = 0; n < Size; n++)
            output[n] += re[n] * window[n] * (0.5f / Size);
        Array.Copy(output, channel.Ready, Hop);
        Array.Copy(output, Hop, output, 0, Size - Hop);
        Array.Clear(output, Size - Hop, Hop);
        Array.Copy(channel.Input, Hop, channel.Input, 0, Size - Hop);
    }

    /// <summary>
    /// The broadcast leveller on both sides together: the slow RMS leveller (its target worked out every 32 samples, held while the voice
    /// pauses) and the fast peak catch over it.
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

            // The peaks: caught within a millisecond, let go of over 80; a soft knee round the threshold.
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

    // How far the leveller moves a voice that is off its target by so many dB: most of the way, gently near the target (a soft knee), within its reach.
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

/// <summary>The De-Esser (see the top of this file): a split-band compressor on 5.5 to 9 kHz, no latency.</summary>
internal sealed class DeEsserEffect : AudioEffect
{
    /// <summary>The band it works on, Hz: the band-pass's -3 dB edges.</summary>
    public const double LowHz = 5500, HighHz = 9000;

    private const double KneeDb = 6, MostReductionDb = 24;

    private readonly double _thresholdDb, _slope, _kneeStart, _attack, _release;
    private RbjSection _bandLeft, _bandRight, _sideLeft, _sideRight;
    private double _envelope;

    /// <summary>P1, the amount (0 to 1), sets the threshold (-18 to -40 dBFS) and the ratio (1 to 8): 0 takes nothing away.</summary>
    public DeEsserEffect(in AudioEffectSpec spec, int sampleRate)
    {
        var amount = Math.Clamp(spec.P1, 0, 1);
        _thresholdDb = -18 - 22 * amount;
        _slope = 1 - 1 / (1 + 7 * amount);
        _kneeStart = Math.Pow(10, (_thresholdDb - KneeDb / 2) / 20);
        _attack = 1 - Math.Exp(-1 / (0.001 * sampleRate));
        _release = 1 - Math.Exp(-1 / (0.060 * sampleRate));

        // The band: a constant-0-dB-peak band-pass at the edges' geometric centre (about 7 kHz), as wide as the band (Q the centre over
        // 3500 Hz), so at its centre it is the sound itself, unity and in phase, and the cut taken there is the reduction computed. The side
        // chain hears the band through the same band-pass again: unity at the centre too, with steeper skirts, so a voice's loud mids stay
        // under the knee and leave the sound as it was.
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

            // Below the knee the sound is left as it was, to the bit.
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
/// A voice model that can stand in for Studio Voice's spectral stage (an NPU's DeepFilterNet3): it cleans one side's band-limited sound
/// as a stream, as late as <see cref="Latency"/> samples at 48 kHz (only one exactly as late as the native stage, StudioVoiceEffect.Latency,
/// is used, so the dry sound stays in step). Called on the mixer's thread: it must never block, wait or allocate.
/// </summary>
internal interface IVoiceModel
{
    string Name { get; }

    int Latency { get; }

    /// <summary>One block of one side (<paramref name="channel"/> 0 left, 1 right), cleaned into <paramref name="output"/> (the same length).</summary>
    void Clean(ReadOnlySpan<float> input, Span<float> output, int channel);

    /// <summary>A jump (a seek): its history let go of.</summary>
    void Reset();
}

/// <summary>
/// Where a voice model would come from (5.2B): a DeepFilterNet3 model in deps\models and an ONNX Runtime beside HandPeg. HandPeg ships no
/// runtime: the first time Studio Voice is used it looks once, on the thread pool (nothing is read at start-up, nothing on the mixer's
/// thread), says what it found in the log, and the native chain runs (<see cref="Current"/> stays null).
/// </summary>
internal static class VoiceModels
{
    /// <summary>The model's file name, in deps\models.</summary>
    public const string ModelPattern = "DeepFilterNet3*.onnx";

    private static int _looked;
    private static IVoiceModel? _model;

    /// <summary>The voice model in use; null (the native chain) while none is loaded, which, in this HandPeg, is always.</summary>
    public static IVoiceModel? Current => Volatile.Read(ref _model);

    /// <summary>Looks for a model once, off the calling thread.</summary>
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

    /// <summary>The model's file (deps\models\DeepFilterNet3*.onnx) and an ONNX Runtime (onnxruntime.dll in deps, deps\onnxruntime or beside HandPeg), each null when not there.</summary>
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
