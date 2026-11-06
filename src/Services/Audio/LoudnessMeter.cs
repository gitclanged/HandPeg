namespace HandPegApp.Services.Audio;

// ----- Loudness: an export's (integrated, after the render) and the live meter's (Pass 5.2B, on the mixer's thread) -----
// Both after ITU-R BS.1770-4 / EBU R128 and both K-weighted by the same two filters (KFilter): the export's normalization measures a whole
// track once it is rendered (LoudnessMeter, until 5.2B in ExportSound.cs); the meter measures the mix as it is played, an export's level
// (after the Sequence's master gain, before the monitor volume: WasapiAudioEngine.Meter), its momentary, short-term and gated integrated
// loudness, its 4x-oversampled true peak and a hold of it (LiveLoudness), with nothing made while it runs.

/// <summary>
/// Integrated loudness after ITU-R BS.1770-4 / EBU R128 (5.2-Audit, loudness normalization of an export): each channel K-weighted
/// (the standard's high shelf and high-pass, worked out for the sample rate), mean squares over 400 ms blocks a quarter apart,
/// summed with the standard's weights (the surround channels 1.41, the LFE left out), gated at -70 LUFS and then 10 LU under the
/// mean of what is left. The sample peak beside it.
/// </summary>
public sealed class LoudnessMeter
{
    private readonly int _channels, _step, _blockSteps;
    private readonly double[] _weights;
    private readonly KFilter[] _shelf, _pass;
    private readonly double[] _stepSums;
    private readonly Queue<double> _recentSteps = new();
    private readonly List<double> _blocks = [];
    private long _inStep;

    public LoudnessMeter(int sampleRate, int channels)
    {
        _channels = Math.Max(channels, 1);
        _step = Math.Max(sampleRate / 10, 1);
        _blockSteps = 4;
        _stepSums = new double[_channels];
        _weights = Enumerable.Range(0, _channels).Select(c => _channels >= 5 && c == 3 ? 0 : _channels >= 5 && c >= 4 ? 1.41 : 1.0).ToArray();
        _shelf = Enumerable.Range(0, _channels).Select(_ => KFilter.Shelf(sampleRate)).ToArray();
        _pass = Enumerable.Range(0, _channels).Select(_ => KFilter.HighPass(sampleRate)).ToArray();
    }

    /// <summary>The largest sample magnitude seen.</summary>
    public double SamplePeak { get; private set; }

    /// <summary>One frame (a sample of each channel).</summary>
    public void Add(ReadOnlySpan<float> frame)
    {
        for (var c = 0; c < _channels && c < frame.Length; c++)
        {
            double x = frame[c];
            SamplePeak = Math.Max(SamplePeak, Math.Abs(x));
            var y = _pass[c].Next(_shelf[c].Next(x));
            _stepSums[c] += y * y;
        }

        if (++_inStep < _step)
            return;

        var weighted = 0.0;
        for (var c = 0; c < _channels; c++)
            (weighted, _stepSums[c]) = (weighted + _weights[c] * _stepSums[c], 0);
        _recentSteps.Enqueue(weighted / _step);
        _inStep = 0;
        if (_recentSteps.Count > _blockSteps)
            _recentSteps.Dequeue();
        if (_recentSteps.Count == _blockSteps)
            _blocks.Add(_recentSteps.Average());
    }

    /// <summary>The integrated loudness so far, LUFS; negative infinity for silence (or less than one block).</summary>
    public double IntegratedLufs
    {
        get
        {
            static double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare);
            var absolute = _blocks.Where(b => b > 0 && Lufs(b) > -70).ToList();
            if (absolute.Count == 0)
                return double.NegativeInfinity;
            var relative = Lufs(absolute.Average()) - 10;
            var gated = absolute.Where(b => Lufs(b) > relative).ToList();
            return gated.Count == 0 ? double.NegativeInfinity : Lufs(gated.Average());
        }
    }
}

/// <summary>A direct-form biquad in double precision: BS.1770's K-weighting, its two stages (LoudnessMeter's, LiveLoudness's).</summary>
internal sealed class KFilter(double b0, double b1, double b2, double a1, double a2)
{
    private double _x1, _x2, _y1, _y2;

    public double Next(double x)
    {
        var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
        (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
        return y;
    }

    /// <summary>Denormals let go of, in the silence after a sound.</summary>
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

    // BS.1770's pre-filter: a high shelf of about +4 dB from 1.5 kHz (its published 48 kHz coefficients, re-derived for any rate).
    public static KFilter Shelf(int rate)
    {
        const double f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / rate);
        var vh = Math.Pow(10, gain / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        return new KFilter((vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
    }

    // BS.1770's RLB weighting: a second-order high-pass at about 38 Hz.
    public static KFilter HighPass(int rate)
    {
        const double f0 = 38.13547087602444, q = 0.5003270373238773;
        var k = Math.Tan(Math.PI * f0 / rate);
        var a0 = 1 + k / q + k * k;
        return new KFilter(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
    }
}

/// <summary>
/// The live meter's loudness and true peak (5.2B), on the mixer's thread with everything made beforehand: stereo, each side K-weighted, mean
/// squares over 100 ms steps; the momentary loudness over the last 4 steps (400 ms), the short-term over the last 30 (3 s), and the gated
/// integrated loudness since <see cref="Restart"/> over every 400 ms block a step apart (a histogram of the blocks, a tenth of an LU a bin from
/// -70 to +5 LUFS, so the gates never need the blocks themselves: the absolute gate at -70, the relative one 10 LU under what passes it).
/// The true peak is the largest of the samples and of the three points between each two that a 4x polyphase interpolator finds (ITU-R
/// BS.1770-4 Annex 2's structure: four phases of twelve taps, a Kaiser-windowed sinc made here, each phase's gain 1), held over 3 s too.
/// </summary>
internal sealed class LiveLoudness
{
    private const int Taps = 12, Phases = 4, MomentarySteps = 4, ShortSteps = 30, HoldSteps = 30;
    private const double Lowest = -70, Highest = 5, BinWidth = 0.1;
    private static readonly int Bins = (int)Math.Round((Highest - Lowest) / BinWidth);

    // The interpolator's phases 1 to 3 (phase 0 is the sample itself), tap j weighing the sample j before the newest.
    private static readonly double[] Interpolator = MakeInterpolator();

    private readonly int _step;
    private readonly KFilter _shelfLeft, _shelfRight, _passLeft, _passRight;
    private readonly double[] _steps = new double[ShortSteps];
    private readonly double[] _histogramEnergy = new double[Bins];
    private readonly int[] _histogramCount = new int[Bins];
    private readonly float[] _holdSteps = new float[HoldSteps];
    private readonly double[] _historyLeft = new double[Taps], _historyRight = new double[Taps];
    private int _historyAt, _stepCount, _stepAt, _holdAt, _inStep;
    private double _stepSum;
    private float _stepPeak;

    public LiveLoudness(int sampleRate)
    {
        _step = Math.Max(sampleRate / 10, 1);
        (_shelfLeft, _shelfRight) = (KFilter.Shelf(sampleRate), KFilter.Shelf(sampleRate));
        (_passLeft, _passRight) = (KFilter.HighPass(sampleRate), KFilter.HighPass(sampleRate));
    }

    /// <summary>The momentary loudness (400 ms), LUFS; negative infinity in silence and before the first step.</summary>
    public float MomentaryLufs { get; private set; } = float.NegativeInfinity;

    /// <summary>The short-term loudness (3 s), LUFS.</summary>
    public float ShortTermLufs { get; private set; } = float.NegativeInfinity;

    /// <summary>The gated integrated loudness since <see cref="Restart"/>, LUFS.</summary>
    public float IntegratedLufs { get; private set; } = float.NegativeInfinity;

    /// <summary>The highest true peak (a factor) of both sides since <see cref="TakePeak"/> was last called.</summary>
    public float Peak { get; private set; }

    /// <summary>The highest true peak over the last 3 s (a factor).</summary>
    public float HeldPeak { get; private set; }

    /// <summary>A sample or a point between two reached full scale since <see cref="TakePeak"/>.</summary>
    public bool Clipped { get; private set; }

    /// <summary>The peak and the clip since the last call, and from now on afresh.</summary>
    public (float Peak, bool Clipped) TakePeak()
    {
        var taken = (Peak, Clipped);
        (Peak, Clipped) = (0, false);
        return taken;
    }

    /// <summary>From here on (play started, a seek): the loudness and its blocks begin afresh; the filters and the peak's hold run on.</summary>
    public void Restart()
    {
        Array.Clear(_steps);
        Array.Clear(_histogramEnergy);
        Array.Clear(_histogramCount);
        (_stepCount, _stepAt, _inStep, _stepSum) = (0, 0, 0, 0);
        (MomentaryLufs, ShortTermLufs, IntegratedLufs) = (float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
    }

    /// <summary>A block of the mix (interleaved stereo).</summary>
    public void Add(ReadOnlySpan<float> stereo, int frames)
    {
        var interpolator = Interpolator;
        for (var i = 0; i < frames; i++)
        {
            double l = stereo[2 * i], r = stereo[2 * i + 1];

            // The true peak: the sample and the three points the interpolator puts before it.
            _historyAt = _historyAt == 0 ? Taps - 1 : _historyAt - 1;
            (_historyLeft[_historyAt], _historyRight[_historyAt]) = (l, r);
            var peak = Math.Max(Math.Abs(l), Math.Abs(r));
            for (var phase = 0; phase < Phases - 1; phase++)
            {
                var (sumLeft, sumRight) = (0.0, 0.0);
                for (var j = 0; j < Taps; j++)
                {
                    var tap = interpolator[phase * Taps + j];
                    var at = _historyAt + j < Taps ? _historyAt + j : _historyAt + j - Taps;
                    sumLeft += tap * _historyLeft[at];
                    sumRight += tap * _historyRight[at];
                }

                peak = Math.Max(peak, Math.Max(Math.Abs(sumLeft), Math.Abs(sumRight)));
            }

            var truePeak = (float)peak;
            if (truePeak > Peak)
                Peak = truePeak;
            if (truePeak > _stepPeak)
                _stepPeak = truePeak;
            if (truePeak >= 1)
                Clipped = true;

            // The loudness: each side K-weighted, its square summed over the step.
            var kl = _passLeft.Next(_shelfLeft.Next(l));
            var kr = _passRight.Next(_shelfRight.Next(r));
            _stepSum += kl * kl + kr * kr;
            if (++_inStep == _step)
                EndStep();
        }

        _shelfLeft.Flush();
        _shelfRight.Flush();
        _passLeft.Flush();
        _passRight.Flush();
    }

    private static float Lufs(double meanSquare) => meanSquare > 0 ? (float)(-0.691 + 10 * Math.Log10(meanSquare)) : float.NegativeInfinity;

    // A 100 ms step done: the windows' loudness, the block ending here into the histogram, the integrated loudness, the peak's hold.
    private void EndStep()
    {
        _steps[_stepAt] = _stepSum / _step;
        _stepAt = (_stepAt + 1) % ShortSteps;
        _stepCount = Math.Min(_stepCount + 1, int.MaxValue - 1);
        (_inStep, _stepSum) = (0, 0);

        MomentaryLufs = Lufs(Mean(MomentarySteps));
        ShortTermLufs = Lufs(Mean(ShortSteps));
        if (_stepCount >= MomentarySteps)
        {
            var block = Mean(MomentarySteps);
            var loudness = Lufs(block);
            if (loudness > Lowest)
            {
                var bin = Math.Clamp((int)((loudness - Lowest) / BinWidth), 0, Bins - 1);
                _histogramEnergy[bin] += block;
                _histogramCount[bin]++;
            }

            IntegratedLufs = Integrated();
        }

        _holdSteps[_holdAt] = _stepPeak;
        _holdAt = (_holdAt + 1) % HoldSteps;
        _stepPeak = 0;
        var held = 0f;
        foreach (var stepPeak in _holdSteps)
            held = Math.Max(held, stepPeak);
        HeldPeak = held;
    }

    // The mean square of the last so many steps (those there are, while fewer have passed).
    private double Mean(int steps)
    {
        var count = Math.Min(steps, _stepCount);
        if (count <= 0)
            return 0;
        var sum = 0.0;
        for (var k = 1; k <= count; k++)
            sum += _steps[(_stepAt - k + ShortSteps) % ShortSteps];
        return sum / count;
    }

    // The gated integrated loudness from the histogram: the blocks over -70 LUFS, then those over 10 LU under their mean.
    private float Integrated()
    {
        var (energy, count) = (0.0, 0L);
        for (var bin = 0; bin < Bins; bin++)
            (energy, count) = (energy + _histogramEnergy[bin], count + _histogramCount[bin]);
        if (count == 0)
            return float.NegativeInfinity;

        var relative = Lufs(energy / count) - 10;
        (energy, count) = (0.0, 0L);
        for (var bin = 0; bin < Bins; bin++)
        {
            if (Lowest + (bin + 0.5) * BinWidth > relative)
                (energy, count) = (energy + _histogramEnergy[bin], count + _histogramCount[bin]);
        }

        return count == 0 ? float.NegativeInfinity : Lufs(energy / count);
    }

    // Phases 1 to 3 of the 4x interpolator: a sinc cut at the input's Nyquist frequency, under a Kaiser window (beta 6) 48 taps long and
    // centred on tap 24, so phase 0 is the sample itself and phase p the point p/4 of the way from the sample 6 before the newest to the next.
    private static double[] MakeInterpolator()
    {
        const int length = Taps * Phases, centre = length / 2;
        const double beta = 6;
        var taps = new double[(Phases - 1) * Taps];
        for (var phase = 1; phase < Phases; phase++)
        {
            var sum = 0.0;
            for (var j = 0; j < Taps; j++)
            {
                var m = j * Phases + phase;
                var t = (m - centre) / (double)Phases;
                var sinc = Math.Abs(t) < 1e-12 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                var x = (m - centre) / (double)centre;
                var window = BesselI0(beta * Math.Sqrt(Math.Max(1 - x * x, 0))) / BesselI0(beta);
                taps[(phase - 1) * Taps + j] = sinc * window;
                sum += sinc * window;
            }

            for (var j = 0; j < Taps; j++)
                taps[(phase - 1) * Taps + j] /= sum;
        }

        return taps;
    }

    private static double BesselI0(double x)
    {
        var (sum, term) = (1.0, 1.0);
        for (var k = 1; k < 40; k++)
        {
            term *= x / (2 * k) * (x / (2 * k));
            sum += term;
            if (term < sum * 1e-16)
                break;
        }

        return sum;
    }
}
