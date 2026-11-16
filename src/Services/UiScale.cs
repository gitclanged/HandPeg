using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using HandPegApp.Native;
using static HandPegApp.Native.User32;

namespace HandPegApp.Services;

/// <summary>
/// Application UI scaling independent of Windows DPI scaling (unless <see cref="AppSettings.FollowWindowsDpiScaling"/>). Content uses
/// <see cref="BasePixelsPerDip"/>: 1:1 physical pixels on desktop monitors, ~1920 DIP wide on high-PPI screens (2880 x 1800 at 200 % gives
/// 1.5 px/DIP). Implemented as an inverse LayoutTransform on the window root times <see cref="AppSettings.UiScalePercent"/> (75-150 %),
/// with layout rounding.
/// <list type="bullet">
/// <item>Window frames keep Windows sizing; XAML-sized dialogs shrink by the same factor and re-centre on their owner. Windows sized by
/// what they cover (editor, settings, overlay, dock windows) opt out via <see cref="SetKeepsItsSize"/>.</item>
/// <item>WindowChrome caption height matches the drawn title bar.</item>
/// <item>Root DPI (<see cref="VisualTreeHelper.SetRootDpi"/>) equals content pixels per DIP, so layout rounds to monitor pixels, 1 px borders
/// stay crisp at 125/150 %, text renders at its true scale, and <see cref="VisualTreeHelper.GetDpi"/> is exact. Frame code uses
/// <see cref="MonitorScale"/>.</item>
/// <item>Applied before the editor's first layout (<see cref="BeforeFirstLayout"/>), on other windows' Loaded, on DpiChanged, and to all
/// open windows on settings save (<see cref="ApplyAll"/>).</item>
/// <item>Out-of-tree popups (tooltips, context menus, code popups via <see cref="Track"/>) are rescaled on open (<see cref="ScalePopup"/>);
/// in-tree popups inherit the window transform.</item>
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

    /// <summary>
    /// Window whose monitor determines this window's scale (null: its own). Overlays use their host's scale so they match it even when
    /// Windows reports another monitor (before placement or straddling).
    /// </summary>
    public static readonly DependencyProperty ScaleSourceProperty =
        DependencyProperty.RegisterAttached("ScaleSource", typeof(Window), typeof(UiScale), new PropertyMetadata(null));

    public static void SetScaleSource(Window window, Window? source) => window.SetValue(ScaleSourceProperty, source);

    public static Window? GetScaleSource(Window window) => (Window?)window.GetValue(ScaleSourceProperty);

    /// <summary>The smallest and largest UI Scale the settings allow, in percent.</summary>
    public const int LeastPercent = 75, MostPercent = 150;

    private static bool _following;

    /// <summary>Scales every window as it is loaded, and again on a monitor of another DPI (App.OnStartup).</summary>
    public static void Follow()
    {
        // Once: the goldens' window tests each ask for it, and a class handler registered twice runs twice.
        if (_following)
            return;

        _following = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(static (sender, _) =>
        {
            if (sender is Window window)
                Apply(window);
        }));

        // Raised on monitor change and by SetRootDpi itself; query the monitor scale rather than trusting the event. WPF is still
        // propagating the monitor DPI, so the root DPI is reapplied afterwards.
        EventManager.RegisterClassHandler(typeof(Window), Window.DpiChangedEvent, new DpiChangedEventHandler(static (sender, _) =>
        {
            if (sender is Window window)
                window.Dispatcher.BeginInvoke(() => Apply(window), System.Windows.Threading.DispatcherPriority.Send);
        }));

        // Hook Opened for all tooltips and context menus (Loaded only fires on elements with their own handlers, so a class handler
        // would miss popup roots).
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

    /// <summary>Registers an out-of-tree code popup for rescaling on every open.</summary>
    public static void Track(Popup popup) => popup.Opened += static (sender, _) =>
    {
        if (sender is Popup { Child: FrameworkElement child } popup)
            ScalePopup(child, popup.PlacementTarget);
    };

    /// <summary>
    /// Places a popup's top-left at (<paramref name="x"/>, <paramref name="y"/>) DIPs from <paramref name="target"/>'s top-left, as
    /// PlacementMode.Relative intends. Relative is mirrored when SystemParameters.MenuDropAlignment is right-handed (touch devices), shifting
    /// the popup a full width left; a custom device-pixel placement is never mirrored.
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

    // ----- Flyout placement against the opening button -----

    /// <summary>Which side of its button a flyout opens on, and which of its edges lines up with the button's.</summary>
    public enum FlyoutSide
    {
        /// <summary>Below the button, right edges aligned (header flyouts at the window's right).</summary>
        BelowEnd,

        /// <summary>Under the button, its left edge at the button's.</summary>
        BelowStart,

        /// <summary>Over the button, its left edge at the button's (the Views menu, at the transport row's left).</summary>
        AboveStart,

        /// <summary>At the button's left, their tops level (the header standing as a strip at the window's right).</summary>
        Left,

        /// <summary>At the button's right, their tops level (the header standing as a strip at the window's left).</summary>
        Right,
    }

    // Button and containment rectangles (target device pixels) captured at first placement after open and held until close, plus the
    // button's device pixels per DIP.
    private sealed class Anchored
    {
        public FrameworkElement Button = null!;
        public UIElement Target = null!;
        public FrameworkElement? Within;
        public FlyoutSide Side;
        public double Gap, Inset;
        public (Rect Button, Rect? Room, double PerDip)? Taken;
    }

    private static readonly DependencyProperty AnchoredProperty =
        DependencyProperty.RegisterAttached("Anchored", typeof(Anchored), typeof(UiScale), new PropertyMetadata(null));

    /// <summary>
    /// Places a flyout <paramref name="gap"/> DIPs from its button using a custom device-pixel placement (immune to MenuDropAlignment
    /// mirroring of Relative/Bottom/Top and to UI Scale). The button rectangle is captured once at open, relative to a pane-stable target
    /// (<paramref name="within"/>, else the window content), so dock rebuilds under an open flyout (layout preview) never move it.
    /// With <paramref name="within"/> it is clamped inside that element at least <paramref name="inset"/> DIPs from its left; Windows keeps
    /// it on screen.
    /// </summary>
    public static void PlaceAgainst(Popup popup, FrameworkElement button, FlyoutSide side, double gap, FrameworkElement? within = null, double inset = 0)
    {
        if (popup.GetValue(AnchoredProperty) is not Anchored state)
        {
            state = new Anchored();
            popup.SetValue(AnchoredProperty, state);
            popup.Closed += static (sender, _) =>
            {
                if (sender is Popup closed && closed.GetValue(AnchoredProperty) is Anchored anchored)
                    anchored.Taken = null;
            };
            popup.CustomPopupPlacementCallback = (popupSize, _, _) => [new CustomPopupPlacement(Placed(state, popupSize), PopupPrimaryAxis.None)];
        }

        var target = within ?? (Window.GetWindow(button)?.Content as UIElement) ?? button;
        (state.Button, state.Target, state.Within, state.Side, state.Gap, state.Inset, state.Taken) = (button, target, within, side, gap, inset, null);
        popup.PlacementTarget = target;
        (popup.Placement, popup.HorizontalOffset, popup.VerticalOffset) = (PlacementMode.Custom, 0, 0);

        // A new place alone does not move a popup that is open: a change of offset does.
        if (popup.IsOpen)
        {
            popup.VerticalOffset = 0.01;
            popup.VerticalOffset = 0;
        }
    }

    // Flyout top-left (target device pixels) from the button rectangle captured at open.
    private static Point Placed(Anchored state, Size popupSize)
    {
        state.Taken ??= Take(state);
        if (state.Taken is not { } taken)
            return new Point(0, 0);

        var room = taken.Room is { } within ? within with { X = within.X + state.Inset * taken.PerDip, Width = Math.Max(0, within.Width - state.Inset * taken.PerDip) } : (Rect?)null;
        return FlyoutCorner(state.Side, taken.Button, popupSize, state.Gap * taken.PerDip, room);
    }

    // Button (and same-window containment) rectangles relative to the target's top-left; null while detached.
    private static (Rect Button, Rect? Room, double PerDip)? Take(Anchored state)
    {
        var (button, target) = (state.Button, state.Target);
        if (PresentationSource.FromVisual(button) is null || PresentationSource.FromVisual(target) is null || button.ActualWidth <= 0)
            return null;

        var origin = target.PointToScreen(new Point(0, 0));
        Rect On(FrameworkElement element)
        {
            var from = element.PointToScreen(new Point(0, 0)) - origin;
            var to = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)) - origin;
            return new Rect(new Point(from.X, from.Y), new Point(to.X, to.Y));
        }

        var rect = On(button);
        var within = state.Within is { } room && PresentationSource.FromVisual(room) is { } source && ReferenceEquals(source, PresentationSource.FromVisual(button))
            ? On(room) : (Rect?)null;
        return (rect, within, rect.Width / button.ActualWidth);
    }

    /// <summary>
    /// Top-left for a <paramref name="popup"/>-sized flyout <paramref name="gap"/> from the button (device pixels, common origin), clamped
    /// to <paramref name="room"/> when given.
    /// </summary>
    internal static Point FlyoutCorner(FlyoutSide side, Rect button, Size popup, double gap, Rect? room)
    {
        var (x, y) = side switch
        {
            FlyoutSide.BelowEnd => (button.Right - popup.Width, button.Bottom + gap),
            FlyoutSide.BelowStart => (button.Left, button.Bottom + gap),
            FlyoutSide.AboveStart => (button.Left, button.Top - gap - popup.Height),
            FlyoutSide.Left => (button.Left - gap - popup.Width, button.Top),
            _ => (button.Right + gap, button.Top),
        };
        if (room is { } within)
        {
            x = Math.Max(within.Left, Math.Min(x, within.Right - popup.Width));
            y = Math.Max(within.Top, Math.Min(y, within.Bottom - popup.Height));
        }

        return new Point(Math.Round(x), Math.Round(y));
    }

    /// <summary>
    /// Scales a window before its first layout (editor constructor), hooking source creation
    /// (<see cref="PresentationSource.AddSourceChangedHandler"/>), the only point between HWND creation and the first measure.
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

    /// <summary>Monitor DPI scale for a window (1.5 at 150 %), used for frame metrics regardless of root DPI.</summary>
    public static double MonitorScale(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var dpi = handle == 0 ? 0 : GetDpiForWindow(handle);
        return dpi > 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(window).DpiScaleX;
    }

    /// <summary>Longer side (px) of the window's monitor; 0 when unknown.</summary>
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

    /// <summary>The settings were saved: every window open takes the scale they make now.</summary>
    public static void ApplyAll()
    {
        if (Application.Current is not { } app)
            return;
        foreach (Window window in app.Windows)
            Apply(window);
    }

    /// <summary>
    /// Target workspace width (DIP) on high-PPI screens, and the high-PPI criteria: Windows scale >= 150 % with a long side over 2560 px.
    /// </summary>
    public const double HighPpiWorkspaceDips = 1920, HighPpiScale = 1.5;
    public const int HighPpiPixels = 2560;

    /// <summary>
    /// Base pixels per DIP for <paramref name="dpiScale"/> and long side <paramref name="monitorPixels"/> (0 unknown), before UI Scale.
    /// Following Windows: <paramref name="dpiScale"/>. Desktop monitors (&lt;= 125 %, or &lt;= 150 % at &lt;= 2560 px): 1. High-PPI: enough for
    /// ~<see cref="HighPpiWorkspaceDips"/> DIP width, capped at <paramref name="dpiScale"/> (2880 x 1800 at 200 %: 1.5; 3840 x 2160 at 200 %:
    /// 2; at 150 %: 1.5).
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

    /// <summary>Effective content pixels per DIP: <see cref="BasePixelsPerDip"/> x UI Scale (75-150 %).</summary>
    public static double PixelsPerDip(double dpiScale, int monitorPixels)
    {
        var settings = AppSettings.Current;
        var percent = Math.Clamp(settings.UiScalePercent is > 0 and var set ? set : 100, LeastPercent, MostPercent) / 100.0;
        return BasePixelsPerDip(dpiScale, monitorPixels) * percent;
    }

    /// <summary>
    /// Content LayoutTransform scale: <see cref="PixelsPerDip"/> / <paramref name="dpiScale"/> for a <paramref name="monitorPixels"/>-wide monitor.
    /// </summary>
    public static double For(double dpiScale, int monitorPixels)
    {
        var monitor = double.IsFinite(dpiScale) && dpiScale > 1.001 ? dpiScale : 1;
        return PixelsPerDip(dpiScale, monitorPixels) / monitor;
    }

    /// <summary>Applies the computed scale and matching root DPI to a window (no-op when unchanged).</summary>
    public static void Apply(Window window)
    {
        try
        {
            if (window.Content is not FrameworkElement root)
                return;

            var monitor = MonitorScale(window);
            var source = GetScaleSource(window) is { } owner && new WindowInteropHelper(owner).Handle != 0 ? owner : window;
            var pixelsPerDip = PixelsPerDip(MonitorScale(source), MonitorPixels(source));
            var scale = pixelsPerDip / (double.IsFinite(monitor) && monitor > 1.001 ? monitor : 1);
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
    /// Out-of-tree popups are separate HWNDs at monitor DPI, so they would render at monitor scale rather than the app's (oversized next
    /// to a 1:1 editor). On open, applies the owner's (<paramref name="target"/>, else editor) root DPI to the popup root and the
    /// monitor-to-owner LayoutTransform to <paramref name="content"/> (not the root, whose size drives the popup HWND size). WPF re-measures
    /// before first render.
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

    /// <summary>Invalidates layout under a root so rounding is redone at the new DPI.</summary>
    private static void MeasureAgain(DependencyObject element)
    {
        if (element is UIElement shown)
            shown.InvalidateMeasure();
        for (var i = VisualTreeHelper.GetChildrenCount(element) - 1; i >= 0; i--)
            MeasureAgain(VisualTreeHelper.GetChild(element, i));
    }

    /// <summary>
    /// Sets the WindowChrome caption height to the drawn title bar (XAML height scaled, window DIPs at monitor scale). WindowChrome would
    /// hit-test using the overridden root DPI, so custom-chrome windows hit-test in physical pixels (FrameHitTest) with this height.
    /// A new WindowChrome is assigned only when values change, since each assignment recomputes the frame (SWP_FRAMECHANGED).
    /// </summary>
    private static void ScaleCaption(Window window, double scale)
    {
        if (WindowChrome.GetWindowChrome(window) is not { } chrome)
            return;

        var designed = (double)window.GetValue(DesignedCaptionProperty);
        if (double.IsNaN(designed))
            window.SetValue(DesignedCaptionProperty, designed = chrome.CaptionHeight);

        var copy = (WindowChrome)chrome.Clone();
        copy.CaptionHeight = designed * scale;
        if (SameFrame(chrome, copy))
            return;

        WindowChrome.SetWindowChrome(window, copy);
        if (DevFlightRecorder.IsEnabled)
            DevFlightRecorder.Record("frame", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{window.GetType().Name}: a new WindowChrome, its caption {copy.CaptionHeight:0.##} DIPs (WPF works the frame out again)"));

        // Everything of a WindowChrome that shapes the frame WPF works out (a caption within a hundredth of a DIP).
        static bool SameFrame(WindowChrome had, WindowChrome wanted) => Math.Abs(had.CaptionHeight - wanted.CaptionHeight) < 0.01
            && had.ResizeBorderThickness == wanted.ResizeBorderThickness && had.GlassFrameThickness == wanted.GlassFrameThickness
            && had.CornerRadius == wanted.CornerRadius && had.UseAeroCaptionButtons == wanted.UseAeroCaptionButtons
            && had.NonClientFrameEdges == wanted.NonClientFrameEdges;
    }

    /// <summary>Scales a dialog's XAML size by a factor and re-centres it on its owner.</summary>
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
