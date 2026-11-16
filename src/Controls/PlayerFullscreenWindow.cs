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
using static HandPegApp.UiBrushes;
using HandPegApp.Native;
using static HandPegApp.Native.User32;

namespace HandPegApp;

/// <summary>
/// Fullscreen Preview window (MainWindow.Fullscreen.cs): borderless, black, owned by the main window, covering a whole monitor including
/// the taskbar. The player surface is reparented into <see cref="Host"/> (same swap chain: no copy, no second engine) and returned before
/// close. No frame handling (no chrome, hook or NC messages). Re-covers its monitor after any Windows move or DPI change; raises
/// <see cref="DisplayChanged"/> on display changes (ending fullscreen). Non-owner closes (Alt+F4) are cancelled and raise
/// <see cref="ExitAsked"/>, so the surface is never destroyed with it.
/// </summary>
public sealed partial class PlayerFullscreenWindow : Window
{
    private const int WmDisplayChange = 0x007E, WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200, MonitorDefaultToNearest = 2;

    // Covered monitor rect (physical pixels); placed flag (Windows' own moves during HWND creation are ignored until set); owner close permit.
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

        // Window size comes from the monitor; only content takes UI scale.
        UiScale.SetKeepsItsSize(this, true);
        LocationChanged += (_, _) => CoverOwnMonitor();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(CoverOwnMonitor);
        Loaded += (_, _) => CoverOwnMonitor();
    }

    /// <summary>Container for the reparented player surface.</summary>
    public Grid Host { get; }

    /// <summary>Raised on a non-owner close request (Alt+F4); the owner should exit fullscreen.</summary>
    public event Action? ExitAsked;

    /// <summary>Raised on WM_DISPLAYCHANGE (monitor added/removed, resolution change); the owner should exit fullscreen.</summary>
    public event Action? DisplayChanged;

    /// <summary>Called after the owner reclaims the player surface; allows the close.</summary>
    internal void LetClose() => _mayClose = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(Message);

        // Place on the target monitor at HWND creation, before first show.
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
        else if (msg == WmWindowPosChanging && _placed && lParam != 0)
            SnapToMonitor(lParam);
        return 0;
    }

    /// <summary>
    /// WM_WINDOWPOSCHANGING: rewrites any proposed move/resize (WPF show sizing, DPI-change suggestion, cross-monitor move) to the full
    /// physical rect of the monitor it lands on, so the window never appears at a scaled or default size regardless of DPI or UI scale.
    /// </summary>
    private void SnapToMonitor(nint lParam)
    {
        var position = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((position.Flags & (SwpNoMove | SwpNoSize)) == (SwpNoMove | SwpNoSize))
            return;

        // Move-only keeps the monitor size; resize-only keeps the monitor origin.
        var (x, y) = (position.Flags & SwpNoMove) != 0 ? (_monitor.X, _monitor.Y) : (position.X, position.Y);
        var (width, height) = (position.Flags & SwpNoSize) != 0 ? (_monitor.Width, _monitor.Height) : (position.Width, position.Height);
        var proposed = new NativeRect { Left = x, Top = y, Right = x + Math.Max(1, width), Bottom = y + Math.Max(1, height) };
        if (MonitorArea(MonitorFromRect(in proposed, MonitorDefaultToNearest)) is not { } area || area.Width <= 0 || area.Height <= 0)
            return;

        _monitor = area;
        (position.X, position.Y, position.Width, position.Height) = (area.X, area.Y, area.Width, area.Height);
        position.Flags &= ~(SwpNoMove | SwpNoSize);
        Marshal.StructureToPtr(position, lParam, false);
    }

    // Re-covers the current monitor after Windows moved the window (monitor or DPI change).
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

    /// <summary>Full rect of the monitor containing most of <paramref name="pixels"/> (physical), or the nearest monitor.</summary>
    internal static Int32Rect MonitorAround(Rect pixels)
    {
        var rect = new NativeRect
        {
            Left = (int)Math.Floor(pixels.Left), Top = (int)Math.Floor(pixels.Top),
            Right = (int)Math.Ceiling(pixels.Right), Bottom = (int)Math.Ceiling(pixels.Bottom),
        };
        return MonitorArea(MonitorFromRect(in rect, MonitorDefaultToNearest))
            ?? new Int32Rect(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
    }

    /// <summary>True when <paramref name="other"/> is on this window's monitor.</summary>
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

}

/// <summary>
/// Player HUD building blocks: the Fullscreen button over the docked player and the fullscreen transport bar, theme-independent dark
/// overlays (like the Live Encoded Preview RAW / ENCODED pills) built on first use, plus idle timeouts and fades.
/// </summary>
internal static class HudChrome
{
    /// <summary>
    /// Idle timeouts (ms) before the Fullscreen button and the fullscreen bar hide, and the recheck interval while a HUD is held (hovered,
    /// scrubber dragged, Volume pill open).
    /// </summary>
    internal const double ButtonRestMs = 1500, BarRestMs = 1800, RecheckMs = 250;

    // Fade-in and fade-out durations (ms).
    private const double FadeInMs = 120, FadeOutMs = 150;

    internal static readonly SolidColorBrush Face = Frozen(0xCC, 0x18, 0x18, 0x1C), Edge = Frozen(0xFF, 0x38, 0x38, 0x42),
        Text = Frozen(0xFF, 0xF2, 0xF2, 0xF2), Muted = Frozen(0xFF, 0x9A, 0x9A, 0xA4), Lit = Frozen(0xE6, 0x2C, 0x2C, 0x36),
        Pressed = Frozen(0xF0, 0x3A, 0x3A, 0x46), Chosen = Frozen(0xCC, 0x00, 0x78, 0xD7);

    private static DoubleAnimation? _fadeIn, _fadeOut;
    private static Style? _button, _toggle;

    /// <summary>Fullscreen button visible: hovered, or pointer moved over the player within <see cref="ButtonRestMs"/>.</summary>
    internal static bool ButtonShown(double sinceMoveMs, bool overButton) => overButton || sinceMoveMs < ButtonRestMs;

    /// <summary>
    /// Fullscreen bar visible: hovered, held (scrubber drag, Volume pill open), or pointer moved within <see cref="BarRestMs"/>.
    /// </summary>
    internal static bool BarShown(double sinceMoveMs, bool overBar, bool held) => overBar || held || sinceMoveMs < BarRestMs;

    /// <summary>
    /// Fullscreen button for the docked player: top-right, 10 DIP margin, Segoe Full Screen glyph on a dark chip; collapsed and not
    /// hit-testable until faded in.
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
        KeyTip.SetText(button, "Fullscreen Preview{keys}: the player across its monitor; Esc comes back.");
        return button;
    }

    /// <summary>
    /// HUD style for a button or toggle (<paramref name="basedOn"/> keeps that style's content triggers, e.g. Play/Pause glyph): light glyph
    /// on transparent, lit on hover, accent when checked; never focusable. Unbased styles are cached.
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
        style.Setters.Add(new Setter(Control.FontFamilyProperty, Glyphs.Font));
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

    // Rounded border template: hover and pressed fills, accent when a toggle is checked, 40% opacity when disabled.
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
    /// Fades a HUD element (120 ms in, 150 ms out; immediate with <paramref name="instant"/> or when Windows animations are off). Hit-testable
    /// only while shown; collapsed after fading out.
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
            // Collapse on completion unless re-shown meanwhile.
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

}

/// <summary>
/// Fullscreen Preview transport: a dark rounded bar along the picture's bottom (max 900 DIPs) with Play/Pause, the Volume pill (opens
/// rightward over the scrubber start), playhead scrubber and time, 3D Exploded View and Live Encoded Preview toggles, and Exit. Nothing is
/// focusable (Space stays Play/Pause). The owner sets DataContext to the view model (volume, mute, Live Encoded Preview, Play glyph),
/// binds the 3D toggle, and pushes time via <see cref="ShowTime"/> only while shown.
/// </summary>
public sealed class FullscreenTransportBar : Border
{
    // 3D Exploded View icon (three separated planes), matching the playback bar toggle.
    private static readonly Geometry PlanesIcon = FrozenGeometry("M 2,3 L 9,0.8 L 16,3 L 9,5.2 Z M 2,8 L 9,5.8 L 16,8 L 9,10.2 Z M 2,13 L 9,10.8 L 16,13 L 9,15.2 Z");

    private readonly Run _position = new(), _duration = new();
    private string? _durationShown;

    // Re-entrancy flag while ShowTime sets the scrubber, and thumb-drag-in-progress flag.
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

        // Play/Pause based on the playback bar style (glyph and name follow IsPlaying), in HUD chrome.
        PlayPause = new Button { Style = HudChrome.ButtonStyle(typeof(Button), playPauseStyle), Padding = new Thickness(12, 6, 12, 6) };
        if (playPauseStyle is null)
        {
            PlayPause.Content = "\uE768";
            AutomationProperties.SetName(PlayPause, "Play");
        }

        // Volume pill as on the playback bar; the speaker toggles player mute.
        Volume = new ExpandablePillSlider
        {
            Icon = PillIcon.Speaker, Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 20, DefaultValue = 100, ReadoutFormat = "{0:0}%",
            IsIconToggle = true, IconAutomationName = "Mute Player", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Volume. Click the speaker to mute the player (the export is not affected). Double-click the track to reset.",

            // HUD colours, not theme colours: the bar is dark in every theme.
            IconBrush = HudChrome.Text, SurfaceBrush = HudChrome.Lit, EdgeBrush = HudChrome.Edge, TrackBrush = HudChrome.Muted,
        };
        AutomationProperties.SetName(Volume, "Volume");
        Volume.SetBinding(ExpandablePillSlider.ValueProperty, new Binding("Volume"));
        Volume.SetBinding(ExpandablePillSlider.IsIconCheckedProperty, new Binding("IsPlayerMuted"));

        // Z-order above the scrubber, which the open pill overlaps.
        Panel.SetZIndex(Volume, 1);

        Scrubber = new Slider { Minimum = 0, Maximum = 1, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        AutomationProperties.SetName(Scrubber, "Playhead");
        Scrubber.ValueChanged += Scrubber_ValueChanged;
        Scrubber.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true), handledEventsToo: true);
        Scrubber.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => LetGoOfScrubber()), handledEventsToo: true);
        Scrubber.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Scrubber_PreviewMouseLeftButtonDown), handledEventsToo: true);

        // Time readout with fixed-width digits (TimeReadout); duration muted.
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
        KeyTip.SetText(Exploded, "3D Exploded View{keys}: the layer stack in 3D; Esc or this button goes back.");

        EncodedPreview = new ToggleButton { Style = HudChrome.ButtonStyle(typeof(ToggleButton), null), Content = Shell.LoopbackPreviewController.LoopbackGlyph, Margin = new Thickness(2, 0, 0, 0) };
        AutomationProperties.SetName(EncodedPreview, Shell.LoopbackPreviewController.LoopbackName);
        EncodedPreview.ToolTip = Shell.LoopbackPreviewController.LoopbackName + ": the picture as the export's encoder would leave it, live.";
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

    /// <summary>Playhead scrubber, 0 to sequence length in ms.</summary>
    public Slider Scrubber { get; }

    /// <summary>Position / duration text, formatted like the playback bar readout.</summary>
    public TextBlock Time { get; }

    public ToggleButton Exploded { get; }

    public ToggleButton EncodedPreview { get; }

    public Button Exit { get; }

    /// <summary>Raised during a thumb drag with the time in ms.</summary>
    public event Action<double>? ScrubbedTo;

    /// <summary>Raised on thumb drag release with the final time in ms.</summary>
    public event Action<double>? ScrubEnded;

    /// <summary>Raised when the value is set outside a drag (track press or UI Automation), in ms.</summary>
    public event Action<double>? SoughtTo;

    /// <summary>True while the thumb is dragged, the Volume pill is open, or any child holds mouse capture.</summary>
    public bool IsHeld => _dragging || Volume.IsExpanded || IsMouseCaptureWithin;

    /// <summary>Updates scrubber (ms) and readout text; the scrubber value is left alone during a thumb drag.</summary>
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
    /// Track press (like the Overview Scrubber): jumps the thumb to the pointer and forwards the press to it, so drag continues from there.
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
