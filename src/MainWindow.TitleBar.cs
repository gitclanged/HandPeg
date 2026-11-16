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

// ----- Custom title bar -----
// WindowChrome title bar (MainWindow.xaml): app name and caption buttons, with Maximize answering HTMAXBUTTON so Windows 11 Snap
// Layouts work. Dimmed status text sits centred; running work is a full-height accent wash proportional to progress; hovering the
// status opens the work flyout; updates are offered at the right end. The title bar is the single home for status, progress and
// update notices. No polling: driven by the work summary's timer.
public partial class MainWindow
{
    /// <summary>Initializes title bar status, progress and window-state hooks before Frame 1 (Law 1) and wires change handlers.</summary>
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

        // Project tabs: one in-memory tab at Frame 1; no disk reads before it.
        InitializeProjectTabs();
        DependencyPropertyDescriptor.FromProperty(TitleProperty, typeof(Window)).AddValueChanged(this, (_, _) => TitleText.Text = Title);
        // StateChanged fires inside WM_SIZE: defer repositioning until that message completes (WPF lays out from its size).
        StateChanged += (_, _) => ApplyWindowState(placeLater: true);

        // Created maximized (Remember Last Size with a screen-filling size), Windows placed it before the hook existed: apply the
        // auto-hide taskbar gap and inset at SourceInitialized (before first layout), on Loaded, and on DPI change (frame size varies).
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TitleBarHook);
            ApplyWindowState();
        };
        Loaded += (_, _) => ApplyWindowState();
        DpiChanged += (_, _) => ApplyWindowState();

        // Maximized move to another monitor (Win+Shift+Arrow): recompute the inset after placement (none beside an auto-hide taskbar;
        // otherwise the frame overhangs every edge).
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
    /// Compositor recovered from a fault (TDR or driver error) via its standby (CompositorClient): shows a dimmed title bar badge for 6 s
    /// with the recovery time in its tooltip. Never a dialog; no action needed.
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
    /// Title bar work text ("Encoding clip.mp4  42%  +2 more"), shared with the Settings window's mirror so both read identically.
    /// </summary>
    internal static string TitleWorkText(WorkSummary work)
    {
        // MoreText already includes its "+" ("+6"); do not add another.
        var more = work.MoreText.Length > 0 ? work.MoreText + " more" : "";
        return string.Join("  ", new[] { work.Title, work.PercentText, more }.Where(p => p.Length > 0));
    }

    // Start-up status (e.g. a missing tool, from the view model constructor) until anything newer arrives; never shown centred in the
    // title bar, which starts empty (other indicators such as the gear badge carry it).
    private string? _startStatus;

    /// <summary>
    /// Centre title text: running work ("Encoding clip.mp4 42%, 2 more"), else the latest status; empty at start until there is activity,
    /// a load, or work.
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

    // Keeps the status between the bar's ends, never under them. Project tabs after the name count as the left end and share the room
    // between name and caption buttons (FitProjectTabs); tabs reaching the centre push the status right instead of hiding it
    // (TitleStatusPlace).
    private void FitTitleStatus()
    {
        PlaceTitleStatus();
        FitProjectTabs(TitleBar.ActualWidth - TitleNameWidth - TitleRight.ActualWidth);
    }

    private double TitleNameWidth => TitleText.ActualWidth + TitleText.Margin.Left + TitleText.Margin.Right;

    // Measures the status as one line and positions it via TitleStatusPlace.
    private void PlaceTitleStatus()
    {
        TitleStatusText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wanted = TitleStatusText.DesiredSize.Width + TitleStatus.Padding.Left + TitleStatus.Padding.Right;
        var (left, width) = TitleStatusPlace(TitleBar.ActualWidth, TitleNameWidth + ProjectTabStrip.ActualWidth, TitleRight.ActualWidth, wanted);
        TitleStatus.Margin = new Thickness(left, 0, 0, 0);
        TitleStatus.MaxWidth = width;
    }

    /// <summary>Minimum status width in DIPs; below this gap it hides rather than showing only an ellipsis.</summary>
    internal const double TitleStatusMinWidth = 64;

    /// <summary>
    /// Pure status placement (golden: titlebar-tab-hittest): centred when it fits; pushed right by the left end (name and tabs) up to the
    /// caption buttons; trimmed only when filling the whole gap; hidden (width 0) when the gap is under <see cref="TitleStatusMinWidth"/>.
    /// </summary>
    internal static (double Left, double Width) TitleStatusPlace(double barWidth, double leftWidth, double rightWidth, double wanted)
    {
        var room = barWidth - leftWidth - rightWidth;
        if (room < TitleStatusMinWidth || wanted <= 0)
            return (Math.Max(0, leftWidth), 0);
        // Avoid Math.Clamp: when the status fills the gap the bounds coincide and rounding can invert them (throws). Left end wins.
        var width = Math.Min(wanted, room);
        return (Math.Max(leftWidth, Math.Min((barWidth - width) / 2, barWidth - rightWidth - width)), width);
    }

    /// <summary>Sizes the progress wash and its bottom rail to the work percentage (full width and fainter when indeterminate). Both sit
    /// above the project tabs (ZIndex 10, not hit-testable) so the rail stays visible over opaque tab faces.</summary>
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

    // Accent wash opacity over the title bar for every theme: 16% determinate, 8% indeterminate; the 2 DIP rail carries the progress
    // over the tabs (rail at 50% when indeterminate).
    private const double TitleProgressStrength = 0.16, TitleProgressFaint = 0.08, TitleProgressRailFaint = 0.5;

    // ----- Caption buttons -----

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
    /// Maximized custom-frame windows overhang the screen by the resize border, so content is inset (MaximizedFrameInset); beside an
    /// auto-hide taskbar the window is placed short of it (KeepClearOfHiddenTaskbarNow). Updates the Maximize/Restore glyph and corners.
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

    // Maximize pressed state, tracked from non-client messages.
    private bool _maximizePressed;

    /// <summary>
    /// Returns HTMAXBUTTON over Maximize (enables Windows 11 Snap Layouts); its hover/press then arrive as non-client messages handled
    /// here. Everything else goes to WindowFrame.FrameHook (physical-pixel FrameHitTest, never WindowChrome; no classic NC painting).
    /// </summary>
    private IntPtr TitleBarHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WindowFrame.WmNcHitTest when IsOverMaximize(lParam):
                handled = true;
                return WindowFrame.HtMaxButton;

            // Taskbar auto-hide toggled (work area changed): Windows repositions the window; re-inset afterwards.
            case WindowFrame.WmSettingChange when wParam == WindowFrame.SpiSetWorkArea:
                Dispatcher.BeginInvoke(() => ApplyWindowState(), System.Windows.Threading.DispatcherPriority.Background);
                break;

            // NC hover: reflect on the Maximize button while passing the message on to Windows (Snap Layouts).
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
    /// True if the WM_NCHITTEST screen point (physical pixels) lies within Maximize's actual rendered bounds (PointToScreen applies UI
    /// scale and monitor DPI), so it never overlaps Close or Minimize.
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
