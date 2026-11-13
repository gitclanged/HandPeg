using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// The Fullscreen Preview's window (5.4.4, MainWindow.Fullscreen.cs): borderless, black, owned by the main window and covering one whole
/// monitor, its taskbar's strip included. The player's own surface is lifted into <see cref="Host"/> while it is up (the same swap chain:
/// no copy of the picture, no second engine) and handed back before it closes. It has no frame to draw, so it takes none of the main
/// window's frame handling (its chrome, hook or client-area messages). It covers the monitor it is put on again whenever Windows moves
/// it or its scale changes, and says so when the displays change (<see cref="DisplayChanged"/>), which ends the fullscreen preview.
/// Closed any other way than by its owner (Alt+F4), it refuses and asks to be left instead (<see cref="ExitAsked"/>), so the surface is
/// never closed with it.
/// </summary>
public sealed partial class PlayerFullscreenWindow : Window
{
    private const int WmDisplayChange = 0x007E;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200, MonitorDefaultToNearest = 2;

    // The monitor it covers (the screen's own pixels); whether it has been put there yet (until then a move of Windows' own, as its handle
    // is made, is not followed); whether its owner has let it close.
    private Int32Rect _monitor;
    private bool _placed, _mayClose;

    public PlayerFullscreenWindow(Int32Rect monitor)
    {
        _monitor = monitor;
        Title = "Fullscreen Preview";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Host = new Grid { Background = Brushes.Black };
        Content = Host;

        // Sized by what it covers: its content takes HandPeg's UI scale, its size never.
        UiScale.SetKeepsItsSize(this, true);
        LocationChanged += (_, _) => CoverOwnMonitor();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(CoverOwnMonitor);
        Loaded += (_, _) => CoverOwnMonitor();
    }

    /// <summary>What holds the player's surface while the window is up.</summary>
    public Grid Host { get; }

    /// <summary>The window was asked to close by anything but its owner (Alt+F4): the fullscreen preview is to be left.</summary>
    public event Action? ExitAsked;

    /// <summary>The displays changed (one added or taken away, a resolution changed): the fullscreen preview is to be left.</summary>
    public event Action? DisplayChanged;

    /// <summary>Its owner has taken the player's surface back: it may close now.</summary>
    internal void LetClose() => _mayClose = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(Message);

        // Put on its monitor as its handle is made, before it is first shown.
        Cover(_monitor);
        _placed = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_mayClose)
        {
            e.Cancel = true;
            ExitAsked?.Invoke();
            return;
        }

        base.OnClosing(e);
    }

    private nint Message(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmDisplayChange)
            Dispatcher.BeginInvoke(() => DisplayChanged?.Invoke());
        return 0;
    }

    // The monitor it is on now (Windows moved it: another monitor, a scale changed), covered whole.
    private void CoverOwnMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_placed && handle != 0 && MonitorArea(MonitorFromWindow(handle, MonitorDefaultToNearest)) is { } area)
            Cover(area);
    }

    private void Cover(Int32Rect area)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || area.Width <= 0 || area.Height <= 0)
            return;

        _monitor = area;
        if (GetWindowRect(handle, out var now) && now.Left == area.X && now.Top == area.Y && now.Right == area.X + area.Width && now.Bottom == area.Y + area.Height)
            return;
        SetWindowPos(handle, 0, area.X, area.Y, area.Width, area.Height, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>The whole of the monitor that holds most of <paramref name="pixels"/> (the screen's own pixels), or the nearest one.</summary>
    internal static Int32Rect MonitorAround(Rect pixels)
    {
        var rect = new NativeRect
        {
            Left = (int)Math.Floor(pixels.Left), Top = (int)Math.Floor(pixels.Top),
            Right = (int)Math.Ceiling(pixels.Right), Bottom = (int)Math.Ceiling(pixels.Bottom),
        };
        return MonitorArea(MonitorFromRect(ref rect, MonitorDefaultToNearest))
            ?? new Int32Rect(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
    }

    /// <summary>Whether <paramref name="other"/> is on the monitor this window covers.</summary>
    internal bool SharesMonitorWith(Window other)
    {
        var (mine, theirs) = (new WindowInteropHelper(this).Handle, new WindowInteropHelper(other).Handle);
        return mine != 0 && theirs != 0 && MonitorFromWindow(mine, MonitorDefaultToNearest) == MonitorFromWindow(theirs, MonitorDefaultToNearest);
    }

    private static Int32Rect? MonitorArea(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return null;
        var whole = info.Monitor;
        return new Int32Rect(whole.Left, whole.Top, whole.Right - whole.Left, whole.Bottom - whole.Top);
    }

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

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(ref NativeRect rect, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

/// <summary>
/// What the player's HUD is made of and how it comes and goes (5.4.4): the Fullscreen button over the docked player and the fullscreen
/// preview's transport bar, both dark over the picture whatever the theme (as the RAW / ENCODED pills of the Live Encoded Preview's wipe),
/// made at their first use; the rests after which they go; their fades.
/// </summary>
internal static class HudChrome
{
    /// <summary>
    /// How long the pointer rests before the Fullscreen button goes (ms), and before the fullscreen bar does; how often a HUD held up
    /// (the pointer on it, a drag of its scrubber, its Volume pill open) is looked at again.
    /// </summary>
    internal const double ButtonRestMs = 1500, BarRestMs = 1800, RecheckMs = 250;

    // The fades: in, out (ms).
    private const double FadeInMs = 120, FadeOutMs = 150;

    internal static readonly SolidColorBrush Face = Frozen(0xCC, 0x18, 0x18, 0x1C), Edge = Frozen(0xFF, 0x38, 0x38, 0x42),
        Text = Frozen(0xFF, 0xF2, 0xF2, 0xF2), Muted = Frozen(0xFF, 0x9A, 0x9A, 0xA4), Lit = Frozen(0xE6, 0x2C, 0x2C, 0x36),
        Pressed = Frozen(0xF0, 0x3A, 0x3A, 0x46), Chosen = Frozen(0xCC, 0x00, 0x78, 0xD7);

    private static readonly FontFamily Glyphs = new("Segoe MDL2 Assets");
    private static DoubleAnimation? _fadeIn, _fadeOut;
    private static Style? _button, _toggle;

    /// <summary>Whether the Fullscreen button stays up: the pointer on it, or moved over the player less than <see cref="ButtonRestMs"/> ago.</summary>
    internal static bool ButtonShown(double sinceMoveMs, bool overButton) => overButton || sinceMoveMs < ButtonRestMs;

    /// <summary>
    /// Whether the fullscreen bar stays up: the pointer on it, something of it held (its scrubber dragged, its Volume pill open), or the
    /// pointer moved less than <see cref="BarRestMs"/> ago.
    /// </summary>
    internal static bool BarShown(double sinceMoveMs, bool overBar, bool held) => overBar || held || sinceMoveMs < BarRestMs;

    /// <summary>
    /// The Fullscreen button over the docked player: top right, 10 DIPs in, Segoe's Full Screen glyph on a dark chip. Hidden (and taking
    /// no clicks) until it is faded in.
    /// </summary>
    internal static Button HoverButton()
    {
        var button = new Button
        {
            Style = ButtonStyle(typeof(Button), null),
            Content = "\uE740",
            Background = Face,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10),
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetName(button, "Fullscreen Preview");
        KeyTip.SetAction(button, "view.fullscreen");
        KeyTip.SetText(button, "Fullscreen Preview{keys}: the player over its whole monitor, with a transport bar that hides itself. Esc comes back.");
        return button;
    }

    /// <summary>
    /// The HUD's look for a button or a toggle (<paramref name="basedOn"/>, when given, keeps that style's content and triggers: Play's
    /// glyph turning to Pause): its glyph light on nothing, lit under the pointer, the accent while a toggle is on; it never takes the keyboard.
    /// </summary>
    internal static Style ButtonStyle(Type type, Style? basedOn)
    {
        if (basedOn is null && type == typeof(Button) && _button is { } button)
            return button;
        if (basedOn is null && type == typeof(ToggleButton) && _toggle is { } toggle)
            return toggle;

        var style = new Style(type, basedOn);
        style.Setters.Add(new Setter(Control.TemplateProperty, Chrome(type)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, Glyphs));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Seal();
        if (basedOn is null && type == typeof(Button))
            _button = style;
        else if (basedOn is null && type == typeof(ToggleButton))
            _toggle = style;
        return style;
    }

    // A rounded box round the content, lit under the pointer and pressed; a toggle's box the accent while it is on; dimmed when disabled.
    private static ControlTemplate Chrome(Type type)
    {
        var box = new FrameworkElementFactory(typeof(Border), "box");
        box.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        box.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        box.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        box.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        box.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.AppendChild(content);

        var template = new ControlTemplate(type) { VisualTree = box };
        template.Triggers.Add(Fill(UIElement.IsMouseOverProperty, Lit));
        if (type == typeof(ToggleButton))
            template.Triggers.Add(Fill(ToggleButton.IsCheckedProperty, Chosen));
        template.Triggers.Add(Fill(ButtonBase.IsPressedProperty, Pressed));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        template.Triggers.Add(disabled);
        template.Seal();
        return template;

        static Trigger Fill(DependencyProperty property, Brush brush)
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, brush, "box"));
            return trigger;
        }
    }

    /// <summary>
    /// Fades a part of the HUD in or out (120 ms in, 150 ms out; at once with <paramref name="instant"/> or with Windows' animations off).
    /// It takes clicks only while shown, and once faded out it is collapsed (nothing of it laid out or drawn).
    /// </summary>
    internal static void Fade(UIElement element, bool shown, bool instant = false)
    {
        element.IsHitTestVisible = shown;
        if (shown)
            element.Visibility = Visibility.Visible;

        if (instant || !SystemParameters.ClientAreaAnimation)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = shown ? 1 : 0;
            if (!shown)
                element.Visibility = Visibility.Collapsed;
            return;
        }

        var clock = (shown ? _fadeIn ??= Faded(1, FadeInMs) : _fadeOut ??= Faded(0, FadeOutMs)).CreateClock();
        if (!shown)
        {
            // Collapsed at the end, unless it was shown again meanwhile.
            clock.Completed += (_, _) =>
            {
                if (!element.IsHitTestVisible)
                    element.Visibility = Visibility.Collapsed;
            };
        }

        element.ApplyAnimationClock(UIElement.OpacityProperty, clock, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation Faded(double to, double ms)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
        animation.Freeze();
        return animation;
    }

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The Fullscreen Preview's transport (5.4.4): a dark rounded bar along the bottom of the picture, at most 900 DIPs wide, with Play / Pause,
/// the Volume pill (it opens to the right, over the scrubber's start), the playhead's scrubber and the time, the 3D Exploded View and Live
/// Encoded Preview toggles, and Exit. None of it takes the keyboard (Space stays Play / Pause). Its owner sets its DataContext to the view
/// model (the volume, the mute, the Live Encoded Preview and Play's glyph are bound to it), binds the 3D toggle to the bar's own, and tells
/// it the time while it is shown (<see cref="ShowTime"/>): nothing of it moves while it is hidden.
/// </summary>
public sealed class FullscreenTransportBar : Border
{
    // The 3D Exploded View's icon (three planes apart), as the playback bar's toggle has it.
    private static readonly Geometry PlanesIcon = FrozenGeometry("M 2,3 L 9,0.8 L 16,3 L 9,5.2 Z M 2,8 L 9,5.8 L 16,8 L 9,10.2 Z M 2,13 L 9,10.8 L 16,13 L 9,15.2 Z");

    private readonly Run _position = new(), _duration = new();
    private string? _durationShown;

    // Whether the time is being set from outside (not the user's move), and whether the scrubber's playhead is held.
    private bool _showing, _dragging;

    public FullscreenTransportBar(Style? playPauseStyle)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(16);
        MaxWidth = 900;
        Padding = new Thickness(6, 4, 8, 4);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Background = HudChrome.Face;
        BorderBrush = HudChrome.Edge;
        TextElement.SetForeground(this, HudChrome.Text);
        AutomationProperties.SetName(this, "Fullscreen Preview Controls");

        // Play / Pause: the playback bar's own style (its glyph and its name follow IsPlaying), in the HUD's look.
        PlayPause = new Button { Style = HudChrome.ButtonStyle(typeof(Button), playPauseStyle), Padding = new Thickness(12, 6, 12, 6) };
        if (playPauseStyle is null)
        {
            PlayPause.Content = "\uE768";
            AutomationProperties.SetName(PlayPause, "Play");
        }

        // The volume as the playback bar has it: the pill, its speaker the player's mute.
        Volume = new ExpandablePillSlider
        {
            Icon = PillIcon.Speaker, Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 20, DefaultValue = 100, ReadoutFormat = "{0:0}%",
            IsIconToggle = true, IconAutomationName = "Mute Player", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Volume. Click the speaker to mute the player (the export is not affected). Double-click the track to reset.",

            // Drawn in the HUD's colours, not the theme's: the bar is dark whatever the theme (5.4.4).
            IconBrush = HudChrome.Text, SurfaceBrush = HudChrome.Lit, EdgeBrush = HudChrome.Edge, TrackBrush = HudChrome.Muted,
        };
        AutomationProperties.SetName(Volume, "Volume");
        Volume.SetBinding(ExpandablePillSlider.ValueProperty, new Binding("Volume"));
        Volume.SetBinding(ExpandablePillSlider.IsIconCheckedProperty, new Binding("IsPlayerMuted"));

        // Opened, it is drawn over the scrubber's start: above it.
        Panel.SetZIndex(Volume, 1);

        Scrubber = new Slider { Minimum = 0, Maximum = 1, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        AutomationProperties.SetName(Scrubber, "Playhead");
        Scrubber.ValueChanged += Scrubber_ValueChanged;
        Scrubber.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true), handledEventsToo: true);
        Scrubber.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => LetGoOfScrubber()), handledEventsToo: true);
        Scrubber.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Scrubber_PreviewMouseLeftButtonDown), handledEventsToo: true);

        // The time, its digits all one width (TimeReadout), the length muted.
        Time = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _duration.Foreground = HudChrome.Muted;
        Time.Inlines.Add(_position);
        Time.Inlines.Add(_duration);
        AutomationProperties.SetName(Time, "Position");
        var readout = new TimeReadout { Child = Time, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

        var planes = new Path { Width = 18, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round, Data = PlanesIcon };
        planes.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1) });
        Exploded = new ToggleButton { Style = HudChrome.ButtonStyle(typeof(ToggleButton), null), Content = planes, Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(Exploded, "3D Exploded View");
        KeyTip.SetAction(Exploded, "view.exploded3d");
        KeyTip.SetText(Exploded, "3D Exploded View{keys}: the layer stack in 3D, each layer a plane in compositing order. Esc or this button goes back.");

        EncodedPreview = new ToggleButton { Style = HudChrome.ButtonStyle(typeof(ToggleButton), null), Content = MainWindow.LoopbackGlyph, Margin = new Thickness(2, 0, 0, 0) };
        AutomationProperties.SetName(EncodedPreview, MainWindow.LoopbackName);
        EncodedPreview.ToolTip = MainWindow.LoopbackName + ": the picture as the export's encoder would leave it, live.";
        EncodedPreview.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsExportQualityPreview") { Mode = BindingMode.TwoWay });
        EncodedPreview.SetBinding(IsEnabledProperty, new Binding("IsVideoExport"));
        ToolTipService.SetShowOnDisabled(EncodedPreview, true);

        Exit = new Button { Style = HudChrome.ButtonStyle(typeof(Button), null), Content = "\uE73F", Margin = new Thickness(6, 0, 0, 0) };
        AutomationProperties.SetName(Exit, "Exit Fullscreen Preview");
        KeyTip.SetAction(Exit, "view.fullscreen");
        KeyTip.SetText(Exit, "Exit Fullscreen Preview{keys}: Esc does the same.");

        var row = new Grid();
        UIElement[] parts = [PlayPause, Volume, Scrubber, readout, Exploded, EncodedPreview, Exit];
        for (var i = 0; i < parts.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = ReferenceEquals(parts[i], Scrubber) ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(parts[i], i);
            row.Children.Add(parts[i]);
        }

        Child = row;
    }

    public Button PlayPause { get; }

    public ExpandablePillSlider Volume { get; }

    /// <summary>The playhead's scrubber, 0 to the Sequence's length in milliseconds.</summary>
    public Slider Scrubber { get; }

    /// <summary>The position and the length, as the playback bar's readout shows them.</summary>
    public TextBlock Time { get; }

    public ToggleButton Exploded { get; }

    public ToggleButton EncodedPreview { get; }

    public Button Exit { get; }

    /// <summary>The scrubber's playhead dragged to a moment (ms).</summary>
    public event Action<double>? ScrubbedTo;

    /// <summary>The drag of the scrubber's playhead let go, at a moment (ms).</summary>
    public event Action<double>? ScrubEnded;

    /// <summary>The scrubber set to a moment by a press beside its playhead or by UI Automation (ms).</summary>
    public event Action<double>? SoughtTo;

    /// <summary>Whether something of it is held: its scrubber's playhead dragged, its Volume pill open, or anything of it with the pointer captured.</summary>
    public bool IsHeld => _dragging || Volume.IsExpanded || IsMouseCaptureWithin;

    /// <summary>The playhead and the length as they are now (ms, and as the readout writes them). A drag of the scrubber keeps its own place.</summary>
    public void ShowTime(double positionMs, double durationMs, string position, string duration)
    {
        _showing = true;
        try
        {
            var end = Math.Max(durationMs, 1);
            if (Scrubber.Maximum != end)
                Scrubber.Maximum = end;
            if (!_dragging)
                Scrubber.Value = Math.Max(0, Math.Min(positionMs, end));
        }
        finally
        {
            _showing = false;
        }

        if (_position.Text != position)
            _position.Text = position;
        if (!string.Equals(_durationShown, duration, StringComparison.Ordinal))
        {
            _durationShown = duration;
            _duration.Text = " / " + duration;
        }
    }

    private void Scrubber_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_showing)
            return;
        if (_dragging)
            ScrubbedTo?.Invoke(e.NewValue);
        else
            SoughtTo?.Invoke(e.NewValue);
    }

    private void LetGoOfScrubber()
    {
        if (!_dragging)
            return;
        _dragging = false;
        ScrubEnded?.Invoke(Scrubber.Value);
    }

    /// <summary>
    /// A press anywhere on the scrubber, not only on its playhead (as the Sequence Overview Scrubber's): the playhead goes to the pointer,
    /// and the press is handed to it, so holding and dragging scrubs from there.
    /// </summary>
    private void Scrubber_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Scrubber.Template?.FindName("PART_Track", Scrubber) is not Track { Thumb: { } thumb } track
            || thumb.IsMouseOver || thumb.IsDragging || Scrubber.Maximum <= Scrubber.Minimum)
        {
            return;
        }

        Scrubber.UpdateLayout();
        var value = track.ValueFromPoint(e.GetPosition(track));
        if (!double.IsFinite(value))
            return;

        Scrubber.Value = Math.Max(Scrubber.Minimum, Math.Min(value, Scrubber.Maximum));
        Scrubber.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent, Source = thumb });
        e.Handled = true;
    }

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
