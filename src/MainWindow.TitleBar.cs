using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using HandPegApp.Services;
using HandPegApp.Shell;

namespace HandPegApp;

// ----- The title bar (Pass 4.1B; the status bar it stood in for retired in 5.4.1-M5) -----
// The window draws its own title bar (WindowChrome, MainWindow.xaml): HandPeg's name, and the caption buttons, the
// Maximize button answering Windows as its own (HTMAXBUTTON), so Windows 11's Snap Layouts open from it. The status text
// sits dimmed in the middle of the title bar, the work under way is a dim fill across the title bar's whole height as far
// as it has got, and resting on the status opens the work flyout; a newer HandPeg is offered at its right end. The title bar
// is their one home (5.4.2A, the user's call: 5.4.1-M5 had put a second progress chip and the update notice on the transport
// row). Nothing here polls: the work summary's one timer drives it.
public partial class MainWindow
{
    /// <summary>The title bar and the status bar as the settings have them; before the first frame (Cold-Start rule 1), and on every change.</summary>
    private void InitializeTitleBar()
    {
        _startStatus = _viewModel.StatusText;
        UpdateTitleStatus();
        WorkScheduler.Summary.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkSummary.IsVisible) or nameof(WorkSummary.Title) or nameof(WorkSummary.Percent)
                or nameof(WorkSummary.IsIndeterminate) or nameof(WorkSummary.PercentText) or nameof(WorkSummary.MoreText))
                UpdateTitleStatus();
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(ViewModels.MainViewModel.StatusText))
                return;
            _startStatus = null;
            UpdateTitleStatus();
        };
        TitleBar.SizeChanged += (_, _) =>
        {
            UpdateTitleProgress();
            FitTitleStatus();
        };
        TitleText.SizeChanged += (_, _) => FitTitleStatus();
        TitleRight.SizeChanged += (_, _) => FitTitleStatus();
        ProjectTabStrip.SizeChanged += (_, _) => FitTitleStatus();

        // The project tabs (5.4A): one tab, made in memory, at the first frame; nothing read from the disk before it.
        InitializeProjectTabs();
        DependencyPropertyDescriptor.FromProperty(TitleProperty, typeof(Window)).AddValueChanged(this, (_, _) => TitleText.Text = Title);
        // Inside WM_SIZE: a new place for the window waits until that message is done (WPF lays it out from that message's size).
        StateChanged += (_, _) => ApplyWindowState(placeLater: true);

        // Made maximized (a size that fills the screen, Remember Last Size: WPF makes its window maximized), Windows placed it
        // before its hook was there: kept short of a taskbar that hides itself and inset as soon as it has its handle, before
        // its first layout, and again once shown. On a monitor of another DPI its frame is another size.
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TitleBarHook);
            ApplyWindowState();
        };
        Loaded += (_, _) => ApplyWindowState();
        DpiChanged += (_, _) => ApplyWindowState();

        // Maximized and moved to another monitor (Win+Shift+Arrow) it stays maximized: its inset is worked out again for where it is
        // now, once Windows has placed it (one beside a taskbar that hides itself is inset nowhere; one without reaches past every edge).
        LocationChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                Dispatcher.BeginInvoke(() => ApplyWindowState(), System.Windows.Threading.DispatcherPriority.Background);
        };
        TitleText.Text = Title;
        ApplyWindowState();
        CompositorClient.EngineRecovered += ms => Dispatcher.BeginInvoke(() => ShowEngineRecovered(ms));
    }

    private System.Windows.Threading.DispatcherTimer? _engineBadgeTimer;

    /// <summary>
    /// The GPU compositor fell over (a TDR, a driver fault) and its standby took over (CompositorClient, 5.0-Core): a dimmed
    /// note in the title bar for six seconds, how long it took in its tooltip. Never a dialog; nothing for the user to do.
    /// </summary>
    private void ShowEngineRecovered(double milliseconds)
    {
        EngineRecoveredBadge.ToolTip = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"The preview's graphics engine stopped (a driver reset or fault) and was restarted in {milliseconds:0} ms. Nothing was lost.");
        EngineRecoveredBadge.Visibility = Visibility.Visible;
        _engineBadgeTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(6), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { EngineRecoveredBadge.Visibility = Visibility.Collapsed; _engineBadgeTimer!.Stop(); }, Dispatcher);
        _engineBadgeTimer.Stop();
        _engineBadgeTimer.Start();
    }

    /// <summary>
    /// The work under way as a title bar says it ("Encoding clip.mp4  42%  +2 more"): the main window's, and the Settings
    /// screen's, which mirrors it (4.4), so the two always read the same.
    /// </summary>
    internal static string TitleWorkText(WorkSummary work)
    {
        // MoreText carries its own "+" ("+6"): the title said "++6" until 5.3-Stage3.
        var more = work.MoreText.Length > 0 ? work.MoreText + " more" : "";
        return string.Join("  ", new[] { work.Title, work.PercentText, more }.Where(p => p.Length > 0));
    }

    // The status the start itself said (a missing tool, from the view model's constructor), until anything else is said: never in the
    // title bar's middle, which is empty at start (5.3-Stage3, the user's call); the status bar and the gear's badge keep saying it.
    private string? _startStatus;

    /// <summary>
    /// The middle of the title bar: the work under way ("Encoding clip.mp4 42%, 2 more"), or else the last status; with neither
    /// (at start, until the user does something, something is loaded or work begins: 5.3-Stage3, the user's call) nothing is there at all.
    /// </summary>
    private void UpdateTitleStatus()
    {
        var work = WorkScheduler.Summary;
        var status = _startStatus is null ? _viewModel.StatusText ?? "" : "";
        TitleStatus.Visibility = work.IsVisible || status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TitleStatusText.Text = work.IsVisible ? TitleWorkText(work) : status;
        TitleStatus.ToolTip = work.IsVisible ? "Work in progress: rest the pointer here to see it all, pause it or cancel it." : status.Length > 0 ? status : null;
        TitleProgress.Visibility = work.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleProgress();
        PlaceTitleStatus();
    }

    // The status stays in the middle of the bar, between what is at its two ends, never under them (5.3-Stage3: a long one ran from
    // the window's name to under the caption buttons; the Settings screen's FitTitleStatus already held it). Since 5.4A the project tabs
    // after the name count as the left end, and the tabs themselves share the room between the name and the buttons (FitProjectTabs).
    // 5.4C: tabs reaching the middle push the status right instead of hiding it (TitleStatusPlace).
    private void FitTitleStatus()
    {
        PlaceTitleStatus();
        FitProjectTabs(TitleBar.ActualWidth - TitleNameWidth - TitleRight.ActualWidth);
    }

    private double TitleNameWidth => TitleText.ActualWidth + TitleText.Margin.Left + TitleText.Margin.Right;

    // The status where it fits (5.4C): its whole text measured as one line, then laid between the ends by TitleStatusPlace.
    private void PlaceTitleStatus()
    {
        TitleStatusText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wanted = TitleStatusText.DesiredSize.Width + TitleStatus.Padding.Left + TitleStatus.Padding.Right;
        var (left, width) = TitleStatusPlace(TitleBar.ActualWidth, TitleNameWidth + ProjectTabStrip.ActualWidth, TitleRight.ActualWidth, wanted);
        TitleStatus.Margin = new Thickness(left, 0, 0, 0);
        TitleStatus.MaxWidth = width;
    }

    /// <summary>The narrowest the status is shown, in DIPs: less than this between the ends, and it hides rather than show an ellipsis.</summary>
    internal const double TitleStatusMinWidth = 64;

    /// <summary>
    /// Where the status lies on the bar and how wide it may be (5.4C, the user's call; pure: titlebar-tab-hittest): in the middle while
    /// it fits there; pushed right by the left end (the name and the tabs) as far as the right end (the caption buttons) lets it; trimmed
    /// only once it fills the whole gap between them; and hidden (width 0) when that gap is under <see cref="TitleStatusMinWidth"/>.
    /// </summary>
    internal static (double Left, double Width) TitleStatusPlace(double barWidth, double leftWidth, double rightWidth, double wanted)
    {
        var room = barWidth - leftWidth - rightWidth;
        if (room < TitleStatusMinWidth || wanted <= 0)
            return (Math.Max(0, leftWidth), 0);
        // Never Math.Clamp: with the status filling the gap its two bounds are the same number, and rounding can put the lower a hair
        // over the upper (which throws). The left end wins.
        var width = Math.Min(wanted, room);
        return (Math.Max(leftWidth, Math.Min((barWidth - width) / 2, barWidth - rightWidth - width)), width);
    }

    /// <summary>The dim fill and the rail at its foot: as far across as the work has got, or (work that cannot say) the whole bar, fainter.
    /// Both lie over the project tabs (5.4D: ZIndex 10, never hit), so the rail shows even where an opaque tab face hides the wash.</summary>
    private void UpdateTitleProgress()
    {
        if (TitleProgress.Visibility != Visibility.Visible)
            return;

        var work = WorkScheduler.Summary;
        TitleProgressFill.Width = work.IsIndeterminate ? TitleBar.ActualWidth : TitleBar.ActualWidth * Math.Max(0, Math.Min(work.Percent, 100)) / 100;
        TitleProgressFill.Opacity = work.IsIndeterminate ? TitleProgressFaint : TitleProgressStrength;
        TitleProgressRail.Width = TitleProgressFill.Width;
        TitleProgressRail.Opacity = work.IsIndeterminate ? TitleProgressRailFaint : 1;
    }

    // How strongly the accent colour lies over the title bar: every theme's fill is this wash, so its contrast and saturation against
    // the bar go with it (CLAUDE.md section 8, 5.4D: a 16% wash, half that for work that cannot say, now that the 2 DIP rail carries the
    // progress over the tabs; 0.175 / 0.0875 before).
    private const double TitleProgressStrength = 0.16, TitleProgressFaint = 0.08, TitleProgressRailFaint = 0.5;

    // ----- The caption buttons -----

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>
    /// Maximized, a window that draws its own frame reaches past the screen's edges by its resize border: the content is
    /// kept inside them; beside a taskbar that hides itself it is placed short of it instead (KeepClearOfHiddenTaskbarNow). The Maximize
    /// button shows Restore then, and Windows 11 squares the window's corners (ThemeManager.ApplyCorners, 5.3).
    /// </summary>
    private void ApplyWindowState() => ApplyWindowState(placeLater: false);

    private void ApplyWindowState(bool placeLater)
    {
        var maximized = WindowState == WindowState.Maximized;
        if (maximized)
            WindowFrame.KeepClearOfHiddenTaskbarNow(new WindowInteropHelper(this).Handle, placeLater);
        ThemeManager.ApplyCorners(this);
        ChromeRoot.Margin = maximized ? WindowFrame.MaximizedFrameInset(this) : new Thickness(0);
        MaximizeButton.Content = maximized ? Glyphs.ChromeRestore : Glyphs.ChromeMaximize;
        MaximizeButton.ToolTip = maximized ? "Restore Down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    // Whether the pointer is pressed on the Maximize button (through the frame's own messages).
    private bool _maximizePressed;

    /// <summary>
    /// Over the Maximize button the window answers HTMAXBUTTON, which is what opens Windows 11's Snap Layouts; the
    /// button's hover and press then arrive as the frame's own messages, and are shown and acted on here. Everything else goes
    /// to the hook every window that draws its own frame has (FrameHook, 5.3): the frame hit-tested in real pixels
    /// (FrameHitTest, never by WindowChrome), and no classic caption or frame ever painted.
    /// </summary>
    private IntPtr TitleBarHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WindowFrame.WmNcHitTest when IsOverMaximize(lParam):
                handled = true;
                return WindowFrame.HtMaxButton;

            // A taskbar set to hide itself or not to (the working area changes): Windows places the window anew, and its
            // content is inset for where it is now, once it has.
            case WindowFrame.WmSettingChange when wParam == WindowFrame.SpiSetWorkArea:
                Dispatcher.BeginInvoke(() => ApplyWindowState(), System.Windows.Threading.DispatcherPriority.Background);
                break;

            // The pointer over the frame: shown on the Maximize button, the message still going on to Windows (Snap Layouts).
            case WindowFrame.WmNcMouseMove:
                MaximizeButton.Tag = wParam == WindowFrame.HtMaxButton ? _maximizePressed ? "pressed" : "hover" : null;
                break;
            case WindowFrame.WmNcMouseLeave:
                (MaximizeButton.Tag, _maximizePressed) = (null, false);
                break;
            case WindowFrame.WmNcLButtonDown when wParam == WindowFrame.HtMaxButton:
                (_maximizePressed, MaximizeButton.Tag, handled) = (true, "pressed", true);
                return IntPtr.Zero;
            case WindowFrame.WmNcLButtonUp when wParam == WindowFrame.HtMaxButton:
                if (_maximizePressed)
                    MaximizeWindow_Click(MaximizeButton, new RoutedEventArgs());
                (_maximizePressed, MaximizeButton.Tag, handled) = (false, null, true);
                return IntPtr.Zero;
        }

        return WindowFrame.FrameHook(hwnd, message, wParam, lParam, ref handled);
    }

    /// <summary>
    /// Whether a point of the screen (WM_NCHITTEST's, real pixels) is on the Maximize button: its bounds where it is really
    /// drawn (PointToScreen goes through HandPeg's UI scale and the monitor's DPI), so they never reach over Close or Minimize.
    /// </summary>
    private bool IsOverMaximize(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible || PresentationSource.FromVisual(MaximizeButton) is null)
            return false;

        var (x, y) = WindowFrame.PointOf(lParam);
        var corner = MaximizeButton.PointToScreen(new Point(0, 0));
        var far = MaximizeButton.PointToScreen(new Point(MaximizeButton.ActualWidth, MaximizeButton.ActualHeight));
        return x >= Math.Min(corner.X, far.X) && x < Math.Max(corner.X, far.X) && y >= Math.Min(corner.Y, far.Y) && y < Math.Max(corner.Y, far.Y);
    }
}
