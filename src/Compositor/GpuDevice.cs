using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Compositor;

/// <summary>
/// The compositor's Direct3D 11 device on one card, with the few fixed states every pass shares (three samplers, two
/// blend states, one rasterizer state, the pass constants) and a pool of render textures. Video support is asked for
/// (Media Foundation decodes on it) and the device is multithread-protected, because the decoder uses it from its own threads.
/// </summary>
internal sealed unsafe class GpuDevice : IDisposable
{
    private nint _device;
    private nint _device1;
    private nint _context;
    private nint _constants;
    private nint _linearClamp, _pointClamp, _linearBorder;
    private nint _premultipliedOver, _rasterizer;

    public D3D11Device Device => new(_device);

    /// <summary>The same device as an ID3D11Device1 (shared resources by name).</summary>
    public D3D11Device Device1 => new(_device1);

    public D3D11Context Context => new(_context);

    public nint DevicePointer => _device;

    public long AdapterLuid { get; private set; }

    /// <summary>The card's place in DXGI's list (the number the GPU roles and FFmpeg's -hwaccel_device use); -1 for WARP.</summary>
    public int AdapterIndex { get; private set; } = -1;

    public string AdapterName { get; private set; } = "";

    public uint VendorId { get; private set; }

    /// <summary>The processor's software renderer: no hardware to decode with.</summary>
    public bool IsWarp { get; private set; }

    public TexturePool Textures { get; }

    private GpuDevice() => Textures = new TexturePool(this);

    /// <summary>
    /// A device on the card at <paramref name="adapterIndex"/> (DXGI's order), on the one Windows calls high-performance
    /// for -1, or on a card found by its LUID when <paramref name="luid"/> is not 0. Falls back to WARP when no card works
    /// and <paramref name="allowWarp"/>.
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
                // A card without video support (or a driver that refuses the flag) still composites.
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

            // The card actually used (also when chosen by preference or after WARP).
            var used = Dxgi.AdapterOf(_device);
            if (used != 0)
            {
                var desc = Dxgi.Describe(used);
                AdapterLuid = desc.Luid;
                AdapterName = Dxgi.Name(desc);
                VendorId = desc.VendorId;
                IsWarp |= (desc.Flags & AdapterDesc1.FlagSoftware) != 0;
                if (AdapterIndex < 0 && !IsWarp)
                    AdapterIndex = IndexOf(factory, desc.Luid);
                Com.Release(ref used);
            }

            // Media Foundation's decoder calls the device from its own threads.
            var multithread = Com.Query(_device, Iid.D3D11Multithread);
            if (multithread != 0)
            {
                ((delegate* unmanaged<nint, int, int>)Com.Table(multithread)[5])(multithread, 1);
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

        // Premultiplied "over": source + destination * (1 - source alpha), for colour and alpha alike.
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

        // The scissor is on: every pass sets it, to its whole target or (a layer laid on the player's letterboxed canvas) the picture.
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

    /// <summary>The states every pass starts from: the samplers, the constants, the rasterizer, triangles, no input layout.</summary>
    public void BindCommon()
    {
        var context = Context;
        var samplers = stackalloc nint[3] { _linearClamp, _pointClamp, _linearBorder };
        context.PSSetSamplers(0, 3, samplers);
        context.PSSetConstantBuffers(0, _constants);
        context.VSSetConstantBuffers(0, _constants);
        context.RSSetState(_rasterizer);
        context.IASetInputLayout(0);
    }

    /// <summary>Premultiplied "over" (true), or writing straight over what is there (false).</summary>
    public void SetBlend(bool over) => Context.OMSetBlendState(over ? _premultipliedOver : 0);

    /// <summary>Writes the pass constants (a dynamic buffer, discarded each time: the driver renames it, no stall).</summary>
    public void SetConstants(in PassConstants constants)
    {
        var context = Context;
        Com.Check(context.Map(_constants, 0, D3D11.MapWriteDiscard, 0, out var mapped), "Map constants");
        *(PassConstants*)mapped.Data = constants;
        context.Unmap(_constants, 0);
    }

    /// <summary>Throws <see cref="GpuException"/> (device lost) when the card has gone away.</summary>
    public void CheckAlive()
    {
        var reason = Device.GetDeviceRemovedReason();
        if (reason < 0)
            throw new GpuException(reason, "The graphics device");
    }

    public GpuTexture CreateTexture(int width, int height, uint format, bool renderTarget, uint miscFlags = 0, SubresourceData* initial = null)
    {
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
        var srv = format is DxgiFormat.Nv12 or DxgiFormat.P010 ? 0 : Device.CreateShaderResourceView(texture);
        var rtv = renderTarget ? Device.CreateRenderTargetView(texture) : 0;
        return new GpuTexture(texture, srv, rtv, width, height, format);
    }

    /// <summary>A staging texture to read a picture back (tests, the benchmark): never on the preview's path.</summary>
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
        Com.Release(ref _premultipliedOver);
        Com.Release(ref _linearBorder);
        Com.Release(ref _pointClamp);
        Com.Release(ref _linearClamp);
        Com.Release(ref _constants);
        Com.Release(ref _context);
        Com.Release(ref _device1);
        Com.Release(ref _device);
    }
}

/// <summary>A texture with its views (any of which may be 0).</summary>
internal sealed class GpuTexture(nint texture, nint srv, nint rtv, int width, int height, uint format) : IDisposable
{
    private nint _texture = texture, _srv = srv, _rtv = rtv;

    public nint Texture => _texture;
    public nint Srv => _srv;
    public nint Rtv => _rtv;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint Format { get; } = format;

    /// <summary>Set by <see cref="TexturePool"/> while rented.</summary>
    public bool Rented { get; set; }

    public void Dispose()
    {
        Com.Release(ref _rtv);
        Com.Release(ref _srv);
        Com.Release(ref _texture);
    }
}

/// <summary>
/// Render textures reused across passes and frames, by size and format: a frame rents what it needs and gives it all
/// back at its end; what went unused for a few seconds is let go (a layer that left the frame, a resized player).
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

    /// <summary>Gives one texture back before the frame ends.</summary>
    public void Return(GpuTexture texture)
    {
        if (!texture.Rented)
            return;
        texture.Rented = false;
        _rented.Remove(texture);
        _free.Add((texture, _frame));
    }

    /// <summary>Every rented texture comes back (a layer is drawn: its working textures are free for the next).</summary>
    public void ReturnAll()
    {
        foreach (var texture in _rented)
        {
            texture.Rented = false;
            _free.Add((texture, _frame));
        }

        _rented.Clear();
    }

    /// <summary>How many frames have ended (a texture given back is marked with the frame it was last used in).</summary>
    public long Frame => _frame;

    /// <summary>Releases every free texture last used before frame <paramref name="frame"/> (the 3D view's, once it has gone).</summary>
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

    /// <summary>The frame is over: every rented texture comes back; ones unused for 240 frames are released.</summary>
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
