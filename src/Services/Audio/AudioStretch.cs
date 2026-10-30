using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

namespace HandPegApp.Services.Audio;

// ----- Rate and time: the resampler and the time stretch (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// A voice's sound goes from its file's rate to the device's through a polyphase windowed-sinc resampler (64 taps, a Kaiser
// window, 512 phases with the two nearest blended), and is played faster or slower without its pitch changing by WSOLA
// (a Hann overlap-add whose every frame is moved, within 10 ms, to where it best continues the frame before). Both pull
// what they need from the stage before them and never wait: told nothing more is there yet, they give what they have.

/// <summary>Planar stereo, pulled.</summary>
internal interface IStereoSource
{
    /// <summary>Fills as much of the spans as it can now (both the same length): fewer only when what it reads from has nothing more yet.</summary>
    int Read(Span<float> left, Span<float> right);
}

/// <summary>
/// Any ratio, read from its source at <see cref="Step"/> input samples per output sample, from a fractional start. With a
/// step of exactly 1 and a whole start it copies, so a file at the device's rate comes out sample for sample.
/// </summary>
internal sealed class PolyphaseResampler : IStereoSource
{
    public const int Taps = 64;
    public const int HalfTaps = Taps / 2;
    private const int Phases = 512;
    private const int Capacity = 16384;

    // The tables by cutoff (in thousandths of the input rate): a few ever exist (44.1 to 48, 48 to 48 shifted, 96 to 48).
    private static readonly ConcurrentDictionary<int, float[]> Tables = new();

    private readonly float[] _left = new float[Capacity], _right = new float[Capacity];
    private float[] _table = [];
    private IStereoSource? _source;
    private int _count;
    private double _position;
    private double _step = 1;
    private long _skip;
    private bool _copy;

    public double Step => _step;

    /// <summary>
    /// Starts reading <paramref name="source"/> afresh: the first output sample is the source's sample
    /// <paramref name="start"/> (fractional; the source's first sample is 0). What is before start less half the
    /// kernel is read and dropped.
    /// </summary>
    public void Reset(IStereoSource source, double step, double start)
    {
        (_source, _step, _count) = (source, step, 0);
        _copy = Math.Abs(step - 1) < 1e-9 && Math.Abs(start - Math.Round(start)) < 1e-6;
        if (_copy)
        {
            _skip = Math.Max((long)Math.Round(start), 0);
            _position = 0;
            return;
        }

        // The kernel reaches half its width back: what lies before the source's first sample is silence.
        var first = (long)Math.Floor(start) - HalfTaps + 1;
        if (first >= 0)
        {
            (_skip, _position) = (first, start - first);
        }
        else
        {
            _count = (int)Math.Min(-first, Capacity / 2);
            _left.AsSpan(0, _count).Clear();
            _right.AsSpan(0, _count).Clear();
            (_skip, _position) = (0, start + _count);
        }

        var cutoff = 0.46 * Math.Min(1, 1 / step);
        _table = Tables.GetOrAdd((int)Math.Round(cutoff * 1000), Build);
    }

    public int Read(Span<float> left, Span<float> right)
    {
        if (_source is null)
            return 0;
        if (!Skipped())
            return 0;

        var produced = 0;
        while (produced < left.Length)
        {
            if (_copy)
            {
                var at = (int)_position;
                if (at >= _count && !Refill())
                    break;
                at = (int)_position;
                var take = Math.Min(left.Length - produced, _count - at);
                _left.AsSpan(at, take).CopyTo(left[produced..]);
                _right.AsSpan(at, take).CopyTo(right[produced..]);
                (_position, produced) = (_position + take, produced + take);
                continue;
            }

            var i0 = (int)Math.Floor(_position);
            if (i0 + HalfTaps >= _count && !Refill())
                break;
            if (i0 + HalfTaps >= _count)
                continue;

            var fraction = _position - i0;
            var phase = fraction * Phases;
            var p = Math.Min((int)phase, Phases - 1);
            var blend = (float)(phase - p);
            var row0 = _table.AsSpan(p * Taps, Taps);
            var row1 = _table.AsSpan((p + 1) * Taps, Taps);
            var start = i0 - HalfTaps + 1;
            (left[produced], right[produced]) = Dot(row0, row1, blend, _left.AsSpan(start, Taps), _right.AsSpan(start, Taps));
            _position += _step;
            produced++;
        }

        return produced;
    }

    private static (float Left, float Right) Dot(ReadOnlySpan<float> row0, ReadOnlySpan<float> row1, float blend, ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var r0 = MemoryMarshal.Cast<float, Vector<float>>(row0);
        var r1 = MemoryMarshal.Cast<float, Vector<float>>(row1);
        var l = MemoryMarshal.Cast<float, Vector<float>>(left);
        var r = MemoryMarshal.Cast<float, Vector<float>>(right);
        var b = new Vector<float>(blend);
        Vector<float> accL = default, accR = default;
        for (var k = 0; k < r0.Length; k++)
        {
            var h = r0[k] + (r1[k] - r0[k]) * b;
            accL += h * l[k];
            accR += h * r[k];
        }

        var (sumL, sumR) = (Vector.Sum(accL), Vector.Sum(accR));
        for (var k = r0.Length * Vector<float>.Count; k < row0.Length; k++)
        {
            var h = row0[k] + (row1[k] - row0[k]) * blend;
            (sumL, sumR) = (sumL + h * left[k], sumR + h * right[k]);
        }

        return (sumL, sumR);
    }

    // What lies before the start is read and dropped (a little: the caller seeks to just before it).
    private bool Skipped()
    {
        while (_skip > 0)
        {
            var take = (int)Math.Min(_skip, Capacity);
            var got = _source!.Read(_left.AsSpan(0, take), _right.AsSpan(0, take));
            if (got == 0)
                return false;
            _skip -= got;
        }

        return true;
    }

    // More of the source, after what is kept of the old (the kernel's reach back from where reading is).
    private bool Refill()
    {
        var keep = Math.Max((int)Math.Floor(_position) - HalfTaps + 1, 0);
        if (keep > 0)
        {
            var left = _count - keep;
            if (left > 0)
            {
                _left.AsSpan(keep, left).CopyTo(_left);
                _right.AsSpan(keep, left).CopyTo(_right);
            }

            _count = Math.Max(left, 0);
            _position -= keep;
        }

        if (_count >= Capacity)
            return false;
        var got = _source!.Read(_left.AsSpan(_count), _right.AsSpan(_count));
        _count += got;
        return got > 0;
    }

    /// <summary>
    /// Phases + 1 rows of the kernel (the last for blending past the last phase): row p holds the weights of the 64 samples
    /// around a point p / Phases past a sample, a Kaiser-windowed sinc (beta 9, about -90 dB) at the cutoff, each row
    /// summing to 1 so that no phase changes the level.
    /// </summary>
    private static float[] Build(int thousandths)
    {
        var cutoff = thousandths / 1000.0;
        const double beta = 9;
        var table = new float[(Phases + 1) * Taps];
        var denominator = BesselI0(beta);
        for (var p = 0; p <= Phases; p++)
        {
            var phi = (double)p / Phases;
            var sum = 0.0;
            var row = new double[Taps];
            for (var k = 0; k < Taps; k++)
            {
                var t = phi + HalfTaps - 1 - k;
                var x = 2 * cutoff * t;
                var sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                var u = t / HalfTaps;
                var window = Math.Abs(u) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - u * u)) / denominator;
                row[k] = 2 * cutoff * sinc * window;
                sum += row[k];
            }

            for (var k = 0; k < Taps; k++)
                table[p * Taps + k] = (float)(row[k] / sum);
        }

        return table;
    }

    private static double BesselI0(double x)
    {
        var (sum, term) = (1.0, 1.0);
        var half = x / 2;
        for (var k = 1; k < 64; k++)
        {
            term *= half / k;
            var squared = term * term;
            sum += squared;
            if (squared < sum * 1e-17)
                break;
        }

        return sum;
    }
}

/// <summary>
/// WSOLA: the sound at <see cref="Tempo"/> times its speed, its pitch kept. Frames of 40 ms, Hann windows overlapping by
/// half; each frame is read from where the nominal time puts it, moved within 10 ms to where its first half best matches
/// what the frame before would have gone on with (searched on the two channels' sum at half the rate, then refined to the
/// sample). Output sample j is the input around sample j x tempo: the frames' middles are placed on that line, so the time
/// never drifts (the moves only wander about it).
/// The match is the squared difference measured against both sides' power, 1 - |c - t|^2 / (|c|^2 + |t|^2) = 2 c.t / (|c|^2 +
/// |t|^2): unlike a correlation normalized by the candidate alone, it tells loud from quiet. After a sound stops, the frame
/// that goes on with silence is read from silence, never from the sound's end again: a burst at half speed is heard once.
/// </summary>
internal sealed class WsolaStretcher : IStereoSource
{
    private const int Capacity = 1 << 17;

    // What keeps the match defined where both sides are digital silence (they then match perfectly).
    private const double Silence = 1e-9;

    private IStereoSource? _source;
    private int _window, _hop, _search;
    private float[] _hann = [];
    private readonly float[] _inL = new float[Capacity], _inR = new float[Capacity];
    private long _inBase;
    private int _inCount;
    private float[] _accL = [], _accR = [];
    private float[] _mono = [], _template = [];
    private int _ready, _readyAt;
    private long _frame;
    private long _previous = -1;
    private bool _ended;

    public double Tempo { get; private set; } = 1;

    public void Reset(IStereoSource source, double tempo, int sampleRate)
    {
        (_source, Tempo) = (source, Math.Clamp(tempo, 0.05, 64));
        var window = Math.Max((int)(sampleRate * 0.040) & ~1, 64);
        if (window != _window)
        {
            (_window, _hop, _search) = (window, window / 2, Math.Max(sampleRate / 100, 8));
            _hann = new float[_window];
            for (var n = 0; n < _window; n++)
                _hann[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / _window));
            _accL = new float[_window];
            _accR = new float[_window];
            _mono = new float[(2 * _search + _hop) / 2 + 2];
            _template = new float[_hop / 2 + 1];
        }

        Array.Clear(_accL);
        Array.Clear(_accR);
        (_inBase, _inCount, _ready, _readyAt, _frame, _previous, _ended) = (0, 0, 0, 0, 0, -1, false);
    }

    public int Read(Span<float> left, Span<float> right)
    {
        var produced = 0;
        while (produced < left.Length)
        {
            if (_ready > 0)
            {
                var take = Math.Min(_ready, left.Length - produced);
                _accL.AsSpan(_readyAt, take).CopyTo(left[produced..]);
                _accR.AsSpan(_readyAt, take).CopyTo(right[produced..]);
                (produced, _ready, _readyAt) = (produced + take, _ready - take, _readyAt + take);
                if (_ready == 0)
                    Shift();
                continue;
            }

            if (!NextFrame())
                break;
        }

        return produced;
    }

    // After the ready half is read: the accumulator moves on by a hop.
    private void Shift()
    {
        _accL.AsSpan(_hop).CopyTo(_accL);
        _accR.AsSpan(_hop).CopyTo(_accR);
        _accL.AsSpan(_hop).Clear();
        _accR.AsSpan(_hop).Clear();
        _readyAt = 0;
    }

    private bool NextFrame()
    {
        // Frame k's middle (output k * hop + hop) is on the input's tempo line.
        var nominal = (long)Math.Round((_frame * _hop + _hop) * Tempo - _hop);
        var lowest = _previous < 0 ? Math.Max(nominal, 0) : Math.Max(nominal - _search, 0);
        var highest = _previous < 0 ? lowest : nominal + _search;
        var needed = Math.Max(highest + _window, _previous + _hop + _hop);
        if (!Have(needed))
            return false;

        var at = _previous < 0 ? lowest : Best(nominal, lowest, highest);
        var offset = (int)(at - _inBase);
        for (var n = 0; n < _window; n++)
        {
            _accL[n] += _inL[offset + n] * _hann[n];
            _accR[n] += _inR[offset + n] * _hann[n];
        }

        (_previous, _frame, _ready, _readyAt) = (at, _frame + 1, _hop, 0);
        Drop(Math.Min(at, Math.Max(nominal - _search, 0)));
        return true;
    }

    // The move of the frame that best continues the one before.
    private long Best(long nominal, long lowest, long highest)
    {
        var templateAt = (int)(_previous + _hop - _inBase);
        var half = _hop / 2;
        var templateEnergy = 0.0;
        for (var j = 0; j < half; j++)
        {
            var m = _template[j] = _inL[templateAt + 2 * j] + _inR[templateAt + 2 * j];
            templateEnergy += m * m;
        }

        var regionAt = (int)(lowest - _inBase);
        var span = (int)(highest - lowest);
        var monoCount = Math.Min((span + _hop) / 2 + 1, _mono.Length);
        for (var j = 0; j < monoCount; j++)
        {
            var i = regionAt + 2 * j;
            _mono[j] = i < _inCount ? _inL[i] + _inR[i] : 0;
        }

        var (best, bestScore) = (lowest, double.NegativeInfinity);
        var template = _template.AsSpan(0, half);
        for (var c = 0; 2 * c <= span && c + half <= monoCount; c++)
        {
            var candidate = _mono.AsSpan(c, half);
            var (dot, energy) = DotAndEnergy(candidate, template);
            var score = (2 * dot + Silence) / (energy + templateEnergy + Silence);
            if (score > bestScore)
                (bestScore, best) = (score, lowest + 2 * c);
        }

        // To the sample: either side of the coarse best, at the full rate.
        var refined = best;
        var refinedScore = double.NegativeInfinity;
        for (var d = -1; d <= 1; d++)
        {
            var start = best + d;
            if (start < lowest || start > highest)
                continue;
            var (dot, energy) = (0.0, 0.0);
            var s = (int)(start - _inBase);
            for (var j = 0; j < half; j++)
            {
                var m = _inL[s + 2 * j] + _inR[s + 2 * j];
                (dot, energy) = (dot + m * _template[j], energy + m * m);
            }

            var score = (2 * dot + Silence) / (energy + templateEnergy + Silence);
            if (score > refinedScore)
                (refinedScore, refined) = (score, start);
        }

        return Math.Clamp(refined, lowest, highest);
    }

    private static (double Dot, double Energy) DotAndEnergy(ReadOnlySpan<float> candidate, ReadOnlySpan<float> template)
    {
        var c = MemoryMarshal.Cast<float, Vector<float>>(candidate);
        var t = MemoryMarshal.Cast<float, Vector<float>>(template);
        Vector<float> dot = default, energy = default;
        for (var i = 0; i < c.Length; i++)
        {
            dot += c[i] * t[i];
            energy += c[i] * c[i];
        }

        double d = Vector.Sum(dot), e = Vector.Sum(energy);
        for (var i = c.Length * Vector<float>.Count; i < candidate.Length; i++)
            (d, e) = (d + candidate[i] * template[i], e + candidate[i] * candidate[i]);
        return (d, e);
    }

    // Whether the input reaches the absolute sample index (exclusive), reading more as far as the source has it.
    private bool Have(long end)
    {
        while (_inBase + _inCount < end)
        {
            if (_ended || _inCount >= Capacity)
                return _inBase + _inCount >= end;
            var got = _source!.Read(_inL.AsSpan(_inCount), _inR.AsSpan(_inCount));
            if (got == 0)
                return false;
            _inCount += got;
        }

        return true;
    }

    // What no later frame can reach is let go.
    private void Drop(long before)
    {
        var drop = (int)Math.Min(before - _inBase, _inCount);
        if (drop < Capacity / 4)
            return;
        _inL.AsSpan(drop, _inCount - drop).CopyTo(_inL);
        _inR.AsSpan(drop, _inCount - drop).CopyTo(_inR);
        (_inBase, _inCount) = (_inBase + drop, _inCount - drop);
    }
}
