using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;
using HandPegApp.Native;

namespace HandPegApp.Services.Audio;

/// <summary>
/// Meter reading since the last read: per-channel peak and RMS (linear; see the dB properties) plus export-equivalent loudness: EBU R128
/// momentary, short-term and gated integrated loudness, 4x-oversampled true peak with 3 s hold, clip flag, and latest-block band energies
/// (AudioBandFollower). Measured on the mix after sequence master gain, before monitor volume and Mute.
/// </summary>
public readonly record struct AudioMeterReading(float PeakLeft, float PeakRight, float RmsLeft, float RmsRight, int Frames)
{
    public float PeakLeftDb => AudioMath.Decibels(PeakLeft);
    public float PeakRightDb => AudioMath.Decibels(PeakRight);
    public float RmsLeftDb => AudioMath.Decibels(RmsLeft);
    public float RmsRightDb => AudioMath.Decibels(RmsRight);

    /// <summary>Highest 4x-oversampled true peak of both channels since the last read, dBTP (negative infinity in silence).</summary>
    public float TruePeakDb { get; init; }

    /// <summary>Highest true peak over the last 3 s, dBTP.</summary>
    public float TruePeakHoldDb { get; init; }

    /// <summary>EBU R128 momentary loudness (400 ms), LUFS; negative infinity when silent.</summary>
    public float MomentaryLufs { get; init; }

    /// <summary>EBU R128 short-term loudness (3 s), LUFS.</summary>
    public float ShortTermLufs { get; init; }

    /// <summary>Gated integrated loudness since playback start or the last seek, LUFS.</summary>
    public float IntegratedLufs { get; init; }

    /// <summary>True when a sample or true peak reached 0 dBFS since the last read.</summary>
    public bool Clipped { get; init; }

    /// <summary>Latest-block band energies, 0 to 1 (AudioBandFollower: 20-160 Hz, 160 Hz-2 kHz, 2-10 kHz).</summary>
    public float Bass { get; init; }

    public float Mid { get; init; }

    public float High { get; init; }
}

/// <summary>
/// Audio engine and master clock: a shared-mode, event-driven WASAPI stream (under 15 ms queued: IAudioClient3's smallest engine period
/// keeping half the target, else IAudioClient), mixed by <see cref="AudioMixer"/> on a dedicated MMCSS ("Pro Audio") thread and written in
/// the device mix format (stereo on the first two channels, others silent; mono devices get the channel average).
/// <para><b>Clock.</b> <see cref="Position"/> is the sequence time being heard: the device position (IAudioClock, at the speakers)
/// extrapolated by the QPC counter since it was read, through transport lines (seek, speed change), plus the Audio Sync Offset. Paused,
/// it equals the last seek or pause exactly. Without a device the engine runs on QPC alone.</para>
/// <para><b>Threads.</b> All public members are thread-safe and return immediately: they write engine fields and wake the mixer, which
/// acts on its next turn. Seeks reach decoders via mixer requests (AudioVoices.cs). Nothing blocks the mixer: undecoded voices are silent
/// and catch up.</para>
/// <para><b>Devices.</b> Default-device changes and the chosen device disappearing or returning arrive via a hand-built
/// IMMNotificationClient COM object (immediate, zero wakeups while idle, unlike polling); a stream whose device is invalidated
/// (AUDCLNT_E_DEVICE_INVALIDATED) is reopened. A missing chosen device plays on the default until it returns.</para>
/// </summary>
public sealed unsafe class WasapiAudioEngine : IDisposable
{
    private const int NoDeviceRate = 48000;

    // ----- Caller requests: written by any thread, read by the mixer -----

    private string _deviceId;
    private int _bufferMs;
    private long _syncOffsetBits;
    private int _wantPlaying;
    private long _seekSerial;
    private long _rateBits = BitConverter.DoubleToInt64Bits(1);
    private int _volumeBits = BitConverter.SingleToInt32Bits(1);
    private int _muted;
    private int _reopen;
    private long _pausedBits;
    private volatile bool _stopping;
    private readonly nint _commands;
    private readonly Thread _thread;
    private readonly object _programGate = new();
    private AudioProgram _program = AudioProgram.Empty;

    // ----- Mixer thread state -----

    private AudioMixer? _mixer;
    private nint _enumerator, _device, _client, _render, _clock, _audioEvent, _timer;
    private DeviceWatch? _watch;
    private int _rate = NoDeviceRate, _channels = 2, _bits = 32;
    private bool _float = true;
    private uint _bufferFrames;
    private int _targetFrames;
    private ulong _clockFrequency;
    private long _written, _streamStart;
    private long _clockStartTicks;
    private bool _playing;
    private long _seenSeek;
    private double _rate1 = 1;
    private float[] _mix = [];
    private TransportLine _lineNow, _linePrevious;
    private bool _lineJump;
    private string _openedId = "";

    // ----- Mixer-to-caller state (seqlock) -----

    private long _clockLock;
    private double _clockHeard;
    private long _clockTicks;
    private bool _clockMoving, _clockPlaying;
    private TransportLine _clockLine, _clockPreviousLine;
    private bool _clockJump;
    private int _clockRate = NoDeviceRate;
    private long _seekPublished;
    private double _clockExtrapolate = 0.03;

    // Meter: accumulated by the mixer, published via seqlock, cleared on read; includes loudness (LiveLoudness), true peak (cleared on read
    // like peaks) and bands (AudioBandFollower), created when the device opens.
    private long _meterLock;
    private float _meterPeakL, _meterPeakR;
    private double _meterSumL, _meterSumR;
    private int _meterFrames;
    private int _meterResets, _meterResetSeen;
    private float _meterTruePeak, _meterHold, _meterMomentary = float.NegativeInfinity, _meterShortTerm = float.NegativeInfinity, _meterIntegrated = float.NegativeInfinity;
    private float _meterBass, _meterMid, _meterHigh;
    private bool _meterClipped;
    private LiveLoudness _loudness = new(NoDeviceRate);
    private AudioBandFollower _bands = new(NoDeviceRate);

    // Position's monotonic hold (caller threads only), plus the last good reading's time (Stopwatch ticks, 0 = none this playback run)
    // and line rate, used to extrapolate while the clock is unreadable.
    private readonly object _positionGate = new();
    private double _lastPosition;
    private long _lastPositionLine = -1;
    private long _lastPositionTicks;
    private double _lastPositionRate;

    // Maximum extrapolation from the last good reading while the clock is unreadable (s); then Position holds.
    private const double LongestCarry = 0.25;

    private volatile bool _hasDevice;
    private volatile string _deviceName = "Starting";

    private WasapiAudioEngine(string? deviceId, int bufferMs, double syncOffsetMs)
    {
        _deviceId = deviceId ?? "";
        _bufferMs = Math.Clamp(bufferMs, 3, 40);
        _syncOffsetBits = BitConverter.DoubleToInt64Bits(Math.Clamp(syncOffsetMs, -250, 250) / 1000);
        _commands = Kernel32.CreateEventW(0, 0, 0, 0);
        _thread = new Thread(Run) { IsBackground = true, Name = "HandPeg sound mixer", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>
    /// Starts the engine on a device (id from <see cref="AudioDevices.List"/>; null or "" = system default) with the Settings buffer and sync
    /// offset. Never throws and returns immediately: the device opens on the engine thread; without a device it runs silent on QPC (logged).
    /// </summary>
    public static WasapiAudioEngine Create(string? deviceId = null)
    {
        var (buffer, offset) = (10, 0.0);
        try
        {
            (buffer, offset) = (AppSettings.Current.AudioBufferMs, AppSettings.Current.AudioSyncOffsetMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Sound engine: the settings could not be read; the defaults are used", ex);
        }

        return new WasapiAudioEngine(deviceId, buffer, offset);
    }

    // ----- Public API -----

    /// <summary>Sets the program played from the next buffer; position and decoded audio of unchanged voices are kept.</summary>
    public void SetProgram(AudioProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        lock (_programGate)
        {
            _program = program;
            Volatile.Read(ref _mixer)?.SetProgram(program);
        }

        Wake();
    }

    /// <summary>Disposes the mixer's idle decoders (any thread; tab deactivated: <see cref="AudioMixer.ReleaseIdleDecoders"/>).</summary>
    public void ReleaseIdleDecoders() => Volatile.Read(ref _mixer)?.ReleaseIdleDecoders();

    public void Play()
    {
        Volatile.Write(ref _wantPlaying, 1);
        Wake();
    }

    /// <summary>Stops at the time being heard; <see cref="Position"/> then holds it exactly and Play resumes from it.</summary>
    public void Pause()
    {
        // Capture the time before requesting the stop so Position never shows an older value in between.
        var at = Position;
        SeekCore(at);
        Volatile.Write(ref _wantPlaying, 0);
        Wake();
    }

    /// <summary>Seeks to a sequence time (sub-microsecond here; decoders follow on their own threads).</summary>
    public void Seek(double sequenceSeconds) => SeekCore(double.IsFinite(sequenceSeconds) ? Math.Max(sequenceSeconds, 0) : 0);

    private void SeekCore(double at)
    {
        Volatile.Write(ref _pausedBits, BitConverter.DoubleToInt64Bits(at));
        Interlocked.Increment(ref _seekSerial);
        Wake();
    }

    public bool IsPlaying => Volatile.Read(ref _wantPlaying) != 0;

    /// <summary>
    /// J/K/L rate: 0.25 to 4 forward with pitch preserved; 0 stops (silent, clock held); negative runs the clock backward at that speed with
    /// silent audio (no reverse audio rendering).
    /// </summary>
    public double Rate
    {
        get => BitConverter.Int64BitsToDouble(Volatile.Read(ref _rateBits));
        set
        {
            var rate = !double.IsFinite(value) ? 1 : value == 0 ? 0 : Math.Sign(value) * Math.Clamp(Math.Abs(value), 0.25, 4);
            Volatile.Write(ref _rateBits, BitConverter.DoubleToInt64Bits(rate));
            Wake();
        }
    }

    /// <summary>Master volume as a linear factor (1 = 0 dB; max 4 = +12 dB).</summary>
    public float MasterVolume
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _volumeBits));
        set => Volatile.Write(ref _volumeBits, BitConverter.SingleToInt32Bits(float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : 1f));
    }

    public bool Muted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    public bool HasDevice => _hasDevice;

    public string DeviceName => _deviceName;

    /// <summary>Raised (on a thread-pool thread) when the device opens: at start and after each change.</summary>
    public event Action? DeviceChanged;

    /// <summary>
    /// Master clock: the sequence time currently heard (see class remarks). Monotonic while playing forward on one line; exact while
    /// paused. Cheap enough to call every compositor frame.
    /// </summary>
    public double Position
    {
        get
        {
            var paused = BitConverter.Int64BitsToDouble(Volatile.Read(ref _pausedBits));
            if (Volatile.Read(ref _wantPlaying) == 0 || Volatile.Read(ref _seekSerial) != Volatile.Read(ref _seekPublished))
                return Paused(paused);
            if (!ReadClock(out var heard, out var ticks, out var moving, out var playing, out var line, out var previous, out var jump, out var rate))
                return Carried(paused);
            if (!playing)
                return Paused(paused);

            if (moving)
            {
                var elapsed = (Stopwatch.GetTimestamp() - ticks) / (double)Stopwatch.Frequency;
                heard += Math.Clamp(elapsed, 0, Volatile.Read(ref _clockExtrapolate)) * rate;
            }

            var sound = heard >= line.Frame ? line.Origin + (heard - line.Frame) * line.Rate / rate
                : jump ? line.Origin
                : previous.Origin + (heard - previous.Frame) * previous.Rate / rate;
            var position = sound + SyncOffset;

            // Never step backward within a forward-running line (QPC extrapolation is corrected by each device reading).
            lock (_positionGate)
            {
                if (_lastPositionLine == line.Id && line.Rate > 0 && position < _lastPosition)
                    position = _lastPosition;
                else if (_lastPositionLine == line.Id && line.Rate < 0 && position > _lastPosition)
                    position = _lastPosition;
                (_lastPosition, _lastPositionLine) = (position, line.Id);
                (_lastPositionTicks, _lastPositionRate) = (Stopwatch.GetTimestamp(), line.Rate);
            }

            return position;
        }
    }

    // Paused, seeking or not yet started: return the requested time and reset the extrapolation baseline.
    private double Paused(double paused)
    {
        Volatile.Write(ref _lastPositionTicks, 0);
        return paused;
    }

    // Playing but the clock is unreadable (writer held the seqlock every try): extrapolate from the last good reading at its line rate,
    // up to LongestCarry, never jumping back to the paused time (which would draw a stray frame mid-play). The paused time is returned
    // only before the run's first reading.
    private double Carried(double paused)
    {
        lock (_positionGate)
        {
            if (_lastPositionTicks == 0)
                return paused;
            var elapsed = Math.Clamp((Stopwatch.GetTimestamp() - _lastPositionTicks) / (double)Stopwatch.Frequency, 0, LongestCarry);
            return _lastPosition + elapsed * _lastPositionRate;
        }
    }

    /// <summary>
    /// Peaks and RMS since the last read plus loudness, true peak and bands (<see cref="AudioMeterReading"/>), measured after sequence
    /// master gain and before monitor volume and Mute, i.e. what an export receives.
    /// </summary>
    public AudioMeterReading Meter
    {
        get
        {
            AudioMeterReading reading = default;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var before = Volatile.Read(ref _meterLock);
                if ((before & 1) != 0)
                    continue;
                var frames = _meterFrames;
                reading = new AudioMeterReading(_meterPeakL, _meterPeakR,
                    frames > 0 ? (float)Math.Sqrt(_meterSumL / frames) : 0, frames > 0 ? (float)Math.Sqrt(_meterSumR / frames) : 0, frames)
                {
                    TruePeakDb = PeakDecibels(_meterTruePeak),
                    TruePeakHoldDb = PeakDecibels(_meterHold),
                    MomentaryLufs = _meterMomentary,
                    ShortTermLufs = _meterShortTerm,
                    IntegratedLufs = _meterIntegrated,
                    Clipped = _meterClipped,
                    Bass = _meterBass,
                    Mid = _meterMid,
                    High = _meterHigh,
                };
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _meterLock) == before)
                    break;
            }

            Interlocked.Increment(ref _meterResets);
            return reading;
        }
    }

    /// <summary>
    /// Band energies of the latest played block (0 to 1; fallback source for audio-reactive effects), read without clearing the meter.
    /// </summary>
    public (float Bass, float Mid, float High) LiveBands
    {
        get
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var before = Volatile.Read(ref _meterLock);
                if ((before & 1) != 0)
                    continue;
                var bands = (_meterBass, _meterMid, _meterHigh);
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _meterLock) == before)
                    return bands;
            }

            return (0, 0, 0);
        }
    }

    // Converts a linear true peak to dBTP (negative infinity for silence).
    private static float PeakDecibels(float factor) => factor > 0 ? 20f * MathF.Log10(factor) : float.NegativeInfinity;

    /// <summary>Applies Settings: device (null or "" = default), buffer (ms) and sync offset (ms). Reopens only when the device or buffer changed.</summary>
    public void ApplySettings(string? deviceId, int bufferMs, double syncOffsetMs)
    {
        var id = deviceId ?? "";
        var buffer = Math.Clamp(bufferMs, 3, 40);
        var offset = Math.Clamp(syncOffsetMs, -250, 250) / 1000;
        var reopen = !string.Equals(id, Volatile.Read(ref _deviceId), StringComparison.Ordinal) || buffer != Volatile.Read(ref _bufferMs);
        Volatile.Write(ref _deviceId, id);
        Volatile.Write(ref _bufferMs, buffer);
        if (BitConverter.DoubleToInt64Bits(offset) != Volatile.Read(ref _syncOffsetBits))
        {
            var at = Position;
            Volatile.Write(ref _syncOffsetBits, BitConverter.DoubleToInt64Bits(offset));
            SeekCore(at);
        }

        if (reopen)
        {
            Volatile.Write(ref _reopen, 1);
            Wake();
        }
    }

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        Kernel32.SetEvent(_commands);

        // The mixer thread unregisters the device watch (whose callbacks signal _commands) in its finally. If it has not stopped, keep the
        // event open: closing it would let a late callback or a Wake from a resumed pump signal a recycled handle.
        if (!ThreadStop.Join(_thread, 3000, "Sound engine mixer"))
            return;
        Kernel32.CloseHandle(_commands);
        Volatile.Write(ref _closed, 1);
    }

    // Set once _commands is closed: a late Wake (pump still running after a join timeout) must not signal a recycled handle.
    private int _closed;

    private double SyncOffset => BitConverter.Int64BitsToDouble(Volatile.Read(ref _syncOffsetBits));

    private void Wake()
    {
        if (Volatile.Read(ref _closed) == 0)
            Kernel32.SetEvent(_commands);
    }

    // ----- Mixer thread -----

    private void Run()
    {
        _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
        uint task = 0;
        var mmcss = AudioNative.AvSetMmThreadCharacteristicsW("Pro Audio", &task);
        _audioEvent = Kernel32.CreateEventW(0, 0, 0, 0);
        _timer = Kernel32.CreateWaitableTimerExW(0, 0, Kernel32.HighResolutionTimer, Kernel32.TimerAllAccess);
        try
        {
            Enumerate();
            Open();
            while (!_stopping)
            {
                Wait();
                if (_stopping)
                    break;
                try
                {
                    Turn();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Never stay down: reopen the device; the clock continues from where it was.
                    AppLog.Write("Sound engine: the mixer failed; the device is opened again", ex);
                    Volatile.Write(ref _reopen, 1);
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Sound engine: stopped", ex);
        }
        finally
        {
            // Unregister the device watch first: its callbacks signal _commands, and the teardown below (mixer and feeders) can take up to
            // 2 s, during which Dispose may be deciding whether to close that event.
            _watch?.Dispose(_enumerator);
            Close();
            Com.Release(ref _enumerator);
            Kernel32.CloseHandle(_audioEvent);
            if (_timer != 0)
                Kernel32.CloseHandle(_timer);
            if (mmcss != 0)
                AudioNative.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    private void Turn()
    {
        if (Interlocked.Exchange(ref _reopen, 0) != 0 || _watch?.Take(_openedId, Volatile.Read(ref _deviceId).Length == 0, _hasDevice) == true)
            Reopen();

        Commands();
        if (_playing)
            Render();
        _mixer?.Prime(_written);
        PublishClock();

        // Only now that the clock carries the seek's line may Position stop holding the seek time.
        Volatile.Write(ref _seekPublished, _seenSeek);
    }

    // Waits until the device needs data, the timer ticks (no device), or a caller signals.
    private void Wait()
    {
        var handles = stackalloc nint[2];
        handles[0] = _commands;
        var count = 1u;
        var timeout = AudioNative.Infinite;
        if (_playing && _client != 0)
        {
            handles[count++] = _audioEvent;
            timeout = 200;
        }
        else if (_playing && _timer != 0)
        {
            var due = -50_000L; // 5 ms, relative
            Kernel32.SetWaitableTimer(_timer, &due, 0, 0, 0, 0);
            handles[count++] = _timer;
            timeout = 50;
        }
        else if (_playing)
        {
            timeout = 5;
        }

        Kernel32.WaitForMultipleObjects(count, handles, 0, timeout);
    }

    private void Commands()
    {
        if (_mixer is not { } mixer)
            return;
        var rate = BitConverter.Int64BitsToDouble(Volatile.Read(ref _rateBits));
        var serial = Volatile.Read(ref _seekSerial);
        if (serial != _seenSeek)
        {
            var target = ReadSeek();
            _seenSeek = serial;
            Line(mixer, _written, target - SyncOffset, rate, jump: true);
        }
        else if (rate != _rate1)
        {
            Line(mixer, _written, mixer.Line.SoundAt(_written, _rate), rate, jump: false);
        }

        var want = Volatile.Read(ref _wantPlaying) != 0;
        if (want && !_playing)
            StartPlaying();
        else if (!want && _playing)
            StopPlaying();
    }

    private void Line(AudioMixer mixer, long frame, double origin, double rate, bool jump)
    {
        _rate1 = rate;
        _linePrevious = _lineNow;
        mixer.SetLine(frame, origin, rate, jump);
        (_lineNow, _lineJump) = (mixer.Line, jump);

        // On a jump (seek, pause, play from there), integrated loudness restarts here.
        if (jump)
            _loudness.Restart();
    }

    private double ReadSeek() => BitConverter.Int64BitsToDouble(Volatile.Read(ref _pausedBits));

    private void StartPlaying()
    {
        _playing = true;
        _streamStart = _written;
        _clockMoving = false;
        if (_client != 0)
        {
            Render();
            var hr = AudioNative.Start(_client);
            if (hr < 0)
            {
                AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Sound engine: the stream would not start (0x{hr:X8}); the device is opened again"));
                Volatile.Write(ref _reopen, 1);
            }
        }
        else
        {
            _clockStartTicks = Stopwatch.GetTimestamp();
        }
    }

    private void StopPlaying()
    {
        _playing = false;
        if (_client != 0)
        {
            AudioNative.Stop(_client);
            AudioNative.Reset(_client);
        }

        // Queued audio is discarded: playback resumes exactly from the paused time (Pause captured it before stopping).
        if (_mixer is { } mixer)
            Line(mixer, _written, BitConverter.Int64BitsToDouble(Volatile.Read(ref _pausedBits)) - SyncOffset, _rate1, jump: true);
    }

    // Fills the device queue up to the target (bounded by free buffer space) each turn.
    private void Render()
    {
        if (_mixer is not { } mixer)
            return;

        int want;
        if (_client != 0)
        {
            var hr = AudioNative.GetCurrentPadding(_client, out var padding);
            if (hr < 0)
            {
                if (AudioNative.IsDeviceGone(hr))
                    Volatile.Write(ref _reopen, 1);
                return;
            }

            want = (int)Math.Min(_bufferFrames - padding, (long)_targetFrames - padding);
        }
        else
        {
            // No device: QPC stands in for the device clock, the target for its queue.
            var played = _streamStart + (long)((Stopwatch.GetTimestamp() - _clockStartTicks) * (double)_rate / Stopwatch.Frequency);
            want = (int)Math.Clamp(played + _targetFrames - _written, 0, mixer.MaxFrames * 4L);
        }

        while (want > 0)
        {
            var frames = Math.Min(want, mixer.MaxFrames);
            mixer.Mix(_mix, _written, frames);
            if (_client != 0)
            {
                var hr = AudioNative.GetBuffer(_render, (uint)frames, out var data);
                if (hr < 0)
                {
                    if (AudioNative.IsDeviceGone(hr))
                        Volatile.Write(ref _reopen, 1);
                    return;
                }

                Finish(frames, data);
                AudioNative.ReleaseBuffer(_render, (uint)frames, 0);
            }
            else
            {
                Finish(frames, null);
            }

            _written += frames;
            want -= frames;
        }

        mixer.WakeFeeders();
    }

    // Meters the mix (before monitor volume, as an export sees it), applies master volume, then converts to the device format (nothing is
    // written without a device).
    private void Finish(int frames, byte* data)
    {
        var mix = _mix.AsSpan(0, frames * 2);
        if (Volatile.Read(ref _meterResets) != _meterResetSeen)
        {
            _meterResetSeen = Volatile.Read(ref _meterResets);
            Interlocked.Increment(ref _meterLock);
            (_meterPeakL, _meterPeakR, _meterSumL, _meterSumR, _meterFrames) = (0, 0, 0, 0, 0);
            (_meterTruePeak, _meterClipped) = (0, false);
            _loudness.TakePeak();
            Volatile.Write(ref _meterLock, _meterLock + 1);
        }

        var (peakL, peakR, sumL, sumR) = (_meterPeakL, _meterPeakR, _meterSumL, _meterSumR);
        for (var i = 0; i < frames; i++)
        {
            var (l, r) = (mix[2 * i], mix[2 * i + 1]);
            (peakL, peakR) = (Math.Max(peakL, Math.Abs(l)), Math.Max(peakR, Math.Abs(r)));
            (sumL, sumR) = (sumL + l * l, sumR + r * r);
        }

        // Loudness, true peak and bands: mixer-owned until published below.
        _loudness.Add(mix, frames);
        _bands.Process(mix, frames);

        Interlocked.Increment(ref _meterLock);
        (_meterPeakL, _meterPeakR, _meterSumL, _meterSumR) = (peakL, peakR, sumL, sumR);
        _meterFrames = (int)Math.Min((long)_meterFrames + frames, int.MaxValue);
        (_meterTruePeak, _meterHold, _meterClipped) = (_loudness.Peak, _loudness.HeldPeak, _loudness.Clipped || peakL >= 1 || peakR >= 1);
        (_meterMomentary, _meterShortTerm, _meterIntegrated) = (_loudness.MomentaryLufs, _loudness.ShortTermLufs, _loudness.IntegratedLufs);
        (_meterBass, _meterMid, _meterHigh) = (_bands.Bass, _bands.Mid, _bands.High);
        Volatile.Write(ref _meterLock, _meterLock + 1);

        // Monitor volume affects the speakers only, not the meter.
        AudioMath.Scale(mix, MasterVolume);
        if (data == null)
            return;
        if (Muted)
            mix.Clear();

        var channels = _channels;
        if (_float && _bits == 32)
        {
            var output = (float*)data;
            if (channels == 2)
            {
                mix.CopyTo(new Span<float>(output, frames * 2));
                return;
            }

            for (var i = 0; i < frames; i++)
            {
                var frame = output + i * channels;
                if (channels == 1)
                {
                    frame[0] = (mix[2 * i] + mix[2 * i + 1]) * 0.5f;
                    continue;
                }

                (frame[0], frame[1]) = (mix[2 * i], mix[2 * i + 1]);
                for (var c = 2; c < channels; c++)
                    frame[c] = 0;
            }

            return;
        }

        // Integer formats: 16, 24 (packed) or 32 bits (24 valid in 32).
        var bytes = _bits / 8;
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                var value = channels == 1 ? (mix[2 * i] + mix[2 * i + 1]) * 0.5f : c < 2 ? mix[2 * i + c] : 0f;
                value = Math.Clamp(value, -1f, 1f);
                var at = data + (i * channels + c) * bytes;
                switch (bytes)
                {
                    case 2:
                        *(short*)at = (short)Math.Round(value * 32767f);
                        break;
                    case 3:
                        var v24 = (int)Math.Round(value * 8388607f);
                        (at[0], at[1], at[2]) = ((byte)v24, (byte)(v24 >> 8), (byte)(v24 >> 16));
                        break;
                    default:
                        *(int*)at = (int)Math.Clamp(Math.Round(value * 2147483647.0), int.MinValue, int.MaxValue);
                        break;
                }
            }
        }
    }

    private void PublishClock()
    {
        var heard = (double)_written;
        var ticks = Stopwatch.GetTimestamp();
        var moving = false;
        if (_playing && _client != 0 && _clock != 0)
        {
            if (AudioNative.GetPosition(_clock, out var position, out var qpc) >= 0 && _clockFrequency > 0)
            {
                heard = _streamStart + position * (double)_rate / _clockFrequency;
                ticks = (long)(qpc * (double)Stopwatch.Frequency / 1e7);
                moving = position > 0;
            }
        }
        else if (_playing)
        {
            heard = _streamStart + (ticks - _clockStartTicks) * (double)_rate / Stopwatch.Frequency - _targetFrames;
            heard = Math.Max(heard, _streamStart);
            moving = true;
        }

        Interlocked.Increment(ref _clockLock);
        (_clockHeard, _clockTicks, _clockMoving, _clockPlaying) = (heard, ticks, moving, _playing);
        (_clockLine, _clockPreviousLine, _clockJump, _clockRate) = (_lineNow, _linePrevious, _lineJump, _rate);
        Volatile.Write(ref _clockLock, _clockLock + 1);
    }

    private bool ReadClock(out double heard, out long ticks, out bool moving, out bool playing, out TransportLine line, out TransportLine previous, out bool jump, out int rate)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var before = Volatile.Read(ref _clockLock);
            if ((before & 1) != 0)
                continue;
            (heard, ticks, moving, playing) = (_clockHeard, _clockTicks, _clockMoving, _clockPlaying);
            (line, previous, jump, rate) = (_clockLine, _clockPreviousLine, _clockJump, _clockRate);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _clockLock) == before)
                return before != 0;
        }

        (heard, ticks, moving, playing, line, previous, jump, rate) = (0, 0, false, false, default, default, false, NoDeviceRate);
        return false;
    }

    // ----- Devices -----

    private void Enumerate()
    {
        var classId = AudioNative.DeviceEnumeratorClass;
        var interfaceId = AudioNative.DeviceEnumeratorId;
        nint enumerator;
        var hr = AudioNative.CoCreateInstance(&classId, 0, AudioNative.ClassContextAll, &interfaceId, &enumerator);
        if (hr < 0)
        {
            AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Sound engine: the device list is not available (0x{hr:X8}); it runs on the processor's clock"));
            return;
        }

        _enumerator = enumerator;
        _watch = DeviceWatch.Register(_enumerator, _commands);
    }

    private void Reopen()
    {
        // Resume from the time being heard on the new device; only audio queued for the old device is lost.
        var at = _mixer is { } mixer ? mixer.Line.SoundAt((long)Math.Min(_clockHeard, _written), _rate) : 0;
        var wasPlaying = _playing;
        Close();
        Open();
        if (_mixer is { } fresh)
            Line(fresh, _written, at, _rate1, jump: true);
        if (wasPlaying)
            StartPlaying();
    }

    private void Open()
    {
        var wanted = Volatile.Read(ref _deviceId);
        _hasDevice = false;
        if (_enumerator != 0)
        {
            var hr = wanted.Length > 0 ? AudioNative.GetDevice(_enumerator, wanted, out _device) : -1;
            if (hr < 0 || _device == 0)
            {
                if (wanted.Length > 0)
                    AppLog.Write("Sound engine: the chosen output device is not there; the default one plays until it is back");
                Com.Release(ref _device);
                hr = AudioNative.GetDefaultAudioEndpoint(_enumerator, AudioNative.Render, AudioNative.Console, out _device);
            }

            if (hr >= 0 && _device != 0)
            {
                try
                {
                    OpenStream();
                    _hasDevice = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLog.Write("Sound engine: the device could not be opened; it runs on the processor's clock", ex);
                    CloseStream();
                }
            }
        }

        if (!_hasDevice)
        {
            (_rate, _channels, _bits, _float, _openedId, _bufferFrames) = (NoDeviceRate, 2, 32, true, "", 0);
            _targetFrames = Math.Max(_rate * Volatile.Read(ref _bufferMs) / 1000, 128);
            _deviceName = "No sound device";
            _clockExtrapolate = 0.03;
        }

        var frames = _hasDevice ? (int)Math.Max(_bufferFrames, 256) : 2048;
        _mix = new float[frames * 2];
        (_loudness, _bands) = (new LiveLoudness(_rate), new AudioBandFollower(_rate));
        lock (_programGate)
        {
            var mixer = new AudioMixer(_rate, frames, threaded: true);
            mixer.SetProgram(_program);
            Volatile.Write(ref _mixer, mixer);
        }

        Line(_mixer!, _written, BitConverter.Int64BitsToDouble(Volatile.Read(ref _pausedBits)) - SyncOffset, _rate1, jump: true);
        _seenSeek = Volatile.Read(ref _seekSerial);
        Volatile.Write(ref _seekPublished, _seenSeek);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Sound engine: {(_hasDevice ? $"\"{_deviceName}\"" : "no device")}, {_rate} Hz, {_channels} channel(s), {(_float ? "float" : "integer")} {_bits}-bit, {_targetFrames * 1000.0 / _rate:0.0} ms queued (buffer {_bufferFrames} frames)"));

        // Notify device-name observers off this thread (at start and on every reopen).
        if (DeviceChanged is { } changed)
            ThreadPool.QueueUserWorkItem(_ => changed());
    }

    private void OpenStream()
    {
        _openedId = AudioNative.DeviceId(_device);
        _deviceName = AudioNative.DeviceName(_device) is { Length: > 0 } name ? name : "Sound device";

        var client3 = AudioNative.Activate(_device, AudioNative.AudioClient3Id, out var three) >= 0;
        _client = three;
        if (!client3)
            Com.Check(AudioNative.Activate(_device, AudioNative.AudioClientId, out _client), "IMMDevice::Activate (IAudioClient)");

        Com.Check(AudioNative.GetMixFormat(_client, out var mixFormat), "IAudioClient::GetMixFormat");
        WaveFormat format = default;
        try
        {
            // A plain WAVEFORMATEX is 18 bytes: read only what the device returned.
            var size = mixFormat->FormatTag == WaveFormat.Extensible && mixFormat->ExtraSize >= 22 ? 40 : 18;
            Buffer.MemoryCopy(mixFormat, &format, sizeof(WaveFormat), size);
            if (size == 18)
                format.ExtraSize = 0;
        }
        finally
        {
            Com.CoTaskMemFree((nint)mixFormat);
        }

        _rate = (int)format.SamplesPerSecond;
        _channels = Math.Max((int)format.Channels, 1);
        _bits = format.BitsPerSample;
        _float = format.FormatTag == WaveFormat.IeeeFloat || (format.FormatTag == WaveFormat.Extensible && format.SubFormat == AudioNative.SubtypeFloat);
        if (_rate < 8000 || _rate > 768000 || (_float && _bits != 32) || (!_float && _bits is not (16 or 24 or 32)))
            throw new GpuException(unchecked((int)0x88890008), "the device's mix format is not one HandPeg writes");

        // Engine period: IAudioClient3's smallest keeping half the target queued (rounded to its step), else IAudioClient with the target
        // as the buffer.
        var bufferMs = Volatile.Read(ref _bufferMs);
        var initialized = false;
        var period = 0u;
        if (client3 && AudioNative.GetSharedModeEnginePeriod(_client, &format, out var standard, out var step, out var minimum, out var maximum) >= 0 && step > 0)
        {
            var wanted = (uint)(_rate * bufferMs / 2000);
            period = Math.Clamp((wanted + step - 1) / step * step, minimum, Math.Min(maximum, standard));
            initialized = AudioNative.InitializeSharedAudioStream(_client, AudioNative.StreamFlagsEventCallback, period, &format) >= 0;
            if (!initialized)
            {
                Com.Release(ref _client);
                Com.Check(AudioNative.Activate(_device, AudioNative.AudioClientId, out _client), "IMMDevice::Activate (IAudioClient)");
            }
        }

        if (!initialized)
        {
            Com.Check(AudioNative.Initialize(_client, AudioNative.StreamFlagsEventCallback, bufferMs * 10_000L, &format), "IAudioClient::Initialize");
            period = AudioNative.GetDevicePeriod(_client, out var defaultPeriod, out _) >= 0 ? (uint)(defaultPeriod * _rate / 10_000_000) : (uint)(_rate / 100);
        }

        Com.Check(AudioNative.GetBufferSize(_client, out _bufferFrames), "IAudioClient::GetBufferSize");
        Com.Check(AudioNative.SetEventHandle(_client, _audioEvent), "IAudioClient::SetEventHandle");
        Com.Check(AudioNative.GetService(_client, AudioNative.RenderClientId, out _render), "IAudioClient::GetService (render)");
        Com.Check(AudioNative.GetService(_client, AudioNative.AudioClockId, out _clock), "IAudioClient::GetService (clock)");
        Com.Check(AudioNative.GetFrequency(_clock, out _clockFrequency), "IAudioClock::GetFrequency");

        // Target queue: the setting, at least 1.5 periods (one event per period), at most the buffer.
        _targetFrames = (int)Math.Clamp(Math.Max(_rate * bufferMs / 1000, period * 3 / 2 + 32), 64, _bufferFrames);
        _clockExtrapolate = Math.Max(2.0 * period / _rate, 0.005);
    }

    private void Close()
    {
        if (_playing && _client != 0)
            AudioNative.Stop(_client);
        _playing = false;
        if (Volatile.Read(ref _mixer) is { } mixer)
        {
            Volatile.Write(ref _mixer, null);
            mixer.Dispose();
        }

        CloseStream();
    }

    private void CloseStream()
    {
        Com.Release(ref _clock);
        Com.Release(ref _render);
        Com.Release(ref _client);
        Com.Release(ref _device);
    }

    /// <summary>
    /// Hand-built IMMNotificationClient: a native block holding the vtable pointer, a refcount and a GCHandle back to this object. Methods
    /// only set flags and wake the mixer (Windows calls them on its own thread, which must not block); the mixer checks the flags on its
    /// next turn.
    /// </summary>
    private sealed class DeviceWatch
    {
        private static readonly nint* Methods = MakeMethods();
        private readonly nint _wake;
        private nint _native;
        private GCHandle _self;
        private int _defaultChanged, _devicesChanged;
        private string _changedId = "";

        private DeviceWatch(nint wake) => _wake = wake;

        public static DeviceWatch? Register(nint enumerator, nint wake)
        {
            var watch = new DeviceWatch(wake);
            watch._self = GCHandle.Alloc(watch);
            var native = (nint*)NativeMemory.Alloc(3, (nuint)sizeof(nint));
            (native[0], native[1], native[2]) = ((nint)Methods, 1, GCHandle.ToIntPtr(watch._self));
            watch._native = (nint)native;
            if (AudioNative.RegisterEndpointNotificationCallback(enumerator, watch._native) < 0)
            {
                AppLog.Write("Sound engine: device changes cannot be followed (the stream is opened again when it fails)");
                Release(watch._native);
                return null;
            }

            return watch;
        }

        /// <summary>True when the device should reopen: default changed while following it, the chosen device changed, or a device arrived while none was open.</summary>
        public bool Take(string openedId, bool followsDefault, bool hasDevice)
        {
            var defaultChanged = Interlocked.Exchange(ref _defaultChanged, 0) != 0;
            var devicesChanged = Interlocked.Exchange(ref _devicesChanged, 0) != 0;
            var id = Volatile.Read(ref _changedId);
            return (defaultChanged && followsDefault) || (devicesChanged && (!hasDevice || !followsDefault || string.Equals(id, openedId, StringComparison.OrdinalIgnoreCase)));
        }

        public void Dispose(nint enumerator)
        {
            if (_native == 0)
                return;
            if (enumerator != 0)
                AudioNative.UnregisterEndpointNotificationCallback(enumerator, _native);
            Release(_native);
            _native = 0;
        }

        private static nint* MakeMethods()
        {
            var table = (nint*)NativeMemory.Alloc(8, (nuint)sizeof(nint));
            table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
            table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
            table[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseMethod;
            table[3] = (nint)(delegate* unmanaged<nint, char*, uint, int>)&OnDeviceStateChanged;
            table[4] = (nint)(delegate* unmanaged<nint, char*, int>)&OnDeviceAdded;
            table[5] = (nint)(delegate* unmanaged<nint, char*, int>)&OnDeviceRemoved;
            table[6] = (nint)(delegate* unmanaged<nint, int, int, char*, int>)&OnDefaultDeviceChanged;
            table[7] = (nint)(delegate* unmanaged<nint, char*, PropertyKey, int>)&OnPropertyValueChanged;
            return table;
        }

        private static DeviceWatch? Of(nint self)
        {
            var handle = ((nint*)self)[2];
            return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as DeviceWatch;
        }

        private static uint Release(nint self)
        {
            long count = Interlocked.Decrement(ref Unsafe.AsRef<long>((void*)(self + sizeof(nint))));
            if (count == 0)
            {
                var handle = ((nint*)self)[2];
                if (handle != 0)
                    GCHandle.FromIntPtr(handle).Free();
                NativeMemory.Free((void*)self);
            }

            return (uint)count;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterface(nint self, Guid* id, nint* result)
        {
            if (*id == AudioNative.UnknownId || *id == AudioNative.NotificationClientId)
            {
                Interlocked.Increment(ref Unsafe.AsRef<long>((void*)(self + sizeof(nint))));
                *result = self;
                return 0;
            }

            *result = 0;
            return unchecked((int)0x80004002);
        }

        [UnmanagedCallersOnly]
        private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref Unsafe.AsRef<long>((void*)(self + sizeof(nint))));

        [UnmanagedCallersOnly]
        private static uint ReleaseMethod(nint self) => Release(self);

        [UnmanagedCallersOnly]
        private static int OnDeviceStateChanged(nint self, char* id, uint state) => Changed(self, id);

        [UnmanagedCallersOnly]
        private static int OnDeviceAdded(nint self, char* id) => Changed(self, id);

        [UnmanagedCallersOnly]
        private static int OnDeviceRemoved(nint self, char* id) => Changed(self, id);

        [UnmanagedCallersOnly]
        private static int OnDefaultDeviceChanged(nint self, int flow, int role, char* id)
        {
            try
            {
                if (flow == AudioNative.Render && role == AudioNative.Console && Of(self) is { } watch)
                {
                    Volatile.Write(ref watch._defaultChanged, 1);
                    Kernel32.SetEvent(watch._wake);
                }
            }
            catch
            {
                // Exceptions must not escape a native callback.
            }

            return 0;
        }

        [UnmanagedCallersOnly]
        private static int OnPropertyValueChanged(nint self, char* deviceId, PropertyKey key) => 0;

        private static int Changed(nint self, char* id)
        {
            try
            {
                if (Of(self) is { } watch)
                {
                    Volatile.Write(ref watch._changedId, id == null ? "" : new string(id));
                    Volatile.Write(ref watch._devicesChanged, 1);
                    Kernel32.SetEvent(watch._wake);
                }
            }
            catch
            {
                // Exceptions must not escape a native callback.
            }

            return 0;
        }
    }
}

/// <summary>An audio endpoint: id (persisted in Settings), friendly name and default flag.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Enumerates active audio outputs (Settings) and inputs (Voiceover). Call off the UI thread (queries the audio service).</summary>
public static unsafe class AudioDevices
{
    public static IReadOnlyList<AudioDevice> List() => Endpoints(AudioNative.Render);

    /// <summary>Active capture endpoints (microphones, virtual mixers such as a streaming app's input).</summary>
    public static IReadOnlyList<AudioDevice> Microphones() => Endpoints(AudioNative.Capture);

    private static List<AudioDevice> Endpoints(int flow)
    {
        var list = new List<AudioDevice>();
        var classId = AudioNative.DeviceEnumeratorClass;
        var interfaceId = AudioNative.DeviceEnumeratorId;
        nint enumerator;
        if (AudioNative.CoCreateInstance(&classId, 0, AudioNative.ClassContextAll, &interfaceId, &enumerator) < 0)
            return list;
        nint collection = 0, standard = 0;
        try
        {
            var defaultId = AudioNative.GetDefaultAudioEndpoint(enumerator, flow, AudioNative.Console, out standard) >= 0 ? AudioNative.DeviceId(standard) : "";
            if (AudioNative.EnumAudioEndpoints(enumerator, flow, AudioNative.DeviceStateActive, out collection) < 0)
                return list;
            var count = AudioNative.CollectionCount(collection);
            for (uint i = 0; i < count; i++)
            {
                if (AudioNative.CollectionItem(collection, i, out var device) < 0)
                    continue;
                try
                {
                    var id = AudioNative.DeviceId(device);
                    if (id.Length > 0)
                        list.Add(new AudioDevice(id, AudioNative.DeviceName(device) is { Length: > 0 } name ? name : id, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    Com.Release(ref device);
                }
            }
        }
        finally
        {
            Com.Release(ref collection);
            Com.Release(ref standard);
            Com.Release(ref enumerator);
        }

        return list;
    }
}
