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
    public const uint AddressWrap = 1, AddressClamp = 3, AddressBorder = 4;
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
            Com.Check(((delegate* unmanaged<nint, BufferDesc*, SubresourceData*, nint*, int>)T[ID3D11DeviceSlot.CreateBuffer])(Pointer, d, initial, &result), "CreateBuffer");
        return result;
    }

    public nint CreateTexture2D(in Texture2DDesc desc, SubresourceData* initial = null)
    {
        nint result;
        fixed (Texture2DDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, Texture2DDesc*, SubresourceData*, nint*, int>)T[ID3D11DeviceSlot.CreateTexture2D])(Pointer, d, initial, &result), "CreateTexture2D");
        return result;
    }

    public nint CreateTexture3D(in Texture3DDesc desc, SubresourceData* initial = null)
    {
        nint result;
        fixed (Texture3DDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, Texture3DDesc*, SubresourceData*, nint*, int>)T[ID3D11DeviceSlot.CreateTexture3D])(Pointer, d, initial, &result), "CreateTexture3D");
        return result;
    }

    public nint CreateShaderResourceView(nint resource, ShaderResourceViewDesc* desc = null)
    {
        nint result;
        Com.Check(((delegate* unmanaged<nint, nint, ShaderResourceViewDesc*, nint*, int>)T[ID3D11DeviceSlot.CreateShaderResourceView])(Pointer, resource, desc, &result), "CreateShaderResourceView");
        return result;
    }

    public nint CreateRenderTargetView(nint resource, RenderTargetViewDesc* desc = null)
    {
        nint result;
        Com.Check(((delegate* unmanaged<nint, nint, RenderTargetViewDesc*, nint*, int>)T[ID3D11DeviceSlot.CreateRenderTargetView])(Pointer, resource, desc, &result), "CreateRenderTargetView");
        return result;
    }

    public nint CreateVertexShader(ReadOnlySpan<byte> code)
    {
        nint result;
        fixed (byte* c = code)
            Com.Check(((delegate* unmanaged<nint, byte*, nuint, nint, nint*, int>)T[ID3D11DeviceSlot.CreateVertexShader])(Pointer, c, (nuint)code.Length, 0, &result), "CreateVertexShader");
        return result;
    }

    public nint CreatePixelShader(ReadOnlySpan<byte> code)
    {
        nint result;
        fixed (byte* c = code)
            Com.Check(((delegate* unmanaged<nint, byte*, nuint, nint, nint*, int>)T[ID3D11DeviceSlot.CreatePixelShader])(Pointer, c, (nuint)code.Length, 0, &result), "CreatePixelShader");
        return result;
    }

    public nint CreateBlendState(in BlendDesc desc)
    {
        nint result;
        fixed (BlendDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, BlendDesc*, nint*, int>)T[ID3D11DeviceSlot.CreateBlendState])(Pointer, d, &result), "CreateBlendState");
        return result;
    }

    public nint CreateRasterizerState(in RasterizerDesc desc)
    {
        nint result;
        fixed (RasterizerDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, RasterizerDesc*, nint*, int>)T[ID3D11DeviceSlot.CreateRasterizerState])(Pointer, d, &result), "CreateRasterizerState");
        return result;
    }

    public nint CreateSamplerState(in SamplerDesc desc)
    {
        nint result;
        fixed (SamplerDesc* d = &desc)
            Com.Check(((delegate* unmanaged<nint, SamplerDesc*, nint*, int>)T[ID3D11DeviceSlot.CreateSamplerState])(Pointer, d, &result), "CreateSamplerState");
        return result;
    }

    public nint CreateQuery(uint query)
    {
        var desc = new QueryDesc { Query = query };
        nint result;
        Com.Check(((delegate* unmanaged<nint, QueryDesc*, nint*, int>)T[ID3D11DeviceSlot.CreateQuery])(Pointer, &desc, &result), "CreateQuery");
        return result;
    }

    public int GetDeviceRemovedReason() => ((delegate* unmanaged<nint, int>)T[ID3D11DeviceSlot.GetDeviceRemovedReason])(Pointer);

    public nint GetImmediateContext()
    {
        nint result;
        ((delegate* unmanaged<nint, nint*, void>)T[ID3D11DeviceSlot.GetImmediateContext])(Pointer, &result);
        return result;
    }

    /// <summary>ID3D11Device1::OpenSharedResourceByName (the pointer must be an ID3D11Device1).</summary>
    public int OpenSharedResourceByName(string name, uint access, in Guid interfaceId, out nint resource)
    {
        nint result = 0;
        int hr;
        fixed (char* n = name)
        fixed (Guid* id = &interfaceId)
            hr = ((delegate* unmanaged<nint, char*, uint, Guid*, nint*, int>)T[ID3D11Device1Slot.OpenSharedResourceByName])(Pointer, n, access, id, &result);
        resource = result;
        return hr;
    }
}

/// <summary>An ID3D11DeviceContext by its method table (the immediate context).</summary>
internal readonly unsafe struct D3D11Context(nint pointer)
{
    public readonly nint Pointer = pointer;
    private void** T => Com.Table(Pointer);

    public void VSSetConstantBuffers(uint slot, nint buffer) => ((delegate* unmanaged<nint, uint, uint, nint*, void>)T[ID3D11DeviceContextSlot.VSSetConstantBuffers])(Pointer, slot, 1, &buffer);

    public void PSSetShaderResources(uint slot, uint count, nint* views) => ((delegate* unmanaged<nint, uint, uint, nint*, void>)T[ID3D11DeviceContextSlot.PSSetShaderResources])(Pointer, slot, count, views);

    public void PSSetShader(nint shader) => ((delegate* unmanaged<nint, nint, nint*, uint, void>)T[ID3D11DeviceContextSlot.PSSetShader])(Pointer, shader, null, 0);

    public void PSSetSamplers(uint slot, uint count, nint* samplers) => ((delegate* unmanaged<nint, uint, uint, nint*, void>)T[ID3D11DeviceContextSlot.PSSetSamplers])(Pointer, slot, count, samplers);

    public void VSSetShader(nint shader) => ((delegate* unmanaged<nint, nint, nint*, uint, void>)T[ID3D11DeviceContextSlot.VSSetShader])(Pointer, shader, null, 0);

    public void Draw(uint vertexCount, uint startVertex) => ((delegate* unmanaged<nint, uint, uint, void>)T[ID3D11DeviceContextSlot.Draw])(Pointer, vertexCount, startVertex);

    public int Map(nint resource, uint subresource, uint mapType, uint flags, out MappedSubresource mapped)
    {
        MappedSubresource result;
        var hr = ((delegate* unmanaged<nint, nint, uint, uint, uint, MappedSubresource*, int>)T[ID3D11DeviceContextSlot.Map])(Pointer, resource, subresource, mapType, flags, &result);
        mapped = result;
        return hr;
    }

    public void Unmap(nint resource, uint subresource) => ((delegate* unmanaged<nint, nint, uint, void>)T[ID3D11DeviceContextSlot.Unmap])(Pointer, resource, subresource);

    public void PSSetConstantBuffers(uint slot, nint buffer) => ((delegate* unmanaged<nint, uint, uint, nint*, void>)T[ID3D11DeviceContextSlot.PSSetConstantBuffers])(Pointer, slot, 1, &buffer);

    public void IASetInputLayout(nint layout) => ((delegate* unmanaged<nint, nint, void>)T[ID3D11DeviceContextSlot.IASetInputLayout])(Pointer, layout);

    public void IASetPrimitiveTopology(uint topology) => ((delegate* unmanaged<nint, uint, void>)T[ID3D11DeviceContextSlot.IASetPrimitiveTopology])(Pointer, topology);

    public void Begin(nint query) => ((delegate* unmanaged<nint, nint, void>)T[ID3D11DeviceContextSlot.Begin])(Pointer, query);

    public void End(nint query) => ((delegate* unmanaged<nint, nint, void>)T[ID3D11DeviceContextSlot.EndQuery])(Pointer, query);

    public int GetData(nint query, void* data, uint size, uint flags) => ((delegate* unmanaged<nint, nint, void*, uint, uint, int>)T[ID3D11DeviceContextSlot.GetData])(Pointer, query, data, size, flags);

    public void OMSetRenderTargets(nint renderTarget)
    {
        if (renderTarget == 0)
            ((delegate* unmanaged<nint, uint, nint*, nint, void>)T[ID3D11DeviceContextSlot.OMSetRenderTargets])(Pointer, 0, null, 0);
        else
            ((delegate* unmanaged<nint, uint, nint*, nint, void>)T[ID3D11DeviceContextSlot.OMSetRenderTargets])(Pointer, 1, &renderTarget, 0);
    }

    public void OMSetBlendState(nint state)
    {
        var factor = stackalloc float[4] { 0, 0, 0, 0 };
        ((delegate* unmanaged<nint, nint, float*, uint, void>)T[ID3D11DeviceContextSlot.OMSetBlendState])(Pointer, state, factor, 0xFFFFFFFF);
    }

    public void RSSetState(nint state) => ((delegate* unmanaged<nint, nint, void>)T[ID3D11DeviceContextSlot.RSSetState])(Pointer, state);

    /// <summary>RSSetScissorRects with one rectangle (left, top, right, bottom in target pixels).</summary>
    public void RSSetScissor(int left, int top, int right, int bottom)
    {
        var rect = stackalloc int[4] { left, top, right, bottom };
        ((delegate* unmanaged<nint, uint, int*, void>)T[ID3D11DeviceContextSlot.RSSetScissorRects])(Pointer, 1, rect);
    }

    public void RSSetViewport(in Viewport viewport)
    {
        fixed (Viewport* v = &viewport)
            ((delegate* unmanaged<nint, uint, Viewport*, void>)T[ID3D11DeviceContextSlot.RSSetViewports])(Pointer, 1, v);
    }

    public void CopySubresourceRegion(nint destination, uint destinationSubresource, uint x, uint y, nint source, uint sourceSubresource, Box* box) =>
        ((delegate* unmanaged<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)T[ID3D11DeviceContextSlot.CopySubresourceRegion])(Pointer, destination, destinationSubresource, x, y, 0, source, sourceSubresource, box);

    public void CopyResource(nint destination, nint source) => ((delegate* unmanaged<nint, nint, nint, void>)T[ID3D11DeviceContextSlot.CopyResource])(Pointer, destination, source);

    public void UpdateSubresource(nint resource, uint subresource, Box* box, void* data, uint rowPitch, uint depthPitch) =>
        ((delegate* unmanaged<nint, nint, uint, Box*, void*, uint, uint, void>)T[ID3D11DeviceContextSlot.UpdateSubresource])(Pointer, resource, subresource, box, data, rowPitch, depthPitch);

    public void ClearRenderTargetView(nint view, float r, float g, float b, float a)
    {
        var color = stackalloc float[4] { r, g, b, a };
        ((delegate* unmanaged<nint, nint, float*, void>)T[ID3D11DeviceContextSlot.ClearRenderTargetView])(Pointer, view, color);
    }

    public void ClearState() => ((delegate* unmanaged<nint, void>)T[ID3D11DeviceContextSlot.ClearState])(Pointer);

    public void Flush() => ((delegate* unmanaged<nint, void>)T[ID3D11DeviceContextSlot.Flush])(Pointer);
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
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Table(factory)[IDXGIFactory1Slot.EnumAdapters1])(factory, index, &adapter);
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
            var hr = ((delegate* unmanaged<nint, uint, uint, Guid*, nint*, int>)Com.Table(factory6)[IDXGIFactory6Slot.EnumAdapterByGpuPreference])(factory6, 0, GpuPreferenceHighPerformance, &id, &adapter);
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
        Com.Check(((delegate* unmanaged<nint, AdapterDesc1*, int>)Com.Table(adapter)[IDXGIAdapter1Slot.GetDesc1])(adapter, &desc), "GetDesc1");
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
                Com.Check(((delegate* unmanaged<nint, nint, uint, char*, nint*, int>)Com.Table(resource1)[IDXGIResource1Slot.CreateSharedHandle])(resource1, 0, D3D11.SharedRead | D3D11.SharedWrite, n, &handle), "CreateSharedHandle");
            return handle;
        }
        finally
        {
            Com.Release(ref resource1);
        }
    }

    /// <summary>IDXGIKeyedMutex::AcquireSync: S_OK, or <see cref="Com.WaitTimeout"/>.</summary>
    public static int AcquireSync(nint keyedMutex, ulong key, uint milliseconds)
    {
        var crumb = HandPegApp.Services.GpuNativeBreadcrumbs.EnterHot("AcquireSync", milliseconds);
        var hr = ((delegate* unmanaged<nint, ulong, uint, int>)Com.Table(keyedMutex)[IDXGIKeyedMutexSlot.AcquireSync])(keyedMutex, key, milliseconds);
        crumb.Leave(hr);
        return hr;
    }

    public static int ReleaseSync(nint keyedMutex, ulong key)
    {
        var crumb = HandPegApp.Services.GpuNativeBreadcrumbs.EnterHot("ReleaseSync");
        var hr = ((delegate* unmanaged<nint, ulong, int>)Com.Table(keyedMutex)[IDXGIKeyedMutexSlot.ReleaseSync])(keyedMutex, key);
        crumb.Leave(hr);
        return hr;
    }

    // ----- Swap chains (the GPU viewport in HandPeg's own process, 5.0-AudioAnd3D) -----

    public const uint SwapEffectFlipDiscard = 4, ScalingStretch = 0, ScalingNone = 1, AlphaIgnore = 3, UsageRenderTargetOutput = 0x20;
    public const uint SwapChainFlagFrameLatencyWaitable = 64;
    public const int StatusOccluded = 0x087A0001;

    /// <summary>
    /// A flip-model swap chain on a window (IDXGIFactory2::CreateSwapChainForHwnd through the factory that made the
    /// device's card), two BGRA8 buffers, a frame-latency waitable object, Alt+Enter left to the app. <paramref name="scaling"/>
    /// says what the desktop does with a buffer of another size than the window (5.4.1A): <see cref="ScalingNone"/> for the player,
    /// whose frame the viewport lays in the middle of its buffer, never stretched across a resized window; <see cref="ScalingStretch"/>
    /// for a side window's mirror, whose buffer is the canvas's region, fitted into a window that keeps its aspect.
    /// (DXGI_SCALING_ASPECT_RATIO_STRETCH is refused for a swap chain on a window.)
    /// </summary>
    public static nint CreateSwapChain(nint device, nint window, int width, int height, uint scaling)
    {
        var adapter = AdapterOf(device);
        if (adapter == 0)
            throw new GpuException(unchecked((int)0x80004005), "The device's adapter");
        nint factory = 0;
        try
        {
            var iid = Iid.DxgiFactory2;
            Com.Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Table(adapter)[IDXGIObjectSlot.GetParent])(adapter, &iid, &factory), "IDXGIObject::GetParent");
            var desc = new SwapChainDesc1
            {
                Width = (uint)Math.Max(width, 8),
                Height = (uint)Math.Max(height, 8),
                Format = DxgiFormat.B8G8R8A8Unorm,
                SampleCount = 1,
                BufferUsage = UsageRenderTargetOutput,
                BufferCount = 2,
                Scaling = scaling,
                SwapEffect = SwapEffectFlipDiscard,
                AlphaMode = AlphaIgnore,
                Flags = SwapChainFlagFrameLatencyWaitable,
            };
            nint swapChain;
            Com.Check(((delegate* unmanaged<nint, nint, nint, SwapChainDesc1*, nint, nint, nint*, int>)Com.Table(factory)[IDXGIFactory2Slot.CreateSwapChainForHwnd])(factory, device, window, &desc, 0, 0, &swapChain),
                "CreateSwapChainForHwnd");

            // No Alt+Enter to full screen (Alt is the 3D view's orbit), no Print Screen handling of DXGI's.
            _ = ((delegate* unmanaged<nint, nint, uint, int>)Com.Table(factory)[IDXGIFactorySlot.MakeWindowAssociation])(factory, window, 0x2 | 0x4);
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
            _ = ((delegate* unmanaged<nint, uint, int>)Com.Table(swapChain2)[IDXGISwapChain2Slot.SetMaximumFrameLatency])(swapChain2, 1);
            return ((delegate* unmanaged<nint, nint>)Com.Table(swapChain2)[IDXGISwapChain2Slot.GetFrameLatencyWaitableObject])(swapChain2);
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
        Com.Check(((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Com.Table(swapChain)[IDXGISwapChainSlot.GetBuffer])(swapChain, 0, &iid, &texture), "GetBuffer");
        return texture;
    }

    /// <summary>IDXGISwapChain::Present: S_OK, DXGI_STATUS_OCCLUDED, or a device-lost code.</summary>
    public static int Present(nint swapChain, uint syncInterval, uint flags)
    {
        var crumb = HandPegApp.Services.GpuNativeBreadcrumbs.EnterHot("Present", syncInterval);
        var hr = ((delegate* unmanaged<nint, uint, uint, int>)Com.Table(swapChain)[IDXGISwapChainSlot.Present])(swapChain, syncInterval, flags);
        crumb.Leave(hr);
        return hr;
    }

    // DXGI_FRAME_STATISTICS: the present shown at the last vblank, the refresh counts, and that vblank's QPC and GPU times.
    private struct FrameStatisticsData
    {
        public uint PresentCount, PresentRefreshCount, SyncRefreshCount;
        public long SyncQpcTime, SyncGpuTime;
    }

    /// <summary>IDXGISwapChain::GetLastPresentCount (slot 17): the count of the swap chain's last Present; 0 when it cannot say.</summary>
    public static uint LastPresentCount(nint swapChain)
    {
        if (swapChain == 0)
            return 0;
        uint count = 0;
        var hr = ((delegate* unmanaged<nint, uint*, int>)Com.Table(swapChain)[IDXGISwapChainSlot.GetLastPresentCount])(swapChain, &count);
        return hr >= 0 ? count : 0;
    }

    /// <summary>
    /// IDXGISwapChain::GetFrameStatistics (slot 16, 5.4.4-Final): the present shown at the last vblank and that vblank's QPC; false when it
    /// gives none (a windowed swap chain's frames are reported only while DWM composes its window; DISJOINT after a mode change).
    /// </summary>
    public static bool FrameStatistics(nint swapChain, out uint presentCount, out long syncQpc)
    {
        (presentCount, syncQpc) = (0, 0);
        if (swapChain == 0)
            return false;
        FrameStatisticsData data;
        var hr = ((delegate* unmanaged<nint, FrameStatisticsData*, int>)Com.Table(swapChain)[IDXGISwapChainSlot.GetFrameStatistics])(swapChain, &data);
        if (hr < 0 || data.PresentCount == 0 || data.SyncQpcTime == 0)
            return false;
        (presentCount, syncQpc) = (data.PresentCount, data.SyncQpcTime);
        return true;
    }

    /// <summary>IDXGISwapChain::ResizeBuffers, keeping the count and format (every buffer reference let go first).</summary>
    public static int ResizeBuffers(nint swapChain, int width, int height)
    {
        var crumb = HandPegApp.Services.GpuNativeBreadcrumbs.Enter("ResizeBuffers", $"{width}x{height}");
        var hr = ((delegate* unmanaged<nint, uint, uint, uint, uint, uint, int>)Com.Table(swapChain)[IDXGISwapChainSlot.ResizeBuffers])(swapChain, 0, (uint)Math.Max(width, 8), (uint)Math.Max(height, 8), 0, SwapChainFlagFrameLatencyWaitable);
        crumb.Leave(hr);
        return hr;
    }

    /// <summary>
    /// IDXGIAdapter3::QueryVideoMemoryInfo for the card's own memory (node 0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL): the budget
    /// Windows gives this process and what it uses. False when the call failed.
    /// </summary>
    public static bool QueryVideoMemory(nint adapter3, out VideoMemoryInfo info)
    {
        VideoMemoryInfo result;
        var hr = ((delegate* unmanaged<nint, uint, uint, VideoMemoryInfo*, int>)Com.Table(adapter3)[IDXGIAdapter3Slot.QueryVideoMemoryInfo])(adapter3, 0, 0, &result);
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
            var hr = ((delegate* unmanaged<nint, nint*, int>)Com.Table(dxgiDevice)[IDXGIDeviceSlot.GetAdapter])(dxgiDevice, &adapter);
            return hr < 0 ? 0 : adapter;
        }
        finally
        {
            Com.Release(ref dxgiDevice);
        }
    }

}
