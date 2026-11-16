using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services.Audio;

// ----- Voice rings feeding the mixer -----
//
// Each voice has a lock-free SPSC ring of stereo frames at the device rate on the sequence clock (ring frame f = mixer output frame f).
// The mixer (consumer) requests feeding from a frame via a seqlock of plain fields (no allocation on its thread); a feeder (producer) seeks
// the decoder there, fills ahead of the mixer, and publishes the ring index and frame its answer starts at (a second seqlock). The mixer
// reads only the answer to its newest request, so a seek never plays audio decoded for the previous position: an unready voice is silent
// for that buffer and catches up.

/// <summary>Transport line: sound time at output frame f = Origin + (f - Frame) x Rate / sample rate.</summary>
internal readonly record struct TransportLine(long Id, long Frame, double Origin, double Rate)
{
    public double SoundAt(long frame, int sampleRate) => Origin + (frame - Frame) * Rate / sampleRate;

    /// <summary>First frame at or after a sound time (far in the future for a time that never comes).</summary>
    public long FrameAt(double sound, int sampleRate)
    {
        if (Rate <= 0 || double.IsNaN(sound))
            return long.MaxValue / 4;
        var frames = Math.Ceiling((sound - Origin) * sampleRate / Rate - 1e-6);
        return frames > 4e15 ? long.MaxValue / 4 : frames < -4e15 ? long.MinValue / 4 : Frame + (long)frames;
    }
}

/// <summary>Pool of ring arrays reused across voices (each is a large-object allocation).</summary>
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

/// <summary>Runtime state of one program voice: ring, request/answer seqlocks and producer.</summary>
internal sealed class VoiceState
{
    private readonly int _capacity;
    private float[]? _ring;

    // _write is written by the producer and read by the consumer; _read the reverse.
    private long _write, _read;

    // Request seqlock (odd while being written); written only by the consumer.
    private long _requestLock, _requestSerial, _requestLineFrame, _requestStart;
    private double _requestOrigin, _requestRate;

    // Answer seqlock; written only by the producer.
    private long _answerLock, _answerSerial, _answerRing, _answerFrame;

    public VoiceState(string key, AudioVoice voice, int capacityFrames)
    {
        (Key, _voice, _capacity) = (key, voice, capacityFrames);
    }

    public string Key { get; }

    private AudioVoice _voice;

    /// <summary>The voice as the newest program defines it (timing is identical across programs sharing this state).</summary>
    public AudioVoice Voice
    {
        get => Volatile.Read(ref _voice);
        set => Volatile.Write(ref _voice, value);
    }

    // ----- Consumer side (mixer thread) -----

    private long _consumerSerial;
    private bool _answered;
    private long _ring0, _frame0;

    /// <summary>Transport line id of the last request.</summary>
    public long RequestedLine { get; private set; } = -1;

    /// <summary>Frames the consumer wanted that the producer had not written (feeder falling behind).</summary>
    public long StarvedFrames { get; set; }

    public bool NeedsRequest { get; set; }

    /// <summary>Line on which the voice was last marked idle (decoder released), so it is marked only once.</summary>
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
    /// Copies <paramref name="frames"/> frames from <paramref name="frame"/> into interleaved stereo, silence where unavailable (unanswered,
    /// starting later, not yet written). Returns the number of real frames.
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

        // Before the answer's first frame (the producer started slightly later than requested): silence.
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

    // ----- Producer side (one feeder thread at a time, guarded by Busy) -----

    /// <summary>1 while a feeder is servicing this voice.</summary>
    public int Busy;

    public VoiceProducer? Producer;

    /// <summary>Runtime version that dropped this voice; ring and decoder are released once the mixer has moved past it.</summary>
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

    /// <summary>Free ring capacity in frames.</summary>
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
    /// Producer side: rents the ring when absent. <see cref="DropRing"/> returns it while nothing will read it (voice idle or long past its
    /// end); the next answer rents one again before the mixer can read.
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

    /// <summary>Returns the ring and decoder (the voice is absent from every program the mixer may still play).</summary>
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
/// Feeds one voice: pooled decoder, WSOLA or phase vocoder for tempo other than 1, resampler for non-device rates (or fractional starts),
/// and the frame written so far.
/// </summary>
internal sealed class VoiceProducer
{
    private const int Chunk = 2048;
    private readonly int _rate;
    private readonly float[] _left = new float[Chunk], _right = new float[Chunk];
    // Created on first need (most voices play at device rate and 1x: the decoder's own samples).
    private PolyphaseResampler? _resampler;
    private WsolaStretcher? _wsola;
    private AudioPhaseVocoder? _vocoder;
    private AudioDecoder? _decoder;
    private IStereoSource? _output;
    private long _served, _produced, _end;
    private int _decoderRate;
    private bool _finished;

    public VoiceProducer(int rate) => _rate = rate;

    /// <summary>Request start meaning "nothing wanted now".</summary>
    public const long Idle = long.MaxValue;

    /// <summary>
    /// Answers a new request (seeking there), then writes ahead up to <paramref name="liveFrame"/> + <paramref name="lead"/>. Live, the answer
    /// starts at least <paramref name="margin"/> frames past the mixer position; offline (<paramref name="exact"/>) exactly where requested.
    /// </summary>
    public void Service(VoiceState state, AudioDecoderPool pool, long liveFrame, int lead, int margin, bool exact)
    {
        if (!state.ReadRequest(out var serial, out var lineFrame, out var origin, out var rate, out var start) || serial == 0)
            return;
        var line = new TransportLine(0, lineFrame, origin, rate);
        var voice = state.Voice;

        // Idle on the new transport line (a seek moved the playhead away): return the decoder.
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
            // Stream changed rate mid-way (HE-AAC SBR): rebuild the chain from the current position; the ring continues.
            Reposition(voice, line, _produced, pool);
        }

        // End per the newest program (a trim moves it without a new request).
        _end = line.FrameAt(voice.End, _rate);
        if (_finished)
        {
            // Long past its end the mixer no longer reads it: return the ring (a seek back requests again).
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
                // No data yet only happens with an FFmpeg pipe that has not caught up (decoders pad with silence past the end).
                // Live, the feeder returns later; offline (exact: AudioMixer.Render, export audio) it waits up to 20 s so the
                // render never contains a gap.
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

        // Everything to play is written: return the decoder for the file's next clip.
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

        // Device rate at 1x: the decoder's own samples, seeking to the nearest sample (a sub-sample offset is microseconds).
        if (!stretched && Math.Abs(step - 1) < 1e-9)
        {
            decoder.Seek(Math.Round(media * fs) / fs);
            _output = decoder;
            return true;
        }

        // Otherwise seek slightly earlier so the resampler kernel (and WSOLA's or the vocoder's first frames) have history. Spectral voices
        // use the phase vocoder up to its fastest tempo (WSOLA beyond), whose frames are longer than WSOLA's at low sample rates.
        var spectral = stretched && voice.Spectral && tempo <= AudioPhaseVocoder.FastestTempo;
        var reach = PolyphaseResampler.HalfTaps + 4 + (spectral ? Math.Max(fs * 0.06, AudioPhaseVocoder.WarmUp(fs)) : stretched ? fs * 0.06 : 0);
        var before = reach * (stretched ? tempo : 1) / fs;
        var at = Math.Round((media - before) * fs) / fs;
        decoder.Seek(at);
        IStereoSource input = decoder;
        if (spectral)
        {
            _vocoder ??= new AudioPhaseVocoder();
            _vocoder.Reset(decoder, tempo, fs);
            input = _vocoder;
        }
        else if (stretched)
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
/// Two feeder threads (mostly decoding, which Media Foundation does on the CPU) keeping every requested voice filled ahead of the mixer.
/// Woken by the mixer after each buffer and by every request; they never take a lock the mixer takes.
/// </summary>
internal sealed class AudioFeeders : IDisposable
{
    private readonly Thread[] _threads;
    private readonly AutoResetEvent _wake = new(false);

    // Stop signal: a ManualResetEvent wakes both feeders at once (the auto-reset wake releases only one, leaving the other on its 1 s
    // timeout at every dispose and device reopen).
    private readonly ManualResetEvent _stop = new(false);
    private readonly WaitHandle[] _wakeOrStop;
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
        _wakeOrStop = [_wake, _stop];
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
            // Woken after every mixer buffer during playback, by every request, and by stop; the timeout is only a safety net, so a
            // paused engine's feeders stay asleep.
            WaitHandle.WaitAny(_wakeOrStop, 1000);
            if (_stopping)
                break;
            try
            {
                Pass(feeder);
                LetGo();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Feeders never stop on errors: the voice whose feeding threw stays silent until its next request.
                AppLog.Write("Sound engine: a feeder failed", ex);
                Thread.Sleep(20);
            }
        }
    }

    // Services every voice once; the two feeders start at opposite ends of the list to share it rather than contend.
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

    // Releases rings and decoders of retired voices no longer held by any program the mixer may still play.
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

    /// <summary>
    /// Stops both feeders against one 2 s deadline (within the mixer thread's 3 s budget) and releases retired voices. A feeder that did not
    /// stop may still be inside a decoder read, so nothing it can reach is released (voices, decoders, events) and the caller leaves the pool's
    /// busy decoders alone.
    /// </summary>
    /// <returns>True when both feeders stopped.</returns>
    public bool Stop()
    {
        _stopping = true;
        _stop.Set();
        if (!ThreadStop.JoinAll(_threads, 2000, "Sound engine feeders"))
            return false;

        while (_retired.TryDequeue(out var state))
        {
            if (Interlocked.CompareExchange(ref state.Busy, 1, 0) == 0)
                state.Release(_pool);
        }

        _wake.Dispose();
        _stop.Dispose();
        return true;
    }

    public void Dispose() => Stop();
}
