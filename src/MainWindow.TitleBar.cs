using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
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

    /// <summary>
    /// The work under way as a title bar says it ("Encoding clip.mp4  42%  +2 more"): the main window's, and the Settings
    /// screen's, which mirrors it (4.4), so the two always read the same.
    /// </summary>
    internal static string TitleWorkText(WorkSummary work)
    {
        var more = work.MoreText.Length > 0 ? "+" + work.MoreText : "";
        return string.Join("  ", new[] { work.Title, work.PercentText, more }.Where(p => p.Length > 0));
    }

    /// <summary>The middle of the title bar: the work under way ("Encoding clip.mp4 42%, 2 more"), or else the last status.</summary>
    private void UpdateTitleStatus()
    {
        var work = WorkScheduler.Summary;
        TitleStatusText.Text = work.IsVisible ? TitleWorkText(work) : _viewModel.StatusText;
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
        TitleProgressFill.Opacity = work.IsIndeterminate ? TitleProgressFaint : TitleProgressStrength;
    }

    // How strongly the accent colour lies over the title bar: every theme's fill is this wash, so its contrast and saturation against
    // the bar go with it (the user's call: a quarter stronger than the 0.14 and 0.07 of before, on all five themes).
    private const double TitleProgressStrength = 0.175, TitleProgressFaint = 0.0875;

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
    /// kept inside them, and the client area short of a taskbar that hides itself (KeepClearOfHiddenTaskbarNow). The Maximize
    /// button shows Restore then.
    /// </summary>
    private void ApplyWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (maximized)
            KeepClearOfHiddenTaskbarNow(new WindowInteropHelper(this).Handle);
        ChromeRoot.Margin = maximized ? MaximizedFrameInset(this) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore Down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    /// <summary>
    /// How far a maximized window that draws its own frame reaches past its screen's edges, in its DIPs: what its content
    /// is kept inside. The main window's, and the Settings screen's, which draws the same title bar (4.4).
    /// </summary>
    internal static Thickness MaximizedFrameInset(Window window)
    {
        // Pixels over DIPs of the window itself (its frame, its margins): the monitor's scale, whatever HandPeg's UI scale
        // makes of its content (UiScale).
        var dpi = UiScale.MonitorScale(window);
        var handle = new WindowInteropHelper(window).Handle;

        // Maximized: as far as its client area (all of it the window draws) really reaches past its monitor's working area (by
        // its frame; at an edge it is kept short of, beside a taskbar that hides itself, not at all). Exact whatever the scale,
        // the taskbars and the monitor.
        if (handle != IntPtr.Zero && IsZoomed(handle) && ClientOnScreen(handle, out var rect)
            && TryMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), out var info))
        {
            var work = info.Work;
            return new Thickness(Math.Max(0, work.Left - rect.Left) / dpi, Math.Max(0, work.Top - rect.Top) / dpi,
                Math.Max(0, rect.Right - work.Right) / dpi, Math.Max(0, rect.Bottom - work.Bottom) / dpi);
        }

        // Not placed maximized yet (no window, or one made maximized before it is shown): the frame's size at the window's
        // DPI, the sizing border and the padding Windows adds to it.
        var at = (uint)Math.Round(96 * dpi);
        var across = GetSystemMetricsForDpi(SmCxSizeFrame, at) + GetSystemMetricsForDpi(SmCxPaddedBorder, at);
        var down = GetSystemMetricsForDpi(SmCySizeFrame, at) + GetSystemMetricsForDpi(SmCxPaddedBorder, at);
        var margin = new Thickness(across / dpi, down / dpi, across / dpi, down / dpi);

        // Beside a taskbar that hides itself the client area stops short of the screen's edge (OnNcCalcSize), so nothing of
        // it reaches past that edge to be kept in.
        var edges = handle == IntPtr.Zero ? 0 : AutoHideEdges(MonitorFromWindow(handle, MonitorDefaultToNearest));
        if ((edges & (1 << AbeLeft)) != 0)
            margin.Left = 0;
        if ((edges & (1 << AbeTop)) != 0)
            margin.Top = 0;
        if ((edges & (1 << AbeRight)) != 0)
            margin.Right = 0;
        if ((edges & (1 << AbeBottom)) != 0)
            margin.Bottom = 0;

        return margin;
    }

    private const int SmCxSizeFrame = 32, SmCySizeFrame = 33, SmCxPaddedBorder = 92;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    // ----- A taskbar that hides itself, and the monitor's working area (Pass 4.2B-2) -----
    // Maximized, the window reaches past every edge of its monitor by its resize border. When the monitor's taskbar
    // hides itself (the working area is then the whole monitor), Windows takes such a window (its client area, which with
    // WindowChrome is the whole window) for a full-screen one and keeps the taskbar hidden: moving the pointer to that edge
    // no longer brings it up, and the Show Desktop corner at its end cannot be reached. So, maximized beside a taskbar that
    // hides, the client area stops two pixels short of that edge (5.2-AuditB, the user's bug: one was not enough), whatever
    // the scale: WM_NCCALCSIZE's rectangles and the monitor's are real pixels. (Not the window's own rectangle: Windows puts a
    // maximized window back where it maximizes it, whatever WM_WINDOWPOSCHANGING or SetWindowPos ask, measured on a 2880 x 1800 laptop.)
    // Every edge of the window's own monitor with such a taskbar counts, not only the main monitor's. Over that strip the
    // window answers nowhere (FrameHitTest), and the video's overlay is kept inside the window's client area (VideoSurface),
    // so nothing of HandPeg's takes the pointer there.

    private const int WmNcCalcSize = 0x0083, WmSettingChange = 0x001A, SpiSetWorkArea = 0x002F;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020, SwpNoOwnerZOrder = 0x0200;
    private const uint MonitorDefaultToNearest = 2;
    private const uint AbmGetState = 4, AbmGetTaskbarPos = 5, AbmGetAutoHideBarEx = 0x0B, AbsAutoHide = 1;
    private const int AbeLeft = 0, AbeTop = 1, AbeRight = 2, AbeBottom = 3;

    // The pixels left uncovered along the edge of a taskbar that hides itself.
    private const int HiddenTaskbarStrip = 2;

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    private static partial int MapWindowPoints(IntPtr from, IntPtr to, ref NativeRect points, uint count);

    /// <summary>A window's client area on the screen, in real pixels (with WindowChrome, all of the window it draws).</summary>
    private static bool ClientOnScreen(IntPtr window, out NativeRect rect)
    {
        if (!GetClientRect(window, out rect))
            return false;
        MapWindowPoints(window, IntPtr.Zero, ref rect, 2);
        return true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    private static bool TryMonitorInfo(IntPtr monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    /// <summary>
    /// The edges of a monitor with a taskbar that hides itself, as bits (1 &lt;&lt; AbeLeft ... 1 &lt;&lt; AbeBottom); 0 when it
    /// has none. Each edge of that monitor is asked for its own (ABM_GETAUTOHIDEBAREX: the taskbars of other monitors are
    /// theirs); when Windows answers for none of them while its taskbar hides itself, the main taskbar's own edge stands in,
    /// if it is this monitor's.
    /// </summary>
    private static int AutoHideEdges(IntPtr monitor)
    {
        var state = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        if ((SHAppBarMessage(AbmGetState, ref state) & AbsAutoHide) == 0 || !TryMonitorInfo(monitor, out var info))
            return 0;

        var edges = 0;
        for (var edge = AbeLeft; edge <= AbeBottom; edge++)
        {
            var bar = new AppBarData { Size = Marshal.SizeOf<AppBarData>(), Edge = (uint)edge, Rect = info.Monitor };
            if (SHAppBarMessage(AbmGetAutoHideBarEx, ref bar) != 0)
                edges |= 1 << edge;
        }

        if (edges == 0)
        {
            var taskbar = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
            if (SHAppBarMessage(AbmGetTaskbarPos, ref taskbar) != 0 && taskbar.Edge <= AbeBottom
                && MonitorFromRect(taskbar.Rect, MonitorDefaultToNearest) == monitor)
                edges = 1 << (int)taskbar.Edge;
        }

        return edges;
    }

    /// <summary>The working area of the monitor a window is on (or nearest to), in that window's DIPs; empty when unknown.</summary>
    private Rect WorkAreaOf(IntPtr window)
    {
        if (window == IntPtr.Zero || !TryMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), out var info))
            return Rect.Empty;

        var dpi = UiScale.MonitorScale(this);
        var work = info.Work;
        return new Rect(work.Left / dpi, work.Top / dpi, (work.Right - work.Left) / dpi, (work.Bottom - work.Top) / dpi);
    }

    /// <summary>
    /// Maximized beside a taskbar that hides itself, the client area Windows is about to give the window (WM_NCCALCSIZE's first
    /// rectangle, the window's own as WindowChrome leaves it) is made to stop short of that edge: maximizing, moving to another
    /// monitor, the taskbar's setting changed, and the frame worked out anew for a window made maximized before this hook was
    /// there (KeepClearOfHiddenTaskbarNow). WindowChrome answers the message after this, with the rectangle as it is left
    /// (5.2-AuditB, the user's bug: HandPeg maximized kept the hidden taskbar from coming up).
    /// </summary>
    private static void OnNcCalcSize(IntPtr window, IntPtr lParam)
    {
        if (!IsZoomed(window))
            return;

        // Both of the message's forms start with that rectangle (NCCALCSIZE_PARAMS.rgrc[0], or the RECT itself).
        var client = Marshal.PtrToStructure<NativeRect>(lParam);
        if (KeepShortOfHiddenTaskbars(ref client))
            Marshal.StructureToPtr(client, lParam, false);
    }

    /// <summary>
    /// A maximized window's client area (real pixels) made to stop <see cref="HiddenTaskbarStrip"/> pixels short of each edge of
    /// its monitor that has a taskbar which hides itself, so it never covers the whole monitor; whether it had to be.
    /// </summary>
    private static bool KeepShortOfHiddenTaskbars(ref NativeRect rect)
    {
        var monitorHandle = MonitorFromRect(rect, MonitorDefaultToNearest);
        var edges = AutoHideEdges(monitorHandle);
        if (edges == 0 || !TryMonitorInfo(monitorHandle, out var info))
            return false;

        var monitor = info.Monitor;
        var moved = false;
        if ((edges & (1 << AbeLeft)) != 0 && rect.Left < monitor.Left + HiddenTaskbarStrip)
            (rect.Left, moved) = (monitor.Left + HiddenTaskbarStrip, true);
        if ((edges & (1 << AbeTop)) != 0 && rect.Top < monitor.Top + HiddenTaskbarStrip)
            (rect.Top, moved) = (monitor.Top + HiddenTaskbarStrip, true);
        if ((edges & (1 << AbeRight)) != 0 && rect.Right > monitor.Right - HiddenTaskbarStrip)
            (rect.Right, moved) = (monitor.Right - HiddenTaskbarStrip, true);
        if ((edges & (1 << AbeBottom)) != 0 && rect.Bottom > monitor.Bottom - HiddenTaskbarStrip)
            (rect.Bottom, moved) = (monitor.Bottom - HiddenTaskbarStrip, true);
        return moved;
    }

    /// <summary>
    /// A window maximized before its hook was there (made maximized: its client area worked out as its handle was made), or
    /// beside a taskbar that came to hide itself since: its frame worked out anew now when its client area covers that
    /// taskbar's edge, which <see cref="OnNcCalcSize"/> keeps it short of.
    /// </summary>
    internal static void KeepClearOfHiddenTaskbarNow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsZoomed(window) || !ClientOnScreen(window, out var client) || !KeepShortOfHiddenTaskbars(ref client))
            return;

        SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>Whether a point of the screen lies in the strip kept clear along an edge of the window's monitor with a taskbar that hides itself.</summary>
    private static bool InHiddenTaskbarStrip(IntPtr window, int x, int y)
    {
        var monitorHandle = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (!TryMonitorInfo(monitorHandle, out var info))
            return false;

        // Windows is asked which edges have such a taskbar only when the point is at one of them (it asks the shell), and
        // its answer is kept for a second: the pointer resting at an edge (Close, in the top right corner) asks at each move.
        var monitor = info.Monitor;
        if (x < monitor.Left || x >= monitor.Right || y < monitor.Top || y >= monitor.Bottom)
            return false;
        var near = (x < monitor.Left + HiddenTaskbarStrip ? 1 << AbeLeft : 0) | (y < monitor.Top + HiddenTaskbarStrip ? 1 << AbeTop : 0)
            | (x >= monitor.Right - HiddenTaskbarStrip ? 1 << AbeRight : 0) | (y >= monitor.Bottom - HiddenTaskbarStrip ? 1 << AbeBottom : 0);
        if (near == 0)
            return false;

        var now = Environment.TickCount64;
        if (_stripEdges.Monitor != monitorHandle || now - _stripEdges.At > 1000)
            _stripEdges = (monitorHandle, AutoHideEdges(monitorHandle), now);
        return (near & _stripEdges.Edges) != 0;
    }

    // The edges of a monitor with a taskbar that hides itself, as InHiddenTaskbarStrip last asked (when, in milliseconds).
    private static (IntPtr Monitor, int Edges, long At) _stripEdges;

    // WM_NCUAHDRAWCAPTION and WM_NCUAHDRAWFRAME (undocumented): what the themed DefWindowProc sends a window to draw its classic
    // caption and frame, after a title, icon or style change, outside WM_NCPAINT.
    private const int WmNcPaint = 0x0085, WmNcUahDrawCaption = 0x00AE, WmNcUahDrawFrame = 0x00AF;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindowDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(IntPtr dc, in NativeRect rect, IntPtr brush);

    [LibraryImport("gdi32.dll")]
    private static partial int ExcludeClipRect(IntPtr dc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateSolidBrush(int color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    /// <summary>
    /// A window that draws its own frame never lets Windows paint one: Windows paints the classic caption and frame through the
    /// window's whole DC, over the picture (5.2.1, the user's bug: an old-style title bar and border flashing as the window was
    /// maximized, restored, minimized or retitled). Maximized beside a taskbar that hides itself, the strip kept clear of it
    /// (HiddenTaskbarStrip) is frame, which Windows painted white (a bright line along the bottom of the screen, measured
    /// 255,255,255 on both rows). Whatever frame there is, in every state, is filled with the window's own background; the
    /// client area is left to the window. Whether the message was answered (always, for a WPF window).
    /// </summary>
    private static bool PaintFrameStrip(IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is not Window window)
            return false;
        // Minimized, or no frame outside the client area (restored, WindowChrome's): nothing to paint.
        if (IsIconic(hwnd) || !GetWindowRect(hwnd, out var frame) || !ClientOnScreen(hwnd, out var client)
            || (client.Left <= frame.Left && client.Top <= frame.Top && client.Right >= frame.Right && client.Bottom >= frame.Bottom))
            return true;

        var dc = GetWindowDC(hwnd);
        if (dc == IntPtr.Zero)
            return true;

        var color = window.Background is System.Windows.Media.SolidColorBrush background ? background.Color : System.Windows.Media.Colors.Black;
        var brush = CreateSolidBrush(color.R | color.G << 8 | color.B << 16);

        // The window's DC counts from its own top left corner; the client area is left to the window.
        ExcludeClipRect(dc, client.Left - frame.Left, client.Top - frame.Top, client.Right - frame.Left, client.Bottom - frame.Top);
        FillRect(dc, new NativeRect { Right = frame.Right - frame.Left, Bottom = frame.Bottom - frame.Top }, brush);
        DeleteObject(brush);
        ReleaseDC(hwnd, dc);
        return true;
    }

    /// <summary>
    /// The window hook of another window that draws its own frame (the Settings screen, 4.4): the same strip kept clear of a
    /// taskbar that hides itself, which its inset (MaximizedFrameInset) counts on, and its frame hit-tested in real pixels
    /// (FrameHitTest), so its title bar's buttons take their clicks under HandPeg's UI scale as the main window's do.
    /// </summary>
    internal static IntPtr KeepClearOfHiddenTaskbar(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcCalcSize:
                OnNcCalcSize(hwnd, lParam);
                break;
            case WmNcPaint when PaintFrameStrip(hwnd):
            case WmNcUahDrawCaption or WmNcUahDrawFrame:
                handled = true;
                break;
            case WmNcHitTest when HwndSource.FromHwnd(hwnd)?.RootVisual is Window window && FrameHitTest(window, hwnd, lParam) is { } hit:
                handled = true;
                return hit;
        }

        return IntPtr.Zero;
    }

    // ----- Snap Layouts: the Maximize button as Windows' own; the frame hit-tested in real pixels -----

    private const int WmNcHitTest = 0x0084, WmNcMouseMove = 0x00A0, WmNcMouseLeave = 0x02A2, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2;
    private const int HtNowhere = 0, HtClient = 1, HtCaption = 2, HtMaxButton = 9, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13,
        HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;

    // What each part of the frame answers, by row (top, middle, bottom) and column (left, middle, right): WindowChrome's own.
    private static readonly int[,] FrameParts =
    {
        { HtTopLeft, HtTop, HtTopRight },
        { HtLeft, HtClient, HtRight },
        { HtBottomLeft, HtBottom, HtBottomRight },
    };

    // Whether the pointer is pressed on the Maximize button (through the frame's own messages).
    private bool _maximizePressed;

    /// <summary>
    /// Over the Maximize button the window answers HTMAXBUTTON, which is what opens Windows 11's Snap Layouts; the
    /// button's hover and press then arrive as the frame's own messages, and are shown and acted on here. Everywhere else
    /// the frame is hit-tested here, in real pixels (FrameHitTest), never by WindowChrome.
    /// </summary>
    private IntPtr TitleBarHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcCalcSize:
                OnNcCalcSize(hwnd, lParam);
                break;
            case WmNcPaint when PaintFrameStrip(hwnd):
            case WmNcUahDrawCaption or WmNcUahDrawFrame:
                handled = true;
                break;
            case WmNcHitTest when IsOverMaximize(lParam):
                handled = true;
                return HtMaxButton;
            case WmNcHitTest when FrameHitTest(this, hwnd, lParam) is { } hit:
                handled = true;
                return hit;

            // A taskbar set to hide itself or not to (the working area changes): Windows places the window anew, and its
            // content is inset for where it is now, once it has.
            case WmSettingChange when wParam == SpiSetWorkArea:
                Dispatcher.BeginInvoke(ApplyWindowState, System.Windows.Threading.DispatcherPriority.Background);
                break;
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

    /// <summary>
    /// Whether a point of the screen (WM_NCHITTEST's, real pixels) is on the Maximize button: its bounds where it is really
    /// drawn (PointToScreen goes through HandPeg's UI scale and the monitor's DPI), so they never reach over Close or Minimize.
    /// </summary>
    private bool IsOverMaximize(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible || PresentationSource.FromVisual(MaximizeButton) is null)
            return false;

        var (x, y) = PointOf(lParam);
        var corner = MaximizeButton.PointToScreen(new Point(0, 0));
        var far = MaximizeButton.PointToScreen(new Point(MaximizeButton.ActualWidth, MaximizeButton.ActualHeight));
        return x >= Math.Min(corner.X, far.X) && x < Math.Max(corner.X, far.X) && y >= Math.Min(corner.Y, far.Y) && y < Math.Max(corner.Y, far.Y);
    }

    /// <summary>The point of the screen a mouse message of the frame carries, in real pixels (signed: a monitor left of or above the main one).</summary>
    private static (int X, int Y) PointOf(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return ((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    /// <summary>
    /// What a window that draws its own frame (WindowChrome) has under a point of the screen (WM_NCHITTEST), worked out in real
    /// pixels: what WindowChrome would answer, at the right scale; null when it cannot be (no chrome, no window yet), and
    /// WindowChrome answers. WindowChrome itself turns the pixels into DIPs by the window's root DPI, which HandPeg's UI scale
    /// sets to its content's pixels per DIP (1.5 on a 2880 x 1800 screen at 200 %: UiScale.SetRootDpi), not by the monitor's
    /// 2 the window is laid out at: it looked a third further from the window's corner than the pointer was, so the Close and
    /// Minimize buttons at the far right were never found there, and most of a click on them was a click on the caption
    /// (5.2-AuditB, the user's bug: Close did not always close HandPeg). Here the pointer is found where the window draws:
    /// <list type="bullet">
    /// <item>a part of the title bar that takes the pointer (WindowChrome.IsHitTestVisibleInChrome: the caption buttons, the
    /// status, Update) is the window's own (HTCLIENT), as is anything with a ResizeGripDirection that direction;</item>
    /// <item>maximized, the strip kept clear of a taskbar that hides itself is nowhere (HTNOWHERE: the client area stops short
    /// of it, OnNcCalcSize, but the window's frame still lies over it);</item>
    /// <item>otherwise the resize borders (not maximized; WindowChrome's ResizeBorderThickness) and the caption (the title bar
    /// as drawn, CaptionHeight from the top of the content) in the window's DIPs at the monitor's scale, then the window's own.</item>
    /// </list>
    /// </summary>
    private static int? FrameHitTest(Window window, IntPtr hwnd, IntPtr lParam)
    {
        if (WindowChrome.GetWindowChrome(window) is not { } chrome || PresentationSource.FromVisual(window) is null || !GetWindowRect(hwnd, out var frame))
            return null;

        var (x, y) = PointOf(lParam);
        var zoomed = IsZoomed(hwnd);
        if (zoomed && InHiddenTaskbarStrip(hwnd, x, y))
            return HtNowhere;

        IInputElement? hit;
        try
        {
            hit = window.InputHitTest(window.PointFromScreen(new Point(x, y)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Not on screen (any more): WindowChrome answers.
            return null;
        }

        if (hit is not null)
        {
            if (WindowChrome.GetIsHitTestVisibleInChrome(hit))
                return HtClient;
            var grip = WindowChrome.GetResizeGripDirection(hit);
            if (grip != ResizeGripDirection.None)
            {
                return grip switch
                {
                    ResizeGripDirection.TopLeft => HtTopLeft,
                    ResizeGripDirection.Top => HtTop,
                    ResizeGripDirection.TopRight => HtTopRight,
                    ResizeGripDirection.Right => HtRight,
                    ResizeGripDirection.BottomRight => HtBottomRight,
                    ResizeGripDirection.Bottom => HtBottom,
                    ResizeGripDirection.BottomLeft => HtBottomLeft,
                    ResizeGripDirection.Left => HtLeft,
                    _ => HtCaption,
                };
            }
        }

        // The frame's parts in pixels: WindowChrome's DIPs (the caption's height scaled with the content, UiScale.ScaleCaption)
        // at the monitor's scale. The caption is the title bar as drawn: CaptionHeight from the top of the content (inset from
        // the client area's top while maximized, MaximizedFrameInset), never reaching into what lies under it. Maximized (or not to
        // be resized), there are no resize borders, only the caption.
        var scale = UiScale.MonitorScale(window);
        var border = chrome.ResizeBorderThickness;
        var top = ClientOnScreen(hwnd, out var client) ? client.Top : frame.Top;
        var contentTop = top + ((window.Content as FrameworkElement)?.Margin.Top ?? 0) * scale;
        var sizable = !zoomed && window.ResizeMode is System.Windows.ResizeMode.CanResize or System.Windows.ResizeMode.CanResizeWithGrip;
        var onTopBorder = sizable && y < frame.Top + border.Top * scale;
        var row = y < contentTop + chrome.CaptionHeight * scale ? 0 : sizable && y >= frame.Bottom - border.Bottom * scale ? 2 : 1;
        var column = !sizable ? 1 : x < frame.Left + border.Left * scale ? 0 : x >= frame.Right - border.Right * scale ? 2 : 1;

        // Beside the caption, below the top border, a side is sized sideways, not from the corner.
        if (row == 0 && column != 1 && !onTopBorder)
            row = 1;
        var part = FrameParts[row, column];
        return part == HtTop && !onTopBorder ? HtCaption : part;
    }
}
