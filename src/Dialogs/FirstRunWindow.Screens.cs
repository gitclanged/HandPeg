using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using HandPegApp.Docking;
using HandPegApp.Services;
using HandPegApp.Native;
using static HandPegApp.Native.User32;

namespace HandPegApp;

// ----- Two-screen flow: step header, slide transition, live miniature, theme swatches, work-area fit -----
//
// Screen 1 (Workspace, Theme & Core Engines): five layout cards and five theme swatches beside a live miniature of the hovered (else
// chosen) layout/theme (SettingsStagePreview Workspace kind), plus two one-click engine downloads. Screen 2 (Hardware Auto-Tune &
// Default Export): FirstRunWindow.Profile.cs and encoders. Design size 940 x 600 DIP (scaled by UiScale on load), then clamped to the
// monitor work area by FitToWorkArea after UiScale (never double-scaled); screen bodies scroll only when space runs short.
public partial class FirstRunWindow
{
    /// <summary>Current screen: 1 (workspace, theme, engines) or 2 (hardware, export defaults).</summary>
    internal int Screen { get; private set; } = 1;

    // Chosen theme (checked swatch, applied immediately) and hovered layout/theme (null when none).
    private string _chosenTheme = ThemeManager.FollowSystem;
    private string? _hoveredLayout, _hoveredTheme;

    // Built-in layouts by name for the miniature, created lazily on first draw (window-free, MainWindow.BuiltInLayoutStates).
    private IReadOnlyDictionary<string, DockNode>? _layouts;

    // Settings tab pane ids as MainWindow registers them (golden list); layouts are built for these.
    private static readonly IReadOnlyList<string> PreviewTabIds = ["Tab:Color", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];

    private const string ScreenOneTitle = "Workspace, Theme & Core Engines", ScreenTwoTitle = "Hardware Auto-Tune & Default Export";

    private void InitializeScreens()
    {
        FillThemeSwatches();
        StagePreview.Show(StageKind.Workspace, AppSettings.Current);
        ShowStage();

        // Miniature tracks the pointer over cards and swatches (HoverAt is point-based so goldens can drive it).
        MouseMove += (_, e) => HoverAt(e.GetPosition(this));
        MouseLeave += (_, _) => HoverAt(null);

        // Work-area fit after UiScale sizes the window (its class handler precedes this instance handler), and again after a DPI
        // change (following UiScale's Send-priority post).
        Loaded += (_, _) => FitToWorkArea(center: true);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => FitToWorkArea(center: false), DispatcherPriority.Loaded);
        Closed += (_, _) => StopSlide();

        // Download progress shows in the footer on both screens; card buttons are the only trigger (panel Install hidden) and mirror
        // the panel's Retry state.
        Dependencies.InstallButton.Visibility = Visibility.Collapsed;
        Dependencies.ProgressChanged += ShowDownloadProgress;
        Dependencies.RunningChanged += ShowDownloadRunning;
        Dependencies.Refreshed += UpdateTierButtons;
    }

    // ----- The screens and the slide between them -----

    private void Next_Click(object sender, RoutedEventArgs e) => GoToScreen(2);

    private void Back_Click(object sender, RoutedEventArgs e) => GoToScreen(1);

    /// <summary>
    /// Shows a screen: title, lit step, Back on screen 2, Next (default) on 1 / Start Editing on 2; slides in over 180 ms when extra
    /// animations and Windows client-area animations are on.
    /// </summary>
    internal void GoToScreen(int screen)
    {
        screen = screen <= 1 ? 1 : 2;
        if (screen == Screen)
            return;

        var forward = screen > Screen;
        Screen = screen;
        StopSlide();
        var (shown, hidden) = screen == 1 ? (Screen1Panel, Screen2Panel) : (Screen2Panel, Screen1Panel);
        hidden.Visibility = Visibility.Collapsed;
        shown.Visibility = Visibility.Visible;
        HoverAt(null);

        ScreenTitle.Text = screen == 1 ? ScreenOneTitle : ScreenTwoTitle;
        StepText.Text = string.Create(CultureInfo.InvariantCulture, $"Step {screen} of 2");
        StepDot1.SetResourceReference(Shape.FillProperty, screen == 1 ? "AccentBrush" : "ControlBorderBrush");
        StepDot2.SetResourceReference(Shape.FillProperty, screen == 2 ? "AccentBrush" : "ControlBorderBrush");
        BackButton.Visibility = screen == 2 ? Visibility.Visible : Visibility.Hidden;
        NextButton.Visibility = screen == 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsDefault = screen == 1;
        StartButton.Visibility = screen == 2 ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsDefault = screen == 2;
        UpdateStartStatus();

        // Keep keyboard focus in the window on the screen's forward button.
        if (IsKeyboardFocusWithin)
            (screen == 1 ? NextButton : StartButton).Focus();
        Slide(shown, forward);
    }

    /// <summary>Slides and fades the screen in from its side (180 ms ease-out); instant without extra animations.</summary>
    private void Slide(FrameworkElement screen, bool forward)
    {
        if (!IsLoaded || !AppSettings.Current.EnableExtraAnimations || !SystemParameters.ClientAreaAnimation)
            return;

        // Lazily created animated transform (not frozen); final values are set and the animations then released.
        if (screen.RenderTransform is not TranslateTransform move)
            screen.RenderTransform = move = new TranslateTransform();
        (move.X, screen.Opacity) = (0, 1);

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        var slide = new DoubleAnimation(forward ? 40 : -40, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        slide.Freeze();
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        fade.Freeze();
        move.BeginAnimation(TranslateTransform.XProperty, slide);
        screen.BeginAnimation(OpacityProperty, fade);
    }

    // Completes an in-flight slide at its end values (also on close).
    private void StopSlide()
    {
        foreach (var screen in new FrameworkElement[] { Screen1Panel, Screen2Panel })
        {
            screen.BeginAnimation(OpacityProperty, null);
            (screen.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }

    // ----- The live miniature: the layout and theme under the pointer, else the chosen ones -----

    /// <summary>
    /// Hit-tests a window point (DIPs) for the miniature: a card previews its layout, a swatch its theme; elsewhere or null (pointer
    /// left) restores the chosen ones.
    /// </summary>
    internal void HoverAt(Point? inWindow)
    {
        string? layout = null, theme = null;
        if (inWindow is { } point && Screen == 1 && InputHitTest(point) is DependencyObject hit)
        {
            for (var at = hit; at is not null && !ReferenceEquals(at, ScreenHost); at = ParentOf(at))
            {
                if (at is RadioButton { GroupName: "Workspace", Tag: string name })
                {
                    layout = name;
                    break;
                }

                if (at is RadioButton { GroupName: "Theme", Tag: string named })
                {
                    theme = named;
                    break;
                }
            }
        }

        if (layout == _hoveredLayout && theme == _hoveredTheme)
            return;

        (_hoveredLayout, _hoveredTheme) = (layout, theme);
        ShowStage();
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    /// <summary>Redraws the miniature with the hovered-or-chosen layout and theme.</summary>
    private void ShowStage()
    {
        // The first card's Checked fires during construction.
        if (StagePreview is null)
            return;

        var name = _hoveredLayout ?? ChosenWorkspace();
        StagePreview.ThemeOverride = _hoveredTheme ?? _chosenTheme;
        _layouts ??= MainWindow.BuiltInLayoutStates(PreviewTabIds).ToDictionary(l => l.Name, l => l.Layout.Main);
        StagePreview.ShowWorkspace(name, _layouts.TryGetValue(name, out var layout) ? layout : null);
        StagePreview.InvalidateVisual();
    }

    // ----- The theme swatches -----

    /// <summary>
    /// Builds one swatch per ThemeManager.Themes entry: a mini window in its colours (ThemeManager.Look window/panel/accent/text, frozen
    /// brushes) above its name; the chosen one checked.
    /// </summary>
    private void FillThemeSwatches()
    {
        var style = (Style)FindResource("SwatchCard");
        foreach (var theme in ThemeManager.Themes)
        {
            var (tokens, _, _) = ThemeManager.Look(theme);
            SolidColorBrush Of(string key, Color fallback)
            {
                var brush = new SolidColorBrush(tokens.TryGetValue(key, out var color) ? color : fallback);
                brush.Freeze();
                return brush;
            }

            var picture = new Grid { Height = 34, SnapsToDevicePixels = true };
            picture.Children.Add(new Border
            {
                Background = Of("WindowBackgroundBrush", Colors.Black), BorderBrush = Of("ControlBorderBrush", Colors.Gray),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            });
            picture.Children.Add(new Border { Background = Of("PanelBrush", Colors.DimGray), CornerRadius = new CornerRadius(2), Margin = new Thickness(14, 9, 4, 4) });
            picture.Children.Add(new Rectangle
            {
                Fill = Of("AccentBrush", Colors.SteelBlue), Width = 7, Height = 21, RadiusX = 1, RadiusY = 1, Margin = new Thickness(4, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            });
            var text = Of("TextBrush", Colors.White);
            picture.Children.Add(new Rectangle { Fill = text, Width = 24, Height = 2, Margin = new Thickness(19, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
            picture.Children.Add(new Rectangle { Fill = text, Width = 16, Height = 2, Margin = new Thickness(19, 19, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Opacity = 0.6 });

            var label = new TextBlock
            {
                Text = ThemeManager.DisplayName(theme), FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            var swatch = new RadioButton
            {
                Style = style, GroupName = "Theme", Tag = theme, IsChecked = theme == _chosenTheme,
                Content = new StackPanel { Children = { picture, label } },
                ToolTip = theme == ThemeManager.WindowsSystem ? "Follows Windows' own light or dark mode and accent colour." : null,
            };
            AutomationProperties.SetName(swatch, $"{theme} Theme");
            swatch.Checked += ThemeSwatch_Checked;
            ThemeSwatches.Items.Add(swatch);
        }
    }

    /// <summary>Applies a swatch's theme immediately (live preview, including this window).</summary>
    private void ThemeSwatch_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string theme } || theme == _chosenTheme)
            return;

        _chosenTheme = theme;
        ShowStage();
        AppSettings.Current.Theme = theme;
        ThemeManager.Apply();
    }

    /// <summary>Golden hook: swatch themes in order and the checked one.</summary>
    internal IReadOnlyList<(string Theme, bool Checked)> Swatches =>
        [.. ThemeSwatches.Items.OfType<RadioButton>().Select(s => ((string)s.Tag, s.IsChecked == true))];

    // ----- The core engines: a package at one click -----

    /// <summary>Selects a package and starts its download (progress mirrored in the footer).</summary>
    private void TierDownload_Click(object sender, RoutedEventArgs e) => Faults.Forget(TierDownload_ClickAsync(sender, e), nameof(TierDownload_Click));

    private async Task TierDownload_ClickAsync(object sender, RoutedEventArgs e)
    {
        (ReferenceEquals(sender, FullDownloadButton) ? EditorTier : MinimalTier).IsChecked = true;
        ApplyTier();
        await Dependencies.InstallAsync();
    }

    // Card buttons: disabled during a download; "Retry" after a failed online check (matching the panel).
    private void UpdateTierButtons()
    {
        var label = Equals(Dependencies.InstallButton.Content, "Retry") ? "Retry" : "Download";
        foreach (var button in new[] { MinimalDownloadButton, FullDownloadButton })
        {
            button.Content = label;
            button.IsEnabled = !Dependencies.IsRunning;
        }
    }

    private void ShowDownloadRunning()
    {
        if (Dependencies.IsRunning)
            ShowDownloadProgress(0);
        FooterProgressPanel.Visibility = Dependencies.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        UpdateTierButtons();
        UpdateStartStatus();
    }

    private void ShowDownloadProgress(double fraction)
    {
        FooterProgress.Value = Math.Clamp(fraction, 0, 1);
        FooterProgressText.Text = string.Create(CultureInfo.InvariantCulture, $"Downloading the core engines  {Math.Clamp(fraction, 0, 1) * 100:0}%");
    }

    // Screen 2 hint explaining why Start Editing is disabled during a download.
    private void UpdateStartStatus()
    {
        if (_finishing)
            return;

        var waiting = Screen == 2 && Dependencies.IsRunning;
        StartStatusText.Text = waiting ? "Start Editing waits for the download" : "";
        StartStatusText.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- The fit to the work area -----

    /// <summary>
    /// Constrains the UiScale-sized window to its monitor work area: sets MaxWidth/MaxHeight in window DIPs (after UiScale, never
    /// double-scaled), recentres CenterScreen windows, otherwise nudges only as needed (CenterOwner from Settings). Manual placement
    /// (golden off-screen windows) is left alone.
    /// </summary>
    private void FitToWorkArea(bool center)
    {
        var work = WorkAreaHere();
        if (work.IsEmpty || work.Width < 1 || work.Height < 1)
            return;

        // Use the design size at the root LayoutTransform scale, not the current size, so a window shrunk for a small work area grows
        // back to 940 x 600 on a larger monitor.
        var scale = (Content as FrameworkElement)?.LayoutTransform is ScaleTransform transform ? transform.ScaleX : 1;
        var placed = FitTo(work, DesignSizeAt(scale));
        (MaxWidth, MaxHeight) = (work.Width, work.Height);
        (Width, Height) = (placed.Width, placed.Height);
        if (WindowStartupLocation == WindowStartupLocation.Manual || !double.IsFinite(Left) || !double.IsFinite(Top))
            return;

        if (center && WindowStartupLocation == WindowStartupLocation.CenterScreen)
        {
            (Left, Top) = (placed.Left, placed.Top);
            return;
        }

        // Max/Min rather than Clamp: a window filling the work area can round to min > max, which Clamp throws on.
        Left = Math.Max(work.Left, Math.Min(Left, work.Right - placed.Width));
        Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - placed.Height));
    }

    /// <summary>Design size (940 x 600 DIP from XAML) at a UI scale (non-positive treated as 1).</summary>
    internal static Size DesignSizeAt(double uiScale)
    {
        var scale = double.IsFinite(uiScale) && uiScale > 0 ? uiScale : 1;
        return new Size(DesignWidth * scale, DesignHeight * scale);
    }

    /// <summary>Design size in DIPs, matching FirstRunWindow.xaml.</summary>
    internal const double DesignWidth = 940, DesignHeight = 600;

    /// <summary>Rect of <paramref name="size"/> clamped to and centred in the work area.</summary>
    internal static Rect FitTo(Rect workArea, Size size)
    {
        var width = Math.Min(size.Width, workArea.Width);
        var height = Math.Min(size.Height, workArea.Height);
        return new Rect(workArea.Left + (workArea.Width - width) / 2, workArea.Top + (workArea.Height - height) / 2, width, height);
    }

    /// <summary>Work area (window DIPs) of the window's nearest monitor; primary monitor before the HWND exists.</summary>
    internal Rect WorkAreaHere()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = handle == 0 ? 0 : MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;

        var scale = UiScale.MonitorScale(this);
        return new Rect(info.Work.Left / scale, info.Work.Top / scale, (info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
    }

    private const uint MonitorDefaultToNearest = 2;

}
