using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace HandPegApp.Services.Audio;

// ----- A strip's processing (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// Each effect mirrors the FFmpeg filter the graph uses for the same setting (TrackAudioFilters.BuildChain), with the
// filter's own defaults for everything HandPeg leaves out, so that what is heard while editing is what the export
// encodes: highpass / bass / treble / equalizer are af_biquads' RBJ biquads (bass and treble with FFmpeg's own width, a
// Q times the square root of two: measured against HandPeg's pinned build to the last bit), acompressor and the ducking are
// af_sidechaincompress, agate is af_agate, extrastereo and volume are what they are, alimiter is af_alimiter line for line
// (its 239-sample delay at 48 kHz included). afftdn is not played (logged); the export has it.
//
// A strip's chain is read from the very filter strings the graph is built from (AudioChainParser), so the two cannot
// drift apart: one source of truth, TrackAudioFilters.BuildChain.

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
}

/// <summary>One effect of a strip with every raw parameter (see <see cref="AudioEffectKind"/> for what P1..P8 are).</summary>
public readonly record struct AudioEffectSpec(AudioEffectKind Kind, double P1 = 0, double P2 = 0, double P3 = 0, double P4 = 0, double P5 = 0, double P6 = 0, double P7 = 0, double P8 = 0)
{
    public static AudioEffectSpec Gain(double factor) => new(AudioEffectKind.Gain, factor);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}({P1:0.####}, {P2:0.####}, {P3:0.####}, {P4:0.####}, {P5:0.####}, {P6:0.####}, {P7:0.####}, {P8:0.####})");
}

/// <summary>A filter chain read back from FFmpeg's text: the effects, and what changes a voice's time (pitch, tempo).</summary>
public sealed record AudioChain(AudioEffectSpec[] Effects, double Pitch, double Tempo, string[] Unsupported)
{
    public static AudioChain None { get; } = new([], 1, 1, []);
}

/// <summary>
/// Reads the audio filters FilterGraphBuilder writes (TrackAudioFilters.BuildChain, a main track's FilterChain) into a
/// strip's effects, with each filter's FFmpeg defaults. Cached by the text: a chain is read once however often it is built.
/// </summary>
public static class AudioChainParser
{
    private static readonly ConcurrentDictionary<string, AudioChain> Read = new(StringComparer.Ordinal);

    public static AudioChain Parse(IReadOnlyList<string> filters)
    {
        if (filters.Count == 0)
            return AudioChain.None;
        var key = string.Join(",", filters);
        if (Read.TryGetValue(key, out var known))
            return known;

        var chain = ParseFresh(key);
        if (Read.Count > 512)
            Read.Clear();
        Read[key] = chain;
        return chain;
    }

    private static AudioChain ParseFresh(string text)
    {
        var effects = new List<AudioEffectSpec>();
        var unsupported = new List<string>();
        var (pitch, tempo, rate) = (1.0, 1.0, 48000.0);
        foreach (var filter in SplitTopLevel(text, ','))
        {
            var equals = filter.IndexOf('=');
            var name = (equals < 0 ? filter : filter[..equals]).Trim();
            var options = Options(equals < 0 ? "" : filter[(equals + 1)..]);
            double Get(string a, string b, double fallback) =>
                options.TryGetValue(a, out var v) && Number(v, out var n) ? n : options.TryGetValue(b, out v) && Number(v, out n) ? n : fallback;

            switch (name)
            {
                case "volume":
                    if (options.ContainsKey("enable"))
                        unsupported.Add(filter);
                    else
                        effects.Add(AudioEffectSpec.Gain(Get("volume", "", 1)));
                    break;
                case "highpass":
                case "lowpass":
                    effects.Add(Biquad(name == "highpass" ? AudioEffectKind.HighPass : AudioEffectKind.LowPass, Get("frequency", "f", name == "highpass" ? 3000 : 500), Get("width", "w", 0.707), WidthType(options, 3), 0));
                    break;
                case "bass":
                case "lowshelf":
                    effects.Add(Biquad(AudioEffectKind.LowShelf, Get("frequency", "f", 100), Get("width", "w", 0.5), WidthType(options, 3), DbOrPlain(options, "gain", "g"), name == "bass"));
                    break;
                case "treble":
                case "highshelf":
                    effects.Add(Biquad(AudioEffectKind.HighShelf, Get("frequency", "f", 3000), Get("width", "w", 0.5), WidthType(options, 3), DbOrPlain(options, "gain", "g"), name == "treble"));
                    break;
                case "equalizer":
                    effects.Add(Biquad(AudioEffectKind.Peaking, Get("frequency", "f", 0), Get("width", "w", 1), WidthType(options, 3), DbOrPlain(options, "gain", "g")));
                    break;
                case "acompressor":
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Compressor, Math.Clamp(Get("threshold", "", 0.125), 0.000976563, 1), Math.Clamp(Get("ratio", "", 2), 1, 20),
                        Math.Clamp(Get("attack", "", 20), 0.01, 2000), Math.Clamp(Get("release", "", 250), 0.01, 9000), Math.Clamp(Get("knee", "", 2.82843), 1, 8),
                        Math.Clamp(Get("makeup", "", 1), 1, 64), Math.Clamp(Get("mix", "", 1), 0, 1), Math.Clamp(Get("level_in", "", 1), 0.015625, 64)));
                    break;
                case "agate":
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Gate, Math.Clamp(Get("threshold", "", 0.125), 0, 1), Math.Clamp(Get("ratio", "", 2), 1, 9000),
                        Math.Clamp(Get("attack", "", 20), 0.01, 9000), Math.Clamp(Get("release", "", 250), 0.01, 9000), Math.Clamp(Get("range", "", 0.06125), 0, 1),
                        Math.Clamp(Get("knee", "", 2.828427125), 1, 8), Math.Clamp(Get("makeup", "", 1), 1, 64), Math.Clamp(Get("level_in", "", 1), 0.015625, 64)));
                    break;
                case "alimiter":
                {
                    // Its auto level (on unless "level" says 0 or false) makes the limit full scale: the output over the limit.
                    var limit = Math.Clamp(Get("limit", "", 1), 0.0625, 1);
                    var autoLevel = !options.TryGetValue("level", out var level) || level.Trim() is not ("0" or "false" or "off" or "no");
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Limiter, limit, Math.Clamp(Get("attack", "", 5), 0.1, 80),
                        Math.Clamp(Get("release", "", 50), 1, 8000), Math.Clamp(Get("level_in", "", 1), 0.015625, 64),
                        Math.Clamp(Get("level_out", "", 1), 0.015625, 64) * (autoLevel ? 1 / limit : 1)));
                    break;
                }
                case "extrastereo":
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Widener, Math.Clamp(Get("m", "", 2.5), -10, 10), Get("c", "", 1) != 0 ? 1 : 0));
                    break;
                case "aresample":
                    rate = Get("sample_rate", "osr", rate);
                    break;
                case "asetrate":
                    var set = Get("sample_rate", "r", rate);
                    if (set > 0 && rate > 0)
                        pitch *= set / rate;
                    rate = set;
                    break;
                case "atempo":
                    tempo *= Math.Clamp(Get("tempo", "", 1), 0.5, 100);
                    break;
                case "anull":
                    break;
                default:
                    unsupported.Add(name);
                    break;
            }
        }

        return new AudioChain([.. effects], pitch, tempo, [.. unsupported]);
    }

    private static AudioEffectSpec Biquad(AudioEffectKind kind, double frequency, double width, int widthType, double gainDb, bool bassOrTreble = false) =>
        new(kind, frequency, width, widthType, gainDb, bassOrTreble ? 1 : 0);

    // FFmpeg's width types: h 1, o 2, q 3, s 4, k 5.
    private static int WidthType(Dictionary<string, string> options, int fallback) =>
        (options.TryGetValue("width_type", out var t) || options.TryGetValue("t", out t)) ? t switch { "h" => 1, "o" => 2, "q" => 3, "s" => 4, "k" => 5, _ => fallback } : fallback;

    // The biquads' gain is in decibels whether written with "dB" or not.
    private static double DbOrPlain(Dictionary<string, string> options, string a, string b)
    {
        if (!options.TryGetValue(a, out var text) && !options.TryGetValue(b, out text))
            return 0;
        text = text.Trim();
        if (text.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
            text = text[..^2];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>A number as FFmpeg reads an option: plain, or in decibels ("-18dB" is 10^(-18/20)).</summary>
    internal static bool Number(string text, out double value)
    {
        text = text.Trim().Trim('\'');
        var decibels = text.EndsWith("dB", StringComparison.OrdinalIgnoreCase);
        if (decibels)
            text = text[..^2];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        if (decibels)
            value = Math.Pow(10, value / 20);
        return true;
    }

    // "a=1:b=2" or a first value without a name (volume=-3dB, atempo=2): the first is stored under the filter's first option's name.
    private static Dictionary<string, string> Options(string text)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = true;
        foreach (var part in SplitTopLevel(text, ':'))
        {
            var equals = part.IndexOf('=');
            if (equals < 0)
            {
                if (first)
                {
                    foreach (var name in (ReadOnlySpan<string>)["volume", "tempo", "sample_rate", "threshold", "frequency"])
                        options.TryAdd(name, part);
                }
            }
            else
            {
                options[part[..equals].Trim()] = part[(equals + 1)..].Trim();
            }

            first = false;
        }

        return options;
    }

    // Splits at a separator outside quotes and brackets.
    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var (depth, quoted, start) = (0, false, 0);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
                quoted = !quoted;
            else if (!quoted && c is '(' or '[')
                depth++;
            else if (!quoted && c is ')' or ']')
                depth--;
            else if (!quoted && depth == 0 && c == separator)
            {
                if (i > start)
                    yield return text[start..i];
                start = i + 1;
            }
        }

        if (start < text.Length)
            yield return text[start..];
    }
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
        _ => new GainEffect(1),
    };

    /// <summary>Processes interleaved stereo in place.</summary>
    public abstract void Process(Span<float> stereo, int frames);

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

    public BiquadEffect(in AudioEffectSpec spec, int sampleRate)
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

        (_b0, _b1, _b2, _a1, _a2) = (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
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

/// <summary>The ducking: a strip pressed down by the voices' side chain (sidechaincompress, FilterGraphBuilder.DuckFilter).</summary>
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
/// by the attack less one sample (239 samples at 48 kHz and 5 ms; the export, without the latency option, is late by the
/// same). A sample over the limit sets the slope the gain falls along to reach limit / peak as that sample comes out; a
/// louder one later replaces it, a gentler one queues behind it; past the last the gain climbs back by (1 - gain) over the
/// release. Its auto level (on by default in FFmpeg, off in what HandPeg writes) and level_out are the output's factor.
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

        // config_input's sizes: the ring is the attack's samples (whole frames), the queues as long as 100 ms.
        _size = (int)(sampleRate * (spec.P2 / 1000) * Channels);
        _size = Math.Max(_size - _size % Channels, Channels);
        var capacity = Math.Max((int)(sampleRate * Channels * 100 / 1000.0 + Channels), _size + Channels);
        (_buffer, _nextDelta, _nextPos) = (new double[capacity], new double[capacity], new int[capacity]);
        Array.Fill(_nextPos, -1);
    }

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
