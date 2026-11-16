using System.Diagnostics;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Live Encoded Preview codecs: manually driven Media Foundation transforms -----
//
// The export codec as a hardware encoder MFT at the export's rate control and bitrate, plus the decoder that reads the bitstream back
// (ExportLoopbackPreview); software encoders go through FfmpegLoopbackEncoder. The encoder takes system-memory NV12 (like FFmpeg's
// h264_mf), so any adapter's encoder works; the decoder writes into a texture on the worker's device when D3D11-aware, else into memory
// that is uploaded.

/// <summary>
/// Loopback configuration: codec subtype, encoded size, frame rate and rate control, plus preset quality vs. speed, 10-bit, encoder family
/// (ExportLoopbackSettings.Family: which vendor's encoder) and software encoder FFmpeg arguments.
/// </summary>
internal readonly record struct LoopbackConfig(Guid Codec, int Width, int Height, double Fps, int RateControl, int BitrateKbps, int Quality,
    int QualityVsSpeed = 50, bool TenBit = false, int Family = 0, string SoftwareArguments = "");

/// <summary>
/// A loopback encoder: a hardware MFT (<see cref="MfEncoderMft"/>) or a software encoder run through FFmpeg one frame at a time
/// (<see cref="FfmpegLoopbackEncoder"/>). Input is system-memory NV12 (or P010) samples; output is bitstream samples.
/// </summary>
internal interface ILoopbackEncoder : IDisposable
{
    /// <summary>Encoder name shown on the preview (MFT friendly name, or "libx264 (FFmpeg)").</summary>
    string Name { get; }

    /// <summary>True when on the adapter its family names (e.g. QuickSync on the Intel adapter), or when it is the requested software encoder.</summary>
    bool OnAskedCard { get; }

    /// <summary>True when it encodes paused frames only (software encoders: tens to thousands of ms per frame).</summary>
    bool PausedOnly { get; }

    /// <summary>True when it accepts 10-bit input: submitted samples are P010.</summary>
    bool TakesP010 { get; }

    /// <summary>Maximum time a paused frame may stay in the encoder before it is given up.</summary>
    int GiveUpMs { get; }

    void SetQualityVsSpeed(int value);

    void Submit(nint sample, bool forceKey);

    nint TryTakeOutput();

    bool Drain();

    byte[]? SequenceHeader();
}

/// <summary>
/// Hardware encoder MFT (asynchronous, as all hardware MFTs are): events polled without waiting on the worker thread (no callback object),
/// one input per METransformNeedInput, one output per METransformHaveOutput.
/// </summary>
internal sealed unsafe class MfEncoderMft : ILoopbackEncoder
{
    private nint _activate, _transform, _events, _codecApi;
    private readonly bool _providesSamples;
    private readonly uint _outputSize;
    private int _needInput, _haveOutput, _inputsSinceDrain;
    private nint _waiting;
    private bool _draining;
    private uint _qualityVsSpeed;
    private readonly bool _qualityVsSpeedModifiable;

    /// <summary>MFT friendly name shown on the preview.</summary>
    public string Name { get; }

    /// <summary>True when on the adapter (LUID) named by the export's encoder family; false for a stand-in on another adapter.</summary>
    public bool OnAskedCard { get; private init; }

    public bool PausedOnly => false;

    /// <summary>True when input is P010 (10-bit export accepted by the MFT); else NV12.</summary>
    public bool TakesP010 { get; private init; }

    public int GiveUpMs => 2000;

    private MfEncoderMft(nint activate, nint transform, nint events, nint codecApi, string name, MftOutputStreamInfo info, uint qualityVsSpeed)
    {
        (_activate, _transform, _events, _codecApi, Name) = (activate, transform, events, codecApi, name);
        _providesSamples = (info.Flags & (MftOutputStreamInfo.ProvidesSamples | MftOutputStreamInfo.CanProvideSamples)) != 0;
        _outputSize = info.Size;
        _qualityVsSpeed = qualityVsSpeed;
        _qualityVsSpeedModifiable = codecApi != 0 && Mf.IsModifiable(codecApi, CodecApi.QualityVsSpeed);
    }

    /// <summary>
    /// Encoder search order for a family (matching the Export tab's selected encoder): QuickSync, AMF and NVENC on their vendor's adapter
    /// (MFTEnum2 filtered by LUID), then the compositor adapter, then any; Media Foundation and Auto on the compositor adapter, then any. Each
    /// entry has the LUID filter (0 = any) and whether it is the requested adapter. Pure; used by goldens.
    /// </summary>
    internal static List<(long Luid, bool Asked, string Where)> SearchOrder(int family, long compositorLuid, string compositorName, IReadOnlyList<HandPegApp.Services.GpuAdapter> cards)
    {
        var order = new List<(long, bool, string)>();
        var vendor = ExportLoopbackSettings.VendorOf(family);
        if (vendor != 0 && cards.FirstOrDefault(c => c.VendorId == vendor && c.Luid != 0) is { } card)
            order.Add((card.Luid, true, FormattableString.Invariant($"on {card.Name} (LUID 0x{card.Luid:X}, the {ExportLoopbackSettings.FamilyShort(family)} card)")));
        if (compositorLuid != 0 && !order.Exists(o => o.Item1 == compositorLuid))
            order.Add((compositorLuid, vendor == 0, FormattableString.Invariant($"on the compositor's card (LUID 0x{compositorLuid:X}, {compositorName})")));
        order.Add((0, false, "on any card"));
        return order;
    }

    /// <summary>
    /// First hardware encoder for the codec that accepts the settings, searched per <see cref="SearchOrder"/> (family's adapter first). Null
    /// with <see cref="ExportLoopbackState.NoEncoder"/> when none exists, or <see cref="ExportLoopbackState.Failed"/> and the HRESULT when
    /// none started.
    /// </summary>
    public static MfEncoderMft? Create(GpuDevice gpu, in LoopbackConfig config, int qualityVsSpeed, IReadOnlySet<string> refused, out ExportLoopbackState state, out int hresult)
    {
        (state, hresult) = (ExportLoopbackState.NoEncoder, 0);
        MediaFoundation.Start();
        var output = new MftRegisterTypeInfo { MajorType = MfGuids.Video, Subtype = config.Codec };
        var places = SearchOrder(config.Family, gpu.AdapterLuid, gpu.AdapterName, HandPegApp.Services.GpuAdapters.All);
        if (ExportLoopbackSettings.VendorOf(config.Family) != 0 && !places[0].Asked)
            AppLog.Write($"Live Encoded Preview: no {ExportLoopbackSettings.FamilyShort(config.Family)} card in this PC: the compositor's card's encoder stands in.");
        foreach (var (luid, asked, where) in places)
        {
            nint attributes = 0;
            nint* activates = null;
            uint count = 0;
            try
            {
                int result;
                if (luid != 0)
                {
                    if (Mf.MFCreateAttributes(&attributes, 1) < 0)
                        continue;
                    var narrowed = luid;
                    Mf.SetBlob(attributes, MfGuids.EnumAdapterLuid, &narrowed, sizeof(long));
                    result = Mf.MFTEnum2(MfGuids.VideoEncoderCategory, Mf.EnumHardware | Mf.EnumSortAndFilter, null, &output, attributes, &activates, &count);
                }
                else
                {
                    result = Mf.MFTEnumEx(MfGuids.VideoEncoderCategory, Mf.EnumHardware | Mf.EnumSortAndFilter, null, &output, &activates, &count);
                }

                if (result < 0 || activates is null)
                {
                    AppLog.Write(FormattableString.Invariant($"Live Encoded Preview: listing the hardware encoders {where} failed (0x{result:X8})."));
                    continue;
                }

                AppLog.Write(FormattableString.Invariant($"Live Encoded Preview: {count} hardware encoder(s) of the codec {where}."));
                for (var i = 0; i < count; i++)
                {
                    var activate = activates[i];
                    if (activate == 0)
                        continue;
                    var candidate = Mf.GetString(activate, MfGuids.FriendlyName) ?? "a Media Foundation encoder";
                    if (refused.Contains(candidate))
                    {
                        AppLog.Write($"Live Encoded Preview: {candidate} skipped (it failed before).");
                        continue;
                    }

                    var encoder = TryStart(activate, config, qualityVsSpeed, asked, out var failed);
                    if (encoder is not null)
                    {
                        activates[i] = 0; // owned by the encoder (shut down and released with it)
                        state = ExportLoopbackState.Starting;
                        AppLog.Write($"Live Encoded Preview: {candidate} started {where}{(encoder.TakesP010 ? ", 10-bit (P010 in)" : config.TenBit ? ", 8-bit (it would not take 10-bit)" : "")}{(asked ? "" : " (a stand-in: not the card the export's encoder is on)")}.");
                        return encoder;
                    }

                    AppLog.Write(FormattableString.Invariant($"Live Encoded Preview: {candidate} would not start (0x{failed:X8})."));
                    (state, hresult) = (ExportLoopbackState.Failed, failed);
                }
            }
            finally
            {
                // Pointer and count were reset before the call; free only an array the call returned (see HardwareBenchmark.CountMfts).
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

    // Activates and configures one MFT: rate control first (some drivers accept it only before types), output type, input type, then
    // streaming. Returns null with the HRESULT on failure; device loss throws (the worker restarts).
    private static MfEncoderMft? TryStart(nint activate, in LoopbackConfig config, int qualityVsSpeed, bool asked, out int failed)
    {
        failed = 0;
        nint transform = 0, events = 0, codecApi = 0;
        try
        {
            MfVideoDecoder.Beat?.Invoke();
            var hr = Mf.ActivateTransform(activate, out transform);
            MfVideoDecoder.Beat?.Invoke();
            Check(hr, "IMFActivate::ActivateObject (encoder)");

            // Async MFTs ignore calls until unlocked.
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

            // 10-bit: Main10 output (HEVC; AV1 Main already covers 10-bit) with P010 input where accepted; else 8-bit (H.264 High 10
            // is never requested: no hardware encoder supports it).
            var (rateNumerator, rateDenominator) = Rate(config.Fps);
            var p010 = config.TenBit && config.Codec != MfGuids.H264
                       && SetOutputResult(transform, config, bits, rateNumerator, rateDenominator, withProfile: true, tenBit: true) >= 0
                       && SetInput(transform, config, rateNumerator, rateDenominator, MfGuids.P010) >= 0;
            if (!p010)
            {
                if (!SetOutput(transform, config, bits, rateNumerator, rateDenominator, withProfile: true))
                    Check(SetOutputResult(transform, config, bits, rateNumerator, rateDenominator, withProfile: false), "IMFTransform::SetOutputType (encoder)");
                Check(SetInput(transform, config, rateNumerator, rateDenominator, MfGuids.Nv12), "IMFTransform::SetInputType (encoder)");
            }

            Check(Mf.GetOutputStreamInfo(transform, 0, out var info), "IMFTransform::GetOutputStreamInfo (encoder)");

            events = Com.Query(transform, MfIid.MediaEventGenerator);
            Check(Mf.ProcessMessage(transform, Mf.MessageBeginStreaming, 0), "MFT_MESSAGE_NOTIFY_BEGIN_STREAMING (encoder)");
            Check(Mf.ProcessMessage(transform, Mf.MessageStartOfStream, 0), "MFT_MESSAGE_NOTIFY_START_OF_STREAM (encoder)");
            var name = Mf.GetString(activate, MfGuids.FriendlyName) ?? "a Media Foundation encoder";
            var encoder = new MfEncoderMft(activate, transform, events, codecApi, name, info, (uint)Math.Clamp(qualityVsSpeed, 0, 100)) { OnAskedCard = asked, TakesP010 = p010 };
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

    // eAVEncH265VProfile_Main_420_10: HEVC Main10 for 10-bit exports.
    private const uint HevcMain10 = 2;

    private static int SetOutputResult(nint transform, in LoopbackConfig config, uint bits, uint numerator, uint denominator, bool withProfile, bool tenBit = false)
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
            if (tenBit && config.Codec == MfGuids.Hevc)
                Mf.SetUInt32(type, MftGuids.Mpeg2Profile, HevcMain10);
            return Mf.SetOutputType(transform, 0, type);
        }
        finally
        {
            Com.Release(ref type);
        }
    }

    // NV12 (or P010) input at size and rate: prefer the MFT's own listed type (may carry driver-specific attributes), else build one.
    private static int SetInput(nint transform, in LoopbackConfig config, uint numerator, uint denominator, Guid wanted)
    {
        for (uint i = 0; i < 64; i++)
        {
            if (Mf.GetInputAvailableType(transform, 0, i, out var listed) < 0)
                break;
            try
            {
                if (Mf.GetGuid(listed, MfGuids.Subtype, out var subtype) < 0 || subtype != wanted)
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
            Mf.SetGuid(type, MfGuids.Subtype, wanted);
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

    /// <summary>Frame rate as an MF ratio: integer rates n/1, NTSC rates n*1000/1001, others in thousandths.</summary>
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

    /// <summary>Sequence header (SPS/PPS etc.) from the encoder's current output type once streaming; null when absent.</summary>
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
    /// Updates quality vs. speed while streaming when the driver allows it (playing frames encode faster than paused ones, unlike the
    /// export preset).
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
    /// Submits a frame (takes ownership of the sample). Fed immediately if the encoder requested input; otherwise it replaces any waiting
    /// frame (newest picture only, matching the worker). <paramref name="forceKey"/> requests a key frame.
    /// </summary>
    public void Submit(nint sample, bool forceKey)
    {
        if (forceKey && _codecApi != 0)
            _ = Mf.SetCodecValue(_codecApi, CodecApi.ForceKeyFrame, 1);
        Com.Release(ref _waiting);
        _waiting = sample;
        Pump();
    }

    /// <summary>Returns a finished bitstream sample (caller releases it), or 0 when none is ready.</summary>
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
            // Output type changed (rare): adopt the first listed type and retry on the next call.
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
    /// Drains the encoder (releases a paused frame held for look-ahead); new input waits for METransformDrainComplete, then the stream
    /// restarts. True once the last input is drained or draining; false while it has not been fed yet (retried on the next poll).
    /// </summary>
    public bool Drain()
    {
        // Not while a frame waits to be fed (an earlier drain is pending; it is fed and drained afterwards); nothing to do when nothing
        // was fed since the last drain.
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

    // Processes queued events (never waits), then feeds the waiting frame when allowed.
    private void Pump()
    {
        if (_events == 0)
        {
            // Synchronous MFT (unexpected for hardware): feed the frame when accepted.
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
                        // Requests during a drain are answered after the drain completes and the stream restarts.
                        if (!_draining)
                            _needInput++;
                        break;
                    case Mf.EventHaveOutput:
                        _haveOutput++;
                        break;
                    case Mf.EventDrainComplete:
                        // Drained to end of stream: start a new stream; the encoder requests input again.
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
                // Rejected: keep the frame for the next request.
                _needInput = 0;
                return;
            }

            Com.Release(ref _waiting);
            _needInput--;
            _inputsSinceDrain++;
            Check(hr, "IMFTransform::ProcessInput (encoder)");
        }
    }

    /// <summary>Creates an empty sample with one system-memory buffer of <paramref name="bytes"/>.</summary>
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

    /// <summary>Throws GpuException on HRESULT failure (including device loss, which restarts the worker).</summary>
    internal static void Check(int hresult, string what) => Com.Check(hresult, what);

    // Sets an attribute if supported; refusal is not a failure.
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
        HandPegApp.Services.GpuNativeBreadcrumbs.Mark("MFT.Dispose", "encoder " + Name);
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
/// Loopback decoder: a synchronous decoder MFT (Windows' H.264, HEVC/AV1 Store extensions) in low-latency mode so each frame comes out as
/// soon as it goes in, writing into the worker's device when possible. The last picture is in <see cref="LumaSrv"/> /
/// <see cref="ChromaSrv"/> at the encoded size (decoder padding, e.g. 1088 for 1080, excluded).
/// </summary>
internal sealed unsafe class MfDecoderMft : IDisposable
{
    private readonly GpuDevice _gpu;
    private nint _activate, _transform;
    private readonly int _width, _height;
    private bool _providesSamples;
    private uint _outputSize;

    // P010 output (10-bit stream): read through memory, each sample's top 8 bits (rounded) uploaded as 8-bit planes.
    private bool _p010;
    private byte[]? _eightBit;

    // Decoded picture: one NV12 texture with two views (GPU output), or two plain textures (memory output).
    private GpuTexture? _nv12, _luma, _chroma;
    private nint _lumaSrv, _chromaSrv;

    public nint LumaSrv => _luma?.Srv ?? _lumaSrv;
    public nint ChromaSrv => _chroma?.Srv ?? _chromaSrv;

    /// <summary>Sample time of the last decoded picture (100 ns); -1 before the first.</summary>
    public long LastTime { get; private set; } = -1;

    /// <summary>True when the decoder writes into the worker's device (D3D11-aware) rather than system memory.</summary>
    public bool OnCard { get; private set; }

    private MfDecoderMft(GpuDevice gpu, nint activate, nint transform, int width, int height) =>
        (_gpu, _activate, _transform, _width, _height) = (gpu, activate, transform, width, height);

    /// <summary>First decoder for the codec that opens; null with <see cref="ExportLoopbackState.NoDecoder"/> (none) or Failed and the HRESULT.</summary>
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
                AppLog.Write(FormattableString.Invariant($"Live Encoded Preview: {(result < 0 ? 0 : count)} decoder(s) of the codec{(narrowed ? " to NV12" : "")} (0x{result:X8})."));
                if (result < 0 || activates is null)
                    continue;
                for (var i = 0; i < count; i++)
                {
                    var activate = activates[i];
                    if (activate == 0)
                        continue;
                    var candidate = Mf.GetString(activate, MfGuids.FriendlyName) ?? "a Media Foundation decoder";
                    var decoder = TryStart(gpu, activate, codec, width, height, fps, sequenceHeader, out var failed);
                    if (decoder is not null)
                    {
                        activates[i] = 0;
                        state = ExportLoopbackState.Starting;
                        AppLog.Write($"Live Encoded Preview: decoder {candidate} started ({(decoder.OnCard ? "on the compositor's card" : "in memory")}).");
                        return decoder;
                    }

                    AppLog.Write(FormattableString.Invariant($"Live Encoded Preview: decoder {candidate} would not start (0x{failed:X8})."));
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

            // Decode on the worker's device when supported (GPU-to-GPU copies), else into system memory.
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

    // Selects NV12 output from the decoder's listed types (again after a stream change) and records sample allocation. A 10-bit decoder may
    // list only P010 (HEVC Main10): then pictures are read through memory and reduced to 8 bits (Upload) to match the compare planes.
    private int PickOutput()
    {
        var result = Mf.TypeNotSet;
        foreach (var wanted in (Guid[])[MfGuids.Nv12, MfGuids.P010])
        {
            for (uint i = 0; i < 64 && result == Mf.TypeNotSet; i++)
            {
                if (Mf.GetOutputAvailableType(_transform, 0, i, out var listed) < 0)
                    break;
                try
                {
                    if (Mf.GetGuid(listed, MfGuids.Subtype, out var subtype) >= 0 && subtype == wanted)
                    {
                        result = Mf.SetOutputType(_transform, 0, listed);
                        if (result >= 0)
                            _p010 = wanted == MfGuids.P010;
                        else
                            result = Mf.TypeNotSet;
                    }
                }
                finally
                {
                    Com.Release(ref listed);
                }
            }

            if (result != Mf.TypeNotSet)
                break;
        }

        if (result >= 0 && Mf.GetOutputStreamInfo(_transform, 0, out var info) >= 0)
        {
            _providesSamples = (info.Flags & (MftOutputStreamInfo.ProvidesSamples | MftOutputStreamInfo.CanProvideSamples)) != 0;
            _outputSize = info.Size;
        }

        return result;
    }

    /// <summary>
    /// Feeds one bitstream sample (not released here) and reads every resulting picture into the planes; true when at least one arrived
    /// (time in <see cref="LastTime"/>).
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

    /// <summary>
    /// Releases a paused frame held in the decoder's one-picture reorder buffer (HEVC and AV1 extensions) via MFT_MESSAGE_COMMAND_DRAIN and
    /// reads every picture it outputs; true when one arrived. The decoder accepts input afterwards (ExportLoopbackPreview forces a key frame
    /// after a drain).
    /// </summary>
    public bool DrainHeld()
    {
        HandPegApp.Services.GpuNativeBreadcrumbs.Mark("MFT.Drain", "decoder");
        if (Mf.ProcessMessage(_transform, Mf.MessageDrain, 0) < 0)
            return false;
        return Drain();
    }

    // Reads every picture the decoder has ready.
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

    // Copies a decoded sample into the planes: GPU texture copy, or system-memory upload.
    private bool Read(nint sample)
    {
        if (Mf.GetBufferByIndex(sample, 0, out var buffer) < 0 || buffer == 0)
            return false;
        try
        {
            // P010 GPU pictures are also read through memory (IMFMediaBuffer::Lock copies out) since the planes are 8-bit.
            var dxgi = _p010 ? 0 : Com.Query(buffer, Iid.MfDxgiBuffer);
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

    // Uploads a system-memory picture: NV12 rows at the buffer pitch, chroma after the padded luma (output type frame size).
    private bool Upload(nint buffer)
    {
        var paddedHeight = _height;
        if (Mf.GetOutputCurrentType(_transform, 0, out var current) >= 0)
        {
            if (Mf.GetUInt64(current, MfGuids.FrameSize, out var size) >= 0)
                paddedHeight = Math.Max((int)(size & 0xFFFFFFFF), _height);
            Com.Release(ref current);
        }

        // The queried 2D interface is owned from here: released whichever lock (if any) succeeds, only the held lock undone.
        var buffer2D = Com.Query(buffer, Iid.Mf2DBuffer);
        bool locked2D = false, lockedPlain = false;
        try
        {
            byte* data;
            int pitch;
            if (buffer2D != 0 && Mf.Lock2D(buffer2D, out data, out pitch) >= 0)
            {
                locked2D = true;
            }
            else
            {
                MfEncoderMft.Check(Mf.Lock(buffer, out data, out _), "IMFMediaBuffer::Lock (decoder)");
                lockedPlain = true;
                pitch = _width;
            }

            if (pitch <= 0)
                return false;
            if (_luma is null)
            {
                ReleasePlanes();
                _luma = _gpu.CreateTexture(_width, _height, DxgiFormat.R8Unorm, renderTarget: false);
                _chroma = _gpu.CreateTexture(_width / 2, _height / 2, DxgiFormat.R8G8Unorm, renderTarget: false);
            }

            if (_p010)
            {
                // 16-bit samples with 10 bits at the top: take each top byte, rounded, into an encoded-size NV12 frame (2D buffer pitch is
                // in bytes; a plain buffer's is twice the width).
                var rowBytes = locked2D ? pitch : _width * 2;
                _eightBit ??= new byte[_width * _height * 3 / 2];
                fixed (byte* eight = _eightBit)
                {
                    for (var y = 0; y < _height; y++)
                        TopBytes((ushort*)(data + (long)y * rowBytes), eight + (long)y * _width, _width);
                    var chroma = data + (long)rowBytes * paddedHeight;
                    for (var y = 0; y < _height / 2; y++)
                        TopBytes((ushort*)(chroma + (long)y * rowBytes), eight + (long)_width * _height + (long)y * _width, _width);
                    _gpu.Context.UpdateSubresource(_luma.Texture, 0, null, eight, (uint)_width, 0);
                    _gpu.Context.UpdateSubresource(_chroma!.Texture, 0, null, eight + (long)_width * _height, (uint)_width, 0);
                }

                return true;
            }

            _gpu.Context.UpdateSubresource(_luma.Texture, 0, null, data, (uint)pitch, 0);
            _gpu.Context.UpdateSubresource(_chroma!.Texture, 0, null, data + (long)pitch * paddedHeight, (uint)pitch, 0);
            return true;
        }
        finally
        {
            if (locked2D)
                _ = Mf.Unlock2D(buffer2D);
            else if (lockedPlain)
                _ = Mf.Unlock(buffer);
            Com.Release(ref buffer2D);
        }
    }

    /// <summary>Converts a row of P010 samples (10 bits in the top of 16) to 8-bit by rounding the top byte. Pure; used by goldens.</summary>
    internal static void TopBytes(ushort* from, byte* to, int count)
    {
        for (var i = 0; i < count; i++)
            to[i] = (byte)Math.Min((from[i] + 0x80) >> 8, 255);
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
        HandPegApp.Services.GpuNativeBreadcrumbs.Mark("MFT.Dispose", "decoder");
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
