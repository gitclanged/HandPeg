using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Compositor;

/// <summary>
/// The compositor's D3D11 device on one adapter, with the fixed states every pass shares (four samplers, two blend states, one rasterizer
/// state, the pass constant buffer) and a render-texture pool. Created with video support (Media Foundation decodes on it) and
/// multithread-protected, since the decoder calls it from its own threads.
/// </summary>
internal sealed unsafe class GpuDevice : IDisposable
{
    private nint _device;
    private nint _device1;
    private nint _context;
    private nint _constants;
    private nint _linearClamp, _pointClamp, _linearBorder, _linearWrap;
    private nint _premultipliedOver, _additive, _rasterizer;

    public D3D11Device Device => new(_device);

    /// <summary>The same device as an ID3D11Device1 (shared resources by name).</summary>
    public D3D11Device Device1 => new(_device1);

    public D3D11Context Context => new(_context);

    public nint DevicePointer => _device;

    public long AdapterLuid { get; private set; }

    /// <summary>Adapter index in DXGI enumeration order (as used by GPU roles and FFmpeg -hwaccel_device); -1 for WARP.</summary>
    public int AdapterIndex { get; private set; } = -1;

    public string AdapterName { get; private set; } = "";

    public uint VendorId { get; private set; }

    /// <summary>True for WARP, the CPU software rasterizer (no hardware decode).</summary>
    public bool IsWarp { get; private set; }

    /// <summary>Dedicated VRAM in bytes (0 for WARP or when unknown); scales the 3D view's plane budget.</summary>
    public long DedicatedVideoMemory { get; private set; }

    /// <summary>Shared system memory DXGI reports for the adapter, in bytes (0 for WARP).</summary>
    public long SharedSystemMemory { get; private set; }

    /// <summary>
    /// True for unified-memory SoC graphics (e.g. Snapdragon X Adreno: 128 MB carve-out, 16 GB shared): DXCore IsIntegrated when reported,
    /// else the DXGI size heuristic (<see cref="MemoryTier.IsUnifiedMemory"/>). Evaluated lazily on first use (the worker's Auto cache budget),
    /// never at device creation, so editor-side devices never load DXCore.
    /// </summary>
    public bool UnifiedMemory => _unifiedMemory ??= MemoryTier.IsUnifiedMemory(IsWarp ? null : DxCore.IsIntegratedCard(AdapterLuid), DedicatedVideoMemory, SharedSystemMemory, IsWarp);

    private bool? _unifiedMemory;

    public TexturePool Textures { get; }

    private GpuDevice() => Textures = new TexturePool(this);

    /// <summary>
    /// Creates a device on adapter <paramref name="adapterIndex"/> (DXGI order), on the high-performance adapter for -1, or on the adapter
    /// matching <paramref name="luid"/> when non-zero. Falls back to WARP when no adapter works and <paramref name="allowWarp"/> is set.
    /// </summary>
    public static GpuDevice Create(int adapterIndex, long luid = 0, bool allowWarp = true)
    {
        var gpu = new GpuDevice();
        try
        {
            gpu.CreateOn(adapterIndex, luid, allowWarp);
            gpu.CreateStates();
            return gpu;
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }

    private void CreateOn(int adapterIndex, long luid, bool allowWarp)
    {
        var factory = Dxgi.CreateFactory();
        nint adapter = 0;
        try
        {
            if (luid != 0)
            {
                for (uint i = 0; i < 16 && adapter == 0; i++)
                {
                    var candidate = Dxgi.EnumAdapter(factory, i);
                    if (candidate == 0)
                        break;
                    if (Dxgi.Describe(candidate).Luid == luid)
                        (adapter, AdapterIndex) = (candidate, (int)i);
                    else
                        Com.Release(ref candidate);
                }
            }
            else if (adapterIndex >= 0)
            {
                adapter = Dxgi.EnumAdapter(factory, (uint)adapterIndex);
                AdapterIndex = adapter != 0 ? adapterIndex : -1;
            }

            if (adapter == 0 && luid == 0)
                adapter = Dxgi.HighPerformanceAdapter(factory);

            var levels = stackalloc uint[2] { D3D11.FeatureLevel11_1, D3D11.FeatureLevel11_0 };
            uint level;
            nint device = 0, context = 0;
            var flags = D3D11.CreateBgraSupport | D3D11.CreateVideoSupport;
            var hr = adapter != 0
                ? Dxgi.D3D11CreateDevice(adapter, D3D11.DriverUnknown, 0, flags, levels, 2, D3D11.SdkVersion, &device, &level, &context)
                : -1;
            if (hr < 0 && adapter != 0)
            {
                // Adapters without video support (or drivers rejecting the flag) can still composite.
                hr = Dxgi.D3D11CreateDevice(adapter, D3D11.DriverUnknown, 0, D3D11.CreateBgraSupport, levels, 2, D3D11.SdkVersion, &device, &level, &context);
            }

            if (hr < 0)
            {
                if (!allowWarp)
                    Com.Check(hr, "D3D11CreateDevice");
                Com.Release(ref adapter);
                Com.Check(Dxgi.D3D11CreateDevice(0, D3D11.DriverWarp, 0, D3D11.CreateBgraSupport, levels, 2, D3D11.SdkVersion, &device, &level, &context), "D3D11CreateDevice (WARP)");
                IsWarp = true;
                AdapterIndex = -1;
            }

            (_device, _context) = (device, context);
            _device1 = Com.Query(_device, Iid.D3D11Device1);

            // Describe the adapter actually used (also when chosen by preference or after the WARP fallback).
            var used = Dxgi.AdapterOf(_device);
            if (used != 0)
            {
                var desc = Dxgi.Describe(used);
                AdapterLuid = desc.Luid;
                AdapterName = Dxgi.Name(desc);
                HandPegApp.Services.GpuNativeBreadcrumbs.Adapter = desc.Luid; // adapter tagged on this process's GPU breadcrumbs
                VendorId = desc.VendorId;
                IsWarp |= (desc.Flags & AdapterDesc1.FlagSoftware) != 0;
                DedicatedVideoMemory = IsWarp ? 0 : (long)desc.DedicatedVideoMemory;
                SharedSystemMemory = IsWarp ? 0 : (long)desc.SharedSystemMemory;
                if (AdapterIndex < 0 && !IsWarp)
                    AdapterIndex = IndexOf(factory, desc.Luid);
                Com.Release(ref used);
            }

            // Media Foundation's decoder calls the device from its own threads.
            var multithread = Com.Query(_device, Iid.D3D11Multithread);
            if (multithread != 0)
            {
                ((delegate* unmanaged<nint, int, int>)Com.Table(multithread)[ID3D11MultithreadSlot.SetMultithreadProtected])(multithread, 1);
                Com.Release(ref multithread);
            }
        }
        finally
        {
            Com.Release(ref adapter);
            Com.Release(ref factory);
        }
    }

    private static int IndexOf(nint factory, long luid)
    {
        for (uint i = 0; i < 16; i++)
        {
            var adapter = Dxgi.EnumAdapter(factory, i);
            if (adapter == 0)
                break;
            var match = Dxgi.Describe(adapter).Luid == luid;
            Com.Release(ref adapter);
            if (match)
                return (int)i;
        }

        return -1;
    }

    private void CreateStates()
    {
        var device = Device;
        _constants = device.CreateBuffer(new BufferDesc
        {
            ByteWidth = (uint)PassConstants.SizeInBytes,
            Usage = D3D11.UsageDynamic,
            BindFlags = D3D11.BindConstantBuffer,
            CpuAccessFlags = D3D11.CpuWrite,
        });

        _linearClamp = device.CreateSamplerState(Sampler(D3D11.FilterLinear, D3D11.AddressClamp));
        _pointClamp = device.CreateSamplerState(Sampler(D3D11.FilterPoint, D3D11.AddressClamp));
        _linearBorder = device.CreateSamplerState(Sampler(D3D11.FilterLinear, D3D11.AddressBorder));
        _linearWrap = device.CreateSamplerState(Sampler(D3D11.FilterLinear, D3D11.AddressWrap));

        // Premultiplied over: src + dst * (1 - src alpha), for colour and alpha.
        var blend = default(BlendDesc);
        blend.Targets[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = D3D11.BlendOne,
            DestBlend = D3D11.BlendInvSrcAlpha,
            BlendOp = D3D11.BlendOpAdd,
            SrcBlendAlpha = D3D11.BlendOne,
            DestBlendAlpha = D3D11.BlendInvSrcAlpha,
            BlendOpAlpha = D3D11.BlendOpAdd,
            WriteMask = 0xF,
        };
        _premultipliedOver = device.CreateBlendState(blend);

        // Additive (shutter sub-frame sums, light particles): src + dst, for colour and alpha.
        var added = blend;
        added.Targets[0].DestBlend = D3D11.BlendOne;
        added.Targets[0].DestBlendAlpha = D3D11.BlendOne;
        _additive = device.CreateBlendState(added);

        // Scissor enabled: every pass sets it to its full target or, for layers on the letterboxed player canvas, the picture rect.
        _rasterizer = device.CreateRasterizerState(new RasterizerDesc { FillMode = D3D11.FillSolid, CullMode = D3D11.CullNone, DepthClipEnable = 1, ScissorEnable = 1 });
    }

    private static SamplerDesc Sampler(uint filter, uint address) => new()
    {
        Filter = filter,
        AddressU = address,
        AddressV = address,
        AddressW = address,
        MaxAnisotropy = 1,
        ComparisonFunc = D3D11.ComparisonNever,
        MaxLod = float.MaxValue,
    };

    /// <summary>Binds the shared baseline state: samplers, constants, rasterizer, no input layout.</summary>
    public void BindCommon()
    {
        var context = Context;
        var samplers = stackalloc nint[4] { _linearClamp, _pointClamp, _linearBorder, _linearWrap };
        context.PSSetSamplers(0, 4, samplers);
        context.PSSetConstantBuffers(0, _constants);
        context.VSSetConstantBuffers(0, _constants);
        context.RSSetState(_rasterizer);
        context.IASetInputLayout(0);
    }

    /// <summary>Premultiplied over (true) or straight overwrite (false).</summary>
    public void SetBlend(bool over) => Context.OMSetBlendState(over ? _premultipliedOver : 0);

    /// <summary>The next draw blends additively.</summary>
    public void SetAdditive() => Context.OMSetBlendState(_additive);

    /// <summary>Uploads the pass constants (dynamic buffer mapped WRITE_DISCARD: the driver renames it, no stall).</summary>
    public void SetConstants(in PassConstants constants)
    {
        var context = Context;
        Com.Check(context.Map(_constants, 0, D3D11.MapWriteDiscard, 0, out var mapped), "Map constants");
        *(PassConstants*)mapped.Data = constants;
        context.Unmap(_constants, 0);
    }

    /// <summary>Throws <see cref="GpuException"/> (device lost) when the device has been removed.</summary>
    public void CheckAlive()
    {
        var reason = Device.GetDeviceRemovedReason();
        if (reason < 0)
            throw new GpuException(reason, "The graphics device");
    }

    /// <summary>
    /// Count of textures created via <see cref="CreateTexture"/> on this device (any thread; decode lanes create theirs too). Published as
    /// ControlBlock.TexturesMade to prove the 3D view's enter/exit creates none after its first frame.
    /// </summary>
    public int TexturesMade => Volatile.Read(ref _texturesMade);

    private int _texturesMade;

    public GpuTexture CreateTexture(int width, int height, uint format, bool renderTarget, uint miscFlags = 0, SubresourceData* initial = null)
    {
        Interlocked.Increment(ref _texturesMade);
        var desc = new Texture2DDesc
        {
            Width = (uint)Math.Max(width, 1),
            Height = (uint)Math.Max(height, 1),
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleCount = 1,
            Usage = D3D11.UsageDefault,
            BindFlags = D3D11.BindShaderResource | (renderTarget ? D3D11.BindRenderTarget : 0),
            MiscFlags = miscFlags,
        };
        var texture = Device.CreateTexture2D(desc, initial);
        nint srv = 0, rtv = 0;
        try
        {
            srv = format is DxgiFormat.Nv12 or DxgiFormat.P010 ? 0 : Device.CreateShaderResourceView(texture);
            rtv = renderTarget ? Device.CreateRenderTargetView(texture) : 0;
        }
        catch
        {
            // View creation failed (memory pressure, unsupported format): release what was created, then rethrow.
            Com.Release(ref srv);
            Com.Release(ref texture);
            throw;
        }

        return new GpuTexture(texture, srv, rtv, width, height, format);
    }

    // IDXGIDevice3 (dxgi1_3.h) interface id; Trim is its first own method (IDXGIDevice3Slot.Trim).
    private static readonly Guid DxgiDevice3 = new("6007896c-3244-4afd-bf18-a6d3beda5023");

    /// <summary>
    /// Returns driver-held memory (worker trim): clears context state (every render rebinds via BindCommon and its passes), flushes queued
    /// work and calls IDXGIDevice3::Trim to release allocations the driver kept for reuse. Call between frames on the drawing thread. False
    /// when Trim is unavailable (it then only clears and flushes).
    /// </summary>
    public bool Trim()
    {
        var context = Context;
        context.ClearState();
        context.Flush();
        var device3 = Com.Query(_device, DxgiDevice3);
        if (device3 == 0)
            return false;
        try
        {
            ((delegate* unmanaged<nint, void>)Com.Table(device3)[IDXGIDevice3Slot.Trim])(device3);
            return true;
        }
        finally
        {
            Com.Release(ref device3);
        }
    }

    /// <summary>Staging texture for CPU readback or upload (tests, benchmark); never on the preview path.</summary>
    public nint CreateStaging(int width, int height, uint format, bool read = true)
    {
        return Device.CreateTexture2D(new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleCount = 1,
            Usage = D3D11.UsageStaging,
            CpuAccessFlags = read ? D3D11.CpuRead : D3D11.CpuWrite,
        });
    }

    public void Dispose()
    {
        Textures.Dispose();
        if (_context != 0)
            Context.ClearState();
        Com.Release(ref _rasterizer);
        Com.Release(ref _additive);
        Com.Release(ref _premultipliedOver);
        Com.Release(ref _linearWrap);
        Com.Release(ref _linearBorder);
        Com.Release(ref _pointClamp);
        Com.Release(ref _linearClamp);
        Com.Release(ref _constants);
        Com.Release(ref _context);
        Com.Release(ref _device1);
        Com.Release(ref _device);
    }
}

/// <summary>A texture with its SRV and RTV (either may be 0).</summary>
internal sealed class GpuTexture(nint texture, nint srv, nint rtv, int width, int height, uint format) : IDisposable
{
    private nint _texture = texture, _srv = srv, _rtv = rtv;

    public nint Texture => _texture;
    public nint Srv => _srv;
    public nint Rtv => _rtv;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint Format { get; } = format;

    /// <summary>True while rented from <see cref="TexturePool"/>.</summary>
    public bool Rented { get; set; }

    public void Dispose()
    {
        Com.Release(ref _rtv);
        Com.Release(ref _srv);
        Com.Release(ref _texture);
    }
}

/// <summary>
/// Render textures reused across passes and frames, keyed by size and format: a frame rents what it needs and returns it all at its end;
/// textures unused for 240 frames are released (a layer left the frame, the player resized).
/// </summary>
internal sealed unsafe class TexturePool(GpuDevice gpu) : IDisposable
{
    private readonly List<(GpuTexture Texture, long LastUsed)> _free = [];
    private readonly List<GpuTexture> _rented = [];
    private long _frame;

    public GpuTexture Rent(int width, int height, uint format = DxgiFormat.R16G16B16A16Float)
    {
        width = Math.Clamp(width, 1, 16384);
        height = Math.Clamp(height, 1, 16384);
        for (var i = 0; i < _free.Count; i++)
        {
            var (texture, _) = _free[i];
            if (texture.Width == width && texture.Height == height && texture.Format == format)
            {
                _free.RemoveAt(i);
                texture.Rented = true;
                _rented.Add(texture);
                return texture;
            }
        }

        var made = gpu.CreateTexture(width, height, format, renderTarget: true);
        made.Rented = true;
        _rented.Add(made);
        return made;
    }

    /// <summary>Returns one texture before the frame ends.</summary>
    public void Return(GpuTexture texture)
    {
        if (!texture.Rented)
            return;
        texture.Rented = false;
        _rented.Remove(texture);
        _free.Add((texture, _frame));
    }

    /// <summary>Returns every rented texture (once a layer is drawn, its working textures are free for the next).</summary>
    public void ReturnAll()
    {
        foreach (var texture in _rented)
        {
            texture.Rented = false;
            _free.Add((texture, _frame));
        }

        _rented.Clear();
    }

    /// <summary>Number of frames ended; returned textures are stamped with their last-use frame.</summary>
    public long Frame => _frame;

    /// <summary>
    /// Releases every non-rented texture and returns the bytes freed (worker trim: between frames nothing is rented, so the pool empties and
    /// the next frame recreates what it needs). Equivalent to <see cref="ReleaseUnusedSince"/> with <see cref="Frame"/> + 1.
    /// </summary>
    public long ReleaseFree()
    {
        long released = 0;
        foreach (var (texture, _) in _free)
        {
            released += BytesOf(texture);
            texture.Dispose();
        }

        _free.Clear();
        return released;
    }

    /// <summary>Number of free (non-rented) textures.</summary>
    public int FreeCount => _free.Count;

    // Pool texture size: RGBA16F working textures are 8 bytes per pixel; anything else is counted as 4.
    private static long BytesOf(GpuTexture texture) =>
        (long)texture.Width * texture.Height * (texture.Format == DxgiFormat.R16G16B16A16Float ? 8 : 4);

    /// <summary>Releases every free texture last used before frame <paramref name="frame"/> (e.g. the 3D view's after it closes).</summary>
    public void ReleaseUnusedSince(long frame)
    {
        for (var i = _free.Count - 1; i >= 0; i--)
        {
            if (_free[i].LastUsed < frame)
            {
                _free[i].Texture.Dispose();
                _free.RemoveAt(i);
            }
        }
    }

    /// <summary>Ends the frame: returns every rented texture and releases those unused for 240 frames.</summary>
    public void EndFrame()
    {
        _frame++;
        foreach (var texture in _rented)
        {
            texture.Rented = false;
            _free.Add((texture, _frame));
        }

        _rented.Clear();
        for (var i = _free.Count - 1; i >= 0; i--)
        {
            if (_frame - _free[i].LastUsed > 240)
            {
                _free[i].Texture.Dispose();
                _free.RemoveAt(i);
            }
        }
    }

    public void Dispose()
    {
        foreach (var texture in _rented)
            texture.Dispose();
        foreach (var (texture, _) in _free)
            texture.Dispose();
        _rented.Clear();
        _free.Clear();
    }
}
