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
/// cannot simply be placed over it; it is the content of this element instead, and is shown in that second
/// window, which follows the first wherever it goes.
///
/// The second window never takes the focus: a click on it reaches its content, and the keyboard stays with
/// the main window. Where it is fully transparent, clicks fall through to what is underneath.
/// </summary>
[ContentProperty(nameof(Overlay))]
public sealed class VideoSurface : HwndHost
{
    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000, SsBlackRect = 0x4;
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;
    private const uint SwpNoZOrder = 0x4, SwpNoActivate = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    private Window? _owner;
    private Window? _overlayWindow;
    private Int32Rect _placed;

    public VideoSurface()
    {
        Loaded += (_, _) => CreateOverlay();
        LayoutUpdated += (_, _) => PlaceOverlay();
        IsVisibleChanged += (_, _) => PlaceOverlay();
    }

    /// <summary>What is shown on top of the video. Set once, in XAML, as this element's content.</summary>
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

    private void CreateOverlay()
    {
        if (_overlayWindow is not null || Overlay is null || Window.GetWindow(this) is not { } owner)
            return;

        _owner = owner;
        _overlayWindow = new Window
        {
            Owner = owner,
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
        _overlayWindow.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(_overlayWindow).Handle;
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExNoActivate | WsExToolWindow));
        };

        owner.LocationChanged += (_, _) => PlaceOverlay();
        owner.SizeChanged += (_, _) => PlaceOverlay();
        owner.StateChanged += (_, _) => PlaceOverlay();
        owner.Closed += (_, _) => _overlayWindow?.Close();
        PlaceOverlay();
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
