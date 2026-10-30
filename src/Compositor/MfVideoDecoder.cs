using HandPegApp.Compositor.Native;

namespace HandPegApp.Compositor;

/// <summary>
/// One video stream of one file, decoded by Media Foundation's Source Reader (Pass 5.0-Core, 5.0A).
/// <para>Hardware: the reader is bound to the compositor's Direct3D 11 device (<see cref="MediaFoundation.DeviceManager"/>),
/// so the card's decoder writes NV12 (or P010) frames into video memory; the frame shown is copied, GPU to GPU, into this
/// decoder's own texture, and the reader's sample is let go at once (holding it would starve the decoder's pool). The
/// processor never touches a pixel.</para>
/// <para>Software (no card that decodes it, WARP, or asked for): the same reader on the processor, its frames uploaded
/// into a luma and a chroma texture.</para>
/// <para>Seeking: a frame ahead within reach is decoded forward to; anything else is a seek (to the key frame at or before
/// it, as Media Foundation seeks) and decoded forward from there. "Within reach" comes from the I-frame times: the
/// client's seek index when it was given, else the key frames learned from where seeks landed (a seek always lands on
/// one). A decoded sample's MFSampleExtension_CleanPoint says nothing: every decoded picture is marked clean.</para>
/// <para>Two ways to be used. Held (the benchmark): <see cref="ShowAt"/> keeps one frame in the decoder's own texture.
/// Cached (the compositor, Instant Seek): every frame kept goes into the <see cref="FrameCache"/> as a frame of its own,
/// and a decode lane drives the decoder one frame at a time (<see cref="Prepare"/>, <see cref="Step"/>); the decoder then
/// belongs to that lane's thread for as long as it works with it.</para>
/// </summary>
internal sealed unsafe class MfVideoDecoder : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly FrameCache? _cache;
    private readonly int _source;
    private nint _reader;
    private readonly List<double> _keyFrames = [];
    private double[]? _iFrames;

    // The frame held: its time (seconds, the file's own, from its first frame) and how long it lasts.
    private double _heldTime = double.NaN;
    private double _heldUntil = double.NaN;

    // Where the reader is: the time of the last sample read (the next read comes after it).
    private double _readTime = double.NaN;
    private bool _endOfStream;

    // The first sample's time in the reader's clock: the file's own time 0.
    private long _origin;

    // A seek was just made: the next sample read is the key frame it landed on.
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
    public bool FullRange { get; private set; }

    /// <summary>The luma and chroma planes of the frame held (0 before the first frame).</summary>
    public nint LumaSrv => _lumaSrv;
    public nint ChromaSrv => _chromaSrv;

    /// <summary>The time of the frame held, seconds; NaN before the first.</summary>
    public double HeldTime => _heldTime;

    /// <summary>The frame used last (for the pool's least-recently-used order).</summary>
    public long LastUsed { get; set; }

    /// <summary>The decode lane working with it (cached), -1 none: set and read under the pool's lock.</summary>
    public int Owner { get; set; } = -1;

    public int Seeks { get; private set; }
    public int FramesDecoded { get; private set; }

    /// <summary>The NV12 texture holding the frame (hardware), for a reader that wants the picture itself (the benchmark).</summary>
    public nint FrameTexture => _frame?.Texture ?? 0;

    public MfVideoDecoder(GpuDevice gpu, string path, int streamIndex, bool hardware, FrameCache? cache = null, int source = 0)
    {
        _gpu = gpu;
        _cache = cache;
        _source = source;
        Path = path;
        StreamIndex = streamIndex;
        try
        {
            try
            {
                Open(hardware && MediaFoundation.DeviceManagerFor(gpu) != 0);
            }
            catch (GpuException) when (hardware)
            {
                // The card cannot decode this one (a codec it lacks, a profile it refuses): the processor can.
                Close();
                Open(false);
            }
        }
        catch
        {
            // Nothing half open is kept: the file, the reader, the decoder's surfaces go with the failure.
            Close();
            throw;
        }
    }

    /// <summary>Called on every sample read and around a file's opening: the worker's heartbeat while it decodes.</summary>
    internal static Action? Beat;

    /// <summary>The client's I-frame times (seconds, the file's own time), when it has them.</summary>
    public void SetIFrames(double[]? times) => _iFrames = times is { Length: > 0 } ? times : null;

    private uint _stream = Mf.FirstVideoStream;

    private uint Stream => _stream;

    /// <summary>Media Foundation's index of the n-th video stream (<see cref="StreamIndex"/> counts video streams only).</summary>
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

        // What the file is: its size, rate, colour; 10-bit sources come out as P010.
        Com.Check(Mf.GetNativeMediaType(_reader, Stream, 0, out var native), "GetNativeMediaType");
        try
        {
            if (Mf.GetUInt64(native, MfGuids.FrameRate, out var rate) >= 0 && (rate & 0xFFFFFFFF) != 0 && (rate >> 32) != 0)
                (FrameDuration, _rateFromStream) = ((double)(rate & 0xFFFFFFFF) / (rate >> 32), true);
            if (Mf.GetUInt32(native, MfGuids.YuvMatrix, out var matrix) >= 0)
                Matrix = (int)matrix;
            if (Mf.GetUInt32(native, MfGuids.NominalRange, out var range) >= 0)
                FullRange = range == 1;
            _ = Mf.GetGuid(native, MfGuids.Subtype, out var subtype);
            // A compressed stream does not say its depth: NV12 is asked first, P010 when the decoder refuses it.
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

        // The first frame: its timestamp is the file's time 0 (ReadFrame takes it as the origin, before the frame is kept,
        // so a cached first frame is at 0 too), and it is there to show at once.
        _originKnown = false;
        if (!ReadFrame(double.NegativeInfinity, out _, out _))
            throw new GpuException(unchecked((int)0xC00D36B4), "The video has no frame");
        _readTime = _heldTime;
    }

    private bool _originKnown;

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

            // The picture inside a decoder's padded frame (1920 x 1088 for 1080 lines of H.264).
            MfVideoArea area;
            if (Mf.GetBlob(current, MfGuids.MinimumDisplayAperture, &area, (uint)sizeof(MfVideoArea)) >= 0 && area.Width > 0 && area.Height > 0)
                (Width, Height) = (Math.Min(area.Width, Width), Math.Min(area.Height, Height));
        }
        finally
        {
            Com.Release(ref current);
        }

        // Even, as the chroma plane needs.
        (Width, Height) = (Width & ~1, Height & ~1);
    }

    /// <summary>
    /// Makes the frame shown at <paramref name="seconds"/> (the file's own time) the one held. Returns false when there is
    /// none (past the end with nothing decoded); the last frame stays held past the end.
    /// </summary>
    public bool ShowAt(double seconds)
    {
        if (_reader == 0)
            return false;
        if (seconds < 0)
            seconds = 0;

        // Already held: the same frame (a paused player, layers sharing a source at one moment).
        if (!double.IsNaN(_heldTime) && seconds >= _heldTime - 1e-6 && (seconds < _heldUntil - 1e-6 || _endOfStream && seconds >= _heldTime))
            return true;

        var ahead = !double.IsNaN(_readTime) && seconds > _readTime && !_endOfStream;
        if (!ahead || !WithinReach(seconds))
            Seek(seconds);

        // Forward to it: every frame before it is read and let go; the last one at or before it is kept.
        var guard = 0;
        while (guard++ < 2000)
        {
            if (!PeekNextBefore(seconds))
                break;
        }

        return !double.IsNaN(_heldTime);
    }

    /// <summary>Whether decoding forward from where the reader is costs no more than a seek.</summary>
    private bool WithinReach(double seconds)
    {
        var distance = seconds - _readTime;
        if (distance <= FrameDuration * 3)
            return true;

        // The client's I-frame list is complete: forward when no key frame lies between, or the one that does is barely nearer.
        if (_iFrames is { } list)
        {
            var at = Array.BinarySearch(list, seconds + 1e-6);
            at = at >= 0 ? at : ~at - 1;
            if (at < 0)
                return true;
            var key = list[at];
            return key <= _readTime + FrameDuration / 2 || distance <= seconds - key + FrameDuration * 4;
        }

        // The key frames learned from seeks are only some of them: a second ahead is decoded through, more is sought,
        // and a known key frame between makes a seek the shorter way sooner.
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
        _ = Mf.Flush(_reader, Stream);

        // Held inside the stream: a moment past its last frame (a clip running to the end, a source whose first frame is
        // late, as B-frames make it) is the last frame, reached by decoding forward from the key frame before it.
        var ticks = _origin + (long)Math.Round(seconds * 1e7);
        if (DurationSeconds > 0)
            ticks = Math.Min(ticks, _origin + (long)((DurationSeconds - FrameDuration) * 1e7));
        var hr = Mf.SetCurrentPosition(_reader, Math.Max(ticks, 0));
        if (hr < 0 && ticks > _origin)
            hr = Mf.SetCurrentPosition(_reader, _origin);
        Com.Check(hr, "SetCurrentPosition");
        _readTime = _runTime = double.NaN;
        _endOfStream = false;
        _heldTime = _heldUntil = double.NaN;
        _landing = true;
    }

    // The time of the frame read before, in this run of the reader (NaN after a seek): the frames read in a row are next
    // to each other in time.
    private double _runTime = double.NaN;

    /// <summary>The longest gap between two frames read in a row that the first one is held across (seconds).</summary>
    public const double MaxRunGap = 1.0;

    /// <summary>How long a cached frame of a length not known yet lasts until the next frame read says (seconds).</summary>
    private const double ProvisionalLength = 0.001;

    // Whether the stream gave its frame rate (MF_MT_FRAME_RATE), or a source's learned length was handed over.
    private bool _rateFromStream, _seeded;

    /// <summary>Whether <see cref="FrameDuration"/> is the stream's own, learned from the frames, or a source's learned one handed over.</summary>
    public bool FrameDurationKnown => _rateFromStream || _seeded || _spacingCount >= _spacings.Length;

    /// <summary>A source's frame length learned by another of its decoders: this one starts from it (when the stream gives none).</summary>
    public void SeedFrameDuration(double seconds)
    {
        if (_rateFromStream || !(seconds > 0) || _spacingCount >= _spacings.Length)
            return;
        (FrameDuration, _seeded) = (seconds, true);
    }

    // The last spacings between frames read in a row (seconds), for the frame length a stream did not give.
    private readonly double[] _spacings = new double[8];
    private int _spacingCount;

    /// <summary>
    /// The frame length as the frames themselves space it: the median of the last eight spacings in a row replaces the
    /// stream's (or the 1/30 s assumed when Media Foundation gives none, as for many Matroska files) when they differ by a
    /// tenth (5.0-Verify: a 60 fps recording taken for 30 fps made every lane's "the frame under the reader" test wrong).
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

    /// <summary>Whether the frame read last was the one a seek landed on (the decode lane tells a late landing from a gap by it).</summary>
    public bool LastWasLanding { get; private set; }

    /// <summary>
    /// Reads the next sample if it starts at or before <paramref name="seconds"/> (keeping it as the frame held), or, the
    /// first time after a seek, whatever comes. Returns whether to go on reading.
    /// </summary>
    private bool PeekNextBefore(double seconds)
    {
        if (_endOfStream)
            return false;
        if (!double.IsNaN(_heldTime) && _heldUntil > seconds + 1e-6)
            return false;

        // A hardware frame is a copy on the card, kept for each; a processor's frame is an upload, made only for those
        // near the moment (so the last frame before the end of the stream is still held).
        if (!ReadFrame(Hardware ? double.NegativeInfinity : seconds - 2 * FrameDuration, out var ticks, out var length))
            return false;
        var time = (ticks - _origin) / 1e7;
        return time + length <= seconds + 1e-6;
    }

    /// <summary>
    /// Reads one sample and, when it ends after <paramref name="keepAfter"/> (seconds), makes it the frame held. False at the
    /// end of the stream.
    /// </summary>
    private bool ReadFrame(double keepAfter, out long ticks, out double length)
    {
        ticks = 0;
        length = FrameDuration;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            Beat?.Invoke();
            var hr = Mf.ReadSample(_reader, Stream, out var flags, out var timestamp, out var sample);
            if (Com.IsDeviceLost(hr))
                throw new GpuException(hr, "ReadSample");
            Com.Check(hr, "ReadSample");
            if ((flags & Mf.ReaderError) != 0)
            {
                Com.Release(ref sample);
                throw new GpuException(unchecked((int)0x80004005), "The decoder reported an error");
            }

            if ((flags & Mf.ReaderCurrentMediaTypeChanged) != 0)
                ReadOutputSize();

            if ((flags & Mf.ReaderEndOfStream) != 0)
            {
                Com.Release(ref sample);
                _endOfStream = true;
                return false;
            }

            if (sample == 0)
                continue; // a stream tick or a gap: read on

            try
            {
                ticks = timestamp;
                if (!_originKnown)
                    (_origin, _originKnown) = (timestamp, true);
                var time = (timestamp - _origin) / 1e7;
                length = Mf.SampleDuration(sample) / 1e7;

                // No duration on the sample: the stream's frame length, or the one learned. Not known yet (a fresh decoder
                // of a stream that gives none), a frame kept in the cache lasts a millisecond until the next one read in a
                // row says where it ends (FrameCache.CloseGap): never a guess that covers a frame not decoded yet.
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

                // Cached, the key frame a seek landed on is always kept (it is what a fast scrub shows), the rest from keepAfter
                // on; a frame the cache already holds is not copied again (a chase through frames decoded before).
                if (time + length > keepAfter || landed && _cache is not null)
                {
                    if (_cache is not null && _cache.HasFrameAt(_source, time, landed))
                        _lastKept = time;
                    else
                        Hold(sample, time, length, landed);
                    _heldTime = time;
                    _heldUntil = time + length;
                }

                // The frame before it in this run lasts until it starts (FrameCache.CloseGap): two frames read in a row with
                // no seek between have nothing between them, so a frame the encoder dropped or a variable-rate stretch is
                // held as FFmpeg's constant-rate canvas holds it (up to a second: beyond, it is a hole).
                if (!double.IsNaN(_runTime))
                {
                    LearnSpacing(time - _runTime);
                    _cache?.CloseGap(_source, _runTime, time, MaxRunGap);
                }

                _runTime = time;
                return true;
            }
            finally
            {
                Com.Release(ref sample);
            }
        }

        return false;
    }

    /// <summary>Copies a sample's picture into this decoder's own textures, or (cached) into a frame of the cache.</summary>
    private void Hold(nint sample, double time, double length, bool keyFrame)
    {
        Com.Check(Mf.GetBufferByIndex(sample, 0, out var buffer), "GetBufferByIndex");
        CachedFrame? frame = null;
        try
        {
            if (Hardware && Com.Query(buffer, Iid.MfDxgiBuffer) is var dxgi and not 0)
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
            else
            {
                if (_cache is not null)
                    frame = _cache.Rent(Width, Height, TenBit, software: true);
                UploadSoftware(buffer, frame);
            }

            if (frame is not null)
            {
                (frame.Source, frame.Time, frame.Until, frame.KeyFrame, frame.Ahead) = (_source, time, time + length, keyFrame, KeepingAhead);
                (frame.Matrix, frame.FullRange) = (Matrix, FullRange);
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

    private double _lastKept = double.NaN;

    /// <summary>Set by a lane while it decodes ahead of being asked (a hint, the pre-roll, play-ahead): marks the frames it keeps.</summary>
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
    /// A frame in the computer's memory (NV12 or P010, rows of <c>pitch</c> bytes, chroma after the padded luma), uploaded
    /// into this decoder's planes, or into <paramref name="into"/>'s (cached).
    /// </summary>
    private void UploadSoftware(nint buffer, CachedFrame? into)
    {
        var bytesPerSample = TenBit ? 2 : 1;
        if (into is null && (_softLuma is null || _softLuma.Width != Width || _softLuma.Height != Height))
        {
            ReleaseFrame();
            _softLuma = _gpu.CreateTexture(Width, Height, TenBit ? DxgiFormat.R16Unorm : DxgiFormat.R8Unorm, renderTarget: false);
            _softChroma = _gpu.CreateTexture(Width / 2, Height / 2, TenBit ? DxgiFormat.R16G16Unorm : DxgiFormat.R8G8Unorm, renderTarget: false);
            (_lumaSrv, _chromaSrv) = (_softLuma.Srv, _softChroma!.Srv);
            Com.AddRef(_lumaSrv);
            Com.AddRef(_chromaSrv);
        }

        // The padded height the planes are laid out with (the output type's frame size, before the aperture).
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
            var chroma = data + (long)pitch * Math.Max(paddedHeight, Height);
            var (lumaPlane, chromaPlane) = into is not null ? (into.Luma!, into.Chroma!) : (_softLuma!, _softChroma!);
            _gpu.Context.UpdateSubresource(lumaPlane.Texture, 0, null, data, (uint)pitch, 0);
            _gpu.Context.UpdateSubresource(chromaPlane.Texture, 0, null, chroma, (uint)pitch, 0);
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

    // ----- Driven by a decode lane (cached) -----

    /// <summary>The time of the last frame read (NaN right after a seek): the next frame read comes after it.</summary>
    public double ReadTime => _readTime;

    public bool AtEnd => _endOfStream;

    public bool IsOpen => _reader != 0;

    /// <summary>A seek costs about this many frames on top of decoding from the key frame (the flush and the first decode).</summary>
    private const double SeekFrames = 4;

    /// <summary>The key frame at or before <paramref name="seconds"/> as far as it is known (the client's list, else those learned); NaN when none is.</summary>
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
    /// The key frame at or before <paramref name="seconds"/> from the client's I-frame list only (NaN without one): safe from
    /// another thread while the lane that owns this decoder reads on (the learned list is that lane's alone).
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
    /// About how many frames must be decoded to have the frame at <paramref name="seconds"/>: forward from where the reader
    /// is when that is within reach, else from the key frame before it, plus a seek.
    /// </summary>
    public double CostTo(double seconds)
    {
        if (_reader == 0)
            return double.PositiveInfinity;
        if (!double.IsNaN(_readTime) && seconds > _readTime && !_endOfStream && WithinReach(seconds))
            return (seconds - _readTime) / FrameDuration;
        var key = KeyAtOrBefore(seconds);
        var chase = double.IsNaN(key) ? Math.Min(Math.Max(seconds, 0), 1.0) : Math.Max(seconds - key, 0);
        return SeekFrames + chase / FrameDuration;
    }

    /// <summary>Makes the frames read next lead to <paramref name="seconds"/>: a seek, unless decoding forward from here is as short.</summary>
    public void Prepare(double seconds)
    {
        if (_reader == 0)
            return;
        seconds = Math.Max(seconds, 0);
        var ahead = !double.IsNaN(_readTime) && seconds > _readTime && !_endOfStream;
        if (!ahead || !WithinReach(seconds))
            Seek(seconds);
    }

    /// <summary>Puts the reader at the key frame at or before <paramref name="seconds"/>, whatever it was doing.</summary>
    public void SeekTo(double seconds)
    {
        if (_reader != 0)
            Seek(Math.Max(seconds, 0));
    }

    /// <summary>
    /// Reads the next frame and keeps it in the cache when it ends after <paramref name="keepFrom"/> (and always the key
    /// frame a seek landed on). Its time and end come back. False at the end of the stream, where the last frame kept is
    /// given no end (a moment past the end shows it).
    /// </summary>
    public bool Step(double keepFrom, out double time, out double until)
    {
        (time, until) = (double.NaN, double.NaN);
        if (_reader == 0 || _endOfStream)
            return false;
        if (!ReadFrame(keepFrom, out var ticks, out var length))
        {
            // Only a last frame reached by reading forward to the end, and kept: never one left from before a seek.
            if (_endOfStream && _cache is not null && !double.IsNaN(_lastKept) && !double.IsNaN(_readTime) && Math.Abs(_lastKept - _readTime) < 1e-6)
                _cache.HoldLastFrame(_source, _lastKept);
            return false;
        }

        time = (ticks - _origin) / 1e7;
        until = time + length;
        return true;
    }

    /// <summary>
    /// The frame held, read back as NV12 bytes (luma rows then chroma rows, tightly packed): the benchmark's accuracy test.
    /// Never on the preview's path.
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
                    // A planar texture maps as its luma rows, then its chroma rows, at one pitch.
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
        ReleaseFrame();
    }

    public void Dispose() => Close();
}

/// <summary>
/// Media Foundation, started once per process, and the device manager that lends the compositor's device to the
/// decoders (one per device).
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

    /// <summary>The DXGI device manager holding <paramref name="gpu"/>'s device; 0 for WARP (no video decode). Any thread: the decode lanes open decoders.</summary>
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

    /// <summary>Lets go of the manager (before its device is released).</summary>
    public static void ReleaseManager()
    {
        lock (Gate)
        {
            Com.Release(ref _manager);
            _managerDevice = 0;
        }
    }
}
