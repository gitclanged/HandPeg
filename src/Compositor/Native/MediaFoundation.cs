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
    public static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
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
