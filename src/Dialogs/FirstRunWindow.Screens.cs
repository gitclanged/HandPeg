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

namespace HandPegApp;

// ----- The two screens (Pass 5.5A): the header's steps, the slide between them, the live miniature, the theme swatches, the fit -----
//
// Screen 1, Workspace, Theme & Core Engines: the five layout cards and five theme swatches beside a live miniature of the layout and
// theme under the pointer (else the chosen ones: SettingsStagePreview's Workspace kind), and the two engine packages, each a click to
// download. Screen 2, Hardware Auto-Tune & Default Export (FirstRunWindow.Profile.cs and the encoders). The window is 940 x 600 of
// HandPeg's DIPs (UiScale sizes it as every dialog as it loads), then kept inside the work area of its monitor here (FitToWorkArea:
// after UiScale, so never scaled twice); each screen's body scrolls only where that leaves too little room.
public partial class FirstRunWindow
{
    /// <summary>The screen shown: 1 (workspace, theme, engines) or 2 (hardware, export defaults).</summary>
    internal int Screen { get; private set; } = 1;

    // The theme chosen (its swatch checked; put into effect at once), and the layout and theme under the pointer (null: none).
    private string _chosenTheme = ThemeManager.FollowSystem;
    private string? _hoveredLayout, _hoveredTheme;

    // The built-in layouts the miniature draws, by name: made the first time the miniature is drawn (window-free, MainWindow.BuiltInLayoutStates).
    private IReadOnlyDictionary<string, DockNode>? _layouts;

    // The settings tabs' pane ids, as the main window registers them (the goldens' list): the layouts are made for them.
    private static readonly IReadOnlyList<string> PreviewTabIds = ["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];

    private const string ScreenOneTitle = "Workspace, Theme & Core Engines", ScreenTwoTitle = "Hardware Auto-Tune & Default Export";

    private void InitializeScreens()
    {
        FillThemeSwatches();
        StagePreview.Show(StageKind.Workspace, AppSettings.Current);
        ShowStage();

        // The miniature follows the pointer over the cards and swatches (HoverAt: point-based, for the goldens too).
        MouseMove += (_, e) => HoverAt(e.GetPosition(this));
        MouseLeave += (_, _) => HoverAt(null);

        // The fit to the work area: once UiScale has sized the window (its class handler runs before this instance handler), and
        // again after a move onto a monitor of another scale (after UiScale's own, which it posts at Send).
        Loaded += (_, _) => FitToWorkArea(center: true);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => FitToWorkArea(center: false), DispatcherPriority.Loaded);
        Closed += (_, _) => StopSlide();

        // The download: its progress in the footer, on either screen; the cards' buttons are the one way to start it here (the
        // panel's own Install button is not shown), and say Retry when the panel would.
        Dependencies.InstallButton.Visibility = Visibility.Collapsed;
        Dependencies.ProgressChanged += ShowDownloadProgress;
        Dependencies.RunningChanged += ShowDownloadRunning;
        Dependencies.Refreshed += UpdateTierButtons;
    }

    // ----- The screens and the slide between them -----

    private void Next_Click(object sender, RoutedEventArgs e) => GoToScreen(2);

    private void Back_Click(object sender, RoutedEventArgs e) => GoToScreen(1);

    /// <summary>
    /// Shows a screen: its title, its step lit, Back on the second, Next (the default button) on the first and Start Editing on the
    /// second; the screen slides in over 180 ms when the extra animations are on (and Windows animates windows' insides).
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

        // The keyboard stays in the window: on the button that goes on from the screen shown.
        if (IsKeyboardFocusWithin)
            (screen == 1 ? NextButton : StartButton).Focus();
        Slide(shown, forward);
    }

    /// <summary>The screen shown comes in from the side it lies on and fades up: 180 ms, eased out; at once without the extra animations.</summary>
    private void Slide(FrameworkElement screen, bool forward)
    {
        if (!IsLoaded || !AppSettings.Current.EnableExtraAnimations || !SystemParameters.ClientAreaAnimation)
            return;

        // Its own transform (animated, so never frozen), made the first time it slides; the final values set, the animations then let go.
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

    // A slide under way ends where it was going (also when the window closes).
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
    /// What is under a point of the window (its own DIPs), for the miniature: a layout card shows its layout, a theme swatch its theme;
    /// anywhere else (or null: the pointer left) the chosen layout and theme come back.
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

    /// <summary>The miniature drawn again: the layout under the pointer or chosen, in the theme under the pointer or chosen.</summary>
    private void ShowStage()
    {
        // Raised by the first card's Checked while the window is still being built.
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
    /// One swatch for each theme (ThemeManager.Themes): a little window in the theme's own colours (ThemeManager.Look: its window, a
    /// panel, its accent and its text, every brush frozen) over its name; the chosen one checked.
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

    /// <summary>The theme of a swatch, put into effect at once so it can be chosen by looking (the window's own colours change with it).</summary>
    private void ThemeSwatch_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string theme } || theme == _chosenTheme)
            return;

        _chosenTheme = theme;
        ShowStage();
        AppSettings.Current.Theme = theme;
        ThemeManager.Apply();
    }

    /// <summary>The swatches, for the goldens: their themes in order, and which is checked.</summary>
    internal IReadOnlyList<(string Theme, bool Checked)> Swatches =>
        [.. ThemeSwatches.Items.OfType<RadioButton>().Select(s => ((string)s.Tag, s.IsChecked == true))];

    // ----- The core engines: a package at one click -----

    /// <summary>A package's Download: the package chosen, and its download started (the panel's progress shown in the footer too).</summary>
    private async void TierDownload_Click(object sender, RoutedEventArgs e)
    {
        (ReferenceEquals(sender, FullDownloadButton) ? EditorTier : MinimalTier).IsChecked = true;
        ApplyTier();
        await Dependencies.InstallAsync();
    }

    // The cards' buttons: off while a download runs; Retry after a look online that failed (as the panel's own button would say).
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

    // On the second screen while a download runs: why Start Editing waits.
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
    /// Keeps the window (as UiScale has sized it) inside the work area of its monitor: no wider or taller (MaxWidth and MaxHeight set
    /// here, in the window's own DIPs, after UiScale: never scaled twice), centred in it when it was to be centred on the screen, else
    /// moved inside it only as far as it must be (opened from Settings: centred on its owner). A window placed by hand (the goldens'
    /// off-screen one) keeps its place.
    /// </summary>
    private void FitToWorkArea(bool center)
    {
        var work = WorkAreaHere();
        if (work.IsEmpty || work.Width < 1 || work.Height < 1)
            return;

        // 5.5A review: the design size at the window's UI scale (its root's LayoutTransform, as UiScale gives it), never its current size:
        // a window fitted once to a small work area grows back to 940 x 600 on a monitor with the room.
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

        // Inside it, never past its far edges (Max/Min, not Clamp: the window may be exactly as wide as the room).
        Left = Math.Max(work.Left, Math.Min(Left, work.Right - placed.Width));
        Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - placed.Height));
    }

    /// <summary>The window's design size (the XAML's 940 x 600 HandPeg DIPs) at a UI scale (1 when it is not a positive number).</summary>
    internal static Size DesignSizeAt(double uiScale)
    {
        var scale = double.IsFinite(uiScale) && uiScale > 0 ? uiScale : 1;
        return new Size(DesignWidth * scale, DesignHeight * scale);
    }

    /// <summary>The window's size in HandPeg DIPs, as FirstRunWindow.xaml gives it (5.5A).</summary>
    internal const double DesignWidth = 940, DesignHeight = 600;

    /// <summary>A window of <paramref name="size"/> in a work area: no larger than it, centred in it.</summary>
    internal static Rect FitTo(Rect workArea, Size size)
    {
        var width = Math.Min(size.Width, workArea.Width);
        var height = Math.Min(size.Height, workArea.Height);
        return new Rect(workArea.Left + (workArea.Width - width) / 2, workArea.Top + (workArea.Height - height) / 2, width, height);
    }

    /// <summary>The work area of the monitor the window is on (or nearest to), in its own DIPs; the main monitor's before it has a handle.</summary>
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRect Monitor, Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
