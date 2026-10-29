using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

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
/// The side window: a second window of HandPeg's for a second monitor, holding any panes dragged to it, arranged as
/// freely as in the main window. Keys, files dropped and the save shortcuts are the main window's, as if it had them.
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

    public SideWindow(Window main)
    {
        Owner = main;
        Title = "HandPeg: Side Window";
        (Width, Height, MinWidth, MinHeight) = (720, 720, 320, 240);
        ShowInTaskbar = false;
        DataContext = main.DataContext;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        // Resources and names are the main window's: the panes were made there and look for them there. Its keys are the
        // main window's too, handed over by the main window itself (HookSideWindow: its PreviewKeyDown, which is where the
        // keybind manager answers every shortcut, saving included), so none is bound here.
        Resources.MergedDictionaries.Add(main.Resources);
        NameScope.SetNameScope(this, NameScope.GetNameScope(main));

        var root = new Grid { Margin = new Thickness(10) };
        root.Children.Add(_hint);
        root.Children.Add(_host);
        Content = root;
        SourceInitialized += (_, _) =>
        {
            Services.ThemeManager.ApplyTitleBar(this);
            KeepOnScreen();
        };
    }

    /// <summary>Whether to open under the pointer (on whichever monitor it is), rather than where it was last.</summary>
    public bool PlaceAtPointer { get; set; }

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromRect(in NativeRect rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    /// <summary>
    /// Puts the window under the pointer if asked, and in any case wholly on one monitor's working area: a place
    /// kept from a monitor that is no longer there, or a pane let go near a screen's edge, never leaves it off screen.
    /// All in the screen's own pixels, as Windows counts them for this process.
    /// </summary>
    private void KeepOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var rect))
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

    /// <summary>Where the side window's panes are put.</summary>
    public ContentControl Host => _host;

    /// <summary>Whether to show the note on what the window is for: while it holds no pane.</summary>
    public void ShowHint(bool empty) => _hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
}
