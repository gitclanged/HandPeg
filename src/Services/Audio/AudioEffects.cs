using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using HandPegApp.Models;

namespace HandPegApp.Services.Audio;

// ----- A strip's processing (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// Each effect mirrors the FFmpeg filter the graph used for the same setting until 5.2-Audit, with the filter's own defaults
// for everything HandPeg leaves out, so that what is heard while editing is what the export encodes (the export is the
// engine's render too): highpass / bass / treble / equalizer are af_biquads' RBJ biquads (bass and treble with FFmpeg's own
// width, a Q times the square root of two: measured against HandPeg's pinned build to the last bit), acompressor and the
// ducking are af_sidechaincompress, agate is af_agate, extrastereo and volume are what they are, alimiter is af_alimiter line
// for line (its 239-sample delay at 48 kHz taken back by the mixer since Pass 5.2D: alimiter with latency=1).
// afftdn is the engine's own spectral noise reduction (DenoiseEffect, 5.2-Audit: nothing is FFmpeg's).
//
// A strip's chain is made of a track's settings (TrackAudioFilters) by AudioChainBuilder (5.2-AuditB: typed, no filter text
// in between): one source of truth, the settings themselves. Studio Voice and the De-Esser (5.2B) are HandPeg's own, in
// StudioVoice.cs; a keyed effect follows the Sequence by the moments the mixer hands it before each block (AudioEffect.Moment).

public enum AudioEffectKind
{
    /// <summary>P1: the factor.</summary>
    Gain,

    /// <summary>
    /// Biquads. P1: frequency (Hz), P2: width, P3: width type (FFmpeg's numbers: 1 Hz, 2 octaves, 3 Q, 4 slope, 5 kHz), P4:
    /// gain (dB), P5: 1 for FFmpeg's bass and treble, whose bandwidth term (the cookbook's alpha) is divided by the square
    /// root of two (a Q of 0.5 is heard as 0.707); 0 for lowshelf and highshelf, the cookbook's own.
    /// </summary>
    HighPass,
    LowPass,
    LowShelf,
    HighShelf,
    Peaking,

    /// <summary>acompressor. P1: threshold (factor), P2: ratio, P3: attack (ms), P4: release (ms), P5: knee, P6: makeup, P7: mix, P8: level in.</summary>
    Compressor,

    /// <summary>agate. P1: threshold (factor), P2: ratio, P3: attack (ms), P4: release (ms), P5: range (factor), P6: knee, P7: makeup, P8: level in.</summary>
    Gate,

    /// <summary>alimiter. P1: limit (factor), P2: attack (ms), P3: release (ms), P4: level in, P5: the output's factor (level_out, over the limit with auto level).</summary>
    Limiter,

    /// <summary>extrastereo. P1: m (the difference's factor), P2: clip (1 on).</summary>
    Widener,

    /// <summary>afftdn's place (5.2-Audit): spectral noise reduction. P1: the most it takes away, dB (afftdn's nr).</summary>
    Denoise,

    /// <summary>
    /// Studio Voice (5.2B, StudioVoice.cs): the one-click vocal chain blended with the dry sound. P1: the mix (0 to 1); or, keyed,
    /// <see cref="AudioEffectSpec.Keys"/> (percent, on the Sequence's clock from their origin).
    /// </summary>
    StudioVoice,

    /// <summary>De-Esser (5.2B, StudioVoice.cs): a split-band compressor on 5.5 to 9 kHz. P1: how hard it works (0 to 1).</summary>
    DeEsser,
}

/// <summary>One effect of a strip with every raw parameter (see <see cref="AudioEffectKind"/> for what P1..P8 are).</summary>
public readonly record struct AudioEffectSpec(AudioEffectKind Kind, double P1 = 0, double P2 = 0, double P3 = 0, double P4 = 0, double P5 = 0, double P6 = 0, double P7 = 0, double P8 = 0)
{
    /// <summary>A value's keyframes on the Sequence's clock (5.2B: Studio Voice's mix), or null for none: equal by value, as the rest is.</summary>
    public AudioMixKeys? Keys { get; init; }

    public static AudioEffectSpec Gain(double factor) => new(AudioEffectKind.Gain, factor);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}({P1:0.####}, {P2:0.####}, {P3:0.####}, {P4:0.####}, {P5:0.####}, {P6:0.####}, {P7:0.####}, {P8:0.####})");
}

/// <summary>
/// A value's keyframes on the Sequence's clock (5.2B: Studio Voice's mix, TrackAudioFilters.StudioVoiceMixKeys): their times seconds from
/// <see cref="Origin"/> (a clip's Start for a clip's filters, 0 for a track's), in order. Equal by value, so a strip whose keys did not change
/// keeps its effects as they are (AudioMixer.SetProgram compares the specs). Read on the mixer's thread without allocating.
/// </summary>
public sealed class AudioMixKeys : IEquatable<AudioMixKeys>
{
    private readonly Keyframe[] _keys;

    public AudioMixKeys(IEnumerable<Keyframe> keys, double origin)
    {
        _keys = [.. keys.Where(k => double.IsFinite(k.Time) && double.IsFinite(k.Value)).OrderBy(k => k.Time)];
        Origin = double.IsFinite(origin) ? origin : 0;
    }

    public IReadOnlyList<Keyframe> Keys => _keys;

    /// <summary>The second of the Sequence the keys' time 0 is.</summary>
    public double Origin { get; }

    /// <summary>The keyed value at a moment of the Sequence (EasingCurves, as every keyframed value is); 0 with no keys.</summary>
    public double ValueAt(double sequenceSeconds) => _keys.Length == 0 ? 0 : EasingCurves.ValueAt(_keys, sequenceSeconds - Origin);

    public bool Equals(AudioMixKeys? other) => other is not null && Origin == other.Origin && _keys.AsSpan().SequenceEqual(other._keys.AsSpan());

    public override bool Equals(object? obj) => obj is AudioMixKeys other && Equals(other);

    // Every key whole (its time, value, easing, handles and part: the record's own hash) and the origin, so a key moved or changed anywhere
    // is a new hash (AudioReactive.KeyOf, which keeps the player's energy map while it holds). Allocates nothing.
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Origin);
        foreach (var key in _keys)
            hash.Add(key);
        return hash.ToHashCode();
    }
}

/// <summary>A strip's chain: its effects, and what changes a voice's time (pitch, tempo).</summary>
public sealed record AudioChain(AudioEffectSpec[] Effects, double Pitch, double Tempo)
{
    public static AudioChain None { get; } = new([], 1, 1);
}

/// <summary>
/// A track's filters and gain as a strip's chain (5.2-AuditB: typed; until then the chain was read back from the filters'
/// FFmpeg words, TrackAudioFilters.BuildChain, which nothing writes any more). What is switched on runs in a fixed order:
/// cleaning first (rumble, noise between words, steady noise), then dynamics, then tone, then the effects; the gain after
/// them, and the limiter last of all so that nothing before it can push the track into clipping. Each setting is held to its
/// range and to the four decimals the words held, and each parameter HandPeg leaves out is its FFmpeg filter's default, so a
/// strip's effects are the very numbers the words gave.
/// </summary>
public static class AudioChainBuilder
{
    // FFmpeg's width types for a biquad: q 3 (bass, treble), o 2 (an equalizer band an octave wide).
    private const int WidthQ = 3, WidthOctaves = 2;

    /// <summary>The chain of a track's (or a clip's) filters, null for none, and its gain.</summary>
    /// <param name="gainDb">The gain, in decibels; within 0.05 of 0 none.</param>
    /// <param name="keysOrigin">
    /// The second of the Sequence the filters' keyframes count from (5.2B: Studio Voice's mix keys): a clip's Start for a clip's filters,
    /// 0 for a track's.
    /// </param>
    public static AudioChain From(TrackAudioFilters? filters, double gainDb, double keysOrigin = 0)
    {
        var gained = Math.Abs(gainDb) >= 0.05;
        if (filters is not { Processes: true } f)
            return gained ? new AudioChain([AudioEffectSpec.Gain(FromDecibels(gainDb))], 1, 1) : AudioChain.None;

        var effects = new List<AudioEffectSpec>();
        if (f.HighPass)
            effects.Add(HighPassOf(f));
        if (f.NoiseGate)
            effects.Add(new AudioEffectSpec(AudioEffectKind.Gate, FromDecibels(Math.Clamp(f.GateThreshold, -80, 0)), 2, 20, 250, 0.06125, 2.828427125, 1, 1));
        if (f.DeNoise)
            effects.Add(new AudioEffectSpec(AudioEffectKind.Denoise, Written(Math.Clamp(f.DeNoiseStrength, 1, 60))));

        // Studio Voice (5.2B) after the cleaning, before the dynamics: left out at a mix of 0 with no keys, so the dry sound is untouched
        // (TrackAudioFilters.StudioVoiceInChain, the one test the strips' routing reads too, through Processes).
        if (f.StudioVoiceInChain)
        {
            var keys = f.StudioVoiceMixKeys is { Count: > 0 } keyed ? new AudioMixKeys(keyed, keysOrigin) : null;
            effects.Add(new AudioEffectSpec(AudioEffectKind.StudioVoice, Written(Math.Clamp(f.StudioVoiceMix, 0, 100)) / 100) { Keys = keys });
        }

        if (f.Compressor)
        {
            effects.Add(new AudioEffectSpec(AudioEffectKind.Compressor, FromDecibels(Math.Clamp(f.CompressorThreshold, -60, 0)),
                Written(Math.Clamp(f.CompressorRatio, 1, 20)), 20, 250, 2.82843, 1, 1, 1));
        }

        // The De-Esser (5.2B) after the dynamics (a compressor lifts the sibilance it then takes down), before the tone.
        if (f.DeEsser)
            effects.Add(new AudioEffectSpec(AudioEffectKind.DeEsser, Written(Math.Clamp(f.DeEsserAmount, 0, 100)) / 100));

        if (f.BassTreble)
        {
            // FFmpeg's bass and treble: shelves half a Q wide, their bandwidth term divided by the square root of two (P5 1).
            if (Math.Abs(f.BassGain) >= 0.05)
                effects.Add(BassOf(f));
            if (Math.Abs(f.TrebleGain) >= 0.05)
                effects.Add(TrebleOf(f));
        }

        if (f.Equalizer)
        {
            // One band per slider that has been moved, each an octave wide at its octave's centre unless it was moved and widened (5.2B).
            for (var band = 0; band < TrackAudioFilters.EqualizerBands.Count && band < f.EqualizerGains.Length; band++)
            {
                if (Math.Abs(f.EqualizerGains[band]) >= 0.05)
                    effects.Add(BandOf(f, band));
            }
        }

        var (pitch, tempo) = (1.0, 1.0);
        if (f.PitchTempo)
        {
            // The samples played faster raise the pitch and shorten the sound together (a rate of 48 kHz times the pitch, read
            // as 48 kHz); the tempo then puts the speed where it is wanted without touching the pitch again: one stretch covers
            // 0.5 to 100, slower than that takes a half first.
            var raised = Math.Pow(2, Math.Clamp(f.PitchSemitones, -12, 12) / 12);
            var speed = Math.Clamp(f.Tempo, 0.5, 2) / raised;
            if (Math.Abs(raised - 1) >= 0.001)
                pitch = Written(48000 * raised) / 48000;
            if (speed < 0.5)
                (tempo, speed) = (0.5, speed / 0.5);
            if (Math.Abs(speed - 1) >= 0.001)
                tempo *= Math.Clamp(Written(speed), 0.5, 100);
        }

        if (f.StereoWidener)
            effects.Add(new AudioEffectSpec(AudioEffectKind.Widener, Written(Math.Clamp(f.StereoWidth, 0, 4)), 1));
        if (gained)
            effects.Add(AudioEffectSpec.Gain(FromDecibels(gainDb)));

        // The limiter's ceiling as a factor, its output at that level (no auto level), FFmpeg's attack and release.
        if (f.Limiter)
            effects.Add(new AudioEffectSpec(AudioEffectKind.Limiter, Math.Clamp(Written(Math.Pow(10, Math.Clamp(f.LimiterCeiling, -24, 0) / 20)), 0.0625, 1), 5, 50, 1, 1));
        return new AudioChain([.. effects], pitch, tempo);
    }

    // The tone filters as the chain has them, one place for From and EqualizerResponseDb (each held to its range, written as the words were).
    private static AudioEffectSpec HighPassOf(TrackAudioFilters f) => new(AudioEffectKind.HighPass, Written(Math.Clamp(f.HighPassHz, 20, 1000)), 0.707, WidthQ);

    private static AudioEffectSpec BassOf(TrackAudioFilters f) => new(AudioEffectKind.LowShelf, 100, 0.5, WidthQ, Written(Math.Clamp(f.BassGain, -20, 20)), 1);

    private static AudioEffectSpec TrebleOf(TrackAudioFilters f) => new(AudioEffectKind.HighShelf, 3000, 0.5, WidthQ, Written(Math.Clamp(f.TrebleGain, -20, 20)), 1);

    // An equalizer band: at its octave's centre, an octave wide, unless the parametric equalizer moved it (5.2B: EqualizerFrequencies, EqualizerWidths).
    private static AudioEffectSpec BandOf(TrackAudioFilters f, int band)
    {
        var hz = f.EqualizerFrequencies is { } moved && band < moved.Length && double.IsFinite(moved[band])
            ? Written(Math.Clamp(moved[band], 20, 20000)) : TrackAudioFilters.EqualizerBands[band];
        var octaves = f.EqualizerWidths is { } widths && band < widths.Length && double.IsFinite(widths[band]) ? Written(Math.Clamp(widths[band], 0.1, 4)) : 1;
        return new AudioEffectSpec(AudioEffectKind.Peaking, hz, octaves, WidthOctaves, Written(Math.Clamp(f.EqualizerGains[band], -20, 20)));
    }

    /// <summary>
    /// The tone a track's filters give a frequency (5.2B: the parametric equalizer's curve), in decibels: the summed magnitude of the
    /// equalizer's bands, and of the high-pass and the bass and treble shelves when they are on, exactly as the chain's biquads have them
    /// (the same specs, BiquadEffect's coefficients). 0 for none. Pure and allocation-free: any thread, every pixel of a curve.
    /// </summary>
    public static double EqualizerResponseDb(TrackAudioFilters filters, double hz, int sampleRate = 48000)
    {
        if (filters is null || !double.IsFinite(hz) || hz <= 0 || sampleRate <= 0)
            return 0;

        var w = 2 * Math.PI * Math.Min(hz, sampleRate * 0.5) / sampleRate;
        var db = 0.0;
        if (filters.HighPass)
            db += ResponseDb(HighPassOf(filters), sampleRate, w);
        if (filters.BassTreble)
        {
            if (Math.Abs(filters.BassGain) >= 0.05)
                db += ResponseDb(BassOf(filters), sampleRate, w);
            if (Math.Abs(filters.TrebleGain) >= 0.05)
                db += ResponseDb(TrebleOf(filters), sampleRate, w);
        }

        if (filters.Equalizer)
        {
            for (var band = 0; band < TrackAudioFilters.EqualizerBands.Count && band < filters.EqualizerGains.Length; band++)
            {
                if (Math.Abs(filters.EqualizerGains[band]) >= 0.05)
                    db += ResponseDb(BandOf(filters, band), sampleRate, w);
            }
        }

        return db;
    }

    // One biquad's magnitude at w (radians a sample), in decibels: |b0 + b1 z^-1 + b2 z^-2| / |1 + a1 z^-1 + a2 z^-2| on the unit circle.
    private static double ResponseDb(in AudioEffectSpec spec, int sampleRate, double w)
    {
        var (b0, b1, b2, a1, a2) = BiquadEffect.Coefficients(spec, sampleRate);
        var (s1, c1) = Math.SinCos(w);
        var (s2, c2) = Math.SinCos(2 * w);
        var (nr, ni) = (b0 + b1 * c1 + b2 * c2, -(b1 * s1 + b2 * s2));
        var (dr, di) = (1 + a1 * c1 + a2 * c2, -(a1 * s1 + a2 * s2));
        var ratio = (nr * nr + ni * ni) / Math.Max(dr * dr + di * di, 1e-300);
        return 10 * Math.Log10(Math.Max(ratio, 1e-30));
    }

    // A level in decibels (as the settings hold it) as a factor.
    private static double FromDecibels(double decibels) => Math.Pow(10, Written(decibels) / 20);

    // A setting as its words held it: to fifteen significant digits, then four decimals, halves away from zero (FFmpeg's words
    // for it were written "0.####"), so the effects are the numbers they always were. A value past what that can hold is itself.
    private static double Written(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 1e15 ? (double)Math.Round((decimal)value, 4, MidpointRounding.AwayFromZero) : value;
}

/// <summary>One effect at work: its state (filter history, envelopes) lives here, on the mixer's thread only.</summary>
internal abstract class AudioEffect
{
    public static AudioEffect Create(in AudioEffectSpec spec, int sampleRate) => spec.Kind switch
    {
        AudioEffectKind.Gain => new GainEffect((float)spec.P1),
        AudioEffectKind.HighPass or AudioEffectKind.LowPass or AudioEffectKind.LowShelf or AudioEffectKind.HighShelf or AudioEffectKind.Peaking => new BiquadEffect(spec, sampleRate),
        AudioEffectKind.Compressor => new CompressorEffect(spec, sampleRate),
        AudioEffectKind.Gate => new GateEffect(spec, sampleRate),
        AudioEffectKind.Limiter => new LimiterEffect(spec, sampleRate),
        AudioEffectKind.Widener => new WidenerEffect((float)spec.P1, spec.P2 != 0),
        AudioEffectKind.Denoise => new DenoiseEffect(spec, sampleRate),
        AudioEffectKind.StudioVoice => new StudioVoiceEffect(spec, sampleRate),
        AudioEffectKind.DeEsser => new DeEsserEffect(spec, sampleRate),
        _ => new GainEffect(1),
    };

    /// <summary>
    /// How many samples late an effect's sound comes out at <paramref name="sampleRate"/> (5.2C): the spectral stages' frame (Noise
    /// Reduction, Studio Voice), and since Pass 5.2D the limiter's lookahead too (the user's answer: every audio
    /// filter with no drift; its attack less one sample, 239 samples at 48 kHz and 5 ms, as af_alimiter with latency=1 takes back). The
    /// mixer reads a strip's voices this far ahead so its sound lands on its picture (AudioMixer.MixInto).
    /// </summary>
    public static int LatencyOf(in AudioEffectSpec spec, int sampleRate) => spec.Kind switch
    {
        AudioEffectKind.Denoise => DenoiseEffect.Size,
        AudioEffectKind.StudioVoice => StudioVoiceEffect.Latency,
        AudioEffectKind.Limiter => LimiterEffect.LatencyFor(spec, sampleRate),
        _ => 0,
    };

    /// <summary>A chain's latency: its effects' latencies summed (samples at <paramref name="sampleRate"/>).</summary>
    public static int LatencyOf(ReadOnlySpan<AudioEffectSpec> specs, int sampleRate)
    {
        var total = 0;
        foreach (var spec in specs)
            total += LatencyOf(spec, sampleRate);
        return total;
    }

    /// <summary>Processes interleaved stereo in place.</summary>
    public abstract void Process(Span<float> stereo, int frames);

    /// <summary>
    /// The moments of the Sequence the next block's sound is heard at (5.2B: the mixer calls it before each <see cref="Process"/>), from
    /// <paramref name="fromSeconds"/> to <paramref name="toSeconds"/>: an effect with keyframes (Studio Voice's mix) follows the Sequence by it.
    /// </summary>
    public virtual void Moment(double fromSeconds, double toSeconds)
    {
    }

    /// <summary>The same effect with new settings takes the old one's state: a slider moved does not click.</summary>
    public virtual void TakeState(AudioEffect previous)
    {
    }

    /// <summary>A jump (a seek): every filter starts afresh there, as FFmpeg's do in a graph begun at that moment.</summary>
    public virtual void Reset()
    {
    }
}

internal sealed class GainEffect(float factor) : AudioEffect
{
    public override void Process(Span<float> stereo, int frames) => AudioMath.Scale(stereo[..(frames * 2)], factor);
}

/// <summary>
/// Spectral noise reduction (5.2-Audit: afftdn's part, heard while editing and exported alike). A short-time Fourier transform of
/// each channel (512-sample square-root Hann windows a quarter apart, overlap-added back); each bin's noise is the least its
/// smoothed power has been over the last second or so (minimum statistics, two half windows, the minimum's bias corrected), held
/// to a background's level (what is steadily louder than -30 dBFS is a sound, not a noise: a held note, a tone); a
/// decision-directed Wiener gain takes it away, never below 10^(-P1 / 20) (the reduction asked for, afftdn's nr). Speech and
/// sound above the noise pass; the steady hiss and hum under them go. The sound comes out 512 samples late (11 ms at 48 kHz).
/// </summary>
internal sealed class DenoiseEffect : AudioEffect
{
    // (Its transform, window and noise floor's rules are Studio Voice's spectral stage's too, 5.2B: StudioVoice.cs.)
    internal const int Size = 512, Hop = Size / 4, Bins = Size / 2 + 1;

    // How much more than its minimum a bin's noise is taken to be (the minimum of a noisy power, smoothed, over some hundred
    // frames lies about a third of its mean down).
    internal const float Bias = 3f;

    // The loudest a bin's noise is taken to be: white noise at -30 dBFS (its power in a bin, sigma^2 times the window's energy, Size / 2).
    internal const float MostNoise = Size / 2 * 1e-3f;

    internal static readonly float[] Window = MakeWindow();
    private static readonly Radix2Fft Fft = new(Size);

    private readonly float _floor;
    private readonly int _halfWindow;
    private Channel[] _channels = [new(), new()];

    public DenoiseEffect(in AudioEffectSpec spec, int sampleRate)
    {
        _floor = (float)Math.Pow(10, -Math.Clamp(spec.P1, 0.01, 97) / 20);
        _halfWindow = Math.Max((int)Math.Round(sampleRate * 0.6 / Hop), 8);
    }

    private sealed class Channel
    {
        public readonly float[] Input = new float[Size], Output = new float[Size], Ready = new float[Hop];
        public readonly float[] Re = new float[Size], Im = new float[Size];
        public readonly float[] Power = new float[Bins], Minimum = new float[Bins], Previous = new float[Bins], Gain = new float[Bins], Post = new float[Bins];
        public int Filled, Frames;
        public bool Started;
    }

    public override void Process(Span<float> stereo, int frames)
    {
        for (var c = 0; c < 2; c++)
        {
            var channel = _channels[c];
            for (var i = 0; i < frames; i++)
            {
                ref var sample = ref stereo[2 * i + c];
                channel.Input[Size - Hop + channel.Filled] = sample;
                sample = channel.Ready[channel.Filled];
                if (++channel.Filled == Hop)
                {
                    Frame(channel);
                    channel.Filled = 0;
                }
            }
        }
    }

    private void Frame(Channel channel)
    {
        var (re, im) = (channel.Re, channel.Im);
        Windowed(channel.Input, re, im);
        Transform(re, im, inverse: false);

        // The noise floor: the least smoothed power over this half window and the last one. Bins 0 to Size / 2 - 1 four at a time (each lane
        // the scalar loop's operations in its order: the same gains to the bit), the last bin alone; the bins above Size / 2 then take their
        // mirror bins' gains.
        var newWindow = channel.Frames % _halfWindow == 0;
        var started = channel.Started;
        Span<Vector128<float>> vectorRe = Vectors(re), vectorIm = Vectors(im);
        Span<Vector128<float>> powers = Vectors(channel.Power), minimums = Vectors(channel.Minimum), previouses = Vectors(channel.Previous), gains = Vectors(channel.Gain), posts = Vectors(channel.Post);
        var (floor, one) = (Vector128.Create(_floor), Vector128<float>.One);
        for (var v = 0; v < vectorRe.Length && v < vectorIm.Length && v < powers.Length && v < minimums.Length && v < previouses.Length && v < gains.Length && v < posts.Length; v++)
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

            var noise = Vector128.Min(Vector128.Max(Vector128.Min(minimums[v], previouses[v]) * Bias, Vector128.Create(1e-12f)), Vector128.Create(MostNoise));
            var post = power / noise;
            var prior = Vector128.Create(0.98f) * gains[v] * gains[v] * posts[v] + Vector128.Create(0.02f) * Vector128.Max(post - one, Vector128<float>.Zero);
            var gain = Vector128.Max(prior / (one + prior), floor);
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

            var noise = Math.Clamp(Math.Min(channel.Minimum[b], channel.Previous[b]) * Bias, 1e-12f, MostNoise);
            var post = power / noise;
            var prior = 0.98f * channel.Gain[b] * channel.Gain[b] * channel.Post[b] + 0.02f * Math.Max(post - 1, 0);
            var gain = Math.Max(prior / (1 + prior), _floor);
            (channel.Gain[b], channel.Post[b]) = (gain, post);
            re[b] *= gain;
            im[b] *= gain;
        }

        Mirror(re, im, channel.Gain);
        channel.Started = true;
        channel.Frames++;
        Transform(re, im, inverse: true);
        OverlapAdd(re, channel.Output);
        Array.Copy(channel.Output, channel.Ready, Hop);
        Array.Copy(channel.Output, Hop, channel.Output, 0, Size - Hop);
        Array.Clear(channel.Output, Size - Hop, Hop);
        Array.Copy(channel.Input, Hop, channel.Input, 0, Size - Hop);
    }

    // An in-place radix-2 FFT of Size points (the inverse unscaled).
    internal static void Transform(float[] re, float[] im, bool inverse) => Fft.Transform(re, im, inverse);

    // The first so many bins (or points) of a frame's array, four to a vector: the bins below Size / 2 unless told.
    internal static Span<Vector128<float>> Vectors(float[] values, int count = Size / 2) => MemoryMarshal.Cast<float, Vector128<float>>(values.AsSpan(0, count));

    /// <summary>A frame's input under the window: the transform's real part (its imaginary part nothing).</summary>
    internal static void Windowed(float[] input, float[] re, float[] im)
    {
        Span<Vector128<float>> from = Vectors(input, Size), window = Vectors(Window, Size), to = Vectors(re, Size);
        for (var v = 0; v < from.Length && v < window.Length && v < to.Length; v++)
            to[v] = from[v] * window[v];
        Array.Clear(im, 0, Size);
    }

    /// <summary>The bins above Size / 2 (a real sound's spectrum, mirrored) scaled by their mirror bins' gains: bin Size - b by bin b's.</summary>
    internal static void Mirror(float[] re, float[] im, float[] gains)
    {
        var n = Size / 2 + 1;
        for (; n + 4 <= Size; n += 4)
        {
            var gain = Vector128.Shuffle(Vector128.Create(gains.AsSpan(Size - n - 3, 4)), Vector128.Create(3, 2, 1, 0));
            (Vector128.Create(re.AsSpan(n, 4)) * gain).CopyTo(re.AsSpan(n, 4));
            (Vector128.Create(im.AsSpan(n, 4)) * gain).CopyTo(im.AsSpan(n, 4));
        }

        for (; n < Size; n++)
        {
            re[n] *= gains[Size - n];
            im[n] *= gains[Size - n];
        }
    }

    /// <summary>A frame's inverse transform overlap-added to the output: square-root Hann twice is Hann, four of which a quarter apart sum to 2.</summary>
    internal static void OverlapAdd(float[] re, float[] output)
    {
        Span<Vector128<float>> from = Vectors(re, Size), window = Vectors(Window, Size), to = Vectors(output, Size);
        var scale = Vector128.Create(0.5f / Size);
        for (var v = 0; v < from.Length && v < window.Length && v < to.Length; v++)
            to[v] += from[v] * window[v] * scale;
    }

    private static float[] MakeWindow()
    {
        var window = new float[Size];
        for (var n = 0; n < Size; n++)
            window[n] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Size));
        return window;
    }

    public override void TakeState(AudioEffect previous)
    {
        if (previous is DenoiseEffect before)
            _channels = before._channels;
    }

    public override void Reset() => _channels = [new(), new()];
}

/// <summary>
/// An in-place radix-2 FFT of a power of two of 8 points or more, the inverse unscaled (5.3: the noise reduction's and Studio Voice's 512
/// points, the beat detector's 128). Each stage's twiddles lie side by side (the stage of half-length h at [h, 2h)), so from h = 4 on a
/// vector does four butterflies (eight where the processor has 256-bit vectors); the first two stages are done four points at a time in
/// registers. Every butterfly is the multiplications, additions and subtractions of one done alone, in the same order and never fused
/// (no fused multiply-add), so the result is the same to the bit as the plain loop's.
/// </summary>
internal sealed class Radix2Fft
{
    private readonly int _size;
    private readonly float[] _cos, _forwardSin, _inverseSin;
    private readonly int[] _reverse, _swaps;

    public Radix2Fft(int size)
    {
        if (size < 8 || !BitOperations.IsPow2(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "A power of two of 8 or more.");

        // The twiddles as the plain loop had them (the table of cos and sin of 2 pi k / size, read at k * step): the same values.
        _size = size;
        (_cos, _forwardSin, _inverseSin) = (new float[size], new float[size], new float[size]);
        for (var half = 1; half < size; half <<= 1)
        {
            var step = size / (2 * half);
            for (var k = 0; k < half; k++)
            {
                var m = k * step;
                var (cos, sin) = ((float)Math.Cos(2 * Math.PI * m / size), (float)Math.Sin(2 * Math.PI * m / size));
                (_cos[half + k], _forwardSin[half + k], _inverseSin[half + k]) = (cos, -sin, sin);
            }
        }

        // The bit-reversed order, and the pairs of points it swaps.
        _reverse = new int[size];
        var bits = BitOperations.Log2((uint)size);
        var swaps = new List<int>(size);
        for (var i = 0; i < size; i++)
        {
            var (r, v) = (0, i);
            for (var b = 0; b < bits; b++, v >>= 1)
                r = (r << 1) | (v & 1);
            _reverse[i] = r;
            if (r > i)
            {
                swaps.Add(i);
                swaps.Add(r);
            }
        }

        _swaps = [.. swaps];
    }

    /// <summary>The point each point of the transform's input goes to before the butterflies (its index's bits reversed).</summary>
    public ReadOnlySpan<int> Reverse => _reverse;

    /// <summary>The transform of re + i im in place, the inverse unscaled.</summary>
    public void Transform(Span<float> re, Span<float> im, bool inverse)
    {
        var swaps = _swaps;
        for (var s = 0; s + 1 < swaps.Length; s += 2)
        {
            var (i, j) = (swaps[s], swaps[s + 1]);
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        Butterflies(re, im, inverse);
    }

    /// <summary>The transform of points already in bit-reversed order (<see cref="Reverse"/>), in place.</summary>
    public void Butterflies(Span<float> re, Span<float> im, bool inverse)
    {
        var size = _size;
        re = re[..size];
        im = im[..size];
        var (cos, sin) = (_cos, inverse ? _inverseSin : _forwardSin);

        // Half-lengths 1 and 2: each four points' two stages in registers.
        var (w1r, w1i, w20r, w20i, w21r, w21i) = (cos[1], sin[1], cos[2], sin[2], cos[3], sin[3]);
        for (var g = 0; g + 3 < re.Length && g + 3 < im.Length; g += 4)
        {
            float x0 = re[g], x1 = re[g + 1], x2 = re[g + 2], x3 = re[g + 3];
            float y0 = im[g], y1 = im[g + 1], y2 = im[g + 2], y3 = im[g + 3];
            var (tr, ti) = (x1 * w1r - y1 * w1i, x1 * w1i + y1 * w1r);
            (x1, y1) = (x0 - tr, y0 - ti);
            (x0, y0) = (x0 + tr, y0 + ti);
            (tr, ti) = (x3 * w1r - y3 * w1i, x3 * w1i + y3 * w1r);
            (x3, y3) = (x2 - tr, y2 - ti);
            (x2, y2) = (x2 + tr, y2 + ti);
            (tr, ti) = (x2 * w20r - y2 * w20i, x2 * w20i + y2 * w20r);
            (x2, y2) = (x0 - tr, y0 - ti);
            (x0, y0) = (x0 + tr, y0 + ti);
            (tr, ti) = (x3 * w21r - y3 * w21i, x3 * w21i + y3 * w21r);
            (x3, y3) = (x1 - tr, y1 - ti);
            (x1, y1) = (x1 + tr, y1 + ti);
            (re[g], re[g + 1], re[g + 2], re[g + 3]) = (x0, x1, x2, x3);
            (im[g], im[g + 1], im[g + 2], im[g + 3]) = (y0, y1, y2, y3);
        }

        // Half-length 4 on (and from 8 on, eight at a time where 256-bit vectors are the processor's own).
        var half = 4;
        for (; half < size && (half < 8 || !Vector256.IsHardwareAccelerated); half <<= 1)
        {
            Stage(MemoryMarshal.Cast<float, Vector128<float>>(re), MemoryMarshal.Cast<float, Vector128<float>>(im),
                MemoryMarshal.Cast<float, Vector128<float>>(cos.AsSpan(half, half)), MemoryMarshal.Cast<float, Vector128<float>>(sin.AsSpan(half, half)));
        }

        for (; half < size; half <<= 1)
        {
            Stage(MemoryMarshal.Cast<float, Vector256<float>>(re), MemoryMarshal.Cast<float, Vector256<float>>(im),
                MemoryMarshal.Cast<float, Vector256<float>>(cos.AsSpan(half, half)), MemoryMarshal.Cast<float, Vector256<float>>(sin.AsSpan(half, half)));
        }
    }

    // One stage, a vector of butterflies at a time: in each group of points, its first half (a) and second half (b), as long as the twiddles.
    // (Each span's length is in the loop's condition, so the indexing is not checked again.)
    private static void Stage(Span<Vector128<float>> re, Span<Vector128<float>> im, ReadOnlySpan<Vector128<float>> cos, ReadOnlySpan<Vector128<float>> sin)
    {
        var h = cos.Length;
        for (var start = 0; start + 2 * h <= re.Length && start + 2 * h <= im.Length; start += 2 * h)
        {
            Span<Vector128<float>> ra = re.Slice(start, h), rb = re.Slice(start + h, h), ia = im.Slice(start, h), ib = im.Slice(start + h, h);
            for (var k = 0; k < cos.Length && k < sin.Length && k < ra.Length && k < rb.Length && k < ia.Length && k < ib.Length; k++)
            {
                var (wr, wi, xr, xi) = (cos[k], sin[k], rb[k], ib[k]);
                var (tr, ti) = (xr * wr - xi * wi, xr * wi + xi * wr);
                var (ar, ai) = (ra[k], ia[k]);
                (rb[k], ib[k]) = (ar - tr, ai - ti);
                (ra[k], ia[k]) = (ar + tr, ai + ti);
            }
        }
    }

    private static void Stage(Span<Vector256<float>> re, Span<Vector256<float>> im, ReadOnlySpan<Vector256<float>> cos, ReadOnlySpan<Vector256<float>> sin)
    {
        var h = cos.Length;
        for (var start = 0; start + 2 * h <= re.Length && start + 2 * h <= im.Length; start += 2 * h)
        {
            Span<Vector256<float>> ra = re.Slice(start, h), rb = re.Slice(start + h, h), ia = im.Slice(start, h), ib = im.Slice(start + h, h);
            for (var k = 0; k < cos.Length && k < sin.Length && k < ra.Length && k < rb.Length && k < ia.Length && k < ib.Length; k++)
            {
                var (wr, wi, xr, xi) = (cos[k], sin[k], rb[k], ib[k]);
                var (tr, ti) = (xr * wr - xi * wi, xr * wi + xi * wr);
                var (ar, ai) = (ra[k], ia[k]);
                (rb[k], ib[k]) = (ar - tr, ai - ti);
                (ra[k], ia[k]) = (ar + tr, ai + ti);
            }
        }
    }
}

internal sealed class WidenerEffect(float m, bool clip) : AudioEffect
{
    public override void Process(Span<float> stereo, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var (l, r) = (stereo[2 * i], stereo[2 * i + 1]);
            var average = (l + r) * 0.5f;
            l = average + m * (l - average);
            r = average + m * (r - average);
            if (clip)
                (l, r) = (Math.Clamp(l, -1f, 1f), Math.Clamp(r, -1f, 1f));
            (stereo[2 * i], stereo[2 * i + 1]) = (l, r);
        }
    }
}

/// <summary>af_biquads' RBJ cookbook filters, in double precision (a 31 Hz band at 48 kHz needs it).</summary>
internal sealed class BiquadEffect : AudioEffect
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1L, _x2L, _y1L, _y2L, _x1R, _x2R, _y1R, _y2R;

    public BiquadEffect(in AudioEffectSpec spec, int sampleRate) => (_b0, _b1, _b2, _a1, _a2) = Coefficients(spec, sampleRate);

    /// <summary>A spec's coefficients, normalized (a0 = 1): the filter's own, and the equalizer curve's (AudioChainBuilder.EqualizerResponseDb, 5.2B).</summary>
    internal static (double B0, double B1, double B2, double A1, double A2) Coefficients(in AudioEffectSpec spec, int sampleRate)
    {
        var frequency = Math.Clamp(spec.P1, 1, sampleRate * 0.4999);
        var width = Math.Max(spec.P2, 1e-6);
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var (sin, cos) = Math.SinCos(w0);
        var a = Math.Pow(10, spec.P4 / 40);
        var alpha = (int)spec.P3 switch
        {
            1 => sin / (2 * frequency / width),
            5 => sin / (2 * frequency / (width * 1000)),
            2 => sin * Math.Sinh(Math.Log(2.0) / 2 * width * w0 / sin),
            4 => sin / 2 * Math.Sqrt((a + 1 / a) * (1 / width - 1) + 2),
            _ => sin / (2 * width),
        };
        if (spec.P5 > 0.5)
            alpha /= Math.Sqrt(2);

        double b0, b1, b2, a0, a1, a2;
        switch (spec.Kind)
        {
            case AudioEffectKind.HighPass:
                (a0, a1, a2) = (1 + alpha, -2 * cos, 1 - alpha);
                (b0, b1, b2) = ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2);
                break;
            case AudioEffectKind.LowPass:
                (a0, a1, a2) = (1 + alpha, -2 * cos, 1 - alpha);
                (b0, b1, b2) = ((1 - cos) / 2, 1 - cos, (1 - cos) / 2);
                break;
            case AudioEffectKind.LowShelf:
            {
                var sq = 2 * Math.Sqrt(a) * alpha;
                (a0, a1, a2) = (a + 1 + (a - 1) * cos + sq, -2 * (a - 1 + (a + 1) * cos), a + 1 + (a - 1) * cos - sq);
                (b0, b1, b2) = (a * (a + 1 - (a - 1) * cos + sq), 2 * a * (a - 1 - (a + 1) * cos), a * (a + 1 - (a - 1) * cos - sq));
                break;
            }
            case AudioEffectKind.HighShelf:
            {
                var sq = 2 * Math.Sqrt(a) * alpha;
                (a0, a1, a2) = (a + 1 - (a - 1) * cos + sq, 2 * (a - 1 - (a + 1) * cos), a + 1 - (a - 1) * cos - sq);
                (b0, b1, b2) = (a * (a + 1 + (a - 1) * cos + sq), -2 * a * (a - 1 + (a + 1) * cos), a * (a + 1 + (a - 1) * cos - sq));
                break;
            }
            default:
                (a0, a1, a2) = (1 + alpha / a, -2 * cos, 1 - alpha / a);
                (b0, b1, b2) = (1 + alpha * a, -2 * cos, 1 - alpha * a);
                break;
        }

        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public override void Process(Span<float> stereo, int frames)
    {
        var (b0, b1, b2, a1, a2) = (_b0, _b1, _b2, _a1, _a2);
        var (x1L, x2L, y1L, y2L, x1R, x2R, y1R, y2R) = (_x1L, _x2L, _y1L, _y2L, _x1R, _x2R, _y1R, _y2R);
        for (var i = 0; i < frames; i++)
        {
            double l = stereo[2 * i], r = stereo[2 * i + 1];
            var yl = b0 * l + b1 * x1L + b2 * x2L - a1 * y1L - a2 * y2L;
            var yr = b0 * r + b1 * x1R + b2 * x2R - a1 * y1R - a2 * y2R;
            (x2L, x1L, y2L, y1L) = (x1L, l, y1L, yl);
            (x2R, x1R, y2R, y1R) = (x1R, r, y1R, yr);
            stereo[2 * i] = (float)yl;
            stereo[2 * i + 1] = (float)yr;
        }

        // Denormals, which would slow the processor to a crawl in the silence after a sound.
        (_x1L, _x2L, _y1L, _y2L) = (Flush(x1L), Flush(x2L), Flush(y1L), Flush(y2L));
        (_x1R, _x2R, _y1R, _y2R) = (Flush(x1R), Flush(x2R), Flush(y1R), Flush(y2R));
    }

    private static double Flush(double value) => Math.Abs(value) < 1e-25 ? 0 : value;

    public override void TakeState(AudioEffect previous)
    {
        if (previous is BiquadEffect old)
            (_x1L, _x2L, _y1L, _y2L, _x1R, _x2R, _y1R, _y2R) = (old._x1L, old._x2L, old._y1L, old._y2L, old._x1R, old._x2R, old._y1R, old._y2R);
    }

    public override void Reset() => (_x1L, _x2L, _y1L, _y2L, _x1R, _x2R, _y1R, _y2R) = (0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// af_sidechaincompress's gain computer, downward, RMS detection, the channels averaged: acompressor (the side chain is
/// the input) and the ducking (the side chain is the voices). Its envelope is the filter's lin_slope.
/// </summary>
internal sealed class CompressorMath
{
    private readonly double _thres, _ratio, _knee, _kneeStart, _kneeStop, _compressedKneeStop, _detector, _attack, _release, _makeup, _mix, _levelIn;
    public double Envelope;

    public CompressorMath(double threshold, double ratio, double attackMs, double releaseMs, double knee, double makeup, double mix, double levelIn, int sampleRate)
    {
        threshold = Math.Max(threshold, 1e-9);
        (_ratio, _knee, _makeup, _mix, _levelIn) = (Math.Max(ratio, 1), Math.Max(knee, 1), makeup, Math.Clamp(mix, 0, 1), levelIn);
        _thres = Math.Log(threshold);
        var linKneeStart = threshold / Math.Sqrt(_knee);
        var linKneeStop = threshold * Math.Sqrt(_knee);
        _kneeStart = Math.Log(linKneeStart);
        _kneeStop = Math.Log(linKneeStop);
        _compressedKneeStop = (_kneeStop - _thres) / _ratio + _thres;
        _detector = linKneeStart * linKneeStart;
        _attack = Math.Min(1, 1 / (attackMs * sampleRate / 4000));
        _release = Math.Min(1, 1 / (releaseMs * sampleRate / 4000));
    }

    /// <summary>The factor for one frame, from its side chain's level (the channels' average magnitude, times the side chain's gain).</summary>
    public double Next(double level)
    {
        var squared = level * level;
        Envelope += (squared - Envelope) * (squared > Envelope ? _attack : _release);
        var gain = 1.0;
        if (Envelope > 0 && Envelope > _detector)
        {
            var slope = Math.Log(Envelope) * 0.5;
            var g = (slope - _thres) / _ratio + _thres;
            if (_knee > 1 && slope < _kneeStop)
                g = AudioMath.Hermite(slope, _kneeStart, _kneeStop, _kneeStart, _compressedKneeStop, 1, 1 / _ratio);
            gain = Math.Exp(g - slope);
        }

        return _levelIn * (gain * _makeup * _mix + (1 - _mix));
    }
}

internal sealed class CompressorEffect : AudioEffect
{
    private readonly CompressorMath _math;
    private readonly double _levelSc;

    // acompressor's side chain is its own input, at its input level (af_sidechaincompress passes level_in for both).
    public CompressorEffect(in AudioEffectSpec spec, int sampleRate)
    {
        _math = new CompressorMath(spec.P1, spec.P2, spec.P3, spec.P4, spec.P5, spec.P6, spec.P7, spec.P8, sampleRate);
        _levelSc = spec.P8;
    }

    public override void Process(Span<float> stereo, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var (l, r) = (stereo[2 * i], stereo[2 * i + 1]);
            var factor = (float)_math.Next((Math.Abs(l * _levelSc) + Math.Abs(r * _levelSc)) * 0.5);
            stereo[2 * i] = l * factor;
            stereo[2 * i + 1] = r * factor;
        }
    }

    public override void TakeState(AudioEffect previous)
    {
        if (previous is CompressorEffect old)
            _math.Envelope = old._math.Envelope;
    }

    public override void Reset() => _math.Envelope = 0;
}

/// <summary>The ducking: a strip pressed down by the voices' side chain (sidechaincompress's, as the export's graph had it before 5.2-Audit).</summary>
internal sealed class Ducker(DuckParameters duck, int sampleRate)
{
    private readonly CompressorMath _math = new(Math.Clamp(duck.Threshold, 0.000976563, 1), Math.Clamp(duck.Ratio, 1, 20), duck.AttackMs, duck.ReleaseMs,
        Math.Clamp(duck.Knee, 1, 8), duck.Makeup, duck.Mix, 1, sampleRate);
    private readonly double _sideChainGain = duck.SideChainGain;

    public void Process(Span<float> stereo, ReadOnlySpan<float> key, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var factor = (float)_math.Next((Math.Abs(key[2 * i] * _sideChainGain) + Math.Abs(key[2 * i + 1] * _sideChainGain)) * 0.5);
            stereo[2 * i] *= factor;
            stereo[2 * i + 1] *= factor;
        }
    }

    public void TakeState(Ducker previous) => _math.Envelope = previous._math.Envelope;

    public void Reset() => _math.Envelope = 0;
}

/// <summary>af_agate, downward, RMS detection, the channels averaged.</summary>
internal sealed class GateEffect : AudioEffect
{
    private readonly double _thres, _ratio, _kneeStart, _kneeStop, _linKneeStop, _range, _attack, _release, _factor, _levelIn;
    private double _envelope;

    public GateEffect(in AudioEffectSpec spec, int sampleRate)
    {
        var threshold = Math.Max(spec.P1, 1e-9);
        var linThreshold = threshold * threshold;
        var kneeRoot = Math.Sqrt(Math.Max(spec.P6, 1));
        _ratio = Math.Max(spec.P2, 1);
        (_attack, _release) = (Math.Min(1, 1 / (spec.P3 * sampleRate / 4000)), Math.Min(1, 1 / (spec.P4 * sampleRate / 4000)));
        _linKneeStop = linThreshold * kneeRoot;
        var linKneeStart = linThreshold / kneeRoot;
        (_thres, _kneeStart, _kneeStop) = (Math.Log(linThreshold), Math.Log(linKneeStart), Math.Log(_linKneeStop));
        (_range, _factor, _levelIn) = (spec.P5, spec.P7, spec.P8);
    }

    public override void Process(Span<float> stereo, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var (l, r) = (stereo[2 * i], stereo[2 * i + 1]);
            var level = (Math.Abs(l * _levelIn) + Math.Abs(r * _levelIn)) * 0.5;
            var squared = level * level;
            _envelope += (squared - _envelope) * (squared > _envelope ? _attack : _release);
            var gain = 1.0;
            if (_envelope > 0 && _envelope < _linKneeStop)
            {
                var slope = Math.Log(_envelope);
                var g = (slope - _thres) * _ratio + _thres;
                if (_kneeStart < _kneeStop && slope > _kneeStart)
                    g = AudioMath.Hermite(slope, _kneeStart, _kneeStop, (_kneeStart - _thres) * _ratio + _thres, _kneeStop, _ratio, 1);
                gain = Math.Max(_range, Math.Exp(g - slope));
            }

            var factor = (float)(_levelIn * gain * _factor);
            stereo[2 * i] = l * factor;
            stereo[2 * i + 1] = r * factor;
        }
    }

    public override void TakeState(AudioEffect previous)
    {
        if (previous is GateEffect old)
            _envelope = old._envelope;
    }

    public override void Reset() => _envelope = 0;
}

/// <summary>
/// af_alimiter, line for line (double precision, the two channels interleaved in one ring as the filter keeps them), so the
/// limiter heard while editing is the export's to the bit (checked against HandPeg's pinned FFmpeg). The sound is held back
/// by the attack less one sample (239 samples at 48 kHz and 5 ms, <see cref="LatencyFor"/>), which the mixer reads ahead by since
/// Pass 5.2D: what comes out is af_alimiter's with latency=1, on its picture. A sample over the limit sets the
/// slope the gain falls along to reach limit / peak as that sample comes out; a louder one later replaces it, a gentler one queues
/// behind it; past the last the gain climbs back by (1 - gain) over the release. Its auto level (on by default in FFmpeg, off in
/// what HandPeg writes) and level_out are the output's factor.
/// </summary>
internal sealed class LimiterEffect : AudioEffect
{
    private const int Channels = 2;
    private readonly double _limit, _levelIn, _levelOut, _release;
    private readonly int _rate, _size;
    private readonly double[] _buffer, _nextDelta;
    private readonly int[] _nextPos;
    private double _att = 1, _delta;
    private int _pos, _nextIter, _nextLen;

    public LimiterEffect(in AudioEffectSpec spec, int sampleRate)
    {
        (_limit, _levelIn, _levelOut, _release, _rate) = (spec.P1, spec.P4, spec.P5, spec.P3 / 1000, sampleRate);
        _size = RingSize(spec, sampleRate);
        var capacity = Math.Max((int)(sampleRate * Channels * 100 / 1000.0 + Channels), _size + Channels);
        (_buffer, _nextDelta, _nextPos) = (new double[capacity], new double[capacity], new int[capacity]);
        Array.Fill(_nextPos, -1);
    }

    // config_input's sizes: the ring is the attack's samples (whole frames, both channels); the queues are as long as 100 ms.
    private static int RingSize(in AudioEffectSpec spec, int sampleRate)
    {
        var size = (int)(sampleRate * (spec.P2 / 1000) * Channels);
        return Math.Max(size - size % Channels, Channels);
    }

    /// <summary>How many frames late the sound comes out: a sample leaves the ring the frame before its place is written again.</summary>
    public static int LatencyFor(in AudioEffectSpec spec, int sampleRate) => RingSize(spec, sampleRate) / Channels - 1;

    public override void Process(Span<float> stereo, int frames)
    {
        var (limit, size, buffer, nextPos, nextDelta) = (_limit, _size, _buffer, _nextPos, _nextDelta);
        for (var n = 0; n < frames; n++)
        {
            var peak = 0.0;
            for (var c = 0; c < Channels; c++)
            {
                var sample = stereo[Channels * n + c] * _levelIn;
                buffer[_pos + c] = sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }

            if (peak > limit)
                Plan(peak);

            var at = (_pos + Channels) % size;
            peak = Math.Max(Math.Abs(buffer[at]), Math.Abs(buffer[at + 1]));
            _att += _delta;
            var (left, right) = (buffer[at] * _att, buffer[at + 1] * _att);
            if (at == nextPos[_nextIter])
            {
                (_delta, _att) = (nextDelta[_nextIter], limit / peak);
                _nextLen--;
                nextPos[_nextIter] = -1;
                _nextIter = (_nextIter + 1) % size;
            }

            if (_att > 1)
            {
                (_att, _delta, _nextIter, _nextLen) = (1, 0, 0, 0);
                nextPos[0] = -1;
            }

            if (_att <= 0)
                (_att, _delta) = (1e-13, (1 - 1e-13) / (_rate * _release));
            if (_att != 1 && 1 - _att < 1e-13)
                _att = 1;
            if (_delta != 0 && Math.Abs(_delta) < 1e-14)
                _delta = 0;

            stereo[Channels * n] = (float)(Math.Clamp(left, -limit, limit) * _levelOut);
            stereo[Channels * n + 1] = (float)(Math.Clamp(right, -limit, limit) * _levelOut);
            _pos = (_pos + Channels) % size;
        }
    }

    // A sample over the limit: the slope that brings the gain to limit / peak when it comes out, or its place in the queue.
    private void Plan(double peak)
    {
        var (limit, size, buffer, nextPos, nextDelta) = (_limit, _size, _buffer, _nextPos, _nextDelta);
        var release = (1 - Math.Min(limit / peak, 1)) / (_rate * _release);
        var delta = (limit / peak - _att) / size * Channels;
        if (delta < _delta)
        {
            _delta = delta;
            (nextPos[0], nextPos[1], nextDelta[0], _nextLen, _nextIter) = (_pos, -1, release, 1, 0);
            return;
        }

        int i;
        var found = false;
        for (i = _nextIter; i < _nextIter + _nextLen; i++)
        {
            var j = i % size;
            var queued = Math.Max(Math.Abs(buffer[nextPos[j]]), Math.Abs(buffer[nextPos[j] + 1]));
            var slope = (limit / peak - limit / queued) / ((size - nextPos[j] + _pos) % size / Channels);
            if (slope < nextDelta[j])
            {
                (nextDelta[j], found) = (slope, true);
                break;
            }
        }

        if (!found)
            return;
        _nextLen = i - _nextIter + 1;
        nextPos[(_nextIter + _nextLen) % size] = _pos;
        nextDelta[(_nextIter + _nextLen) % size] = release;
        nextPos[(_nextIter + _nextLen + 1) % size] = -1;
        _nextLen++;
    }

    public override void TakeState(AudioEffect previous)
    {
        if (previous is LimiterEffect old && old._size == _size && old._buffer.Length == _buffer.Length)
        {
            old._buffer.CopyTo(_buffer, 0);
            old._nextDelta.CopyTo(_nextDelta, 0);
            old._nextPos.CopyTo(_nextPos, 0);
            (_att, _delta, _pos, _nextIter, _nextLen) = (old._att, old._delta, old._pos, old._nextIter, old._nextLen);
        }
    }

    public override void Reset()
    {
        Array.Clear(_buffer);
        Array.Clear(_nextDelta);
        Array.Fill(_nextPos, -1);
        (_att, _delta, _pos, _nextIter, _nextLen) = (1, 0, 0, 0, 0);
    }
}

/// <summary>Small numeric helpers, SIMD where it counts.</summary>
internal static class AudioMath
{
    public static double Hermite(double x, double x0, double x1, double p0, double p1, double m0, double m1)
    {
        var width = x1 - x0;
        var t = (x - x0) / width;
        m0 *= width;
        m1 *= width;
        var t2 = t * t;
        var t3 = t2 * t;
        var ct2 = -3 * p0 - 2 * m0 + 3 * p1 - m1;
        var ct3 = 2 * p0 + m0 - 2 * p1 + m1;
        return ct3 * t3 + ct2 * t2 + m0 * t + p0;
    }

    public static void Scale(Span<float> data, float factor)
    {
        if (factor == 1f)
            return;
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(data);
        for (var i = 0; i < vectors.Length; i++)
            vectors[i] *= factor;
        for (var i = vectors.Length * Vector<float>.Count; i < data.Length; i++)
            data[i] *= factor;
    }

    /// <summary>target = source * factor, each sample one multiplication (5.3: an export track's gain).</summary>
    public static void Scale(ReadOnlySpan<float> source, Span<float> target, float factor)
    {
        var s = MemoryMarshal.Cast<float, Vector<float>>(source);
        var t = MemoryMarshal.Cast<float, Vector<float>>(target);
        var n = Math.Min(t.Length, s.Length);
        var f = new Vector<float>(factor);
        for (var i = 0; i < n; i++)
            t[i] = s[i] * f;
        for (var i = n * Vector<float>.Count; i < target.Length && i < source.Length; i++)
            target[i] = source[i] * factor;
    }

    /// <summary>target += source.</summary>
    public static void Add(Span<float> target, ReadOnlySpan<float> source)
    {
        var t = MemoryMarshal.Cast<float, Vector<float>>(target);
        var s = MemoryMarshal.Cast<float, Vector<float>>(source);
        var n = Math.Min(t.Length, s.Length);
        for (var i = 0; i < n; i++)
            t[i] += s[i];
        for (var i = n * Vector<float>.Count; i < target.Length && i < source.Length; i++)
            target[i] += source[i];
    }

    /// <summary>target += source * factor.</summary>
    public static void AddScaled(Span<float> target, ReadOnlySpan<float> source, float factor)
    {
        var t = MemoryMarshal.Cast<float, Vector<float>>(target);
        var s = MemoryMarshal.Cast<float, Vector<float>>(source);
        var n = Math.Min(t.Length, s.Length);
        var f = new Vector<float>(factor);
        for (var i = 0; i < n; i++)
            t[i] += s[i] * f;
        for (var i = n * Vector<float>.Count; i < target.Length && i < source.Length; i++)
            target[i] += source[i] * factor;
    }

    public static float Decibels(float factor) => factor <= 1e-9f ? -180f : 20f * MathF.Log10(factor);
}
