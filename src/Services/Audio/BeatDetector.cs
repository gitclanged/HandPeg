using System.IO;
using System.Runtime.CompilerServices;

namespace HandPegApp.Services.Audio;

// ----- Audio beats (Pass 5.2B): Detect & Snap to Audio Beats -----
// A sound's onsets, the beats a cut snaps to (TimelineClip.BeatTimes, in its file's seconds). Spectral flux, as onset detectors have it: the
// sound made mono and brought down to about 5.5 kHz (a beat's weight is low; four samples of 22 kHz averaged), Hann frames of 256 a half
// apart (46 ms, 23 ms apart), each bin's magnitude log-compressed (log(1 + 100 |X|)), and the flux the sum of the bins' rises since the last
// frame (half-wave rectified). A frame is an onset where its flux is the highest within two frames either way and over an adaptive threshold
// (1.25 times the median and half the mean of the flux within half a second, and a fiftieth of the loudest flux). Its moment is then found
// to the millisecond in the sound's own energy (1 ms blocks at the full rate): the first block that rises a quarter of the way from the
// quietest to the loudest round the frame. Onsets closer than the gap asked for keep the stronger; with eight or more, a weak onset (under
// half the median strength) that falls off the dominant interval's grid (its half-beats) is let go. A minute of 22 kHz takes a few ms: the
// sample loops are compiled fully optimized from their first call (AggressiveOptimization), as a detection runs once, on a background thread.

/// <summary>Onsets and beats of a sound (see the top of this file).</summary>
public static class BeatDetector
{
    /// <summary>The rate a file's sound is analysed at, mono (<see cref="DetectFileAsync"/>).</summary>
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
    /// The audio of a media file (its stream <paramref name="audioStream"/>, -1 the default, the first) decoded to mono at the analysis rate
    /// and <see cref="Detect"/>-ed: times in the file's own seconds (media time, as a clip's MediaOffset counts it: from the first video frame
    /// of a video). Background thread; cached per path, last write time and stream (a detection cancelled or failed is not kept).
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

    // The file's stream decoded from its media time 0 (as the sound engine places it), made mono at the analysis rate, as it comes.
    private static double[] DetectFile(string file, int stream, CancellationToken token)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("The file is not there.", file);

        var source = new AudioSource(file, stream) { VideoStart = MediaProbe.KnownVideoStart(file) ?? double.NaN };
        using var decoder = AudioDecoder.Open(source, AnalysisRate);
        decoder.Seek(0);
        var duration = decoder.Duration > 0 ? decoder.Duration : MediaProbe.KnownInfo(file)?.DurationSeconds ?? 0;

        // Without a length (a pipe's), it reads until 20 s of nothing, four hours at most.
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
                // A pipe not caught up yet: a moment's wait, up to ten seconds of nothing new.
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

    /// <summary>The flux of a sound's frames and its energy in 1 ms blocks, as the sound comes; then its onsets (<see cref="Finish"/>).</summary>
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

        // The sound's 1 ms energies, then its decimated samples: each a run of whole blocks summed in locals (5.2B: a field written per sample
        // cost three quarters of a detection), the same sums in the same order as one sample at a time.
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

        // One decimated sample: a frame each half frame, once the first is full.
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

        // The frame's log-compressed magnitudes (a real FFT of 256 as a complex one of 128), and its flux against the last frame's.
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

            // (The noise reduction's transform, its butterflies vectors at a time: the same flux to the bit as one butterfly at a time.)
            Fft.Butterflies(re, im, inverse: false);

            // X[k] = E[k] + W^k O[k], E and O the even and odd samples' spectra, taken apart from the packed one.
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

            // (The first frame has none before it to rise from: what starts with the sound is not told from what was already sounding.)
            _flux.Add(_hasLogs ? flux : 0);
            _hasLogs = true;
        }

        // The natural log of a number of 1 or more, to a few thousandths (the float's exponent, and a quadratic of its mantissa: monotonic,
        // the same everywhere): a frame's 129 logs are most of its cost otherwise.
        private static float Log(float x)
        {
            var bits = BitConverter.SingleToInt32Bits(x);
            var mantissa = BitConverter.Int32BitsToSingle((bits & 0x007FFFFF) | 0x3F800000);
            var log2 = ((bits >> 23) & 0xFF) - 128 + ((-0.34484843f * mantissa + 2.02466578f) * mantissa - 0.67487759f);
            return log2 * 0.6931472f;
        }

        public double[] Finish(double minGapSeconds)
        {
            // What is left of the sound: the frames that would hold it, filled with silence.
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

            // The candidates: the highest within two frames either way, over the threshold of the half second round them.
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

                // The frame's onset: between the middles of it and the frame before, found to the millisecond in the sound's energy.
                var coarse = (m * (double)Hop + Size / 2.0 - Hop / 2.0) * _decimation / _rate;
                found.Add((Refined(coarse), here));
            }

            // Closer than the gap: the stronger stays.
            var kept = new List<(double Time, float Strength)>();
            foreach (var onset in found.OrderByDescending(o => o.Strength))
            {
                if (!kept.Exists(k => Math.Abs(k.Time - onset.Time) < minGapSeconds))
                    kept.Add(onset);
            }

            kept.Sort((a, b) => a.Time.CompareTo(b.Time));
            return [.. OnTheBeat(kept).Select(o => Math.Max(o.Time, 0))];
        }

        // Whether a frame's flux is the highest within so many frames either way (the first of equal ones).
        private static bool Highest(List<float> flux, int m, int around)
        {
            for (var k = Math.Max(m - around, 0); k <= Math.Min(m + around, flux.Count - 1); k++)
            {
                if (k != m && (flux[k] > flux[m] || (flux[k] == flux[m] && k < m)))
                    return false;
            }

            return true;
        }

        // An onset's moment: the first 1 ms block after the quietest that rises a quarter of the way to the loudest, within 60 ms before
        // the coarse moment and 50 ms after it (a frame's onset lies within about a frame and a half after it, or half a frame before).
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

        // With eight onsets or more: a weak one (under half the median strength) off the grid of the dominant interval's half-beats is let go.
        private static List<(double Time, float Strength)> OnTheBeat(List<(double Time, float Strength)> onsets)
        {
            if (onsets.Count < 8)
                return onsets;

            // The dominant interval between neighbours, 0.25 to 1.5 s, in 10 ms bins smoothed by their neighbours.
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

        // The real split's twiddles (256 points; the complex FFT of 128 is Radix2Fft's).
        private static (float[], float[]) MakeTables()
        {
            var (splitCos, splitSin) = (new float[Half + 1], new float[Half + 1]);
            for (var k = 0; k <= Half; k++)
                (splitCos[k], splitSin[k]) = ((float)Math.Cos(2 * Math.PI * k / Size), (float)Math.Sin(2 * Math.PI * k / Size));
            return (splitCos, splitSin);
        }
    }
}
