using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

// The full-window settings screen (Pass 4.3-0B): the window over the editor, the category rail, and the live preview
// stage that shows what the option under the pointer or with the focus does.
public partial class SettingsWindow
{
    // ----- Over the editor, as a full-window screen -----

    /// <summary>Takes the editor's place on the screen: its bounds, and maximized when it is (on its monitor).</summary>
    public void CoverWindow(Window owner)
    {
        // A maximized window's Left, Top, Width and Height are its restored ones: on the same monitor, which is where this maximizes too.
        var bounds = owner.WindowState == WindowState.Normal ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight) : owner.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (bounds.Left, bounds.Top, Math.Max(bounds.Width, MinWidth), Math.Max(bounds.Height, MinHeight));
        WindowState = owner.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    // Escape goes back to the editor, saving, as Back does; in the search box with words in it, it clears them first. An open
    // list, a cell being edited or the like takes its own Escape (handled), and this never sees it.
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

    // ----- The category rail -----

    // Each category's icon: lines on a 16 x 16 square, stroked in the rail's text colour.
    private static readonly Dictionary<string, string> RailIcons = new()
    {
        ["General"] = "M2,4.5 H14 M2,8 H14 M2,11.5 H14 M5,2.8 V6.2 M10.5,6.3 V9.7 M7,9.8 V13.2",
        ["Timeline & Player"] = "M2,4.5 H9 V7.5 H2 Z M6,9.5 H14 V12.5 H6 Z M11.5,1.5 V14.5",
        ["Preview & Cache"] = "M3,4 C3,2.3 13,2.3 13,4 V12 C13,13.7 3,13.7 3,12 Z M3,4 C3,5.7 13,5.7 13,4 M3,8 C3,9.7 13,9.7 13,8",
        ["Export & Hardware"] = "M4.5,4.5 H11.5 V11.5 H4.5 Z M6.5,6.5 H9.5 V9.5 H6.5 Z M6,2 V4.5 M8,2 V4.5 M10,2 V4.5 M6,11.5 V14 M8,11.5 V14 M10,11.5 V14 M2,6 H4.5 M2,8 H4.5 M2,10 H4.5 M11.5,6 H14 M11.5,8 H14 M11.5,10 H14",
        ["Auto-Captions"] = "M1.5,3.5 H14.5 V12.5 H1.5 Z M7,6.3 C5,5.4 3.8,6.4 3.8,8 C3.8,9.6 5,10.6 7,9.7 M12.2,6.3 C10.2,5.4 9,6.4 9,8 C9,9.6 10.2,10.6 12.2,9.7",
        ["Automation & Drop Targets"] = "M9,1.5 L3.5,9 H8 L7,14.5 L12.5,7 H8 Z",
        ["Tools & Backup"] = "M2.5,9.5 V13.5 H13.5 V9.5 M8,2 V10 M5,7 L8,10 L11,7",
    };

    private static readonly Dictionary<string, Geometry> RailGeometries = [];

    private static Geometry RailIcon(string header)
    {
        if (!RailGeometries.TryGetValue(header, out var geometry))
        {
            geometry = Geometry.Parse(RailIcons.GetValueOrDefault(header, "M6,8 A2,2 0 1 1 10,8 A2,2 0 1 1 6,8"));
            geometry.Freeze();
            RailGeometries[header] = geometry;
        }

        return geometry;
    }

    // A category for each tab: its icon and header, hidden with it when the search leaves the tab nothing.
    private void BuildCategoryRail()
    {
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            var header = tab.Header as string ?? "";
            var icon = new Shapes.Path
            {
                Data = RailIcon(header), Width = 16, Height = 16, StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center,
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
    private static readonly Dictionary<string, string> CategoryIntros = new()
    {
        ["General"] = "How HandPeg looks, how it starts, how it keeps itself and its tools up to date, and how it looks after a project and its crash reports.",
        ["Timeline & Player"] = "How the timeline is drawn, how it snaps and edits, the hover previews above it, and the player.",
        ["Preview & Cache"] = "Render Preview, what is kept on disk and how much of it, proxies of heavy videos, the media browser and downloads.",
        ["Export & Hardware"] = "The encoding a new export starts with, the graphics card that decodes for the player, and how much of the computer background work may take.",
        ["Auto-Captions"] = "The speech model that writes captions, how it listens, Remove Dead Air, and the style new captions start with.",
        ["Automation & Drop Targets"] = "Smart rules for files that are dropped or loaded, the targets of the drop overlay, and the Social Sharing Squisher's presets.",
        ["Tools & Backup"] = "The programs HandPeg downloads and where they come from, SponsorBlock, getting settings and projects in and out, and the keys of the main window.",
    };

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

    /// <summary>Makes the rail, and has every option show itself on the stage when it is pointed at or given the focus.</summary>
    private void InitializeScreen()
    {
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
            StagePreview.ThemeOverride = null;
            RefreshStageSoon();
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

    /// <summary>The stage for an option of a tab: its name, where it is, its default, what it does and its picture; for the tab itself when none.</summary>
    private void ShowStage(TabItem? tab, FrameworkElement? option)
    {
        if (tab is null)
            return;

        var category = tab.Header as string ?? "";
        var section = option is null ? null : SectionOf(option);
        _stageTab = tab;
        StageCategory.Text = section is null ? category : $"{category}  ›  {section}";
        StageTitle.Text = option is null ? category : NameOf(option) ?? section ?? category;
        StageDescription.Text = option is null ? CategoryIntros.GetValueOrDefault(category, "")
            : TipOf(option) ?? (option is TextBlock { Text.Length: > 0 } words ? words.Text : IntroOf(option) ?? "");

        var fallback = option is null ? null : DefaultOf(option);
        StageDefaultBadge.Visibility = fallback is null ? Visibility.Collapsed : Visibility.Visible;
        StageDefaultText.Text = $"Default: {fallback}";
        LayOutStage();
        RefreshStage();
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
        var kind = (_stageTab?.Header as string) switch
        {
            "General" => StageKind.Appearance,
            "Timeline & Player" => StageKind.Timeline,
            "Preview & Cache" => StageKind.Cache,
            "Export & Hardware" => StageKind.Hardware,
            "Auto-Captions" => StageKind.Captions,
            _ => StageKind.None,
        };

        StageCard.Visibility = kind == StageKind.None ? Visibility.Collapsed : Visibility.Visible;
        StageCardTitle.Text = kind switch
        {
            StageKind.Appearance => $"Live preview: {ThemeManager.DisplayName(StagePreview.ThemeOverride ?? _settings.Theme)}",
            StageKind.Timeline => "Live preview: a stretch of timeline",
            StageKind.Cache => "What is kept on disk",
            StageKind.Hardware => "Who uses the processors and graphics cards",
            StageKind.Captions => "Live preview: the default caption style",
            _ => "",
        };

        StagePreview.DroppedClipsJoin = DroppedClipsBox.SelectedIndex == 1;
        StagePreview.CacheBytes = _cacheBytes;
        StagePreview.Show(kind, _settings);

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

    /// <summary>
    /// The options and the stage share the window 55 / 45, or 35 / 65 with the stage widened (by its button, or by a
    /// category whose tab's Tag is "WideStage": the visual tools to come); a window too narrow for both shows no stage.
    /// </summary>
    private void LayOutStage()
    {
        var room = ActualWidth <= 0 || ActualWidth >= 980;
        var wide = _stageWide || _stageTab?.Tag is "WideStage";
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

    /// <summary>What an option is when nothing has been changed: a number box's declared default, else the built-in setting its box shows.</summary>
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
