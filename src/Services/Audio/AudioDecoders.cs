using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using CliWrap;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services.Audio;

// ----- Decoding a file's sound (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// Media Foundation's Source Reader decodes each (file, audio stream) to float on the feeders' threads, at the file's own
// rate, made stereo as FFmpeg's aformat would (swresample's matrix: mono at -3 dB on both sides, a centre and the
// surrounds at -3 dB, the LFE left out, unnormalized for float). Seeks are exact to the sample: the reader is sent to a
// tenth of a second before the moment (an AAC frame needs the one before it to decode cleanly) and what comes before it
// is dropped, by the sample times. A source Media Foundation cannot read falls back to an FFmpeg pipe (f32le, stereo, at
// the device's rate), run through WorkScheduler and ProcessPipes like every tool HandPeg starts.

/// <summary>One audio stream of one file, decoded: stereo at <see cref="SampleRate"/>, positioned by media time.</summary>
internal abstract class AudioDecoder : IStereoSource, IDisposable
{
    protected AudioDecoder(AudioSource source) => Source = source;

    public AudioSource Source { get; }

    public abstract int SampleRate { get; }

    /// <summary>Media seconds of sound the stream has (0 when not known).</summary>
    public abstract double Duration { get; }

    /// <summary>What decodes it, for the log: "Media Foundation", "FFmpeg", "silence".</summary>
    public abstract string Kind { get; }

    /// <summary>The next <see cref="Read"/> starts at this media moment, to the sample (silence before the file's start).</summary>
    public abstract void Seek(double mediaSeconds);

    /// <summary>Silence past the end, so it always fills; fewer only while a pipe has not caught up.</summary>
    public abstract int Read(Span<float> left, Span<float> right);

    public abstract void Dispose();

    /// <summary>
    /// A decoder for a source: Media Foundation, else FFmpeg, else silence (logged once per source). Opened on a feeder's
    /// thread, never the mixer's or the interface's.
    /// </summary>
    public static AudioDecoder Open(AudioSource source, int deviceRate)
    {
        // An MP4 or MOV with more than one sound track: Media Foundation's reader lists them in an order of its own (two or
        // three AAC tracks FFmpeg wrote came out reversed, and not in its own presentation descriptor's order either), so
        // its n-th is not FFmpeg's "0:a:N", which the export mixes. FFmpeg reads those (5.0-Verify, measured); Matroska,
        // OBS's recordings with four tracks, comes in the file's order and stays Media Foundation's.
        if (Mp4Timing.Read(source.Path) is { } tracks && tracks.Count(t => t.Handler == "soun") > 1 && FfmpegPipeDecoder.Available)
        {
            Note(source, "an MP4 with several sound tracks, whose order only FFmpeg keeps: FFmpeg decodes it");
            return new FfmpegPipeDecoder(source, deviceRate);
        }

        try
        {
            return new MfAudioDecoder(source);
        }
        catch (Exception ex) when (ex is GpuException or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            if (FfmpegPipeDecoder.Available)
            {
                Note(source, $"Media Foundation could not read it ({ex.Message}); FFmpeg decodes it");
                return new FfmpegPipeDecoder(source, deviceRate);
            }

            Note(source, $"it could not be decoded ({ex.Message}) and FFmpeg is not installed: it is silent");
            return new SilentDecoder(source, deviceRate);
        }
    }

    private static readonly ConcurrentDictionary<AudioSource, byte> Noted = new();

    protected static void Note(AudioSource source, string what)
    {
        if (Noted.TryAdd(source, 0))
            AppLog.Write($"Sound engine: {source}: {what}");
    }
}

/// <summary>A source nothing could decode: silence, at the device's rate.</summary>
internal sealed class SilentDecoder(AudioSource source, int rate) : AudioDecoder(source)
{
    public override int SampleRate => rate;
    public override double Duration => 0;
    public override string Kind => "silence";

    public override void Seek(double mediaSeconds)
    {
    }

    public override int Read(Span<float> left, Span<float> right)
    {
        left.Clear();
        right.Clear();
        return left.Length;
    }

    public override void Dispose()
    {
    }
}

/// <summary>
/// The Source Reader on one audio stream, float output. Where its media time 0 is: <see cref="AudioSource"/>'s video start
/// when FFmpeg found one; else the MP4's edit lists (Mp4Timing); else the first video sample's time as the reader gives it.
/// Its sound's samples are placed by the same rule FFmpeg's demuxer follows (Mp4Timing), so the priming an AAC encoder
/// put in front is where FFmpeg drops it, whether or not Media Foundation drops it too.
/// </summary>
internal sealed unsafe class MfAudioDecoder : AudioDecoder
{
    private const double Preroll = 0.1;
    private nint _reader;
    private uint _stream;
    private int _rate, _channels;
    private float[] _matrix = [];
    private double _shift;
    private readonly double _duration;

    // What the last sample decoded holds (stereo, planar) and the media frame of its first sample.
    private float[] _pendingL = new float[8192], _pendingR = new float[8192];
    private int _pendingAt, _pendingCount;
    private long _pendingFrame;

    // The media frame the next Read starts with; whether the next sample's time places it (after a seek); the end.
    private long _next;
    private bool _placeNext = true;
    private bool _ended;
    private long _expected;

    public MfAudioDecoder(AudioSource source) : base(source)
    {
        if (!MfAudio.Start())
            throw new InvalidOperationException("Media Foundation is not available");
        try
        {
            Open();
            _duration = Mf.Duration(_reader) / 1e7;
        }
        catch
        {
            Com.Release(ref _reader);
            throw;
        }
    }

    public override int SampleRate => _rate;
    public override double Duration => _duration;
    public override string Kind => "Media Foundation";

    private void Open()
    {
        nint reader;
        Com.Check(Mf.MFCreateSourceReaderFromURL(Source.Path, 0, &reader), "MFCreateSourceReaderFromURL");
        _reader = reader;

        // Which of the reader's streams is the n-th audio one, and whether there is a picture.
        var (seen, hasVideo) = (-1, false);
        _stream = uint.MaxValue;
        for (uint i = 0; i < 64; i++)
        {
            if (Mf.GetNativeMediaType(_reader, i, 0, out var type) < 0)
                break;
            try
            {
                if (Mf.GetGuid(type, MfGuids.MajorType, out var major) < 0)
                    continue;
                if (major == MfGuids.Video)
                    hasVideo = true;
                else if (major == MfAudio.Audio && ++seen == Source.Stream && _stream == uint.MaxValue)
                    _stream = i;
            }
            finally
            {
                Com.Release(ref type);
            }
        }

        if (_stream == uint.MaxValue)
            throw new InvalidOperationException("the file has no such audio stream");

        // The first video sample's time on the reader's clock (compressed: nothing is decoded), when it is needed: not for
        // an MP4 (its boxes say it) or a transport stream (its time stamps say it, on FFmpeg's clock, which its reader's is
        // not), nor for a Matroska file whose video start is known (its reader's clock is FFmpeg's).
        var videoFirst = double.NaN;
        var mp4 = Mp4Timing.Read(Source.Path);
        var ts = mp4 is null ? TsTiming.Read(Source.Path) : null;
        var matroska = mp4 is null && ts is null && MatroskaTiming.Read(Source.Path) is not null;
        if (hasVideo && mp4 is null && ts is null && (!matroska || double.IsNaN(Source.VideoStart)))
            videoFirst = FirstVideoTime();

        _ = Mf.SetStreamSelection(_reader, Mf.AllStreams, false);
        Com.Check(Mf.SetStreamSelection(_reader, _stream, true), "SetStreamSelection");
        nint output;
        Com.Check(Mf.MFCreateMediaType(&output), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(output, MfGuids.MajorType, MfAudio.Audio);
            Mf.SetGuid(output, MfGuids.Subtype, MfAudio.Float);
            Com.Check(Mf.SetCurrentMediaType(_reader, _stream, output), "SetCurrentMediaType (float)");
        }
        finally
        {
            Com.Release(ref output);
        }

        ReadFormat();

        // The reader's time of its first sound sample: where its count of samples starts.
        _ = Mf.SetCurrentPosition(_reader, 0);
        var audioFirst = FirstAudioTime();

        // FFmpeg's pts of that first decoded sample (P0), and its video start (the origin of media time).
        var tracks = mp4 ?? [];
        var audioTrack = tracks.Where(t => t.Handler == "soun").Skip(Source.Stream).FirstOrDefault();
        var videoTrack = tracks.FirstOrDefault(t => t.Handler == "vide");
        // A transport stream's: its first PES's PTS (TsTiming; Windows' TS source counts a clock of its own from the start
        // of the stream, so taken as FFmpeg's its sound was 2.1 s early, its picture right). A Matroska file's: the reader's
        // own first time, less its track's CodecDelay (an encoder's priming: 1024 samples in every AAC track of an OBS
        // recording), which FFmpeg drops and Windows' Matroska source plays (MatroskaTiming). Any other container's (MPEG-PS,
        // AVI, ASF): the reader's clock tied to FFmpeg's at the first video frame, FFmpeg's video start (Windows' MPEG-PS
        // source keeps a clock of its own too: measured, the sound 39 ms early); without a picture, the reader's as it is.
        var tied = !matroska && !double.IsNaN(videoFirst) && !double.IsNaN(Source.VideoStart) ? Source.VideoStart - videoFirst : 0;
        var firstPts = audioTrack is not null ? audioTrack.EmptyEdit - audioTrack.MediaTime
            : TsTiming.FirstAudioPts(ts, Source.Stream) is { } tsFirst ? tsFirst
            : audioFirst + tied - (matroska ? MatroskaTiming.CodecDelayOfAudio(Source.Path, Source.Stream) : 0);
        var origin = !double.IsNaN(Source.VideoStart) ? Source.VideoStart
            : videoTrack is not null ? videoTrack.FirstPts
            : TsTiming.FirstVideoPts(ts) is { } tsVideo ? tsVideo
            : !double.IsNaN(videoFirst) ? videoFirst
            : 0;
        _shift = audioFirst - firstPts + origin;
        Seek(0);
    }

    private double FirstVideoTime()
    {
        _ = Mf.SetStreamSelection(_reader, Mf.AllStreams, false);
        if (Mf.SetStreamSelection(_reader, Mf.FirstVideoStream, true) < 0)
            return double.NaN;
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (Mf.ReadSample(_reader, Mf.FirstVideoStream, out var flags, out var time, out var sample) < 0)
                    return double.NaN;
                Com.Release(ref sample);
                if ((flags & (Mf.ReaderEndOfStream | Mf.ReaderError)) != 0)
                    return double.NaN;
                if ((flags & Mf.ReaderStreamTick) == 0)
                    return time / 1e7;
            }

            return double.NaN;
        }
        finally
        {
            _ = Mf.SetStreamSelection(_reader, Mf.FirstVideoStream, false);
        }
    }

    private double FirstAudioTime()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (Mf.ReadSample(_reader, _stream, out var flags, out var time, out var sample) < 0)
                break;
            var had = sample != 0;
            Com.Release(ref sample);
            if ((flags & (Mf.ReaderEndOfStream | Mf.ReaderError)) != 0)
                break;
            if ((flags & Mf.ReaderCurrentMediaTypeChanged) != 0)
                ReadFormat();
            if (had)
                return time / 1e7;
        }

        return 0;
    }

    private void ReadFormat()
    {
        Com.Check(Mf.GetCurrentMediaType(_reader, _stream, out var current), "GetCurrentMediaType");
        try
        {
            Com.Check(Mf.GetUInt32(current, MfAudio.SamplesPerSecond, out var rate), "MF_MT_AUDIO_SAMPLES_PER_SECOND");
            Com.Check(Mf.GetUInt32(current, MfAudio.NumChannels, out var channels), "MF_MT_AUDIO_NUM_CHANNELS");
            if (Mf.GetUInt32(current, MfAudio.ChannelMask, out var mask) < 0)
                mask = 0;
            if (rate == 0 || channels == 0 || channels > 32)
                throw new InvalidOperationException("the decoder gave no usable format");
            (_rate, _channels) = ((int)rate, (int)channels);
            _matrix = AudioDownmix.ToStereo(_channels, mask);
        }
        finally
        {
            Com.Release(ref current);
        }
    }

    public override void Seek(double mediaSeconds)
    {
        _next = (long)Math.Round(mediaSeconds * _rate);
        (_pendingAt, _pendingCount, _placeNext, _ended) = (0, 0, true, false);
        _ = Mf.Flush(_reader, _stream);
        var ticks = (long)Math.Round((mediaSeconds - Preroll + _shift) * 1e7);
        if (Mf.SetCurrentPosition(_reader, Math.Max(ticks, 0)) < 0)
            _ = Mf.SetCurrentPosition(_reader, 0);
    }

    public override int Read(Span<float> left, Span<float> right)
    {
        var produced = 0;
        while (produced < left.Length)
        {
            if (_pendingCount > 0)
            {
                // Before where reading is: dropped. After it (a gap in the stream, a landing past the moment): silence first.
                if (_pendingFrame < _next)
                {
                    var drop = (int)Math.Min(_pendingCount, _next - _pendingFrame);
                    (_pendingAt, _pendingCount, _pendingFrame) = (_pendingAt + drop, _pendingCount - drop, _pendingFrame + drop);
                    continue;
                }

                if (_pendingFrame > _next)
                {
                    var gap = (int)Math.Min(left.Length - produced, _pendingFrame - _next);
                    left.Slice(produced, gap).Clear();
                    right.Slice(produced, gap).Clear();
                    (produced, _next) = (produced + gap, _next + gap);
                    continue;
                }

                var take = Math.Min(_pendingCount, left.Length - produced);
                _pendingL.AsSpan(_pendingAt, take).CopyTo(left[produced..]);
                _pendingR.AsSpan(_pendingAt, take).CopyTo(right[produced..]);
                (_pendingAt, _pendingCount, _pendingFrame) = (_pendingAt + take, _pendingCount - take, _pendingFrame + take);
                (produced, _next) = (produced + take, _next + take);
                continue;
            }

            if (_ended || !Decode())
            {
                _ended = true;
                left[produced..].Clear();
                right[produced..].Clear();
                _next += left.Length - produced;
                produced = left.Length;
            }
        }

        return produced;
    }

    // One more sample from the reader into the pending buffers; false at the end (or on an error, logged).
    private bool Decode()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var hr = Mf.ReadSample(_reader, _stream, out var flags, out var timestamp, out var sample);
            if (hr < 0 || (flags & Mf.ReaderError) != 0)
            {
                Com.Release(ref sample);
                Note(Source, string.Create(CultureInfo.InvariantCulture, $"the decoder failed (0x{hr:X8})"));
                return false;
            }

            if ((flags & Mf.ReaderCurrentMediaTypeChanged) != 0)
                ReadFormat();
            if ((flags & Mf.ReaderEndOfStream) != 0)
            {
                Com.Release(ref sample);
                return false;
            }

            if (sample == 0)
                continue;
            try
            {
                if (Unpack(sample, timestamp))
                    return true;
            }
            finally
            {
                Com.Release(ref sample);
            }
        }

        return false;
    }

    private bool Unpack(nint sample, long timestamp)
    {
        if (MfAudio.ContiguousBuffer(sample, out var buffer) < 0)
            return false;
        try
        {
            if (Mf.Lock(buffer, out var data, out var length) < 0)
                return false;
            try
            {
                var frames = (int)(length / (uint)(4 * _channels));
                if (frames == 0)
                    return false;
                if (_pendingL.Length < frames)
                    (_pendingL, _pendingR) = (new float[frames], new float[frames]);
                AudioDownmix.Apply(new ReadOnlySpan<float>(data, frames * _channels), _channels, _matrix, _pendingL, _pendingR, frames);

                // Placed by its time the first time after a seek, and when it strays from the count by more than 2 ms
                // (a gap in the file); otherwise counted on from the one before, which time stamps rounded to 100 ns cannot.
                var placed = (long)Math.Round((timestamp / 1e7 - _shift) * _rate);
                var frame = _placeNext || Math.Abs(placed - _expected) > _rate / 500 ? placed : _expected;
                _placeNext = false;
                (_pendingAt, _pendingCount, _pendingFrame, _expected) = (0, frames, frame, frame + frames);
                return true;
            }
            finally
            {
                _ = Mf.Unlock(buffer);
            }
        }
        finally
        {
            Com.Release(ref buffer);
        }
    }

    public override void Dispose() => Com.Release(ref _reader);
}

/// <summary>
/// FFmpeg decoding a source Media Foundation cannot (Opus or Vorbis in Matroska on a PC without the codec, and the like):
/// "-ss" before "-i" (exact for sound), the stream as f32le stereo at the device's rate on its standard output, read on a
/// task into a buffer the feeder takes from without waiting. A seek starts it again from there.
/// </summary>
internal sealed class FfmpegPipeDecoder : AudioDecoder
{
    private readonly int _rate;
    private readonly double _origin;
    private Run? _run;

    public FfmpegPipeDecoder(AudioSource source, int rate) : base(source)
    {
        _rate = rate;

        // Where the picture starts on FFmpeg's clock: what FFmpeg found, else what an MP4's boxes or a transport stream's
        // time stamps say; NaN when none of them knows (the container's start is taken for it then).
        _origin = !double.IsNaN(source.VideoStart) ? source.VideoStart
            : Mp4Timing.Read(source.Path)?.FirstOrDefault(t => t.Handler == "vide") is { } video ? video.FirstPts
            : TsTiming.FirstVideoPts(TsTiming.Read(source.Path)) ?? double.NaN;
        Seek(0);
    }

    public static bool Available
    {
        get
        {
            try
            {
                return File.Exists(DependencyUpdater.FfmpegPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }

    public override int SampleRate => _rate;
    public override double Duration => 0;
    public override string Kind => "FFmpeg";

    public override void Seek(double mediaSeconds)
    {
        _run?.Stop();

        // Media time is from the first video frame: read from that time stamp itself (-seek_timestamp; "-ss" alone counts
        // from the container's start, which is the sound's own when it starts before the picture, 0.5 s out in an M2TS).
        var absolute = !double.IsNaN(_origin);
        var from = mediaSeconds + (absolute ? _origin : 0);
        var silence = from < 0 ? (long)Math.Round(-from * _rate) : 0;
        _run = new Run(Source, Math.Max(from, 0), absolute, _rate, silence);
    }

    public override int Read(Span<float> left, Span<float> right) => _run?.Take(left, right) ?? 0;

    public override void Dispose() => _run?.Stop();

    /// <summary>One run of FFmpeg from one moment, and the frames it has written that were not taken yet.</summary>
    private sealed class Run
    {
        private const int Capacity = 1 << 17;
        private readonly float[] _frames = new float[Capacity * 2];
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _room = new(0);
        private long _written, _taken;
        private long _silence;
        private volatile bool _done;

        public Run(AudioSource source, double from, bool absolute, int rate, long silence)
        {
            _silence = silence;

            // Begun exactly at the moment, as the export's BuildLayerAudio begins at its cut (aresample's first_pts): a sound
            // that starts after it is preceded by silence, which the raw output alone leaves out.
            var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
            if (absolute)
                arguments.AddRange(["-seek_timestamp", "1"]);
            arguments.AddRange(
            [
                "-ss", from.ToString("0.######", CultureInfo.InvariantCulture), "-i", source.Path,
                "-map", string.Create(CultureInfo.InvariantCulture, $"0:a:{source.Stream}"), "-vn", "-sn", "-dn",
                "-af", string.Create(CultureInfo.InvariantCulture, $"aresample={rate}:async=1:first_pts=0"), "-ac", "2", "-f", "f32le", "pipe:1",
            ]);
            var command = Cli.Wrap(DependencyUpdater.FfmpegPath).WithArguments(arguments)
                .WithStandardOutputPipe(PipeTarget.Create(ReadAsync))
                .WithValidation(CommandResultValidation.None);
            var token = _stop.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await WorkScheduler.Run(new WorkSpec($"Sound of {Path.GetFileName(source.Path)}", WorkLane.Foreground) { Visible = false, Urgent = true, Cancellable = false },
                        work => ProcessPipes.RunAsync(command, work.Token), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppLog.Write("Sound engine: FFmpeg's decode of a source failed", ex);
                }
                finally
                {
                    _done = true;
                }
            }, CancellationToken.None);
        }

        private async Task ReadAsync(Stream stream, CancellationToken cancellation)
        {
            var bytes = new byte[1 << 16];
            var carry = 0;
            while (!_stop.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(carry), cancellation).ConfigureAwait(false);
                if (read == 0)
                    break;
                var total = carry + read;
                var whole = total / 8 * 8;
                for (var at = 0; at < whole; at += 8)
                {
                    while (Volatile.Read(ref _written) - Volatile.Read(ref _taken) >= Capacity)
                    {
                        if (!await _room.WaitAsync(250, cancellation).ConfigureAwait(false) && _stop.IsCancellationRequested)
                            return;
                    }

                    var slot = (int)(_written % Capacity) * 2;
                    _frames[slot] = BitConverter.ToSingle(bytes, at);
                    _frames[slot + 1] = BitConverter.ToSingle(bytes, at + 4);
                    Volatile.Write(ref _written, _written + 1);
                }

                carry = total - whole;
                if (carry > 0)
                    Array.Copy(bytes, whole, bytes, 0, carry);
            }
        }

        public int Take(Span<float> left, Span<float> right)
        {
            var produced = 0;
            if (_silence > 0)
            {
                var gap = (int)Math.Min(_silence, left.Length);
                left[..gap].Clear();
                right[..gap].Clear();
                (_silence, produced) = (_silence - gap, gap);
            }

            var available = Volatile.Read(ref _written) - _taken;
            var take = (int)Math.Min(available, left.Length - produced);
            for (var i = 0; i < take; i++)
            {
                var slot = (int)((_taken + i) % Capacity) * 2;
                left[produced + i] = _frames[slot];
                right[produced + i] = _frames[slot + 1];
            }

            Volatile.Write(ref _taken, _taken + take);
            produced += take;
            if (take > 0)
                _room.Release();

            // Past the end (FFmpeg finished and everything was taken): silence.
            if (produced < left.Length && _done && Volatile.Read(ref _written) == _taken)
            {
                left[produced..].Clear();
                right[produced..].Clear();
                produced = left.Length;
            }

            return produced;
        }

        public void Stop()
        {
            _stop.Cancel();
            _room.Release();
        }
    }
}

/// <summary>Made stereo as swresample's default matrix makes it for float (FFmpeg's aformat=channel_layouts=stereo).</summary>
internal static class AudioDownmix
{
    // WAVEFORMATEXTENSIBLE's speaker bits, in the order the channels are interleaved.
    private const uint FrontLeft = 0x1, FrontRight = 0x2, FrontCenter = 0x4, LowFrequency = 0x8, BackLeft = 0x10, BackRight = 0x20,
        FrontLeftOfCenter = 0x40, FrontRightOfCenter = 0x80, BackCenter = 0x100, SideLeft = 0x200, SideRight = 0x400;

    private const float Root = 0.70710678f;

    /// <summary>The weights (left, right) of each channel.</summary>
    public static float[] ToStereo(int channels, uint mask)
    {
        var matrix = new float[channels * 2];
        if (channels == 1)
        {
            (matrix[0], matrix[1]) = (Root, Root);
            return matrix;
        }

        if (mask == 0 || CountBits(mask) != channels)
            mask = channels switch
            {
                2 => FrontLeft | FrontRight,
                3 => FrontLeft | FrontRight | FrontCenter,
                4 => FrontLeft | FrontRight | BackLeft | BackRight,
                5 => FrontLeft | FrontRight | FrontCenter | BackLeft | BackRight,
                6 => FrontLeft | FrontRight | FrontCenter | LowFrequency | BackLeft | BackRight,
                8 => FrontLeft | FrontRight | FrontCenter | LowFrequency | BackLeft | BackRight | SideLeft | SideRight,
                _ => 0,
            };

        var channel = 0;
        for (var bit = 0; bit < 32 && channel < channels; bit++)
        {
            var speaker = 1u << bit;
            if (mask != 0 && (mask & speaker) == 0)
                continue;
            var (l, r) = speaker switch
            {
                _ when mask == 0 => channel == 0 ? (1f, 0f) : channel == 1 ? (0f, 1f) : (0f, 0f),
                FrontLeft or FrontLeftOfCenter => (1f, 0f),
                FrontRight or FrontRightOfCenter => (0f, 1f),
                FrontCenter => (Root, Root),
                BackLeft or SideLeft => (Root, 0f),
                BackRight or SideRight => (0f, Root),
                BackCenter => (Root * Root, Root * Root),
                _ => (0f, 0f),
            };
            (matrix[2 * channel], matrix[2 * channel + 1]) = (l, r);
            channel++;
        }

        return matrix;
    }

    private static int CountBits(uint mask) => System.Numerics.BitOperations.PopCount(mask);

    public static void Apply(ReadOnlySpan<float> interleaved, int channels, float[] matrix, Span<float> left, Span<float> right, int frames)
    {
        if (channels == 2 && matrix[0] == 1 && matrix[1] == 0 && matrix[2] == 0 && matrix[3] == 1)
        {
            for (var i = 0; i < frames; i++)
                (left[i], right[i]) = (interleaved[2 * i], interleaved[2 * i + 1]);
            return;
        }

        for (var i = 0; i < frames; i++)
        {
            var (l, r) = (0f, 0f);
            var frame = interleaved.Slice(i * channels, channels);
            for (var c = 0; c < channels; c++)
                (l, r) = (l + frame[c] * matrix[2 * c], r + frame[c] * matrix[2 * c + 1]);
            (left[i], right[i]) = (l, r);
        }
    }
}

/// <summary>
/// Decoders not in use, by source, so that the next clip of the same file (the main video cut into many) seeks one that is
/// open instead of opening the file again. The least recently returned go first past the limit.
/// </summary>
internal sealed class AudioDecoderPool : IDisposable
{
    private const int Limit = 16;
    private readonly List<AudioDecoder> _idle = [];
    private readonly int _deviceRate;
    private bool _disposed;

    public AudioDecoderPool(int deviceRate) => _deviceRate = deviceRate;

    public AudioDecoder Rent(AudioSource source)
    {
        lock (_idle)
        {
            var at = _idle.FindLastIndex(d => d.Source == source && (d is not FfmpegPipeDecoder || d.SampleRate == _deviceRate));
            if (at >= 0)
            {
                var decoder = _idle[at];
                _idle.RemoveAt(at);
                return decoder;
            }
        }

        return AudioDecoder.Open(source, _deviceRate);
    }

    public void Return(AudioDecoder decoder)
    {
        AudioDecoder? drop = null;
        lock (_idle)
        {
            if (_disposed)
            {
                drop = decoder;
            }
            else
            {
                _idle.Add(decoder);
                if (_idle.Count > Limit)
                {
                    drop = _idle[0];
                    _idle.RemoveAt(0);
                }
            }
        }

        drop?.Dispose();
    }

    public void Dispose()
    {
        List<AudioDecoder> all;
        lock (_idle)
        {
            _disposed = true;
            all = [.. _idle];
            _idle.Clear();
        }

        foreach (var decoder in all)
            decoder.Dispose();
    }
}
