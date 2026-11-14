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
/// pump at the display's refresh while playing and a picture is never more than a frame old when it is shown. Every frame queued takes
/// a turn of it, the paused pump's too (5.4.4, <see cref="FrameLatencyTurns"/>): a present without one let the playing pump run that many
/// frames ahead of the display for the rest of the session.</para>
/// <para>A lost device (a TDR) or a worker of a new generation on another card makes the reader's device anew; the
/// swap chain follows it on the next present.</para>
/// <para>Resized (5.4.1A), it never stretches: the player's swap chain is made with DXGI_SCALING_NONE, its buffers follow the window
/// on the next present, and a frame of another size than the window (the worker's frame one step behind a dock splitter's drag) is
/// laid 1:1 in the middle of black (<see cref="CenteredCopy"/>), never refused, so the window always shows a frame of its own size.</para>
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

    // What the desktop does with a buffer of another size than the window (5.4.1A): none for the player, a stretch for a mirror.
    private readonly uint _scaling;

    // The back buffer's render target view, made once a swap chain (flip model in D3D11: buffer 0 is always the one drawn next), for
    // the black a centred frame lies on; let go before every ResizeBuffers.
    private nint _backRtv;

    // A mirror (5.4C) shares the player's reader, and so its device: it never disposes it.
    private readonly bool _ownsReader = true;

    // The swap chain's frame-latency turns (5.4.4): every frame queued takes one, waited for or owed. The wait given to them.
    private FrameLatencyTurns _turns;
    private readonly Func<int, bool> _takeTurn;

    // The last present queued nothing (DXGI_STATUS_OCCLUDED and the like, 5.4.4): the playing pump's next turn is paced by the clock.
    private bool _nothingQueued;

    public GpuViewportControl(CompositorClient client, nint window)
    {
        _client = client;
        _reader = new CompositorFrameReader(client);
        _window = window;
        _scaling = Dxgi.ScalingNone;
        _takeTurn = TakeTurn;
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
        _scaling = Dxgi.ScalingStretch;
        _takeTurn = TakeTurn;
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

    /// <summary>
    /// Windows' scale of the monitor the window is on now (2.0 at 200 %; 1 when the window is gone), asked at every call: a fullscreen player
    /// can be on another monitor than the main window (5.4.4, Dynamic Preview Scaling's "1:1 DIP"). Not HandPeg's own content scale, which
    /// follows its UI Scale. Any thread.
    /// </summary>
    public double MonitorScale => GetDpiForWindow(_window) is var dpi and > 0 ? dpi / 96.0 : 1;

    /// <summary>Frames presented on the screen (diagnostics).</summary>
    public long Presents { get; private set; }

    // The Playback & A/V Sync Test's sampler (5.5.2) reads the swap chain from a thread of its own: the pump lets it go only under this gate.
    private readonly object _statisticsGate = new();

    /// <summary>The swap chain's last present count, 0 none (any thread: the sampler's; never while the pump lets the swap chain go).</summary>
    internal uint SampleLastPresentCount()
    {
        lock (_statisticsGate)
            return Dxgi.LastPresentCount(Volatile.Read(ref _swapChain));
    }

    /// <summary>The present shown at the last vblank and that vblank's QPC (any thread, as <see cref="SampleLastPresentCount"/>); false none.</summary>
    internal bool SampleFrameStatistics(out uint presentCount, out long syncQpc)
    {
        lock (_statisticsGate)
            return Dxgi.FrameStatistics(Volatile.Read(ref _swapChain), out presentCount, out syncQpc);
    }

    // The scene (its sequence number) in the frame last handed to IDXGISwapChain::Present (5.4.3): what is on the screen, not only drawn.
    private long _presentedSequence;

    /// <summary>The scene of the frame last presented on the swap chain (any thread; the worker's PresentedSequence is only drawn).</summary>
    public long PresentedSequence => Volatile.Read(ref _presentedSequence);

    /// <summary>
    /// The frame on the screen now as a still (5.4.3, a project tab put behind with no still of its playhead): read back on the presenting
    /// thread (its device's context), cropped and sampled by <paramref name="region"/> and <paramref name="step"/>. False when there is none.
    /// </summary>
    internal bool TryReadStill(Func<int, int, (int Left, int Top, int Right, int Bottom)> region, Func<int, int, int> step, out byte[] bgra, out int width, out int height)
    {
        (bgra, width, height) = ([], 0, 0);
        return Presents > 0 && _reader.TryReadStill(region, step, out bgra, out width, out height);
    }

    /// <summary>
    /// Waits until the swap chain can take another frame (its frame-latency object), at most <paramref name="milliseconds"/>: the turns
    /// owed by frames queued without one taken back first (5.4.4, <see cref="FrameLatencyTurns.Wait"/>). Without a swap chain yet, or
    /// after a present that queued nothing (an occluded window: minimised, a locked session; its turn is still held, so the wait would not
    /// wait at all and the playing pump would spin), it is paced by the clock: a refresh at 60 Hz, at most that long.
    /// </summary>
    public void WaitForTurn(int milliseconds)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_waitable == 0 || _nothingQueued)
            Thread.Sleep(Math.Min(milliseconds, 16));
        if (_waitable != 0)
            _ = _turns.Wait(_takeTurn, milliseconds);
        LastTurnMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // Pass 5.5.2 (the A/V bisect): how long the pump's last turn wait, back-buffer copy and IDXGISwapChain::Present call took, and what Present
    // returned, for AvSync's trace (read by reflection; a few counter reads a frame).
    internal float LastTurnMs, LastCopyMs, LastPresentCallMs;
    internal int LastPresentHr;

    // One turn of the frame-latency object, waited for at most that long (5.4.4: FrameLatencyTurns' wait).
    private bool TakeTurn(int milliseconds) => _waitable != 0 && WaitForSingleObject(_waitable, (uint)milliseconds) == 0;

    /// <summary>
    /// Whether the worker presented a frame this viewport has not shown yet, or the window changed size since the last present (5.4.1A:
    /// the paused pump then shows the last frame at once in the resized window, centred, while the worker draws the new size).
    /// </summary>
    public bool HasNewFrame =>
        Volatile.Read(ref _client.Memory.Control.FramesRendered) != _framesSeen || Volatile.Read(ref _client.Memory.Control.OutputGeneration) != _generationSeen
        || (PixelSize is var (width, height) && width >= 8 && height >= 8 && (width, height) != _buffers);

    /// <summary>
    /// Copies the worker's newest frame into the back buffer and presents it (with <paramref name="vsync"/>, on the next
    /// refresh). A window that changed size presents again even with no new frame, and a frame of another size than the window
    /// is laid 1:1 in its middle on black (5.4.1A: a resize under way, never stretched, never refused). False when there was no
    /// new frame and no new size (and <paramref name="force"/> is not set), the frame was busy, or there is nothing to present on yet.
    /// </summary>
    public bool PresentNewest(bool vsync, bool force = false)
    {
        ref var control = ref _client.Memory.Control;
        var frames = Volatile.Read(ref control.FramesRendered);
        var generation = Volatile.Read(ref control.OutputGeneration);
        var (width, height) = PixelSize;
        if (!force && frames == _framesSeen && generation == _generationSeen && (width, height) == _buffers)
            return false;
        if (!_reader.Ready() || _reader.Device is not { } device)
            return false;

        try
        {
            if (width < 8 || height < 8)
                return false;
            if (!ReferenceEquals(device, _device) || _swapChain == 0)
                MakeSwapChain(device, width, height);
            else if (_buffers != (width, height))
                Resize(width, height);
            if (_swapChain == 0)
                return false;

            var back = Dxgi.BackBuffer(_swapChain);
            try
            {
                if (_backRtv == 0)
                    _backRtv = device.Device.CreateRenderTargetView(back);
                var copyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var copied = _reader.CopyPresentedCentered(back, _backRtv, width, height, 8);
                LastCopyMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
                if (!copied)
                {
                    // A capture's frame (read back, 5.4.1A): seen, never shown; the player's next frame follows it.
                    if (_reader.SkippedReadback)
                        (_framesSeen, _generationSeen) = (frames, generation);
                    return false;
                }
            }
            finally
            {
                Com.Release(ref back);
            }

            (_framesSeen, _generationSeen) = (frames, generation);
            var presentStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var hr = Dxgi.Present(_swapChain, vsync ? 1u : 0u, 0);
            (LastPresentCallMs, LastPresentHr) = ((float)System.Diagnostics.Stopwatch.GetElapsedTime(presentStarted).TotalMilliseconds, hr);
            if (Com.IsDeviceLost(hr))
            {
                DropSwapChain();
                return false;
            }

            // A frame queued takes its turn, the paused pump's (no wait before it) too (5.4.4).
            if (hr == 0)
            {
                _turns.Queued(_takeTurn);
                if (vsync)
                    MeasureLead();
            }

            _nothingQueued = hr > 0;
            Presents++;
            if (hr >= 0)
                Volatile.Write(ref _presentedSequence, _reader.Sequence);
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

    // ----- The display's lead (5.4.4-Final, the user's Q4 a) -----
    // How long after the pump chose a playing frame's moment (FrameOrigin: its turn) that frame is on the screen: the swap chain's own report
    // (GetFrameStatistics: the present shown at a vblank, and that vblank's QPC) matched to the presents made, over the last 128 (5.4.4: the
    // 500 Hz Desktop shows a frame 6-9 ms after its Present, three or four refreshes, where the pump had counted one: the picture ran late of
    // the sound by the difference). The median of the last LeadSamples is DisplayLeadSeconds; the pump draws each frame for that moment.

    private const int LeadRing = 128, LeadSamples = 31;
    private readonly uint[] _leadIds = new uint[LeadRing];
    private readonly long[] _leadOrigins = new long[LeadRing];
    private int _leadNext;
    private readonly double[] _leads = new double[LeadSamples];
    private int _leadCount, _leadAt;
    private uint _leadSeen;
    private double _displayLead = double.NaN;

    /// <summary>The pump's turn that chose the frame presented next (QPC): set by the pump before it presents a playing frame.</summary>
    public long FrameOrigin { get; set; }

    /// <summary>
    /// The measured time from the pump's turn to the frame on the screen, in seconds (the median of the last 31 matched presents); NaN until
    /// enough are known (a swap chain DWM does not report on: the pump then counts one refresh, as before).
    /// </summary>
    public double DisplayLeadSeconds => Volatile.Read(ref _displayLead);

    // After a vsync'd present: its count and the turn that made it kept when it has one (a new frame: FrameOrigin set); the swap chain asked
    // which present the last vblank showed after every one. Pass 5.5.2: only new frames are kept. On the 500 Hz Desktop seven presents in
    // eight are copies of a 60 fps timeline's frame (no draw, no wait for the worker), so a median over every present measured the copies'
    // lead and left out the draw the new frames wait for: those were drawn for a moment a few ms too soon (1x median -4 ms, worst -24).
    private void MeasureLead()
    {
        var id = Dxgi.LastPresentCount(_swapChain);
        if (id != 0 && FrameOrigin != 0)
        {
            _leadIds[_leadNext] = id;
            _leadOrigins[_leadNext] = FrameOrigin;
            _leadNext = (_leadNext + 1) % LeadRing;
        }

        if (!Dxgi.FrameStatistics(_swapChain, out var shown, out var shownAt) || shown == _leadSeen)
            return;
        _leadSeen = shown;
        for (var i = 0; i < LeadRing; i++)
        {
            if (_leadIds[i] != shown || _leadOrigins[i] == 0)
                continue;
            if (LeadOf(_leadOrigins[i], shownAt, System.Diagnostics.Stopwatch.Frequency) is { } lead)
            {
                _leads[_leadAt] = lead;
                _leadAt = (_leadAt + 1) % LeadSamples;
                _leadCount = Math.Min(_leadCount + 1, LeadSamples);
                if (_leadCount >= 5)
                    Volatile.Write(ref _displayLead, Median(_leads.AsSpan(0, _leadCount)));
            }

            break;
        }
    }

    /// <summary>A turn and the vblank that showed its frame as a lead in seconds; null when out of reason (before the turn, or over 150 ms). Pure.</summary>
    internal static double? LeadOf(long origin, long shownAt, long frequency) =>
        shownAt > origin && frequency > 0 && (shownAt - origin) / (double)frequency is var seconds and < 0.15 ? seconds : null;

    /// <summary>The median of some values (the middle one of an odd count, the mean of the middle two of an even one). Pure.</summary>
    internal static double Median(ReadOnlySpan<double> values)
    {
        Span<double> sorted = stackalloc double[values.Length];
        values.CopyTo(sorted);
        sorted.Sort();
        var middle = sorted.Length / 2;
        return sorted.Length == 0 ? double.NaN : sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// The lead the pump draws a playing frame for (5.4.4-Final): the measured one while it is known and within reason (at least a refresh,
    /// at most 100 ms), else one refresh (the frame drawn now is on the screen at the next one). Pure, for the golden test.
    /// </summary>
    internal static double PlayingLead(double refresh, double measured) =>
        double.IsFinite(measured) && measured >= refresh && measured <= 0.1 ? measured : refresh;

    /// <summary>
    /// The origin a playing turn gives its frame's lead (pure, Pass 5.5.2-Release): the turn's QPC, or 0 (no lead measured) when the turn is a
    /// stall, longer than 1.5 of the monitor's refresh (else of the pump's estimate). On the 500 Hz Desktop, with the player on screen (real
    /// frame statistics: AvSync --onscreen), a cold play's first second turns every 20-60 ms and its frames reach the screen 50-60 ms after
    /// their turn; those leads filled the median, and once the pipeline drained the frames were drawn 57-61 ms ahead of the sound.
    /// </summary>
    internal static long LeadOrigin(long now, double interval, double monitorPeriod, double refresh)
    {
        var period = double.IsFinite(monitorPeriod) && monitorPeriod > 0 ? monitorPeriod : refresh;
        return period > 0 && interval > 1.5 * period ? 0 : now;
    }

    /// <summary>
    /// Where a frame of one size goes in a target of another (5.4.1A, the player while it is resized): 1:1, never scaled, in whole
    /// pixels, centred: a smaller frame with margins round it (DstX, DstY), a larger one with its middle cropped out of it (the source
    /// rectangle SrcLeft..SrcRight x SrcTop..SrcBottom). Empty (all zero) for a frame or target of no size. Pure: the golden test
    /// player-resize-no-stretch checks it.
    /// </summary>
    internal static (int SrcLeft, int SrcTop, int SrcRight, int SrcBottom, int DstX, int DstY) CenteredCopy(int frameWidth, int frameHeight, int targetWidth, int targetHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            return (0, 0, 0, 0, 0, 0);

        var (left, x) = frameWidth > targetWidth ? ((frameWidth - targetWidth) / 2, 0) : (0, (targetWidth - frameWidth) / 2);
        var (top, y) = frameHeight > targetHeight ? ((frameHeight - targetHeight) / 2, 0) : (0, (targetHeight - frameHeight) / 2);
        return (left, top, left + Math.Min(frameWidth, targetWidth), top + Math.Min(frameHeight, targetHeight), x, y);
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
            // A capture's frame (read back) is never mirrored, nor the swap chain resized for it; else the presented frame's own size
            // (5.4.1A: the worker's ring can be larger than its frames).
            var presented = Volatile.Read(ref control.PresentedIndex);
            if (_reader.ReadbackIn(presented))
            {
                (_framesSeen, _generationSeen) = (frames, generation);
                return false;
            }

            var (outputWidth, outputHeight) = _reader.ContentSize(presented);
            var (left, top, right, bottom) = MirrorRegion(outputWidth, outputHeight, canvasWidth, canvasHeight);
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
                {
                    if (_reader.SkippedReadback)
                        (_framesSeen, _generationSeen) = (frames, generation);
                    return false;
                }
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
            if (hr >= 0)
                Volatile.Write(ref _presentedSequence, _reader.Sequence);
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
        _swapChain = Dxgi.CreateSwapChain(device.DevicePointer, _window, width, height, _scaling);
        _waitable = Dxgi.FrameLatencyWaitable(_swapChain);
        _turns.Reset();
        _nothingQueued = false;

        // A new swap chain counts its presents from 1 again: the presents kept for the lead are another's (the lead measured stays).
        Array.Clear(_leadIds);
        _leadSeen = 0;
        (_device, _buffers) = (device, (width, height));
    }

    private void Resize(int width, int height)
    {
        // Nothing holds a buffer here between presents (the back buffer is let go after each copy; its view now).
        Com.Release(ref _backRtv);
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
        Com.Release(ref _backRtv);
        if (_waitable != 0)
        {
            _ = CloseHandle(_waitable);
            _waitable = 0;
        }

        if (_swapChain != 0)
        {
            // Under the gate the Playback & A/V Sync Test's sampler reads its statistics under (5.5.2): never let go mid-call.
            lock (_statisticsGate)
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

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    /// <summary>
    /// The refresh period of the monitor the window is on (nearest one for a window off every screen), in seconds, from its current display
    /// mode (whole Hz); NaN when it cannot be told (Pass 5.5.2: the pump's bound for its measured refresh, <see cref="NextRefresh"/>). Any thread.
    /// </summary>
    public double MonitorRefreshSeconds
    {
        get
        {
            var monitor = MonitorFromWindow(_window, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor == 0)
                return double.NaN;
            var info = stackalloc byte[104]; // MONITORINFOEXW: cbSize, rcMonitor, rcWork, dwFlags, szDevice[32]
            *(uint*)info = 104;
            if (!GetMonitorInfoW(monitor, info))
                return double.NaN;
            var mode = stackalloc byte[220]; // DEVMODEW: dmSize at 68, dmDisplayFrequency at 184
            new Span<byte>(mode, 220).Clear();
            *(ushort*)(mode + 68) = 220;
            if (!EnumDisplaySettingsW((char*)(info + 40), -1 /* ENUM_CURRENT_SETTINGS */, mode))
                return double.NaN;
            var hertz = *(uint*)(mode + 184);
            return hertz > 1 ? 1.0 / hertz : double.NaN;
        }
    }

    /// <summary>
    /// The pump's refresh estimate after one more paced turn <paramref name="interval"/> apart (seconds), pure for the golden test (Pass 5.5.2):
    /// a moving average as before, except that a turn longer than 1.5 of the monitor's refresh (<paramref name="monitorPeriod"/>, when known) is
    /// a stall, not a refresh, and leaves it as it was. On the 500 Hz Desktop a cold play's first half second has turns of 37-52 ms (frames
    /// retired slowly while the decoders start): they took the estimate from 2 ms to ~18 ms, and with no measured display lead the frames were
    /// drawn that far ahead of the sound (the paused step's worst +20..+35 ms).
    /// </summary>
    internal static double NextRefresh(double refresh, double interval, double monitorPeriod)
    {
        if (interval is not (> 0.0015 and < 0.05))
            return refresh;
        if (double.IsFinite(monitorPeriod) && monitorPeriod > 0 && interval > 1.5 * monitorPeriod)
            return refresh;
        return 0.9 * refresh + 0.1 * interval;
    }

    /// <summary>
    /// Where a play begins its refresh estimate (pure, Pass 5.5.2): the monitor's period when known and the estimate is off it by more than half
    /// (the 1/60 s start on a 500 Hz display, or a window moved to another monitor), else the estimate as it was.
    /// </summary>
    internal static double StartRefresh(double refresh, double monitorPeriod) =>
        double.IsFinite(monitorPeriod) && monitorPeriod > 0 && (refresh > 1.5 * monitorPeriod || refresh < 0.5 * monitorPeriod) ? monitorPeriod : refresh;

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, byte* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettingsW(char* device, int mode, byte* devMode);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
