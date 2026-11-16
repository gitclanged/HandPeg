using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Shell;

namespace HandPegApp;

// Full-window settings screen: covers the editor, with a category rail and a live preview stage for the hovered/focused option.
public partial class SettingsWindow
{
    // ----- Over the editor, as a full-window screen -----

    /// <summary>Matches the editor's bounds and maximized state on its monitor.</summary>
    public void CoverWindow(Window owner)
    {
        // Maximized windows report restored bounds, which are on the same monitor this window maximizes to.
        var bounds = owner.WindowState == WindowState.Normal ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight) : owner.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (bounds.Left, bounds.Top, Math.Max(bounds.Width, MinWidth), Math.Max(bounds.Height, MinHeight));
        WindowState = owner.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    // Escape saves and returns like Back; in a non-empty search box it clears first. Controls handling their own Escape (open lists,
    // cell edits) mark it handled.
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
            SearchBox.Clear();
        else
            Save_Click(BackButton, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ----- Title bar (mirrors the editor's status and progress) -----

    /// <summary>
    /// Title bar overlaying the editor's: hidden until a frame exists, inset while maximized, mirroring status and work progress.
    /// Uses WindowFrame.FrameHook so Windows never paints a classic caption or frame.
    /// </summary>
    private void InitializeTitleBar()
    {
        SourceInitialized += (_, _) =>
        {
            ThemeManager.SetCloaked(this, true);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowFrame.FrameHook);

            // Work area change (taskbar auto-hide toggled): re-inset after Windows repositions, as the editor's TitleBarHook does.
            source?.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool _) =>
            {
                if (message == 0x001A && wParam == 0x002F)
                    Dispatcher.BeginInvoke(() => ApplyFrame(), System.Windows.Threading.DispatcherPriority.Background);
                return IntPtr.Zero;
            });
            ApplyFrame();
        };
        ContentRendered += (_, _) => ThemeManager.SetCloaked(this, false);

        // CoverWindow maximizes before the HWND exists; frame metrics are only available from here.
        Loaded += (_, _) => ApplyFrame();
        StateChanged += (_, _) => ApplyFrame(placeLater: true);
        DpiChanged += (_, _) => ApplyFrame();

        // Maximized move to another monitor (Win+Shift+Arrow): re-inset after placement.
        LocationChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                Dispatcher.BeginInvoke(() => ApplyFrame(), System.Windows.Threading.DispatcherPriority.Background);
        };
        TitleBar.SizeChanged += (_, _) =>
        {
            FitTitleStatus();
            UpdateTitleProgress();
        };

        // The work summary is app-lifetime: unsubscribe on close or this window leaks.
        WorkScheduler.Summary.PropertyChanged += WorkSummary_PropertyChanged;
        Closed += (_, _) => WorkScheduler.Summary.PropertyChanged -= WorkSummary_PropertyChanged;
        UpdateTitleStatus();
    }

    // Maximized self-framed windows overhang the screen, so content is inset; with an auto-hide taskbar it is placed short instead
    // (WindowFrame.MaximizedPlace; CoverWindow maximizes before the hook). Corners square when maximized and round when restored
    // (ThemeManager.ApplyCorners).
    private void ApplyFrame() => ApplyFrame(placeLater: false);

    // placeLater: from a state change, inside WM_SIZE (WindowFrame.KeepClearOfHiddenTaskbarNow).
    private void ApplyFrame(bool placeLater)
    {
        var maximized = WindowState == WindowState.Maximized;
        if (maximized)
            WindowFrame.KeepClearOfHiddenTaskbarNow(new WindowInteropHelper(this).Handle, placeLater);
        ThemeManager.ApplyCorners(this);
        ScreenRoot.Margin = maximized ? WindowFrame.MaximizedFrameInset(this) : new Thickness(0);
        MaximizeButton.Content = maximized ? Glyphs.ChromeRestore : Glyphs.ChromeMaximize;
        MaximizeButton.ToolTip = maximized ? "Restore Down" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void WorkSummary_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkSummary.Percent) or nameof(WorkSummary.IsIndeterminate))
            UpdateTitleProgress();
        else if (e.PropertyName is nameof(WorkSummary.IsVisible) or nameof(WorkSummary.Title) or nameof(WorkSummary.PercentText)
                 or nameof(WorkSummary.MoreText) or nameof(WorkSummary.ToolTip))
            UpdateTitleStatus();
    }

    /// <summary>Centre text: current work (MainWindow.TitleWorkText), else the editor's last status.</summary>
    private void UpdateTitleStatus()
    {
        var work = WorkScheduler.Summary;
        var status = _viewModel.StatusText ?? "";
        TitleStatusText.Text = work.IsVisible ? MainWindow.TitleWorkText(work) : status;
        TitleStatus.ToolTip = work.IsVisible ? work.ToolTip.Length > 0 ? work.ToolTip : null : status.Length > 0 ? status : null;
        TitleProgress.Visibility = work.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleProgress();
    }

    /// <summary>
    /// Progress wash and accent rail: proportional to progress, or full-width and fainter when indeterminate (as
    /// MainWindow.UpdateTitleProgress).
    /// </summary>
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

    // Wash and rail opacities (determinate/indeterminate), matching MainWindow.TitleBar.cs.
    private const double TitleProgressStrength = 0.16, TitleProgressFaint = 0.08, TitleProgressRailFaint = 0.5;

    // Keep the status centred between the end elements without overlapping them.
    private void FitTitleStatus() =>
        TitleStatus.MaxWidth = Math.Max(0, TitleBar.ActualWidth - 2 * Math.Max(TitleLeft.ActualWidth, TitleRight.ActualWidth));

    // ----- The category rail -----

    // Each category's icon: lines on a 16 x 16 square, stroked in the rail's text colour.
    private static readonly Dictionary<SettingsCategory, string> RailIcons = new()
    {
        [SettingsCategory.General] = "M2,4.5 H14 M2,8 H14 M2,11.5 H14 M5,2.8 V6.2 M10.5,6.3 V9.7 M7,9.8 V13.2",
        [SettingsCategory.TimelinePlayer] = "M2,4.5 H9 V7.5 H2 Z M6,9.5 H14 V12.5 H6 Z M11.5,1.5 V14.5",
        [SettingsCategory.PreviewCache] = "M3,4 C3,2.3 13,2.3 13,4 V12 C13,13.7 3,13.7 3,12 Z M3,4 C3,5.7 13,5.7 13,4 M3,8 C3,9.7 13,9.7 13,8",
        [SettingsCategory.ExportHardware] = "M4.5,4.5 H11.5 V11.5 H4.5 Z M6.5,6.5 H9.5 V9.5 H6.5 Z M6,2 V4.5 M8,2 V4.5 M10,2 V4.5 M6,11.5 V14 M8,11.5 V14 M10,11.5 V14 M2,6 H4.5 M2,8 H4.5 M2,10 H4.5 M11.5,6 H14 M11.5,8 H14 M11.5,10 H14",
        [SettingsCategory.AutoCaptions] = "M1.5,3.5 H14.5 V12.5 H1.5 Z M7,6.3 C5,5.4 3.8,6.4 3.8,8 C3.8,9.6 5,10.6 7,9.7 M12.2,6.3 C10.2,5.4 9,6.4 9,8 C9,9.6 10.2,10.6 12.2,9.7",
        [SettingsCategory.Automation] = "M9,1.5 L3.5,9 H8 L7,14.5 L12.5,7 H8 Z",
        // A keyboard: its outline, two rows of keys and the space bar.
        [SettingsCategory.Keybinds] = "M1.5,4 H14.5 V12 H1.5 Z M4,6.5 H5 M7,6.5 H8 M10,6.5 H11 M5.5,8.5 H6.5 M8.5,8.5 H9.5 M5,10.3 H11",
        [SettingsCategory.Tools] = "M2.5,9.5 V13.5 H13.5 V9.5 M8,2 V10 M5,7 L8,10 L11,7",
    };

    private static readonly Dictionary<SettingsCategory, Geometry> RailGeometries = [];

    private static Geometry RailIcon(SettingsCategory category)
    {
        if (!RailGeometries.TryGetValue(category, out var geometry))
        {
            geometry = Geometry.Parse(RailIcons.GetValueOrDefault(category, "M6,8 A2,2 0 1 1 10,8 A2,2 0 1 1 6,8"));
            geometry.Freeze();
            RailGeometries[category] = geometry;
        }

        return geometry;
    }

    /// <summary>
    /// A tab's stable category, from its XAML <c>Tag</c> (never its header text, which may be relabelled or translated; Codex BAD-05);
    /// <see cref="SettingsCategory.None"/> for an untagged tab.
    /// </summary>
    internal static SettingsCategory CategoryOf(TabItem? tab) =>
        tab?.Tag is string id && Enum.TryParse<SettingsCategory>(id, out var category) && Enum.IsDefined(category) ? category : SettingsCategory.None;

    /// <summary>The stage picture of a category (pure).</summary>
    internal static StageKind StageKindOf(SettingsCategory category) => category switch
    {
        SettingsCategory.General => StageKind.Appearance,
        SettingsCategory.TimelinePlayer => StageKind.Timeline,
        SettingsCategory.PreviewCache => StageKind.Cache,
        SettingsCategory.ExportHardware => StageKind.Hardware,
        SettingsCategory.AutoCaptions => StageKind.Captions,
        SettingsCategory.Keybinds => StageKind.Keybinds,
        _ => StageKind.None,
    };

    // A category for each tab: its icon and header, hidden with it when the search leaves the tab nothing.
    private void BuildCategoryRail()
    {
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            var header = tab.Header as string ?? "";
            var icon = new Shapes.Path
            {
                Data = RailIcon(CategoryOf(tab)), Width = 16, Height = 16, StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            icon.SetBinding(Shapes.Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });

            var item = new ListBoxItem
            {
                Tag = tab,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { icon, new TextBlock { Text = header, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            AutomationProperties.SetName(item, header);
            item.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = tab });
            CategoryRail.Items.Add(item);
        }

        if (SettingsTabs.SelectedIndex < 0 && SettingsTabs.Items.Count > 0)
            SettingsTabs.SelectedIndex = 0;
        CategoryRail.SelectedIndex = SettingsTabs.SelectedIndex;
    }

    private void CategoryRail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryRail.SelectedItem is ListBoxItem { Tag: TabItem tab } && !ReferenceEquals(SettingsTabs.SelectedItem, tab))
            SettingsTabs.SelectedItem = tab;
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The pages' own lists (every combo box) announce their selections through here as well.
        if (!ReferenceEquals(e.OriginalSource, SettingsTabs))
            return;

        CategoryRail.SelectedIndex = SettingsTabs.SelectedIndex;
        if (SettingsTabs.SelectedItem is TabItem tab)
            ShowStage(tab, null);
    }

    // ----- The live preview stage -----

    // What each category is about, for the stage when the category is chosen and no option is looked at yet.
    private static readonly Dictionary<SettingsCategory, string> CategoryIntros = new()
    {
        [SettingsCategory.General] = "How HandPeg looks, how it starts, how it keeps itself and its tools up to date, and how it looks after a project and its crash reports.",
        [SettingsCategory.TimelinePlayer] = "How the timeline is drawn, how it snaps and edits, the hover previews above it, and the player.",
        [SettingsCategory.PreviewCache] = "What is kept on disk and how much of it, proxies of heavy videos, the media browser and downloads.",
        [SettingsCategory.ExportHardware] = "The encoding a new export starts with, which graphics card does what (and the test that tunes it), and how much of the computer background work may take.",
        [SettingsCategory.AutoCaptions] = "The speech model that writes captions, how it listens, Remove Dead Air, and the style new captions start with.",
        [SettingsCategory.Automation] = "Smart rules for files that are dropped or loaded, the targets of the drop overlay, and the Social Sharing Squisher's presets.",
        [SettingsCategory.Keybinds] = "What the Lightning Dial's slices hold, every key of the main window (from a preset or set by hand), and the Quick-Start Tour.",
        [SettingsCategory.Tools] = "The programs HandPeg downloads and where they come from, SponsorBlock, and getting settings and projects in and out.",
    };

    // Editor region (MainWindow.StageMirror) mirrored for each category section (null section = category default); unlisted
    // sections show only their picture.
    private static readonly Dictionary<(SettingsCategory Category, string? Section), string> StageMirrors = new()
    {
        [(SettingsCategory.General, null)] = "Window",
        [(SettingsCategory.General, "Appearance")] = "Window",
        [(SettingsCategory.General, "Launch")] = "Window",
        [(SettingsCategory.General, "A new window starts with")] = "Window",
        [(SettingsCategory.General, "Projects and saving")] = "Header",
        [(SettingsCategory.TimelinePlayer, null)] = "Timeline",
        [(SettingsCategory.TimelinePlayer, "Timeline")] = "Timeline",
        [(SettingsCategory.TimelinePlayer, "I-frames")] = "Timeline",
        [(SettingsCategory.TimelinePlayer, "Hover previews")] = "Timeline",
        [(SettingsCategory.TimelinePlayer, "Player")] = "Player",
        [(SettingsCategory.TimelinePlayer, "Tabs and bars")] = "Window",
        [(SettingsCategory.PreviewCache, null)] = "Player",
        [(SettingsCategory.PreviewCache, "Proxies")] = "Player",
        [(SettingsCategory.PreviewCache, "Media browser")] = "Media",
        [(SettingsCategory.ExportHardware, null)] = "Export",
        [(SettingsCategory.ExportHardware, "Export tab")] = "Export",
        [(SettingsCategory.ExportHardware, "Default encoding settings")] = "Export",
        [(SettingsCategory.AutoCaptions, "Remove Dead Air")] = "Timeline",
    };

    // Options about another part of the editor than the rest of their section's (by the name the stage gives them).
    private static readonly Dictionary<string, string> OptionMirrors = new()
    {
        ["Canvas: show the selected clip's motion path"] = "Canvas",
        ["Canvas: snap to the middle and the safe margins"] = "Canvas",
        ["New Clips on the Frame"] = "Canvas",
    };

    /// <summary>
    /// Live editor region lookup (MainWindow.StageMirror, set by MainWindow.OpenSettings): the element when visible, else null.
    /// Without it the stage shows drawn pictures only.
    /// </summary>
    internal Func<string, (FrameworkElement Source, Rect View, ImageSource? Video, Rect VideoRect)?>? MirrorSource { get; init; }

    // The still standing in for the player's video follows the playhead while the player is on the stage.
    private readonly DispatcherTimer _mirrorTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _stageSection, _stageOptionName;
    private StageKind _stageKind;

    private static readonly AppSettings Defaults = new();

    // The options' boxes whose values the code fills in rather than a binding: the setting each one shows.
    private static readonly Dictionary<string, string> SetInCode = new()
    {
        [nameof(TimeFormatBox)] = nameof(AppSettings.ShowTimesAsFrames),
        [nameof(DroppedClipsBox)] = nameof(AppSettings.DroppedClipsJoinTrack),
        [nameof(DecodeAdapterBox)] = nameof(AppSettings.HardwareDecodeAdapter),
        [nameof(BackgroundJobsBox)] = nameof(AppSettings.BackgroundJobs),
        [nameof(ReservedCoresBox)] = nameof(AppSettings.ReservedCores),
        [nameof(BackgroundGpuBox)] = nameof(AppSettings.BackgroundGpu),
        [nameof(BackgroundThreadsBox)] = nameof(AppSettings.BackgroundToolThreads),
        [nameof(WhisperThreadsBox)] = nameof(AppSettings.WhisperThreads),
        [nameof(WhisperModelBox)] = nameof(AppSettings.WhisperModel),
        [nameof(DefaultLanguageBox)] = nameof(AppSettings.WhisperDefaultLanguage),
        [nameof(DefaultVideoBox)] = nameof(AppSettings.DefaultVideoEncoder),
        [nameof(DefaultAudioBox)] = nameof(AppSettings.DefaultAudioEncoder),
        [nameof(DefaultBitrateBox)] = nameof(AppSettings.DefaultAudioBitrate),
        [nameof(DefaultContainerBox)] = nameof(AppSettings.DefaultContainer),
        [nameof(DefaultRateControlBox)] = nameof(AppSettings.DefaultRateControl),
        [nameof(DefaultFramerateBox)] = nameof(AppSettings.DefaultFramerate),
        [nameof(DefaultMergeBox)] = nameof(AppSettings.DefaultMergeAudio),
        [nameof(DefaultQualityBox)] = nameof(AppSettings.DefaultQuality),
    };

    // The panels whose children are options: every tab's, and the folded Advanced section's.
    private readonly HashSet<Panel> _optionPanels = [];
    private readonly DispatcherTimer _captionTimer = new() { Interval = TimeSpan.FromMilliseconds(520) };
    private (long Timeline, long Browser, long Proxies) _cacheBytes;
    private TabItem? _stageTab;
    private bool _stageWide, _stageRefreshQueued;

    /// <summary>Builds the rail and wires hover/focus of every option to the stage.</summary>
    private void InitializeScreen()
    {
        // The keys' page fills its hosts first (SettingsWindow.Keybinds.cs), so they are options like any other below.
        InitializeKeybindsPage();
        BuildCategoryRail();
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            if ((tab.Content is ScrollViewer scroller ? scroller.Content : tab.Content) is not Panel panel)
                continue;

            WireOptions(tab, panel);
            panel.GotKeyboardFocus += (_, e) =>
            {
                if (OptionOf(e.OriginalSource as DependencyObject) is { } option)
                    ShowStage(tab, option);
            };
        }

        // Every change of a value redraws the picture: by the time this runs, the bindings have written it into the copy.
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => RefreshStageSoon()), true);
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => RefreshStageSoon()), true);
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => RefreshStageSoon()), true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => RefreshStageSoon()), true);
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => RefreshStageSoon()), true);

        // The theme under the pointer in the open list is drawn, before it is chosen.
        ThemeBox.AddHandler(MouseMoveEvent, new MouseEventHandler(ThemeBox_MouseMove), true);
        ThemeBox.DropDownClosed += (_, _) =>
        {
            ApplyChosenTheme();
            StagePreview.ThemeOverride = null;
            RefreshStageSoon();
        };

        // Apply the chosen theme app-wide: on dropdown close, or after arrow-key stepping settles (the stage previews each step via
        // the binding) to avoid re-theming per step. On close the saved theme stays in effect (new if saved, else the original).
        _themeTimer.Tick += (_, _) => ApplyChosenTheme();
        ThemeBox.SelectionChanged += (_, _) =>
        {
            _themeTimer.Stop();
            if (IsLoaded && !ThemeBox.IsDropDownOpen)
                _themeTimer.Start();
        };
        Closed += (_, _) =>
        {
            _themeTimer.Stop();
            if (ThemeManager.Current != ThemeManager.Normalize(AppSettings.Current.Theme))
                ThemeManager.Apply();
        };

        // Re-query the mirrored editor on hide/show/resize; release it on close.
        StagePreview.MirrorChanged += () => Dispatcher.InvokeAsync(RefreshMirror, DispatcherPriority.Background);
        _mirrorTimer.Tick += (_, _) => RefreshMirror();
        Closed += (_, _) =>
        {
            _mirrorTimer.Stop();
            StagePreview.ShowMirror(null, Rect.Empty, null, Rect.Empty);
        };

        _captionTimer.Tick += (_, _) => StageCaptions.SpokenWord = (StageCaptions.SpokenWord + 1) % 4;
        Closed += (_, _) => _captionTimer.Stop();
        SizeChanged += (_, _) => LayOutStage();
        ShowStage(SettingsTabs.SelectedItem as TabItem, null);
    }

    private void WireOptions(TabItem tab, Panel panel)
    {
        _optionPanels.Add(panel);
        foreach (var option in panel.Children.OfType<FrameworkElement>())
        {
            if (option is Expander { Content: Panel folded })
                WireOptions(tab, folded);
            option.MouseEnter += (_, _) => ShowStage(tab, option);
        }
    }

    // The option (a child of a tab's panel, or of a folded section's) an element is part of.
    private FrameworkElement? OptionOf(DependencyObject? element)
    {
        for (var item = element; item is not null; item = item is Visual ? VisualTreeHelper.GetParent(item) ?? LogicalTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
        {
            if (item is FrameworkElement { Parent: Panel panel } option && _optionPanels.Contains(panel))
                return option;
        }

        return null;
    }

    /// <summary>Stage content for an option (name, location, default, description, picture), or for the tab when none.</summary>
    private void ShowStage(TabItem? tab, FrameworkElement? option)
    {
        if (tab is null)
            return;

        var category = tab.Header as string ?? "";
        var section = option is null ? null : SectionOf(option);
        var name = option is null ? null : NameOf(option);
        (_stageTab, _stageSection, _stageOptionName) = (tab, section, name);
        StageCategory.Text = section is null ? category : $"{category}  ›  {section}";
        StageTitle.Text = option is null ? category : name ?? section ?? category;
        StageDescription.Text = option is null ? CategoryIntros.GetValueOrDefault(CategoryOf(tab), "")
            : TipOf(option) ?? (option is TextBlock { Text.Length: > 0 } words ? words.Text : IntroOf(option) ?? "");

        var fallback = option is null ? null : DefaultOf(option);
        StageDefaultBadge.Visibility = fallback is null ? Visibility.Collapsed : Visibility.Visible;
        StageDefaultText.Text = $"Default: {fallback}";
        StageTitle.ToolTip = StageTitle.Text;
        FitStageDescription();
        LayOutStage();
        RefreshStage();
    }

    /// <summary>
    /// Clamps the header description to three lines with ellipsis; longer text appears in full below the card and as a tooltip so
    /// the card never shifts.
    /// </summary>
    private void FitStageDescription()
    {
        var text = StageDescription.Text;
        var (width, height) = (StageDescription.ActualWidth, StageDescription.ActualHeight);
        var fits = true;
        if (text.Length > 0 && width > 0 && height > 0)
        {
            var face = new Typeface(StageDescription.FontFamily, StageDescription.FontStyle, StageDescription.FontWeight, StageDescription.FontStretch);
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection, face, StageDescription.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = width, LineHeight = StageDescription.LineHeight };
            fits = formatted.Height <= height + 0.5;
        }

        StageDescriptionMore.Text = fits ? "" : text;
        StageDescriptionMore.Visibility = fits ? Visibility.Collapsed : Visibility.Visible;
        StageDescription.ToolTip = fits ? null : text;
    }

    private void StageDescription_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            FitStageDescription();
    }

    private void RefreshStageSoon()
    {
        if (_stageRefreshQueued)
            return;

        _stageRefreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _stageRefreshQueued = false;
            RefreshStage();
        }, DispatcherPriority.Background);
    }

    // The picture of the category on show, drawn from the settings being edited.
    private void RefreshStage()
    {
        var kind = StageKindOf(CategoryOf(_stageTab));
        _stageKind = kind;
        StageCardTitle.Text = kind switch
        {
            StageKind.Appearance => $"Live preview: {ThemeManager.DisplayName(StagePreview.ThemeOverride ?? _settings.Theme)}",
            StageKind.Timeline => "Live preview: a stretch of timeline",
            StageKind.Cache => "What is kept on disk",
            StageKind.Hardware => "Who uses the processors and graphics cards",
            StageKind.Captions => "Live preview: the default caption style",
            StageKind.Keybinds => "The keys of the main window",
            _ => "",
        };
        StageCardTitle.Visibility = StageCardTitle.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StageKeybindsHost.Visibility = kind == StageKind.Keybinds ? Visibility.Visible : Visibility.Collapsed;

        // Clickable GPU/NPU role cards on Export & Hardware (their single home; SettingsStagePreview draws none).
        StageHardwarePanel.Visibility = kind == StageKind.Hardware ? Visibility.Visible : Visibility.Collapsed;
        ShowStageRoles();

        StagePreview.DroppedClipsJoin = DroppedClipsBox.SelectedIndex == 1;
        StagePreview.CacheBytes = _cacheBytes;
        StagePreview.Show(kind, _settings);
        RefreshMirror();

        // The sample caption is spoken a word at a time while it is on show.
        var captions = kind == StageKind.Captions;
        StageCaptionFrame.Visibility = captions ? Visibility.Visible : Visibility.Collapsed;
        if (captions)
        {
            StageCaptions.Show(_settings.DefaultCaptionStyle ?? new CaptionStyle());
            _captionTimer.Start();
        }
        else
        {
            _captionTimer.Stop();
        }
    }

    /// <summary>
    /// Mirrors the relevant visible editor region (option's, else section's, else category's). Closed, hidden or background panes are
    /// never created or shown (MainWindow.StageMirror). The card is visible only with content.
    /// </summary>
    private void RefreshMirror()
    {
        string? region = null;
        if (_stageOptionName is { } option && OptionMirrors.TryGetValue(option, out var own))
            region = own;
        else if (_stageTab is not null && StageMirrors.TryGetValue((CategoryOf(_stageTab), _stageSection), out var shown))
            region = shown;

        var mirror = region is not null && MirrorSource is { } find ? find(region) : null;
        StagePreview.ShowMirror(mirror?.Source, mirror?.View ?? Rect.Empty, mirror?.Video, mirror?.VideoRect ?? Rect.Empty);

        // The player's still follows the playhead, twice a second, while the player is on the stage.
        if (mirror is { } player && !player.VideoRect.IsEmpty)
            _mirrorTimer.Start();
        else
            _mirrorTimer.Stop();

        StageCard.Visibility = StagePreview.Visibility == Visibility.Visible || _stageKind == StageKind.Keybinds ? Visibility.Visible : Visibility.Collapsed;
    }

    // The theme stepped to with the arrow keys is put into effect once the keys have rested this long.
    private readonly DispatcherTimer _themeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    // The theme chosen in the box is put into effect in every window, unless it already is.
    private void ApplyChosenTheme()
    {
        _themeTimer.Stop();
        if (IsLoaded && ThemeBox.SelectedItem is string theme && ThemeManager.Normalize(theme) != ThemeManager.Current)
            ThemeManager.Apply(theme);
    }

    private void ThemeBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ThemeBox.IsDropDownOpen || ItemsControl.ContainerFromElement(ThemeBox, e.OriginalSource as DependencyObject) is not ComboBoxItem item
            || ThemeBox.ItemContainerGenerator.ItemFromContainer(item) is not string theme || theme == StagePreview.ThemeOverride)
            return;

        StagePreview.ThemeOverride = theme;
        RefreshStage();
    }

    // The caption picture is 16:9, as wide as the card.
    private void StageCaptionFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            StageCaptionFrame.Height = e.NewSize.Width * 9 / 16;
    }

    private void StageWide_Click(object sender, RoutedEventArgs e)
    {
        _stageWide = StageWideToggle.IsChecked == true;
        LayOutStage();
    }

    // Show Quick-Start Tour: save like Back, then start the tour in the editor after this screen closes; skipped if saving fails.
    private void ShowQuickStartTour_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is not MainWindow editor)
            return;

        Save_Click(BackButton, e);
        if (DialogResult == true)
            editor.Dispatcher.InvokeAsync(editor.StartQuickStartTour, DispatcherPriority.Background);
    }

    /// <summary>
    /// Splits options/stage 55/45, or 35/65 when widened (button, or the Keybinds category, whose keyboard needs the room); hides the
    /// stage when too narrow.
    /// </summary>
    private void LayOutStage()
    {
        var room = ActualWidth <= 0 || ActualWidth >= 980;
        var wide = _stageWide || CategoryOf(_stageTab) == SettingsCategory.Keybinds;
        StagePanel.Visibility = room ? Visibility.Visible : Visibility.Collapsed;
        ListColumn.Width = new GridLength(room && wide ? 35 : 55, GridUnitType.Star);
        StageColumn.Width = room ? new GridLength(wide ? 65 : 45, GridUnitType.Star) : new GridLength(0);
        StageWideToggle.IsChecked = wide;
    }

    // ----- What an option is called, says and starts as -----

    // The title of the section an option is under (in a folded section with none above it, the fold's own).
    private static string? SectionOf(FrameworkElement option)
    {
        if (option.Parent is not Panel panel)
            return null;

        string? title = null;
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            if (ReferenceEquals(child, option))
                break;
            if (child is TextBlock heading && ReferenceEquals(heading.Style, heading.TryFindResource("SectionTitle")))
                title = heading.Text;
        }

        return title ?? (panel.Parent is Expander { Header: string fold } ? fold : null);
    }

    // The hint that opens an option's section: what describes a row that has no tool tip of its own.
    private static string? IntroOf(FrameworkElement option)
    {
        if (option.Parent is not Panel panel)
            return null;

        string? hint = null;
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            if (child is TextBlock heading && ReferenceEquals(heading.Style, heading.TryFindResource("SectionTitle")))
                hint = null;
            else if (hint is null && child is TextBlock { Text.Length: > 0 } words && ReferenceEquals(words.Style, words.TryFindResource("Hint")))
                hint = words.Text;
            if (ReferenceEquals(child, option))
                break;
        }

        return hint;
    }

    // What an option is called: a tick box's words, a row's label (without its colon), a button's words, or its first box's name.
    private static string? NameOf(FrameworkElement option)
    {
        switch (option)
        {
            case Expander { Header: string fold }:
                return fold;
            case ContentControl { Content: string words } when words.Length > 0:
                return words;
            case TextBlock:
                return null;
        }

        foreach (var element in Within(option))
        {
            if (element is TextBlock { Text.Length: > 0 } label
                && (ReferenceEquals(label.Style, label.TryFindResource("FieldLabel")) || ReferenceEquals(label.Style, label.TryFindResource("WideLabel"))))
                return label.Text.TrimEnd(':', ' ');
        }

        foreach (var element in Within(option))
        {
            if (element is FrameworkElement named && AutomationProperties.GetName(named) is { Length: > 0 } name)
                return name;
        }

        return null;
    }

    // An option's tool tip, or the first one inside it.
    private static string? TipOf(FrameworkElement option)
    {
        foreach (var element in Within(option))
        {
            if (element is FrameworkElement { ToolTip: string { Length: > 0 } tip })
                return tip;
        }

        return null;
    }

    // An option and everything in it, the option first (the logical tree, as the search box reads it).
    private static IEnumerable<DependencyObject> Within(DependencyObject option)
    {
        yield return option;
        foreach (var child in LogicalTreeHelper.GetChildren(option).OfType<DependencyObject>())
        {
            foreach (var inner in Within(child))
                yield return inner;
        }
    }

    /// <summary>Option default: a number box's declared default, else the built-in setting value.</summary>
    private static string? DefaultOf(FrameworkElement option)
    {
        // A folded section is many options: each shows its own default when it is pointed at.
        if (option is Expander)
            return null;

        foreach (var element in Within(option))
        {
            if (NumericInput.GetDefault(element) is { Length: > 0 } number)
                return number;

            var property = element switch
            {
                CheckBox => ToggleButton.IsCheckedProperty,
                ComboBox list => BindingOperations.GetBinding(list, Selector.SelectedItemProperty) is not null ? Selector.SelectedItemProperty : Selector.SelectedValueProperty,
                TextBox => TextBox.TextProperty,
                _ => null,
            };

            if (property is null || element is not FrameworkElement box)
                continue;
            var path = BindingOperations.GetBinding(box, property)?.Path?.Path ?? SetInCode.GetValueOrDefault(box.Name);
            if (path is null || typeof(AppSettings).GetProperty(path) is not { } setting)
                continue;

            return Describe(box, setting.GetValue(Defaults));
        }

        return null;
    }

    // A default as its box would show it: the list's entry for it where there is one.
    private static string Describe(FrameworkElement box, object? value)
    {
        if (box is ComboBox list)
        {
            if (value is bool on && list.Items.Count == 2)
                return list.Items[on ? 1 : 0] is ComboBoxItem { Content: string words } ? words : on ? "On" : "Off";

            foreach (var item in list.ItemsSource?.Cast<object>() ?? list.Items.Cast<object>())
            {
                var (key, shown) = item switch
                {
                    NumberChoice choice => ((object)choice.Value, choice.Display),
                    Choice choice => (choice.Name, choice.Display),
                    GpuAdapter adapter => (adapter.Index, adapter.Display),
                    KeyValuePair<string, string> pair => (pair.Key, pair.Value),
                    ComboBoxItem { Tag: { } tag } entry => (tag, entry.Content as string ?? tag.ToString() ?? ""),
                    _ => (item, item.ToString() ?? ""),
                };

                if (Equals(key, value) || value is not null && Equals(key?.ToString(), value.ToString()))
                    return shown;
            }
        }

        return value switch
        {
            null or "" => "blank (built in)",
            bool on => on ? "On" : "Off",
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }
}

/// <summary>
/// A Settings category's stable identity, carried by its tab's XAML <c>Tag</c>: what the stage picture, its intro, rail icon and mirrors
/// are chosen by, whatever the header says (Codex BAD-05).
/// </summary>
internal enum SettingsCategory
{
    None,
    General,
    TimelinePlayer,
    PreviewCache,
    ExportHardware,
    AutoCaptions,
    Automation,
    Keybinds,
    Tools,
}
