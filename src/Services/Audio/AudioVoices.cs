using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services.Audio;

// ----- Voices on their way to the mixer (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// Each voice has a lock-free single-producer, single-consumer ring of stereo frames at the device's rate, already on the
// sequence's clock: frame f of the ring is output frame f of the mixer. The mixer (the consumer) asks for a voice to be
// fed from a frame (a request, a seqlock of plain fields: nothing is allocated on its thread); a feeder (the producer)
// seeks the voice's decoder there, fills the ring ahead of the mixer, and says from which ring index and which frame its
// answer starts (a second seqlock). The mixer reads only an answer to its newest request, so a seek never plays what was
// decoded for the moment before it: a voice not ready yet is silent for that buffer, and catches up.

/// <summary>The transport's line: sound time at output frame f is Origin + (f - Frame) x Rate / sample rate.</summary>
internal readonly record struct TransportLine(long Id, long Frame, double Origin, double Rate)
{
    public double SoundAt(long frame, int sampleRate) => Origin + (frame - Frame) * Rate / sampleRate;

    /// <summary>The first frame at or after a sound moment (a long way off for a moment that never comes).</summary>
    public long FrameAt(double sound, int sampleRate)
    {
        if (Rate <= 0 || double.IsNaN(sound))
            return long.MaxValue / 4;
        var frames = Math.Ceiling((sound - Origin) * sampleRate / Rate - 1e-6);
        return frames > 4e15 ? long.MaxValue / 4 : frames < -4e15 ? long.MinValue / 4 : Frame + (long)frames;
    }
}

/// <summary>Ring arrays, kept for the next voice: each is a large object, made once.</summary>
internal static class AudioRings
{
    private static readonly ConcurrentBag<float[]> Free = [];

    public static float[] Rent(int floats)
    {
        while (Free.TryTake(out var ring))
        {
            if (ring.Length == floats)
                return ring;
        }

        return new float[floats];
    }

    public static void Return(float[] ring)
    {
        if (Free.Count < 64)
            Free.Add(ring);
    }
}

/// <summary>One voice of the program at work: its ring, the requests and answers, and its producer.</summary>
internal sealed class VoiceState
{
    private readonly int _capacity;
    private float[]? _ring;

    // Written by the producer, read by the consumer (and the other way round for _read).
    private long _write, _read;

    // The request: a seqlock (odd while it is being written), the consumer's alone to write.
    private long _requestLock, _requestSerial, _requestLineFrame, _requestStart;
    private double _requestOrigin, _requestRate;

    // The answer: a seqlock, the producer's alone to write.
    private long _answerLock, _answerSerial, _answerRing, _answerFrame;

    public VoiceState(string key, AudioVoice voice, int capacityFrames)
    {
        (Key, _voice, _capacity) = (key, voice, capacityFrames);
    }

    public string Key { get; }

    private AudioVoice _voice;

    /// <summary>The voice as the newest program has it (its timing line is the same in every program that shares this state).</summary>
    public AudioVoice Voice
    {
        get => Volatile.Read(ref _voice);
        set => Volatile.Write(ref _voice, value);
    }

    // ----- The consumer's side (the mixer's thread) -----

    private long _consumerSerial;
    private bool _answered;
    private long _ring0, _frame0;

    /// <summary>The transport line the last request was made on.</summary>
    public long RequestedLine { get; private set; } = -1;

    /// <summary>Frames the consumer wanted and the producer had not written: a feeder falling behind.</summary>
    public long StarvedFrames { get; set; }

    public bool NeedsRequest { get; set; }

    /// <summary>The line the voice was told it is not wanted on (its decoder let go), so it is told once.</summary>
    public long IdleLine { get; private set; } = -1;

    public void Idle(in TransportLine line)
    {
        Request(line, VoiceProducer.Idle);
        (RequestedLine, IdleLine) = (-1, line.Id);
    }

    public void Request(in TransportLine line, long start)
    {
        var serial = ++_consumerSerial;
        Interlocked.Increment(ref _requestLock);
        (_requestSerial, _requestLineFrame, _requestOrigin, _requestRate, _requestStart) = (serial, line.Frame, line.Origin, line.Rate, start);
        Volatile.Write(ref _requestLock, _requestLock + 1);
        (_answered, RequestedLine, NeedsRequest, StarvedFrames) = (false, line.Id, false, 0);
    }

    /// <summary>
    /// Copies the voice's frames from <paramref name="frame"/> into interleaved stereo (<paramref name="frames"/> of them),
    /// silence for what is not there (not answered yet, started later, not written yet). Returns how many were real.
    /// </summary>
    public int Take(long frame, Span<float> stereo, int frames)
    {
        var target = stereo[..(frames * 2)];
        if (!_answered)
        {
            if (!ReadAnswer(out var serial, out var ring0, out var frame0) || serial != _consumerSerial)
            {
                target.Clear();
                return 0;
            }

            (_answered, _ring0, _frame0) = (true, ring0, frame0);
            if (ring0 > _read)
                Volatile.Write(ref _read, ring0);
        }

        var ring = _ring;
        if (ring is null)
        {
            target.Clear();
            return 0;
        }

        // Before the answer's first frame (the producer began a little later than asked): silence.
        var done = 0;
        if (frame < _frame0)
        {
            done = (int)Math.Min(frames, _frame0 - frame);
            target[..(done * 2)].Clear();
            frame += done;
        }

        var index = _ring0 + (frame - _frame0);
        var written = Volatile.Read(ref _write);
        var real = 0;
        if (index >= _read)
        {
            var available = (int)Math.Clamp(written - index, 0, frames - done);
            for (var copied = 0; copied < available;)
            {
                var at = (int)((index + copied) % _capacity);
                var run = Math.Min(available - copied, _capacity - at);
                ring.AsSpan(at * 2, run * 2).CopyTo(target[((done + copied) * 2)..]);
                copied += run;
            }

            real = available;
            Volatile.Write(ref _read, index + available);
        }

        var missing = frames - done - real;
        if (missing > 0)
        {
            target[((done + real) * 2)..].Clear();
            StarvedFrames += missing;
        }
        else
        {
            StarvedFrames = 0;
        }

        return real;
    }

    private bool ReadAnswer(out long serial, out long ring0, out long frame0)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = Volatile.Read(ref _answerLock);
            if ((before & 1) != 0)
                continue;
            (serial, ring0, frame0) = (_answerSerial, _answerRing, _answerFrame);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _answerLock) == before)
                return before != 0;
        }

        (serial, ring0, frame0) = (0, 0, 0);
        return false;
    }

    // ----- The producer's side (a feeder's thread, one at a time: Busy) -----

    /// <summary>1 while a feeder works on it.</summary>
    public int Busy;

    public VoiceProducer? Producer;

    /// <summary>The runtime version that dropped it: its ring and decoder go once the mixer has moved past it.</summary>
    public long RetiredAt = long.MaxValue;

    public bool ReadRequest(out long serial, out long lineFrame, out double origin, out double rate, out long start)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = Volatile.Read(ref _requestLock);
            if ((before & 1) != 0)
                continue;
            (serial, lineFrame, origin, rate, start) = (_requestSerial, _requestLineFrame, _requestOrigin, _requestRate, _requestStart);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _requestLock) == before)
                return before != 0;
        }

        (serial, lineFrame, origin, rate, start) = (0, 0, 0, 0, 0);
        return false;
    }

    public long RequestSerial => Volatile.Read(ref _requestSerial);

    public void Answer(long serial, long frame)
    {
        EnsureRing();
        Interlocked.Increment(ref _answerLock);
        (_answerSerial, _answerRing, _answerFrame) = (serial, _write, frame);
        Volatile.Write(ref _answerLock, _answerLock + 1);
    }

    /// <summary>Room in the ring for more frames.</summary>
    public int Room => _ring is null ? 0 : (int)(_capacity - (_write - Volatile.Read(ref _read)));

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int frames)
    {
        var ring = _ring!;
        for (var i = 0; i < frames; i++)
        {
            var at = (int)((_write + i) % _capacity) * 2;
            ring[at] = left[i];
            ring[at + 1] = right[i];
        }

        Volatile.Write(ref _write, _write + frames);
    }

    /// <summary>
    /// The producer's: gives the ring back while nothing will read it (the voice idle, or long past its end); the next
    /// answer takes one again before the mixer may read it.
    /// </summary>
    public void EnsureRing() => _ring ??= AudioRings.Rent(_capacity * 2);

    public void DropRing()
    {
        if (_ring is { } ring)
        {
            _ring = null;
            AudioRings.Return(ring);
        }
    }

    /// <summary>Gives back the ring and the decoder (the voice is gone from every program the mixer may still play).</summary>
    public void Release(AudioDecoderPool pool)
    {
        Producer?.Release(pool);
        Producer = null;
        if (_ring is { } ring)
        {
            _ring = null;
            AudioRings.Return(ring);
        }
    }
}

/// <summary>
/// What feeds one voice: its decoder (from the pool), WSOLA for a tempo other than 1, the resampler for a rate other than
/// the device's (or a start between two samples), and the frame it has written up to.
/// </summary>
internal sealed class VoiceProducer
{
    private const int Chunk = 2048;
    private readonly int _rate;
    private readonly float[] _left = new float[Chunk], _right = new float[Chunk];
    // Made the first time a voice needs them (most play at the device's rate, at speed: the decoder's own samples).
    private PolyphaseResampler? _resampler;
    private WsolaStretcher? _wsola;
    private AudioDecoder? _decoder;
    private IStereoSource? _output;
    private long _served, _produced, _end;
    private int _decoderRate;
    private bool _finished;

    public VoiceProducer(int rate) => _rate = rate;

    /// <summary>A request's start that says: nothing wanted now.</summary>
    public const long Idle = long.MaxValue;

    /// <summary>
    /// Answers a new request (seeks there), then writes ahead up to <paramref name="liveFrame"/> + <paramref name="lead"/>.
    /// Live, the answer starts no sooner than <paramref name="margin"/> frames past where the mixer is; exactly where asked
    /// otherwise (the offline render).
    /// </summary>
    public void Service(VoiceState state, AudioDecoderPool pool, long liveFrame, int lead, int margin, bool exact)
    {
        if (!state.ReadRequest(out var serial, out var lineFrame, out var origin, out var rate, out var start) || serial == 0)
            return;
        var line = new TransportLine(0, lineFrame, origin, rate);
        var voice = state.Voice;

        // Not wanted on the transport's new line (a seek took the playhead away from it): its decoder goes back.
        if (start == Idle)
        {
            if (serial != _served)
            {
                ReleaseDecoder(pool);
                state.DropRing();
                (_served, _finished) = (serial, true);
                _end = long.MinValue;
            }

            return;
        }

        if (serial != _served)
        {
            var from = exact ? start : Math.Max(start, liveFrame + margin);
            if (!Reposition(voice, line, from, pool))
                return;
            state.Answer(serial, from);
            (_served, _produced, _finished) = (serial, from, false);
        }
        else if (_decoder is not null && _decoder.SampleRate != _decoderRate)
        {
            // The stream changed its rate mid-way (HE-AAC's SBR): the chain is set up again from where it is, the ring goes on.
            Reposition(voice, line, _produced, pool);
        }

        // Its end as the newest program has it (a trim moves it without a new request).
        _end = line.FrameAt(voice.End, _rate);
        if (_finished)
        {
            // Long past its end, the mixer reads nothing of it any more: its ring goes back (a seek back asks again).
            if (_produced >= _end)
            {
                if (liveFrame > _end + 2L * lead)
                    state.DropRing();
                return;
            }

            Reposition(voice, line, _produced, pool);
            _finished = false;
        }

        var target = Math.Min(liveFrame + lead, _end);
        if (_produced < target)
            state.EnsureRing();
        var waitedSince = 0L;
        while (_produced < target && state.RequestSerial == serial)
        {
            var frames = (int)Math.Min(Math.Min(target - _produced, Chunk), state.Room);
            if (frames <= 0)
                break;
            var got = _output!.Read(_left.AsSpan(0, frames), _right.AsSpan(0, frames));
            if (got <= 0)
            {
                // Nothing yet is only ever an FFmpeg pipe that has not caught up (a decoder fills with silence past its
                // end). Live, the feeder comes back to it; offline (exact: AudioMixer.Render, Render Preview's sound) it is
                // waited for, up to 20 s, so what is rendered is the file's sound and never a gap.
                if (exact && (waitedSince == 0 || Stopwatch.GetElapsedTime(waitedSince).TotalSeconds < 20))
                {
                    waitedSince = waitedSince == 0 ? Stopwatch.GetTimestamp() : waitedSince;
                    Thread.Sleep(1);
                    continue;
                }

                break;
            }

            state.Write(_left, _right, got);
            _produced += got;
        }

        // Everything it plays is written: its decoder goes back for the next clip of the file.
        if (_produced >= _end)
        {
            _finished = true;
            ReleaseDecoder(pool);
        }
    }

    private bool Reposition(AudioVoice voice, in TransportLine line, long from, AudioDecoderPool pool)
    {
        try
        {
            _decoder ??= pool.Rent(voice.Source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write($"Sound engine: {voice.Source} could not be opened", ex);
            _decoder = new SilentDecoder(voice.Source, _rate);
        }

        var decoder = _decoder;
        var fs = _decoderRate = decoder.SampleRate;
        var sound = line.SoundAt(from, _rate);
        var media = voice.MediaAt(sound);
        var pitch = Math.Clamp(voice.Pitch, 0.25, 4);
        var tempo = Math.Clamp(voice.Rate * line.Rate / pitch, 0.05, 64);
        var stretched = Math.Abs(tempo - 1) > 1e-4;
        var step = fs * pitch / _rate;

        // At the device's rate and at speed: the decoder's own samples, exactly (from the nearest one: a part of a sample
        // is a few microseconds, nothing to the picture).
        if (!stretched && Math.Abs(step - 1) < 1e-9)
        {
            decoder.Seek(Math.Round(media * fs) / fs);
            _output = decoder;
            return true;
        }

        // Otherwise from a little before, for the resampler's kernel (and WSOLA's first frames) to have what they reach back to.
        var reach = PolyphaseResampler.HalfTaps + 4 + (stretched ? fs * 0.06 : 0);
        var before = reach * (stretched ? tempo : 1) / fs;
        var at = Math.Round((media - before) * fs) / fs;
        decoder.Seek(at);
        IStereoSource input = decoder;
        if (stretched)
        {
            _wsola ??= new WsolaStretcher();
            _wsola.Reset(decoder, tempo, fs);
            input = _wsola;
        }

        _resampler ??= new PolyphaseResampler();
        _resampler.Reset(input, step, (media - at) * fs / (stretched ? tempo : 1));
        _output = _resampler;
        return true;
    }

    private void ReleaseDecoder(AudioDecoderPool pool)
    {
        if (_decoder is { } decoder)
        {
            (_decoder, _output) = (null, null);
            pool.Return(decoder);
        }
    }

    public void Release(AudioDecoderPool pool)
    {
        ReleaseDecoder(pool);
        _served = 0;
    }
}

/// <summary>
/// The feeders: two threads (most of the work is decoding, which Media Foundation does on the processor) that keep every
/// voice the mixer asked for filled ahead of it, the least filled first. Woken by the mixer after each buffer and by every
/// request; they never take a lock the mixer takes.
/// </summary>
internal sealed class AudioFeeders : IDisposable
{
    private readonly Thread[] _threads;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<VoiceState[]?> _voices;
    private readonly Func<long> _liveFrame;
    private readonly Func<long> _observedVersion;
    private readonly ConcurrentQueue<VoiceState> _retired = new();
    private readonly AudioDecoderPool _pool;
    private readonly int _rate, _lead, _margin;
    private volatile bool _stopping;

    public AudioFeeders(int rate, int lead, int margin, AudioDecoderPool pool, Func<VoiceState[]?> voices, Func<long> liveFrame, Func<long> observedVersion)
    {
        (_rate, _lead, _margin, _pool, _voices, _liveFrame, _observedVersion) = (rate, lead, margin, pool, voices, liveFrame, observedVersion);
        _threads = new Thread[2];
        for (var i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Loop) { IsBackground = true, Name = string.Create(CultureInfo.InvariantCulture, $"HandPeg sound feeder {i + 1}"), Priority = ThreadPriority.AboveNormal };
            _threads[i].Start(i);
        }
    }

    public void Wake() => _wake.Set();

    public void Retire(VoiceState state) => _retired.Enqueue(state);

    private void Loop(object? number)
    {
        var feeder = (int)number!;
        _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
        while (!_stopping)
        {
            // Woken by the mixer after every buffer while it plays and by every request; the timeout is only a safety net,
            // so a paused engine's feeders do not wake by themselves.
            _wake.WaitOne(1000);
            if (_stopping)
                break;
            try
            {
                Pass(feeder);
                LetGo();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never down: the voice whose feeding threw is silent until its next request.
                AppLog.Write("Sound engine: a feeder failed", ex);
                Thread.Sleep(20);
            }
        }
    }

    // Every voice once. The two feeders start at opposite ends of the list, so that they share it rather than queue on it.
    private void Pass(int feeder)
    {
        if (_voices() is not { Length: > 0 } voices)
            return;
        var live = _liveFrame();
        for (var n = 0; n < voices.Length; n++)
        {
            var state = voices[feeder == 0 ? n : voices.Length - 1 - n];
            if (state.RequestSerial == 0 || Interlocked.CompareExchange(ref state.Busy, 1, 0) != 0)
                continue;
            try
            {
                state.Producer ??= new VoiceProducer(_rate);
                state.Producer.Service(state, _pool, live, _lead, _margin, exact: false);
            }
            finally
            {
                Volatile.Write(ref state.Busy, 0);
            }

            if (_stopping)
                return;
        }
    }

    // Voices no program the mixer may still play holds: their rings and decoders go.
    private void LetGo()
    {
        var observed = _observedVersion();
        var count = _retired.Count;
        for (var i = 0; i < count && _retired.TryDequeue(out var state); i++)
        {
            if (state.RetiredAt > observed || Interlocked.CompareExchange(ref state.Busy, 1, 0) != 0)
            {
                _retired.Enqueue(state);
                continue;
            }

            state.Release(_pool);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        foreach (var thread in _threads)
            thread.Join(2000);
        while (_retired.TryDequeue(out var state))
            state.Release(_pool);
        _wake.Dispose();
    }
}
