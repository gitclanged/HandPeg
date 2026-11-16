using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using HandPegApp.Native;
using HandPegApp.Services;
using static HandPegApp.Native.Dwmapi;
using static HandPegApp.Native.Gdi32;
using static HandPegApp.Native.Shell32;
using static HandPegApp.Native.User32;

namespace HandPegApp.Shell;

/// <summary>
/// Shared frame handling for WindowChrome windows (main, Settings, side windows): one hook (<see cref="FrameHook"/>), one
/// auto-hide-taskbar maximized placement, one physical-pixel hit test. Static; only the per-monitor taskbar cache and the dev
/// trace hold state, both UI-thread owned.
/// </summary>
internal static partial class WindowFrame
{
    /// <summary>
    /// How far a maximized WindowChrome window overhangs its monitor, in window DIPs; content is inset by this
    /// (main window and Settings, which share the title bar).
    /// </summary>
    internal static Thickness MaximizedFrameInset(Window window)
    {
        // Window-level pixels per DIP (frame, margins): the monitor scale, independent of the content UiScale.
        var dpi = UiScale.MonitorScale(window);
        var handle = new WindowInteropHelper(window).Handle;

        // Maximized: the actual client-area overhang past the work area (zero on auto-hide-taskbar edges), exact for any
        // scale, taskbar layout or monitor.
        if (handle != IntPtr.Zero && IsZoomed(handle) && ClientOnScreen(handle, out var rect)
            && TryMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), out var info))
        {
            var work = info.Work;
            return new Thickness(Math.Max(0, work.Left - rect.Left) / dpi, Math.Max(0, work.Top - rect.Top) / dpi,
                Math.Max(0, rect.Right - work.Right) / dpi, Math.Max(0, rect.Bottom - work.Bottom) / dpi);
        }

        // Not yet placed maximized (no HWND, or maximized before show): sizing frame plus padded border at the window DPI.
        var at = (uint)Math.Round(96 * dpi);
        var across = GetSystemMetricsForDpi(SmCxSizeFrame, at) + GetSystemMetricsForDpi(SmCxPaddedBorder, at);
        var down = GetSystemMetricsForDpi(SmCySizeFrame, at) + GetSystemMetricsForDpi(SmCxPaddedBorder, at);
        var margin = new Thickness(across / dpi, down / dpi, across / dpi, down / dpi);

        // With an auto-hide taskbar the window is maximized inside the work area (edge 2 px in, MaximizedPlace): no overhang.
        var edges = handle == IntPtr.Zero ? 0 : AutoHideEdges(MonitorFromWindow(handle, MonitorDefaultToNearest));
        return edges != 0 ? new Thickness(0) : margin;
    }

    private const int SmCxSizeFrame = 32, SmCySizeFrame = 33, SmCxPaddedBorder = 92;

    // ----- Auto-hide taskbar and monitor work area -----
    // A maximized window overhangs its monitor by the resize border. With an auto-hide taskbar (work area = monitor), Windows
    // treats such a window as full-screen and suppresses the taskbar reveal and Show Desktop corner. So the window stops 2 px
    // (1 px was insufficient) short of each auto-hide edge, in physical pixels, via its maximized placement.
    // Never shrink the client area instead: frame outside the client area makes Windows clip with a region and permanently disable
    // DWM frame rendering (DWMWA_NCRENDERING_ENABLED 0, measured), after which the classic caption paints over the content.
    // Every auto-hide edge of the window's own monitor counts. The strip belongs to the desktop (FrameHitTest returns nowhere); a
    // click-through backdrop in the window's colour covers it so no wallpaper shows (WindowFrame.Backdrop.cs).

    internal const int WmNcCalcSize = 0x0083, WmWindowPosChanging = 0x0046, WmSettingChange = 0x001A, SpiSetWorkArea = 0x002F,
        WmDisplayChange = 0x007E;

    // Physical pixels left uncovered along an auto-hide taskbar edge.
    private const int HiddenTaskbarStrip = 2;

    /// <summary>
    /// Auto-hide taskbar edges of a monitor as bits (1 &lt;&lt; AbeLeft .. 1 &lt;&lt; AbeBottom); 0 for none. Queries each edge with
    /// ABM_GETAUTOHIDEBAREX; if none answers while auto-hide is on, falls back to the primary taskbar's edge when on this monitor.
    /// </summary>
    internal static int AutoHideEdges(IntPtr monitor)
    {
        // Called per move/resize step and each query is cross-process to the shell: cache 1 s per monitor, invalidated on work
        // area or display changes.
        var now = Environment.TickCount64;
        if (_hiddenEdges.TryGetValue(monitor, out var known) && now - known.At <= 1000)
            return known.Edges;
        var edges = AskAutoHideEdges(monitor);
        if (_hiddenEdges.Count > 8)
            _hiddenEdges.Clear();
        _hiddenEdges[monitor] = (edges, now);
        return edges;
    }

    // AutoHideEdges cache per monitor with timestamp; UI thread only.
    private static readonly Dictionary<IntPtr, (int Edges, long At)> _hiddenEdges = [];

    /// <summary>Clears the cache on WM_SETTINGCHANGE (SPI_SETWORKAREA) or WM_DISPLAYCHANGE.</summary>
    internal static void ForgetHiddenTaskbars() => _hiddenEdges.Clear();

    private static int AskAutoHideEdges(IntPtr monitor)
    {
        var state = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        if ((SHAppBarMessage(AbmGetState, ref state) & AbsAutoHide) == 0 || !TryMonitorInfo(monitor, out var info))
            return 0;

        var edges = 0;
        for (var edge = AbeLeft; edge <= AbeBottom; edge++)
        {
            var bar = new AppBarData { Size = Marshal.SizeOf<AppBarData>(), Edge = (uint)edge, Rect = info.Monitor };
            if (SHAppBarMessage(AbmGetAutoHideBarEx, ref bar) != 0)
                edges |= 1 << edge;
        }

        if (edges == 0)
        {
            var taskbar = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
            if (SHAppBarMessage(AbmGetTaskbarPos, ref taskbar) != 0 && taskbar.Edge <= AbeBottom
                && MonitorFromRect(taskbar.Rect, MonitorDefaultToNearest) == monitor)
                edges = 1 << (int)taskbar.Edge;
        }

        return edges;
    }

    /// <summary>Work area of the window's (nearest) monitor in window DIPs; empty when unknown.</summary>
    internal static Rect WorkAreaOf(Window owner, IntPtr window)
    {
        if (window == IntPtr.Zero || !TryMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), out var info))
            return Rect.Empty;

        var dpi = UiScale.MonitorScale(owner);
        var work = info.Work;
        return new Rect(work.Left / dpi, work.Top / dpi, (work.Right - work.Left) / dpi, (work.Bottom - work.Top) / dpi);
    }

    /// <summary>
    /// Maximized rect (physical pixels) on a monitor with auto-hide edges: the work area inset <see cref="HiddenTaskbarStrip"/> px on
    /// each such edge. False when there are none (Windows' default overhanging placement applies).
    /// </summary>
    private static bool MaximizedPlace(IntPtr monitorHandle, out NativeRect place, out NativeRect monitor)
    {
        (place, monitor) = (default, default);
        var edges = AutoHideEdges(monitorHandle);
        if (edges == 0 || !TryMonitorInfo(monitorHandle, out var info))
            return false;

        monitor = info.Monitor;
        var work = info.Work;
        var (left, top, right, bottom) = MaximizedPlaceOf((monitor.Left, monitor.Top, monitor.Right, monitor.Bottom), (work.Left, work.Top, work.Right, work.Bottom), edges);
        place = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom };
        return true;
    }

    /// <summary>
    /// Pure maximized-rect computation from monitor rect, work area and auto-hide edge bits (1 &lt;&lt; edge, LTRB): the work area with
    /// each such edge <see cref="HiddenTaskbarStrip"/> px inside the monitor edge. Exercised by golden window-frame-messages.
    /// </summary>
    internal static (int Left, int Top, int Right, int Bottom) MaximizedPlaceOf((int Left, int Top, int Right, int Bottom) monitor,
        (int Left, int Top, int Right, int Bottom) work, int edges)
    {
        var place = work;
        if ((edges & (1 << AbeLeft)) != 0)
            place.Left = Math.Max(place.Left, monitor.Left + HiddenTaskbarStrip);
        if ((edges & (1 << AbeTop)) != 0)
            place.Top = Math.Max(place.Top, monitor.Top + HiddenTaskbarStrip);
        if ((edges & (1 << AbeRight)) != 0)
            place.Right = Math.Min(place.Right, monitor.Right - HiddenTaskbarStrip);
        if ((edges & (1 << AbeBottom)) != 0)
            place.Bottom = Math.Min(place.Bottom, monitor.Bottom - HiddenTaskbarStrip);
        return place;
    }

    /// <summary>
    /// WM_WINDOWPOSCHANGING while maximized: replaces any proposed rect (maximize, Win+Shift+Arrow, taskbar setting change) with the
    /// target monitor's <see cref="MaximizedPlace"/>; monitors without auto-hide keep Windows' rect. Restores are untouched (Windows
    /// clears the maximized state before moving).
    /// </summary>
    private static void KeepMaximizedPlaceShort(IntPtr window, IntPtr lParam)
    {
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((pos.Flags & SwpNoSize) != 0 && (pos.Flags & SwpNoMove) != 0 || !IsZoomed(window) || !GetWindowRect(window, out var now))
            return;

        var proposed = new NativeRect
        {
            Left = (pos.Flags & SwpNoMove) != 0 ? now.Left : pos.X,
            Top = (pos.Flags & SwpNoMove) != 0 ? now.Top : pos.Y,
        };
        proposed.Right = proposed.Left + ((pos.Flags & SwpNoSize) != 0 ? now.Right - now.Left : pos.Width);
        proposed.Bottom = proposed.Top + ((pos.Flags & SwpNoSize) != 0 ? now.Bottom - now.Top : pos.Height);
        if (!MaximizedPlace(MonitorFromRect(proposed, MonitorDefaultToNearest), out var place, out _)
            || (proposed.Left == place.Left && proposed.Top == place.Top && proposed.Right == place.Right && proposed.Bottom == place.Bottom))
            return;

        (pos.X, pos.Y, pos.Width, pos.Height) = (place.Left, place.Top, place.Right - place.Left, place.Bottom - place.Top);
        pos.Flags &= ~(SwpNoMove | SwpNoSize);
        Marshal.StructureToPtr(pos, lParam, false);
    }

    /// <summary>
    /// Corrects a window maximized before the hook existed (WPF creates it with WS_MAXIMIZE, placed at HWND creation and not again
    /// on show) or after auto-hide was enabled, to the <see cref="KeepMaximizedPlaceShort"/> rect. Immediate at SourceInitialized
    /// (still hidden, so Frame 1 is correct) or deferred with <paramref name="later"/> when inside WM_SIZE (WPF lays out from that
    /// size afterwards). SetWindowPos keeps the maximized state (measured).
    /// </summary>
    internal static void KeepClearOfHiddenTaskbarNow(IntPtr window, bool later = false)
    {
        if (later)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => KeepClearOfHiddenTaskbarNow(window),
                System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        if (window == IntPtr.Zero || !IsZoomed(window) || !GetWindowRect(window, out var now)
            || !MaximizedPlace(MonitorFromWindow(window, MonitorDefaultToNearest), out var place, out _)
            || (now.Left == place.Left && now.Top == place.Top && now.Right == place.Right && now.Bottom == place.Bottom))
            return;

        SetWindowPos(window, IntPtr.Zero, place.Left, place.Top, place.Right - place.Left, place.Bottom - place.Top, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>Whether a screen point lies in the reserved strip along an auto-hide edge of the window's monitor.</summary>
    private static bool InHiddenTaskbarStrip(IntPtr window, int x, int y)
    {
        var monitorHandle = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (!TryMonitorInfo(monitorHandle, out var info))
            return false;

        // Query auto-hide edges (cached 1 s, AutoHideEdges) only for points at a monitor edge, where the pointer often rests (Close).
        var monitor = info.Monitor;
        if (x < monitor.Left || x >= monitor.Right || y < monitor.Top || y >= monitor.Bottom)
            return false;
        var near = (x < monitor.Left + HiddenTaskbarStrip ? 1 << AbeLeft : 0) | (y < monitor.Top + HiddenTaskbarStrip ? 1 << AbeTop : 0)
            | (x >= monitor.Right - HiddenTaskbarStrip ? 1 << AbeRight : 0) | (y >= monitor.Bottom - HiddenTaskbarStrip ? 1 << AbeBottom : 0);
        if (near == 0)
            return false;

        return (near & AutoHideEdges(monitorHandle)) != 0;
    }

    // Undocumented WM_NCUAHDRAWCAPTION / WM_NCUAHDRAWFRAME: themed DefWindowProc's classic caption/frame paint after title, icon
    // or style changes, outside WM_NCPAINT.
    private const int WmNcPaint = 0x0085, WmNcUahDrawCaption = 0x00AE, WmNcUahDrawFrame = 0x00AF;

    // Other frame messages: activation, title/icon change, size-move loop bounds, DPI and theme changes.
    private const int WmNcActivate = 0x0086, WmSetText = 0x000C, WmSetIcon = 0x0080, WmEnterSizeMove = 0x0231, WmExitSizeMove = 0x0232,
        WmDpiChanged = 0x02E0, WmThemeChanged = 0x031A;
    private const long WsVisible = 0x10000000;

    /// <summary>
    /// Paints any on-screen frame outside the client area (the auto-hide strip, which Windows painted white: measured 255,255,255)
    /// with the window background, leaving the client area alone. Returns false when there is no such frame or the window is
    /// minimized, so WM_NCPAINT falls through to Windows (which then has an empty frame).
    /// </summary>
    private static bool PaintFrameStrip(IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is not Window window || IsIconic(hwnd) || !FrameOutsideClient(hwnd, out var frame, out var client))
            return false;

        // No DC available: still report handled so Windows never paints it white.
        var dc = GetWindowDC(hwnd);
        if (dc == IntPtr.Zero)
            return true;

        var color = window.Background is System.Windows.Media.SolidColorBrush background ? background.Color : System.Windows.Media.Colors.Black;
        var brush = CreateSolidBrush(color.R | color.G << 8 | color.B << 16);

        // Window DC origin is the window's top-left; exclude the client area.
        ExcludeClipRect(dc, client.Left - frame.Left, client.Top - frame.Top, client.Right - frame.Left, client.Bottom - frame.Top);
        FillRect(dc, new NativeRect { Right = frame.Right - frame.Left, Bottom = frame.Bottom - frame.Top }, brush);
        DeleteObject(brush);
        ReleaseDC(hwnd, dc);
        return true;
    }

    /// <summary>Whether any frame lies outside the client area on screen (the auto-hide strip), in physical pixels.</summary>
    private static bool FrameOutsideClient(IntPtr hwnd, out NativeRect frame, out NativeRect client)
    {
        client = default;
        return GetWindowRect(hwnd, out frame) && ClientOnScreen(hwnd, out client)
            && (client.Left > frame.Left || client.Top > frame.Top || client.Right < frame.Right || client.Bottom < frame.Bottom);
    }

    // ----- Frame message hook (shared by all WindowChrome windows) -----
    // Main window (via TitleBarHook), Settings and side windows (DockWindows) share FrameHook, whose policy is the pure FrameDecision
    // (golden window-frame-messages prints it). WindowChromeWorker's hook is added first (XAML sets chrome before SourceInitialized)
    // and hooks run last-added first, so FrameHook answers before WindowChrome:
    // - WM_NCACTIVATE: DefWindowProc with lParam -1 (active look changes, caption not repainted); return its result (0 would block deactivation).
    // - WM_SETTEXT, WM_SETICON: DefWindowProc with WS_VISIBLE temporarily cleared (restored only if it was set) so no caption paints;
    //   mirrors WindowChromeWorker but independent of hook order.
    // - WM_NCCALCSIZE: never handled; WindowChromeWorker always returns client = window (WVR_REDRAW). Frame outside the client area
    //   disables DWM frame rendering (see taskbar section); DefWindowProc would add a classic caption and border.
    // - WM_WINDOWPOSCHANGING: maximized with auto-hide -> MaximizedPlace of the target monitor. WM_GETMINMAXINFO is left to Windows,
    //   which applies it to whichever monitor the window maximizes on, so per-current-monitor values were wrong elsewhere.
    // - WM_NCPAINT: handled only when frame lies outside the client area (paint the strip); otherwise passed on, including when
    //   minimized (handling it then permanently disabled DWM frame rendering, measured).
    // - WM_NCUAHDRAWCAPTION, WM_NCUAHDRAWFRAME: always swallowed (WindowChromeWorker ignores them).
    // - WM_NCHITTEST: physical-pixel hit test (FrameHitTest).

    /// <summary>What <see cref="FrameHook"/> does with a message (<see cref="FrameDecision"/>).</summary>
    internal enum FrameAnswer
    {
        /// <summary>Not handled: pass to later hooks (WindowChrome, Window) and DefWindowProc.</summary>
        PassOn,

        /// <summary>Swallow: suppresses classic caption and frame painting.</summary>
        Swallow,

        /// <summary>DefWindowProc with lParam -1, returning its result: active state changes without caption repaint.</summary>
        ActivateNoPaint,

        /// <summary>DefWindowProc with WS_VISIBLE temporarily cleared (if set), returning its result: title/icon stored, nothing painted.</summary>
        DefaultHidden,

        /// <summary>Paint frame outside the client area with the background (<see cref="PaintFrameStrip"/>), then handle.</summary>
        PaintStrip,

        /// <summary>Clamp the maximized rect short of auto-hide edges (WM_WINDOWPOSCHANGING), then pass on.</summary>
        KeepShortThenPassOn,

        /// <summary>Physical-pixel hit test (<see cref="FrameHitTest"/>); falls back to WindowChrome when undetermined.</summary>
        HitTest,
    }

    /// <summary>
    /// Pure hook policy per message and state: <paramref name="zoomed"/> (maximized), <paramref name="iconic"/> (minimized),
    /// <paramref name="stripOutside"/> (frame outside the client area, auto-hide only). Shared by the hook and golden window-frame-messages.
    /// </summary>
    internal static FrameAnswer FrameDecision(int message, nint wParam, bool zoomed, bool iconic, bool stripOutside) => message switch
    {
        // All states, regardless of wParam.
        WmNcActivate => FrameAnswer.ActivateNoPaint,
        WmSetText or WmSetIcon => FrameAnswer.DefaultHidden,
        WmNcUahDrawCaption or WmNcUahDrawFrame => FrameAnswer.Swallow,
        WmNcHitTest => FrameAnswer.HitTest,
        WmWindowPosChanging => FrameAnswer.KeepShortThenPassOn,
        // Never swallowed: handling it unpainted while minimized (taskbar-less Settings shows as a desktop caption) makes Windows
        // clip with a region and permanently disable DWM frame rendering.
        WmNcPaint => zoomed && stripOutside ? FrameAnswer.PaintStrip : FrameAnswer.PassOn,
        _ => FrameAnswer.PassOn,
    };

    /// <summary>
    /// HwndSource hook for every WindowChrome window (main via <see cref="TitleBarHook"/>, Settings, side windows): suppresses classic
    /// caption/frame painting, keeps maximized windows short of auto-hide taskbars (client = window), and hit-tests in physical
    /// pixels (FrameHitTest) so caption buttons work under UiScale.
    /// </summary>
    internal static IntPtr FrameHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmSettingChange && wParam == SpiSetWorkArea || message == WmDisplayChange)
            ForgetHiddenTaskbars();
        else if (message == WmWindowPosChanged)
            BackdropAfterMove(hwnd);
        else if (message == WmDestroy)
            ForgetBackdrops(hwnd);
        // Only WM_NCPAINT depends on window state; all others are decided by message id.
        var zoomed = message is WmNcPaint && IsZoomed(hwnd);
        var iconic = message is WmNcPaint && IsIconic(hwnd);
        var outside = zoomed && message is WmNcPaint && FrameOutsideClient(hwnd, out _, out _);
        var answer = FrameDecision(message, wParam, zoomed, iconic, outside);
        var result = IntPtr.Zero;
        switch (answer)
        {
            case FrameAnswer.ActivateNoPaint:
                result = DefWindowProc(hwnd, WmNcActivate, wParam, -1);
                handled = true;
                break;
            case FrameAnswer.DefaultHidden:
                result = DefWindowProcHidden(hwnd, message, wParam, lParam);
                handled = true;
                break;
            case FrameAnswer.KeepShortThenPassOn:
                KeepMaximizedPlaceShort(hwnd, lParam);
                break;
            case FrameAnswer.PaintStrip:
                if (PaintFrameStrip(hwnd))
                    handled = true;
                break;
            case FrameAnswer.Swallow:
                handled = true;
                break;
            case FrameAnswer.HitTest when HwndSource.FromHwnd(hwnd)?.RootVisual is Window window && FrameHitTest(window, hwnd, lParam) is { } hit:
                result = hit;
                handled = true;
                break;
        }

        if (DevFlightRecorder.IsEnabled)
            TraceFrameMessage(hwnd, message, wParam, answer, handled);
        return result;
    }

    /// <summary>
    /// Forwards WM_SETTEXT/WM_SETICON to DefWindowProc with WS_VISIBLE temporarily cleared (as WindowChromeWorker does) so no caption
    /// paints; the bit is restored only if it was set, merged onto the resulting style. Returns DefWindowProc's result.
    /// </summary>
    private static IntPtr DefWindowProcHidden(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var visible = (style & WsVisible) != 0;
        if (visible)
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style & ~WsVisible));

        var result = DefWindowProc(hwnd, message, wParam, lParam);
        if (visible)
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(GetWindowLongPtr(hwnd, GwlStyle).ToInt64() | WsVisible));
        return result;
    }

    // ----- Frame message tracing (developer flight recorder) -----
    // With DevFlightRecorder verbose (dev-verbose.flag or Settings > General > Diagnostics, decided after Frame 1), FrameHook logs
    // message, window, state, wParam and decision; when off the cost is one IsEnabled check. Policy:
    // - WM_NCCALCSIZE/WM_NCPAINT (per move step): only inside a size-move loop and for 1 s after another frame message or a state change;
    // - other frame messages (NCACTIVATE, SETTEXT, SETICON, NCUAHDRAW*, loop bounds, DPICHANGED, THEMECHANGED): always;
    // - capped at FrameTraceCap lines/s and FrameTraceLoopCap frequent lines per loop (overflow counted in one line) to bound ring usage.

    private const int FrameTraceCap = 40;

    // HWND in a size-move loop (0 = none); TickCount64 deadline for frequent messages; current second's recorded/dropped counts.
    private static IntPtr _traceSizeMove;
    private static long _traceUntil, _traceSecond;
    private static int _tracedThisSecond, _traceDropped, _tracedThisLoop, _droppedThisLoop;

    // Max frequent lines per size-move loop (the ring holds DevFlightRecorder.Capacity lines total).
    private const int FrameTraceLoopCap = 60;

    // Last traced state per window (0 restored, 1 maximized, 2 minimized) for change detection.
    private static Dictionary<IntPtr, int>? _traceStates;

    /// <summary>Logs a frame message and <see cref="FrameHook"/>'s decision while the recorder is on; never throws.</summary>
    private static void TraceFrameMessage(IntPtr hwnd, int message, IntPtr wParam, FrameAnswer answer, bool handled)
    {
        var frequent = message is WmNcCalcSize or WmNcPaint or WmWindowPosChanging;
        if (!frequent && message is not (WmNcActivate or WmSetText or WmSetIcon or WmNcUahDrawCaption or WmNcUahDrawFrame
            or WmEnterSizeMove or WmExitSizeMove or WmDpiChanged or WmThemeChanged))
            return;

        try
        {
            var now = Environment.TickCount64;
            var state = IsIconic(hwnd) ? 2 : IsZoomed(hwnd) ? 1 : 0;
            _traceStates ??= new();
            if (_traceStates.Count > 8)
                _traceStates.Clear();
            var changed = _traceStates.TryGetValue(hwnd, out var was) && was != state;
            _traceStates[hwnd] = state;

            if (message == WmEnterSizeMove)
                (_traceSizeMove, _tracedThisLoop, _droppedThisLoop) = (hwnd, 0, 0);
            else if (message == WmExitSizeMove && _traceSizeMove == hwnd)
            {
                _traceSizeMove = IntPtr.Zero;
                if (_droppedThisLoop > 0)
                    DevFlightRecorder.Record("frame", string.Create(CultureInfo.InvariantCulture, $"{_droppedThisLoop} more frame messages in that move or size loop were not recorded"));
            }

            // Bound ring usage: keep each loop's first lines, count the rest.
            if (frequent && _traceSizeMove == hwnd && ++_tracedThisLoop > FrameTraceLoopCap)
            {
                _droppedThisLoop++;
                return;
            }
            if (changed || !frequent)
                _traceUntil = now + 1000;
            if (frequent && _traceSizeMove != hwnd && now > _traceUntil)
                return;

            if (now - _traceSecond >= 1000)
            {
                if (_traceDropped > 0)
                    DevFlightRecorder.Record("frame", string.Create(CultureInfo.InvariantCulture, $"{_traceDropped} more frame messages in the second before were not recorded"));
                (_traceSecond, _tracedThisSecond, _traceDropped) = (now, 0, 0);
            }

            if (++_tracedThisSecond > FrameTraceCap)
            {
                _traceDropped++;
                return;
            }

            var where = state switch
            {
                2 => "minimized",
                1 when FrameOutsideClient(hwnd, out _, out _) => "maximized, frame outside its client area",
                1 => "maximized",
                _ => "restored",
            };
            var window = HwndSource.FromHwnd(hwnd)?.RootVisual?.GetType().Name ?? "a window";
            var loop = _traceSizeMove == hwnd ? ", in a move or size loop" : "";
            // DWMWA_NCRENDERING_ENABLED: when off, Windows paints the classic frame.
            var desktopFrame = DwmGetWindowAttribute(hwnd, DwmwaNcRenderingEnabled, out var enabled, sizeof(int)) == 0 ? enabled != 0 ? ", desktop frame on" : ", DESKTOP FRAME OFF" : "";
            DevFlightRecorder.Record("frame", string.Create(CultureInfo.InvariantCulture,
                $"{FrameMessageName(message)} wParam 0x{wParam.ToInt64():X} to {window} 0x{hwnd.ToInt64():X} ({where}{loop}{desktopFrame}): {answer}, {(handled ? "answered here" : "passed on")}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Tracing must never break message handling.
        }
    }

    private static string FrameMessageName(int message) => message switch
    {
        WmNcCalcSize => "WM_NCCALCSIZE",
        WmWindowPosChanging => "WM_WINDOWPOSCHANGING",
        WmNcPaint => "WM_NCPAINT",
        WmNcActivate => "WM_NCACTIVATE",
        WmSetText => "WM_SETTEXT",
        WmSetIcon => "WM_SETICON",
        WmNcUahDrawCaption => "WM_NCUAHDRAWCAPTION",
        WmNcUahDrawFrame => "WM_NCUAHDRAWFRAME",
        WmEnterSizeMove => "WM_ENTERSIZEMOVE",
        WmExitSizeMove => "WM_EXITSIZEMOVE",
        WmDpiChanged => "WM_DPICHANGED",
        WmThemeChanged => "WM_THEMECHANGED",
        _ => string.Create(CultureInfo.InvariantCulture, $"0x{message:X4}"),
    };

    // ----- Snap Layouts: the Maximize button as Windows' own; the frame hit-tested in real pixels -----

    internal const int WmNcHitTest = 0x0084, WmNcMouseMove = 0x00A0, WmNcMouseLeave = 0x02A2, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2;
    internal const int HtNowhere = 0, HtClient = 1, HtCaption = 2, HtMaxButton = 9, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13,
        HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;

    // Hit-test codes by row (top/middle/bottom) and column (left/middle/right), matching WindowChrome.
    private static readonly int[,] FrameParts =
    {
        { HtTopLeft, HtTop, HtTopRight },
        { HtLeft, HtClient, HtRight },
        { HtBottomLeft, HtBottom, HtBottomRight },
    };

    /// <summary>Screen point from a frame mouse message's lParam, in signed physical pixels (monitors left/above primary are negative).</summary>
    internal static (int X, int Y) PointOf(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return ((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    /// <summary>
    /// WM_NCHITTEST for WindowChrome windows computed in physical pixels; null (WindowChrome answers) without chrome or HWND.
    /// WindowChrome converts by the root DPI, which UiScale sets to content pixels per DIP (1.5 on 2880 x 1800 at 200 %,
    /// UiScale.SetRootDpi) instead of the monitor's 2, so it hit-tested a third too far and caption buttons missed clicks.
    /// <list type="bullet">
    /// <item>Hit-test-visible chrome elements (WindowChrome.IsHitTestVisibleInChrome: caption buttons, status, Update) are HTCLIENT;
    /// ResizeGripDirection elements return their direction.</item>
    /// <item>Maximized: the auto-hide strip is HTNOWHERE (normally outside the window via MaximizedPlace; kept as a safeguard).</item>
    /// <item>Otherwise resize borders (when restored; ResizeBorderThickness) and caption (CaptionHeight from the content top) in
    /// window DIPs at monitor scale, else HTCLIENT.</item>
    /// </list>
    /// </summary>
    private static int? FrameHitTest(Window window, IntPtr hwnd, IntPtr lParam)
    {
        if (WindowChrome.GetWindowChrome(window) is not { } chrome || PresentationSource.FromVisual(window) is null || !GetWindowRect(hwnd, out var frame))
            return null;

        var (x, y) = PointOf(lParam);
        var zoomed = IsZoomed(hwnd);
        if (zoomed && InHiddenTaskbarStrip(hwnd, x, y))
            return HtNowhere;

        IInputElement? hit;
        try
        {
            hit = window.InputHitTest(window.PointFromScreen(new Point(x, y)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Not on screen: defer to WindowChrome.
            return null;
        }

        if (hit is not null)
        {
            if (WindowChrome.GetIsHitTestVisibleInChrome(hit))
                return HtClient;
            var grip = WindowChrome.GetResizeGripDirection(hit);
            if (grip != ResizeGripDirection.None)
            {
                return grip switch
                {
                    ResizeGripDirection.TopLeft => HtTopLeft,
                    ResizeGripDirection.Top => HtTop,
                    ResizeGripDirection.TopRight => HtTopRight,
                    ResizeGripDirection.Right => HtRight,
                    ResizeGripDirection.BottomRight => HtBottomRight,
                    ResizeGripDirection.Bottom => HtBottom,
                    ResizeGripDirection.BottomLeft => HtBottomLeft,
                    ResizeGripDirection.Left => HtLeft,
                    _ => HtCaption,
                };
            }
        }

        // Frame regions in pixels: WindowChrome DIPs (caption height scaled with content, UiScale.ScaleCaption) at monitor scale.
        // Caption = CaptionHeight from the content top (inset by MaximizedFrameInset when maximized). Maximized or non-resizable
        // windows have no resize borders.
        var scale = UiScale.MonitorScale(window);
        var border = chrome.ResizeBorderThickness;
        var top = ClientOnScreen(hwnd, out var client) ? client.Top : frame.Top;
        var contentTop = top + ((window.Content as FrameworkElement)?.Margin.Top ?? 0) * scale;
        var sizable = !zoomed && window.ResizeMode is System.Windows.ResizeMode.CanResize or System.Windows.ResizeMode.CanResizeWithGrip;
        var onTopBorder = sizable && y < frame.Top + border.Top * scale;
        var row = y < contentTop + chrome.CaptionHeight * scale ? 0 : sizable && y >= frame.Bottom - border.Bottom * scale ? 2 : 1;
        var column = !sizable ? 1 : x < frame.Left + border.Left * scale ? 0 : x >= frame.Right - border.Right * scale ? 2 : 1;

        // Below the top border, side edges resize horizontally rather than as corners.
        if (row == 0 && column != 1 && !onTopBorder)
            row = 1;
        var part = FrameParts[row, column];
        return part == HtTop && !onTopBorder ? HtCaption : part;
    }
}
