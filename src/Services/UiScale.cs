using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace HandPegApp.Services;

/// <summary>
/// HandPeg's own UI scale (5.1-Polish, the user's directive: Windows' high-DPI scaling takes room an editor needs). Unless the
/// settings follow Windows' display scaling (<see cref="AppSettings.FollowWindowsDpiScaling"/>, off by default), every
/// window's content is laid out at a scale of HandPeg's own (<see cref="BasePixelsPerDip"/>, screen-aware since 5.2): 1:1 at
/// the monitor's own pixels on a desktop monitor (at 150 % in Windows, half as much room again), and about 1920 DIPs across on
/// a high-PPI screen (a 2880 x 1800 laptop at 200 %: 1.5 pixels a DIP, not a microscopic 1:1). It is an inverse
/// LayoutTransform on the window's root, times the UI Scale (<see cref="AppSettings.UiScalePercent"/>, 75 to 150 %), with
/// layout rounding.
/// <list type="bullet">
/// <item>A window's own size stays Windows' (its frame, its saved place), but a dialog sized by its XAML is made as much
/// smaller (once, and again if its scale changes), so it holds its content as designed, centred on its owner again.
/// Windows sized by what they cover (the editor, the settings screen over it, the video's overlay, the dock's windows) say so
/// with <see cref="SetKeepsItsSize"/>.</item>
/// <item>A title bar drawn under WindowChrome gets a caption as tall as it is drawn.</item>
/// <item>The window's root DPI (5.1-AutoHud-Part1, the user's Q3: <see cref="VisualTreeHelper.SetRootDpi"/>) is what one of its
/// content's DIPs is in real pixels: the UI Scale, on its own, unless Windows' display scaling is followed. WPF then rounds
/// layout to whole pixels of the monitor, 1 px borders land on one pixel at 125 % and 150 %, text is laid out for what it
/// is drawn at, and <see cref="VisualTreeHelper.GetDpi"/> within a window is exactly its content's pixels per DIP. What
/// truly wants the monitor's DPI (a window's own frame) asks <see cref="MonitorScale"/>.</item>
/// <item>The editor is scaled before its first layout (<see cref="BeforeFirstLayout"/>), every other window as it is
/// loaded, a window moved to a monitor of another DPI again (Window.DpiChanged), and every window open when the settings
/// are saved (<see cref="ApplyAll"/>).</item>
/// <item>A popup outside every window's tree (a tooltip, a context menu, a popup HandPeg makes in code: <see cref="Track"/>)
/// is drawn at its window's pixels per DIP as it opens (5.2, <see cref="ScalePopup"/>). One inside a window's tree (a
/// ComboBox's list, a menu's submenu, the flyouts of MainWindow.xaml) takes its window's transform from WPF.</item>
/// </list>
/// </summary>
public static partial class UiScale
{
    /// <summary>The scale a window was given (NaN: none yet).</summary>
    private static readonly DependencyProperty AppliedProperty =
        DependencyProperty.RegisterAttached("Applied", typeof(double), typeof(UiScale), new PropertyMetadata(double.NaN));

    /// <summary>The caption height its WindowChrome was made with, before any scale.</summary>
    private static readonly DependencyProperty DesignedCaptionProperty =
        DependencyProperty.RegisterAttached("DesignedCaption", typeof(double), typeof(UiScale), new PropertyMetadata(double.NaN));

    /// <summary>A window sized by what it covers or is laid over: its content is scaled, its size never.</summary>
    public static readonly DependencyProperty KeepsItsSizeProperty =
        DependencyProperty.RegisterAttached("KeepsItsSize", typeof(bool), typeof(UiScale), new PropertyMetadata(false));

    public static void SetKeepsItsSize(Window window, bool value) => window.SetValue(KeepsItsSizeProperty, value);

    public static bool GetKeepsItsSize(Window window) => (bool)window.GetValue(KeepsItsSizeProperty);

    /// <summary>The smallest and largest UI Scale the settings allow, in percent.</summary>
    public const int LeastPercent = 75, MostPercent = 150;

    /// <summary>Scales every window as it is loaded, and again on a monitor of another DPI (App.OnStartup).</summary>
    public static void Follow()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(static (sender, _) =>
        {
            if (sender is Window window)
                Apply(window);
        }));

        // Raised by a monitor's change, and by SetRootDpi itself: the monitor's own scale is asked for, never the event's. A
        // change of monitor is still walking the tree with the monitor's DPI: the root DPI is put back once it has finished.
        EventManager.RegisterClassHandler(typeof(Window), Window.DpiChangedEvent, new DpiChangedEventHandler(static (sender, _) =>
        {
            if (sender is Window window)
                window.Dispatcher.BeginInvoke(() => Apply(window), System.Windows.Threading.DispatcherPriority.Send);
        }));

        // Every tooltip and context menu as it opens (both raise Opened whether or not anything listens; Loaded is raised only
        // on elements with handlers of their own, so a class handler for it never sees a popup's root).
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(static (sender, _) =>
        {
            if (sender is ToolTip tip)
                ScalePopup(tip, tip.PlacementTarget);
        }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(static (sender, _) =>
        {
            if (sender is ContextMenu menu)
                ScalePopup(menu, menu.PlacementTarget);
        }));
    }

    /// <summary>A popup HandPeg makes in code, outside every window's tree: drawn at its window's pixels per DIP each time it opens.</summary>
    public static void Track(Popup popup) => popup.Opened += static (sender, _) =>
    {
        if (sender is Popup { Child: FrameworkElement child } popup)
            ScalePopup(child, popup.PlacementTarget);
    };

    /// <summary>
    /// Places a popup's top left corner <paramref name="x"/>, <paramref name="y"/> of <paramref name="target"/>'s own DIPs from
    /// the target's top left: what PlacementMode.Relative is meant to do (5.2). Relative is mirrored on a PC whose menus open to
    /// the left of the hand (a touch screen set right-handed, SystemParameters.MenuDropAlignment: the user's touch-screen laptop), where it
    /// puts the popup's top right corner there instead, a whole popup's width to the left (the tour's ring beside what it rings,
    /// the dial beside the pointer, X-Ray off the window). A custom placement, in the target's device pixels, is never mirrored.
    /// </summary>
    public static void PlaceRelative(Popup popup, UIElement target, double x, double y)
    {
        popup.PlacementTarget = target;
        popup.CustomPopupPlacementCallback = (_, targetSize, _) =>
        {
            var size = target.RenderSize;
            var (perX, perY) = (size.Width > 0 ? targetSize.Width / size.Width : 1, size.Height > 0 ? targetSize.Height / size.Height : 1);
            return [new CustomPopupPlacement(new Point(x * perX, y * perY), PopupPrimaryAxis.None)];
        };

        (popup.Placement, popup.HorizontalOffset, popup.VerticalOffset) = (PlacementMode.Custom, 0, 0);

        // A new callback alone does not move a popup that is open: a change of offset does.
        if (popup.IsOpen)
        {
            popup.VerticalOffset = 0.01;
            popup.VerticalOffset = 0;
        }
    }

    /// <summary>
    /// A window scaled before its first layout (the editor's constructor): its first measure already comes after the window's
    /// handle is made and its root is set, the one moment before it (<see cref="PresentationSource.AddSourceChangedHandler"/>).
    /// </summary>
    public static void BeforeFirstLayout(Window window)
    {
        SourceChangedEventHandler? first = null;
        first = (_, e) =>
        {
            if (e.NewSource is null)
                return;
            PresentationSource.RemoveSourceChangedHandler(window, first);
            Apply(window);
        };
        PresentationSource.AddSourceChangedHandler(window, first);
    }

    /// <summary>The scale of the monitor a window is on (1.5 at Windows' 150 %): what the window's own frame is measured in, whatever its root DPI says.</summary>
    public static double MonitorScale(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var dpi = handle == 0 ? 0 : GetDpiForWindow(handle);
        return dpi > 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(window).DpiScaleX;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    /// <summary>The longer side, in pixels, of the monitor a window is on (its width when it stands landscape); 0 when it is not known.</summary>
    public static int MonitorPixels(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = handle == 0 ? 0 : MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return 0;
        return Math.Max(info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRect Monitor, Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    /// <summary>The settings were saved: every window open takes the scale they make now.</summary>
    public static void ApplyAll()
    {
        if (Application.Current is not { } app)
            return;
        foreach (Window window in app.Windows)
            Apply(window);
    }

    /// <summary>
    /// The workspace HandPeg keeps on a high-PPI screen, in DIPs across; and what makes a screen high-PPI rather than a desktop
    /// monitor: Windows scaling it to 150 % or more, with more than 2560 pixels on its longer side (5.2, the user's Windows on ARM laptop).
    /// </summary>
    public const double HighPpiWorkspaceDips = 1920, HighPpiScale = 1.5;
    public const int HighPpiPixels = 2560;

    /// <summary>
    /// What one of a window's content DIPs is in real pixels on a monitor of <paramref name="dpiScale"/> (2 at Windows' 200 %)
    /// whose longer side is <paramref name="monitorPixels"/> pixels (0: not known), before the UI Scale. Windows' own scale when
    /// it is followed. Otherwise 1 on a desktop monitor (scaled to 125 % or less, or to 150 % or less with no more than 2560
    /// pixels: a 1080p or 1440p screen), so HandPeg has the monitor's every pixel; and on a high-PPI screen (150 % or more, over
    /// 2560 pixels) as many pixels as keep the workspace about <see cref="HighPpiWorkspaceDips"/> DIPs across, never more than
    /// Windows' own scale: 1.5 on 2880 x 1800 at 200 % (not 1, which made the editor microscopic), 2 on 3840 x 2160 at 200 %,
    /// 1.5 on 3840 x 2160 at 150 %.
    /// </summary>
    public static double BasePixelsPerDip(double dpiScale, int monitorPixels)
    {
        if (!double.IsFinite(dpiScale) || !(dpiScale > 1.001))
            return 1;
        if (AppSettings.Current.FollowWindowsDpiScaling)
            return dpiScale;

        var desktop = dpiScale <= 1.25 + 1e-3 || (monitorPixels <= HighPpiPixels && dpiScale <= HighPpiScale + 1e-3);
        if (desktop || dpiScale < HighPpiScale - 1e-3 || monitorPixels < HighPpiPixels)
            return 1;
        return Math.Clamp(monitorPixels / HighPpiWorkspaceDips, 1, dpiScale);
    }

    /// <summary><see cref="BasePixelsPerDip"/> times the UI Scale (75 to 150 %): what one of a window's content DIPs is in real pixels.</summary>
    public static double PixelsPerDip(double dpiScale, int monitorPixels)
    {
        var settings = AppSettings.Current;
        var percent = Math.Clamp(settings.UiScalePercent is > 0 and var set ? set : 100, LeastPercent, MostPercent) / 100.0;
        return BasePixelsPerDip(dpiScale, monitorPixels) * percent;
    }

    /// <summary>
    /// The scale of a window's content on a monitor of <paramref name="dpiScale"/> (1.5 at Windows' 150 %) and
    /// <paramref name="monitorPixels"/> pixels: its <see cref="PixelsPerDip"/> over the monitor's own scale.
    /// </summary>
    public static double For(double dpiScale, int monitorPixels)
    {
        var monitor = double.IsFinite(dpiScale) && dpiScale > 1.001 ? dpiScale : 1;
        return PixelsPerDip(dpiScale, monitorPixels) / monitor;
    }

    /// <summary>Gives a window the scale its monitor and the settings make, and the root DPI that goes with it (nothing when it has them already).</summary>
    public static void Apply(Window window)
    {
        try
        {
            if (window.Content is not FrameworkElement root)
                return;

            var monitor = MonitorScale(window);
            var scale = For(monitor, MonitorPixels(window));
            var rootDpiChanged = SetRootDpi(window, scale * monitor);
            var applied = (double)window.GetValue(AppliedProperty);
            if (applied == scale)
            {
                if (rootDpiChanged && window.IsLoaded)
                    MeasureAgain(root);
                return;
            }

            var was = double.IsNaN(applied) ? 1 : applied;
            window.SetValue(AppliedProperty, scale);
            root.UseLayoutRounding = true;
            if (Math.Abs(scale - 1) < 1e-4)
            {
                root.ClearValue(FrameworkElement.LayoutTransformProperty);
            }
            else
            {
                var transform = new ScaleTransform(scale, scale);
                transform.Freeze();
                root.LayoutTransform = transform;
            }

            ScaleCaption(window, scale);
            if (!GetKeepsItsSize(window) && Math.Abs(scale / was - 1) > 1e-4)
                ScaleSize(window, scale / was);
            if (rootDpiChanged && window.IsLoaded)
                MeasureAgain(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A scale is a help, never a reason to fail: the window is drawn as Windows scales it.
            AppLog.Write("A window could not be given HandPeg's UI scale", ex);
        }
    }

    /// <summary>
    /// A popup outside every window's tree (a tooltip, a context menu, one made in code) is a window of its own at its
    /// monitor's DPI, so WPF draws it at the monitor's scale, not its window's (5.2, the user's bug: at 200 % beside an editor
    /// drawn 1:1, tooltips came out several times the size of everything round them). As it opens, its content takes the pixels
    /// per DIP of the window it belongs to (that of <paramref name="target"/>, else the editor): that window's root DPI on the
    /// popup's root, and the LayoutTransform from the monitor's scale to it on <paramref name="content"/>, the popup's child. Not
    /// on its root: the popup's window is sized from its root's own size, which the root's LayoutTransform does not change. WPF
    /// sizes and places the popup again before it is first drawn.
    /// </summary>
    private static void ScalePopup(FrameworkElement content, UIElement? target)
    {
        try
        {
            if (PresentationSource.FromVisual(content) is not HwndSource { RootVisual: Visual root } source)
                return;
            var window = (target is null ? null : Window.GetWindow(target)) ?? Application.Current?.MainWindow;
            if (window is null)
                return;

            var pixelsPerDip = VisualTreeHelper.GetDpi(window).DpiScaleX;
            var dpi = GetDpiForWindow(source.Handle);
            var scale = pixelsPerDip / (dpi > 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(root).DpiScaleX);
            if (Math.Abs(VisualTreeHelper.GetDpi(root).DpiScaleX - pixelsPerDip) >= 1e-4)
                VisualTreeHelper.SetRootDpi(root, new DpiScale(pixelsPerDip, pixelsPerDip));

            if (Math.Abs(scale - 1) < 1e-4)
            {
                content.ClearValue(FrameworkElement.LayoutTransformProperty);
            }
            else if (content.LayoutTransform is not ScaleTransform { IsFrozen: true } had || Math.Abs(had.ScaleX - scale) >= 1e-4)
            {
                var transform = new ScaleTransform(scale, scale);
                transform.Freeze();
                content.LayoutTransform = transform;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AppLog.Write("A popup could not be given HandPeg's UI scale", ex);
        }
    }

    /// <summary>Says what one of a window's DIPs is in real pixels; whether that changed anything.</summary>
    private static bool SetRootDpi(Window window, double pixelsPerDip)
    {
        if (Math.Abs(VisualTreeHelper.GetDpi(window).DpiScaleX - pixelsPerDip) < 1e-4)
            return false;

        VisualTreeHelper.SetRootDpi(window, new DpiScale(pixelsPerDip, pixelsPerDip));
        return true;
    }

    /// <summary>Everything under a root laid out again: a window already laid out rounded its layout at the DPI it had (a dialog is scaled as it is loaded).</summary>
    private static void MeasureAgain(DependencyObject element)
    {
        if (element is UIElement shown)
            shown.InvalidateMeasure();
        for (var i = VisualTreeHelper.GetChildrenCount(element) - 1; i >= 0; i--)
            MeasureAgain(VisualTreeHelper.GetChild(element, i));
    }

    /// <summary>
    /// The caption WindowChrome hit-tests as tall as the title bar drawn (its XAML height, scaled): in the window's own DIPs,
    /// at the monitor's scale. WindowChrome itself would measure the pointer by the root DPI set here (SetRootDpi), a third off
    /// at 200 % on a 2880 x 1800 screen: the windows that draw their own title bar hit-test their frame in real pixels instead
    /// (MainWindow.FrameHitTest, 5.2-AuditB), with this height.
    /// </summary>
    private static void ScaleCaption(Window window, double scale)
    {
        if (WindowChrome.GetWindowChrome(window) is not { } chrome)
            return;

        var designed = (double)window.GetValue(DesignedCaptionProperty);
        if (double.IsNaN(designed))
            window.SetValue(DesignedCaptionProperty, designed = chrome.CaptionHeight);

        var wanted = designed * scale;
        if (Math.Abs(chrome.CaptionHeight - wanted) < 0.01)
            return;
        var copy = (WindowChrome)chrome.Clone();
        copy.CaptionHeight = wanted;
        WindowChrome.SetWindowChrome(window, copy);
    }

    /// <summary>A dialog's own size (what its XAML gives it) by a factor, its content's size following it; centred on its owner again.</summary>
    private static void ScaleSize(Window window, double factor)
    {
        var (oldWidth, oldHeight) = (window.ActualWidth, window.ActualHeight);
        var sizesWidth = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        var sizesHeight = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;

        if (!sizesWidth)
        {
            if (double.IsFinite(window.Width))
                window.Width *= factor;
            window.MinWidth *= factor;
            if (double.IsFinite(window.MaxWidth))
                window.MaxWidth *= factor;
        }

        if (!sizesHeight)
        {
            if (double.IsFinite(window.Height))
                window.Height *= factor;
            window.MinHeight *= factor;
            if (double.IsFinite(window.MaxHeight))
                window.MaxHeight *= factor;
        }

        // Where it was centred, it is centred again (on its owner, or where it stood).
        if (window.WindowStartupLocation is WindowStartupLocation.CenterOwner or WindowStartupLocation.CenterScreen && oldWidth > 0 && oldHeight > 0)
        {
            var (newWidth, newHeight) = (sizesWidth ? oldWidth * factor : window.Width, sizesHeight ? oldHeight * factor : window.Height);
            if (double.IsFinite(newWidth) && double.IsFinite(window.Left))
                window.Left += (oldWidth - newWidth) / 2;
            if (double.IsFinite(newHeight) && double.IsFinite(window.Top))
                window.Top += (oldHeight - newHeight) / 2;
        }
    }
}
