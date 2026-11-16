using System.Runtime.InteropServices;
using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;
using HandPegApp.Native;
using static HandPegApp.Native.Kernel32;
using static HandPegApp.Native.User32;

namespace HandPegApp;

/// <summary>
/// Player presentation: a flip-model swap chain on the video surface HWND (<see cref="VideoSurface.SurfaceHandle"/>), filled by a GPU
/// copy of the compositor's newest output texture; pixels never touch the CPU. The surface still handles black-before-first-frame,
/// visibility, the overlay window (gizmos, crop, key map) and dock rebuilds.
/// <para>Owned by the frame pump thread (reader device and swap chain). The frame-latency waitable (max one queued frame) lets
/// <see cref="WaitForTurn"/> pace playback at the display refresh. Every queued frame consumes a turn, paused ones included
/// (<see cref="FrameLatencyTurns"/>); otherwise the pump drifts frames ahead of the display.</para>
/// <para>Device loss (TDR) or a new worker generation on another adapter recreates the reader device; the swap chain follows on the next present.</para>
/// <para>Never stretches on resize: DXGI_SCALING_NONE, buffers resized on the next present, and mismatched frames (worker lagging a
/// splitter drag) are centred 1:1 on black (<see cref="CenteredCopy"/>).</para>
/// </summary>
internal sealed unsafe partial class GpuViewport : IDisposable
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

    // DXGI scaling for size mismatch: none for the player, stretch for mirrors.
    private readonly uint _scaling;

    // Back-buffer RTV (flip model: buffer 0 is always next) used to clear to black for centred frames; released before ResizeBuffers.
    private nint _backRtv;

    // Mirrors share the player's reader and device and never dispose them.
    private readonly bool _ownsReader = true;

    // Frame-latency turn accounting: each queued frame consumes one, waited or owed.
    private FrameLatencyTurns _turns;
    private readonly Func<int, bool> _takeTurn;

    // Last present queued nothing (e.g. DXGI_STATUS_OCCLUDED): the next playing turn is clock-paced.
    private bool _nothingQueued;

    public GpuViewport(CompositorClient client, nint window)
    {
        _client = client;
        _reader = new CompositorFrameReader(client);
        _window = window;
        _scaling = Dxgi.ScalingNone;
        _takeTurn = TakeTurn;
    }

    /// <summary>
    /// Live Video Preview mirror of <paramref name="player"/> on another HWND: shares the player's reader and device, with its own swap
    /// chain sized to the canvas region (<see cref="MirrorRegion"/>). Pump thread only.
    /// </summary>
    public GpuViewport(GpuViewport player, nint window)
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

    /// <summary>Current window size in pixels (worker render size); (0, 0) when hidden or destroyed.</summary>
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
    /// DPI scale of the window's current monitor (2.0 at 200 %; 1 if destroyed), queried per call since fullscreen may be on another
    /// monitor (Dynamic Preview Scaling 1:1 DIP). Independent of UI Scale. Any thread.
    /// </summary>
    public double MonitorScale => GetDpiForWindow(_window) is var dpi and > 0 ? dpi / 96.0 : 1;

    /// <summary>Frames presented on the screen (diagnostics).</summary>
    public long Presents { get; private set; }

    // A/V Sync Test sampler reads the swap chain from its own thread; the pump releases the swap chain only under this gate.
    private readonly object _statisticsGate = new();

    /// <summary>Last present count, 0 if none (any thread; gated against swap chain release).</summary>
    internal uint SampleLastPresentCount()
    {
        lock (_statisticsGate)
            return Dxgi.LastPresentCount(Volatile.Read(ref _swapChain));
    }

    /// <summary>Present shown at the last vblank and its QPC (any thread, gated like <see cref="SampleLastPresentCount"/>); false if none.</summary>
    internal bool SampleFrameStatistics(out uint presentCount, out long syncQpc)
    {
        lock (_statisticsGate)
            return Dxgi.FrameStatistics(Volatile.Read(ref _swapChain), out presentCount, out syncQpc);
    }

    // Scene sequence of the frame last passed to IDXGISwapChain::Present (on screen, not merely rendered).
    private long _presentedSequence;

    /// <summary>Scene of the last presented frame (any thread); the worker's PresentedSequence only means rendered.</summary>
    public long PresentedSequence => Volatile.Read(ref _presentedSequence);

    /// <summary>
    /// Reads back the on-screen frame as a still (tab deactivation without a playhead still), on the presenting thread, cropped/subsampled
    /// by <paramref name="region"/> and <paramref name="step"/>. False when none.
    /// </summary>
    internal bool TryReadStill(Func<int, int, (int Left, int Top, int Right, int Bottom)> region, Func<int, int, int> step, out byte[] bgra, out int width, out int height)
    {
        (bgra, width, height) = ([], 0, 0);
        return Presents > 0 && _reader.TryReadStill(region, step, out bgra, out width, out height);
    }

    /// <summary>
    /// Waits up to <paramref name="milliseconds"/> for the frame-latency object, first repaying owed turns (<see cref="FrameLatencyTurns.Wait"/>).
    /// Without a swap chain, or after an occluded present (minimized, locked session; the waitable would not block and the pump would spin),
    /// paces by clock at 60 Hz.
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

    // AvSync trace: last turn wait, back-buffer copy and Present durations plus Present's HRESULT (read via reflection; cheap per frame).
    internal float LastTurnMs, LastCopyMs, LastPresentCallMs;
    internal int LastPresentHr;

    // Max wait per frame-latency turn (FrameLatencyTurns).
    private bool TakeTurn(int milliseconds) => _waitable != 0 && WaitForSingleObject(_waitable, (uint)milliseconds) == 0;

    /// <summary>
    /// True when the worker has an unshown frame or the window resized since the last present (paused pump re-presents the last frame
    /// centred while the worker renders the new size).
    /// </summary>
    public bool HasNewFrame =>
        Volatile.Read(ref _client.Memory.Control.FramesRendered) != _framesSeen || Volatile.Read(ref _client.Memory.Control.OutputGeneration) != _generationSeen
        || (PixelSize is var (width, height) && width >= 8 && height >= 8 && (width, height) != _buffers);

    /// <summary>
    /// Copies the newest worker frame to the back buffer and presents (<paramref name="vsync"/> = next refresh). Resizes re-present without a
    /// new frame; mismatched frames are centred 1:1 on black. False when nothing new (unless <paramref name="force"/>), the frame is busy, or no target.
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
                    // Readback (capture) frame: consumed, never shown.
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

            // Every queued frame consumes a turn, including paused ones that skipped the wait.
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
            AppLog.Write("GPU viewport", ex);
            DropSwapChain();
            return false;
        }
    }

    // ----- Measured display lead -----
    // Time from the pump's turn (FrameOrigin) to the vblank showing that frame, from GetFrameStatistics matched against the last 128
    // presents (a 500 Hz display showed frames 6-9 ms after Present, several refreshes rather than one, so video lagged audio). The
    // median of the last LeadSamples is DisplayLeadSeconds, used as the evaluation offset.

    private const int LeadRing = 128, LeadSamples = 31;
    private readonly uint[] _leadIds = new uint[LeadRing];
    private readonly long[] _leadOrigins = new long[LeadRing];
    private int _leadNext;
    private readonly double[] _leads = new double[LeadSamples];
    private int _leadCount, _leadAt;
    private uint _leadSeen;
    private double _displayLead = double.NaN;

    // Seed from the saved A/V test lead: SeedCopies samples, dropped if no real sample arrives within SeedExpiryPresents frames (no DWM stats).
    internal const int SeedCopies = 4, SeedExpiryPresents = 60;
    private int _seedLeft;

    /// <summary>QPC of the pump turn that chose the next presented frame; set before presenting a playing frame.</summary>
    public long FrameOrigin { get; set; }

    /// <summary>
    /// Measured turn-to-screen latency (s), median of the last 31 matched presents; NaN until enough samples (or no DWM stats; then one refresh is assumed).
    /// </summary>
    public double DisplayLeadSeconds => Volatile.Read(ref _displayLead);

    // After each vsync'd present, record (count, origin) for new frames only and query which present the last vblank showed. Copies are
    // excluded: at 500 Hz with 60 fps content most presents are copies, whose shorter lead would bias the median low (frames drawn early).
    private void MeasureLead()
    {
        var id = Dxgi.LastPresentCount(_swapChain);
        if (id != 0 && FrameOrigin != 0)
        {
            _leadIds[_leadNext] = id;
            _leadOrigins[_leadNext] = FrameOrigin;
            _leadNext = (_leadNext + 1) % LeadRing;
            if (_seedLeft > 0 && --_seedLeft == 0 && _leadCount <= SeedCopies)
                ForgetSeed();
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
                _seedLeft = 0;
                if (_leadCount >= 5)
                    Volatile.Write(ref _displayLead, Median(_leads.AsSpan(0, _leadCount)));
            }

            break;
        }
    }

    /// <summary>
    /// Seeds the lead from the saved A/V Sync Test value (pump thread, before first play) with <see cref="SeedCopies"/> samples, so real
    /// samples dominate after five and cold-start outliers are damped. No-op when already measured or not finite.
    /// </summary>
    public void SeedLead(double seconds)
    {
        if (!double.IsFinite(seconds) || _leadCount > 0)
            return;

        for (var i = 0; i < SeedCopies; i++)
            _leads[i] = seconds;
        (_leadAt, _leadCount, _seedLeft) = (SeedCopies, SeedCopies, SeedExpiryPresents);
        Volatile.Write(ref _displayLead, seconds);
    }

    private void ForgetSeed()
    {
        (_leadAt, _leadCount, _seedLeft) = (0, 0, 0);
        Volatile.Write(ref _displayLead, double.NaN);
    }

    /// <summary>
    /// Seed lead (s) from the saved test, or NaN (pure). Requires real frame statistics, 0 &lt; lead &lt;= 100 ms, and a refresh within 1.5 Hz
    /// or 1 % of <paramref name="monitorPeriod"/> (EnumDisplaySettings integer Hz vs DWM fractional rates).
    /// </summary>
    internal static double SeedLeadFor(double? leadMs, bool frameStatistics, double testHz, double monitorPeriod)
    {
        if (!frameStatistics || leadMs is not { } ms || !double.IsFinite(ms) || ms <= 0 || ms > 100
            || !double.IsFinite(testHz) || testHz <= 0 || !double.IsFinite(monitorPeriod) || monitorPeriod <= 0)
            return double.NaN;

        var monitorHz = 1 / monitorPeriod;
        return Math.Abs(testHz - monitorHz) <= Math.Max(1.5, monitorHz * 0.01) ? ms / 1000 : double.NaN;
    }

    /// <summary>Golden-test helper: median after real samples follow a seed (pure).</summary>
    internal static double SeededMedian(double seed, ReadOnlySpan<double> real)
    {
        var all = new double[Math.Min(SeedCopies + real.Length, LeadSamples)];
        var count = 0;
        for (var i = 0; i < SeedCopies && count < all.Length; i++)
            all[count++] = seed;
        foreach (var lead in real)
            all[count++ % all.Length] = lead;
        return Median(all.AsSpan(0, Math.Min(count, all.Length)));
    }

    /// <summary>Lead (s) from turn to vblank; null when implausible (negative or over 150 ms). Pure.</summary>
    internal static double? LeadOf(long origin, long shownAt, long frequency) =>
        shownAt > origin && frequency > 0 && (shownAt - origin) / (double)frequency is var seconds and < 0.15 ? seconds : null;

    /// <summary>Median (mean of the middle two for even counts). Pure.</summary>
    internal static double Median(ReadOnlySpan<double> values)
    {
        Span<double> sorted = stackalloc double[values.Length];
        values.CopyTo(sorted);
        sorted.Sort();
        var middle = sorted.Length / 2;
        return sorted.Length == 0 ? double.NaN : sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// Evaluation lead: the measured lead when known and within [one refresh, 100 ms], else one refresh. Pure (golden-tested).
    /// </summary>
    internal static double PlayingLead(double refresh, double measured) =>
        double.IsFinite(measured) && measured >= refresh && measured <= 0.1 ? measured : refresh;

    /// <summary>
    /// Lead origin for a playing turn (pure): the turn's QPC, or 0 (not sampled) for stalls longer than 1.5x the monitor refresh (else the
    /// pump estimate). Cold-start turns of 20-60 ms with 50-60 ms leads would otherwise skew the median and draw frames far ahead of audio.
    /// </summary>
    internal static long LeadOrigin(long now, double interval, double monitorPeriod, double refresh)
    {
        var period = double.IsFinite(monitorPeriod) && monitorPeriod > 0 ? monitorPeriod : refresh;
        return period > 0 && interval > 1.5 * period ? 0 : now;
    }

    /// <summary>
    /// 1:1 centred placement of a frame in a differently sized target (integer pixels): smaller frames get margins (DstX, DstY), larger
    /// ones are centre-cropped (Src rectangle). All zero for empty sizes. Pure (player-resize-no-stretch golden).
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
    /// Canvas region of the player output for Live Video Preview: letterboxed fit at zoom 1 (SceneRenderer.Letterbox), excluding bars, in
    /// integer pixels (left, top, right, bottom). All zero for empty sizes. Pure (side-window-mirror golden).
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
    /// Mirror present after the player: GPU-copies the canvas region and presents with sync interval 0, only if the frame-latency object
    /// is already signalled (never blocks the pump). False when nothing new, hidden/destroyed, busy, or not ready (retried next turn).
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
            // Skip readback frames (no mirror, no resize); otherwise use the frame's content size (the ring may be larger).
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

            // Copy first; consume the frame-latency signal only when a present follows (consuming without presenting would never re-signal).
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
            AppLog.Write("GPU viewport mirror", ex);
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

        // New swap chain restarts present counts: drop pending samples (the measured lead is kept).
        Array.Clear(_leadIds);
        _leadSeen = 0;
        (_device, _buffers) = (device, (width, height));
    }

    private void Resize(int width, int height)
    {
        // No buffer references between presents (back buffer released after each copy; RTV released here).
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
            // Release under the sampler gate so the A/V Sync Test never reads a released swap chain.
            lock (_statisticsGate)
                Com.Release(ref _swapChain);

            // Flush so the swap chain is actually destroyed and the HWND can take a new one.
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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    /// <summary>
    /// Refresh period (s) of the window's (or nearest) monitor from its display mode (integer Hz); NaN if unknown. Bounds the pump's estimate
    /// (<see cref="NextRefresh"/>). Any thread.
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
            if (!GetMonitorInfo(monitor, info))
                return double.NaN;
            var mode = stackalloc byte[220]; // DEVMODEW: dmSize at 68, dmDisplayFrequency at 184
            new Span<byte>(mode, 220).Clear();
            *(ushort*)(mode + 68) = 220;
            if (!EnumDisplaySettings((char*)(info + 40), -1 /* ENUM_CURRENT_SETTINGS */, mode))
                return double.NaN;
            var hertz = *(uint*)(mode + 184);
            return hertz > 1 ? 1.0 / hertz : double.NaN;
        }
    }

    /// <summary>
    /// Updated refresh estimate after a paced turn of <paramref name="interval"/> s (pure, golden-tested): moving average, ignoring stalls
    /// longer than 1.5x <paramref name="monitorPeriod"/> when known (cold-start 37-52 ms turns would otherwise inflate a 2 ms estimate to ~18 ms).
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
    /// Initial refresh estimate for a play (pure): the monitor period when known and the estimate differs by over 50 % (60 Hz default on a
    /// 500 Hz display, monitor change), else the existing estimate.
    /// </summary>
    internal static double StartRefresh(double refresh, double monitorPeriod) =>
        double.IsFinite(monitorPeriod) && monitorPeriod > 0 && (refresh > 1.5 * monitorPeriod || refresh < 0.5 * monitorPeriod) ? monitorPeriod : refresh;

}
