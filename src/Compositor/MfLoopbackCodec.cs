using System.Diagnostics;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- The Export Quality Preview's encoder and decoder (Pass 5.4B): Media Foundation transforms driven by hand -----
//
// The export's codec, as a hardware encoder transform of this PC has it, at the export's rate control and bitrate, and the decoder
// that reads its bitstream back (ExportLoopbackPreview). No FFmpeg in the worker (Law 4: FFmpeg only encodes and muxes an export).
// The encoder takes NV12 in the computer's memory, as FFmpeg's h264_mf does, so any card's encoder works; the decoder writes into
// a texture of the worker's own device where it can (D3D11-aware), else into memory that is uploaded.

/// <summary>What the loopback is made for: the codec's subtype, the encoded size, the clock and the rate control.</summary>
internal readonly record struct LoopbackConfig(Guid Codec, int Width, int Height, double Fps, int RateControl, int BitrateKbps, int Quality);

/// <summary>
/// A hardware encoder transform (asynchronous, as every hardware one is): its events read without waiting on the worker's thread
/// (no callback object), one input per METransformNeedInput, one output per METransformHaveOutput.
/// </summary>
internal sealed unsafe class MfEncoderMft : IDisposable
{
    private nint _activate, _transform, _events, _codecApi;
    private readonly bool _providesSamples;
    private readonly uint _outputSize;
    private int _needInput, _haveOutput, _inputsSinceDrain;
    private nint _waiting;
    private bool _draining;
    private uint _qualityVsSpeed;
    private readonly bool _qualityVsSpeedModifiable;

    /// <summary>The transform's friendly name (what the preview is an approximation by).</summary>
    public string Name { get; }

    private MfEncoderMft(nint activate, nint transform, nint events, nint codecApi, string name, MftOutputStreamInfo info, uint qualityVsSpeed)
    {
        (_activate, _transform, _events, _codecApi, Name) = (activate, transform, events, codecApi, name);
        _providesSamples = (info.Flags & (MftOutputStreamInfo.ProvidesSamples | MftOutputStreamInfo.CanProvideSamples)) != 0;
        _outputSize = info.Size;
        _qualityVsSpeed = qualityVsSpeed;
        _qualityVsSpeedModifiable = codecApi != 0 && Mf.IsModifiable(codecApi, CodecApi.QualityVsSpeed);
    }

    /// <summary>
    /// The first hardware encoder of the codec that takes the settings: the worker's card's first (MFTEnum2 narrowed to its LUID), then
    /// any card's. Null with <see cref="ExportLoopbackState.NoEncoder"/> when there is none, or <see cref="ExportLoopbackState.Failed"/>
    /// and the HRESULT when none started.
    /// </summary>
    public static MfEncoderMft? Create(GpuDevice gpu, in LoopbackConfig config, int qualityVsSpeed, out ExportLoopbackState state, out int hresult)
    {
        (state, hresult) = (ExportLoopbackState.NoEncoder, 0);
        MediaFoundation.Start();
        var output = new MftRegisterTypeInfo { MajorType = MfGuids.Video, Subtype = config.Codec };
        foreach (var onCard in (bool[])[true, false])
        {
            nint attributes = 0;
            nint* activates = null;
            uint count = 0;
            try
            {
                int result;
                if (onCard)
                {
                    if (Mf.MFCreateAttributes(&attributes, 1) < 0)
                        continue;
                    var luid = gpu.AdapterLuid;
                    Mf.SetBlob(attributes, MfGuids.EnumAdapterLuid, &luid, sizeof(long));
                    result = Mf.MFTEnum2(MfGuids.VideoEncoderCategory, Mf.EnumHardware | Mf.EnumSortAndFilter, null, &output, attributes, &activates, &count);
                }
                else
                {
                    result = Mf.MFTEnumEx(MfGuids.VideoEncoderCategory, Mf.EnumHardware | Mf.EnumSortAndFilter, null, &output, &activates, &count);
                }

                if (result < 0 || activates is null)
                    continue;
                for (var i = 0; i < count; i++)
                {
                    var activate = activates[i];
                    if (activate == 0)
                        continue;
                    var encoder = TryStart(activate, config, qualityVsSpeed, out var failed);
                    if (encoder is not null)
                    {
                        activates[i] = 0; // kept by the encoder (shut down and let go with it)
                        state = ExportLoopbackState.Starting;
                        return encoder;
                    }

                    (state, hresult) = (ExportLoopbackState.Failed, failed);
                }
            }
            finally
            {
                // The array was null and the count 0 before the call; only an array the call gave is let go (HardwareBenchmark.CountMfts, 5.2E).
                if (activates is not null)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var left = activates[i];
                        Com.Release(ref left);
                    }

                    Com.CoTaskMemFree((nint)activates);
                }

                Com.Release(ref attributes);
            }
        }

        return null;
    }

    // One transform activated and set up: rate control first (some drivers take it only before the types), the output type, then the
    // input's, then streaming. Null and why when any step fails; a lost device is thrown (the worker starts again).
    private static MfEncoderMft? TryStart(nint activate, in LoopbackConfig config, int qualityVsSpeed, out int failed)
    {
        failed = 0;
        nint transform = 0, events = 0, codecApi = 0;
        try
        {
            MfVideoDecoder.Beat?.Invoke();
            var hr = Mf.ActivateTransform(activate, out transform);
            MfVideoDecoder.Beat?.Invoke();
            Check(hr, "IMFActivate::ActivateObject (encoder)");

            // An asynchronous transform answers nothing until it is unlocked.
            var attributes = Mf.TransformAttributes(transform);
            if (attributes != 0)
            {
                if (Mf.GetUInt32(attributes, MftGuids.TransformAsync, out var isAsync) >= 0 && isAsync != 0)
                    Mf.SetUInt32(attributes, MftGuids.TransformAsyncUnlock, 1);
                TrySet(attributes, MfGuids.LowLatency, 1);
                Com.Release(ref attributes);
            }

            var bits = (uint)Math.Clamp((long)config.BitrateKbps * 1000, 100_000, 400_000_000);
            codecApi = Com.Query(transform, MfIid.CodecApi);
            if (codecApi != 0)
            {
                _ = Mf.SetCodecValue(codecApi, CodecApi.RateControlMode, (uint)config.RateControl);
                if (config.RateControl == 3)
                {
                    _ = Mf.SetCodecValue(codecApi, CodecApi.Quality, (uint)Math.Clamp(config.Quality, 1, 100));
                }
                else
                {
                    _ = Mf.SetCodecValue(codecApi, CodecApi.MeanBitRate, bits);
                    if (config.RateControl == 0)
                        _ = Mf.SetCodecValue(codecApi, CodecApi.BufferSize, bits);
                }

                _ = Mf.SetCodecValue(codecApi, CodecApi.QualityVsSpeed, (uint)Math.Clamp(qualityVsSpeed, 0, 100));
                _ = Mf.SetCodecBool(codecApi, CodecApi.LowLatencyMode, true);
                _ = Mf.SetCodecValue(codecApi, CodecApi.DefaultBPictureCount, 0);
                _ = Mf.SetCodecValue(codecApi, CodecApi.GopSize, (uint)Math.Clamp(Math.Round(config.Fps * 2), 1, 1000));
            }

            var (rateNumerator, rateDenominator) = Rate(config.Fps);
            if (!SetOutput(transform, config, bits, rateNumerator, rateDenominator, withProfile: true))
                Check(SetOutputResult(transform, config, bits, rateNumerator, rateDenominator, withProfile: false), "IMFTransform::SetOutputType (encoder)");
            Check(SetInput(transform, config, rateNumerator, rateDenominator), "IMFTransform::SetInputType (encoder)");
            Check(Mf.GetOutputStreamInfo(transform, 0, out var info), "IMFTransform::GetOutputStreamInfo (encoder)");

            events = Com.Query(transform, MfIid.MediaEventGenerator);
            Check(Mf.ProcessMessage(transform, Mf.MessageBeginStreaming, 0), "MFT_MESSAGE_NOTIFY_BEGIN_STREAMING (encoder)");
            Check(Mf.ProcessMessage(transform, Mf.MessageStartOfStream, 0), "MFT_MESSAGE_NOTIFY_START_OF_STREAM (encoder)");
            var name = Mf.GetString(activate, MfGuids.FriendlyName) ?? "a Media Foundation encoder";
            var encoder = new MfEncoderMft(activate, transform, events, codecApi, name, info, (uint)Math.Clamp(qualityVsSpeed, 0, 100));
            (transform, events, codecApi) = (0, 0, 0);
            return encoder;
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            failed = ex.Code;
            return null;
        }
        finally
        {
            Com.Release(ref events);
            Com.Release(ref codecApi);
            if (transform != 0)
            {
                Com.Release(ref transform);
                _ = Mf.ShutdownObject(activate);
            }
        }
    }

    private static bool SetOutput(nint transform, in LoopbackConfig config, uint bits, uint numerator, uint denominator, bool withProfile) =>
        SetOutputResult(transform, config, bits, numerator, denominator, withProfile) >= 0;

    private static int SetOutputResult(nint transform, in LoopbackConfig config, uint bits, uint numerator, uint denominator, bool withProfile)
    {
        nint type;
        Check(Mf.MFCreateMediaType(&type), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(type, MfGuids.MajorType, MfGuids.Video);
            Mf.SetGuid(type, MfGuids.Subtype, config.Codec);
            Mf.SetPair(type, MfGuids.FrameSize, (uint)config.Width, (uint)config.Height);
            Mf.SetPair(type, MfGuids.FrameRate, numerator, denominator);
            Mf.SetPair(type, MftGuids.PixelAspectRatio, 1, 1);
            Mf.SetUInt32(type, MftGuids.InterlaceMode, MftGuids.Progressive);
            Mf.SetUInt32(type, MftGuids.AvgBitrate, bits);
            if (withProfile && config.Codec == MfGuids.H264)
                Mf.SetUInt32(type, MftGuids.Mpeg2Profile, MftGuids.H264High);
            return Mf.SetOutputType(transform, 0, type);
        }
        finally
        {
            Com.Release(ref type);
        }
    }

    // NV12 at the size and rate: the transform's own NV12 type where it lists one (it may carry what its driver wants), else one made here.
    private static int SetInput(nint transform, in LoopbackConfig config, uint numerator, uint denominator)
    {
        for (uint i = 0; i < 64; i++)
        {
            if (Mf.GetInputAvailableType(transform, 0, i, out var listed) < 0)
                break;
            try
            {
                if (Mf.GetGuid(listed, MfGuids.Subtype, out var subtype) < 0 || subtype != MfGuids.Nv12)
                    continue;
                Mf.SetPair(listed, MfGuids.FrameSize, (uint)config.Width, (uint)config.Height);
                Mf.SetPair(listed, MfGuids.FrameRate, numerator, denominator);
                if (Mf.SetInputType(transform, 0, listed) >= 0)
                    return 0;
            }
            finally
            {
                Com.Release(ref listed);
            }
        }

        nint type;
        Check(Mf.MFCreateMediaType(&type), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(type, MfGuids.MajorType, MfGuids.Video);
            Mf.SetGuid(type, MfGuids.Subtype, MfGuids.Nv12);
            Mf.SetPair(type, MfGuids.FrameSize, (uint)config.Width, (uint)config.Height);
            Mf.SetPair(type, MfGuids.FrameRate, numerator, denominator);
            Mf.SetPair(type, MftGuids.PixelAspectRatio, 1, 1);
            Mf.SetUInt32(type, MftGuids.InterlaceMode, MftGuids.Progressive);
            return Mf.SetInputType(transform, 0, type);
        }
        finally
        {
            Com.Release(ref type);
        }
    }

    /// <summary>A frame rate as Media Foundation's ratio: whole rates as n/1, the NTSC ones as n*1000/1001, others in thousandths.</summary>
    internal static (uint Numerator, uint Denominator) Rate(double fps)
    {
        if (!(fps > 0) || !double.IsFinite(fps))
            return (30, 1);
        if (Math.Abs(fps - Math.Round(fps)) < 1e-3)
            return ((uint)Math.Round(fps), 1);
        var ntsc = fps * 1001 / 1000;
        if (Math.Abs(ntsc - Math.Round(ntsc)) < 1e-2)
            return ((uint)Math.Round(ntsc) * 1000, 1001);
        return ((uint)Math.Round(fps * 1000), 1000);
    }

    /// <summary>The bitstream's sequence header (SPS/PPS and the like) as the encoder's output type has it once streaming; null if none.</summary>
    public byte[]? SequenceHeader()
    {
        if (Mf.GetOutputCurrentType(_transform, 0, out var type) < 0)
            return null;
        try
        {
            return Mf.GetBlobBytes(type, MftGuids.MpegSequenceHeader);
        }
        finally
        {
            Com.Release(ref type);
        }
    }

    /// <summary>
    /// Quality against speed while streaming, where the driver lets it change (a playing frame is encoded faster than a paused one,
    /// as the export's preset would not be).
    /// </summary>
    public void SetQualityVsSpeed(int value)
    {
        var wanted = (uint)Math.Clamp(value, 0, 100);
        if (!_qualityVsSpeedModifiable || wanted == _qualityVsSpeed)
            return;
        if (Mf.SetCodecValue(_codecApi, CodecApi.QualityVsSpeed, wanted) >= 0)
            _qualityVsSpeed = wanted;
    }

    /// <summary>
    /// Hands a frame over (the sample is taken: let go here). It goes in at once when the encoder asked for input; else it waits, in
    /// place of any frame that was waiting (the newest picture only, as the worker draws). <paramref name="forceKey"/>: a key frame.
    /// </summary>
    public void Submit(nint sample, bool forceKey)
    {
        if (forceKey && _codecApi != 0)
            _ = Mf.SetCodecValue(_codecApi, CodecApi.ForceKeyFrame, 1);
        Com.Release(ref _waiting);
        _waiting = sample;
        Pump();
    }

    /// <summary>A finished bitstream sample when the encoder has one (the caller lets it go); 0 when none is ready yet.</summary>
    public nint TryTakeOutput()
    {
        Pump();
        if (_events != 0 && _haveOutput <= 0)
            return 0;

        var buffer = new MftOutputDataBuffer();
        nint own = 0;
        if (!_providesSamples)
        {
            own = Sample(Math.Max(_outputSize, 1u << 20));
            buffer.Sample = own;
        }

        var hr = Mf.ProcessOutput(_transform, ref buffer, out _);
        if (buffer.Events != 0)
            Com.Release(ref buffer.Events);
        if (_events != 0)
            _haveOutput--;
        if (hr == Mf.NeedMoreInput)
        {
            Com.Release(ref own);
            return 0;
        }

        if (hr == Mf.StreamChange)
        {
            // The encoder's output type changed (rare): its own first listed type is taken, and the next output tried.
            Com.Release(ref own);
            if (Mf.GetOutputAvailableType(_transform, 0, 0, out var type) >= 0)
            {
                _ = Mf.SetOutputType(_transform, 0, type);
                Com.Release(ref type);
            }

            return 0;
        }

        if (hr < 0)
        {
            Com.Release(ref own);
            Check(hr, "IMFTransform::ProcessOutput (encoder)");
        }

        return buffer.Sample;
    }

    /// <summary>
    /// The encoder told to give out what it holds (a paused frame a driver keeps back for its look-ahead); new input waits until it
    /// says it is done (METransformDrainComplete), and the stream then starts again. True once what went in last is drained or draining;
    /// false while it has not gone in yet (asked again on the next look).
    /// </summary>
    public bool Drain()
    {
        // Not yet while the frame waits to go in (an earlier drain not done: it goes in after it, and is drained then); nothing to do
        // when nothing went in since the last drain.
        if (_waiting != 0)
            return false;
        if (_events == 0 || _inputsSinceDrain == 0)
            return true;
        if (_draining)
            return false;
        if (Mf.ProcessMessage(_transform, Mf.MessageDrain, 0) >= 0)
            (_draining, _needInput, _inputsSinceDrain) = (true, 0, 0);
        return true;
    }

    // The events queued (never waited for), then the waiting frame in when it may go.
    private void Pump()
    {
        if (_events == 0)
        {
            // A synchronous transform (not expected of a hardware one): the frame goes in when it takes it.
            if (_waiting != 0)
            {
                var hr = Mf.ProcessInput(_transform, 0, _waiting);
                if (hr != Mf.NotAccepting)
                {
                    Com.Release(ref _waiting);
                    Check(hr, "IMFTransform::ProcessInput (encoder)");
                }
            }

            return;
        }

        for (var i = 0; i < 64; i++)
        {
            var hr = Mf.GetEventNoWait(_events, out var mediaEvent);
            if (hr == Mf.NoEventsAvailable)
                break;
            Check(hr, "IMFMediaEventGenerator::GetEvent (encoder)");
            try
            {
                Check(Mf.EventStatus(mediaEvent), "an encoder event");
                switch (Mf.EventType(mediaEvent))
                {
                    case Mf.EventNeedInput:
                        // (Asked for while it drains: answered once the drain is done and the stream has started again.)
                        if (!_draining)
                            _needInput++;
                        break;
                    case Mf.EventHaveOutput:
                        _haveOutput++;
                        break;
                    case Mf.EventDrainComplete:
                        // Drained, the encoder is at the end of a stream: a new one starts, and it asks for input again.
                        _ = Mf.ProcessMessage(_transform, Mf.MessageStartOfStream, 0);
                        (_draining, _needInput) = (false, 1);
                        break;
                }
            }
            finally
            {
                Com.Release(ref mediaEvent);
            }
        }

        if (_waiting != 0 && _needInput > 0 && !_draining)
        {
            var hr = Mf.ProcessInput(_transform, 0, _waiting);
            if (hr == Mf.NotAccepting)
            {
                // Not now after all: the frame waits for the next request.
                _needInput = 0;
                return;
            }

            Com.Release(ref _waiting);
            _needInput--;
            _inputsSinceDrain++;
            Check(hr, "IMFTransform::ProcessInput (encoder)");
        }
    }

    /// <summary>An empty sample with one buffer of <paramref name="bytes"/> in the computer's memory.</summary>
    internal static nint Sample(uint bytes)
    {
        nint sample, buffer;
        Check(Mf.MFCreateSample(&sample), "MFCreateSample");
        if (Mf.MFCreateMemoryBuffer(bytes, &buffer) < 0)
        {
            Com.Release(ref sample);
            Check(unchecked((int)0x8007000E), "MFCreateMemoryBuffer");
        }

        var hr = Mf.AddBuffer(sample, buffer);
        Com.Release(ref buffer);
        if (hr < 0)
            Com.Release(ref sample);
        Check(hr, "IMFSample::AddBuffer");
        return sample;
    }

    /// <summary>An HRESULT failure as a GpuException (a lost device among them, which the worker answers by starting again).</summary>
    internal static void Check(int hresult, string what) => Com.Check(hresult, what);

    // An attribute set where the transform takes it; one it refuses is no failure.
    internal static void TrySet(nint attributes, in Guid key, uint value)
    {
        try
        {
            Mf.SetUInt32(attributes, key, value);
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
        }
    }

    public void Dispose()
    {
        Com.Release(ref _waiting);
        if (_transform != 0)
        {
            _ = Mf.ProcessMessage(_transform, Mf.MessageEndOfStream, 0);
            _ = Mf.ProcessMessage(_transform, Mf.MessageEndStreaming, 0);
            _ = Mf.ProcessMessage(_transform, Mf.MessageFlush, 0);
        }

        Com.Release(ref _events);
        Com.Release(ref _codecApi);
        Com.Release(ref _transform);
        if (_activate != 0)
        {
            _ = Mf.ShutdownObject(_activate);
            Com.Release(ref _activate);
        }
    }
}

/// <summary>
/// The decoder of the loopback: a synchronous decoder transform (Windows' own H.264 one, the HEVC and AV1 Store extensions'), low
/// latency so a frame comes out as soon as it went in, writing into the worker's device where it can. Its last picture is in
/// <see cref="LumaSrv"/> / <see cref="ChromaSrv"/>, the encoded size (a decoder's padding, 1088 for 1080, left out).
/// </summary>
internal sealed unsafe class MfDecoderMft : IDisposable
{
    private readonly GpuDevice _gpu;
    private nint _activate, _transform;
    private readonly int _width, _height;
    private bool _providesSamples;
    private uint _outputSize;

    // The decoded picture: one NV12 texture and its two views (from the card), or two plain textures (from memory).
    private GpuTexture? _nv12, _luma, _chroma;
    private nint _lumaSrv, _chromaSrv;

    public nint LumaSrv => _luma?.Srv ?? _lumaSrv;
    public nint ChromaSrv => _chroma?.Srv ?? _chromaSrv;

    /// <summary>The sample time of the picture decoded last (100 ns units); -1 before the first.</summary>
    public long LastTime { get; private set; } = -1;

    /// <summary>Whether the decoder writes into the worker's device (D3D11-aware), not into memory.</summary>
    public bool OnCard { get; private set; }

    private MfDecoderMft(GpuDevice gpu, nint activate, nint transform, int width, int height) =>
        (_gpu, _activate, _transform, _width, _height) = (gpu, activate, transform, width, height);

    /// <summary>The first decoder of the codec that opens; null with <see cref="ExportLoopbackState.NoDecoder"/> (none) or Failed and the HRESULT.</summary>
    public static MfDecoderMft? Create(GpuDevice gpu, Guid codec, int width, int height, double fps, byte[]? sequenceHeader, out ExportLoopbackState state, out int hresult)
    {
        (state, hresult) = (ExportLoopbackState.NoDecoder, 0);
        MediaFoundation.Start();
        var input = new MftRegisterTypeInfo { MajorType = MfGuids.Video, Subtype = codec };
        var output = new MftRegisterTypeInfo { MajorType = MfGuids.Video, Subtype = MfGuids.Nv12 };
        foreach (var narrowed in (bool[])[true, false])
        {
            nint* activates = null;
            uint count = 0;
            try
            {
                var result = Mf.MFTEnumEx(MfGuids.VideoDecoderCategory, Mf.EnumSyncMft | Mf.EnumSortAndFilter, &input, narrowed ? &output : null, &activates, &count);
                if (result < 0 || activates is null)
                    continue;
                for (var i = 0; i < count; i++)
                {
                    var activate = activates[i];
                    if (activate == 0)
                        continue;
                    var decoder = TryStart(gpu, activate, codec, width, height, fps, sequenceHeader, out var failed);
                    if (decoder is not null)
                    {
                        activates[i] = 0;
                        state = ExportLoopbackState.Starting;
                        return decoder;
                    }

                    (state, hresult) = (ExportLoopbackState.Failed, failed);
                }

                if (count > 0)
                    return null;
            }
            finally
            {
                if (activates is not null)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var left = activates[i];
                        Com.Release(ref left);
                    }

                    Com.CoTaskMemFree((nint)activates);
                }
            }
        }

        return null;
    }

    private static MfDecoderMft? TryStart(GpuDevice gpu, nint activate, Guid codec, int width, int height, double fps, byte[]? sequenceHeader, out int failed)
    {
        failed = 0;
        nint transform = 0;
        try
        {
            MfVideoDecoder.Beat?.Invoke();
            var hr = Mf.ActivateTransform(activate, out transform);
            MfVideoDecoder.Beat?.Invoke();
            MfEncoderMft.Check(hr, "IMFActivate::ActivateObject (decoder)");

            var decoder = new MfDecoderMft(gpu, activate, transform, width, height);
            var attributes = Mf.TransformAttributes(transform);
            var aware = false;
            if (attributes != 0)
            {
                aware = Mf.GetUInt32(attributes, MftGuids.D3D11Aware, out var d3d) >= 0 && d3d != 0;
                MfEncoderMft.TrySet(attributes, MfGuids.LowLatency, 1);
                Com.Release(ref attributes);
            }

            // On the worker's own device where the decoder can be (its pictures copied card to card), else in memory.
            if (aware && MediaFoundation.DeviceManagerFor(gpu) is var manager and not 0)
                decoder.OnCard = Mf.ProcessMessage(transform, Mf.MessageSetD3DManager, manager) >= 0;

            nint type;
            MfEncoderMft.Check(Mf.MFCreateMediaType(&type), "MFCreateMediaType");
            try
            {
                var (numerator, denominator) = MfEncoderMft.Rate(fps);
                Mf.SetGuid(type, MfGuids.MajorType, MfGuids.Video);
                Mf.SetGuid(type, MfGuids.Subtype, codec);
                Mf.SetPair(type, MfGuids.FrameSize, (uint)width, (uint)height);
                Mf.SetPair(type, MfGuids.FrameRate, numerator, denominator);
                if (sequenceHeader is { Length: > 0 })
                {
                    fixed (byte* header = sequenceHeader)
                        Mf.SetBlob(type, MftGuids.MpegSequenceHeader, header, (uint)sequenceHeader.Length);
                }

                MfEncoderMft.Check(Mf.SetInputType(transform, 0, type), "IMFTransform::SetInputType (decoder)");
            }
            finally
            {
                Com.Release(ref type);
            }

            MfEncoderMft.Check(decoder.PickOutput(), "IMFTransform::SetOutputType (decoder)");
            MfEncoderMft.Check(Mf.ProcessMessage(transform, Mf.MessageBeginStreaming, 0), "MFT_MESSAGE_NOTIFY_BEGIN_STREAMING (decoder)");
            MfEncoderMft.Check(Mf.ProcessMessage(transform, Mf.MessageStartOfStream, 0), "MFT_MESSAGE_NOTIFY_START_OF_STREAM (decoder)");
            transform = 0;
            return decoder;
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            failed = ex.Code;
            return null;
        }
        finally
        {
            if (transform != 0)
            {
                Com.Release(ref transform);
                _ = Mf.ShutdownObject(activate);
            }
        }
    }

    // NV12 out of the types the decoder lists (again after a stream change), and how its samples come.
    private int PickOutput()
    {
        var result = Mf.TypeNotSet;
        for (uint i = 0; i < 64; i++)
        {
            if (Mf.GetOutputAvailableType(_transform, 0, i, out var listed) < 0)
                break;
            try
            {
                if (Mf.GetGuid(listed, MfGuids.Subtype, out var subtype) >= 0 && subtype == MfGuids.Nv12)
                {
                    result = Mf.SetOutputType(_transform, 0, listed);
                    break;
                }
            }
            finally
            {
                Com.Release(ref listed);
            }
        }

        if (result >= 0 && Mf.GetOutputStreamInfo(_transform, 0, out var info) >= 0)
        {
            _providesSamples = (info.Flags & (MftOutputStreamInfo.ProvidesSamples | MftOutputStreamInfo.CanProvideSamples)) != 0;
            _outputSize = info.Size;
        }

        return result;
    }

    /// <summary>
    /// One bitstream sample in (not let go here), and every picture it gives out read into the planes: true when at least one came
    /// (its time in <see cref="LastTime"/>).
    /// </summary>
    public bool Decode(nint bitstream)
    {
        var hr = Mf.ProcessInput(_transform, 0, bitstream);
        var got = false;
        if (hr == Mf.NotAccepting)
        {
            got = Drain();
            hr = Mf.ProcessInput(_transform, 0, bitstream);
        }

        MfEncoderMft.Check(hr, "IMFTransform::ProcessInput (decoder)");
        return Drain() || got;
    }

    // Every picture the decoder has ready.
    private bool Drain()
    {
        var got = false;
        for (var turns = 0; turns < 8; turns++)
        {
            var buffer = new MftOutputDataBuffer();
            nint own = 0;
            if (!_providesSamples)
            {
                own = MfEncoderMft.Sample(Math.Max(_outputSize, (uint)(_width * _height * 3 / 2)));
                buffer.Sample = own;
            }

            var hr = Mf.ProcessOutput(_transform, ref buffer, out _);
            if (buffer.Events != 0)
                Com.Release(ref buffer.Events);
            if (hr == Mf.NeedMoreInput)
            {
                Com.Release(ref own);
                return got;
            }

            if (hr == Mf.StreamChange)
            {
                Com.Release(ref own);
                MfEncoderMft.Check(PickOutput(), "IMFTransform::SetOutputType (decoder, a stream change)");
                continue;
            }

            if (hr < 0)
            {
                Com.Release(ref own);
                MfEncoderMft.Check(hr, "IMFTransform::ProcessOutput (decoder)");
            }

            var sample = buffer.Sample;
            try
            {
                if (sample != 0 && Read(sample))
                {
                    LastTime = Mf.SampleTime(sample);
                    got = true;
                }
            }
            finally
            {
                if (sample != own)
                    Com.Release(ref own);
                Com.Release(ref sample);
            }
        }

        return got;
    }

    // A decoded sample into the planes: its texture copied on the card, or its memory uploaded.
    private bool Read(nint sample)
    {
        if (Mf.GetBufferByIndex(sample, 0, out var buffer) < 0 || buffer == 0)
            return false;
        try
        {
            var dxgi = Com.Query(buffer, Iid.MfDxgiBuffer);
            if (dxgi != 0)
            {
                try
                {
                    MfEncoderMft.Check(Mf.GetTexture(dxgi, out var texture, out var subresource), "IMFDXGIBuffer::GetResource (decoder)");
                    try
                    {
                        EnsureCardPlanes();
                        var box = new Box { Right = (uint)_width, Bottom = (uint)_height, Back = 1 };
                        _gpu.Context.CopySubresourceRegion(_nv12!.Texture, 0, 0, 0, texture, subresource, &box);
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

                return true;
            }

            return Upload(buffer);
        }
        finally
        {
            Com.Release(ref buffer);
        }
    }

    private void EnsureCardPlanes()
    {
        if (_nv12 is not null)
            return;
        ReleasePlanes();
        _nv12 = _gpu.CreateTexture(_width, _height, DxgiFormat.Nv12, renderTarget: false);
        var luma = new ShaderResourceViewDesc { Format = DxgiFormat.R8Unorm, ViewDimension = D3D11.SrvTexture2D, U0 = 0, U1 = 1 };
        var chroma = new ShaderResourceViewDesc { Format = DxgiFormat.R8G8Unorm, ViewDimension = D3D11.SrvTexture2D, U0 = 0, U1 = 1 };
        _lumaSrv = _gpu.Device.CreateShaderResourceView(_nv12.Texture, &luma);
        _chromaSrv = _gpu.Device.CreateShaderResourceView(_nv12.Texture, &chroma);
    }

    // A picture in memory: NV12 rows at the buffer's pitch, the chroma after the padded luma (the output type's frame size).
    private bool Upload(nint buffer)
    {
        var paddedHeight = _height;
        if (Mf.GetOutputCurrentType(_transform, 0, out var current) >= 0)
        {
            if (Mf.GetUInt64(current, MfGuids.FrameSize, out var size) >= 0)
                paddedHeight = Math.Max((int)(size & 0xFFFFFFFF), _height);
            Com.Release(ref current);
        }

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
            MfEncoderMft.Check(Mf.Lock(buffer, out data, out _), "IMFMediaBuffer::Lock (decoder)");
            pitch = _width;
        }

        try
        {
            if (pitch <= 0)
                return false;
            if (_luma is null)
            {
                ReleasePlanes();
                _luma = _gpu.CreateTexture(_width, _height, DxgiFormat.R8Unorm, renderTarget: false);
                _chroma = _gpu.CreateTexture(_width / 2, _height / 2, DxgiFormat.R8G8Unorm, renderTarget: false);
            }

            _gpu.Context.UpdateSubresource(_luma.Texture, 0, null, data, (uint)pitch, 0);
            _gpu.Context.UpdateSubresource(_chroma!.Texture, 0, null, data + (long)pitch * paddedHeight, (uint)pitch, 0);
            return true;
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

    private void ReleasePlanes()
    {
        Com.Release(ref _lumaSrv);
        Com.Release(ref _chromaSrv);
        _nv12?.Dispose();
        _luma?.Dispose();
        _chroma?.Dispose();
        (_nv12, _luma, _chroma) = (null, null, null);
    }

    public void Dispose()
    {
        if (_transform != 0)
        {
            _ = Mf.ProcessMessage(_transform, Mf.MessageEndOfStream, 0);
            _ = Mf.ProcessMessage(_transform, Mf.MessageEndStreaming, 0);
            _ = Mf.ProcessMessage(_transform, Mf.MessageFlush, 0);
            Com.Release(ref _transform);
        }

        if (_activate != 0)
        {
            _ = Mf.ShutdownObject(_activate);
            Com.Release(ref _activate);
        }

        ReleasePlanes();
    }
}
