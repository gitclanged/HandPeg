using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Where the video is drawn: a native child window for the player (mpv) to draw into, and, lying exactly over
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
    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000, SsBlackRect = 0x4;
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;
    private const uint SwpNoZOrder = 0x4, SwpNoActivate = 0x10;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    private Window? _owner;
    private Window? _overlayWindow;
    private Int32Rect _placed;

    public VideoSurface()
    {
        Loaded += (_, _) => CreateOverlay();
        LayoutUpdated += (_, _) => PlaceOverlay();
        IsVisibleChanged += (_, _) => PlaceOverlay();
    }

    /// <summary>What is shown on top of the video. Set once, in XAML, as this layer's content.</summary>
    public UIElement? Overlay { get; set; }

    /// <summary>The native window the player draws into; zero until it has been created.</summary>
    public IntPtr SurfaceHandle { get; private set; }

    /// <summary>Raised when <see cref="SurfaceHandle"/> has been created.</summary>
    public event Action? SurfaceReady;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // A plain black rectangle until a player draws in it.
        SurfaceHandle = CreateWindowEx(
            0, "static", "", WsChild | WsVisible | WsClipChildren | WsClipSiblings | SsBlackRect, 0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.BeginInvoke(() => SurfaceReady?.Invoke());
        return new HandleRef(this, SurfaceHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        SurfaceHandle = IntPtr.Zero;
        DestroyWindow(hwnd.Handle);
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

    /// <summary>Puts the second window exactly over the native one, or hides it when that one cannot be seen.</summary>
    private void PlaceOverlay()
    {
        if (_overlayWindow is not { } overlay || _owner is not { } owner)
            return;

        if (!IsVisible || owner.WindowState == WindowState.Minimized || !owner.IsVisible || PresentationSource.FromVisual(this) is null
            || ActualWidth < 1 || ActualHeight < 1)
        {
            if (overlay.IsVisible)
                overlay.Hide();
            _placed = default;
            return;
        }

        // In real pixels, which is what the screen and SetWindowPos count in whatever the scaling.
        var corner = PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        var place = new Int32Rect((int)Math.Round(corner.X), (int)Math.Round(corner.Y),
            (int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        if (!overlay.IsVisible)
        {
            overlay.Show();
            _placed = default;
        }

        if (place == _placed)
            return;

        _placed = place;
        SetWindowPos(new WindowInteropHelper(overlay).Handle, IntPtr.Zero, place.X, place.Y, place.Width, place.Height, SwpNoZOrder | SwpNoActivate);
    }
}
