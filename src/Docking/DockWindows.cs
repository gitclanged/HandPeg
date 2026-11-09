using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace HandPegApp.Docking;

/// <summary>
/// The shape shown while a pane is dragged: where it will go if let go now. A window of its own, see-through, on top
/// and never in the way (clicks and the drag pass through it), so that it shows over the player's video too.
/// </summary>
public sealed partial class DockGuide : Window
{
    private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;
    private const uint SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private readonly TextBlock _label = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _shape, _words;
    private readonly Brush _accent, _fill;

    public DockGuide(Brush accent)
    {
        Services.UiScale.SetKeepsItsSize(this, true);
        (WindowStyle, AllowsTransparency, Background, ShowInTaskbar, ShowActivated, Topmost, ResizeMode) =
            (WindowStyle.None, true, Brushes.Transparent, false, false, true, ResizeMode.NoResize);
        var fill = accent.Clone();
        fill.Opacity = 0.32;
        fill.Freeze();
        (_accent, _fill) = (accent, fill);
        _words = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = _label };
        _shape = new Border
        {
            Background = fill,
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Child = _words,
        };
        Content = _shape;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExTransparent | WsExNoActivate | WsExToolWindow));
        };
    }

    /// <summary>Shows the shape over a rectangle of the screen, in its real pixels, with a word on where the pane would go.</summary>
    public void ShowAt(Rect pixels, string text)
    {
        _label.Text = text;
        (_shape.Background, _shape.BorderThickness, _shape.CornerRadius, _words.Visibility) = (_fill, new Thickness(2), new CornerRadius(4), Visibility.Visible);
        Place(pixels, 8);
    }

    /// <summary>Shows a thin upright line in the accent colour: where, among a row of tabs, a tab let go now goes.</summary>
    public void ShowLine(Rect pixels)
    {
        (_shape.Background, _shape.BorderThickness, _shape.CornerRadius, _words.Visibility) = (_accent, new Thickness(0), new CornerRadius(1.5), Visibility.Collapsed);
        Place(pixels, 2);
    }

    private void Place(Rect pixels, int least)
    {
        if (!IsVisible)
        {
            (Left, Top, Width, Height) = (0, 0, 1, 1);
            Show();
        }

        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, (int)Math.Round(pixels.X), (int)Math.Round(pixels.Y), Math.Max((int)Math.Round(pixels.Width), least), Math.Max((int)Math.Round(pixels.Height), least), SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }
}

/// <summary>
/// A side window (5.4C: as many as wanted, each for a monitor of its own), in one of two modes chosen in its title bar: a Live Video
/// Preview (a mirror of the player's picture, fitted at the canvas's shape: MainWindow.SyncMirrors and GpuPreviewEngine.AttachMirror),
/// or a Pane Docker holding any panes dragged to it, arranged as freely as in the main window. Keys, files dropped and the save
/// shortcuts are the main window's, as if it had them (MainWindow.HookSideWindow).
/// <para>Its frame is HandPeg's own (5.4C): WindowChrome and the main window's frame hook (MainWindow.FrameHook), a dark 26 DIP title
/// bar (its number, the mode, Minimize, Maximize and Close in the caption buttons' styles), kept inside its monitor while maximized as
/// the Settings screen is (ApplyFrame). F11, or a double-click on the picture, makes it cover its whole monitor (Escape or F11 again
/// gives it back). Its place is kept as Windows keeps it (Get/SetWindowPlacement: pixels of the monitor it was on, whatever that one's
/// scale), and never left off screen (KeepOnScreen).</para>
/// </summary>
public sealed partial class SideWindow : Window
{
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly TextBlock _hint = new()
    {
        Text = "Drag a pane here by its tab (its title, or the tab that appears at its top-left corner), or right-click a tab and choose Move to Side Window.",
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24),
    };

    // The window's parts: the title bar and the body under it (the docker's panes, or the preview), inside a root kept within the
    // monitor while maximized.
    private readonly Grid _root = new();
    private readonly Grid _titleBar = new();
    private readonly Border _body = new();
    private readonly Grid _docker = new() { Margin = new Thickness(10) };
    private readonly Grid _preview = new() { Background = Brushes.Black };
    private readonly AspectPanel _aspect = new();
    private readonly TextBlock _note = new()
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420, Margin = new Thickness(24),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), FontSize = 12 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _mode = new() { Width = 150, Height = 20, FontSize = 11.5, Padding = new Thickness(6, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    private readonly Button _minimize = new() { Content = "", ToolTip = "Minimize" };
    private readonly Button _maximize = new() { Content = "", ToolTip = "Maximize" };
    private readonly Button _close = new() { Content = "", ToolTip = "Close (a Pane Docker's panes go back to the main window)" };

    private VideoSurface? _surface;
    private SideWindowMode _shown;
    private bool _settingMode;
    private int _number = 1;

    public SideWindow(Window main, SideWindowMode mode)
    {
        Services.UiScale.SetKeepsItsSize(this, true);
        Owner = main;
        (Width, Height, MinWidth, MinHeight) = (720, 720, 320, 240);
        ShowInTaskbar = false;
        DataContext = main.DataContext;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        // Resources and names are the main window's: the panes were made there and look for them there. Its keys are the
        // main window's too, handed over by the main window itself (HookSideWindow: its PreviewKeyDown, which is where the
        // keybind manager answers every shortcut, saving included), so none is bound here but its own F11 and Escape.
        Resources.MergedDictionaries.Add(main.Resources);
        NameScope.SetNameScope(this, NameScope.GetNameScope(main));

        // The frame drawn by HandPeg, as the main window's and the Settings screen's (their WindowChrome), before the window has a handle.
        var caption = main.TryFindResource("CaptionBarHeight") is double height ? height : CaptionHeight;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = caption, GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false, ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
        });

        BuildTitleBar(caption);
        _docker.Children.Add(_hint);
        _docker.Children.Add(_host);
        _preview.Children.Add(_aspect);
        _preview.Children.Add(_note);
        _preview.MouseLeftButtonDown += (_, e) =>
        {
            // A double-click on the picture: the whole monitor, or back.
            if (e.ClickCount == 2)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        };

        // The body is the window's own (never its caption): dragging or double-clicking the picture never moves or maximizes it,
        // even in full screen with the title bar gone.
        WindowChrome.SetIsHitTestVisibleInChrome(_body, true);
        Grid.SetRow(_body, 1);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.Children.Add(_titleBar);
        _root.Children.Add(_body);
        Content = _root;
        ShowMode(mode);

        SourceInitialized += (_, _) =>
        {
            Services.ThemeManager.SetCloaked(this, true);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(MainWindow.FrameHook);

            // A taskbar set to hide itself or not to (the working area changes): inset again for where it is now.
            source?.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool _) =>
            {
                if (message == 0x001A && wParam == 0x002F)
                    Dispatcher.BeginInvoke(() => ApplyFrame(), System.Windows.Threading.DispatcherPriority.Background);
                return IntPtr.Zero;
            });
            ApplyPlacement();
            KeepOnScreen();
            ApplyFrame();
        };
        ContentRendered += (_, _) =>
        {
            Services.ThemeManager.SetCloaked(this, false);
            if (_wantFullScreen)
            {
                _wantFullScreen = false;
                SetFullScreen(true);
            }
        };
        Loaded += (_, _) => ApplyFrame();
        StateChanged += (_, _) => ApplyFrame(placeLater: true);
        DpiChanged += (_, _) => ApplyFrame();
        LocationChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                Dispatcher.BeginInvoke(() => ApplyFrame(), System.Windows.Threading.DispatcherPriority.Background);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11 || (e.Key == Key.Escape && _fullScreen))
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        };
        Closed += (_, _) => DropSurface();
    }

    /// <summary>The title bar's height as designed (CaptionBarHeight, App.xaml), when the main window's resources do not say.</summary>
    public const double CaptionHeight = 26;

    /// <summary>Its id among the side windows (SideWindowState.Id).</summary>
    public string SideId { get; init; } = "";

    /// <summary>Its number among the side windows open (1 the first): in its title and the menus' "Move to Side Window N".</summary>
    public int Number
    {
        get => _number;
        set
        {
            _number = value;
            UpdateTitle();
        }
    }

    /// <summary>What it shows now.</summary>
    public SideWindowMode Mode => _shown;

    /// <summary>Raised when its mode is chosen in its title bar: the dock manager hands a docker's panes back, then shows the mode (<see cref="ShowMode"/>).</summary>
    public event Action<SideWindowMode>? ModeRequested;

    /// <summary>Raised when a Live Video Preview's surface has its native window (to attach the mirror to), and when it goes.</summary>
    public event Action<SideWindow>? PreviewSurfaceChanged;

    /// <summary>The native window a Live Video Preview's mirror presents into; zero for a Pane Docker, or before it is made.</summary>
    public IntPtr PreviewHandle => _shown == SideWindowMode.LiveVideoPreview && _surface is { } surface ? surface.SurfaceHandle : IntPtr.Zero;

    /// <summary>Whether to open under the pointer (on whichever monitor it is), rather than where it was last.</summary>
    public bool PlaceAtPointer { get; set; }

    /// <summary>Where the side window's panes are put.</summary>
    public ContentControl Host => _host;

    /// <summary>Whether to show the note on what the window is for: while it holds no pane.</summary>
    public void ShowHint(bool empty) => _hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The title bar, for the golden test's look at its chrome.</summary>
    internal Grid TitleBar => _titleBar;

    /// <summary>The mode box in the title bar, for the golden test.</summary>
    internal ComboBox ModeBox => _mode;

    /// <summary>The caption buttons, for the golden test.</summary>
    internal IReadOnlyList<Button> CaptionButtons => [_minimize, _maximize, _close];

    /// <summary>What the body shows now (the docker's grid or the preview's), for the golden test.</summary>
    internal FrameworkElement Body => (FrameworkElement)_body.Child;

    /// <summary>The panel fitting the mirror at the canvas's shape, for the golden test.</summary>
    internal AspectPanel PreviewPanel => _aspect;

    private void BuildTitleBar(double caption)
    {
        _titleBar.Height = caption;
        _titleBar.SetResourceReference(Panel.BackgroundProperty, "TitleBarBrush");
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        _mode.Items.Add(new ComboBoxItem { Content = "Live Video Preview", ToolTip = "A live view of the player's picture, fitted to the canvas's shape: for a second monitor." });
        _mode.Items.Add(new ComboBoxItem { Content = "Pane Docker", ToolTip = "Holds any panes dragged here by their tabs, arranged as freely as in the main window." });
        _mode.ToolTip = "What this side window shows: a live view of the player, or panes of your choosing.";
        System.Windows.Automation.AutomationProperties.SetName(_mode, "Side Window Mode");
        WindowChrome.SetIsHitTestVisibleInChrome(_mode, true);
        _mode.SelectionChanged += (_, _) =>
        {
            if (_settingMode || _mode.SelectedIndex < 0)
                return;
            var mode = _mode.SelectedIndex == 0 ? SideWindowMode.LiveVideoPreview : SideWindowMode.PaneDocker;
            if (mode == _shown)
                return;
            if (ModeRequested is { } requested)
                requested(mode);
            else
                ShowMode(mode);
        };

        _minimize.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        _maximize.Click += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        };
        _close.Click += (_, _) => Close();
        _minimize.SetResourceReference(StyleProperty, "CaptionButton");
        _maximize.SetResourceReference(StyleProperty, "CaptionButton");
        _close.SetResourceReference(StyleProperty, "CaptionCloseButton");
        System.Windows.Automation.AutomationProperties.SetName(_minimize, "Minimize");
        System.Windows.Automation.AutomationProperties.SetName(_maximize, "Maximize");
        System.Windows.Automation.AutomationProperties.SetName(_close, "Close Side Window");

        UIElement[] parts = [_title, _mode, _status, _minimize, _maximize, _close];
        for (var i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn(parts[i], i);
            _titleBar.Children.Add(parts[i]);
        }

        // The caption buttons' styles say so too; set here as well, so they take their clicks before their style is applied.
        foreach (var button in CaptionButtons)
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
    }

    private void UpdateTitle()
    {
        var what = _shown == SideWindowMode.LiveVideoPreview ? "Live Video Preview" : "Pane Docker";
        Title = $"HandPeg: Side Window {_number} ({what})";
        _title.Text = $"Side Window {_number}";
    }

    /// <summary>
    /// Shows a mode (asked for by the dock manager, which has handed a docker's panes back first): the docker's host (its panes, or
    /// the hint while it holds none), or the preview (the mirror's surface, made now, at the canvas's shape). The mode box follows.
    /// </summary>
    public void ShowMode(SideWindowMode mode)
    {
        _shown = mode;
        _settingMode = true;
        _mode.SelectedIndex = mode == SideWindowMode.LiveVideoPreview ? 0 : 1;
        _settingMode = false;
        if (mode == SideWindowMode.LiveVideoPreview)
        {
            if (_surface is null)
            {
                // Its native window lets the pointer through to the preview under it (a double-click there is full screen).
                _surface = new VideoSurface { PassesPointer = true };
                var made = _surface;
                made.SurfaceReady += () =>
                {
                    if (ReferenceEquals(_surface, made))
                        PreviewSurfaceChanged?.Invoke(this);
                };
                _aspect.Children.Add(made);
            }

            _body.Child = _preview;
        }
        else
        {
            DropSurface();
            _body.Child = _docker;
        }

        UpdateTitle();
        _status.Text = "";
    }

    /// <summary>The preview's surface goes (a docker, or the window closed): its mirror is let go first (PreviewSurfaceChanged), then its window.</summary>
    private void DropSurface()
    {
        if (_surface is not { } surface)
            return;
        _surface = null;
        PreviewSurfaceChanged?.Invoke(this);
        _aspect.Children.Remove(surface);
        surface.Dispose();
    }

    /// <summary>The shape the mirror is fitted at (the canvas's width over its height, as the player shows it).</summary>
    public void SetPreviewAspect(double aspect) => _aspect.Aspect = aspect;

    /// <summary>
    /// A Live Video Preview's note in place of its picture (nothing to mirror yet: no player running), or null for the picture. A
    /// native window covers anything drawn over it, so the surface is hidden while the note shows.
    /// </summary>
    public void ShowPreviewNote(string? note)
    {
        _note.Text = note ?? "";
        _note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        _aspect.Visibility = note is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A short word in the title bar beside the mode (the player hidden: its last picture is kept); null for none.</summary>
    public void SetStatus(string? status) => _status.Text = status ?? "";

    // ----- The frame (WindowChrome, MainWindow.FrameHook) -----

    // Maximized, a window that draws its own frame reaches past the screen's edges by its frame: its content is kept inside (as the
    // Settings screen's, SettingsWindow.Stage.cs); Windows 11 squares its corners then, and rounds them again restored.
    private void ApplyFrame() => ApplyFrame(placeLater: false);

    private void ApplyFrame(bool placeLater)
    {
        var maximized = WindowState == WindowState.Maximized;
        if (maximized && !_fullScreen)
            MainWindow.KeepClearOfHiddenTaskbarNow(new WindowInteropHelper(this).Handle, placeLater);
        Services.ThemeManager.ApplyCorners(this);
        _root.Margin = maximized && !_fullScreen ? MainWindow.MaximizedFrameInset(this) : new Thickness(0);
        _maximize.Content = maximized ? "" : "";
        _maximize.ToolTip = maximized ? "Restore Down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(_maximize, maximized ? "Restore" : "Maximize");
    }

    // ----- Its place: as Windows keeps it, full screen, on screen -----

    private const int SwHide = 0, SwShowNormal = 1, SwShowMaximized = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length, Flags, ShowCmd;
        public NativePoint MinPosition, MaxPosition;
        public NativeRect NormalPosition;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromRect(in NativeRect rect, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(IntPtr window, in WindowPlacement placement);

    // Where it is to open (Place), until its window has a handle; and its place before it went full screen.
    private int[]? _placement;
    private bool _wantFullScreen;
    private bool _fullScreen;
    private (int[]? Placement, bool Maximized) _beforeFullScreen;

    /// <summary>Whether it covers its whole monitor now (F11, a double-click on the picture).</summary>
    public bool IsFullScreen => _fullScreen;

    /// <summary>
    /// Where it opens, before it is shown: its saved place (<paramref name="placement"/>, Windows' own: set as the window gets its
    /// handle), else a place in DIPs from before 5.4C, else under the pointer, else over the main window; maximized, or full screen.
    /// </summary>
    public void Place(int[]? placement, double[]? bounds, bool maximized, bool fullScreen, bool atPointer)
    {
        _placement = placement is { Length: 4 } && placement[2] - placement[0] >= 100 && placement[3] - placement[1] >= 100 ? placement : null;
        if (_placement is not null)
            WindowStartupLocation = WindowStartupLocation.Manual;
        else if (bounds is [var left, var top, var width, var height] && width > 100 && height > 100)
            (WindowStartupLocation, Left, Top, Width, Height) = (WindowStartupLocation.Manual, left, top, width, height);
        else if (atPointer)
            (WindowStartupLocation, PlaceAtPointer) = (WindowStartupLocation.Manual, true);
        else
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (maximized)
            WindowState = WindowState.Maximized;
        _wantFullScreen = fullScreen;
    }

    // The saved place, as the window gets its handle (hidden still: SW_HIDE only sets where it will be restored to and shown).
    private void ApplyPlacement()
    {
        if (_placement is not [var left, var top, var right, var bottom])
            return;
        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>(), ShowCmd = SwHide,
            NormalPosition = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom },
        };
        SetWindowPlacement(new WindowInteropHelper(this).Handle, placement);
    }

    /// <summary>
    /// Its place as Windows keeps it now (the restored place, in workspace pixels; maximized; full screen, with the place it had before),
    /// to be saved; null before it has a handle.
    /// </summary>
    public (int[]? Placement, bool Maximized, bool FullScreen)? CurrentPlacement()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return null;
        if (_fullScreen)
            return (_beforeFullScreen.Placement, _beforeFullScreen.Maximized, true);

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(handle, ref placement))
            return null;
        var normal = placement.NormalPosition;
        return ([normal.Left, normal.Top, normal.Right, normal.Bottom], WindowState == WindowState.Maximized, false);
    }

    /// <summary>The whole monitor, or back to where it was (F11, Escape, a double-click on the picture).</summary>
    public void ToggleFullScreen() => SetFullScreen(!_fullScreen);

    private void SetFullScreen(bool on)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            _wantFullScreen = on;
            return;
        }

        if (on == _fullScreen)
            return;

        if (on)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
                return;
            var placed = CurrentPlacement();
            _beforeFullScreen = (placed?.Placement, placed?.Maximized ?? false);
            _fullScreen = true;
            _titleBar.Visibility = Visibility.Collapsed;
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            var monitor = info.Monitor;
            SetWindowPos(handle, IntPtr.Zero, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, 0x10 | 0x20);
        }
        else
        {
            _fullScreen = false;
            _titleBar.Visibility = Visibility.Visible;
            ResizeMode = ResizeMode.CanResize;
            if (_beforeFullScreen.Placement is [var left, var top, var right, var bottom])
            {
                var placement = new WindowPlacement
                {
                    Length = Marshal.SizeOf<WindowPlacement>(), ShowCmd = _beforeFullScreen.Maximized ? SwShowMaximized : SwShowNormal,
                    NormalPosition = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom },
                };
                SetWindowPlacement(handle, placement);
            }
        }

        ApplyFrame();
    }

    /// <summary>
    /// Puts the window under the pointer if asked, and in any case wholly on one monitor's working area: a place
    /// kept from a monitor that is no longer there, or a pane let go near a screen's edge, never leaves it off screen.
    /// All in the screen's own pixels, as Windows counts them for this process. (Maximized, Windows places it.)
    /// </summary>
    private void KeepOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Maximized || !GetWindowRect(handle, out var rect))
            return;

        var (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (PlaceAtPointer && GetCursorPos(out var pointer))
            rect = new NativeRect { Left = pointer.X - width / 2, Top = pointer.Y - 40, Right = pointer.X - width / 2 + width, Bottom = pointer.Y - 40 + height };

        // The monitor it is most on (or nearest to), and the room there beside the taskbar.
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(rect, 2), ref info))
            return;

        var work = info.Work;
        (width, height) = (Math.Min(width, work.Right - work.Left), Math.Min(height, work.Bottom - work.Top));
        var x = Math.Clamp(rect.Left, work.Left, work.Right - width);
        var y = Math.Clamp(rect.Top, work.Top, work.Bottom - height);
        SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x4 | 0x10);
    }
}

/// <summary>
/// One child laid out at a shape (its width over its height), as large as fits, centred (5.4C: the Live Video Preview's surface at
/// the canvas's shape, so the swap chain's stretch to its window is the same both ways).
/// </summary>
public sealed class AspectPanel : Panel
{
    private double _aspect = 16.0 / 9.0;

    /// <summary>The shape, width over height (16:9 until told).</summary>
    public double Aspect
    {
        get => _aspect;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || Math.Abs(value - _aspect) < 1e-6)
                return;
            _aspect = value;
            InvalidateMeasure();
        }
    }

    /// <summary>The largest rectangle of a shape inside a room, centred (pure: the golden test checks it).</summary>
    internal static Rect Fit(Size room, double aspect)
    {
        if (!(room.Width > 0) || !(room.Height > 0) || !double.IsFinite(room.Width) || !double.IsFinite(room.Height) || !(aspect > 0))
            return new Rect(0, 0, 0, 0);
        var (width, height) = room.Width / room.Height > aspect ? (room.Height * aspect, room.Height) : (room.Width, room.Width / aspect);
        return new Rect((room.Width - width) / 2, (room.Height - height) / 2, width, height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var fitted = Fit(availableSize, _aspect).Size;
        foreach (UIElement child in InternalChildren)
            child.Measure(fitted);
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var place = Fit(finalSize, _aspect);
        foreach (UIElement child in InternalChildren)
            child.Arrange(place);
        return finalSize;
    }
}

/// <summary>The order of HandPeg's top-level windows on the screen (5.4C: which side window a dragged tab is over, which one is lowest).</summary>
internal static partial class WindowOrder
{
    private const uint GwHwndNext = 2;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetTopWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint command);

    /// <summary>The highest on the screen of some top-level windows; zero when none of them is found.</summary>
    public static IntPtr Topmost(IReadOnlyCollection<IntPtr> windows) => Walk(windows, highest: true);

    /// <summary>The lowest on the screen of some top-level windows; zero when none of them is found.</summary>
    public static IntPtr Lowest(IReadOnlyCollection<IntPtr> windows) => Walk(windows, highest: false);

    // Down the screen's order of top-level windows from the top, until each of them has been met (or the highest, at once).
    private static IntPtr Walk(IReadOnlyCollection<IntPtr> windows, bool highest)
    {
        var found = IntPtr.Zero;
        var left = windows.Count;
        for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero && left > 0; window = GetWindow(window, GwHwndNext))
        {
            if (!windows.Contains(window))
                continue;
            if (highest)
                return window;
            found = window;
            left--;
        }

        return found;
    }
}
