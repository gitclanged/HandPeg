using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services;

/// <summary>
/// The other end of the compositor's output (Pass 5.0-Core): a Direct3D 11 device of this process on the worker's card,
/// with the worker's shared output textures opened by name (ID3D11Device1::OpenSharedResourceByName, keyed mutex key 0).
/// <para>The hot path (5.0D's GpuViewportControl) copies the presented texture into its own swap chain on the card:
/// <see cref="CopyPresentedTo"/>, no pixel leaves video memory. <see cref="TryRead"/> brings one frame back to the
/// processor, for the golden tests and diagnostics only.</para>
/// </summary>
public sealed unsafe class CompositorFrameReader : IDisposable
{
    private readonly CompositorClient _client;
    private GpuDevice? _gpu;
    private int _generation = -1;
    private readonly (nint Texture, nint Mutex)[] _opened = new (nint, nint)[CompositorProtocol.OutputRing];
    private nint _staging;
    private (int Width, int Height) _stagingSize;

    public CompositorFrameReader(CompositorClient client) => _client = client;

    /// <summary>The sequence number of the frame read last.</summary>
    public long Sequence { get; private set; }

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

        _generation = generation;
        return true;
    }

    /// <summary>The presented frame copied into a texture of this reader's device (same size, BGRA8): the zero-copy path.</summary>
    internal bool CopyPresentedTo(nint destination, uint milliseconds = 16)
    {
        if (!Open())
            return false;
        ref var control = ref _client.Memory.Control;
        var index = Volatile.Read(ref control.PresentedIndex);
        var (texture, mutex) = _opened[index];
        if (Dxgi.AcquireSync(mutex, 0, milliseconds) != 0)
            return false;
        try
        {
            _gpu!.Context.CopyResource(destination, texture);
            Sequence = Volatile.Read(ref control.PresentedSequence);
        }
        finally
        {
            _ = Dxgi.ReleaseSync(mutex, 0);
        }

        return true;
    }

    /// <summary>The presented frame's pixels, BGRA8 rows packed (tests and diagnostics only: a readback).</summary>
    public bool TryRead(out byte[] bgra, out int width, out int height)
    {
        bgra = [];
        (width, height) = (0, 0);
        if (!Open())
            return false;

        ref var control = ref _client.Memory.Control;
        (width, height) = (control.OutputWidth, control.OutputHeight);
        if (_staging == 0 || _stagingSize != (width, height))
        {
            Com.Release(ref _staging);
            _staging = _gpu!.CreateStaging(width, height, DxgiFormat.B8G8R8A8Unorm);
            _stagingSize = (width, height);
        }

        if (!CopyPresentedTo(_staging, 1000))
            return false;

        var context = _gpu!.Context;
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
