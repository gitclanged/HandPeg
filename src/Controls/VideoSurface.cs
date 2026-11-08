using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Where the video is drawn: a native child window for the player (the GPU preview's swap chain) to draw into, and, lying exactly over
/// it, a transparent window of its own for WPF content. A native window always covers the WPF content of the
/// window it is in, so anything that has to appear on top of the video (the crop rectangle, the drop hint)
/// cannot simply be placed over it; it is the content of this layer instead, and is shown in that second
/// window, which follows the first wherever it goes.
///
/// The second window never takes the focus: a click on it reaches its content, and the keyboard stays with
/// the main window. Where it is fully transparent, clicks fall through to what is underneath.
/// </summary>
[ContentProperty(nameof(Overlay))]
public sealed partial class VideoSurface : HwndHost
{
    private const int WsChild = 0x40000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000, CsHRedraw = 0x2, CsVRedraw = 0x1, BlackBrush = 4;
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public int Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(in WindowClass windowClass);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? module);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetProcAddress(IntPtr module, string name);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr GetStockObject(int kind);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    private const int WmNcHitTest = 0x0084, HtTransparent = -1;

    private Window? _owner;
    private Window? _overlayWindow;
    private Int32Rect _placed;

    // The part of the second window that is shown (its own pixels), when it is cut to the window it lies on; empty: all of it.
    private Int32Rect _clip;

    public VideoSurface()
    {
        Loaded += (_, _) => CreateOverlay();
        LayoutUpdated += (_, _) => PlaceOverlay();
        IsVisibleChanged += (_, _) => PlaceOverlay();
    }

    /// <summary>What is shown on top of the video. Set once, in XAML, as this layer's content.</summary>
    public UIElement? Overlay { get; set; }

    /// <summary>
    /// The window that carries <see cref="Overlay"/> above the video (null until it is made): input that lands on it is
    /// the player's, which the keybinds tell apart by it (W, A, S and D steering what is under the pointer, the Lightning Dial, 4.4).
    /// </summary>
    public Window? OverlayWindow => _overlayWindow;

    /// <summary>The native window the player draws into; zero until it has been created.</summary>
    public IntPtr SurfaceHandle { get; private set; }

    /// <summary>Raised when <see cref="SurfaceHandle"/> has been created.</summary>
    public event Action? SurfaceReady;

    // The window class of the surface: Windows' own window procedure, painting black. (A "static" black rectangle is
    // painted in the window frame colour, which is grey on Windows 10 and 11: that was the grey box at launch.)
    private static readonly Lazy<string> SurfaceClass = new(() =>
    {
        const string name = "HandPegVideoSurface";
        var windowClass = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(), Style = CsHRedraw | CsVRedraw,
            Procedure = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"), Instance = GetModuleHandle(null),
            Background = GetStockObject(BlackBrush), ClassName = Marshal.StringToHGlobalUni(name),
        };

        // Kept for the life of the process, as the class is; "static" if it cannot be had.
        return windowClass.Procedure != IntPtr.Zero && RegisterClassEx(windowClass) != 0 ? name : "static";
    });

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Black until a player draws in it. Made hidden: the host shows it once it is laid out and meant to be seen, so
        // it never stands in the window, at the size the window was first measured at, before anything else is drawn.
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
    /// The window drawn edge to edge (5.3-Stage2): over a restored window's resize band the pointer is its frame's, the player at
    /// its edge or not. There the native window lets the pointer through (HTTRANSPARENT), so the window under it answers as its
    /// frame (MainWindow.FrameHitTest: HTLEFT and the rest) and is resized from there.
    /// </summary>
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>
    /// The part of a window that draws its own frame (WindowChrome) inside its resize band, on the screen in real pixels: while it is
    /// restored and can be resized, the whole window (its client area: it has no frame outside it) less the band Windows sizes it from,
    /// its ResizeBorderThickness at its monitor's scale, counted as MainWindow.FrameHitTest counts it. Null when it has no such band
    /// (maximized or minimized, not to be resized, Windows' own frame as the side window has, or no handle yet).
    /// </summary>
    private static Int32Rect? InsideResizeBand(Window? owner)
    {
        if (owner is null || owner.WindowState != WindowState.Normal || owner.ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            || System.Windows.Shell.WindowChrome.GetWindowChrome(owner) is not { } chrome)
            return null;

        var handle = new WindowInteropHelper(owner).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var frame))
            return null;

        // The band is x < left + border (so up to the pixel that reaches past it) and x >= right - border, as FrameHitTest compares.
        var scale = Services.UiScale.MonitorScale(owner);
        var border = chrome.ResizeBorderThickness;
        var (left, top) = (frame.Left + (int)Math.Ceiling(border.Left * scale), frame.Top + (int)Math.Ceiling(border.Top * scale));
        var (right, bottom) = (frame.Right - (int)Math.Floor(border.Right * scale), frame.Bottom - (int)Math.Floor(border.Bottom * scale));
        return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : null;
    }

    /// <summary>
    /// Makes the second window, or, when the player has been moved into another window (the side window), hands it
    /// to that one: it follows whichever window the player is in.
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

            // Laid over the video at its size: its content takes HandPeg's UI scale (5.1-Polish), its size never. A new window is cut to nothing yet.
            Services.UiScale.SetKeepsItsSize(made, true);
            _clip = Int32Rect.Empty;
            made.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(made).Handle;
                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExNoActivate | WsExToolWindow));
            };

            // Closed some other way than by this layer (with a window that owned it): the next one is made afresh.
            made.Closed += (_, _) =>
            {
                made.Content = null;
                if (ReferenceEquals(_overlayWindow, made))
                    _overlayWindow = null;
            };
        }

        // Owned by the window the player is in, so it stays above that one (and goes when the app goes).
        _overlayWindow.Owner = owner;
        owner.LocationChanged += Owner_Moved;
        owner.SizeChanged += Owner_Moved;
        owner.StateChanged += Owner_Moved;
        owner.Closing += Owner_Closing;
        owner.Closed += Owner_Closed;
        _placed = default;
        PlaceOverlay();
        KeepUnderSideWindow();
    }

    private void Owner_Moved(object? sender, EventArgs e) => PlaceOverlay();

    // A window closes the windows it owns: the side window lets go of this one first, as the player is handed back.
    private void Owner_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (e.Cancel || _overlayWindow is not { } overlay || !ReferenceEquals(sender, _owner) || ReferenceEquals(sender, Application.Current?.MainWindow))
            return;

        overlay.Hide();
        overlay.Owner = null;
    }

    // The side window closing with the player in it only hands the player back; the main window closing ends the app.
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
    /// The second window and the side window are both owned by the main window (while the player is in the main
    /// window), and between two windows with one owner the one shown or raised last is on top: shown again (after any
    /// rearrangement of the panes), this one would cover the side window wherever that is. It is put back directly
    /// under the side window each time.
    /// </summary>
    private void KeepUnderSideWindow()
    {
        if (_overlayWindow is not { } overlay || _owner is not { } owner)
            return;

        foreach (Window other in owner.OwnedWindows)
        {
            if (other is Docking.SideWindow { IsVisible: true } side && new WindowInteropHelper(side).Handle is var sideHandle && sideHandle != IntPtr.Zero)
            {
                SetWindowPos(new WindowInteropHelper(overlay).Handle, sideHandle, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                return;
            }
        }
    }

    /// <summary>
    /// Puts the second window exactly over the native one, or hides it when that one cannot be seen. It is cut to the client
    /// area of the window it lies on (5.2-AuditB): a pane laid out larger than its window never has it reach out past that
    /// window's edge, nor (maximized) over the strip kept clear of a taskbar that hides itself (MainWindow.TitleBar.cs), where
    /// the pointer brings the taskbar up and the Show Desktop corner is.
    /// </summary>
    private void PlaceOverlay()
    {
        if (_overlayWindow is not { } overlay || _owner is not { } owner)
            return;

        if (!IsVisible || owner.WindowState == WindowState.Minimized || !owner.IsVisible || PresentationSource.FromVisual(this) is null
            || ActualWidth < 1 || ActualHeight < 1)
        {
            HideOverlay(overlay);
            return;
        }

        // In real pixels, which is what the screen and SetWindowPos count in whatever the scaling.
        var corner = PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        var place = new Int32Rect((int)Math.Round(corner.X), (int)Math.Round(corner.Y),
            (int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        // The part of it inside the owner's client area; none of it there, it is not shown. Restored, not over the owner's resize band
        // either (5.3-Stage2: the window is drawn edge to edge), where the pointer is the frame's (WndProc).
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
            SetWindowPos(handle, IntPtr.Zero, place.X, place.Y, place.Width, place.Height, SwpNoZOrder | SwpNoActivate);
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

    /// <summary>A window's client area on the screen, in real pixels; null when it has no handle yet.</summary>
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

    /// <summary>Shows only <paramref name="clip"/> of a window (its own pixels; empty: all of it), and takes the pointer only there.</summary>
    private static void CutTo(IntPtr window, Int32Rect clip)
    {
        if (window == IntPtr.Zero)
            return;
        if (clip.IsEmpty)
        {
            SetWindowRgn(window, IntPtr.Zero, true);
            return;
        }

        // Windows owns the region once it is the window's; not taken, it is let go here.
        var region = CreateRectRgn(clip.X, clip.Y, clip.X + clip.Width, clip.Y + clip.Height);
        if (region != IntPtr.Zero && SetWindowRgn(window, region, true) == 0)
            DeleteObject(region);
    }
}
