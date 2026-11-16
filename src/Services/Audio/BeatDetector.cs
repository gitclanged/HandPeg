using System.IO;
using System.Runtime.CompilerServices;

namespace HandPegApp.Services.Audio;

// ----- Audio beat detection (Detect & Snap to Audio Beats) -----
// Onsets for cut snapping (TimelineClip.BeatTimes, in file seconds) by spectral flux: mono, decimated to ~5.5 kHz (four 22 kHz samples
// averaged; beat energy is low), Hann frames of 256 at 50 % overlap (46 ms / 23 ms), log-compressed magnitudes (log(1 + 100 |X|)), flux =
// sum of half-wave-rectified bin rises. An onset is a flux peak within +-2 frames above an adaptive threshold (1.25 x median + 0.5 x mean
// over +-0.5 s, plus loudest / 50), refined to the millisecond on 1 ms full-rate energy blocks (first block rising 25 % from the local
// minimum toward the maximum). Onsets closer than the gap keep the stronger; with 8+ onsets, weak ones (under half the median strength) off
// the dominant interval's half-beat grid are dropped. A minute at 22 kHz takes a few ms (AggressiveOptimization: runs once, on a background
// thread).

/// <summary>Audio onset and beat detection (see file header).</summary>
public static class BeatDetector
{
    /// <summary>Mono analysis sample rate for files (<see cref="DetectFileAsync"/>).</summary>
    public const int AnalysisRate = 22050;

    /// <summary>
    /// Onset/beat times in seconds from the first sample: spectral flux with an adaptive threshold, peak-picked, at least
    /// <paramref name="minGapSeconds"/> apart. Pure, any thread. Under 15 ms per minute of 22050 Hz mono.
    /// </summary>
    public static double[] Detect(ReadOnlySpan<float> mono, int sampleRate, double minGapSeconds = 0.1)
    {
        var analysis = new Analysis(sampleRate);
        analysis.Add(mono);
        return analysis.Finish(minGapSeconds);
    }

    private static readonly object CacheGate = new();
    private static readonly Dictionary<(string Path, long Written, int Stream), Task<double[]>> Cache = [];

    /// <summary>
    /// Decodes audio stream <paramref name="audioStream"/> (-1 = first) of a media file to mono at the analysis rate and runs <see cref="Detect"/>:
    /// times in media seconds (as a clip's MediaOffset counts them: from the first video frame of a video). Background thread; cached per path,
    /// write time and stream (cancelled or failed detections are not cached).
    /// </summary>
    public static Task<double[]> DetectFileAsync(string path, int audioStream, CancellationToken token)
    {
        var file = path.Trim().Trim('"');
        var stream = Math.Max(audioStream, 0);
        long written;
        try
        {
            written = File.GetLastWriteTimeUtc(file).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromException<double[]>(ex);
        }

        var key = (file.ToUpperInvariant(), written, stream);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var known) && !known.IsFaulted && !known.IsCanceled)
                return known;
        }

        var task = Task.Run(() => DetectFile(file, stream, token), token);
        lock (CacheGate)
        {
            if (Cache.Count >= 32)
                Cache.Clear();
            Cache[key] = task;
        }

        return task;
    }

    // Decodes the stream from media time 0 (as the sound engine places it), streaming mono at the analysis rate.
    private static double[] DetectFile(string file, int stream, CancellationToken token)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("The file is not there.", file);

        var source = new AudioSource(file, stream) { VideoStart = MediaProbe.KnownVideoStart(file) ?? double.NaN };
        using var decoder = AudioDecoder.Open(source, AnalysisRate);
        decoder.Seek(0);
        var duration = decoder.Duration > 0 ? decoder.Duration : MediaProbe.KnownInfo(file)?.DurationSeconds ?? 0;

        // Unknown length (pipes): read until 20 s of silence, at most four hours.
        var total = duration > 0 ? (long)Math.Ceiling(duration * AnalysisRate) : 4L * 3600 * AnalysisRate;
        var resampler = new PolyphaseResampler();
        resampler.Reset(decoder, decoder.SampleRate / (double)AnalysisRate, 0);
        var analysis = new Analysis(AnalysisRate);
        var (left, right, mono) = (new float[4096], new float[4096], new float[4096]);
        var (done, idle, silent) = (0L, 0, 0L);
        while (done < total)
        {
            token.ThrowIfCancellationRequested();
            var want = (int)Math.Min(left.Length, total - done);
            var got = resampler.Read(left.AsSpan(0, want), right.AsSpan(0, want));
            if (got <= 0)
            {
                // Pipe not caught up yet: wait 1 ms, giving up after ~10 s with no new data.
                if (++idle > 10_000)
                    break;
                Thread.Sleep(1);
                continue;
            }

            idle = 0;
            var quiet = true;
            for (var i = 0; i < got; i++)
            {
                mono[i] = 0.5f * (left[i] + right[i]);
                quiet &= mono[i] == 0;
            }

            analysis.Add(mono.AsSpan(0, got));
            done += got;
            silent = quiet ? silent + got : 0;
            if (duration <= 0 && silent > 20L * AnalysisRate)
                break;
        }

        return analysis.Finish(0.1);
    }

    /// <summary>Streaming analysis: frame flux and 1 ms energy blocks as audio arrives, then onsets (<see cref="Finish"/>).</summary>
    private sealed class Analysis
    {
        private const int Size = 256, Hop = Size / 2, Bins = Size / 2 + 1, Half = Size / 2;
        private const float Compression = 100;

        private static readonly float[] Window = MakeWindow();
        private static readonly Radix2Fft Fft = new(Half);
        private static readonly (float[] SplitCos, float[] SplitSin) Tables = MakeTables();

        private readonly int _rate, _decimation, _blockLength;
        private readonly double _frameRate;
        private readonly float[] _ring = new float[Size], _logs = new float[Bins];
        private readonly float[] _re = new float[Half], _im = new float[Half];
        private readonly List<float> _flux = new(1 << 12), _energy = new(1 << 14);
        private int _ringAt, _sinceFrame;
        private long _decimated;
        private float _decimationSum;
        private int _decimationCount;
        private double _blockSum;
        private int _blockCount;
        private bool _hasLogs;

        public Analysis(int sampleRate)
        {
            _rate = Math.Max(sampleRate, 1000);
            _decimation = Math.Max((int)Math.Round(_rate / 5512.5), 1);
            _blockLength = Math.Max(_rate / 1000, 1);
            _frameRate = _rate / (double)_decimation;
        }

        // Accumulates 1 ms energies, then decimated samples, summing runs in locals (a per-sample field write cost three quarters of a
        // detection); sums and order match per-sample processing exactly.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Add(ReadOnlySpan<float> mono)
        {
            var (blockSum, blockCount, blockLength) = (_blockSum, _blockCount, _blockLength);
            for (var i = 0; i < mono.Length;)
            {
                var end = Math.Min(i + blockLength - blockCount, mono.Length);
                blockCount += end - i;
                for (; i < end; i++)
                    blockSum += mono[i] * mono[i];
                if (blockCount == blockLength)
                {
                    _energy.Add((float)(blockSum / blockLength));
                    (blockSum, blockCount) = (0, 0);
                }
            }

            (_blockSum, _blockCount) = (blockSum, blockCount);
            var (decimationSum, decimationCount, decimation) = (_decimationSum, _decimationCount, _decimation);
            for (var i = 0; i < mono.Length;)
            {
                var end = Math.Min(i + decimation - decimationCount, mono.Length);
                decimationCount += end - i;
                for (; i < end; i++)
                    decimationSum += mono[i];
                if (decimationCount == decimation)
                {
                    Push(decimationSum / decimation);
                    (decimationSum, decimationCount) = (0, 0);
                }
            }

            (_decimationSum, _decimationCount) = (decimationSum, decimationCount);
        }

        // One decimated sample; emits a frame every half frame once the first is full.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Push(float sample)
        {
            _ring[_ringAt] = sample;
            _ringAt = (_ringAt + 1) & (Size - 1);
            _decimated++;
            if (_decimated >= Size && ++_sinceFrame >= (_decimated == Size ? 1 : Hop))
            {
                _sinceFrame = 0;
                Frame();
            }
        }

        // Frame log magnitudes (real 256-point FFT via a 128-point complex FFT) and flux versus the previous frame.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Frame()
        {
            var (splitCos, splitSin) = Tables;
            var (re, im) = (_re, _im);
            var reverse = Fft.Reverse;
            for (var k = 0; k < Half; k++)
            {
                var j = reverse[k];
                var (even, odd) = (2 * j, 2 * j + 1);
                re[k] = _ring[(_ringAt + even) & (Size - 1)] * Window[even];
                im[k] = _ring[(_ringAt + odd) & (Size - 1)] * Window[odd];
            }

            // Vectorized butterflies (shared with noise reduction); flux is bit-identical to scalar butterflies.
            Fft.Butterflies(re, im, inverse: false);

            // Unpack: X[k] = E[k] + W^k O[k], with E and O the even/odd sample spectra separated from the packed transform.
            const float scale = Compression / (Size / 4f);
            var flux = 0f;
            for (var k = 0; k <= Half; k++)
            {
                var (ar, ai) = (re[k % Half], im[k % Half]);
                var (br, bi) = (re[(Half - k) % Half], -im[(Half - k) % Half]);
                var (evenR, evenI) = ((ar + br) * 0.5f, (ai + bi) * 0.5f);
                var (dr, di) = (ar - br, ai - bi);
                var (oddR, oddI) = (di * 0.5f, -dr * 0.5f);
                var (wr, wi) = (splitCos[k], -splitSin[k]);
                var (xr, xi) = (evenR + wr * oddR - wi * oddI, evenI + wr * oddI + wi * oddR);
                var log = Log(1 + scale * MathF.Sqrt(xr * xr + xi * xi));
                if (_hasLogs && log > _logs[k])
                    flux += log - _logs[k];
                _logs[k] = log;
            }

            // The first frame has no predecessor to rise from, so its flux is 0.
            _flux.Add(_hasLogs ? flux : 0);
            _hasLogs = true;
        }

        // Fast natural log for x >= 1, accurate to a few thousandths (exponent plus a quadratic in the mantissa: monotonic, deterministic);
        // the 129 logs per frame would otherwise dominate the cost.
        private static float Log(float x)
        {
            var bits = BitConverter.SingleToInt32Bits(x);
            var mantissa = BitConverter.Int32BitsToSingle((bits & 0x007FFFFF) | 0x3F800000);
            var log2 = ((bits >> 23) & 0xFF) - 128 + ((-0.34484843f * mantissa + 2.02466578f) * mantissa - 0.67487759f);
            return log2 * 0.6931472f;
        }

        public double[] Finish(double minGapSeconds)
        {
            // Flush the tail: pad with silence to complete the last frame.
            if (_decimated > 0)
            {
                var pad = _decimated < Size ? Size - _decimated : Hop - _sinceFrame;
                for (var i = 0; i < pad && pad < Size + 1; i++)
                    Push(0);
            }

            var flux = _flux;
            var frames = flux.Count;
            if (frames == 0)
                return [];

            var loudest = 0f;
            foreach (var value in flux)
                loudest = Math.Max(loudest, value);
            if (loudest < 1e-3f)
                return [];

            // Candidates: maxima within +-2 frames above the threshold of the surrounding half second.
            var hop = Hop / _frameRate;
            var reach = Math.Max((int)Math.Round(0.5 / hop), 2);
            var window = new float[2 * reach + 1];
            var floor = loudest / 50;
            var found = new List<(double Time, float Strength)>();
            for (var m = 0; m < frames; m++)
            {
                var here = flux[m];
                if (here <= floor || !Highest(flux, m, 2))
                    continue;

                var (from, to) = (Math.Max(m - reach, 0), Math.Min(m + reach, frames - 1));
                var (count, sum) = (to - from + 1, 0f);
                for (var k = from; k <= to; k++)
                    (window[k - from], sum) = (flux[k], sum + flux[k]);
                Array.Sort(window, 0, count);
                var median = count % 2 == 1 ? window[count / 2] : 0.5f * (window[count / 2 - 1] + window[count / 2]);
                if (here <= 1.25f * median + 0.5f * (sum / count) + floor)
                    continue;

                // Onset time: between the centres of this frame and the previous one, refined to the millisecond on the energy envelope.
                var coarse = (m * (double)Hop + Size / 2.0 - Hop / 2.0) * _decimation / _rate;
                found.Add((Refined(coarse), here));
            }

            // Within the minimum gap, keep the stronger onset.
            var kept = new List<(double Time, float Strength)>();
            foreach (var onset in found.OrderByDescending(o => o.Strength))
            {
                if (!kept.Exists(k => Math.Abs(k.Time - onset.Time) < minGapSeconds))
                    kept.Add(onset);
            }

            kept.Sort((a, b) => a.Time.CompareTo(b.Time));
            return [.. OnTheBeat(kept).Select(o => Math.Max(o.Time, 0))];
        }

        // True when the frame's flux is the maximum within +-around frames (the first of equal values wins).
        private static bool Highest(List<float> flux, int m, int around)
        {
            for (var k = Math.Max(m - around, 0); k <= Math.Min(m + around, flux.Count - 1); k++)
            {
                if (k != m && (flux[k] > flux[m] || (flux[k] == flux[m] && k < m)))
                    return false;
            }

            return true;
        }

        // Refines an onset: the first 1 ms block after the local minimum that rises 25 % toward the local maximum, searched from 60 ms
        // before to 50 ms after the coarse time (a frame's onset lies within ~1.5 frames after it or half a frame before).
        private double Refined(double coarse)
        {
            var blockSeconds = _blockLength / (double)_rate;
            var energy = _energy;
            var first = Math.Max((int)Math.Floor((coarse - 0.06) / blockSeconds), 0);
            var last = Math.Min((int)Math.Ceiling((coarse + 0.05) / blockSeconds), energy.Count - 1);
            if (last <= first)
                return coarse;

            var (quietest, loudest) = (first, first);
            for (var j = first; j <= last; j++)
            {
                if (energy[j] > energy[loudest])
                    loudest = j;
            }

            for (var j = first; j <= loudest; j++)
            {
                if (energy[j] < energy[quietest])
                    quietest = j;
            }

            var rise = energy[quietest] + 0.25f * (energy[loudest] - energy[quietest]);
            for (var j = quietest; j <= loudest; j++)
            {
                if (energy[j] >= rise && energy[loudest] > energy[quietest])
                    return j * blockSeconds;
            }

            return coarse;
        }

        // With 8+ onsets: drop weak ones (under half the median strength) off the dominant interval's half-beat grid.
        private static List<(double Time, float Strength)> OnTheBeat(List<(double Time, float Strength)> onsets)
        {
            if (onsets.Count < 8)
                return onsets;

            // Dominant inter-onset interval within 0.25-1.5 s, in 10 ms bins smoothed with their neighbours.
            var votes = new int[151];
            for (var i = 1; i < onsets.Count; i++)
            {
                var bin = (int)Math.Round((onsets[i].Time - onsets[i - 1].Time) * 100);
                if (bin is >= 25 and <= 150)
                    votes[bin]++;
            }

            var (best, bestVotes) = (0, 0);
            for (var bin = 25; bin <= 150; bin++)
            {
                var score = votes[bin - 1] + 2 * votes[bin] + (bin < 150 ? votes[bin + 1] : 0);
                if (score > bestVotes)
                    (best, bestVotes) = (bin, score);
            }

            if (bestVotes < 4)
                return onsets;

            var halfBeat = best / 200.0;
            var strengths = onsets.Select(o => o.Strength).Order().ToList();
            var weak = strengths[strengths.Count / 2] * 0.5f;
            var kept = new List<(double Time, float Strength)>(onsets.Count);
            double? anchor = null;
            foreach (var onset in onsets)
            {
                if (onset.Strength >= weak)
                {
                    kept.Add(onset);
                    anchor = onset.Time;
                    continue;
                }

                var steps = anchor is { } at ? (onset.Time - at) / halfBeat : 0;
                if (anchor is null || Math.Abs(steps - Math.Round(steps)) <= 0.15)
                    kept.Add(onset);
            }

            return kept;
        }

        private static float[] MakeWindow()
        {
            var window = new float[Size];
            for (var n = 0; n < Size; n++)
                window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Size));
            return window;
        }

        // Real-split twiddles for 256 points (the 128-point complex FFT is Radix2Fft's).
        private static (float[], float[]) MakeTables()
        {
            var (splitCos, splitSin) = (new float[Half + 1], new float[Half + 1]);
            for (var k = 0; k <= Half; k++)
                (splitCos[k], splitSin[k]) = ((float)Math.Cos(2 * Math.PI * k / Size), (float)Math.Sin(2 * Math.PI * k / Size));
            return (splitCos, splitSin);
        }
    }
}
