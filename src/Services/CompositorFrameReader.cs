using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services;

/// <summary>
/// The other end of the compositor's output (Pass 5.0-Core): a Direct3D 11 device of this process on the worker's card,
/// with the worker's shared output textures opened by name (ID3D11Device1::OpenSharedResourceByName, keyed mutex key 0).
/// <para>The hot path (5.0D's GpuViewportControl) copies the presented texture into its own swap chain on the card:
/// <see cref="CopyPresentedCentered"/>, no pixel leaves video memory; a side window's Live Video Preview copies the canvas's part of it
/// (5.4C, <see cref="CopyPresentedRegionTo"/>) on the same device. <see cref="TryRead"/> brings one frame back to the
/// processor, for the golden tests and diagnostics only.</para>
/// <para>Since 5.4.1A a frame can be smaller than the texture it is in (a resized player keeps the worker's ring while it fits): every
/// copy takes the frame's own size, the slot's content size (<see cref="ContentSize"/>), read while the slot is held.</para>
/// </summary>
public sealed unsafe class CompositorFrameReader : IDisposable
{
    private readonly CompositorClient _client;
    private GpuDevice? _gpu;
    private int _generation = -1;
    private readonly (nint Texture, nint Mutex)[] _opened = new (nint, nint)[CompositorProtocol.OutputRing];
    private nint _staging;
    private (int Width, int Height) _stagingSize;

    // The size of the textures opened (the ring of their generation, 5.4.1A).
    private (int Width, int Height) _ring;

    public CompositorFrameReader(CompositorClient client) => _client = client;

    /// <summary>The sequence number of the frame read last.</summary>
    public long Sequence { get; private set; }

    /// <summary>
    /// Whether the last <see cref="CopyPresentedCentered"/> or <see cref="CopyPresentedRegionTo"/> copied nothing because the presented
    /// frame was read back (a capture: the scopes' picture, a thumbnail, a tab's still), never the player's to show (5.4.1A): the caller
    /// takes it as seen, and shows the next.
    /// </summary>
    internal bool SkippedReadback { get; private set; }

    /// <summary>This reader's device (null until the output was first opened): the GPU viewport's swap chain lives on it.</summary>
    internal GpuDevice? Device => _gpu;

    /// <summary>Opens the worker's output when there is one (a new worker generation, a lost device); false while there is none.</summary>
    internal bool Ready() => Open();

    private bool Open()
    {
        ref var control = ref _client.Memory.Control;
        var generation = Volatile.Read(ref control.OutputGeneration);
        if (generation <= 0)
            return false;
        if (_gpu is not null && generation == _generation)
            return true;

        // A new worker generation (a respawn, a resized output): the textures are new, and the device may be gone too (a
        // real TDR removes every device on the card, this one's as well) or on another card (WARP after a failure).
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

        // The ring's size from its own textures (never control.OutputWidth: a newer ring's size can be written there before its
        // generation), and kept only if no newer ring was announced meanwhile.
        Texture2DDesc desc;
        ((delegate* unmanaged<nint, Texture2DDesc*, void>)Com.Table(_opened[0].Texture)[10])(_opened[0].Texture, &desc);
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
    /// The size of the frame in output slot <paramref name="index"/> (5.4.1A): the worker's content size, kept inside the textures this
    /// reader opened. Exact only while the slot is held (its keyed mutex); outside it, the size of the frame there a moment ago. Empty
    /// (0 x 0) when the slot was last drawn into another ring than the one opened (a new ring being announced): not this frame's size.
    /// </summary>
    internal (int Width, int Height) ContentSize(int index)
    {
        ref var control = ref _client.Memory.Control;
        if (control.OutputContentTag(index) >> 1 != _generation)
            return (0, 0);

        // The slot's own size against the textures opened (never the control block's ring size: a newer ring's may be there already).
        var (width, height) = control.OutputContentWritten(index);
        return width <= 0 || height <= 0 ? _ring : (Math.Min(width, _ring.Width), Math.Min(height, _ring.Height));
    }

    /// <summary>A frame read back (a capture) in the slot: never the player's or a mirror's to show.</summary>
    internal bool ReadbackIn(int index) => (_client.Memory.Control.OutputContentTag(index) & 1) != 0;

    /// <summary>
    /// The presented frame copied into a texture of this reader's device (BGRA8, <paramref name="targetWidth"/> x <paramref name="targetHeight"/>):
    /// the player's zero-copy path. A frame of the target's size in a texture of that size is one CopyResource; any other (a resize
    /// under way, a ring larger than its frame) is laid 1:1 in the middle of the target, never scaled (5.4.1A,
    /// GpuViewportControl.CenteredCopy): the target cleared black through <paramref name="destinationRtv"/> first (0: not cleared),
    /// then the frame's middle copied in whole pixels. Waits at most <paramref name="milliseconds"/> for the frame.
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

            var content = ContentSize(index);
            if (content.Width < 1 || content.Height < 1)
                return false;
            if (content == _ring && content == (targetWidth, targetHeight))
            {
                context.CopyResource(destination, texture);
            }
            else
            {
                // Black only where the frame leaves margins (a ring larger than its frame alone is copied over the whole target).
                if (destinationRtv != 0 && (content.Width < targetWidth || content.Height < targetHeight))
                    context.ClearRenderTargetView(destinationRtv, 0, 0, 0, 1);
                var (left, top, right, bottom, x, y) = GpuViewportControl.CenteredCopy(content.Width, content.Height, targetWidth, targetHeight);
                if (right > left && bottom > top)
                {
                    var box = new Box { Left = (uint)left, Top = (uint)top, Front = 0, Right = (uint)right, Bottom = (uint)bottom, Back = 1 };
                    context.CopySubresourceRegion(destination, 0, (uint)x, (uint)y, texture, 0, &box);
                }
            }

            Sequence = Volatile.Read(ref control.PresentedSequence);
        }
        finally
        {
            _ = Dxgi.ReleaseSync(mutex, 0);
        }

        return true;
    }

    /// <summary>
    /// A rectangle of the presented frame (left, top, right, bottom in its pixels, kept inside the frame's own size, 5.4.1A) copied to
    /// (<paramref name="x"/>, <paramref name="y"/>) of a texture of this reader's device (BGRA8, at least that large; the top-left corner
    /// by default): a side window's Live Video Preview, the canvas without the player's bars (5.4C). Waits at most
    /// <paramref name="milliseconds"/> for the frame (0: never waits; false when it is busy or the rectangle is empty).
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
    /// The presented frame's pixels, BGRA8 rows packed (tests and diagnostics only: a readback), at exactly the size the worker drew it
    /// (its slot's content size, 5.4.1A, never the ring's).
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
            Sequence = Volatile.Read(ref control.PresentedSequence);
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
