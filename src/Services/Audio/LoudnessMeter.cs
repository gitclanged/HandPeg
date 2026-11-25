using System.Runtime.Intrinsics;

namespace HandPegApp.Services.Audio;

// ----- Loudness: export integrated loudness (post-render) and the live meter (mixer thread) -----
// Both follow ITU-R BS.1770-4 / EBU R128 with the same K-weighting filters (KFilter). LoudnessMeter measures a rendered export track for
// normalization; LiveLoudness meters the mix during playback at export level (after the master gain, before monitor volume:
// WasapiAudioEngine.Meter): momentary, short-term and gated integrated loudness, 4x-oversampled true peak and its hold, allocation-free.

/// <summary>
/// Integrated loudness per ITU-R BS.1770-4 / EBU R128 (export loudness normalization): K-weighted channels (high shelf + high-pass derived
/// for the sample rate), mean squares over 400 ms blocks with 75 % overlap, channel weights (surrounds 1.41, LFE excluded), gated at -70 LUFS,
/// then 10 LU below the mean of the remainder. Tracks the sample peak alongside.
/// </summary>
public sealed class LoudnessMeter
{
    private readonly int _channels, _step, _blockSteps;
    private readonly double[] _weights;

    // Channels processed in pairs (one SIMD lane each): filters, per-step square sums and peaks.
    private readonly BiquadPair[] _shelf, _pass;
    private readonly Vector128<double>[] _stepSums;
    private Vector128<double> _peaks;
    private readonly Queue<double> _recentSteps = new();
    private readonly List<double> _blocks = [];
    private long _inStep;

    public LoudnessMeter(int sampleRate, int channels)
    {
        _channels = Math.Max(channels, 1);
        _step = Math.Max(sampleRate / 10, 1);
        _blockSteps = 4;
        var pairs = (_channels + 1) / 2;
        _stepSums = new Vector128<double>[pairs];
        _weights = Enumerable.Range(0, _channels).Select(c => _channels >= 5 && c == 3 ? 0 : _channels >= 5 && c >= 4 ? 1.41 : 1.0).ToArray();
        _shelf = Enumerable.Range(0, pairs).Select(_ => KFilter.Shelf(sampleRate).Paired()).ToArray();
        _pass = Enumerable.Range(0, pairs).Select(_ => KFilter.HighPass(sampleRate).Paired()).ToArray();
    }

    /// <summary>Largest sample magnitude seen.</summary>
    public double SamplePeak => Math.Max(_peaks.GetElement(0), _peaks.GetElement(1));

    /// <summary>Adds one frame (one sample per channel; missing channels are silence).</summary>
    public void Add(ReadOnlySpan<float> frame)
    {
        if (frame.Length >= _channels)
        {
            Add(frame, 1);
            return;
        }

        Span<float> whole = stackalloc float[_channels];
        frame.CopyTo(whole);
        Add(whole, 1);
    }

    /// <summary>Adds interleaved frames (one sample per channel each), e.g. an export track block by block.</summary>
    public void Add(ReadOnlySpan<float> interleaved, int frames)
    {
        frames = Math.Min(frames, interleaved.Length / _channels);
        for (var done = 0; done < frames;)
        {
            var run = (int)Math.Min(frames - done, _step - _inStep);
            for (var pair = 0; pair < _shelf.Length; pair++)
                Weigh(interleaved.Slice(done * _channels, run * _channels), run, pair);
            done += run;
            if ((_inStep += run) < _step)
                continue;

            var weighted = 0.0;
            for (var c = 0; c < _channels; c++)
                weighted += _weights[c] * _stepSums[c / 2].GetElement(c % 2);
            Array.Clear(_stepSums);
            _recentSteps.Enqueue(weighted / _step);
            _inStep = 0;
            if (_recentSteps.Count > _blockSteps)
                _recentSteps.Dequeue();
            if (_recentSteps.Count == _blockSteps)
                _blocks.Add(_recentSteps.Average());
        }
    }

    // K-weights a channel pair, one lane each (lane arithmetic matches the scalar filter order, so loudness is bit-identical to per-channel
    // processing), summing squares and tracking peaks. With an odd channel count the last channel also fills the unused second lane.
    private void Weigh(ReadOnlySpan<float> samples, int frames, int pair)
    {
        var (channels, first) = (_channels, 2 * pair);
        var second = first + 1 < channels ? first + 1 : first;
        var (shelf, pass, sum, peaks) = (_shelf[pair], _pass[pair], _stepSums[pair], _peaks);
        for (var i = 0; i < frames; i++)
        {
            var x = Vector128.Create((double)samples[i * channels + first], samples[i * channels + second]);
            peaks = Vector128.Max(peaks, Vector128.Abs(x));
            var y = pass.Next(shelf.Next(x));
            sum += y * y;
        }

        (_shelf[pair], _pass[pair], _stepSums[pair], _peaks) = (shelf, pass, sum, peaks);
    }

    /// <summary>Integrated loudness so far, LUFS; negative infinity for silence or less than one block.</summary>
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

/// <summary>
/// 1-click speech auto-level (the clip gain line's double-click): a clip's own sound is measured as <see cref="LoudnessMeter"/> measures an
/// export (BS.1770 gating drops the pauses between phrases) over the file range it plays, and its clip gain set so it reaches
/// <see cref="TargetLufs"/> integrated, held so its sample peak stays under <see cref="CeilingDb"/>, within the clip gain range.
/// </summary>
public static class SpeechLevel
{
    /// <summary>Speech target, LUFS integrated (the streaming platforms' level; the meter's gold mark).</summary>
    public const double TargetLufs = -14;

    /// <summary>Highest sample peak after the gain, dBFS.</summary>
    public const double CeilingDb = -1;

    /// <summary>Clip gain range (TimelineTransaction.SetClipGain), dB.</summary>
    public const double LeastGainDb = -40, MostGainDb = 24;

    /// <summary>
    /// Clip gain (dB, to 0.1) bringing a sound measured at 0 dB to <paramref name="targetLufs"/>, held by its peak's headroom under
    /// <paramref name="ceilingDb"/> and to the clip gain range; null when nothing was heard (silence, or under one 400 ms block). Pure.
    /// </summary>
    public static double? GainDb(double integratedLufs, double samplePeak, double targetLufs = TargetLufs, double ceilingDb = CeilingDb)
    {
        if (!double.IsFinite(integratedLufs) || !double.IsFinite(targetLufs))
            return null;
        var gain = targetLufs - integratedLufs;
        if (double.IsFinite(samplePeak) && samplePeak > 0)
            gain = Math.Min(gain, ceilingDb - 20 * Math.Log10(samplePeak));
        return Math.Round(Math.Clamp(gain, LeastGainDb, MostGainDb), 1);
    }

    /// <summary>
    /// Integrated loudness (LUFS) and sample peak of a file's audio stream over <paramref name="seconds"/> of media time from
    /// <paramref name="from"/> (the engine's clock: from the first video frame), at the decoder's own rate, 0 dB. Blocking; cancellable.
    /// </summary>
    public static (double Lufs, double Peak) Measure(string path, int stream, double from, double seconds, CancellationToken token)
    {
        if (!System.IO.File.Exists(path))
            throw new System.IO.FileNotFoundException("The file is not there.", path);

        var source = new AudioSource(path, Math.Max(stream, 0)) { VideoStart = MediaProbe.KnownVideoStart(path) ?? double.NaN };
        using var decoder = AudioDecoder.Open(source, 48000);
        decoder.Seek(Math.Max(from, 0));
        var rate = decoder.SampleRate;
        var meter = new LoudnessMeter(rate, 2);
        var total = (long)Math.Ceiling(Math.Max(seconds, 0) * rate);
        var (left, right, both) = (new float[4096], new float[4096], new float[8192]);
        var (done, idle) = (0L, 0);
        while (done < total)
        {
            token.ThrowIfCancellationRequested();
            var got = decoder.Read(left.AsSpan(0, (int)Math.Min(left.Length, total - done)), right.AsSpan(0, (int)Math.Min(right.Length, total - done)));
            if (got <= 0)
            {
                // A pipe not caught up yet: wait 1 ms, giving up after ~10 s with no new data.
                if (++idle > 10_000)
                    break;
                Thread.Sleep(1);
                continue;
            }

            idle = 0;
            for (var i = 0; i < got; i++)
                (both[2 * i], both[2 * i + 1]) = (left[i], right[i]);
            meter.Add(both.AsSpan(0, got * 2), got);
            done += got;
        }

        return (meter.IntegratedLufs, meter.SamplePeak);
    }
}

/// <summary>
/// Direct-form double-precision biquad for BS.1770 K-weighting (both stages); LoudnessMeter and LiveLoudness run it on channel pairs via
/// <see cref="Paired"/> with identical per-lane arithmetic.
/// </summary>
internal sealed class KFilter(double b0, double b1, double b2, double a1, double a2)
{
    private double _x1, _x2, _y1, _y2;

    public double Next(double x)
    {
        var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
        (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
        return y;
    }

    /// <summary>Flushes denormals in the silence after a sound.</summary>
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

    /// <summary>The same filter on two channels at once (<see cref="BiquadPair"/>), history cleared.</summary>
    public BiquadPair Paired() => new(b0, b1, b2, a1, a2);

    // BS.1770 pre-filter: ~+4 dB high shelf from ~1.5 kHz (published 48 kHz coefficients re-derived for any rate).
    public static KFilter Shelf(int rate)
    {
        const double f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / rate);
        var vh = Math.Pow(10, gain / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        return new KFilter((vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
    }

    // BS.1770 RLB weighting: 2nd-order high-pass at ~38 Hz.
    public static KFilter HighPass(int rate)
    {
        const double f0 = 38.13547087602444, q = 0.5003270373238773;
        var k = Math.Tan(Math.PI * f0 / rate);
        var a0 = 1 + k / q + k * k;
        return new KFilter(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
    }
}

/// <summary>
/// Live loudness and true peak on the mixer thread, allocation-free: stereo, K-weighted, mean squares per 100 ms step; momentary over 4 steps
/// (400 ms), short-term over 30 (3 s), and gated integrated since <see cref="Restart"/> over 400 ms blocks at 100 ms hops via a histogram
/// (0.1 LU bins from -70 to +5 LUFS, so gating never stores blocks: absolute gate -70, relative gate -10 LU). True peak = max of the samples
/// and the three inter-sample points of a 4x polyphase interpolator (BS.1770-4 Annex 2 structure: 4 phases x 12 taps, Kaiser-windowed sinc,
/// unity gain per phase), held over 3 s.
/// </summary>
internal sealed class LiveLoudness
{
    private const int Taps = 12, Phases = 4, MomentarySteps = 4, ShortSteps = 30, HoldSteps = 30;
    private const double Lowest = -70, Highest = 5, BinWidth = 0.1;
    private static readonly int Bins = (int)Math.Round((Highest - Lowest) / BinWidth);

    // Interpolator phases 1-3 (phase 0 is the sample itself); tap j weights the sample j before the newest, broadcast to both lanes (per-lane
    // arithmetic identical to one channel alone, so peaks are bit-identical).
    private static readonly Vector128<double>[] Interpolator = [.. MakeInterpolator().Select(tap => Vector128.Create(tap))];

    private readonly int _step;
    private BiquadPair _shelf, _pass;
    private readonly double[] _steps = new double[ShortSteps];
    private readonly double[] _histogramEnergy = new double[Bins];
    private readonly int[] _histogramCount = new int[Bins];
    private readonly float[] _holdSteps = new float[HoldSteps];

    // Both channels' last Taps samples (one lane each), stored twice so the newest's window is contiguous; newest at _historyAt.
    private readonly Vector128<double>[] _history = new Vector128<double>[2 * Taps];
    private int _historyAt, _stepCount, _stepAt, _holdAt, _inStep;
    private double _stepSum;
    private float _stepPeak;

    public LiveLoudness(int sampleRate)
    {
        _step = Math.Max(sampleRate / 10, 1);
        (_shelf, _pass) = (KFilter.Shelf(sampleRate).Paired(), KFilter.HighPass(sampleRate).Paired());
    }

    /// <summary>Momentary loudness (400 ms), LUFS; negative infinity in silence and before the first step.</summary>
    public float MomentaryLufs { get; private set; } = float.NegativeInfinity;

    /// <summary>Short-term loudness (3 s), LUFS.</summary>
    public float ShortTermLufs { get; private set; } = float.NegativeInfinity;

    /// <summary>Gated integrated loudness since <see cref="Restart"/>, LUFS.</summary>
    public float IntegratedLufs { get; private set; } = float.NegativeInfinity;

    /// <summary>Highest true peak (linear) of both channels since the last <see cref="TakePeak"/>.</summary>
    public float Peak { get; private set; }

    /// <summary>Highest true peak over the last 3 s (linear).</summary>
    public float HeldPeak { get; private set; }

    /// <summary>True when a sample or inter-sample point reached full scale since the last <see cref="TakePeak"/>.</summary>
    public bool Clipped { get; private set; }

    /// <summary>Returns the peak and clip flag since the last call, then resets them.</summary>
    public (float Peak, bool Clipped) TakePeak()
    {
        var taken = (Peak, Clipped);
        (Peak, Clipped) = (0, false);
        return taken;
    }

    /// <summary>Restarts loudness measurement (play start, seek); the filters and the peak hold continue.</summary>
    public void Restart()
    {
        Array.Clear(_steps);
        Array.Clear(_histogramEnergy);
        Array.Clear(_histogramCount);
        (_stepCount, _stepAt, _inStep, _stepSum) = (0, 0, 0, 0);
        (MomentaryLufs, ShortTermLufs, IntegratedLufs) = (float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
    }

    /// <summary>Adds a block of the mix (interleaved stereo).</summary>
    public void Add(ReadOnlySpan<float> stereo, int frames)
    {
        var history = _history;
        ReadOnlySpan<Vector128<double>> first = Interpolator.AsSpan(0, Taps), second = Interpolator.AsSpan(Taps, Taps), third = Interpolator.AsSpan(2 * Taps, Taps);
        for (var i = 0; i < frames; i++)
        {
            var sample = Vector128.Create((double)stereo[2 * i], stereo[2 * i + 1]);

            // True peak: the sample plus the three interpolated points before it.
            _historyAt = _historyAt == 0 ? Taps - 1 : _historyAt - 1;
            history[_historyAt] = history[_historyAt + Taps] = sample;
            var window = history.AsSpan(_historyAt, Taps);
            var (one, two, three) = (Vector128<double>.Zero, Vector128<double>.Zero, Vector128<double>.Zero);
            for (var j = 0; j < window.Length && j < first.Length && j < second.Length && j < third.Length; j++)
            {
                one += first[j] * window[j];
                two += second[j] * window[j];
                three += third[j] * window[j];
            }

            var most = Vector128.Max(Vector128.Max(Vector128.Abs(sample), Vector128.Abs(one)), Vector128.Max(Vector128.Abs(two), Vector128.Abs(three)));
            var truePeak = (float)Math.Max(most.GetElement(0), most.GetElement(1));
            if (truePeak > Peak)
                Peak = truePeak;
            if (truePeak > _stepPeak)
                _stepPeak = truePeak;
            if (truePeak >= 1)
                Clipped = true;

            // Loudness: K-weight each channel and sum squares over the step.
            var weighted = _pass.Next(_shelf.Next(sample));
            var squares = weighted * weighted;
            _stepSum += squares.GetElement(0) + squares.GetElement(1);
            if (++_inStep == _step)
                EndStep();
        }

        _shelf.Flush();
        _pass.Flush();
    }

    private static float Lufs(double meanSquare) => meanSquare > 0 ? (float)(-0.691 + 10 * Math.Log10(meanSquare)) : float.NegativeInfinity;

    // End of a 100 ms step: window loudness, histogram entry for the block ending here, integrated loudness, peak hold.
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

    // Mean square of the last N steps (fewer while warming up).
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

    // Gated integrated loudness from the histogram: blocks above -70 LUFS, then those above 10 LU below their mean.
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

    // Interpolator phases 1-3: Nyquist-cutoff sinc under a 48-tap Kaiser window (beta 6) centred on tap 24, so phase 0 is the sample itself
    // and phase p is the point p/4 of the way from the sample 6 before the newest to the next.
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
