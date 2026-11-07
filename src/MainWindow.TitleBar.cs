using System.ComponentModel;
using System.Globalization;
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
    /// kept inside them; beside a taskbar that hides itself it is placed short of it instead (KeepClearOfHiddenTaskbarNow). The Maximize
    /// button shows Restore then, and Windows 11 squares the window's corners (ThemeManager.ApplyCorners, 5.3).
    /// </summary>
    private void ApplyWindowState() => ApplyWindowState(placeLater: false);

    private void ApplyWindowState(bool placeLater)
    {
        var maximized = WindowState == WindowState.Maximized;
        if (maximized)
            KeepClearOfHiddenTaskbarNow(new WindowInteropHelper(this).Handle, placeLater);
        ThemeManager.ApplyCorners(this);
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

        // Beside a taskbar that hides itself the window is maximized to the working area, that edge two pixels in
        // (MaximizedPlace), so nothing of it reaches past any edge to be kept in.
        var edges = handle == IntPtr.Zero ? 0 : AutoHideEdges(MonitorFromWindow(handle, MonitorDefaultToNearest));
        return edges != 0 ? new Thickness(0) : margin;
    }

    private const int SmCxSizeFrame = 32, SmCySizeFrame = 33, SmCxPaddedBorder = 92;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    // ----- A taskbar that hides itself, and the monitor's working area (Pass 4.2B-2) -----
    // Maximized, the window reaches past every edge of its monitor by its resize border. When the monitor's taskbar
    // hides itself (the working area is then the whole monitor), Windows takes such a window (its client area, which with
    // WindowChrome is the whole window) for a full-screen one and keeps the taskbar hidden: moving the pointer to that edge
    // no longer brings it up, and the Show Desktop corner at its end cannot be reached. So, maximized beside a taskbar that
    // hides, the window stops two pixels short of that edge (5.2-AuditB, the user's bug: one was not enough), whatever the
    // scale: the monitor's rectangles are real pixels. Since 5.3 that is the window's own maximized place (WM_GETMINMAXINFO: the
    // monitor's working area, that edge two pixels in), never a client area made smaller than the window: a strip of frame
    // outside the client area made Windows clip the maximized window with a region and turn the desktop's own frame drawing off
    // for that window for good (DWMWA_NCRENDERING_ENABLED 0, measured), after which Windows painted its classic caption over the
    // picture as the window went inactive, maximized or restored (the user's bug: a Vista title bar during moves and resizes).
    // Every edge of the window's own monitor with such a taskbar counts, not only the main monitor's. The strip is the
    // desktop's, outside the window; over it the window would answer nowhere anyway (FrameHitTest).

    private const int WmNcCalcSize = 0x0083, WmWindowPosChanging = 0x0046, WmSettingChange = 0x001A, SpiSetWorkArea = 0x002F,
        WmDisplayChange = 0x007E;
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
        // Asked at every step of a move or resize (WM_GETMINMAXINFO, WM_WINDOWPOSCHANGING): each answer is the shell's, across processes,
        // so it is kept for a second per monitor, and forgotten at once when the working area or the displays change.
        var now = Environment.TickCount64;
        if (_hiddenEdges.TryGetValue(monitor, out var known) && now - known.At <= 1000)
            return known.Edges;
        var edges = AskAutoHideEdges(monitor);
        if (_hiddenEdges.Count > 8)
            _hiddenEdges.Clear();
        _hiddenEdges[monitor] = (edges, now);
        return edges;
    }

    // What AutoHideEdges last heard for each monitor, and when (the UI thread's alone: both windows that ask live on it).
    private static readonly Dictionary<IntPtr, (int Edges, long At)> _hiddenEdges = [];

    /// <summary>The taskbar or the displays changed (WM_SETTINGCHANGE with SPI_SETWORKAREA, WM_DISPLAYCHANGE): ask the shell anew.</summary>
    internal static void ForgetHiddenTaskbars() => _hiddenEdges.Clear();

    private static int AskAutoHideEdges(IntPtr monitor)
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
    /// Where a window maximized on this monitor goes when an edge of it has a taskbar that hides itself (real pixels, on the screen):
    /// the monitor's working area with each such edge <see cref="HiddenTaskbarStrip"/> pixels in, so the window never covers the whole
    /// monitor; false when no edge has one (the window is maximized as Windows places it, reaching past the edges by its frame).
    /// </summary>
    private static bool MaximizedPlace(IntPtr monitorHandle, out NativeRect place, out NativeRect monitor)
    {
        (place, monitor) = (default, default);
        var edges = AutoHideEdges(monitorHandle);
        if (edges == 0 || !TryMonitorInfo(monitorHandle, out var info))
            return false;

        monitor = info.Monitor;
        var work = info.Work;
        var (left, top, right, bottom) = MaximizedPlaceOf((monitor.Left, monitor.Top, monitor.Right, monitor.Bottom), (work.Left, work.Top, work.Right, work.Bottom), edges);
        place = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom };
        return true;
    }

    /// <summary>
    /// The maximized place on a monitor (real pixels) from its rectangle, its working area and its edges with a taskbar that hides
    /// itself (bits 1 &lt;&lt; edge, left top right bottom): the working area with each such edge <see cref="HiddenTaskbarStrip"/> pixels in
    /// from the monitor's. Pure: the golden test (window-frame-messages) asks it for monitors it makes up.
    /// </summary>
    internal static (int Left, int Top, int Right, int Bottom) MaximizedPlaceOf((int Left, int Top, int Right, int Bottom) monitor,
        (int Left, int Top, int Right, int Bottom) work, int edges)
    {
        var place = work;
        if ((edges & (1 << AbeLeft)) != 0)
            place.Left = Math.Max(place.Left, monitor.Left + HiddenTaskbarStrip);
        if ((edges & (1 << AbeTop)) != 0)
            place.Top = Math.Max(place.Top, monitor.Top + HiddenTaskbarStrip);
        if ((edges & (1 << AbeRight)) != 0)
            place.Right = Math.Min(place.Right, monitor.Right - HiddenTaskbarStrip);
        if ((edges & (1 << AbeBottom)) != 0)
            place.Bottom = Math.Min(place.Bottom, monitor.Bottom - HiddenTaskbarStrip);
        return place;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    /// <summary>
    /// WM_WINDOWPOSCHANGING of a maximized window beside a taskbar that hides itself: whatever place Windows proposes (maximizing, on
    /// any monitor; Win+Shift+Arrow to another monitor; the taskbar's setting changed) becomes the <see cref="MaximizedPlace"/> of the
    /// monitor it lies on; on a monitor with no taskbar that hides itself Windows' own place stands. A restore is not touched: Windows
    /// clears the window's maximized state before it moves it.
    /// </summary>
    private static void KeepMaximizedPlaceShort(IntPtr window, IntPtr lParam)
    {
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((pos.Flags & SwpNoSize) != 0 && (pos.Flags & SwpNoMove) != 0 || !IsZoomed(window) || !GetWindowRect(window, out var now))
            return;

        var proposed = new NativeRect
        {
            Left = (pos.Flags & SwpNoMove) != 0 ? now.Left : pos.X,
            Top = (pos.Flags & SwpNoMove) != 0 ? now.Top : pos.Y,
        };
        proposed.Right = proposed.Left + ((pos.Flags & SwpNoSize) != 0 ? now.Right - now.Left : pos.Width);
        proposed.Bottom = proposed.Top + ((pos.Flags & SwpNoSize) != 0 ? now.Bottom - now.Top : pos.Height);
        if (!MaximizedPlace(MonitorFromRect(proposed, MonitorDefaultToNearest), out var place, out _)
            || (proposed.Left == place.Left && proposed.Top == place.Top && proposed.Right == place.Right && proposed.Bottom == place.Bottom))
            return;

        (pos.X, pos.Y, pos.Width, pos.Height) = (place.Left, place.Top, place.Right - place.Left, place.Bottom - place.Top);
        pos.Flags &= ~(SwpNoMove | SwpNoSize);
        Marshal.StructureToPtr(pos, lParam, false);
    }

    /// <summary>
    /// A window maximized before its hook was there (WPF makes a window that starts maximized with WS_MAXIMIZE, so Windows places it as
    /// its handle is made, and showing it later does not place it again), or beside a taskbar that came to hide itself since: put where
    /// <see cref="KeepMaximizedPlaceShort"/> would keep it, when it is not there. At once (at SourceInitialized the window is still
    /// hidden, so its first frame is already right), or with <paramref name="later"/> once the message that asked is done (a state change,
    /// inside WM_SIZE: WPF lays the window out from that message's size afterwards). It stays maximized (measured: Windows keeps a
    /// maximized window where SetWindowPos puts it).
    /// </summary>
    internal static void KeepClearOfHiddenTaskbarNow(IntPtr window, bool later = false)
    {
        if (later)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => KeepClearOfHiddenTaskbarNow(window),
                System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        if (window == IntPtr.Zero || !IsZoomed(window) || !GetWindowRect(window, out var now)
            || !MaximizedPlace(MonitorFromWindow(window, MonitorDefaultToNearest), out var place, out _)
            || (now.Left == place.Left && now.Top == place.Top && now.Right == place.Right && now.Bottom == place.Bottom))
            return;

        SetWindowPos(window, IntPtr.Zero, place.Left, place.Top, place.Right - place.Left, place.Bottom - place.Top, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>Whether a point of the screen lies in the strip kept clear along an edge of the window's monitor with a taskbar that hides itself.</summary>
    private static bool InHiddenTaskbarStrip(IntPtr window, int x, int y)
    {
        var monitorHandle = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (!TryMonitorInfo(monitorHandle, out var info))
            return false;

        // Windows is asked which edges have such a taskbar only when the point is at one of them (it asks the shell), and
        // its answer is kept for a second (AutoHideEdges): the pointer resting at an edge (Close, in the top right corner) asks at each move.
        var monitor = info.Monitor;
        if (x < monitor.Left || x >= monitor.Right || y < monitor.Top || y >= monitor.Bottom)
            return false;
        var near = (x < monitor.Left + HiddenTaskbarStrip ? 1 << AbeLeft : 0) | (y < monitor.Top + HiddenTaskbarStrip ? 1 << AbeTop : 0)
            | (x >= monitor.Right - HiddenTaskbarStrip ? 1 << AbeRight : 0) | (y >= monitor.Bottom - HiddenTaskbarStrip ? 1 << AbeBottom : 0);
        if (near == 0)
            return false;

        return (near & AutoHideEdges(monitorHandle)) != 0;
    }

    // WM_NCUAHDRAWCAPTION and WM_NCUAHDRAWFRAME (undocumented): what the themed DefWindowProc sends a window to draw its classic
    // caption and frame, after a title, icon or style change, outside WM_NCPAINT.
    private const int WmNcPaint = 0x0085, WmNcUahDrawCaption = 0x00AE, WmNcUahDrawFrame = 0x00AF;

    // The frame's other messages (5.3): activation, a new title or icon, a move or size loop's two ends, a DPI or theme change.
    private const int WmNcActivate = 0x0086, WmSetText = 0x000C, WmSetIcon = 0x0080, WmEnterSizeMove = 0x0231, WmExitSizeMove = 0x0232,
        WmDpiChanged = 0x02E0, WmThemeChanged = 0x031A;
    private const int GwlStyle = -16;
    private const long WsVisible = 0x10000000;

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

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
    /// A window that draws its own frame never lets Windows paint frame it has on the screen: maximized beside a taskbar that
    /// hides itself, the strip kept clear of it (HiddenTaskbarStrip) is frame, which Windows painted white (5.2.1, the user's bug:
    /// a bright line along the bottom of the screen, measured 255,255,255 on both rows). That frame is filled with the window's
    /// own background; the client area is left to the window. Whether it was: false when no frame lies outside the client area
    /// (restored, WindowChrome makes the client area the whole window) or the window is minimized, so that WM_NCPAINT then goes
    /// on to Windows, which has an empty frame to paint (5.3; 5.2.1 answered it in every state).
    /// </summary>
    private static bool PaintFrameStrip(IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is not Window window || IsIconic(hwnd) || !FrameOutsideClient(hwnd, out var frame, out var client))
            return false;

        // Frame on the screen but no DC to paint it with: answered all the same, so Windows never paints it white.
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

    /// <summary>Whether any of a window's frame lies outside its client area (maximized beside a taskbar that hides itself: the strip); both on the screen, in real pixels.</summary>
    private static bool FrameOutsideClient(IntPtr hwnd, out NativeRect frame, out NativeRect client)
    {
        client = default;
        return GetWindowRect(hwnd, out frame) && ClientOnScreen(hwnd, out client)
            && (client.Left > frame.Left || client.Top > frame.Top || client.Right < frame.Right || client.Bottom < frame.Bottom);
    }

    // ----- The frame's messages (5.3): one hook for every window that draws its own frame -----
    // The main window (through TitleBarHook) and the Settings screen share one hook, FrameHook, whose decisions are one pure
    // function (FrameDecision, which window-frame-messages prints as a table). WindowChrome's own hook (WPF's WindowChromeWorker)
    // is added first, as the XAML sets the chrome before the code-behind subscribes to SourceInitialized, and a window's hooks are
    // called from the last added to the first, so HandPeg's hook answers before WindowChrome's:
    // - WM_NCACTIVATE: DefWindowProc with lParam -1, which changes the window's active look without painting over the caption,
    //   its answer returned (TRUE: answering 0 to wParam FALSE would refuse to let the window go inactive).
    // - WM_SETTEXT, WM_SETICON: DefWindowProc with WS_VISIBLE cleared around it, so Windows keeps the title or the icon and has no
    //   visible caption to paint; put back only if it was set, so a window not shown yet is never made visible.
    //   These two are exactly what WindowChrome's worker does (WPF 8.0 and 10.0); answered here, they never depend on its order.
    // - WM_NCCALCSIZE: never answered here, never changed: WindowChrome's worker answers it in every state (the client area the
    //   whole window, WVR_REDRAW), so the window never has frame outside its client area (which turned the desktop's own frame
    //   drawing off for it: see the taskbar section). DefWindowProc's answer would give the window a classic caption and border.
    // - WM_WINDOWPOSCHANGING: while maximized beside a taskbar that hides itself, whatever place Windows proposes becomes the monitor's
    //   maximized place (MaximizedPlace), then passed on. WM_GETMINMAXINFO is left to Windows: it reads those numbers for whichever monitor
    //   the window maximizes on, so numbers for the monitor it is on now were wrong on another (a monitor larger than the main one, or
    //   one with no taskbar that hides itself).
    // - WM_NCPAINT: answered only where the window has frame on the screen (maximized with frame outside its client area, which
    //   nothing of HandPeg's makes any more: the strip painted first); otherwise it goes on to Windows, minimized too, which keeps
    //   the desktop drawing the frame (answering it while minimized turned that off for good, measured).
    // - WM_NCUAHDRAWCAPTION, WM_NCUAHDRAWFRAME: swallowed in every state (WindowChrome's worker has no answer for them).
    // - WM_NCHITTEST: the frame hit-tested in real pixels (FrameHitTest).
    // A window with Windows' own caption (the side window, DockWindows) has no such hook: its caption must repaint on activation.

    /// <summary>What <see cref="FrameHook"/> does with a message (<see cref="FrameDecision"/>).</summary>
    internal enum FrameAnswer
    {
        /// <summary>Not the frame's, or not in this state: left to the hooks after this one (WindowChrome's, the Window's) and to DefWindowProc.</summary>
        PassOn,

        /// <summary>Answered with nothing done: Windows' classic caption and frame are never painted.</summary>
        Swallow,

        /// <summary>DefWindowProc with lParam -1, its answer returned: the active look changes, nothing is painted over the caption.</summary>
        ActivateNoPaint,

        /// <summary>DefWindowProc with WS_VISIBLE cleared around it (only when set), its answer returned: the title or icon kept, nothing painted.</summary>
        DefaultHidden,

        /// <summary>The frame outside the client area painted in the window's background (<see cref="PaintFrameStrip"/>), then answered.</summary>
        PaintStrip,

        /// <summary>The maximized place kept short of a taskbar that hides itself (WM_WINDOWPOSCHANGING), then passed on.</summary>
        KeepShortThenPassOn,

        /// <summary>The frame hit-tested in real pixels (<see cref="FrameHitTest"/>), answered when it can say; else left to WindowChrome's hook.</summary>
        HitTest,
    }

    /// <summary>
    /// What the frame hook does with a message, by the window's state: <paramref name="zoomed"/> (maximized), <paramref name="iconic"/>
    /// (minimized), and <paramref name="stripOutside"/> (frame outside its client area, which only a window maximized beside a
    /// taskbar that hides itself has). Pure: the hook and the golden test (window-frame-messages) both ask it.
    /// </summary>
    internal static FrameAnswer FrameDecision(int message, nint wParam, bool zoomed, bool iconic, bool stripOutside) => message switch
    {
        // Every state, whatever wParam says (activating or not; a whole frame or part of one).
        WmNcActivate => FrameAnswer.ActivateNoPaint,
        WmSetText or WmSetIcon => FrameAnswer.DefaultHidden,
        WmNcUahDrawCaption or WmNcUahDrawFrame => FrameAnswer.Swallow,
        WmNcHitTest => FrameAnswer.HitTest,
        WmWindowPosChanging => FrameAnswer.KeepShortThenPassOn,
        // Never swallowed (5.3): a window that answers it unpainted while minimized (one with no taskbar button, the Settings screen,
        // lies on the desktop as a caption) is clipped by Windows with a region and loses the desktop's frame drawing for good.
        WmNcPaint => zoomed && stripOutside ? FrameAnswer.PaintStrip : FrameAnswer.PassOn,
        _ => FrameAnswer.PassOn,
    };

    /// <summary>
    /// The window hook of every window that draws its own frame: the main window's (through <see cref="TitleBarHook"/>) and the
    /// Settings screen's (4.4). Windows never paints a classic caption or frame over its picture (see the section above); maximized
    /// beside a taskbar that hides itself, the window is placed short of it (its client area always the whole window); and its frame
    /// is hit-tested in real pixels (FrameHitTest), so its title bar's buttons take their clicks under HandPeg's UI scale.
    /// </summary>
    internal static IntPtr FrameHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmSettingChange && wParam == SpiSetWorkArea || message == WmDisplayChange)
            ForgetHiddenTaskbars();
        // Only WM_NCPAINT depends on the window's state; every other message is decided by its number alone.
        var zoomed = message is WmNcPaint && IsZoomed(hwnd);
        var iconic = message is WmNcPaint && IsIconic(hwnd);
        var outside = zoomed && message is WmNcPaint && FrameOutsideClient(hwnd, out _, out _);
        var answer = FrameDecision(message, wParam, zoomed, iconic, outside);
        var result = IntPtr.Zero;
        switch (answer)
        {
            case FrameAnswer.ActivateNoPaint:
                result = DefWindowProc(hwnd, WmNcActivate, wParam, -1);
                handled = true;
                break;
            case FrameAnswer.DefaultHidden:
                result = DefWindowProcHidden(hwnd, message, wParam, lParam);
                handled = true;
                break;
            case FrameAnswer.KeepShortThenPassOn:
                KeepMaximizedPlaceShort(hwnd, lParam);
                break;
            case FrameAnswer.PaintStrip:
                if (PaintFrameStrip(hwnd))
                    handled = true;
                break;
            case FrameAnswer.Swallow:
                handled = true;
                break;
            case FrameAnswer.HitTest when HwndSource.FromHwnd(hwnd)?.RootVisual is Window window && FrameHitTest(window, hwnd, lParam) is { } hit:
                result = hit;
                handled = true;
                break;
        }

        if (DevFlightRecorder.IsEnabled)
            TraceFrameMessage(hwnd, message, wParam, answer, handled);
        return result;
    }

    /// <summary>
    /// WM_SETTEXT or WM_SETICON given to DefWindowProc with the window's WS_VISIBLE cleared around it, as WindowChrome's worker does:
    /// Windows keeps the title or the icon and has no visible caption to paint. The style is put back only if it was set (a window not
    /// shown yet is never made visible), onto what DefWindowProc left. DefWindowProc's answer (TRUE; the icon before).
    /// </summary>
    private static IntPtr DefWindowProcHidden(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var visible = (style & WsVisible) != 0;
        if (visible)
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style & ~WsVisible));

        var result = DefWindowProc(hwnd, message, wParam, lParam);
        if (visible)
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(GetWindowLongPtr(hwnd, GwlStyle).ToInt64() | WsVisible));
        return result;
    }

    // ----- The frame's messages in the flight recorder (5.3, developers only) -----
    // With the verbose flight recorder on (DevFlightRecorder: dev-verbose.flag or Settings > General > Diagnostics, decided after the
    // first frame, as the recorder is), FrameHook records the frame's messages: which one, the window and its state, its wParam and
    // what the hook did with it, so a real drag on a test machine shows what reached Windows. Off, it costs the one test of
    // DevFlightRecorder.IsEnabled. On:
    // - WM_NCCALCSIZE and WM_NCPAINT, which come with every step of a move or resize, only inside a move or size loop
    //   (WM_ENTERSIZEMOVE to WM_EXITSIZEMOVE) and for a second after any other frame message or a change of the window's state
    //   (maximized, minimized, restored: seen as the messages come, so the change's own messages are among them);
    // - the rest (WM_NCACTIVATE, WM_SETTEXT, WM_SETICON, WM_NCUAHDRAWCAPTION, WM_NCUAHDRAWFRAME, the loop's two ends, WM_DPICHANGED,
    //   WM_THEMECHANGED) always;
    // - never more than FrameTraceCap lines in a second, nor FrameTraceLoopCap of the frequent ones in one move or size loop, the
    //   others counted in one line, so a long drag keeps to a share of the recorder's ring (DevFlightRecorder.Capacity lines).

    private const int FrameTraceCap = 40;

    // The window in a move or size loop (zero: none); until when (Environment.TickCount64) the frequent messages are recorded;
    // the second being counted, the lines recorded in it and those left out.
    private static IntPtr _traceSizeMove;
    private static long _traceUntil, _traceSecond;
    private static int _tracedThisSecond, _traceDropped, _tracedThisLoop, _droppedThisLoop;

    // The frame lines recorded in one move or size loop, at most (the recorder's ring holds DevFlightRecorder.Capacity lines in all).
    private const int FrameTraceLoopCap = 60;

    // Each window's state when its last frame message was traced (0 restored, 1 maximized, 2 minimized), to see it change.
    private static Dictionary<IntPtr, int>? _traceStates;

    /// <summary>Records one of the frame's messages in the flight recorder, as <see cref="FrameHook"/> answered it. Only while the recorder is on; never throws.</summary>
    private static void TraceFrameMessage(IntPtr hwnd, int message, IntPtr wParam, FrameAnswer answer, bool handled)
    {
        var frequent = message is WmNcCalcSize or WmNcPaint or WmWindowPosChanging;
        if (!frequent && message is not (WmNcActivate or WmSetText or WmSetIcon or WmNcUahDrawCaption or WmNcUahDrawFrame
            or WmEnterSizeMove or WmExitSizeMove or WmDpiChanged or WmThemeChanged))
            return;

        try
        {
            var now = Environment.TickCount64;
            var state = IsIconic(hwnd) ? 2 : IsZoomed(hwnd) ? 1 : 0;
            _traceStates ??= new();
            if (_traceStates.Count > 8)
                _traceStates.Clear();
            var changed = _traceStates.TryGetValue(hwnd, out var was) && was != state;
            _traceStates[hwnd] = state;

            if (message == WmEnterSizeMove)
                (_traceSizeMove, _tracedThisLoop, _droppedThisLoop) = (hwnd, 0, 0);
            else if (message == WmExitSizeMove && _traceSizeMove == hwnd)
            {
                _traceSizeMove = IntPtr.Zero;
                if (_droppedThisLoop > 0)
                    DevFlightRecorder.Record("frame", string.Create(CultureInfo.InvariantCulture, $"{_droppedThisLoop} more frame messages in that move or size loop were not recorded"));
            }

            // A long drag keeps to a share of the recorder's ring: the first lines of each move or size loop, the rest counted.
            if (frequent && _traceSizeMove == hwnd && ++_tracedThisLoop > FrameTraceLoopCap)
            {
                _droppedThisLoop++;
                return;
            }
            if (changed || !frequent)
                _traceUntil = now + 1000;
            if (frequent && _traceSizeMove != hwnd && now > _traceUntil)
                return;

            if (now - _traceSecond >= 1000)
            {
                if (_traceDropped > 0)
                    DevFlightRecorder.Record("frame", string.Create(CultureInfo.InvariantCulture, $"{_traceDropped} more frame messages in the second before were not recorded"));
                (_traceSecond, _tracedThisSecond, _traceDropped) = (now, 0, 0);
            }

            if (++_tracedThisSecond > FrameTraceCap)
            {
                _traceDropped++;
                return;
            }

            var where = state switch
            {
                2 => "minimized",
                1 when FrameOutsideClient(hwnd, out _, out _) => "maximized, frame outside its client area",
                1 => "maximized",
                _ => "restored",
            };
            var window = HwndSource.FromHwnd(hwnd)?.RootVisual?.GetType().Name ?? "a window";
            var loop = _traceSizeMove == hwnd ? ", in a move or size loop" : "";
            // Whether the desktop still draws the window's frame (DWMWA_NCRENDERING_ENABLED): off, Windows paints its classic one.
            var desktopFrame = DwmGetWindowAttribute(hwnd, DwmwaNcRenderingEnabled, out var enabled, sizeof(int)) == 0 ? enabled != 0 ? ", desktop frame on" : ", DESKTOP FRAME OFF" : "";
            DevFlightRecorder.Record("frame", string.Create(CultureInfo.InvariantCulture,
                $"{FrameMessageName(message)} wParam 0x{wParam.ToInt64():X} to {window} 0x{hwnd.ToInt64():X} ({where}{loop}{desktopFrame}): {answer}, {(handled ? "answered here" : "passed on")}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The trace is a help for developers, never a reason for a window's messages to fail.
        }
    }

    private const int DwmwaNcRenderingEnabled = 1;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    private static string FrameMessageName(int message) => message switch
    {
        WmNcCalcSize => "WM_NCCALCSIZE",
        WmWindowPosChanging => "WM_WINDOWPOSCHANGING",
        WmNcPaint => "WM_NCPAINT",
        WmNcActivate => "WM_NCACTIVATE",
        WmSetText => "WM_SETTEXT",
        WmSetIcon => "WM_SETICON",
        WmNcUahDrawCaption => "WM_NCUAHDRAWCAPTION",
        WmNcUahDrawFrame => "WM_NCUAHDRAWFRAME",
        WmEnterSizeMove => "WM_ENTERSIZEMOVE",
        WmExitSizeMove => "WM_EXITSIZEMOVE",
        WmDpiChanged => "WM_DPICHANGED",
        WmThemeChanged => "WM_THEMECHANGED",
        _ => string.Create(CultureInfo.InvariantCulture, $"0x{message:X4}"),
    };

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
    /// button's hover and press then arrive as the frame's own messages, and are shown and acted on here. Everything else goes
    /// to the hook every window that draws its own frame has (FrameHook, 5.3): the frame hit-tested in real pixels
    /// (FrameHitTest, never by WindowChrome), and no classic caption or frame ever painted.
    /// </summary>
    private IntPtr TitleBarHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest when IsOverMaximize(lParam):
                handled = true;
                return HtMaxButton;

            // A taskbar set to hide itself or not to (the working area changes): Windows places the window anew, and its
            // content is inset for where it is now, once it has.
            case WmSettingChange when wParam == SpiSetWorkArea:
                Dispatcher.BeginInvoke(() => ApplyWindowState(), System.Windows.Threading.DispatcherPriority.Background);
                break;

            // The pointer over the frame: shown on the Maximize button, the message still going on to Windows (Snap Layouts).
            case WmNcMouseMove:
                MaximizeButton.Tag = wParam == HtMaxButton ? _maximizePressed ? "pressed" : "hover" : null;
                break;
            case WmNcMouseLeave:
                (MaximizeButton.Tag, _maximizePressed) = (null, false);
                break;
            case WmNcLButtonDown when wParam == HtMaxButton:
                (_maximizePressed, MaximizeButton.Tag, handled) = (true, "pressed", true);
                return IntPtr.Zero;
            case WmNcLButtonUp when wParam == HtMaxButton:
                if (_maximizePressed)
                    MaximizeWindow_Click(MaximizeButton, new RoutedEventArgs());
                (_maximizePressed, MaximizeButton.Tag, handled) = (false, null, true);
                return IntPtr.Zero;
        }

        return FrameHook(hwnd, message, wParam, lParam, ref handled);
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
    /// <item>maximized, the strip kept clear of a taskbar that hides itself is nowhere (HTNOWHERE: since 5.3 the window stops short
    /// of it, MaximizedPlace, so a point there is not the window's; kept for a window Windows still placed over it);</item>
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
