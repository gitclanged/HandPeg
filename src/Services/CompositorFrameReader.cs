using System.Runtime.InteropServices;
using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services;

/// <summary>
/// Client side of the compositor output: a D3D11 device on the worker's adapter that opens its shared output textures by name
/// (ID3D11Device1::OpenSharedResourceByName, keyed mutex key 0).
/// <para>Hot path: GpuViewport copies the presented texture into its swap chain (<see cref="CopyPresentedCentered"/>, VRAM only); side
/// windows' Live Video Preview copies the canvas region (<see cref="CopyPresentedRegionTo"/>). <see cref="TryRead"/> reads back to the
/// CPU for goldens and diagnostics only.</para>
/// <para>Frames may be smaller than their texture (a resized player reuses the ring while it fits): copies use the slot's content size
/// (<see cref="ContentSize"/>), read while the slot is held.</para>
/// </summary>
public sealed unsafe class CompositorFrameReader : IDisposable
{
    private readonly CompositorClient _client;
    private GpuDevice? _gpu;
    private int _generation = -1;
    private readonly (nint Texture, nint Mutex)[] _opened = new (nint, nint)[CompositorProtocol.OutputRing];
    private nint _staging;
    private (int Width, int Height) _stagingSize;

    // Opened texture size (the ring of the current generation).
    private (int Width, int Height) _ring;

    public CompositorFrameReader(CompositorClient client) => _client = client;

    /// <summary>The sequence number of the frame read last.</summary>
    public long Sequence { get; private set; }

    /// <summary>
    /// True when the last <see cref="CopyPresentedCentered"/> or <see cref="CopyPresentedRegionTo"/> skipped a capture frame (scopes,
    /// thumbnail, tab still) not meant for display; the caller treats it as consumed and shows the next.
    /// </summary>
    internal bool SkippedReadback { get; private set; }

    /// <summary>Reader device (null until first open); hosts the GPU viewport's swap chain.</summary>
    internal GpuDevice? Device => _gpu;

    /// <summary>Opens or reopens the worker output (new generation, lost device); false when none exists.</summary>
    internal bool Ready() => Open();

    private bool Open()
    {
        ref var control = ref _client.Memory.Control;
        var generation = Volatile.Read(ref control.OutputGeneration);
        if (generation <= 0)
            return false;
        if (_gpu is not null && generation == _generation)
            return true;

        // New generation (respawn, resize): textures are new and the device may be lost (TDR removes all devices on the adapter) or
        // on another adapter (WARP fallback).
        Close();
        if (_gpu is not null && (_gpu.Device.GetDeviceRemovedReason() < 0 || _gpu.AdapterLuid != control.AdapterLuid))
        {
            Com.Release(ref _staging);
            _gpu.Dispose();
            _gpu = null;
        }

        _gpu ??= GpuDevice.Create(-1, control.AdapterLuid, allowWarp: control.IsSoftwareDevice != 0);
        for (var i = 0; i < _opened.Length; i++)
        {
            var name = CompositorProtocol.OutputTexture(_client.BaseName, generation, i);
            if (_gpu.Device1.OpenSharedResourceByName(name, D3D11.SharedRead | D3D11.SharedWrite, Iid.D3D11Texture2D, out var texture) < 0)
            {
                Close();
                return false;
            }

            _opened[i] = (texture, Com.Query(texture, Iid.DxgiKeyedMutex));
        }

        // Ring size from its textures (not control.OutputWidth, which may already describe a newer ring); kept only if no newer ring appeared.
        Texture2DDesc desc;
        ((delegate* unmanaged<nint, Texture2DDesc*, void>)Com.Table(_opened[0].Texture)[ID3D11Texture2DSlot.GetDesc])(_opened[0].Texture, &desc);
        _ring = ((int)desc.Width, (int)desc.Height);
        if (Volatile.Read(ref control.OutputGeneration) != generation)
        {
            Close();
            return false;
        }

        _generation = generation;
        return true;
    }

    /// <summary>
    /// Content size of output slot <paramref name="index"/>, clamped to the opened textures. Exact only while the slot's keyed mutex is
    /// held; 0 x 0 when last drawn into a different ring (a new ring pending).
    /// </summary>
    internal (int Width, int Height) ContentSize(int index)
    {
        ref var control = ref _client.Memory.Control;
        if (control.OutputContentTag(index) >> 1 != _generation)
            return (0, 0);

        // Validate against the opened textures, not the control block's ring size (which may be newer).
        var (width, height) = control.OutputContentWritten(index);
        return width <= 0 || height <= 0 ? _ring : (Math.Min(width, _ring.Width), Math.Min(height, _ring.Height));
    }

    /// <summary>A frame read back (a capture) in the slot: never the player's or a mirror's to show.</summary>
    internal bool ReadbackIn(int index) => (_client.Memory.Control.OutputContentTag(index) & 1) != 0;

    /// <summary>
    /// Zero-copy player path: copies the presented frame into a BGRA8 <paramref name="targetWidth"/> x <paramref name="targetHeight"/>
    /// texture. Exact size match is one CopyResource; otherwise the frame is centred 1:1, never scaled (GpuViewport.CenteredCopy), after
    /// clearing via <paramref name="destinationRtv"/> (0: no clear). Waits up to <paramref name="milliseconds"/>.
    /// </summary>
    internal bool CopyPresentedCentered(nint destination, nint destinationRtv, int targetWidth, int targetHeight, uint milliseconds = 16)
    {
        SkippedReadback = false;
        if (!Open())
            return false;
        ref var control = ref _client.Memory.Control;
        var index = Volatile.Read(ref control.PresentedIndex);
        var (texture, mutex) = _opened[index];
        if (Dxgi.AcquireSync(mutex, 0, milliseconds) != 0)
            return false;
        try
        {
            var context = _gpu!.Context;
            if (ReadbackIn(index))
            {
                SkippedReadback = true;
                return false;
            }

            // The held slot's own scene number (UGLY-07), read under its mutex with the copy, so the viewport reports the scene it shows
            // (never a newer slot's number). A slot already redrawn for a newer scene is shown as before, with that scene's number.
            var sequence = control.OutputContentSequence(index);
            var content = ContentSize(index);
            if (content.Width < 1 || content.Height < 1)
                return false;
            if (content == _ring && content == (targetWidth, targetHeight))
            {
                context.CopyResource(destination, texture);
            }
            else
            {
                // Clear only when the frame leaves margins.
                if (destinationRtv != 0 && (content.Width < targetWidth || content.Height < targetHeight))
                    context.ClearRenderTargetView(destinationRtv, 0, 0, 0, 1);
                var (left, top, right, bottom, x, y) = GpuViewport.CenteredCopy(content.Width, content.Height, targetWidth, targetHeight);
                if (right > left && bottom > top)
                {
                    var box = new Box { Left = (uint)left, Top = (uint)top, Front = 0, Right = (uint)right, Bottom = (uint)bottom, Back = 1 };
                    context.CopySubresourceRegion(destination, 0, (uint)x, (uint)y, texture, 0, &box);
                }
            }

            Sequence = sequence;
        }
        finally
        {
            _ = Dxgi.ReleaseSync(mutex, 0);
        }

        return true;
    }

    /// <summary>
    /// Copies a frame rectangle (clamped to content size) to (<paramref name="x"/>, <paramref name="y"/>) of a BGRA8 texture for side
    /// window Live Video Preview (canvas without letterbox). Waits up to <paramref name="milliseconds"/> (0: no wait; false if busy or
    /// empty).
    /// </summary>
    internal bool CopyPresentedRegionTo(nint destination, int left, int top, int right, int bottom, uint milliseconds = 0, int x = 0, int y = 0)
    {
        SkippedReadback = false;
        if (!Open())
            return false;
        ref var control = ref _client.Memory.Control;
        var index = Volatile.Read(ref control.PresentedIndex);
        var (texture, mutex) = _opened[index];
        if (Dxgi.AcquireSync(mutex, 0, milliseconds) != 0)
            return false;
        try
        {
            if (ReadbackIn(index))
            {
                SkippedReadback = true;
                return false;
            }

            var (width, height) = ContentSize(index);
            (left, top) = (Math.Max(0, Math.Min(left, width)), Math.Max(0, Math.Min(top, height)));
            (right, bottom) = (Math.Max(left, Math.Min(right, width)), Math.Max(top, Math.Min(bottom, height)));
            if (right - left < 1 || bottom - top < 1)
                return false;

            var box = new Box { Left = (uint)left, Top = (uint)top, Front = 0, Right = (uint)right, Bottom = (uint)bottom, Back = 1 };
            _gpu!.Context.CopySubresourceRegion(destination, 0, (uint)Math.Max(x, 0), (uint)Math.Max(y, 0), texture, 0, &box);
        }
        finally
        {
            _ = Dxgi.ReleaseSync(mutex, 0);
        }

        return true;
    }

    /// <summary>
    /// CPU readback of the presented frame (BGRA8, packed rows) at its content size; tests and diagnostics only.
    /// </summary>
    public bool TryRead(out byte[] bgra, out int width, out int height)
    {
        bgra = [];
        (width, height) = (0, 0);
        if (!Open())
            return false;

        ref var control = ref _client.Memory.Control;
        var index = Volatile.Read(ref control.PresentedIndex);
        var (texture, mutex) = _opened[index];
        if (Dxgi.AcquireSync(mutex, 0, 1000) != 0)
            return false;
        var context = _gpu!.Context;
        try
        {
            (width, height) = ContentSize(index);
            if (width < 1 || height < 1)
                return false;
            if (_staging == 0 || _stagingSize != (width, height))
            {
                Com.Release(ref _staging);
                _staging = _gpu.CreateStaging(width, height, DxgiFormat.B8G8R8A8Unorm);
                _stagingSize = (width, height);
            }

            var box = new Box { Left = 0, Top = 0, Front = 0, Right = (uint)width, Bottom = (uint)height, Back = 1 };
            context.CopySubresourceRegion(_staging, 0, 0, 0, texture, 0, &box);

            // The held slot's own scene number, read under its mutex with the copy (UGLY-07).
            Sequence = control.OutputContentSequence(index);
        }
        finally
        {
            _ = Dxgi.ReleaseSync(mutex, 0);
        }

        Com.Check(context.Map(_staging, 0, D3D11.MapRead, 0, out var mapped), "Map");
        try
        {
            bgra = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                new ReadOnlySpan<byte>(mapped.Data + (long)y * mapped.RowPitch, width * 4).CopyTo(bgra.AsSpan(y * width * 4));
        }
        finally
        {
            context.Unmap(_staging, 0);
        }

        return true;
    }

    /// <summary>
    /// Immediate decimated readback of <paramref name="region"/> (every <paramref name="step"/>-th pixel) for a background tab's playhead
    /// still; the staging texture is released afterwards. False for capture frames or no frame.
    /// </summary>
    internal bool TryReadStill(Func<int, int, (int Left, int Top, int Right, int Bottom)> region, Func<int, int, int> step, out byte[] bgra, out int width, out int height)
    {
        bgra = [];
        (width, height) = (0, 0);
        if (!Open())
            return false;

        ref var control = ref _client.Memory.Control;
        var index = Volatile.Read(ref control.PresentedIndex);
        var (texture, mutex) = _opened[index];
        if (Dxgi.AcquireSync(mutex, 0, 16) != 0)
            return false;
        var context = _gpu!.Context;
        nint staging = 0;
        int left, top, every;
        try
        {
            if (ReadbackIn(index))
                return false;
            var content = ContentSize(index);
            var (l, t, r, b) = region(content.Width, content.Height);
            (left, top) = (Math.Clamp(l, 0, content.Width), Math.Clamp(t, 0, content.Height));
            var (right, bottom) = (Math.Clamp(r, left, content.Width), Math.Clamp(b, top, content.Height));
            if (right - left < 2 || bottom - top < 2)
                return false;
            every = Math.Max(step(right - left, bottom - top), 1);
            (width, height) = ((right - left) / every, (bottom - top) / every);
            if (width < 1 || height < 1)
                return false;
            staging = _gpu.CreateStaging(right - left, bottom - top, DxgiFormat.B8G8R8A8Unorm);
            var box = new Box { Left = (uint)left, Top = (uint)top, Front = 0, Right = (uint)right, Bottom = (uint)bottom, Back = 1 };
            context.CopySubresourceRegion(staging, 0, 0, 0, texture, 0, &box);
        }
        finally
        {
            _ = Dxgi.ReleaseSync(mutex, 0);
        }

        try
        {
            Com.Check(context.Map(staging, 0, D3D11.MapRead, 0, out var mapped), "Map");
            try
            {
                bgra = new byte[width * height * 4];
                var pixels = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
                for (var y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<uint>((void*)(mapped.Data + (long)y * every * mapped.RowPitch), width * every);
                    var into = pixels.Slice(y * width, width);
                    for (var x = 0; x < width; x++)
                        into[x] = row[x * every];
                }
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        finally
        {
            Com.Release(ref staging);
        }

        return true;
    }

    private void Close()
    {
        for (var i = 0; i < _opened.Length; i++)
        {
            var (texture, mutex) = _opened[i];
            Com.Release(ref mutex);
            Com.Release(ref texture);
            _opened[i] = default;
        }

        _generation = -1;
    }

    public void Dispose()
    {
        Close();
        Com.Release(ref _staging);
        _gpu?.Dispose();
        _gpu = null;
    }
}
