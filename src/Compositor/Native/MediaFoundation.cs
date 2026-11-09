using System.Runtime.InteropServices;

namespace HandPegApp.Compositor.Native;

// ----- Media Foundation: the Source Reader decoding into Direct3D 11 textures (Pass 5.0-Core) -----

internal static class MfGuids
{
    public static readonly Guid SourceReaderD3DManager = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid DisableDxva = new("aa456cfd-3943-4a1e-a77d-1838c0ea2e35");
    public static readonly Guid EnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    public static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    public static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    public static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    public static readonly Guid Duration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid P010 = new("30313050-0000-0010-8000-00aa00389b71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid Hevc = new("43564548-0000-0010-8000-00aa00389b71");
    public static readonly Guid Av1 = new("31305641-0000-0010-8000-00aa00389b71");
    public static readonly Guid Vp9 = new("30395056-0000-0010-8000-00aa00389b71");
    public static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid VideoDecoderCategory = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    public static readonly Guid EnumAdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
}

/// <summary>MFVideoArea: offsets (16.16 fixed) and the size.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MfVideoArea
{
    public ushort OffsetXFract;
    public short OffsetX;
    public ushort OffsetYFract;
    public short OffsetY;
    public int Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftRegisterTypeInfo
{
    public Guid MajorType, Subtype;
}

/// <summary>A PROPVARIANT holding a 64-bit integer (24 bytes on 64-bit Windows).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public long Value;

    public const ushort I8 = 20, UI8 = 21;
}

internal static unsafe partial class Mf
{
    public const uint Version = 0x00020070;
    public const uint StartupNoSocket = 1;
    public const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    public const uint ReaderError = 0x1, ReaderEndOfStream = 0x2, ReaderNewStream = 0x4, ReaderCurrentMediaTypeChanged = 0x20, ReaderStreamTick = 0x100;
    public const uint EnumHardware = 0x4, EnumSortAndFilter = 0x40;

    /// <summary>MFT_ENUM_FLAG_ALL (synchronous, asynchronous, hardware, field-of-use, local, transcode-only): every kind of transform.</summary>
    public const uint EnumAll = 0x3F;

    [LibraryImport("mfplat.dll")]
    public static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    public static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(nint* attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(nint* mediaType);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateDXGIDeviceManager(uint* resetToken, nint* manager);

    [LibraryImport("mfplat.dll")]
    public static partial int MFTEnumEx(Guid category, uint flags, MftRegisterTypeInfo* input, MftRegisterTypeInfo* output, nint** activates, uint* count);

    /// <summary>MFTEnum2: the MFTs of a category, narrowed by attributes (MFT_ENUM_ADAPTER_LUID: one card's hardware MFTs).</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFTEnum2(Guid category, uint flags, MftRegisterTypeInfo* input, MftRegisterTypeInfo* output, nint attributes, nint** activates, uint* count);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MFCreateSourceReaderFromURL(string url, nint attributes, nint* reader);

    // ----- IMFAttributes (also IMFMediaType, IMFSample, IMFActivate) -----

    public static int GetUInt32(nint attributes, in Guid key, out uint value)
    {
        uint result = 0;
        int hr;
        fixed (Guid* k = &key)
            hr = ((delegate* unmanaged<nint, Guid*, uint*, int>)Com.Table(attributes)[7])(attributes, k, &result);
        value = result;
        return hr;
    }

    public static int GetUInt64(nint attributes, in Guid key, out ulong value)
    {
        ulong result = 0;
        int hr;
        fixed (Guid* k = &key)
            hr = ((delegate* unmanaged<nint, Guid*, ulong*, int>)Com.Table(attributes)[8])(attributes, k, &result);
        value = result;
        return hr;
    }

    public static int GetGuid(nint attributes, in Guid key, out Guid value)
    {
        Guid result = default;
        int hr;
        fixed (Guid* k = &key)
            hr = ((delegate* unmanaged<nint, Guid*, Guid*, int>)Com.Table(attributes)[10])(attributes, k, &result);
        value = result;
        return hr;
    }

    public static int GetBlob(nint attributes, in Guid key, void* buffer, uint size)
    {
        uint written;
        fixed (Guid* k = &key)
            return ((delegate* unmanaged<nint, Guid*, void*, uint, uint*, int>)Com.Table(attributes)[15])(attributes, k, buffer, size, &written);
    }

    public static string? GetString(nint attributes, in Guid key)
    {
        nint text;
        uint length;
        fixed (Guid* k = &key)
        {
            if (((delegate* unmanaged<nint, Guid*, nint*, uint*, int>)Com.Table(attributes)[13])(attributes, k, &text, &length) < 0)
                return null;
        }

        try
        {
            return new string((char*)text, 0, (int)length);
        }
        finally
        {
            Com.CoTaskMemFree(text);
        }
    }

    public static void SetUInt32(nint attributes, in Guid key, uint value)
    {
        fixed (Guid* k = &key)
            Com.Check(((delegate* unmanaged<nint, Guid*, uint, int>)Com.Table(attributes)[21])(attributes, k, value), "IMFAttributes::SetUINT32");
    }

    public static void SetGuid(nint attributes, in Guid key, in Guid value)
    {
        fixed (Guid* k = &key)
        fixed (Guid* v = &value)
            Com.Check(((delegate* unmanaged<nint, Guid*, Guid*, int>)Com.Table(attributes)[24])(attributes, k, v), "IMFAttributes::SetGUID");
    }

    public static void SetBlob(nint attributes, in Guid key, void* data, uint size)
    {
        fixed (Guid* k = &key)
            Com.Check(((delegate* unmanaged<nint, Guid*, void*, uint, int>)Com.Table(attributes)[26])(attributes, k, data, size), "IMFAttributes::SetBlob");
    }

    public static void SetUnknown(nint attributes, in Guid key, nint value)
    {
        fixed (Guid* k = &key)
            Com.Check(((delegate* unmanaged<nint, Guid*, nint, int>)Com.Table(attributes)[27])(attributes, k, value), "IMFAttributes::SetUnknown");
    }

    // ----- IMFDXGIDeviceManager -----

    public static void ResetDevice(nint manager, nint device, uint resetToken) =>
        Com.Check(((delegate* unmanaged<nint, nint, uint, int>)Com.Table(manager)[7])(manager, device, resetToken), "IMFDXGIDeviceManager::ResetDevice");

    // ----- IMFSourceReader -----

    public static int SetStreamSelection(nint reader, uint stream, bool selected) =>
        ((delegate* unmanaged<nint, uint, int, int>)Com.Table(reader)[4])(reader, stream, selected ? 1 : 0);

    public static int GetNativeMediaType(nint reader, uint stream, uint index, out nint mediaType)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, uint, uint, nint*, int>)Com.Table(reader)[5])(reader, stream, index, &result);
        mediaType = result;
        return hr;
    }

    public static int GetCurrentMediaType(nint reader, uint stream, out nint mediaType)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Table(reader)[6])(reader, stream, &result);
        mediaType = result;
        return hr;
    }

    public static int SetCurrentMediaType(nint reader, uint stream, nint mediaType) =>
        ((delegate* unmanaged<nint, uint, uint*, nint, int>)Com.Table(reader)[7])(reader, stream, null, mediaType);

    /// <summary>Seeks to a moment in 100 ns units (the reader goes to the key frame at or before it).</summary>
    public static int SetCurrentPosition(nint reader, long ticks)
    {
        var position = new PropVariant { Type = PropVariant.I8, Value = ticks };
        var format = Guid.Empty;
        return ((delegate* unmanaged<nint, Guid*, PropVariant*, int>)Com.Table(reader)[8])(reader, &format, &position);
    }

    public static int ReadSample(nint reader, uint stream, out uint flags, out long timestamp, out nint sample)
    {
        uint actual, f;
        long time;
        nint s = 0;
        var hr = ((delegate* unmanaged<nint, uint, uint, uint*, uint*, long*, nint*, int>)Com.Table(reader)[9])(reader, stream, 0, &actual, &f, &time, &s);
        flags = f;
        timestamp = time;
        sample = s;
        return hr;
    }

    /// <summary>IMFSourceReader::ReadSample over every selected stream: <paramref name="stream"/> says whose sample it is.</summary>
    public static int ReadSampleAny(nint reader, out uint stream, out uint flags, out long timestamp, out nint sample)
    {
        uint actual, f;
        long time;
        nint s = 0;
        var hr = ((delegate* unmanaged<nint, uint, uint, uint*, uint*, long*, nint*, int>)Com.Table(reader)[9])(reader, AllStreams, 0, &actual, &f, &time, &s);
        (stream, flags, timestamp, sample) = (actual, f, time, s);
        return hr;
    }

    public static int Flush(nint reader, uint stream) => ((delegate* unmanaged<nint, uint, int>)Com.Table(reader)[10])(reader, stream);

    /// <summary>The source's length in 100 ns units (MF_PD_DURATION), 0 when it does not say.</summary>
    public static long Duration(nint reader)
    {
        var value = default(PropVariant);
        var key = MfGuids.Duration;
        var hr = ((delegate* unmanaged<nint, uint, Guid*, PropVariant*, int>)Com.Table(reader)[12])(reader, MediaSource, &key, &value);
        return hr >= 0 && value.Type == PropVariant.UI8 ? value.Value : 0;
    }

    // ----- IMFSample and its buffers -----

    public static int GetBufferByIndex(nint sample, uint index, out nint buffer)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Table(sample)[40])(sample, index, &result);
        buffer = result;
        return hr;
    }

    public static long SampleTime(nint sample)
    {
        long time = 0;
        ((delegate* unmanaged<nint, long*, int>)Com.Table(sample)[35])(sample, &time);
        return time;
    }

    public static long SampleDuration(nint sample)
    {
        long time = 0;
        ((delegate* unmanaged<nint, long*, int>)Com.Table(sample)[37])(sample, &time);
        return time;
    }

    /// <summary>IMFDXGIBuffer::GetResource (as an ID3D11Texture2D) and GetSubresourceIndex.</summary>
    public static int GetTexture(nint dxgiBuffer, out nint texture, out uint subresource)
    {
        nint result = 0;
        uint index = 0;
        var id = Iid.D3D11Texture2D;
        var hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Table(dxgiBuffer)[3])(dxgiBuffer, &id, &result);
        if (hr >= 0)
            hr = ((delegate* unmanaged<nint, uint*, int>)Com.Table(dxgiBuffer)[4])(dxgiBuffer, &index);
        texture = result;
        subresource = index;
        return hr;
    }

    /// <summary>IMF2DBuffer::Lock2D: the first row and the pitch (which may be negative for bottom-up pictures).</summary>
    public static int Lock2D(nint buffer2D, out byte* scanline0, out int pitch)
    {
        byte* line;
        int p;
        var hr = ((delegate* unmanaged<nint, byte**, int*, int>)Com.Table(buffer2D)[3])(buffer2D, &line, &p);
        scanline0 = line;
        pitch = p;
        return hr;
    }

    public static int Unlock2D(nint buffer2D) => ((delegate* unmanaged<nint, int>)Com.Table(buffer2D)[4])(buffer2D);

    /// <summary>IMFMediaBuffer::Lock (a buffer without the 2D interface).</summary>
    public static int Lock(nint buffer, out byte* data, out uint length)
    {
        byte* d;
        uint max, current;
        var hr = ((delegate* unmanaged<nint, byte**, uint*, uint*, int>)Com.Table(buffer)[3])(buffer, &d, &max, &current);
        data = d;
        length = current;
        return hr;
    }

    public static int Unlock(nint buffer) => ((delegate* unmanaged<nint, int>)Com.Table(buffer)[4])(buffer);
}

// ----- Media Foundation transforms driven directly: the Export Quality Preview's encoder and decoder (Pass 5.4B) -----
//
// IMFTransform, ICodecAPI, IMFMediaEventGenerator, IMFActivate and the sample and buffer calls the encode loopback needs
// (ExportLoopbackPreview, MfLoopbackCodec). Every GUID, value and method place below was checked for this pass against the
// Windows SDK 10.0.26100 headers (mftransform.h, mfapi.h, mfobjects.h, codecapi.h, strmif.h, mferror.h): IUnknown is 0-2,
// IMFAttributes 3-32, then each interface's own methods in declaration order.

/// <summary>The interface ids of the transforms (mftransform.h, strmif.h, mfobjects.h).</summary>
internal static class MfIid
{
    public static readonly Guid Transform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    public static readonly Guid CodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static readonly Guid MediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
}

/// <summary>The attributes of a transform and of its media types (mftransform.h, mfapi.h).</summary>
internal static class MftGuids
{
    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid D3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    /// <summary>MFVideoInterlace_Progressive.</summary>
    public const uint Progressive = 2;

    /// <summary>eAVEncH264VProfile_High.</summary>
    public const uint H264High = 100;
}

/// <summary>The encoder settings (codecapi.h): each a VT_UI4 unless said.</summary>
internal static class CodecApi
{
    /// <summary>eAVEncCommonRateControlMode: 0 CBR, 1 peak-constrained VBR, 2 unconstrained VBR, 3 quality, 4 low-delay VBR.</summary>
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");

    /// <summary>0 (smallest) to 100 (best): the quality mode's target.</summary>
    public static readonly Guid Quality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");

    /// <summary>0 (fastest) to 100 (best quality): the nearest a hardware encoder has to a preset.</summary>
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    /// <summary>VT_BOOL; the same GUID as MF_LOW_LATENCY (<see cref="MfGuids.LowLatency"/>).</summary>
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid DefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    /// <summary>1: the next frame is a key frame.</summary>
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const ushort VtBool = 11, VtUi4 = 19;
}

/// <summary>MFT_OUTPUT_STREAM_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint Flags, Size, Alignment;

    /// <summary>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES, MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES.</summary>
    public const uint ProvidesSamples = 0x100, CanProvideSamples = 0x200;
}

/// <summary>MFT_OUTPUT_DATA_BUFFER (32 bytes on 64-bit Windows).</summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct MftOutputDataBuffer
{
    [FieldOffset(0)] public uint StreamId;
    [FieldOffset(8)] public nint Sample;
    [FieldOffset(16)] public uint Status;
    [FieldOffset(24)] public nint Events;
}

internal static unsafe partial class Mf
{
    /// <summary>MFT_ENUM_FLAG_SYNCMFT, MFT_ENUM_FLAG_ASYNCMFT (mfapi.h).</summary>
    public const uint EnumSyncMft = 0x1, EnumAsyncMft = 0x2;

    /// <summary>MFT_MESSAGE_TYPE (mftransform.h).</summary>
    public const uint MessageFlush = 0, MessageDrain = 1, MessageSetD3DManager = 2, MessageBeginStreaming = 0x10000000,
        MessageEndStreaming = 0x10000001, MessageEndOfStream = 0x10000002, MessageStartOfStream = 0x10000003;

    /// <summary>MediaEventType (mfobjects.h): METransformUnknown is 600.</summary>
    public const uint EventNeedInput = 601, EventHaveOutput = 602, EventDrainComplete = 603;

    /// <summary>MF_EVENT_FLAG_NO_WAIT.</summary>
    public const uint EventNoWait = 1;

    /// <summary>The HRESULTs a transform answers with (mferror.h).</summary>
    public const int NeedMoreInput = unchecked((int)0xC00D6D72), StreamChange = unchecked((int)0xC00D6D61), NotAccepting = unchecked((int)0xC00D36B5),
        NoEventsAvailable = unchecked((int)0xC00D3E80), TypeNotSet = unchecked((int)0xC00D6D60), NoMoreTypes = unchecked((int)0xC00D36B9);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateSample(nint* sample);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMemoryBuffer(uint maxLength, nint* buffer);

    // ----- IMFAttributes (more) -----

    public static void SetUInt64(nint attributes, in Guid key, ulong value)
    {
        fixed (Guid* k = &key)
            Com.Check(((delegate* unmanaged<nint, Guid*, ulong, int>)Com.Table(attributes)[22])(attributes, k, value), "IMFAttributes::SetUINT64");
    }

    /// <summary>Two 32-bit halves packed as MFSetAttributeSize / MFSetAttributeRatio do: the first in the high half.</summary>
    public static void SetPair(nint attributes, in Guid key, uint high, uint low) => SetUInt64(attributes, key, ((ulong)high << 32) | low);

    /// <summary>IMFAttributes::GetBlobSize then GetBlob: the bytes of a blob attribute, null when it is not there.</summary>
    public static byte[]? GetBlobBytes(nint attributes, in Guid key)
    {
        uint size;
        fixed (Guid* k = &key)
        {
            if (((delegate* unmanaged<nint, Guid*, uint*, int>)Com.Table(attributes)[14])(attributes, k, &size) < 0 || size == 0)
                return null;
        }

        var bytes = new byte[size];
        fixed (byte* b = bytes)
            return GetBlob(attributes, key, b, size) < 0 ? null : bytes;
    }

    // ----- IMFActivate -----

    /// <summary>IMFActivate::ActivateObject for IMFTransform.</summary>
    public static int ActivateTransform(nint activate, out nint transform)
    {
        nint result = 0;
        var id = MfIid.Transform;
        var hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Table(activate)[33])(activate, &id, &result);
        transform = result;
        return hr;
    }

    public static int ShutdownObject(nint activate) => ((delegate* unmanaged<nint, int>)Com.Table(activate)[34])(activate);

    // ----- IMFTransform -----

    public static int GetOutputStreamInfo(nint transform, uint stream, out MftOutputStreamInfo info)
    {
        MftOutputStreamInfo result;
        var hr = ((delegate* unmanaged<nint, uint, MftOutputStreamInfo*, int>)Com.Table(transform)[7])(transform, stream, &result);
        info = result;
        return hr;
    }

    /// <summary>IMFTransform::GetAttributes: the transform's own attributes (0 when it has none).</summary>
    public static nint TransformAttributes(nint transform)
    {
        nint result = 0;
        return ((delegate* unmanaged<nint, nint*, int>)Com.Table(transform)[8])(transform, &result) < 0 ? 0 : result;
    }

    public static int GetInputAvailableType(nint transform, uint stream, uint index, out nint mediaType)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, uint, uint, nint*, int>)Com.Table(transform)[13])(transform, stream, index, &result);
        mediaType = result;
        return hr;
    }

    public static int GetOutputAvailableType(nint transform, uint stream, uint index, out nint mediaType)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, uint, uint, nint*, int>)Com.Table(transform)[14])(transform, stream, index, &result);
        mediaType = result;
        return hr;
    }

    public static int SetInputType(nint transform, uint stream, nint mediaType) =>
        ((delegate* unmanaged<nint, uint, nint, uint, int>)Com.Table(transform)[15])(transform, stream, mediaType, 0);

    public static int SetOutputType(nint transform, uint stream, nint mediaType) =>
        ((delegate* unmanaged<nint, uint, nint, uint, int>)Com.Table(transform)[16])(transform, stream, mediaType, 0);

    public static int GetOutputCurrentType(nint transform, uint stream, out nint mediaType)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Table(transform)[18])(transform, stream, &result);
        mediaType = result;
        return hr;
    }

    public static int ProcessMessage(nint transform, uint message, nint parameter) =>
        ((delegate* unmanaged<nint, uint, nint, int>)Com.Table(transform)[23])(transform, message, parameter);

    public static int ProcessInput(nint transform, uint stream, nint sample) =>
        ((delegate* unmanaged<nint, uint, nint, uint, int>)Com.Table(transform)[24])(transform, stream, sample, 0);

    /// <summary>IMFTransform::ProcessOutput of one stream; <paramref name="buffer"/>'s sample and events come back in it.</summary>
    public static int ProcessOutput(nint transform, ref MftOutputDataBuffer buffer, out uint status)
    {
        uint result = 0;
        int hr;
        fixed (MftOutputDataBuffer* b = &buffer)
            hr = ((delegate* unmanaged<nint, uint, uint, MftOutputDataBuffer*, uint*, int>)Com.Table(transform)[25])(transform, 0, 1, b, &result);
        status = result;
        return hr;
    }

    // ----- IMFMediaEventGenerator and IMFMediaEvent -----

    /// <summary>IMFMediaEventGenerator::GetEvent without waiting: <see cref="NoEventsAvailable"/> when none is queued.</summary>
    public static int GetEventNoWait(nint generator, out nint mediaEvent)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Table(generator)[3])(generator, EventNoWait, &result);
        mediaEvent = result;
        return hr;
    }

    public static uint EventType(nint mediaEvent)
    {
        uint type = 0;
        ((delegate* unmanaged<nint, uint*, int>)Com.Table(mediaEvent)[33])(mediaEvent, &type);
        return type;
    }

    public static int EventStatus(nint mediaEvent)
    {
        var status = 0;
        ((delegate* unmanaged<nint, int*, int>)Com.Table(mediaEvent)[35])(mediaEvent, &status);
        return status;
    }

    // ----- ICodecAPI -----

    /// <summary>ICodecAPI::IsModifiable: S_OK when the setting may change while streaming.</summary>
    public static bool IsModifiable(nint codecApi, in Guid api)
    {
        fixed (Guid* a = &api)
            return ((delegate* unmanaged<nint, Guid*, int>)Com.Table(codecApi)[4])(codecApi, a) == 0;
    }

    /// <summary>ICodecAPI::SetValue with a VT_UI4 (the 24-byte PropVariant has a VARIANT's layout for it).</summary>
    public static int SetCodecValue(nint codecApi, in Guid api, uint value)
    {
        var variant = new PropVariant { Type = CodecApi.VtUi4, Value = value };
        fixed (Guid* a = &api)
            return ((delegate* unmanaged<nint, Guid*, PropVariant*, int>)Com.Table(codecApi)[9])(codecApi, a, &variant);
    }

    /// <summary>ICodecAPI::SetValue with a VT_BOOL (VARIANT_TRUE is -1 in the low 16 bits).</summary>
    public static int SetCodecBool(nint codecApi, in Guid api, bool value)
    {
        var variant = new PropVariant { Type = CodecApi.VtBool, Value = value ? 0xFFFF : 0 };
        fixed (Guid* a = &api)
            return ((delegate* unmanaged<nint, Guid*, PropVariant*, int>)Com.Table(codecApi)[9])(codecApi, a, &variant);
    }

    // ----- IMFSample and IMFMediaBuffer (more) -----

    public static int SetSampleTime(nint sample, long time) => ((delegate* unmanaged<nint, long, int>)Com.Table(sample)[36])(sample, time);

    public static int SetSampleDuration(nint sample, long duration) => ((delegate* unmanaged<nint, long, int>)Com.Table(sample)[38])(sample, duration);

    public static int GetBufferCount(nint sample, out uint count)
    {
        uint result = 0;
        var hr = ((delegate* unmanaged<nint, uint*, int>)Com.Table(sample)[39])(sample, &result);
        count = result;
        return hr;
    }

    public static int AddBuffer(nint sample, nint buffer) => ((delegate* unmanaged<nint, nint, int>)Com.Table(sample)[42])(sample, buffer);

    public static int SetCurrentLength(nint buffer, uint length) => ((delegate* unmanaged<nint, uint, int>)Com.Table(buffer)[6])(buffer, length);
}
