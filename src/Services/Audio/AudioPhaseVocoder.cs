using System.Collections.Concurrent;

namespace HandPegApp.Services.Audio;

// ----- Spectral Audio: the phase vocoder (5.6.0) -----
//
// A sound slowed down (or sped up a little) with its pitch kept, by the short-time Fourier transform instead of WSOLA's moved
// frames: smooth slow-motion sound, with no grain heard twice. Frames of 2048 samples at 48 kHz (4096 above 64 kHz, 1024 below
// 32 kHz) under a Hann window, a quarter of a frame apart on the output; each frame is read from where the tempo puts it on the
// input. Identity phase locking (Laroche and Dolson): each peak of the spectrum turns by the frequency it is measured to have, and
// the bins around it turn with it, so a partial stays one partial and its shape in the frame is kept. The phases are taken again
// from the input (a phase reset) where the spectral flux jumps: an onset keeps its attack. An attack still ahead of a frame's
// middle is held out of that frame (its part from the attack on is the part before it, mirrored), so nothing of it is heard before
// its time.

/// <summary>
/// The sound at <see cref="Tempo"/> times its speed, its pitch kept, by a phase vocoder. Output sample j is the input around sample
/// j x tempo, as <see cref="WsolaStretcher"/> lays it (each frame's middle on that line), so either can feed the resampler the same
/// way. Two channels share one complex transform (left in the real part, right in the imaginary), and turn by one phase taken from
/// their sum, so the stereo image holds. Nothing is allocated while reading.
/// </summary>
internal sealed class AudioPhaseVocoder : IStereoSource
{
    /// <summary>The fastest tempo the vocoder takes: a frame's analysis hop must stay under a frame for a partial's frequency to be measured (faster is WSOLA's).</summary>
    public const double FastestTempo = 3.5;

    private const int Capacity = 1 << 17;

    // The attack guard's blocks (samples), how many before one it compares with, and how much louder an attack is (power).
    private const int Block = 64, Before = 8;
    private const float AttackJump = 8f, AttackFloor = 1e-5f, QuietFloor = 1e-7f;

    // A phase reset: the flux (the new part of the spectrum's magnitude) over this much, three times its recent median, and rising.
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

    public double Tempo { get; private set; } = 1;

    /// <summary>The frame's length at a sample rate: about 43 ms.</summary>
    public static int FrameFor(int sampleRate) => sampleRate > 64000 ? 4096 : sampleRate < 32000 ? 1024 : 2048;

    /// <summary>How many output samples (at the input's rate) the first frames need before the overlap is whole: what a voice reads before where it starts.</summary>
    public static int WarmUp(int sampleRate) => FrameFor(sampleRate);

    public void Reset(IStereoSource source, double tempo, int sampleRate)
    {
        (_source, Tempo) = (source, Math.Clamp(tempo, 0.05, 64));
        var size = FrameFor(sampleRate);
        if (size != _size)
        {
            (_size, _hop, _half) = (size, size / 4, size / 2);
            _fft = Transforms.GetOrAdd(size, static n => new Radix2Fft(n));

            // A periodic Hann window, on the input and again on the output: its squares a quarter apart sum to 1.5.
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

    // After the finished hop is read: the accumulator moves on by a hop.
    private void Shift()
    {
        _accL.AsSpan(_hop).CopyTo(_accL);
        _accR.AsSpan(_hop).CopyTo(_accR);
        _accL.AsSpan(_size - _hop).Clear();
        _accR.AsSpan(_size - _hop).Clear();
        _readyAt = 0;
    }

    // Where frame k is read from: its middle (output k x hop + half a frame) on the input's tempo line.
    private long AnalysisAt(long frame) => (long)Math.Round((frame * _hop + _half) * Tempo - _half);

    private bool NextFrame()
    {
        var at = AnalysisAt(_frame);
        if (!Have(at + _size))
            return false;

        // The frame as read (before the input's start: silence), an attack still ahead of its middle held out of it, then windowed.
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

        // A phase reset: the first frame, a frame read no further on than the last (a tempo so slow it rounds to 0), or an onset.
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
    /// The attack guard: the first attack (a block of 64 samples eight times louder than the eight before it, and not quiet) found in
    /// the frame's second half (past its middle by a block) is held out: from the block before it to the frame's end, the frame is the
    /// part before that, mirrored. A stationary sound goes on as it was (the mirror has its spectrum); the attack is heard only from
    /// the frame in which it has reached the middle, whose phases the flux then resets.
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
    /// Splits the shared transform into the two channels' spectra; their magnitude (the two together) and one phase for both (their
    /// sum's, or their difference's where the sum cancels); returns the flux against the frame before: the magnitude that is new, as
    /// a share of the whole.
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
    /// Each bin's turn from its analysis phase to its output phase. Reset: none (the frame as read). Otherwise each peak turns by
    /// its measured frequency over the output hop, from its own output phase in the frame before, and every bin of its region (to the
    /// lowest bin between it and the next peak) turns as it does. Returns false for no turn at all (the frame is put back as read).
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

            // The advance a bin's centre makes over the hops, exact (whole multiples of 2 pi dropped in integers).
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

        // The ends of the spectrum stay real (a transform of a real frame): no turn there.
        (_turn[0], _turnCos[0], _turnSin[0]) = (0f, 1f, 0f);
        (_turn[_half], _turnCos[_half], _turnSin[_half]) = (0f, 1f, 0f);
        for (var b = 0; b <= _half; b++)
            _synthesis[b] = Wrap(_phase[b] + _turn[b]);
        return true;
    }

    // The spectra turned, put back together into one transform, back to time, windowed and added on.
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

        // This frame is the one the next is measured against.
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

    // An angle brought into [-pi, pi].
    private static float Wrap(float angle)
    {
        if (angle is >= -MathF.PI and <= MathF.PI)
            return angle;

        var turns = MathF.Round(angle / (2 * MathF.PI));
        return angle - turns * 2 * MathF.PI;
    }

    // Whether the input reaches the absolute sample index (exclusive), reading more as far as the source has it.
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

    // What no later frame can reach is released.
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
