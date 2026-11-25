using System.Collections.Concurrent;

namespace HandPegApp.Services.Audio;

// ----- Spectral Audio: phase vocoder -----
//
// Pitch-preserving time stretch by STFT instead of WSOLA frame shifting: smooth slow-motion audio without repeated grains. Frames of 2048
// samples at 48 kHz (4096 above 64 kHz, 1024 below 32 kHz), Hann-windowed, output hop = frame / 4; each frame is read where the tempo maps
// it on the input. Identity phase locking (Laroche-Dolson): each spectral peak advances by its measured frequency and the bins of its region
// rotate with it, preserving partials. Phases reset from the input when spectral flux jumps (onsets keep their attack), and an attack ahead
// of a frame's centre is masked out of that frame (mirrored pre-attack audio), so it is never heard early.

/// <summary>
/// Phase-vocoder stretch at <see cref="Tempo"/> with pitch preserved. Output sample j maps to input around j x tempo, the same alignment as
/// <see cref="WsolaStretcher"/> (frame centres on that line), so either can feed the resampler. Both channels share one complex FFT (left =
/// real, right = imaginary) and rotate by one phase derived from their sum, preserving the stereo image. Allocation-free while reading.
/// </summary>
internal sealed class AudioPhaseVocoder : IStereoSource
{
    /// <summary>Fastest supported tempo: the analysis hop must stay under a frame to measure partial frequencies (faster tempos use WSOLA).</summary>
    public const double FastestTempo = 3.5;

    private const int Capacity = 1 << 17;

    // Attack guard: block size (samples), blocks averaged before it, and the power jump and floors that define an attack.
    private const int Block = 64, Before = 8;
    private const float AttackJump = 8f, AttackFloor = 1e-5f, QuietFloor = 1e-7f;

    // Phase reset when flux (new spectral magnitude) exceeds LeastFlux and 3x its recent median, and is rising.
    private const float LeastFlux = 0.2f, FluxOverMedian = 3f;
    private const int FluxHistory = 16;

    private static readonly ConcurrentDictionary<int, Radix2Fft> Transforms = new();

    private IStereoSource? _source;
    private Radix2Fft? _fft;
    private int _size, _hop, _half;
    private float _norm;
    private float[] _window = [], _re = [], _im = [];
    private float[] _leftRe = [], _leftIm = [], _rightRe = [], _rightIm = [];
    private float[] _magnitude = [], _magnitudeBefore = [], _phase = [], _phaseBefore = [], _synthesis = [];
    private float[] _turnCos = [], _turnSin = [], _turn = [];
    private int[] _peaks = [];
    private float[] _accL = [], _accR = [], _blocks = [];
    private readonly float[] _inL = new float[Capacity], _inR = new float[Capacity];
    private readonly float[] _fluxes = new float[FluxHistory], _sorted = new float[FluxHistory];
    private long _inBase;
    private int _inCount, _ready, _readyAt, _fluxCount, _fluxNext;
    private long _frame, _analysedAt, _resetAt;
    private float _fluxBefore;
    private ITempoMap? _map;

    public double Tempo { get; private set; } = 1;

    /// <summary>
    /// Restarts at a varying tempo (a speed ramp: analysis frames follow <paramref name="map"/>, each frame's phase advance measured over its
    /// own analysis hop); null keeps <paramref name="tempo"/>. Callers keep the ramp's fastest tempo within <see cref="FastestTempo"/>.
    /// </summary>
    public void Reset(IStereoSource source, double tempo, int sampleRate, ITempoMap? map)
    {
        Reset(source, tempo, sampleRate);
        _map = map;
    }

    /// <summary>Frame length for a sample rate (~43 ms).</summary>
    public static int FrameFor(int sampleRate) => sampleRate > 64000 ? 4096 : sampleRate < 32000 ? 1024 : 2048;

    /// <summary>Output samples (at the input rate) the first frames need to complete the overlap: the pre-roll a voice reads before its start.</summary>
    public static int WarmUp(int sampleRate) => FrameFor(sampleRate);

    public void Reset(IStereoSource source, double tempo, int sampleRate)
    {
        (_source, Tempo, _map) = (source, Math.Clamp(tempo, 0.05, 64), null);
        var size = FrameFor(sampleRate);
        if (size != _size)
        {
            (_size, _hop, _half) = (size, size / 4, size / 2);
            _fft = Transforms.GetOrAdd(size, static n => new Radix2Fft(n));

            // Periodic Hann for analysis and synthesis: squared windows at quarter-frame hops sum to 1.5.
            _window = new float[size];
            for (var n = 0; n < size; n++)
                _window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / size));
            _norm = 1f / (size * 1.5f);

            (_re, _im, _accL, _accR) = (new float[size], new float[size], new float[size], new float[size]);
            var bins = _half + 1;
            (_leftRe, _leftIm, _rightRe, _rightIm) = (new float[bins], new float[bins], new float[bins], new float[bins]);
            (_magnitude, _magnitudeBefore, _phase, _phaseBefore, _synthesis) = (new float[bins], new float[bins], new float[bins], new float[bins], new float[bins]);
            (_turnCos, _turnSin, _turn, _peaks) = (new float[bins], new float[bins], new float[bins], new int[bins]);
            _blocks = new float[size / Block];
        }

        Array.Clear(_accL);
        Array.Clear(_accR);
        Array.Clear(_magnitudeBefore);
        Array.Clear(_fluxes);
        (_inBase, _inCount, _ready, _readyAt, _frame, _analysedAt, _resetAt) = (0, 0, 0, 0, 0, 0, long.MinValue / 2);
        (_fluxCount, _fluxNext, _fluxBefore) = (0, 0, 0);
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

    // After the finished hop is read, slide the accumulator by one hop.
    private void Shift()
    {
        _accL.AsSpan(_hop).CopyTo(_accL);
        _accR.AsSpan(_hop).CopyTo(_accR);
        _accL.AsSpan(_size - _hop).Clear();
        _accR.AsSpan(_size - _hop).Clear();
        _readyAt = 0;
    }

    // Input position of frame k: its centre (output k x hop + half a frame) mapped onto the tempo line (a ramp's map: its integral).
    private long AnalysisAt(long frame)
    {
        var centre = frame * _hop + _half;
        return (long)Math.Round((_map is { } map ? map.InputAt(centre) : centre * Tempo) - _half);
    }

    private bool NextFrame()
    {
        var at = AnalysisAt(_frame);
        if (!Have(at + _size))
            return false;

        // Read the frame (silence before the input start), mask any attack ahead of its centre, then window it.
        var offset = at - _inBase;
        for (var n = 0; n < _size; n++)
        {
            var i = offset + n;
            (_re[n], _im[n]) = i >= 0 && i < _inCount ? (_inL[i], _inR[i]) : (0f, 0f);
        }

        GuardAttack();
        for (var n = 0; n < _size; n++)
        {
            _re[n] *= _window[n];
            _im[n] *= _window[n];
        }

        _fft!.Transform(_re, _im, inverse: false);
        var flux = Analyse();

        // Phase reset on the first frame, a non-advancing frame (tempo so slow the hop rounds to 0), or an onset.
        var hop = at - _analysedAt;
        var reset = _frame == 0 || hop <= 0
                    || (flux > LeastFlux && flux > FluxOverMedian * MedianFlux() && flux > _fluxBefore && at - _resetAt >= _half);
        if (reset)
            _resetAt = at;
        var turned = Turn(reset, hop);
        RememberFlux(flux);
        Synthesise(turned);

        (_analysedAt, _frame, _ready, _readyAt) = (at, _frame + 1, _hop, 0);
        Drop(AnalysisAt(_frame));
        return true;
    }

    /// <summary>
    /// Attack guard: the first attack (a 64-sample block 8x louder than the mean of the 8 before it, above the floor) in the frame's second
    /// half (a block past the centre) is masked: from the block before it to the frame end, the frame mirrors the audio before that point.
    /// Stationary sound continues (the mirror shares its spectrum); the attack sounds only once it reaches a frame's centre, where flux resets phases.
    /// </summary>
    private void GuardAttack()
    {
        var count = _blocks.Length;
        for (var b = 0; b < count; b++)
        {
            var energy = 0f;
            for (int n = b * Block, end = n + Block; n < end; n++)
                energy += _re[n] * _re[n] + _im[n] * _im[n];
            _blocks[b] = energy;
        }

        for (var b = _half / Block + 1; b < count; b++)
        {
            var mean = 0f;
            for (var j = b - Before; j < b; j++)
                mean += _blocks[j];
            mean /= Before;
            if (_blocks[b] < AttackFloor * Block || _blocks[b] < AttackJump * Math.Max(mean, QuietFloor * Block))
                continue;

            var cut = (b - 1) * Block;
            for (var m = 0; cut + m < _size; m++)
            {
                _re[cut + m] = _re[cut - 1 - m];
                _im[cut + m] = _im[cut - 1 - m];
            }

            return;
        }
    }

    /// <summary>
    /// Splits the shared transform into per-channel spectra, computes the joint magnitude and one phase for both (from the sum, or the
    /// difference where the sum cancels), and returns the flux versus the previous frame: new magnitude as a fraction of the total.
    /// </summary>
    private float Analyse()
    {
        var (flux, whole) = (0f, 0f);
        var mask = _size - 1;
        for (var k = 0; k <= _half; k++)
        {
            var j = (_size - k) & mask;
            var (zr, zi, wr, wi) = (_re[k], _im[k], _re[j], _im[j]);
            var (lr, li, rr, ri) = ((zr + wr) * 0.5f, (zi - wi) * 0.5f, (zi + wi) * 0.5f, (wr - zr) * 0.5f);
            (_leftRe[k], _leftIm[k], _rightRe[k], _rightIm[k]) = (lr, li, rr, ri);

            var power = lr * lr + li * li + rr * rr + ri * ri;
            var magnitude = MathF.Sqrt(power);
            var (sr, si) = (lr + rr, li + ri);
            if (sr * sr + si * si < 0.1f * power)
                (sr, si) = (lr - rr, li - ri);
            _phase[k] = MathF.Atan2(si, sr);
            _magnitude[k] = magnitude;
            flux += Math.Max(magnitude - _magnitudeBefore[k], 0f);
            whole += magnitude;
        }

        return flux / (whole + _size * 1e-6f);
    }

    /// <summary>
    /// Per-bin rotation from analysis to synthesis phase. On reset: none (frame as read). Otherwise each peak advances by its measured
    /// frequency over the output hop from its previous synthesis phase, and every bin of its region (up to the minimum before the next peak)
    /// rotates with it. Returns false when nothing rotates (frame resynthesized as read).
    /// </summary>
    private bool Turn(bool reset, long hop)
    {
        var peaks = 0;
        if (!reset)
        {
            for (var k = 1; k < _half; k++)
            {
                var m = _magnitude[k];
                if (m > _magnitude[k - 1] && m >= _magnitude[k + 1] && (k < 2 || m > _magnitude[k - 2]) && (k + 2 > _half || m >= _magnitude[k + 2]))
                    _peaks[peaks++] = k;
            }
        }

        if (peaks == 0)
        {
            _phase.AsSpan().CopyTo(_synthesis);
            return false;
        }

        var (size, outHop) = (_size, _hop);
        var start = 0;
        for (var p = 0; p < peaks; p++)
        {
            var k = _peaks[p];

            // Expected bin-centre advance over the hop, exact (whole 2 pi multiples dropped in integer arithmetic).
            var expected = (float)(2 * Math.PI * (k * hop % size) / size);
            var deviation = Wrap(_phase[k] - _phaseBefore[k] - expected);
            var advance = (float)(2 * Math.PI * (k * (long)outHop % size) / size) + deviation * outHop / hop;
            var turn = Wrap(_synthesis[k] + advance - _phase[k]);

            var end = _half;
            if (p + 1 < peaks)
            {
                end = k;
                for (var b = k + 1; b < _peaks[p + 1]; b++)
                {
                    if (_magnitude[b] < _magnitude[end])
                        end = b;
                }
            }

            var (sin, cos) = MathF.SinCos(turn);
            for (var b = start; b <= end; b++)
                (_turn[b], _turnCos[b], _turnSin[b]) = (turn, cos, sin);
            start = end + 1;
        }

        // DC and Nyquist stay real (transform of a real frame): no rotation.
        (_turn[0], _turnCos[0], _turnSin[0]) = (0f, 1f, 0f);
        (_turn[_half], _turnCos[_half], _turnSin[_half]) = (0f, 1f, 0f);
        for (var b = 0; b <= _half; b++)
            _synthesis[b] = Wrap(_phase[b] + _turn[b]);
        return true;
    }

    // Rotate the spectra, recombine into one transform, inverse FFT, window and overlap-add.
    private void Synthesise(bool turned)
    {
        for (var k = 0; k <= _half; k++)
        {
            float a = _leftRe[k], b = _leftIm[k], c = _rightRe[k], d = _rightIm[k];
            if (turned)
            {
                var (cos, sin) = (_turnCos[k], _turnSin[k]);
                (a, b) = (a * cos - b * sin, a * sin + b * cos);
                (c, d) = (c * cos - d * sin, c * sin + d * cos);
            }

            (_re[k], _im[k]) = (a - d, b + c);
            if (k != 0 && k != _half)
                (_re[_size - k], _im[_size - k]) = (a + d, c - b);
        }

        _fft!.Transform(_re, _im, inverse: true);
        for (var n = 0; n < _size; n++)
        {
            var gain = _window[n] * _norm;
            _accL[n] += _re[n] * gain;
            _accR[n] += _im[n] * gain;
        }

        // This frame becomes the reference for the next.
        _magnitude.AsSpan().CopyTo(_magnitudeBefore);
        _phase.AsSpan().CopyTo(_phaseBefore);
    }

    private float MedianFlux()
    {
        if (_fluxCount == 0)
            return 0f;

        _fluxes.AsSpan(0, _fluxCount).CopyTo(_sorted);
        Array.Sort(_sorted, 0, _fluxCount);
        return _sorted[_fluxCount / 2];
    }

    private void RememberFlux(float flux)
    {
        _fluxes[_fluxNext] = flux;
        _fluxNext = (_fluxNext + 1) % FluxHistory;
        _fluxCount = Math.Min(_fluxCount + 1, FluxHistory);
        _fluxBefore = flux;
    }

    // Wraps an angle into [-pi, pi].
    private static float Wrap(float angle)
    {
        if (angle is >= -MathF.PI and <= MathF.PI)
            return angle;

        var turns = MathF.Round(angle / (2 * MathF.PI));
        return angle - turns * 2 * MathF.PI;
    }

    // True when the input reaches the absolute sample index (exclusive), reading more from the source as needed.
    private bool Have(long end)
    {
        while (_inBase + _inCount < end)
        {
            if (_inCount >= Capacity)
                return false;
            var got = _source!.Read(_inL.AsSpan(_inCount), _inR.AsSpan(_inCount));
            if (got == 0)
                return false;
            _inCount += got;
        }

        return true;
    }

    // Releases input no later frame can reach (compacts once a quarter of capacity is droppable).
    private void Drop(long before)
    {
        var drop = (int)Math.Min(Math.Max(before - _inBase, 0), _inCount);
        if (drop < Capacity / 4)
            return;
        _inL.AsSpan(drop, _inCount - drop).CopyTo(_inL);
        _inR.AsSpan(drop, _inCount - drop).CopyTo(_inR);
        (_inBase, _inCount) = (_inBase + drop, _inCount - drop);
    }
}
