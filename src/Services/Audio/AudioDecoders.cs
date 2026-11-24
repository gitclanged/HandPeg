using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using CliWrap;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services.Audio;

// ----- Audio decoding -----
//
// MF Source Reader decodes each (file, audio stream) to float on feeder threads at the file's native rate, downmixed to stereo as
// FFmpeg's aformat would (swresample matrix: mono at -3 dB on both sides, centre and surrounds at -3 dB, LFE dropped, unnormalized
// for float). Seeks are sample-exact: the reader seeks 0.1 s early (an AAC frame needs its predecessor to decode cleanly) and earlier
// samples are dropped by timestamp. Sources MF cannot read fall back to an FFmpeg pipe (f32le stereo at device rate), run through
// WorkScheduler and ProcessPipes like every external tool.

/// <summary>One decoded audio stream of one file: stereo at <see cref="SampleRate"/>, positioned by media time.</summary>
internal abstract class AudioDecoder : IStereoSource, IDisposable
{
    protected AudioDecoder(AudioSource source) => Source = source;

    // Live request being served (VoiceProducer, feeder thread): Read may stop early, with nothing produced, once the voice's request
    // serial has moved past it (a newer seek), instead of decoding on through a long-GOP post-seek skip. Null offline (exact renders).
    private VoiceState? _abortOwner;
    private long _abortSerial;

    /// <summary>Lets <see cref="Read"/> abandon its post-seek skip once <paramref name="owner"/>'s request serial is no longer <paramref name="serial"/>.</summary>
    internal void AbortWhenSuperseded(VoiceState? owner, long serial) => (_abortOwner, _abortSerial) = (owner, serial);

    /// <summary>True when a newer request superseded the one being read (see <see cref="AbortWhenSuperseded"/>).</summary>
    protected bool Superseded => _abortOwner is { } owner && Superseding(owner.RequestSerial, _abortSerial);

    /// <summary>True when the voice's current request serial (<paramref name="current"/>) supersedes the one being read. Pure.</summary>
    internal static bool Superseding(long current, long reading) => reading != 0 && current != reading;

    public AudioSource Source { get; }

    public abstract int SampleRate { get; }

    /// <summary>Stream audio length in media seconds (0 when unknown).</summary>
    public abstract double Duration { get; }

    /// <summary>Decoder backend for logging: "Media Foundation", "FFmpeg", "silence".</summary>
    public abstract string Kind { get; }

    /// <summary>Positions the next <see cref="Read"/> at this media time, sample-exact (silence before the file start).</summary>
    public abstract void Seek(double mediaSeconds);

    /// <summary>Fills both spans, with silence past the end; returns fewer frames only while a pipe has not caught up.</summary>
    public abstract int Read(Span<float> left, Span<float> right);

    public abstract void Dispose();

    /// <summary>
    /// Opens a decoder for a source: Media Foundation, else FFmpeg, else silence (logged once per source). Called on a feeder thread,
    /// never the mixer or UI thread.
    /// </summary>
    public static AudioDecoder Open(AudioSource source, int deviceRate)
    {
        // MP4/MOV with several audio tracks: MF's reader orders them its own way (FFmpeg-written AAC tracks came out reversed, not even in
        // its presentation descriptor's order), so its n-th is not FFmpeg's "0:a:N" used by export; use FFmpeg for those. Matroska
        // (e.g. four-track OBS recordings) keeps file order and stays on MF.
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

/// <summary>Fallback for undecodable sources: silence at the device rate.</summary>
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
/// MF Source Reader on one audio stream, float output. Media time 0 is <see cref="AudioSource"/>'s video start when FFmpeg found one; else
/// the MP4 edit lists (Mp4Timing); else the reader's first video sample time. Samples are placed by FFmpeg's demuxer rules (Mp4Timing),
/// so AAC encoder priming is dropped where FFmpeg drops it, whether or not MF does.
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

    // Last decoded sample's audio (planar stereo) and the media frame of its first sample.
    private float[] _pendingL = new float[8192], _pendingR = new float[8192];
    private int _pendingAt, _pendingCount;
    private long _pendingFrame;

    // Media frame of the next Read; whether the next sample's timestamp repositions (after a seek); end of stream.
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

        // Find the reader stream index of the n-th audio stream, and whether video exists.
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

        // First video sample time on the reader's clock (compressed, nothing decoded), only when needed: not for MP4 (boxes give it),
        // transport streams (timestamps give it on FFmpeg's clock, which the reader's is not), or Matroska with a known video start
        // (its reader clock matches FFmpeg's).
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

        // Reader time of the first audio sample: origin of the sample count.
        _ = Mf.SetCurrentPosition(_reader, 0);
        var audioFirst = FirstAudioTime();

        // FFmpeg's pts of that first decoded sample (P0), and its video start (media time origin).
        var tracks = mp4 ?? [];
        var audioTrack = tracks.Where(t => t.Handler == "soun").Skip(Source.Stream).FirstOrDefault();
        var videoTrack = tracks.FirstOrDefault(t => t.Handler == "vide");
        // Transport stream: first PES PTS (TsTiming; Windows' TS source uses its own clock from stream start, which put audio 2.1 s
        // early). Matroska: the reader's first time minus the track's CodecDelay (encoder priming, e.g. 1024 samples per OBS AAC track),
        // which FFmpeg drops but Windows' Matroska source plays (MatroskaTiming). Other containers (MPEG-PS, AVI, ASF): reader clock tied
        // to FFmpeg's at the first video frame (Windows' MPEG-PS source also has its own clock: audio measured 39 ms early); audio-only
        // files use the reader clock as-is.
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
            _matrix = Source.ChannelPair >= 0 ? AudioDownmix.Pair(_channels, Source.ChannelPair) : AudioDownmix.ToStereo(_channels, mask);
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
                // Before the read position: drop. After it (stream gap, or seek landed late): emit silence first.
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

            // Nothing produced yet (still skipping to the seek point) and a newer seek superseded this one: stop before the next ReadSample.
            if (produced == 0 && !_ended && Superseded)
                return 0;

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

    // Reads one sample into the pending buffers; false at end of stream (or on a logged error).
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

                // Position by timestamp after a seek or when it drifts from the running count by over 2 ms (a file gap); otherwise
                // continue the count, which 100 ns-rounded timestamps cannot do exactly.
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
/// FFmpeg fallback for sources MF cannot decode (e.g. Opus or Vorbis in Matroska without the codec): input seek "-ss" before "-i" (exact
/// for audio), f32le stereo at device rate on stdout, read by a task into a buffer the feeder drains without waiting. A seek restarts it.
/// </summary>
internal sealed class FfmpegPipeDecoder : AudioDecoder
{
    private readonly int _rate;
    private readonly double _origin;
    private Run? _run;

    public FfmpegPipeDecoder(AudioSource source, int rate) : base(source)
    {
        _rate = rate;

        // Video start on FFmpeg's clock: FFmpeg's value, else MP4 boxes or transport stream timestamps; NaN when unknown (container
        // start is used then).
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

        // Media time counts from the first video frame: seek by absolute timestamp (-seek_timestamp; "-ss" alone counts from container
        // start, which is the audio's start when audio precedes video, 0.5 s off in an M2TS).
        var absolute = !double.IsNaN(_origin);
        var from = mediaSeconds + (absolute ? _origin : 0);
        var silence = from < 0 ? (long)Math.Round(-from * _rate) : 0;
        _run = new Run(Source, Math.Max(from, 0), absolute, _rate, silence);
    }

    public override int Read(Span<float> left, Span<float> right) => _run?.Take(left, right) ?? 0;

    public override void Dispose() => _run?.Stop();

    /// <summary>One FFmpeg run from one position, with its written-but-unconsumed frames.</summary>
    private sealed class Run
    {
        private const int Capacity = 1 << 17;
        private readonly float[] _frames = new float[Capacity * 2];
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _room = new(0);
        private long _written, _taken;
        private long _silence;
        private volatile bool _done;

        // Channels per output frame and which are left/right: a surround pair read as-is, else FFmpeg's stereo downmix (-ac 2).
        private readonly int _channels = 2, _left, _right = 1;

        public Run(AudioSource source, double from, bool absolute, int rate, long silence)
        {
            _silence = silence;
            var native = source.ChannelPair >= 0 && MediaProbe.KnownInfo(source.Path)?.Audio is { } streams && source.Stream < streams.Count ? streams[source.Stream].Channels : 0;
            if (native > 2)
            {
                _channels = native;
                (_left, _right) = (Math.Min(2 * source.ChannelPair, native - 1), Math.Min(2 * source.ChannelPair + 1, native - 1));
            }

            // Start exactly at the requested time (aresample first_pts): audio starting later is preceded by silence, which raw
            // output alone would omit.
            var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
            if (absolute)
                arguments.AddRange(["-seek_timestamp", "1"]);
            arguments.AddRange(
            [
                "-ss", from.ToString("0.######", CultureInfo.InvariantCulture), "-i", source.Path,
                "-map", string.Create(CultureInfo.InvariantCulture, $"0:a:{source.Stream}"), "-vn", "-sn", "-dn",
                "-af", string.Create(CultureInfo.InvariantCulture, $"aresample={rate}:async=1:first_pts=0"),
            ]);
            if (_channels == 2)
                arguments.AddRange(["-ac", "2"]);
            arguments.AddRange(["-f", "f32le", "pipe:1"]);
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
                var frameBytes = 4 * _channels;
                var whole = total / frameBytes * frameBytes;
                for (var at = 0; at < whole; at += frameBytes)
                {
                    while (Volatile.Read(ref _written) - Volatile.Read(ref _taken) >= Capacity)
                    {
                        if (!await _room.WaitAsync(250, cancellation).ConfigureAwait(false) && _stop.IsCancellationRequested)
                            return;
                    }

                    var slot = (int)(_written % Capacity) * 2;
                    _frames[slot] = BitConverter.ToSingle(bytes, at + 4 * _left);
                    _frames[slot + 1] = BitConverter.ToSingle(bytes, at + 4 * _right);
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

            // Past the end (FFmpeg finished and all output consumed): silence.
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

/// <summary>Stereo downmix matching swresample's default float matrix (FFmpeg aformat=channel_layouts=stereo).</summary>
internal static class AudioDownmix
{
    // WAVEFORMATEXTENSIBLE speaker bits, in channel interleave order.
    private const uint FrontLeft = 0x1, FrontRight = 0x2, FrontCenter = 0x4, LowFrequency = 0x8, BackLeft = 0x10, BackRight = 0x20,
        FrontLeftOfCenter = 0x40, FrontRightOfCenter = 0x80, BackCenter = 0x100, SideLeft = 0x200, SideRight = 0x400;

    private const float Root = 0.70710678f;

    /// <summary>Per-channel (left, right) weights.</summary>
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

    /// <summary>
    /// Weights selecting one channel pair as-is (surround exported unmixed): channels 2p and 2p + 1 as left and right; a lone last channel
    /// on both sides; a mono stream's pair 0 is the mono signal on both.
    /// </summary>
    public static float[] Pair(int channels, int pair)
    {
        if (channels <= 2)
            return pair == 0 ? ToStereo(channels, 0) : new float[channels * 2];

        var matrix = new float[channels * 2];
        var (left, right) = (2 * pair, 2 * pair + 1);
        if (left < channels)
            matrix[2 * left] = 1;
        if (right < channels)
            matrix[2 * right + 1] = 1;
        else if (left < channels)
            matrix[2 * left + 1] = 1;
        return matrix;
    }

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
/// Idle decoders by source, so the next clip of the same file (a recording cut into many clips) reuses an open decoder instead of
/// reopening. Least recently returned are evicted first past the limit.
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

    /// <summary>
    /// Disposes all idle decoders while keeping the pool open (tab deactivated: its files are closed, the next project's open on demand).
    /// Decoders still held by a voice return later as usual.
    /// </summary>
    public void ReleaseIdle()
    {
        List<AudioDecoder> idle;
        lock (_idle)
        {
            idle = [.. _idle];
            _idle.Clear();
        }

        foreach (var decoder in idle)
            decoder.Dispose();
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
