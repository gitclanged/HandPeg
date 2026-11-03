using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace HandPegApp.Services;

/// <summary>
/// HandPeg's own UI scale (5.1-Polish, the user's directive: Windows' high-DPI scaling takes room an editor needs). Unless the
/// settings follow Windows' display scaling (<see cref="AppSettings.FollowWindowsDpiScaling"/>, off by default), every
/// window's content is laid out at its monitor's own pixels: an inverse LayoutTransform (96 over the monitor's DPI) on its
/// root, times the UI Scale (<see cref="AppSettings.UiScalePercent"/>, 75 to 150 %), with layout rounding. At 150 % in
/// Windows, HandPeg is then drawn 1:1, with half as much room again. Popups (menus, tooltips, flyouts, the dial) take their
/// placement target's scale from WPF.
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

    /// <summary>The settings were saved: every window open takes the scale they make now.</summary>
    public static void ApplyAll()
    {
        if (Application.Current is not { } app)
            return;
        foreach (Window window in app.Windows)
            Apply(window);
    }

    /// <summary>
    /// The scale of a window's content on a monitor of <paramref name="dpiScale"/> (1.5 at Windows' 150 %): the UI Scale,
    /// over the monitor's own scale unless Windows' display scaling is followed.
    /// </summary>
    public static double For(double dpiScale)
    {
        var settings = AppSettings.Current;
        var percent = Math.Clamp(settings.UiScalePercent is > 0 and var set ? set : 100, LeastPercent, MostPercent) / 100.0;
        var windows = settings.FollowWindowsDpiScaling || !(dpiScale > 1.001) || !double.IsFinite(dpiScale) ? 1 : dpiScale;
        return percent / windows;
    }

    /// <summary>Gives a window the scale its monitor and the settings make, and the root DPI that goes with it (nothing when it has them already).</summary>
    public static void Apply(Window window)
    {
        try
        {
            if (window.Content is not FrameworkElement root)
                return;

            var monitor = MonitorScale(window);
            var scale = For(monitor);
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

    /// <summary>The caption WindowChrome hit-tests as tall as the title bar drawn (its XAML height, scaled).</summary>
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
