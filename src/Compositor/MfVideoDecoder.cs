using HandPegApp.Compositor.Native;

namespace HandPegApp.Compositor;

/// <summary>
/// One video stream of one file, decoded by the Media Foundation Source Reader.
/// <para>Hardware: the reader is bound to the compositor's D3D11 device (<see cref="MediaFoundation.DeviceManager"/>), so the GPU decoder
/// writes NV12 (or P010) frames into VRAM; the shown frame is copied GPU-to-GPU into this decoder's texture and the reader's sample is
/// released immediately (holding it would starve the decoder's surface pool). The CPU never touches pixels.</para>
/// <para>Software (no hardware decoder, WARP, or requested): the same reader on the CPU, frames uploaded into luma and chroma textures.</para>
/// <para>Seeking: a frame within reach ahead is decoded forward to; otherwise seek (to the key frame at or before it, as MF seeks) and
/// decode forward. "Within reach" uses I-frame times: the client's seek index when given, else key frames learned from seek landings (a
/// seek always lands on one). MFSampleExtension_CleanPoint is useless here: every decoded picture is marked clean.</para>
/// <para>Two modes. Held (benchmark): <see cref="ShowAt"/> keeps one frame in the decoder's texture. Cached (compositor, Instant Seek):
/// each kept frame goes into the <see cref="FrameCache"/> and a decode lane steps the decoder one frame at a time (<see cref="Prepare"/>,
/// <see cref="Step"/>); the decoder belongs to that lane's thread while in use.</para>
/// <para>FFmpeg fallback: streams MF cannot open on GPU or CPU (HEVC without the Store HEVC Video Extensions, missing codecs or
/// containers, Windows without MF) are decoded by <see cref="FfmpegVideoDecoder"/>: pictures arrive over a pipe with their own timestamps
/// and are uploaded like CPU frames. Held/cached modes, key frames, seek landings and frame durations are shared; only reading, seeking and
/// uploading differ.</para>
/// <para>Preferred FFmpeg: exports on ARM64 decode HEVC with FFmpeg even when MF could (Snapdragon X Elite: MF HEVC export 229-254 fps vs.
/// FFmpeg 277-406; game style 72-107 vs. 154-164). MF is used when FFmpeg is unavailable.</para>
/// </summary>
internal sealed unsafe class MfVideoDecoder : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly FrameCache? _cache;
    private readonly int _source;
    private nint _reader;
    private FfmpegVideoDecoder? _ffmpeg;
    private readonly List<double> _keyFrames = [];
    private double[]? _iFrames;

    // Held frame: time (file seconds from the first frame) and end time.
    private double _heldTime = double.NaN;
    private double _heldUntil = double.NaN;

    // Reader position: time of the last sample read (the next read follows it).
    private double _readTime = double.NaN;
    private bool _endOfStream;

    // First sample's time on the reader clock: file time 0.
    private long _origin;

    // Set after a seek: the next sample is the landing key frame.
    private bool _landing = true;

    private GpuTexture? _frame;
    private nint _lumaSrv, _chromaSrv;
    private GpuTexture? _softLuma, _softChroma;

    public string Path { get; }
    public int StreamIndex { get; }
    public bool Hardware { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>NV12 (8-bit) or P010 (10-bit).</summary>
    public bool TenBit { get; private set; }

    public double FrameDuration { get; private set; } = 1 / 30.0;
    public double DurationSeconds { get; private set; }

    /// <summary>YUV matrix: 1 BT.709, 2 BT.601, 4 BT.2020 (MFVideoTransferMatrix), and full range or not.</summary>
    public int Matrix { get; private set; } = 1;

    /// <summary>Transfer function (MFVideoTransferFunction): 15 SMPTE ST 2084 (PQ), 16 HLG, others SDR; 0 when unspecified.</summary>
    public int Transfer { get; private set; }
    public bool FullRange { get; private set; }

    /// <summary>Luma and chroma views of the held frame (0 before the first frame).</summary>
    public nint LumaSrv => _lumaSrv;
    public nint ChromaSrv => _chromaSrv;

    /// <summary>Held frame time in seconds; NaN before the first.</summary>
    public double HeldTime => _heldTime;

    /// <summary>Frame counter at last use (pool LRU order).</summary>
    public long LastUsed { get; set; }

    /// <summary>Owning decode lane (cached mode), -1 when none; accessed under the pool lock.</summary>
    public int Owner { get; set; } = -1;

    public int Seeks { get; private set; }
    public int FramesDecoded { get; private set; }

    /// <summary>NV12 texture holding the frame (hardware), for consumers needing the raw picture (benchmark).</summary>
    public nint FrameTexture => _frame?.Texture ?? 0;

    /// <summary>FFmpeg codec name when FFmpeg decodes this stream; null when MF does.</summary>
    public string? FfmpegCodec => _ffmpeg?.Codec;

    public MfVideoDecoder(GpuDevice gpu, string path, int streamIndex, bool hardware, FrameCache? cache = null, int source = 0, bool preferFfmpeg = false)
    {
        _gpu = gpu;
        _hardwareWanted = hardware;
        _cache = cache;
        _source = source;
        Path = path;
        StreamIndex = streamIndex;
        try
        {
            try
            {
                try
                {
                    Open(hardware && MediaFoundation.DeviceManagerFor(gpu) != 0);
                }
                catch (GpuException) when (hardware)
                {
                    // GPU cannot decode it (unsupported codec or profile): fall back to CPU.
                    Close();
                    Open(false);
                }

                if (preferFfmpeg && _nativeSubtype == MfGuids.Hevc)
                    PreferFfmpeg(hardware);
            }
            catch (Exception ex) when (ex is GpuException { IsDeviceLost: false } or DllNotFoundException or EntryPointNotFoundException)
            {
                // MF cannot decode it at all (no HEVC Video Extensions, missing codec or container, no MF): FFmpeg decodes into the
                // same planes.
                Close();
                OpenWithFfmpeg(ex);
            }
        }
        catch
        {
            // Nothing half-open survives: file, reader, FFmpeg and decoder surfaces are released on failure.
            Close();
            throw;
        }
    }

    /// <summary>
    /// Switches an MF-opened HEVC stream to FFmpeg (ARM64 exports, see class summary); reverts to MF when FFmpeg is missing or fails.
    /// </summary>
    private void PreferFfmpeg(bool hardware)
    {
        if (FfmpegVideoDecoder.Missing is not null)
            return;

        Close();
        try
        {
            OpenWithFfmpeg(new GpuException(unchecked((int)0xC00D5212), "FFmpeg preferred for HEVC"));
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            // Fall back to MF, reopened from the file start.
            Close();
            _keyFrames.Clear();
            (_heldTime, _heldUntil, _readTime, _runTime, _lastKept) = (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
            (_endOfStream, _landing) = (false, true);
            Open(hardware && MediaFoundation.DeviceManagerFor(_gpu) != 0);
        }
    }

    // Native stream subtype (MF_MT_SUBTYPE: H.264, HEVC...).
    private Guid _nativeSubtype;

    // Hardware decode requested (not WARP, not disabled in Settings); also enables FFmpeg D3D11VA on discrete GPUs.
    private readonly bool _hardwareWanted;

    /// <summary>
    /// True when FFmpeg should decode export HEVC (see class summary): on ARM64 unless HANDPEG_EXPORT_HEVC_DECODER=mf selects MF (a
    /// measurement switch for alternating golden runs).
    /// </summary>
    public static bool PrefersFfmpegForHevcExports { get; } =
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
        && !string.Equals(Environment.GetEnvironmentVariable("HANDPEG_EXPORT_HEVC_DECODER"), "mf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Invoked on every sample read and around file opens: the worker heartbeat during decode.</summary>
    internal static Action? Beat;

    /// <summary>Sets the client's I-frame times (file seconds) when available.</summary>
    public void SetIFrames(double[]? times) => _iFrames = times is { Length: > 0 } ? times : null;

    private uint _stream = Mf.FirstVideoStream;

    private uint Stream => _stream;

    /// <summary>Resolves the MF stream index of the n-th video stream (<see cref="StreamIndex"/> counts video streams only).</summary>
    private void ChooseStream()
    {
        _stream = Mf.FirstVideoStream;
        if (StreamIndex <= 0)
            return;
        var seen = -1;
        for (uint i = 0; i < 64; i++)
        {
            if (Mf.GetNativeMediaType(_reader, i, 0, out var type) < 0)
                break;
            try
            {
                if (Mf.GetGuid(type, MfGuids.MajorType, out var major) >= 0 && major == MfGuids.Video && ++seen == StreamIndex)
                {
                    _stream = i;
                    return;
                }
            }
            finally
            {
                Com.Release(ref type);
            }
        }

        throw new GpuException(unchecked((int)0xC00D36B3), "The file has no such video stream");
    }

    private void Open(bool hardware)
    {
        MediaFoundation.Start();
        nint attributes;
        Com.Check(Mf.MFCreateAttributes(&attributes, 4), "MFCreateAttributes");
        try
        {
            if (hardware)
            {
                Mf.SetUnknown(attributes, MfGuids.SourceReaderD3DManager, MediaFoundation.DeviceManagerFor(_gpu));
                Mf.SetUInt32(attributes, MfGuids.EnableHardwareTransforms, 1);
            }
            else
            {
                Mf.SetUInt32(attributes, MfGuids.DisableDxva, 1);
                Mf.SetUInt32(attributes, MfGuids.EnableHardwareTransforms, 0);
            }

            nint reader;
            Beat?.Invoke();
            Com.Check(Mf.MFCreateSourceReaderFromURL(Path, attributes, &reader), "MFCreateSourceReaderFromURL");
            Beat?.Invoke();
            _reader = reader;
        }
        finally
        {
            Com.Release(ref attributes);
        }

        Hardware = hardware;
        ChooseStream();
        _ = Mf.SetStreamSelection(_reader, Mf.AllStreams, false);
        Com.Check(Mf.SetStreamSelection(_reader, Stream, true), "SetStreamSelection");

        // Stream properties: size, rate, colour; 10-bit sources output P010.
        Com.Check(Mf.GetNativeMediaType(_reader, Stream, 0, out var native), "GetNativeMediaType");
        try
        {
            if (Mf.GetUInt64(native, MfGuids.FrameRate, out var rate) >= 0 && (rate & 0xFFFFFFFF) != 0 && (rate >> 32) != 0)
                (FrameDuration, _rateFromStream) = ((double)(rate & 0xFFFFFFFF) / (rate >> 32), true);
            if (Mf.GetUInt32(native, MfGuids.YuvMatrix, out var matrix) >= 0)
                Matrix = (int)matrix;
            if (Mf.GetUInt32(native, MfGuids.NominalRange, out var range) >= 0)
                FullRange = range == 1;
            if (Mf.GetUInt32(native, MfGuids.TransferFunction, out var transfer) >= 0)
                Transfer = (int)transfer;
            _ = Mf.GetGuid(native, MfGuids.Subtype, out var subtype);
            _nativeSubtype = subtype;
            // Compressed streams do not report bit depth: request NV12 first, P010 if the decoder refuses.
            TenBit = subtype == MfGuids.P010;
        }
        finally
        {
            Com.Release(ref native);
        }

        if (!SetOutput(TenBit ? MfGuids.P010 : MfGuids.Nv12) && !SetOutput(TenBit ? MfGuids.Nv12 : MfGuids.P010))
            throw new GpuException(unchecked((int)0xC00D5212), "No NV12 or P010 decoder output");

        DurationSeconds = Mf.Duration(_reader) / 1e7;
        ReadOutputSize();

        // First frame: its timestamp defines file time 0 (ReadFrame sets the origin before keeping it, so a cached first frame is at 0)
        // and it is available to show immediately.
        _originKnown = false;
        if (!ReadFrame(double.NegativeInfinity, out _, out _))
            throw new GpuException(unchecked((int)0xC00D36B4), "The video has no frame");
        _readTime = _heldTime;
    }

    /// <summary>
    /// Opens the stream with FFmpeg after MF failed with <paramref name="why"/>: size, depth and colour from ffprobe, first frame read (file
    /// time 0, as in <see cref="Open"/>). Throws with both reasons (nothing left open) when FFmpeg is missing or also fails.
    /// </summary>
    private void OpenWithFfmpeg(Exception why)
    {
        var code = why is GpuException mf ? mf.Code : unchecked((int)0xC00D5212);
        try
        {
            // Discrete GPUs decode via D3D11VA (a GTX 1070 without HEVC extensions: CPU decode on its 6-core host made 101 of 600
            // frames late). Unified-memory GPUs decode on the CPU: pictures go through the pipe either way and each seek reopens the GPU
            // decoder (Adreno HEVC 4K60: 145-207 fps GPU vs. 243 fps CPU; first picture after seek 300-715 ms vs. 210 ms).
            var card = _hardwareWanted && !_gpu.IsWarp && !_gpu.UnifiedMemory && _gpu.AdapterIndex >= 0 ? _gpu.AdapterIndex : -1;
            var ffmpeg = _ffmpeg = FfmpegVideoDecoder.Open(Path, StreamIndex, card);
            Hardware = false;
            (Width, Height, TenBit) = (ffmpeg.Width, ffmpeg.Height, ffmpeg.TenBit);
            (Matrix, Transfer, FullRange) = (ffmpeg.Matrix, ffmpeg.Transfer, ffmpeg.FullRange);
            (FrameDuration, _rateFromStream, _seeded, _spacingCount) = (ffmpeg.FrameDuration > 0 ? ffmpeg.FrameDuration : 1 / 30.0, ffmpeg.FrameDuration > 0, false, 0);
            DurationSeconds = ffmpeg.DurationSeconds;

            // Discard state from the MF attempt: the first frame read is file time 0.
            _keyFrames.Clear();
            (_heldTime, _heldUntil, _readTime, _runTime, _lastKept) = (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
            (_endOfStream, _landing, _originKnown) = (false, true, false);
            if (!ReadFrame(double.NegativeInfinity, out _, out _))
                throw new GpuException(unchecked((int)0xC00D36B4), "FFmpeg gave no frame");
            _readTime = _heldTime;
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            throw new GpuException(code, $"Decoding with Media Foundation ({why.Message}) and with FFmpeg ({FfmpegVideoDecoder.ReasonOf(ex)})");
        }
    }

    private bool _originKnown;

    // True when a reader is open (MF or FFmpeg).
    private bool Opened => _reader != 0 || _ffmpeg is not null;

    private bool SetOutput(in Guid subtype)
    {
        nint type;
        Com.Check(Mf.MFCreateMediaType(&type), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(type, MfGuids.MajorType, MfGuids.Video);
            Mf.SetGuid(type, MfGuids.Subtype, subtype);
            if (Mf.SetCurrentMediaType(_reader, Stream, type) < 0)
                return false;
            TenBit = subtype == MfGuids.P010;
            return true;
        }
        finally
        {
            Com.Release(ref type);
        }
    }

    private void ReadOutputSize()
    {
        Com.Check(Mf.GetCurrentMediaType(_reader, Stream, out var current), "GetCurrentMediaType");
        try
        {
            Com.Check(Mf.GetUInt64(current, MfGuids.FrameSize, out var size), "MF_MT_FRAME_SIZE");
            (Width, Height) = ((int)(size >> 32), (int)(size & 0xFFFFFFFF));

            // Visible picture inside the decoder's padded frame (1920 x 1088 for 1080-line H.264).
            MfVideoArea area;
            if (Mf.GetBlob(current, MfGuids.MinimumDisplayAperture, &area, (uint)sizeof(MfVideoArea)) >= 0 && area.Width > 0 && area.Height > 0)
                (Width, Height) = (Math.Min(area.Width, Width), Math.Min(area.Height, Height));
        }
        finally
        {
            Com.Release(ref current);
        }

        // Round down to even for the chroma plane.
        (Width, Height) = (Width & ~1, Height & ~1);
    }

    /// <summary>
    /// Holds the frame shown at <paramref name="seconds"/> (file time). False when none (past the end with nothing decoded); the last
    /// frame stays held past the end.
    /// </summary>
    public bool ShowAt(double seconds)
    {
        if (!Opened)
            return false;
        if (seconds < 0)
            seconds = 0;

        // Already held (paused player, or layers sharing a source at one time).
        if (!double.IsNaN(_heldTime) && seconds >= _heldTime - 1e-6 && (seconds < _heldUntil - 1e-6 || _endOfStream && seconds >= _heldTime))
            return true;

        var ahead = !double.IsNaN(_readTime) && seconds > _readTime && !_endOfStream;
        if (!ahead || !WithinReach(seconds))
            Seek(seconds);

        // Decode forward: earlier frames are read and dropped; the last one at or before the target is held.
        var guard = 0;
        while (guard++ < 2000)
        {
            if (!PeekNextBefore(seconds))
                break;
        }

        return !double.IsNaN(_heldTime);
    }

    /// <summary>True when decoding forward from the reader position costs no more than a seek.</summary>
    private bool WithinReach(double seconds)
    {
        var distance = seconds - _readTime;
        if (distance <= FrameDuration * 3)
            return true;

        // Complete client I-frame list: go forward when no key frame lies between, or the one that does is barely nearer.
        if (_iFrames is { } list)
        {
            var at = Array.BinarySearch(list, seconds + 1e-6);
            at = at >= 0 ? at : ~at - 1;
            if (at < 0)
                return true;
            var key = list[at];
            return key <= _readTime + FrameDuration / 2 || distance <= seconds - key + FrameDuration * 4;
        }

        // Learned key frames are incomplete: decode through up to 1 s ahead, seek beyond; a known key frame in between favours
        // seeking sooner.
        if (distance > 1.0)
            return false;
        foreach (var key in _keyFrames)
        {
            if (key > _readTime + FrameDuration / 2 && key <= seconds + 1e-6 && seconds - key + FrameDuration * 4 < distance)
                return false;
        }

        return true;
    }

    private void Seek(double seconds)
    {
        Seeks++;

        // Clamp inside the stream: a time past the last frame (clip running to the end, B-frame-delayed first frame) resolves to the
        // last frame, reached by decoding forward from the preceding key frame.
        var ticks = _origin + (long)Math.Round(seconds * 1e7);
        if (DurationSeconds > 0)
            ticks = Math.Min(ticks, _origin + (long)((DurationSeconds - FrameDuration) * 1e7));
        if (_ffmpeg is { } ffmpeg)
        {
            // Restart FFmpeg at the key frame at or before it (its stamps share the origin's stream clock).
            ffmpeg.Seek(Math.Max(ticks, _origin));
        }
        else
        {
            _ = Mf.Flush(_reader, Stream);
            var hr = Mf.SetCurrentPosition(_reader, Math.Max(ticks, 0));
            if (hr < 0 && ticks > _origin)
                hr = Mf.SetCurrentPosition(_reader, _origin);
            Com.Check(hr, "SetCurrentPosition");
        }

        _readTime = _runTime = double.NaN;
        _endOfStream = false;
        _heldTime = _heldUntil = double.NaN;
        _landing = true;
    }

    // Time of the previous frame in this reader run (NaN after a seek): consecutive reads are adjacent in time.
    private double _runTime = double.NaN;

    /// <summary>Maximum gap between consecutive frames that the first is extended across (seconds).</summary>
    public const double MaxRunGap = 1.0;

    /// <summary>Provisional duration of a cached frame of unknown length until the next read closes it (seconds).</summary>
    private const double ProvisionalLength = 0.001;

    // Whether the stream reported its frame rate (MF_MT_FRAME_RATE), or a learned source duration was seeded.
    private bool _rateFromStream, _seeded;

    /// <summary>True when <see cref="FrameDuration"/> is reliable: from the stream, learned from frame spacing, or seeded.</summary>
    public bool FrameDurationKnown => _rateFromStream || _seeded || _spacingCount >= _spacings.Length;

    /// <summary>Seeds the frame duration learned by another decoder of the same source (when the stream reports none).</summary>
    public void SeedFrameDuration(double seconds)
    {
        if (_rateFromStream || !(seconds > 0) || _spacingCount >= _spacings.Length)
            return;
        (FrameDuration, _seeded) = (seconds, true);
    }

    // Recent consecutive frame spacings (seconds), for streams without a reported frame rate.
    private readonly double[] _spacings = new double[8];
    private int _spacingCount;

    /// <summary>
    /// Learns frame duration from spacing: the median of the last eight consecutive spacings replaces the stream's value (or the assumed
    /// 1/30 s when MF reports none, e.g. many Matroska files) when they differ by over 10 % (a 60 fps file treated as 30 fps broke every
    /// lane's frame-under-reader test).
    /// </summary>
    private void LearnSpacing(double spacing)
    {
        if (!(spacing is > 0.0004 and < 1.0))
            return;
        _spacings[_spacingCount++ % _spacings.Length] = spacing;
        if (_spacingCount < _spacings.Length)
            return;

        Span<double> sorted = stackalloc double[8];
        _spacings.CopyTo(sorted);
        sorted.Sort();
        var median = (sorted[3] + sorted[4]) / 2;
        if (Math.Abs(median - FrameDuration) > FrameDuration * 0.1)
            FrameDuration = median;
    }

    /// <summary>True when the last frame read was a seek landing (lets the decode lane tell a late landing from a gap).</summary>
    public bool LastWasLanding { get; private set; }

    /// <summary>
    /// Reads the next sample if it starts at or before <paramref name="seconds"/> (holding it), or anything right after a seek. Returns
    /// whether to continue reading.
    /// </summary>
    private bool PeekNextBefore(double seconds)
    {
        if (_endOfStream)
            return false;
        if (!double.IsNaN(_heldTime) && _heldUntil > seconds + 1e-6)
            return false;

        // Hardware frames are cheap GPU copies, kept for each; CPU frames are uploads, made only near the target (so the last frame
        // before end of stream is still held).
        if (!ReadFrame(Hardware ? double.NegativeInfinity : seconds - 2 * FrameDuration, out var ticks, out var length))
            return false;
        var time = (ticks - _origin) / 1e7;
        return time + length <= seconds + 1e-6;
    }

    /// <summary>
    /// Reads one sample and holds it when it ends after <paramref name="keepAfter"/> (seconds). False at end of stream.
    /// </summary>
    private bool ReadFrame(double keepAfter, out long ticks, out double length)
    {
        ticks = 0;
        length = FrameDuration;
        if (_ffmpeg is { } ffmpeg)
        {
            // FFmpeg's next picture with its stamp and duration to the next (0 = unknown, like a sample without duration).
            Beat?.Invoke();
            if (!ffmpeg.Read(out ticks, out var lengthTicks))
            {
                _endOfStream = true;
                return false;
            }

            length = Took(ticks, lengthTicks / 1e7, keepAfter, 0);
            return true;
        }

        for (var attempt = 0; attempt < 64; attempt++)
        {
            Beat?.Invoke();
            var hr = Mf.ReadSample(_reader, Stream, out var flags, out var timestamp, out var sample);

            // The sample is owned from here: every path below (error, throwing media-type refresh, end of stream, frame taken) releases it
            // exactly once in the finally.
            try
            {
                if (Com.IsDeviceLost(hr))
                    throw new GpuException(hr, "ReadSample");
                Com.Check(hr, "ReadSample");
                if ((flags & Mf.ReaderError) != 0)
                    throw new GpuException(unchecked((int)0x80004005), "The decoder reported an error");

                if ((flags & Mf.ReaderCurrentMediaTypeChanged) != 0)
                    ReadOutputSize();

                if ((flags & Mf.ReaderEndOfStream) != 0)
                {
                    _endOfStream = true;
                    return false;
                }

                if (sample == 0)
                    continue; // stream tick or gap: keep reading

                ticks = timestamp;
                length = Took(timestamp, Mf.SampleDuration(sample) / 1e7, keepAfter, sample);
                return true;
            }
            finally
            {
                Com.Release(ref sample);
            }
        }

        return false;
    }

    /// <summary>
    /// Processes a read frame from MF (<paramref name="sample"/>) or FFmpeg (0 = its picture): converts to file time, records seek-landing
    /// key frames, keeps it (held or cached) when it ends after <paramref name="keepAfter"/>, and closes the gap to the previous frame.
    /// Returns its duration.
    /// </summary>
    private double Took(long timestamp, double length, double keepAfter, nint sample)
    {
        if (!_originKnown)
            (_origin, _originKnown) = (timestamp, true);
        var time = (timestamp - _origin) / 1e7;

        // FFmpeg durations run to the next picture, so the end is known (FrameCache closed frame); MF sample durations are not.
        var closed = sample == 0 && length > 0;

        // No sample duration: use the stream's or learned frame duration. If still unknown (fresh decoder, no reported rate), a cached
        // frame lasts 1 ms until the next consecutive read closes it (FrameCache.CloseGap): never a guess covering an undecoded frame.
        if (length <= 0)
            length = FrameDurationKnown || _cache is null ? FrameDuration : ProvisionalLength;
        var landed = _landing;
        if (_landing)
        {
            _landing = false;
            var at = _keyFrames.BinarySearch(time);
            if (at < 0 && _keyFrames.Count < 100_000)
                _keyFrames.Insert(~at, time);
        }

        _readTime = time;
        FramesDecoded++;
        LastWasLanding = landed;

        // Cached mode always keeps the seek-landing key frame (what fast scrubs show), others from keepAfter on; frames already cached
        // are not copied again (a chase through previously decoded frames).
        if (time + length > keepAfter || landed && _cache is not null)
        {
            if (_cache is not null && _cache.HasFrameAt(_source, time, landed))
                _lastKept = time;
            else
                Hold(sample, time, length, landed, closed);
            _heldTime = time;
            _heldUntil = time + length;
        }

        // Extend the previous frame in this run to this one's start (FrameCache.CloseGap): consecutive reads without a seek have nothing
        // between them, so encoder-dropped frames or VFR gaps are held as a constant-rate timeline would (up to 1 s; beyond is a hole).
        if (!double.IsNaN(_runTime))
        {
            LearnSpacing(time - _runTime);
            _cache?.CloseGap(_source, _runTime, time, MaxRunGap);
        }

        _runTime = time;
        return length;
    }

    /// <summary>
    /// Copies a sample's picture (FFmpeg's when <paramref name="sample"/> is 0) into this decoder's textures, or (cached) into a cache frame.
    /// </summary>
    private void Hold(nint sample, double time, double length, bool keyFrame, bool closed)
    {
        nint buffer = 0;
        CachedFrame? frame = null;
        try
        {
            if (sample == 0)
            {
                // FFmpeg picture in system memory: upload like CPU-decoded frames.
                if (_cache is not null)
                    frame = _cache.Rent(Width, Height, TenBit, software: true);
                UploadFfmpegPicture(frame);
            }
            else
            {
                Com.Check(Mf.GetBufferByIndex(sample, 0, out buffer), "GetBufferByIndex");
                if (Hardware && Com.Query(buffer, Iid.MfDxgiBuffer) is var dxgi and not 0)
                {
                    var crumb = HandPegApp.Services.GpuNativeBreadcrumbs.EnterHot("MF.CopyHardwareFrame");
                    CopyHardwareFrame(dxgi, ref frame);
                    crumb.Leave();
                }
                else
                {
                    if (_cache is not null)
                        frame = _cache.Rent(Width, Height, TenBit, software: true);
                    UploadSoftware(buffer, frame);
                }
            }

            if (frame is not null)
            {
                (frame.Source, frame.Time, frame.Until, frame.KeyFrame, frame.Ahead, frame.Closed) = (_source, time, time + length, keyFrame, KeepingAhead, closed);
                (frame.Matrix, frame.FullRange, frame.Transfer) = (Matrix, FullRange, Transfer);
                _cache!.Insert(frame);
                _lastKept = time;
                frame = null;
            }
        }
        finally
        {
            if (frame is not null)
                _cache!.Return(frame);
            Com.Release(ref buffer);
        }
    }

    /// <summary>
    /// Copies the GPU-decoded picture (IMFDXGIBuffer texture) GPU-to-GPU into this decoder's texture, or (cached) into a rented cache frame
    /// (<paramref name="frame"/>). Releases <paramref name="dxgi"/>.
    /// </summary>
    private void CopyHardwareFrame(nint dxgi, ref CachedFrame? frame)
    {
        try
        {
            Com.Check(Mf.GetTexture(dxgi, out var texture, out var subresource), "IMFDXGIBuffer::GetResource");
            try
            {
                nint target;
                if (_cache is not null)
                {
                    frame = _cache.Rent(Width, Height, TenBit, software: false);
                    target = frame.Planar!.Texture;
                }
                else
                {
                    EnsureHardwareFrame();
                    target = _frame!.Texture;
                }

                var box = new Box { Right = (uint)Width, Bottom = (uint)Height, Back = 1 };
                _gpu.Context.CopySubresourceRegion(target, 0, 0, 0, texture, subresource, &box);
            }
            finally
            {
                Com.Release(ref texture);
            }
        }
        finally
        {
            Com.Release(ref dxgi);
        }
    }

    private double _lastKept = double.NaN;

    /// <summary>Set by a lane during speculative decoding (hint, pre-roll, play-ahead): marks the frames it keeps.</summary>
    public bool KeepingAhead { get; set; }

    private void EnsureHardwareFrame()
    {
        var format = TenBit ? DxgiFormat.P010 : DxgiFormat.Nv12;
        if (_frame is { } frame && frame.Width == Width && frame.Height == Height && frame.Format == format)
            return;

        ReleaseFrame();
        _frame = _gpu.CreateTexture(Width, Height, format, renderTarget: false);
        var luma = new ShaderResourceViewDesc { Format = TenBit ? DxgiFormat.R16Unorm : DxgiFormat.R8Unorm, ViewDimension = D3D11.SrvTexture2D, U0 = 0, U1 = 1 };
        var chroma = new ShaderResourceViewDesc { Format = TenBit ? DxgiFormat.R16G16Unorm : DxgiFormat.R8G8Unorm, ViewDimension = D3D11.SrvTexture2D, U0 = 0, U1 = 1 };
        _lumaSrv = _gpu.Device.CreateShaderResourceView(_frame.Texture, &luma);
        _chromaSrv = _gpu.Device.CreateShaderResourceView(_frame.Texture, &chroma);
    }

    /// <summary>
    /// Uploads a system-memory frame (NV12 or P010, rows of <c>pitch</c> bytes, chroma after the padded luma) into this decoder's planes,
    /// or into <paramref name="into"/>'s (cached).
    /// </summary>
    private void UploadSoftware(nint buffer, CachedFrame? into)
    {
        var bytesPerSample = TenBit ? 2 : 1;

        // Padded plane height (output type frame size, before the display aperture).
        Com.Check(Mf.GetCurrentMediaType(_reader, Stream, out var current), "GetCurrentMediaType");
        ulong size;
        try
        {
            _ = Mf.GetUInt64(current, MfGuids.FrameSize, out size);
        }
        finally
        {
            Com.Release(ref current);
        }

        var paddedHeight = (int)(size & 0xFFFFFFFF);
        var buffer2D = Com.Query(buffer, Iid.Mf2DBuffer);
        byte* data;
        int pitch;
        bool locked2D;
        if (buffer2D != 0 && Mf.Lock2D(buffer2D, out data, out pitch) >= 0)
        {
            locked2D = true;
        }
        else
        {
            locked2D = false;
            Com.Check(Mf.Lock(buffer, out data, out _), "IMFMediaBuffer::Lock");
            pitch = Width * bytesPerSample;
        }

        try
        {
            if (pitch < 0)
                throw new GpuException(unchecked((int)0x80004005), "A bottom-up frame");
            Upload(data, pitch, data + (long)pitch * Math.Max(paddedHeight, Height), pitch, into);
        }
        finally
        {
            if (locked2D)
                _ = Mf.Unlock2D(buffer2D);
            else
                _ = Mf.Unlock(buffer);
            Com.Release(ref buffer2D);
        }
    }

    /// <summary>Uploads FFmpeg's last picture (luma rows, then interleaved chroma rows, each at its own pitch) like <see cref="UploadSoftware"/>.</summary>
    private void UploadFfmpegPicture(CachedFrame? into)
    {
        var ffmpeg = _ffmpeg!;
        fixed (byte* picture = ffmpeg.Picture)
            Upload(picture, ffmpeg.LumaPitch, picture + ffmpeg.ChromaOffset, ffmpeg.ChromaPitch, into);
    }

    /// <summary>
    /// Uploads two system-memory planes into this decoder's luma and chroma textures (created at frame size when needed), or into
    /// <paramref name="into"/>'s (cached).
    /// </summary>
    private void Upload(byte* luma, int lumaPitch, byte* chroma, int chromaPitch, CachedFrame? into)
    {
        if (into is null && (_softLuma is null || _softLuma.Width != Width || _softLuma.Height != Height))
        {
            ReleaseFrame();
            _softLuma = _gpu.CreateTexture(Width, Height, TenBit ? DxgiFormat.R16Unorm : DxgiFormat.R8Unorm, renderTarget: false);
            _softChroma = _gpu.CreateTexture(Width / 2, Height / 2, TenBit ? DxgiFormat.R16G16Unorm : DxgiFormat.R8G8Unorm, renderTarget: false);
            (_lumaSrv, _chromaSrv) = (_softLuma.Srv, _softChroma!.Srv);
            Com.AddRef(_lumaSrv);
            Com.AddRef(_chromaSrv);
        }

        var (lumaPlane, chromaPlane) = into is not null ? (into.Luma!, into.Chroma!) : (_softLuma!, _softChroma!);
        _gpu.Context.UpdateSubresource(lumaPlane.Texture, 0, null, luma, (uint)lumaPitch, 0);
        _gpu.Context.UpdateSubresource(chromaPlane.Texture, 0, null, chroma, (uint)chromaPitch, 0);
    }

    // ----- Decode-lane interface (cached mode) -----

    /// <summary>Time of the last frame read (NaN right after a seek); the next read follows it.</summary>
    public double ReadTime => _readTime;

    public bool AtEnd => _endOfStream;

    public bool IsOpen => Opened;

    /// <summary>
    /// Seek overhead in frame-equivalents beyond decoding from the key frame (flush and first decode); FFmpeg's is higher since it restarts
    /// a process (~80 ms on Snapdragon X Elite).
    /// </summary>
    private const double SeekFrames = 4, FfmpegSeekFrames = 12;

    /// <summary>Best-known key frame at or before <paramref name="seconds"/> (client list, else learned); NaN when none.</summary>
    public double KeyAtOrBefore(double seconds)
    {
        if (_iFrames is { } list)
        {
            var at = Array.BinarySearch(list, seconds + 1e-6);
            at = at >= 0 ? at : ~at - 1;
            return at >= 0 ? list[at] : 0;
        }

        var learned = _keyFrames.BinarySearch(seconds + 1e-6);
        learned = learned >= 0 ? learned : ~learned - 1;
        return learned >= 0 ? _keyFrames[learned] : double.NaN;
    }

    /// <summary>
    /// Key frame at or before <paramref name="seconds"/> from the client's I-frame list only (NaN without one): safe from other threads
    /// while the owning lane reads (the learned list is owned by that lane).
    /// </summary>
    public double ListedKeyAtOrBefore(double seconds)
    {
        if (_iFrames is not { } list)
            return double.NaN;
        var at = Array.BinarySearch(list, seconds + 1e-6);
        at = at >= 0 ? at : ~at - 1;
        return at >= 0 ? list[at] : 0;
    }

    /// <summary>
    /// Estimated frames to decode to reach <paramref name="seconds"/>: forward from the reader when within reach, else from the preceding
    /// key frame plus seek overhead.
    /// </summary>
    public double CostTo(double seconds)
    {
        if (!Opened)
            return double.PositiveInfinity;
        if (!double.IsNaN(_readTime) && seconds > _readTime && !_endOfStream && WithinReach(seconds))
            return (seconds - _readTime) / FrameDuration;
        var key = KeyAtOrBefore(seconds);
        var chase = double.IsNaN(key) ? Math.Min(Math.Max(seconds, 0), 1.0) : Math.Max(seconds - key, 0);
        return (_ffmpeg is null ? SeekFrames : FfmpegSeekFrames) + chase / FrameDuration;
    }

    /// <summary>Positions reading toward <paramref name="seconds"/>: seeks unless decoding forward is as short.</summary>
    public void Prepare(double seconds)
    {
        if (!Opened)
            return;
        seconds = Math.Max(seconds, 0);
        var ahead = !double.IsNaN(_readTime) && seconds > _readTime && !_endOfStream;
        if (!ahead || !WithinReach(seconds))
            Seek(seconds);
    }

    /// <summary>Unconditionally seeks to the key frame at or before <paramref name="seconds"/>.</summary>
    public void SeekTo(double seconds)
    {
        if (Opened)
            Seek(Math.Max(seconds, 0));
    }

    /// <summary>
    /// Reads the next frame, caching it when it ends after <paramref name="keepFrom"/> (seek-landing key frames always), and returns its
    /// time and end. False at end of stream, where the last kept frame is made open-ended (times past the end show it).
    /// </summary>
    public bool Step(double keepFrom, out double time, out double until)
    {
        (time, until) = (double.NaN, double.NaN);
        if (!Opened || _endOfStream)
            return false;
        if (!ReadFrame(keepFrom, out var ticks, out var length))
        {
            // Only a kept last frame reached by reading forward to the end, never one left over from before a seek.
            if (_endOfStream && _cache is not null && !double.IsNaN(_lastKept) && !double.IsNaN(_readTime) && Math.Abs(_lastKept - _readTime) < 1e-6)
                _cache.HoldLastFrame(_source, _lastKept);
            return false;
        }

        time = (ticks - _origin) / 1e7;
        until = time + length;
        return true;
    }

    /// <summary>
    /// Reads back the held frame as tightly packed NV12 bytes (luma rows, then chroma rows) for the benchmark's accuracy test. Never used
    /// on the preview path.
    /// </summary>
    public byte[] ReadBack()
    {
        var bytesPerSample = TenBit ? 2 : 1;
        var result = new byte[Width * Height * bytesPerSample * 3 / 2];
        if (Hardware && _frame is not null)
        {
            var staging = _gpu.CreateStaging(Width, Height, _frame.Format);
            try
            {
                _gpu.Context.CopyResource(staging, _frame.Texture);
                Com.Check(_gpu.Context.Map(staging, 0, D3D11.MapRead, 0, out var mapped), "Map");
                try
                {
                    // A planar texture maps as luma rows then chroma rows at a single pitch.
                    var row = Width * bytesPerSample;
                    for (var y = 0; y < Height; y++)
                        new ReadOnlySpan<byte>(mapped.Data + (long)y * mapped.RowPitch, row).CopyTo(result.AsSpan(y * row));
                    for (var y = 0; y < Height / 2; y++)
                        new ReadOnlySpan<byte>(mapped.Data + (long)(Height + y) * mapped.RowPitch, row).CopyTo(result.AsSpan((Height + y) * row));
                }
                finally
                {
                    _gpu.Context.Unmap(staging, 0);
                }
            }
            finally
            {
                Com.Release(ref staging);
            }

            return result;
        }

        if (_softLuma is not null && _softChroma is not null)
        {
            ReadPlane(_softLuma, result.AsSpan(0, Width * Height * bytesPerSample), Width * bytesPerSample);
            ReadPlane(_softChroma, result.AsSpan(Width * Height * bytesPerSample), Width * bytesPerSample);
        }

        return result;
    }

    private void ReadPlane(GpuTexture plane, Span<byte> into, int row)
    {
        var staging = _gpu.CreateStaging(plane.Width, plane.Height, plane.Format);
        try
        {
            _gpu.Context.CopyResource(staging, plane.Texture);
            Com.Check(_gpu.Context.Map(staging, 0, D3D11.MapRead, 0, out var mapped), "Map");
            try
            {
                for (var y = 0; y < plane.Height; y++)
                    new ReadOnlySpan<byte>(mapped.Data + (long)y * mapped.RowPitch, row).CopyTo(into[(y * row)..]);
            }
            finally
            {
                _gpu.Context.Unmap(staging, 0);
            }
        }
        finally
        {
            Com.Release(ref staging);
        }
    }

    private void ReleaseFrame()
    {
        Com.Release(ref _lumaSrv);
        Com.Release(ref _chromaSrv);
        _frame?.Dispose();
        _frame = null;
        _softLuma?.Dispose();
        _softChroma?.Dispose();
        _softLuma = _softChroma = null;
    }

    private void Close()
    {
        Com.Release(ref _reader);
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        ReleaseFrame();
    }

    public void Dispose()
    {
        var crumb = HandPegApp.Services.GpuNativeBreadcrumbs.Enter("MF.Dispose", System.IO.Path.GetFileName(Path));
        Close();
        crumb.Leave();
    }
}

/// <summary>
/// Process-wide Media Foundation startup and the DXGI device manager that shares the compositor's device with decoders (one per device).
/// </summary>
internal static unsafe class MediaFoundation
{
    private static readonly object Gate = new();
    private static bool _started;
    private static nint _manager;
    private static nint _managerDevice;

    public static void Start()
    {
        lock (Gate)
        {
            if (_started)
                return;
            Com.Check(Mf.MFStartup(Mf.Version, Mf.StartupNoSocket), "MFStartup");
            _started = true;
        }
    }

    /// <summary>DXGI device manager bound to <paramref name="gpu"/>'s device; 0 for WARP (no video decode). Thread-safe (decode lanes open decoders).</summary>
    public static nint DeviceManagerFor(GpuDevice gpu)
    {
        if (gpu.IsWarp)
            return 0;
        Start();
        lock (Gate)
        {
            if (_manager != 0 && _managerDevice == gpu.DevicePointer)
                return _manager;

            if (_manager == 0)
            {
                uint token;
                nint manager;
                Com.Check(Mf.MFCreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
                _manager = manager;
                _resetToken = token;
            }

            Mf.ResetDevice(_manager, gpu.DevicePointer, _resetToken);
            _managerDevice = gpu.DevicePointer;
            return _manager;
        }
    }

    private static uint _resetToken;

    /// <summary>Releases the manager (before its device is released).</summary>
    public static void ReleaseManager()
    {
        lock (Gate)
        {
            Com.Release(ref _manager);
            _managerDevice = 0;
        }
    }
}
