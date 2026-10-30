using System.Runtime.InteropServices;

namespace HandPegApp.Compositor.Native;

// ----- DXGI and Direct3D 11: the structures, numbers and methods the compositor uses (Pass 5.0-Core) -----

internal static class DxgiFormat
{
    public const uint R32G32B32A32Float = 2, R16G16B16A16Float = 10, R16G16Unorm = 35, R8G8B8A8Unorm = 28, R8G8Unorm = 49,
        R16Unorm = 56, R8Unorm = 61, B8G8R8A8Unorm = 87, Nv12 = 103, P010 = 104;
}

internal static class D3D11
{
    public const uint UsageDefault = 0, UsageImmutable = 1, UsageDynamic = 2, UsageStaging = 3;
    public const uint BindConstantBuffer = 0x4, BindShaderResource = 0x8, BindRenderTarget = 0x20, BindDecoder = 0x200;
    public const uint CpuWrite = 0x10000, CpuRead = 0x20000;
    public const uint MiscShared = 0x2, MiscSharedKeyedMutex = 0x100, MiscSharedNtHandle = 0x800;
    public const uint MapRead = 1, MapWrite = 2, MapWriteDiscard = 4;

    /// <summary>D3D11_MAP_FLAG_DO_NOT_WAIT: Map answers DXGI_ERROR_WAS_STILL_DRAWING instead of waiting for the card.</summary>
    public const uint MapFlagDoNotWait = 0x100000;
    public const uint FilterPoint = 0, FilterLinear = 0x15;
    public const uint AddressClamp = 3, AddressBorder = 4;
    public const uint ComparisonNever = 1;
    public const uint BlendZero = 1, BlendOne = 2, BlendSrcAlpha = 5, BlendInvSrcAlpha = 6, BlendOpAdd = 1;
    public const uint FillSolid = 3, CullNone = 1;
    public const uint TopologyTriangleList = 4, TopologyTriangleStrip = 5;
    public const uint SrvTexture2D = 4, SrvTexture2DArray = 5, SrvTexture3D = 8;
    public const uint RtvTexture2D = 4;
    public const uint QueryEvent = 0, QueryTimestamp = 2, QueryTimestampDisjoint = 3;
    public const uint CreateBgraSupport = 0x20, CreateVideoSupport = 0x800, CreateDebug = 0x2;
    public const uint DriverUnknown = 0, DriverHardware = 1, DriverWarp = 5;
    public const uint FeatureLevel11_0 = 0xb000, FeatureLevel11_1 = 0xb100;
    public const uint SdkVersion = 7;
    public const uint AsyncGetDataDoNotFlush = 1;
    public const uint SharedRead = 0x80000000, SharedWrite = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDesc
{
    public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Texture3DDesc
{
    public uint Width, Height, Depth, MipLevels, Format, Usage, BindFlags, CpuAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BufferDesc
{
    public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SubresourceData
{
    public void* SysMem;
    public uint SysMemPitch, SysMemSlicePitch;
}

/// <summary>D3D11_SHADER_RESOURCE_VIEW_DESC: the format, the dimension, and its union (four numbers at most for the kinds used).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShaderResourceViewDesc
{
    public uint Format, ViewDimension, U0, U1, U2, U3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RenderTargetViewDesc
{
    public uint Format, ViewDimension, U0, U1, U2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SamplerDesc
{
    public uint Filter, AddressU, AddressV, AddressW;
    public float MipLodBias;
    public uint MaxAnisotropy, ComparisonFunc;
    public float Border0, Border1, Border2, Border3, MinLod, MaxLod;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RenderTargetBlendDesc
{
    public int BlendEnable;
    public uint SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
    public byte WriteMask;
    private readonly byte _pad0, _pad1, _pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BlendDesc
{
    public int AlphaToCoverage, IndependentBlend;
    public RenderTargets Targets;

    [System.Runtime.CompilerServices.InlineArray(8)]
    public struct RenderTargets
    {
        private RenderTargetBlendDesc _first;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RasterizerDesc
{
    public uint FillMode, CullMode;
    public int FrontCounterClockwise, DepthBias;
    public float DepthBiasClamp, SlopeScaledDepthBias;
    public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Viewport
{
    public float X, Y, Width, Height, MinDepth, MaxDepth;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MappedSubresource
{
    public byte* Data;
    public uint RowPitch, DepthPitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct QueryDesc
{
    public uint Query, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TimestampDisjoint
{
    public ulong Frequency;
    public int Disjoint;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Box
{
    public uint Left, Top, Front, Right, Bottom, Back;
}

/// <summary>DXGI_QUERY_VIDEO_MEMORY_INFO.</summary>
internal struct VideoMemoryInfo
{
    public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
}

/// <summary>DXGI_SWAP_CHAIN_DESC1 (44 bytes).</summary>
internal struct SwapChainDesc1
{
    public uint Width, Height, Format;
    public int Stereo;
    public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
}

/// <summary>DXGI_ADAPTER_DESC1 (312 bytes).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct AdapterDesc1
{
    public fixed char Description[128];
    public uint VendorId, DeviceId, SubSysId, Revision;
    public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public long Luid;
    public uint Flags;

    public const uint FlagSoftware = 2;
}

/// <summary>An ID3D11Device (or ID3D11Device1: <see cref="OpenSharedResourceByName"/>) by its method table.</summary>
internal readonly unsafe struct D3D11Device(nint pointer)
{
    public readonly nint Pointer = pointer;
    private void** T => Com.Table(Pointer);

    public nint CreateBuffer(in BufferDesc desc, SubresourceData* initial = null)
    {
        nint result;
        fixed (BufferDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, BufferDesc*, SubresourceData*, nint*, int>)T[3])(Pointer, d, initial, &result), "CreateBuffer");
        return result;
    }

    public nint CreateTexture2D(in Texture2DDesc desc, SubresourceData* initial = null)
    {
        nint result;
        fixed (Texture2DDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, Texture2DDesc*, SubresourceData*, nint*, int>)T[5])(Pointer, d, initial, &result), "CreateTexture2D");
        return result;
    }

    public nint CreateTexture3D(in Texture3DDesc desc, SubresourceData* initial = null)
    {
        nint result;
        fixed (Texture3DDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, Texture3DDesc*, SubresourceData*, nint*, int>)T[6])(Pointer, d, initial, &result), "CreateTexture3D");
        return result;
    }

    public nint CreateShaderResourceView(nint resource, ShaderResourceViewDesc* desc = null)
    {
        nint result;
        Com.Check(((delegate* unmanaged<nint, nint, ShaderResourceViewDesc*, nint*, int>)T[7])(Pointer, resource, desc, &result), "CreateShaderResourceView");
        return result;
    }

    public nint CreateRenderTargetView(nint resource, RenderTargetViewDesc* desc = null)
    {
        nint result;
        Com.Check(((delegate* unmanaged<nint, nint, RenderTargetViewDesc*, nint*, int>)T[9])(Pointer, resource, desc, &result), "CreateRenderTargetView");
        return result;
    }

    public nint CreateVertexShader(ReadOnlySpan<byte> code)
    {
        nint result;
        fixed (byte* c = code)
            Com.Check(((delegate* unmanaged<nint, byte*, nuint, nint, nint*, int>)T[12])(Pointer, c, (nuint)code.Length, 0, &result), "CreateVertexShader");
        return result;
    }

    public nint CreatePixelShader(ReadOnlySpan<byte> code)
    {
        nint result;
        fixed (byte* c = code)
            Com.Check(((delegate* unmanaged<nint, byte*, nuint, nint, nint*, int>)T[15])(Pointer, c, (nuint)code.Length, 0, &result), "CreatePixelShader");
        return result;
    }

    public nint CreateBlendState(in BlendDesc desc)
    {
        nint result;
        fixed (BlendDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, BlendDesc*, nint*, int>)T[20])(Pointer, d, &result), "CreateBlendState");
        return result;
    }

    public nint CreateRasterizerState(in RasterizerDesc desc)
    {
        nint result;
        fixed (RasterizerDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, RasterizerDesc*, nint*, int>)T[22])(Pointer, d, &result), "CreateRasterizerState");
        return result;
    }

    public nint CreateSamplerState(in SamplerDesc desc)
    {
        nint result;
        fixed (SamplerDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, SamplerDesc*, nint*, int>)T[23])(Pointer, d, &result), "CreateSamplerState");
        return result;
    }

    public nint CreateQuery(uint query)
    {
        var desc = new QueryDesc { Query = query };
        nint result;
        Com.Check(((delegate* unmanaged<nint, QueryDesc*, nint*, int>)T[24])(Pointer, &desc, &result), "CreateQuery");
        return result;
    }

    public int GetDeviceRemovedReason() => ((delegate* unmanaged<nint, int>)T[39])(Pointer);

    public nint GetImmediateContext()
    {
        nint result;
        ((delegate* unmanaged<nint, nint*, void>)T[40])(Pointer, &result);
        return result;
    }

    /// <summary>ID3D11Device1::OpenSharedResourceByName (the pointer must be an ID3D11Device1).</summary>
    public int OpenSharedResourceByName(string name, uint access, in Guid interfaceId, out nint resource)
    {
        nint result = 0;
        int hr;
        fixed (char* n = name)
        fixed (Guid* id = &interfaceId)
            hr = ((delegate* unmanaged<nint, char*, uint, Guid*, nint*, int>)T[49])(Pointer, n, access, id, &result);
        resource = result;
        return hr;
    }
}

/// <summary>An ID3D11DeviceContext by its method table (the immediate context).</summary>
internal readonly unsafe struct D3D11Context(nint pointer)
{
    public readonly nint Pointer = pointer;
    private void** T => Com.Table(Pointer);

    public void VSSetConstantBuffers(uint slot, nint buffer) => ((delegate* unmanaged<nint, uint, uint, nint*, void>)T[7])(Pointer, slot, 1, &buffer);

    public void PSSetShaderResources(uint slot, uint count, nint* views) => ((delegate* unmanaged<nint, uint, uint, nint*, void>)T[8])(Pointer, slot, count, views);

    public void PSSetShader(nint shader) => ((delegate* unmanaged<nint, nint, nint*, uint, void>)T[9])(Pointer, shader, null, 0);

    public void PSSetSamplers(uint slot, uint count, nint* samplers) => ((delegate* unmanaged<nint, uint, uint, nint*, void>)T[10])(Pointer, slot, count, samplers);

    public void VSSetShader(nint shader) => ((delegate* unmanaged<nint, nint, nint*, uint, void>)T[11])(Pointer, shader, null, 0);

    public void Draw(uint vertexCount, uint startVertex) => ((delegate* unmanaged<nint, uint, uint, void>)T[13])(Pointer, vertexCount, startVertex);

    public int Map(nint resource, uint subresource, uint mapType, uint flags, out MappedSubresource mapped)
    {
        MappedSubresource result;
        var hr = ((delegate* unmanaged<nint, nint, uint, uint, uint, MappedSubresource*, int>)T[14])(Pointer, resource, subresource, mapType, flags, &result);
        mapped = result;
        return hr;
    }

    public void Unmap(nint resource, uint subresource) => ((delegate* unmanaged<nint, nint, uint, void>)T[15])(Pointer, resource, subresource);

    public void PSSetConstantBuffers(uint slot, nint buffer) => ((delegate* unmanaged<nint, uint, uint, nint*, void>)T[16])(Pointer, slot, 1, &buffer);

    public void IASetInputLayout(nint layout) => ((delegate* unmanaged<nint, nint, void>)T[17])(Pointer, layout);

    public void IASetPrimitiveTopology(uint topology) => ((delegate* unmanaged<nint, uint, void>)T[24])(Pointer, topology);

    public void Begin(nint query) => ((delegate* unmanaged<nint, nint, void>)T[27])(Pointer, query);

    public void End(nint query) => ((delegate* unmanaged<nint, nint, void>)T[28])(Pointer, query);

    public int GetData(nint query, void* data, uint size, uint flags) => ((delegate* unmanaged<nint, nint, void*, uint, uint, int>)T[29])(Pointer, query, data, size, flags);

    public void OMSetRenderTargets(nint renderTarget)
    {
        if (renderTarget == 0)
            ((delegate* unmanaged<nint, uint, nint*, nint, void>)T[33])(Pointer, 0, null, 0);
        else
            ((delegate* unmanaged<nint, uint, nint*, nint, void>)T[33])(Pointer, 1, &renderTarget, 0);
    }

    public void OMSetBlendState(nint state)
    {
        var factor = stackalloc float[4] { 0, 0, 0, 0 };
        ((delegate* unmanaged<nint, nint, float*, uint, void>)T[35])(Pointer, state, factor, 0xFFFFFFFF);
    }

    public void RSSetState(nint state) => ((delegate* unmanaged<nint, nint, void>)T[43])(Pointer, state);

    /// <summary>RSSetScissorRects with one rectangle (left, top, right, bottom in target pixels).</summary>
    public void RSSetScissor(int left, int top, int right, int bottom)
    {
        var rect = stackalloc int[4] { left, top, right, bottom };
        ((delegate* unmanaged<nint, uint, int*, void>)T[45])(Pointer, 1, rect);
    }

    public void RSSetViewport(in Viewport viewport)
    {
        fixed (Viewport* v = &viewport)
            ((delegate* unmanaged<nint, uint, Viewport*, void>)T[44])(Pointer, 1, v);
    }

    public void CopySubresourceRegion(nint destination, uint destinationSubresource, uint x, uint y, nint source, uint sourceSubresource, Box* box) =>
        ((delegate* unmanaged<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)T[46])(Pointer, destination, destinationSubresource, x, y, 0, source, sourceSubresource, box);

    public void CopyResource(nint destination, nint source) => ((delegate* unmanaged<nint, nint, nint, void>)T[47])(Pointer, destination, source);

    public void UpdateSubresource(nint resource, uint subresource, Box* box, void* data, uint rowPitch, uint depthPitch) =>
        ((delegate* unmanaged<nint, nint, uint, Box*, void*, uint, uint, void>)T[48])(Pointer, resource, subresource, box, data, rowPitch, depthPitch);

    public void ClearRenderTargetView(nint view, float r, float g, float b, float a)
    {
        var color = stackalloc float[4] { r, g, b, a };
        ((delegate* unmanaged<nint, nint, float*, void>)T[50])(Pointer, view, color);
    }

    public void ClearState() => ((delegate* unmanaged<nint, void>)T[110])(Pointer);

    public void Flush() => ((delegate* unmanaged<nint, void>)T[111])(Pointer);
}

/// <summary>DXGI: the factory, the cards, shared handles and keyed mutexes.</summary>
internal static unsafe partial class Dxgi
{
    public const uint GpuPreferenceHighPerformance = 2;
    public const int NotFound = unchecked((int)0x887A0002);

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(Guid* interfaceId, nint* factory);

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, uint* featureLevels, uint featureLevelCount,
        uint sdkVersion, nint* device, uint* featureLevel, nint* context);

    public static nint CreateFactory()
    {
        nint factory;
        var id = Iid.DxgiFactory1;
        Com.Check(CreateDXGIFactory1(&id, &factory), "CreateDXGIFactory1");
        return factory;
    }

    /// <summary>IDXGIFactory1::EnumAdapters1: the card at an index (DXGI's order), or 0 past the last.</summary>
    public static nint EnumAdapter(nint factory, uint index)
    {
        nint adapter;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Table(factory)[12])(factory, index, &adapter);
        return hr < 0 ? 0 : adapter;
    }

    /// <summary>IDXGIFactory6::EnumAdapterByGpuPreference(0, high performance): the card Windows calls the fast one, or 0.</summary>
    public static nint HighPerformanceAdapter(nint factory)
    {
        var factory6 = Com.Query(factory, Iid.DxgiFactory6);
        if (factory6 == 0)
            return 0;
        try
        {
            nint adapter;
            var id = Iid.DxgiAdapter1;
            var hr = ((delegate* unmanaged<nint, uint, uint, Guid*, nint*, int>)Com.Table(factory6)[29])(factory6, 0, GpuPreferenceHighPerformance, &id, &adapter);
            return hr < 0 ? 0 : adapter;
        }
        finally
        {
            Com.Release(ref factory6);
        }
    }

    /// <summary>IDXGIAdapter1::GetDesc1.</summary>
    public static AdapterDesc1 Describe(nint adapter)
    {
        AdapterDesc1 desc;
        Com.Check(((delegate* unmanaged<nint, AdapterDesc1*, int>)Com.Table(adapter)[10])(adapter, &desc), "GetDesc1");
        return desc;
    }

    public static string Name(in AdapterDesc1 desc)
    {
        fixed (char* chars = desc.Description)
            return new string(chars).Trim();
    }

    /// <summary>IDXGIResource1::CreateSharedHandle, named, for reading and writing.</summary>
    public static nint CreateSharedHandle(nint resource, string name)
    {
        var resource1 = Com.Query(resource, Iid.DxgiResource1);
        if (resource1 == 0)
            throw new GpuException(unchecked((int)0x80004002), "IDXGIResource1");
        try
        {
            nint handle;
            fixed (char* n = name)
                Com.Check(((delegate* unmanaged<nint, nint, uint, char*, nint*, int>)Com.Table(resource1)[13])(resource1, 0, D3D11.SharedRead | D3D11.SharedWrite, n, &handle), "CreateSharedHandle");
            return handle;
        }
        finally
        {
            Com.Release(ref resource1);
        }
    }

    /// <summary>IDXGIKeyedMutex::AcquireSync: S_OK, or <see cref="Com.WaitTimeout"/>.</summary>
    public static int AcquireSync(nint keyedMutex, ulong key, uint milliseconds) =>
        ((delegate* unmanaged<nint, ulong, uint, int>)Com.Table(keyedMutex)[8])(keyedMutex, key, milliseconds);

    public static int ReleaseSync(nint keyedMutex, ulong key) => ((delegate* unmanaged<nint, ulong, int>)Com.Table(keyedMutex)[9])(keyedMutex, key);

    // ----- Swap chains (the GPU viewport in HandPeg's own process, 5.0-AudioAnd3D) -----

    public const uint SwapEffectFlipDiscard = 4, ScalingStretch = 0, ScalingNone = 1, AlphaIgnore = 3, UsageRenderTargetOutput = 0x20;
    public const uint SwapChainFlagFrameLatencyWaitable = 64;
    public const int StatusOccluded = 0x087A0001;

    /// <summary>
    /// A flip-model swap chain on a window (IDXGIFactory2::CreateSwapChainForHwnd through the factory that made the
    /// device's card), two BGRA8 buffers, a frame-latency waitable object, Alt+Enter left to the app.
    /// </summary>
    public static nint CreateSwapChain(nint device, nint window, int width, int height)
    {
        var adapter = AdapterOf(device);
        if (adapter == 0)
            throw new GpuException(unchecked((int)0x80004005), "The device's adapter");
        nint factory = 0;
        try
        {
            var iid = Iid.DxgiFactory2;
            Com.Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Table(adapter)[6])(adapter, &iid, &factory), "IDXGIObject::GetParent");
            var desc = new SwapChainDesc1
            {
                Width = (uint)Math.Max(width, 8),
                Height = (uint)Math.Max(height, 8),
                Format = DxgiFormat.B8G8R8A8Unorm,
                SampleCount = 1,
                BufferUsage = UsageRenderTargetOutput,
                BufferCount = 2,
                Scaling = ScalingStretch,
                SwapEffect = SwapEffectFlipDiscard,
                AlphaMode = AlphaIgnore,
                Flags = SwapChainFlagFrameLatencyWaitable,
            };
            nint swapChain;
            Com.Check(((delegate* unmanaged<nint, nint, nint, SwapChainDesc1*, nint, nint, nint*, int>)Com.Table(factory)[15])(factory, device, window, &desc, 0, 0, &swapChain),
                "CreateSwapChainForHwnd");

            // No Alt+Enter to full screen (Alt is the 3D view's orbit), no Print Screen handling of DXGI's.
            _ = ((delegate* unmanaged<nint, nint, uint, int>)Com.Table(factory)[8])(factory, window, 0x2 | 0x4);
            return swapChain;
        }
        finally
        {
            Com.Release(ref factory);
            Com.Release(ref adapter);
        }
    }

    /// <summary>IDXGISwapChain2: at most one frame queued, and the event that is set when the next may be drawn (0 if none).</summary>
    public static nint FrameLatencyWaitable(nint swapChain)
    {
        var swapChain2 = Com.Query(swapChain, Iid.DxgiSwapChain2);
        if (swapChain2 == 0)
            return 0;
        try
        {
            _ = ((delegate* unmanaged<nint, uint, int>)Com.Table(swapChain2)[31])(swapChain2, 1);
            return ((delegate* unmanaged<nint, nint>)Com.Table(swapChain2)[33])(swapChain2);
        }
        finally
        {
            Com.Release(ref swapChain2);
        }
    }

    /// <summary>IDXGISwapChain::GetBuffer(0) as an ID3D11Texture2D (released by the caller before a resize).</summary>
    public static nint BackBuffer(nint swapChain)
    {
        var iid = Iid.D3D11Texture2D;
        nint texture;
        Com.Check(((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Com.Table(swapChain)[9])(swapChain, 0, &iid, &texture), "GetBuffer");
        return texture;
    }

    /// <summary>IDXGISwapChain::Present: S_OK, DXGI_STATUS_OCCLUDED, or a device-lost code.</summary>
    public static int Present(nint swapChain, uint syncInterval, uint flags) =>
        ((delegate* unmanaged<nint, uint, uint, int>)Com.Table(swapChain)[8])(swapChain, syncInterval, flags);

    /// <summary>IDXGISwapChain::ResizeBuffers, keeping the count and format (every buffer reference let go first).</summary>
    public static int ResizeBuffers(nint swapChain, int width, int height) =>
        ((delegate* unmanaged<nint, uint, uint, uint, uint, uint, int>)Com.Table(swapChain)[13])(swapChain, 0, (uint)Math.Max(width, 8), (uint)Math.Max(height, 8), 0, SwapChainFlagFrameLatencyWaitable);

    /// <summary>
    /// IDXGIAdapter3::QueryVideoMemoryInfo for the card's own memory (node 0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL): the budget
    /// Windows gives this process and what it uses. False when the call failed.
    /// </summary>
    public static bool QueryVideoMemory(nint adapter3, out VideoMemoryInfo info)
    {
        VideoMemoryInfo result;
        var hr = ((delegate* unmanaged<nint, uint, uint, VideoMemoryInfo*, int>)Com.Table(adapter3)[14])(adapter3, 0, 0, &result);
        info = result;
        return hr >= 0;
    }

    /// <summary>The DXGI adapter behind a Direct3D 11 device (IDXGIDevice::GetAdapter).</summary>
    public static nint AdapterOf(nint device)
    {
        var dxgiDevice = Com.Query(device, Iid.DxgiDevice);
        if (dxgiDevice == 0)
            return 0;
        try
        {
            nint adapter;
            var hr = ((delegate* unmanaged<nint, nint*, int>)Com.Table(dxgiDevice)[7])(dxgiDevice, &adapter);
            return hr < 0 ? 0 : adapter;
        }
        finally
        {
            Com.Release(ref dxgiDevice);
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}
