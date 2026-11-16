using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using HandPegApp.Native;
using static HandPegApp.Native.User32;
using static HandPegApp.Native.Gdi32;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp;

/// <summary>
/// Video host: a native child HWND for the GPU preview swap chain, plus a transparent owned overlay window exactly over it for WPF
/// content. Airspace rules mean a native child always covers its parent's WPF content, so overlays (crop rectangle, drop hint) live
/// in the overlay window, which tracks the child wherever it goes.
///
/// The overlay is non-activating: clicks reach its content while keyboard focus stays with the main window. Fully transparent
/// pixels pass clicks through.
/// </summary>
[ContentProperty(nameof(Overlay))]
public sealed partial class VideoSurface : HwndHost
{
    private const int WsChild = 0x40000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000, CsHRedraw = 0x2, CsVRedraw = 0x1, BlackBrush = 4;
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    private const int WmNcHitTest = 0x0084, HtTransparent = -1, WmMouseMove = 0x0200, WmMouseLeave = 0x02A3, TmeLeave = 0x2, WmSize = 0x0005,
        WmWindowPosChanged = 0x0047;

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse
    {
        public int Size, Flags;
        public IntPtr Window;
        public int HoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TrackMouse track);

    // TrackMouseEvent(TME_LEAVE) armed on the surface HWND until WM_MOUSELEAVE arrives (one-shot per entry).
    private bool _trackingLeave;

    private Window? _owner;
    private Window? _overlayWindow;
    private Int32Rect _placed;

    // Visible region of the overlay in its own pixels when clipped to the owner; empty = unclipped.
    private Int32Rect _clip;

    // Overlay placement queued by WM_WINDOWPOSCHANGED (coalesced to one per dispatcher turn during resize drags).
    private bool _placeQueued;

    public VideoSurface()
    {
        Loaded += (_, _) => CreateOverlay();
        LayoutUpdated += (_, _) => PlaceOverlay();
        IsVisibleChanged += (_, _) => PlaceOverlay();
    }

    /// <summary>
    /// Native window returns HTTRANSPARENT so the pointer reaches WPF content beneath (side window Live Video Preview, double-click for
    /// full screen). The main player surface keeps the pointer.
    /// </summary>
    public bool PassesPointer { get; init; }

    /// <summary>Overlay content above the video; set once in XAML.</summary>
    public UIElement? Overlay { get; set; }

    /// <summary>
    /// Window hosting <see cref="Overlay"/> (null until created). Keybinds use it to identify player input (WASD steering, Lightning Dial).
    /// </summary>
    public Window? OverlayWindow => _overlayWindow;

    /// <summary>
    /// Raw message hook on the current overlay window, ahead of WPF input (3D view horizontal wheel, which WPF does not surface, and its
    /// wheel modifier flags).
    /// </summary>
    public event HwndSourceHook? OverlayMessageHook;

    /// <summary>
    /// Pointer position over the player in element DIPs, from either the native window or the overlay (wipe handle, clip boxes); null after
    /// leaving both. Used by the Live Encoded Preview Magnifier. Leave tracking is armed only while a handler is attached.
    /// </summary>
    public event Action<Point?>? PointerMoved;

    /// <summary>Native render HWND; zero until created.</summary>
    public IntPtr SurfaceHandle { get; private set; }

    /// <summary>Raised when <see cref="SurfaceHandle"/> has been created.</summary>
    public event Action? SurfaceReady;

    /// <summary>
    /// Raised on the native window's WM_SIZE (inside the host's move, after the client rect changed). SizeChanged fires before the host
    /// moves the window, so a consumer relying on it alone may read the old size.
    /// </summary>
    public event Action? SurfaceResized;

    // Surface window class: DefWindowProcW with a black background brush. A "static" class paints COLOR_WINDOWFRAME (grey on
    // Windows 10/11), which showed as a grey box at launch.
    private static readonly Lazy<string> SurfaceClass = new(() =>
    {
        const string name = "HandPegVideoSurface";
        var windowClass = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(), Style = CsHRedraw | CsVRedraw,
            Procedure = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"), Instance = GetModuleHandle(null),
            Background = GetStockObject(BlackBrush), ClassName = Marshal.StringToHGlobalUni(name),
        };

        // Class name string lives for the process (as the class does); falls back to "static" if registration fails.
        return windowClass.Procedure != IntPtr.Zero && RegisterClassEx(windowClass) != 0 ? name : "static";
    });

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Created hidden (no WS_VISIBLE) and black: the host shows it after layout, so it never flashes at the first-measure size before
        // other content draws.
        SurfaceHandle = CreateWindowEx(
            0, SurfaceClass.Value, "", WsChild | WsClipChildren | WsClipSiblings, 0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        Dispatcher.BeginInvoke(() => SurfaceReady?.Invoke());
        return new HandleRef(this, SurfaceHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        SurfaceHandle = IntPtr.Zero;
        DestroyWindow(hwnd.Handle);
    }

    /// <summary>
    /// The window is edge-to-edge, so in a restored window's resize band the native child returns HTTRANSPARENT and the parent answers as
    /// frame (WindowFrame.FrameHitTest: HTLEFT etc.), keeping resize available even when the player touches the edge.
    /// </summary>
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Picture-only surfaces (Live Video Preview mirrors) pass the pointer through.
        if (msg == WmNcHitTest && PassesPointer)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        if (msg == WmNcHitTest && InsideResizeBand(_owner) is { } inside)
        {
            var value = lParam.ToInt64();
            var (x, y) = unchecked(((int)(short)(value & 0xFFFF), (int)(short)((value >> 16) & 0xFFFF)));
            if (x < inside.X || y < inside.Y || x >= inside.X + inside.Width || y >= inside.Y + inside.Height)
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }
        }

        // WM_MOUSELEAVE consumes the TME_LEAVE request whether handled or not; the next move re-arms it.
        if (msg == WmMouseLeave)
            _trackingLeave = false;
        if (msg is WmMouseMove or WmMouseLeave && PointerMoved is not null)
            TellPointer(hwnd, msg, lParam);
        if (msg == WmSize && hwnd == SurfaceHandle)
            SurfaceResized?.Invoke();

        // Host moved/sized/showed/hid the native window after layout: re-place the overlay from the real rect, read after the move.
        if (msg == WmWindowPosChanged && hwnd == SurfaceHandle)
            QueuePlaceOverlay();

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>
    /// Forwards moves/leaves from the native window or the coincident overlay as <see cref="PointerMoved"/>: moves as DIP points (arming
    /// TME_LEAVE); leave only when the pointer is off both windows, since crossing from picture to overlay handle leaves one for the other.
    /// </summary>
    private void TellPointer(IntPtr window, int msg, IntPtr lParam)
    {
        if (msg == WmMouseMove)
        {
            // WPF arms the overlay's own leave tracking for its MouseLeave.
            if (!_trackingLeave && window == SurfaceHandle)
            {
                var track = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = TmeLeave, Window = window };
                _trackingLeave = TrackMouseEvent(ref track);
            }

            var value = lParam.ToInt64();
            var (x, y) = unchecked(((int)(short)(value & 0xFFFF), (int)(short)((value >> 16) & 0xFFFF)));
            var dpi = VisualTreeHelper.GetDpi(this);
            PointerMoved?.Invoke(new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY));
            return;
        }

        if (!PointerOverPlayer())
            PointerMoved?.Invoke(null);
    }

    // True if WindowFromPoint at the cursor is the surface or overlay (transparent overlay pixels resolve to the surface; side windows or
    // popups over the player do not count).
    private bool PointerOverPlayer()
    {
        if (SurfaceHandle == IntPtr.Zero || !GetCursorPos(out var at))
            return false;
        var under = WindowFromPoint(at);
        return under != IntPtr.Zero && (under == SurfaceHandle
            || (_overlayWindow is { } overlay && under == new WindowInteropHelper(overlay).Handle));
    }

    /// <summary>
    /// Screen-pixel rect of a restored, resizable WindowChrome window minus its resize band (ResizeBorderThickness at monitor scale, matching
    /// WindowFrame.FrameHitTest). Null when maximized/minimized, not resizable, without WindowChrome, or without a handle (side windows
    /// included).
    /// </summary>
    private static Int32Rect? InsideResizeBand(Window? owner)
    {
        if (owner is null || owner.WindowState != WindowState.Normal || owner.ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            || System.Windows.Shell.WindowChrome.GetWindowChrome(owner) is not { } chrome)
            return null;

        var handle = new WindowInteropHelper(owner).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var frame))
            return null;

        // Band is x < left + border (ceil) and x >= right - border (floor), matching FrameHitTest's comparisons.
        var scale = Services.UiScale.MonitorScale(owner);
        var border = chrome.ResizeBorderThickness;
        var (left, top) = (frame.Left + (int)Math.Ceiling(border.Left * scale), frame.Top + (int)Math.Ceiling(border.Top * scale));
        var (right, bottom) = (frame.Right - (int)Math.Floor(border.Right * scale), frame.Bottom - (int)Math.Floor(border.Bottom * scale));
        return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : null;
    }

    /// <summary>
    /// Creates the overlay window, or re-owns it when the player moves to another window (side window), so it always follows the
    /// player's host window.
    /// </summary>
    private void CreateOverlay()
    {
        if (Overlay is null || Window.GetWindow(this) is not { } owner || ReferenceEquals(owner, _owner))
            return;

        if (_owner is { } previous)
        {
            previous.LocationChanged -= Owner_Moved;
            previous.SizeChanged -= Owner_Moved;
            previous.StateChanged -= Owner_Moved;
            previous.Closing -= Owner_Closing;
            previous.Closed -= Owner_Closed;
        }

        _owner = owner;
        if (_overlayWindow is null)
        {
            _overlayWindow = new Window
            {
                Content = Overlay,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            var made = _overlayWindow;

            // Sized to the video in physical pixels: content gets UI scale, window size does not. A new window starts unclipped.
            Services.UiScale.SetKeepsItsSize(made, true);
            _clip = Int32Rect.Empty;
            made.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(made).Handle;
                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExNoActivate | WsExToolWindow));
                HwndSource.FromHwnd(handle)?.AddHook(OverlayMessage);
            };

            // WPF applies WM_DPICHANGED's suggested rect (old size x DPI ratio); re-place from the video rect after the new DPI applies.
            made.DpiChanged += (_, _) =>
            {
                _placed = default;
                QueuePlaceOverlay();
            };

            // Closed externally (e.g. with its owner): recreate on next CreateOverlay.
            made.Closed += (_, _) =>
            {
                made.Content = null;
                if (ReferenceEquals(_overlayWindow, made))
                    _overlayWindow = null;
            };
        }

        // Owned by the player's host window (stays above it, closes with the app); scale sourced from the owner regardless of the monitor
        // Windows first reports for the overlay.
        _overlayWindow.Owner = owner;
        Services.UiScale.SetScaleSource(_overlayWindow, owner);
        Services.UiScale.Apply(_overlayWindow);
        owner.LocationChanged += Owner_Moved;
        owner.SizeChanged += Owner_Moved;
        owner.StateChanged += Owner_Moved;
        owner.Closing += Owner_Closing;
        owner.Closed += Owner_Closed;
        _placed = default;
        PlaceOverlay();
        KeepUnderSideWindow();
    }

    private IntPtr OverlayMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Overlay is pixel-coincident with the surface, so its moves are player moves too (Magnifier over the wipe handle).
        if (msg is WmMouseMove or WmMouseLeave && PointerMoved is not null)
            TellPointer(hwnd, msg, lParam);
        return OverlayMessageHook is { } hook ? hook(hwnd, msg, wParam, lParam, ref handled) : IntPtr.Zero;
    }

    private void Owner_Moved(object? sender, EventArgs e) => PlaceOverlay();

    // Owners close owned windows: a closing side window first detaches the overlay as the player returns to the main window.
    private void Owner_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (e.Cancel || _overlayWindow is not { } overlay || !ReferenceEquals(sender, _owner) || ReferenceEquals(sender, Application.Current?.MainWindow))
            return;

        overlay.Hide();
        overlay.Owner = null;
    }

    // Side window closed: detach only (player returns). Main window closed: close the overlay (app exit).
    private void Owner_Closed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _owner))
            return;

        _owner = null;
        if (Application.Current?.MainWindow is { } main && ReferenceEquals(sender, main))
        {
            _overlayWindow?.Close();
            _overlayWindow = null;
        }
        else if (_overlayWindow is { } overlay && new WindowInteropHelper(overlay).Handle != IntPtr.Zero)
        {
            overlay.Hide();
            overlay.Owner = null;
        }
    }

    /// <summary>
    /// The overlay and side windows share the main window as owner, and among siblings the last shown/raised is topmost, so a re-shown
    /// overlay would cover side windows. Re-inserts it directly below the lowest visible side window (Docking.WindowOrder) every time.
    /// </summary>
    private void KeepUnderSideWindow()
    {
        if (_overlayWindow is not { } overlay || _owner is not { } owner)
            return;

        var sides = new List<IntPtr>();
        foreach (Window other in owner.OwnedWindows)
        {
            if (other is Docking.SideWindow { IsVisible: true } side && new WindowInteropHelper(side).Handle is var sideHandle && sideHandle != IntPtr.Zero)
                sides.Add(sideHandle);
        }

        var lowest = sides.Count switch { 0 => IntPtr.Zero, 1 => sides[0], _ => Docking.WindowOrder.Lowest(sides) };
        if (lowest != IntPtr.Zero)
            SetWindowPos(new WindowInteropHelper(overlay).Handle, lowest, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void QueuePlaceOverlay()
    {
        if (_placeQueued)
            return;
        _placeQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _placeQueued = false;
            PlaceOverlay();
        }, System.Windows.Threading.DispatcherPriority.Send);
    }

    /// <summary>
    /// Places the overlay exactly over the native surface, or hides it when not visible. Uses the surface's physical client rect
    /// (GetClientRect + ClientToScreen), never WPF layout x DPI, which is wrong where element DPI and root UI scale differ (overlay
    /// overhung adjacent panes). Clipped via SetWindowRgn to the owner's client area (and resize band when restored), so it never
    /// extends past the window edge or, maximized, over the auto-hide taskbar strip (MainWindow.TitleBar.cs).
    /// </summary>
    internal void PlaceOverlay()
    {
        if (_overlayWindow is not { } overlay || _owner is not { } owner)
            return;

        if (!IsVisible || owner.WindowState == WindowState.Minimized || !owner.IsVisible || PresentationSource.FromVisual(this) is null
            || SurfaceRectangle() is not { } place)
        {
            HideOverlay(overlay);
            return;
        }

        // Visible part = intersection with the owner's client area (hide if empty); when restored, also exclude the resize band, which
        // belongs to the frame (WndProc).
        var shown = ClientAreaOf(owner) is { } client ? Intersection(place, client) : place;
        if (InsideResizeBand(owner) is { } inside)
            shown = Intersection(shown, inside);
        if (shown.Width <= 0 || shown.Height <= 0)
        {
            HideOverlay(overlay);
            return;
        }

        if (!overlay.IsVisible)
        {
            overlay.Show();
            _placed = default;
            KeepUnderSideWindow();
        }

        var clip = shown == place ? Int32Rect.Empty : new Int32Rect(shown.X - place.X, shown.Y - place.Y, shown.Width, shown.Height);
        if (place == _placed && clip == _clip)
            return;

        var handle = new WindowInteropHelper(overlay).Handle;
        if (place != _placed)
        {
            _placed = place;
            var crumb = HandPegApp.Services.GpuNativeBreadcrumbs.Enter("PlaceOverlay.SetWindowPos", $"{place.X},{place.Y} {place.Width}x{place.Height}");
            SetWindowPos(handle, IntPtr.Zero, place.X, place.Y, place.Width, place.Height, SwpNoZOrder | SwpNoActivate);
            crumb.Leave();
        }

        if (clip != _clip)
        {
            _clip = clip;
            CutTo(handle, clip);
        }
    }

    private void HideOverlay(Window overlay)
    {
        if (overlay.IsVisible)
            overlay.Hide();
        _placed = default;
    }

    /// <summary>Surface client rect in physical screen pixels; null if not created, hidden or empty.</summary>
    internal Int32Rect? SurfaceRectangle()
    {
        var corner = new NativePoint();
        if (SurfaceHandle == IntPtr.Zero || !IsWindowVisible(SurfaceHandle) || !GetClientRect(SurfaceHandle, out var area) || !ClientToScreen(SurfaceHandle, ref corner))
            return null;
        var (width, height) = (area.Right - area.Left, area.Bottom - area.Top);
        return width > 0 && height > 0 ? new Int32Rect(corner.X, corner.Y, width, height) : null;
    }

    /// <summary>Window client area in physical screen pixels; null without a handle.</summary>
    private static Int32Rect? ClientAreaOf(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var corner = new NativePoint();
        if (handle == IntPtr.Zero || !GetClientRect(handle, out var area) || !ClientToScreen(handle, ref corner))
            return null;
        return new Int32Rect(corner.X, corner.Y, Math.Max(0, area.Right - area.Left), Math.Max(0, area.Bottom - area.Top));
    }

    private static Int32Rect Intersection(Int32Rect a, Int32Rect b)
    {
        var (left, top) = (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var (right, bottom) = (Math.Min(a.X + a.Width, b.X + b.Width), Math.Min(a.Y + a.Height, b.Y + b.Height));
        return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : new Int32Rect(left, top, 0, 0);
    }

    /// <summary>Sets the window region to <paramref name="clip"/> (window pixels; empty = none), limiting both drawing and hit-testing.</summary>
    private static void CutTo(IntPtr window, Int32Rect clip)
    {
        if (window == IntPtr.Zero)
            return;
        var crumb = HandPegApp.Services.GpuNativeBreadcrumbs.Enter("PlaceOverlay.SetWindowRgn", $"{clip.X},{clip.Y} {clip.Width}x{clip.Height}");
        if (clip.IsEmpty)
        {
            SetWindowRgn(window, IntPtr.Zero, true);
            crumb.Leave();
            return;
        }

        // SetWindowRgn takes ownership on success; delete the region only if the call fails.
        var region = CreateRectRgn(clip.X, clip.Y, clip.X + clip.Width, clip.Y + clip.Height);
        if (region != IntPtr.Zero && SetWindowRgn(window, region, true) == 0)
            DeleteObject(region);
        crumb.Leave();
    }
}
