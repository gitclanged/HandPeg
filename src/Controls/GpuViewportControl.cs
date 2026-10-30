using System.Runtime.InteropServices;
using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// The GPU engine's picture in the player (Pass 5.0-AudioAnd3D, 5.0D): a flip-model swap chain on the video surface's
/// native window (<see cref="VideoSurface.SurfaceHandle"/>, the window mpv draws into when it is the engine), filled by
/// copying the compositor's newest presented output texture into the back buffer, card to card, and presented. No pixel
/// reaches the processor. The surface keeps everything else it does: black before the first frame, hidden until laid
/// out, the overlay window above it (gizmos, crop, the key map) and its place through dock rebuilds.
/// <para>One thread uses it (the GPU engine's frame pump): the reader's device and the swap chain belong to that thread.
/// The swap chain has a frame-latency waitable object (one frame queued at most), so <see cref="WaitForTurn"/> paces the
/// pump at the display's refresh while playing and a picture is never more than a frame old when it is shown.</para>
/// <para>A lost device (a TDR) or a worker of a new generation on another card makes the reader's device anew; the
/// swap chain follows it on the next present.</para>
/// </summary>
internal sealed unsafe partial class GpuViewportControl : IDisposable
{
    private readonly CompositorClient _client;
    private readonly CompositorFrameReader _reader;
    private readonly nint _window;
    private GpuDevice? _device;
    private nint _swapChain;
    private nint _waitable;
    private (int Width, int Height) _buffers;
    private int _framesSeen = -1;
    private int _generationSeen = -1;

    public GpuViewportControl(CompositorClient client, nint window)
    {
        _client = client;
        _reader = new CompositorFrameReader(client);
        _window = window;
    }

    /// <summary>The window's size in pixels now (what the worker is asked to render at); (0, 0) while it is hidden or gone.</summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            if (!IsWindow(_window) || !GetClientRect(_window, out var rect))
                return (0, 0);
            return (Math.Max(rect.Right - rect.Left, 0), Math.Max(rect.Bottom - rect.Top, 0));
        }
    }

    /// <summary>Frames presented on the screen (diagnostics).</summary>
    public long Presents { get; private set; }

    /// <summary>
    /// Waits until the swap chain can take another frame (its frame-latency object), at most <paramref name="milliseconds"/>.
    /// Without a swap chain yet, it waits that long.
    /// </summary>
    public void WaitForTurn(int milliseconds)
    {
        if (_waitable != 0)
            _ = WaitForSingleObject(_waitable, (uint)milliseconds);
        else
            Thread.Sleep(Math.Min(milliseconds, 16));
    }

    /// <summary>Whether the worker presented a frame this viewport has not shown yet.</summary>
    public bool HasNewFrame =>
        Volatile.Read(ref _client.Memory.Control.FramesRendered) != _framesSeen || Volatile.Read(ref _client.Memory.Control.OutputGeneration) != _generationSeen;

    /// <summary>
    /// Copies the worker's newest frame into the back buffer and presents it (with <paramref name="vsync"/>, on the next
    /// refresh). False when there was no new frame (and <paramref name="force"/> is not set), the sizes differ for a
    /// frame (a resize under way: the next one will match), or there is nothing to present on yet.
    /// </summary>
    public bool PresentNewest(bool vsync, bool force = false)
    {
        ref var control = ref _client.Memory.Control;
        var frames = Volatile.Read(ref control.FramesRendered);
        var generation = Volatile.Read(ref control.OutputGeneration);
        if (!force && frames == _framesSeen && generation == _generationSeen)
            return false;
        if (!_reader.Ready() || _reader.Device is not { } device)
            return false;

        try
        {
            var (width, height) = PixelSize;
            if (width < 8 || height < 8)
                return false;
            if (!ReferenceEquals(device, _device) || _swapChain == 0)
                MakeSwapChain(device, width, height);
            else if (_buffers != (width, height))
                Resize(width, height);

            // A frame of the old size while the window was resized: kept for the one at the new size.
            if ((control.OutputWidth, control.OutputHeight) != _buffers)
                return false;

            var back = Dxgi.BackBuffer(_swapChain);
            try
            {
                if (!_reader.CopyPresentedTo(back, 8))
                    return false;
            }
            finally
            {
                Com.Release(ref back);
            }

            (_framesSeen, _generationSeen) = (frames, generation);
            var hr = Dxgi.Present(_swapChain, vsync ? 1u : 0u, 0);
            if (Com.IsDeviceLost(hr))
            {
                DropSwapChain();
                return false;
            }

            Presents++;
            return hr >= 0;
        }
        catch (GpuException ex)
        {
            // A device gone under the swap chain (a TDR) or a window that went away: made again on the next present.
            AppLog.Write("GPU viewport: " + ex.Message);
            DropSwapChain();
            return false;
        }
    }

    private void MakeSwapChain(GpuDevice device, int width, int height)
    {
        DropSwapChain();
        _swapChain = Dxgi.CreateSwapChain(device.DevicePointer, _window, width, height);
        _waitable = Dxgi.FrameLatencyWaitable(_swapChain);
        (_device, _buffers) = (device, (width, height));
    }

    private void Resize(int width, int height)
    {
        // Nothing holds a buffer here between presents (the back buffer is let go after each copy).
        var hr = Dxgi.ResizeBuffers(_swapChain, width, height);
        if (hr < 0)
        {
            DropSwapChain();
            return;
        }

        _buffers = (width, height);
    }

    private void DropSwapChain()
    {
        if (_waitable != 0)
        {
            _ = CloseHandle(_waitable);
            _waitable = 0;
        }

        if (_swapChain != 0)
        {
            Com.Release(ref _swapChain);

            // Destroyed for real (the window free for another swap chain, or for libmpv) once the context lets go of it.
            if (_device is { } device && device.Device.GetDeviceRemovedReason() >= 0)
            {
                device.Context.ClearState();
                device.Context.Flush();
            }
        }

        (_device, _buffers) = (null, (0, 0));
    }

    public void Dispose()
    {
        DropSwapChain();
        _reader.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
