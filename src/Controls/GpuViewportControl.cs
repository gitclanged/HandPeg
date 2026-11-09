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

    // A mirror (5.4C) shares the player's reader, and so its device: it never disposes it.
    private readonly bool _ownsReader = true;

    public GpuViewportControl(CompositorClient client, nint window)
    {
        _client = client;
        _reader = new CompositorFrameReader(client);
        _window = window;
    }

    /// <summary>
    /// A mirror of <paramref name="player"/> (5.4C: a side window's Live Video Preview) on another native window: the player's reader
    /// and device (no second device, no second opening of the worker's output), its own swap chain, as large as the canvas's part of
    /// the player's picture (<see cref="MirrorRegion"/>). Used by the pump's thread only, as the player's is.
    /// </summary>
    public GpuViewportControl(GpuViewportControl player, nint window)
    {
        _client = player._client;
        _reader = player._reader;
        _window = window;
        _ownsReader = false;
    }

    /// <summary>The native window it presents into.</summary>
    public nint Window => _window;

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

    /// <summary>
    /// The part of the player's output a Live Video Preview shows (5.4C): the canvas fitted into the output and centred, as the player
    /// lays it at no zoom (SceneRenderer.Letterbox), so the bars are left out; whole pixels, inside the output (left, top, right,
    /// bottom). Empty (all zero) for an output or canvas of no size. Pure: the golden test side-window-mirror checks it.
    /// </summary>
    internal static (int Left, int Top, int Right, int Bottom) MirrorRegion(int outputWidth, int outputHeight, int canvasWidth, int canvasHeight)
    {
        if (outputWidth <= 0 || outputHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0)
            return (0, 0, 0, 0);

        var fit = Math.Min((double)outputWidth / canvasWidth, (double)outputHeight / canvasHeight);
        var (width, height) = (canvasWidth * fit, canvasHeight * fit);
        var left = Math.Clamp((int)Math.Round((outputWidth - width) / 2), 0, outputWidth);
        var top = Math.Clamp((int)Math.Round((outputHeight - height) / 2), 0, outputHeight);
        var right = Math.Clamp((int)Math.Round((outputWidth + width) / 2), left, outputWidth);
        var bottom = Math.Clamp((int)Math.Round((outputHeight + height) / 2), top, outputHeight);
        return (left, top, right, bottom);
    }

    /// <summary>
    /// A mirror's turn (5.4C), after the player presented: the canvas's part of the newest presented frame copied into its back buffer
    /// (one region copy on the card) and presented with no wait for the display (sync interval 0), only when its swap chain can take a
    /// frame now (its frame-latency object already set: never waited for), so the pump never waits on a side window. False when there
    /// was nothing new, its window is hidden or gone, the frame was busy, or the swap chain was not ready (the next turn tries again).
    /// </summary>
    public bool PresentMirror(int canvasWidth, int canvasHeight)
    {
        ref var control = ref _client.Memory.Control;
        var frames = Volatile.Read(ref control.FramesRendered);
        var generation = Volatile.Read(ref control.OutputGeneration);
        if (frames == _framesSeen && generation == _generationSeen)
            return false;
        if (!_reader.Ready() || _reader.Device is not { } device)
            return false;

        try
        {
            var (left, top, right, bottom) = MirrorRegion(control.OutputWidth, control.OutputHeight, canvasWidth, canvasHeight);
            var (width, height) = (right - left, bottom - top);
            var (windowWidth, windowHeight) = PixelSize;
            if (width < 8 || height < 8 || windowWidth < 8 || windowHeight < 8)
                return false;
            if (!ReferenceEquals(device, _device) || _swapChain == 0)
                MakeSwapChain(device, width, height);
            else if (_buffers != (width, height))
                Resize(width, height);
            if (_swapChain == 0)
                return false;

            // Copied first, then presented only if the swap chain can take it now: its frame-latency object is waited on only once a
            // present follows (taken and not presented, it would never be set again).
            var back = Dxgi.BackBuffer(_swapChain);
            try
            {
                if (!_reader.CopyPresentedRegionTo(back, left, top, right, bottom, 0))
                    return false;
            }
            finally
            {
                Com.Release(ref back);
            }

            if (_waitable != 0 && WaitForSingleObject(_waitable, 0) != 0)
                return false;

            (_framesSeen, _generationSeen) = (frames, generation);
            var hr = Dxgi.Present(_swapChain, 0, 0);
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
            AppLog.Write("GPU viewport mirror: " + ex.Message);
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

            // Destroyed for real (the window free for another swap chain) once the context lets go of it.
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
        if (_ownsReader)
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
