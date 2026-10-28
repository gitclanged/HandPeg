using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The title bar, and the status bar that can hide (Pass 4.1B) -----
// The window draws its own title bar (WindowChrome, MainWindow.xaml): HandPeg's name, and the caption buttons, the
// Maximize button answering Windows as its own (HTMAXBUTTON), so Windows 11's Snap Layouts open from it. The status bar
// at the bottom can be hidden for each mode (its right-click menu, or Tabs and Panes: AppSettings.ShowStatusBar);
// hidden, its status text sits dimmed in the middle of the title bar, the work under way is a dim fill across the
// title bar's whole height as far as it has got, resting on the status opens the work flyout as the progress chip
// does, and an update waiting is offered there too. Nothing here polls: the work summary's one timer drives it.
public partial class MainWindow
{
    /// <summary>The title bar and the status bar as the settings have them; before the first frame (Cold-Start rule 1), and on every change.</summary>
    private void InitializeTitleBar()
    {
        ApplyStatusBar();
        WorkScheduler.Summary.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkSummary.IsVisible) or nameof(WorkSummary.Title) or nameof(WorkSummary.Percent)
                or nameof(WorkSummary.IsIndeterminate) or nameof(WorkSummary.PercentText) or nameof(WorkSummary.MoreText))
                UpdateTitleStatus();
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModels.MainViewModel.StatusText))
                UpdateTitleStatus();
        };
        TitleBar.SizeChanged += (_, _) => UpdateTitleProgress();
        DependencyPropertyDescriptor.FromProperty(TitleProperty, typeof(Window)).AddValueChanged(this, (_, _) => TitleText.Text = Title);
        DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement)).AddValueChanged(UpdateNotice, (_, _) => ApplyStatusBar());
        StateChanged += (_, _) => ApplyWindowState();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TitleBarHook);
        TitleText.Text = Title;
        ApplyWindowState();
    }

    private static bool StatusBarShown => AppSettings.Current.ShowStatusBar;

    /// <summary>The status bar, or the status in the title bar: whichever the mode in use has.</summary>
    private void ApplyStatusBar()
    {
        // The title bar's status and its progress fill are there with the status bar shown or hidden (4.1.1); only the
        // update button moves between them.
        var shown = StatusBarShown;
        StatusBar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        TitleStatus.Visibility = Visibility.Visible;
        TitleUpdateButton.Visibility = !shown && UpdateNotice.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        TitleUpdateButton.Content = UpdateActionButton.Content;
        TitleUpdateButton.ToolTip = UpdateNoticeText.Text;
        UpdateTitleStatus();
    }

    /// <summary>Hides or shows the status bar in the mode in use (its right-click menu, the title bar's, Tabs and Panes).</summary>
    private void SetStatusBarShown(bool shown)
    {
        _viewModel.SavePaneLayout(s => s.ShowStatusBar = shown);
        ApplyStatusBar();
    }

    private void HideStatusBar_Click(object sender, RoutedEventArgs e) => SetStatusBarShown(false);

    private void ShowStatusBar_Click(object sender, RoutedEventArgs e) => SetStatusBarShown(true);

    /// <summary>The middle of the title bar: the work under way ("Encoding clip.mp4 42%, 2 more"), or else the last status.</summary>
    private void UpdateTitleStatus()
    {
        var work = WorkScheduler.Summary;
        TitleStatusText.Text = work.IsVisible
            ? string.Join("  ", new[] { work.Title, work.PercentText, work.MoreText.Length > 0 ? "+" + work.MoreText : "" }.Where(p => p.Length > 0))
            : _viewModel.StatusText;
        TitleStatus.ToolTip = work.IsVisible ? "Work in progress: rest the pointer here to see it all, pause it or cancel it." : _viewModel.StatusText;
        TitleProgress.Visibility = work.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleProgress();
    }

    /// <summary>The dim fill: as far across as the work has got, or (work that cannot say) the whole bar, fainter.</summary>
    private void UpdateTitleProgress()
    {
        if (TitleProgress.Visibility != Visibility.Visible)
            return;

        var work = WorkScheduler.Summary;
        TitleProgressFill.Width = work.IsIndeterminate ? TitleBar.ActualWidth : TitleBar.ActualWidth * Math.Clamp(work.Percent, 0, 100) / 100;
        TitleProgressFill.Opacity = work.IsIndeterminate ? 0.07 : 0.14;
    }

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
    /// kept inside them. The Maximize button shows Restore then.
    /// </summary>
    private void ApplyWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (maximized)
        {
            // The frame's size at this window's DPI: the sizing border and the padding Windows adds to it.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var at = (uint)Math.Round(96 * dpi.DpiScaleX);
            var across = GetSystemMetricsForDpi(SmCxSizeFrame, at) + GetSystemMetricsForDpi(SmCxPaddedBorder, at);
            var down = GetSystemMetricsForDpi(SmCySizeFrame, at) + GetSystemMetricsForDpi(SmCxPaddedBorder, at);
            var margin = new Thickness(across / dpi.DpiScaleX, down / dpi.DpiScaleY, across / dpi.DpiScaleX, down / dpi.DpiScaleY);

            // Beside a taskbar that hides itself the window stops a pixel short of the screen's edge (OnWindowPosChanging),
            // so nothing of it reaches past that edge to be kept in.
            var handle = new WindowInteropHelper(this).Handle;
            switch (handle == IntPtr.Zero ? null : AutoHideEdge(MonitorFromWindow(handle, MonitorDefaultToNearest)))
            {
                case AbeLeft: margin.Left = 0; break;
                case AbeTop: margin.Top = 0; break;
                case AbeRight: margin.Right = 0; break;
                case AbeBottom: margin.Bottom = 0; break;
            }

            ChromeRoot.Margin = margin;
        }
        else
        {
            ChromeRoot.Margin = new Thickness(0);
        }

        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore Down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private const int SmCxSizeFrame = 32, SmCySizeFrame = 33, SmCxPaddedBorder = 92;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    // ----- A taskbar that hides itself, and the monitor's working area (Pass 4.2B-2) -----
    // Maximized, the window reaches past every edge of its monitor by its resize border. When the monitor's taskbar
    // hides itself (the working area is then the whole monitor), Windows takes such a window for a full-screen one and
    // keeps the taskbar hidden: moving the pointer to that edge no longer brings it up. So, maximized beside a taskbar
    // that hides, the window stops one pixel short of that edge; the pointer reaching it wakes the taskbar.

    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002;
    private const uint MonitorDefaultToNearest = 2;
    private const uint AbmGetState = 4, AbmGetAutoHideBarEx = 0x0B, AbsAutoHide = 1;
    private const int AbeLeft = 0, AbeTop = 1, AbeRight = 2, AbeBottom = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public IntPtr Window;
        public uint CallbackMessage, Edge;
        public NativeRect Rect;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromRect(in NativeRect rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr window);

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    private static bool TryMonitorInfo(IntPtr monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    /// <summary>The edge of a monitor (AbeLeft ... AbeBottom) that has a taskbar which hides itself, or null.</summary>
    private static int? AutoHideEdge(IntPtr monitor)
    {
        var state = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        if ((SHAppBarMessage(AbmGetState, ref state) & AbsAutoHide) == 0 || !TryMonitorInfo(monitor, out var info))
            return null;

        for (var edge = AbeLeft; edge <= AbeBottom; edge++)
        {
            var bar = new AppBarData { Size = Marshal.SizeOf<AppBarData>(), Edge = (uint)edge, Rect = info.Monitor };
            if (SHAppBarMessage(AbmGetAutoHideBarEx, ref bar) != 0)
                return edge;
        }

        return null;
    }

    /// <summary>The working area of the monitor a window is on (or nearest to), in that window's DIPs; empty when unknown.</summary>
    private Rect WorkAreaOf(IntPtr window)
    {
        if (window == IntPtr.Zero || !TryMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), out var info))
            return Rect.Empty;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var work = info.Work;
        return new Rect(work.Left / dpi.DpiScaleX, work.Top / dpi.DpiScaleY, (work.Right - work.Left) / dpi.DpiScaleX, (work.Bottom - work.Top) / dpi.DpiScaleY);
    }

    /// <summary>Maximized beside a taskbar that hides itself, the window's place is made to stop a pixel short of that edge.</summary>
    private static void OnWindowPosChanging(IntPtr window, IntPtr place)
    {
        var pos = Marshal.PtrToStructure<WindowPos>(place);
        if ((pos.Flags & (SwpNoSize | SwpNoMove)) != 0 || !IsZoomed(window))
            return;

        var rect = new NativeRect { Left = pos.X, Top = pos.Y, Right = pos.X + pos.Width, Bottom = pos.Y + pos.Height };
        var monitorHandle = MonitorFromRect(rect, MonitorDefaultToNearest);
        if (AutoHideEdge(monitorHandle) is not { } edge || !TryMonitorInfo(monitorHandle, out var info))
            return;

        var monitor = info.Monitor;
        switch (edge)
        {
            case AbeLeft when rect.Left < monitor.Left + 1: rect.Left = monitor.Left + 1; break;
            case AbeTop when rect.Top < monitor.Top + 1: rect.Top = monitor.Top + 1; break;
            case AbeRight when rect.Right > monitor.Right - 1: rect.Right = monitor.Right - 1; break;
            case AbeBottom when rect.Bottom > monitor.Bottom - 1: rect.Bottom = monitor.Bottom - 1; break;
            default: return;
        }

        (pos.X, pos.Y, pos.Width, pos.Height) = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        Marshal.StructureToPtr(pos, place, false);
    }

    // ----- Snap Layouts: the Maximize button as Windows' own -----

    private const int WmNcHitTest = 0x0084, WmNcMouseMove = 0x00A0, WmNcMouseLeave = 0x02A2, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2;
    private const int HtMaxButton = 9;

    // Whether the pointer is pressed on the Maximize button (through the frame's own messages).
    private bool _maximizePressed;

    /// <summary>
    /// Over the Maximize button the window answers HTMAXBUTTON, which is what opens Windows 11's Snap Layouts; the
    /// button's hover and press then arrive as the frame's own messages, and are shown and acted on here.
    /// </summary>
    private IntPtr TitleBarHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmWindowPosChanging:
                OnWindowPosChanging(hwnd, lParam);
                break;
            case WmNcHitTest when IsOverMaximize(lParam):
                handled = true;
                return HtMaxButton;
            case WmNcMouseMove:
                MaximizeButton.Tag = wParam == HtMaxButton ? _maximizePressed ? "pressed" : "hover" : null;
                break;
            case WmNcMouseLeave:
                (MaximizeButton.Tag, _maximizePressed) = (null, false);
                break;
            case WmNcLButtonDown when wParam == HtMaxButton:
                (_maximizePressed, MaximizeButton.Tag, handled) = (true, "pressed", true);
                break;
            case WmNcLButtonUp when wParam == HtMaxButton:
                if (_maximizePressed)
                    MaximizeWindow_Click(MaximizeButton, new RoutedEventArgs());
                (_maximizePressed, MaximizeButton.Tag, handled) = (false, null, true);
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximize(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible || PresentationSource.FromVisual(MaximizeButton) is null)
            return false;

        var value = lParam.ToInt64();
        var point = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        var corner = MaximizeButton.PointToScreen(new Point(0, 0));
        var far = MaximizeButton.PointToScreen(new Point(MaximizeButton.ActualWidth, MaximizeButton.ActualHeight));
        return point.X >= corner.X && point.X < far.X && point.Y >= corner.Y && point.Y < far.Y;
    }
}
