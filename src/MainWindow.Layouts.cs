using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The Layout button: built-in layouts, the user's own, its menu and the Layout Selector -----
// The header's Layout button has the Layout Selector (resting on it, Pass 4.1B: a card for each layout, shown in the window while the pointer is on it, and a row of themes, put on live the same way)
// and one menu (a click: the side window, the layouts, Save Current Layout, Reset Layout). A layout arranges the main
// window; the side window is left as it is (DockManager.Prepared). The player floats over its place (DockPane.Floats),
// so a preview only moves it.
public partial class MainWindow
{
    /// <summary>A layout the Layout button offers: its name, what it is for, the arrangement, and its place when it is one of the user's own.</summary>
    private sealed record LayoutChoice(string Name, string About, DockLayoutState Layout, int? Slot);

    // The settings tabs as one group, in their order; the one shown first named.
    private DockNode TabsGroup(string? active = null, params string[] without)
    {
        var tabs = DockNode.Group([.. _tabPanes.Select(t => t.Id).Where(id => !without.Contains(id))]);
        if (active is not null && tabs.Panes.Contains(active))
            tabs.Active = active;
        return tabs;
    }

    /// <summary>
    /// The bottom of the built-in layouts but Standard Editing (4.4): the timeline, with a group of settings tabs at its
    /// right. It is the shape a 4.3 layout is brought up to (UpgradeLayout: the timeline beside the tabs, 70 to 30,
    /// TimelineShare), so the Layout Selector's "In use" mark still finds a layout taken up before 4.4; a 4.3 Standard
    /// Editing goes on to the timeline as the tabs' first (PutTimelineFirst).
    /// </summary>
    private static DockNode TimelineBeside(DockNode tabs) => DockNode.Split(false, (DockNode.Group(TimelinePaneId), 70), (tabs, 30));

    /// <summary>
    /// The main tab group with the timeline as its first tab, and shown (Standard Editing, 4.4-BugHunt: the user's Q8): the
    /// whole width of the bottom for the timeline, the settings tabs a click away beside its tab.
    /// </summary>
    private static DockNode TimelineFirst(DockNode tabs)
    {
        tabs.Panes.Remove(TimelinePaneId);
        tabs.Panes.Insert(0, TimelinePaneId);
        tabs.Active = TimelinePaneId;
        return tabs;
    }

    /// <summary>
    /// Standard Editing (where HandPeg starts, and what Reset Layout puts back): the player at the top left with the
    /// playback controls under it; the Canvas over the effects and keyframes in a column beside it; the header
    /// joined over the media browser at the right; the overview bar under all of it (shown: AppSettings.ShowMasterTimeline
    /// is on by default), and under that the timeline, the first tab of the group of settings tabs (4.4-BugHunt).
    /// </summary>
    private DockLayoutState StandardLayout()
    {
        var player = DockNode.Split(true, (DockNode.Group("Player"), 1), (DockNode.Group("Playback"), 1));
        var middle = DockNode.Split(true, (DockNode.Group("Layout"), 1), (DockNode.Group("Effects", "Keyframes"), 1.3));
        var side = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        var top = DockNode.Split(false, (player, 56), (middle, 22), (side, 22));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 46), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TimelineFirst(TabsGroup()), 38)),
        };
    }

    /// <summary>
    /// Standard Editing as 4.4 first made it (the timeline in a group of its own, the settings tabs beside it): what an
    /// arrangement of version 2 taken up as Standard Editing is recognised by (PutTimelineFirst).
    /// </summary>
    private DockLayoutState StandardLayoutBeside()
    {
        var state = StandardLayout();
        var tabs = state.Main.Children[^1];
        tabs.Panes.Remove(TimelinePaneId);
        tabs.Active = tabs.Panes.FirstOrDefault();
        state.Main.Children[^1] = TimelineBeside(tabs);
        return state;
    }

    /// <summary>
    /// Color and Effects: a large player with the Canvas beside it; two tall inspector columns at the right, the
    /// effects and keyframes, and the header joined over the media browser with the Summary; under them the overview
    /// bar, and the timeline with the other settings tabs at its right.
    /// </summary>
    private DockLayoutState InspectorLayout()
    {
        var player = DockNode.Split(true, (DockNode.Split(false, (DockNode.Group("Player"), 72), (DockNode.Group("Layout"), 28)), 1), (DockNode.Group("Playback"), 1));
        var inspector = DockNode.Group("Effects", "Keyframes");
        var media = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media", "Tab:Summary"), 1));
        var top = DockNode.Split(false, (player, 60), (inspector, 20), (media, 20));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 66), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TimelineBeside(TabsGroup(null, "Tab:Summary")), 34)),
        };
    }

    /// <summary>
    /// Assembly and Media: the player at the top left; a wide media column at the right, the header joined over it at
    /// the top right (as in the other layouts), the Canvas a tab beside the media; the overview bar across the whole
    /// window, and under it the timeline, the settings tabs (open on Export) and the effects and keyframes side by side.
    /// </summary>
    private DockLayoutState AssemblyLayout()
    {
        var player = DockNode.Split(true, (DockNode.Group("Player"), 1), (DockNode.Group("Playback"), 1));
        var media = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media", "Layout"), 1));
        var top = DockNode.Split(false, (player, 66), (media, 34));
        var bottom = DockNode.Split(false, (DockNode.Group(TimelinePaneId), 49), (TabsGroup(ExportId), 21), (DockNode.Group("Effects", "Keyframes"), 30));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 54), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (bottom, 40)),
        };
    }

    /// <summary>
    /// Vertical and Shorts Studio, for 9:16 shorts and highlights: a tall player and the Canvas side by side at the top
    /// left (each a tall frame), with the playback controls under them; the header joined over the media, effects and
    /// keyframes at the top right; the overview bar across the whole window, and under it the timeline with the
    /// settings tabs at its right, Export and Audio first (the others after them).
    /// </summary>
    private DockLayoutState ShortsLayout()
    {
        var stage = DockNode.Split(false, (DockNode.Group("Player"), 50), (DockNode.Group("Layout"), 50));
        var player = DockNode.Split(true, (stage, 1), (DockNode.Group("Playback"), 1));
        var side = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media", "Effects", "Keyframes"), 1));
        var top = DockNode.Split(false, (player, 64), (side, 36));
        string[] first = [ExportId, "Tab:Audio"];
        var tabs = DockNode.Group([.. first, .. _tabPanes.Select(t => t.Id).Where(id => !first.Contains(id))]);
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 60), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TimelineBeside(tabs), 40)),
        };
    }

    private IEnumerable<LayoutChoice> BuiltInLayouts() =>
    [
        new("Standard Editing", "The player at the top left, the Canvas and effects beside it, the media at the right; the overview bar, and the timeline below, the first of the settings tabs. Where HandPeg starts.", StandardLayout(), null),
        new("Color & Effects", "A large player, tall effects and keyframes and media and Summary columns at the right; the timeline below, with the other tabs beside it.", InspectorLayout(), null),
        new("Assembly & Media", "The player at the left, a wide media column with the Canvas at the right; the timeline, the settings tabs and the effects and keyframes side by side below.", AssemblyLayout(), null),
        new("Vertical & Shorts Studio", "For 9:16 shorts: a tall player and Canvas side by side, media, effects and keyframes at the right; the timeline below, with the Export and Audio tabs beside it.", ShortsLayout(), null),
    ];

    /// <summary>
    /// The built-in layout chosen in the first-run window (AppSettings.FirstRunLayout) as an arrangement to load: written
    /// into AppSettings.DockLayout, and the choice cleared. Null when no choice is waiting (or it names no layout).
    /// </summary>
    private string? TakeFirstRunLayout()
    {
        if (AppSettings.Current.FirstRunLayout is not { } name)
            return null;

        var json = BuiltInLayouts().FirstOrDefault(l => l.Name == name) is { } choice ? System.Text.Json.JsonSerializer.Serialize(choice.Layout) : null;
        _viewModel.SavePaneLayout(s => (s.DockLayout, s.FirstRunLayout) = (json ?? s.DockLayout, null));
        return json;
    }

    /// <summary>The first-run window opened again from the settings: the layout chosen there, put on at once.</summary>
    public void ApplyFirstRunLayout()
    {
        if (TakeFirstRunLayout() is { } json && DockLayoutState.Read(json) is { } layout)
            _dock.Apply(_dock.Prepared(layout));
    }

    /// <summary>The user's own layouts, by place.</summary>
    private IEnumerable<LayoutChoice> SavedLayoutChoices() => AppSettings.Current.SavedLayouts.OrderBy(s => s.Slot)
        .Select(s => DockLayoutState.Read(s.Layout) is { } layout ? new LayoutChoice(s.Name, $"Your layout, kept in place {s.Slot}.", layout, s.Slot) : null)
        .OfType<LayoutChoice>();

    // ----- The menu (a click on the Layout button) -----

    /// <summary>A click on the Layout button: its menu, beside it. A click on it while the menu is open closes it (CloseLayoutMenuOnPress).</summary>
    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _layoutHover.Close();
        if (sender is FrameworkElement button)
            OpenLayoutMenu(button);
    }

    /// <summary>The menu: its rows made for the layouts, the side window's tick, and its place beside the button clicked.</summary>
    private void OpenLayoutMenu(FrameworkElement host)
    {
        UpdateSideWindowCheck();
        FillLayoutRows(BuiltInLayoutRows, BuiltInLayouts());
        var saved = SavedLayoutChoices().ToList();
        FillLayoutRows(SavedLayoutRows, saved);
        SavedLayoutsTitle.Visibility = saved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        LayoutFlyout.PlacementTarget = host;
        LayoutFlyoutPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PlaceHeaderFlyout(LayoutFlyout, LayoutButton, LayoutFlyoutPanel.DesiredSize.Width);
        _layoutMenuFrom = host;
        LayoutFlyout.IsOpen = true;
    }

    // The button the menu was opened from, to give the keyboard back to when it closes by Escape.
    private FrameworkElement? _layoutMenuFrom;

    /// <summary>Closes the menu (a choice made in it, Escape, a press anywhere else in the window, the window left).</summary>
    private void CloseLayoutMenu() => LayoutFlyout.IsOpen = false;

    /// <summary>
    /// A press in the window while the menu is open closes it (the menu is a window of its own: presses on it never come
    /// here, nor do those on a saved layout's right-click menu). A press on the Layout button closes it and is kept from
    /// opening it again, so the button opens and closes its menu.
    /// </summary>
    private void CloseLayoutMenuOnPress(object sender, MouseButtonEventArgs e)
    {
        if (!LayoutFlyout.IsOpen)
            return;

        CloseLayoutMenu();
        if (LayoutButton.IsMouseOver)
            e.Handled = true;
    }

    /// <summary>A row for each layout: a click takes it up; one of the user's own can be deleted from its right-click menu.</summary>
    private void FillLayoutRows(Panel rows, IEnumerable<LayoutChoice> choices)
    {
        rows.Children.Clear();
        foreach (var choice in choices)
        {
            var row = new Button { Content = choice.Name, ToolTip = choice.About };
            AutomationProperties.SetName(row, choice.Name);
            row.Click += (_, _) =>
            {
                CloseLayoutMenu();
                TakeUpLayout(choice);
            };
            if (choice.Slot is { } slot)
            {
                var delete = new MenuItem { Header = $"Delete \"{choice.Name}\"" };
                delete.Click += (_, _) => DeleteSavedLayout(slot);
                row.ContextMenu = new ContextMenu { Items = { delete } };
            }

            rows.Children.Add(row);
        }
    }

    private void TakeUpLayout(LayoutChoice choice)
    {
        _dock.Apply(_dock.Prepared(choice.Layout));
        _viewModel.StatusText = $"Layout: {choice.Name}.";
    }

    private void LayoutFlyout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        CloseLayoutMenu();
        _layoutMenuFrom?.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// From the keyboard: Down on the Layout button opens the Layout Selector with its first card chosen (a card with
    /// the keyboard is shown as one under the pointer is); the menu key opens the menu with its first row chosen. Enter
    /// and Space click it (the menu).
    /// </summary>
    private void LayoutButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Apps) || sender is not FrameworkElement button)
            return;

        if (e.Key == Key.Down)
        {
            _layoutHover.Open(button);
            Dispatcher.BeginInvoke(() => LayoutCards.Children.OfType<Button>().FirstOrDefault()?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            _layoutHover.Close();
            OpenLayoutMenu(button);
            Dispatcher.BeginInvoke(() => LayoutFlyoutPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), System.Windows.Threading.DispatcherPriority.Input);
        }

        e.Handled = true;
    }

    // ----- The user's own layouts -----

    /// <summary>Save Current Layout: the panes as they are now, named, into one of the three places.</summary>
    private void SaveCurrentLayout_Click(object sender, RoutedEventArgs e)
    {
        CloseLayoutMenu();
        var kept = AppSettings.Current.SavedLayouts;
        var dialog = new SaveLayoutDialog(kept) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var layout = System.Text.Json.JsonSerializer.Serialize(_dock.Current);
        var saved = kept.Where(s => s.Slot != dialog.Slot).Append(new SavedDockLayout { Slot = dialog.Slot, Name = dialog.LayoutName, Layout = layout }).OrderBy(s => s.Slot).ToList();
        _viewModel.SavePaneLayout(s => s.SavedLayouts = saved);
        _cardsKey = null;
        _viewModel.StatusText = $"Layout saved as \"{dialog.LayoutName}\" (place {dialog.Slot}): the Layout button offers it.";
    }

    private void DeleteSavedLayout(int slot)
    {
        CloseLayoutMenu();
        var name = AppSettings.Current.SavedLayouts.FirstOrDefault(s => s.Slot == slot)?.Name;
        _viewModel.SavePaneLayout(s => s.SavedLayouts = s.SavedLayouts.Where(l => l.Slot != slot).ToList());
        _cardsKey = null;
        _viewModel.StatusText = $"Layout \"{name}\" deleted.";
    }

    // ----- The Layout Selector -----

    // The arrangement as it was when the selector opened, to come back to; the layouts its cards show (made ready to be
    // laid out), the one being shown, and whether one was kept.
    private string? _layoutBeforePreview;
    private List<DockLayoutState> _cardLayouts = [];
    private int _previewing = -1;
    private bool _layoutKept;

    // What the cards were last drawn from: the layouts, the panes shown and the theme. The same again, they are kept.
    private string? _cardsKey;

    private const double CardPictureWidth = 164, CardPictureHeight = 92;

    /// <summary>
    /// The pointer rested on the Layout button (HoverFlyout): the Layout Selector, under it (beside it, while the header
    /// is a strip), with the arrangement and the theme taken as they are now, to come back to.
    /// </summary>
    private void OpeningLayoutSelector()
    {
        CloseLayoutMenu();
        _layoutBeforePreview = _dock.Save();
        _themeBeforePreview = ThemeManager.Current;
        (_previewing, _layoutKept, _themeKept) = (-1, false, false);
        MakeLayoutCards();
        MakeThemeCards();
        PlaceLayoutSelector(_layoutHover.OpenedFrom);
    }

    /// <summary>
    /// The cards: each layout made ready to be laid out (as it would be taken up now), and its picture drawn once
    /// (DockManager.Miniature, frozen). When nothing they are drawn from has changed since the last time, the cards
    /// made then are kept as they are, so opening the selector again costs nothing.
    /// </summary>
    private void MakeLayoutCards()
    {
        var choices = BuiltInLayouts().Concat(SavedLayoutChoices()).ToList();
        _cardLayouts = choices.Select(c => _dock.Prepared(c.Layout)).ToList();

        Brush Frozen(string key)
        {
            var brush = ((Brush?)TryFindResource(key) ?? Brushes.Gray).CloneCurrentValue();
            brush.Freeze();
            return brush;
        }

        var (part, band, text, accent) = (Frozen("PanelBrush"), Frozen("ControlBrush"), Frozen("MutedTextBrush"), Frozen("AccentBrush"));

        // The layout in use: every card whose arrangement has the window's parts as they were when the selector opened
        // (DockManager.SameShape: the panes in the same splits and groups, however wide each part was dragged).
        var inUse = DockLayoutState.Read(_layoutBeforePreview) ?? _dock.Current;
        var active = _cardLayouts.Select(l => DockManager.SameShape(l, inUse)).ToList();
        var key = string.Join("", active.Select(a => a ? '1' : '0')) + "|" + string.Join("|", _cardLayouts.Select(l => System.Text.Json.JsonSerializer.Serialize(l))) + "|" + string.Join(",", choices.Select(c => c.Name))
            + "|" + string.Join(",", _viewModel.IsMediaBrowserVisible, _viewModel.ShowKeyframesPane, _viewModel.ShowEffectsPane,
                _viewModel.IsArrangeActive, _viewModel.ShowAdvancedPlayback, _viewModel.ShowPresetBarAtTop, _viewModel.ShowCommandPreviewTab)
            + "|" + string.Join(",", new[] { part, band, text, accent }.Select(b => (b as SolidColorBrush)?.Color.ToString()));
        if (key == _cardsKey && LayoutCards.Children.Count == choices.Count)
            return;

        _cardsKey = key;
        LayoutCards.Children.Clear();
        for (var i = 0; i < choices.Count; i++)
        {
            var (choice, index) = (choices[i], i);
            var picture = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(2),
                Child = new Image { Source = _dock.Miniature(_cardLayouts[i], new Size(CardPictureWidth, CardPictureHeight), part, band, text, accent), Width = CardPictureWidth, Height = CardPictureHeight },
            };
            picture.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
            var name = new TextBlock { Text = choice.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 6, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = CardPictureWidth + 4 };
            var kind = new TextBlock { Text = choice.Slot is { } slot ? $"Saved, place {slot}" : "Built-in", Margin = new Thickness(2, 1, 2, 0), FontSize = 11 };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var footer = new DockPanel { LastChildFill = true };
            if (active[i])
                footer.Children.Add(InUseBadge());
            footer.Children.Add(kind);

            var card = new Button { Content = new StackPanel { Children = { picture, name, footer } }, ToolTip = active[i] ? $"In use. {choice.About}" : choice.About };
            card.SetResourceReference(StyleProperty, "LayoutCard");
            card.Tag = active[i] ? "active" : null;
            AutomationProperties.SetName(card, active[i] ? $"{choice.Name} (in use)" : choice.Name);
            AutomationProperties.SetHelpText(card, choice.About);
            card.MouseEnter += (_, _) => PreviewLayout(index);
            card.GotKeyboardFocus += (_, _) => PreviewLayout(index);
            card.Click += (_, _) => KeepLayout(index, choice.Name);
            LayoutCards.Children.Add(card);
        }
    }

    /// <summary>The mark on the card of the layout in use (Pass 4.1C): a tick and "In use", in the chosen edge's colour.</summary>
    private static FrameworkElement InUseBadge()
    {
        var tick = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) };
        var words = new TextBlock { Text = "In use", FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        tick.SetResourceReference(TextBlock.ForegroundProperty, "SelectedBorderBrush");
        words.SetResourceReference(TextBlock.ForegroundProperty, "SelectedBorderBrush");
        var badge = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 1, 2, 0), Children = { tick, words } };
        DockPanel.SetDock(badge, Dock.Right);
        return badge;
    }

    // ----- The Themes row (Pass 4.1B) -----
    // Under the layout cards, as wide as they are, a card a theme, about a third of a layout card's height: its window,
    // a panel, its accent and its name in its own colours. The pointer on one (or the keyboard) puts it on live in every
    // window, the layout cards redrawn in it; leaving the row, Escape or closing puts back the theme taken when the
    // selector opened; a click keeps it (AppSettings.Theme).

    private string? _themeBeforePreview;
    private bool _themeKept;

    private const double ThemeSwatchHeight = 30;

    private void MakeThemeCards()
    {
        ThemeCards.Children.Clear();
        var saved = ThemeManager.Normalize(AppSettings.Current.Theme);
        foreach (var theme in ThemeManager.Themes)
        {
            // The window's colour with the theme's name in its text colour, over a band of its panel colour holding its
            // accent (and a tick on the one in use). A name too long for the card is drawn smaller, never cut.
            var (window, panel, accent, text) = ThemeManager.Swatch(theme);
            var swatch = new Grid { Height = ThemeSwatchHeight, ClipToBounds = true };
            swatch.RowDefinitions.Add(new RowDefinition());
            swatch.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
            var name = new Viewbox
            {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 5, 0),
                Child = new TextBlock { Text = theme, Foreground = Frozen(text), FontSize = 11.5, FontWeight = FontWeights.SemiBold },
            };
            var band = new Grid { Background = Frozen(panel) };
            Grid.SetRow(band, 1);
            band.Children.Add(new Border { Background = Frozen(accent), Width = 18, Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            if (theme == saved)
                band.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, Foreground = Frozen(accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            swatch.Children.Add(name);
            swatch.Children.Add(band);

            var about = theme == saved ? $"{theme} (in use)" : theme;
            var card = new Button
            {
                Content = new Border { CornerRadius = new CornerRadius(4), Background = Frozen(window), Child = swatch, ClipToBounds = true },
                ToolTip = $"{about}: point at it to try it; click to keep it.",
                Tag = theme == saved ? "active" : null,
            };
            card.SetResourceReference(StyleProperty, "LayoutCard");
            card.Padding = new Thickness(3);
            AutomationProperties.SetName(card, theme == saved ? $"Theme {theme} (in use)" : $"Theme {theme}");
            card.MouseEnter += (_, _) => PreviewTheme(theme);
            card.GotKeyboardFocus += (_, _) => PreviewTheme(theme);
            card.Click += (_, _) => KeepTheme(theme);
            ThemeCards.Children.Add(card);
        }

        static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>The pointer (or the keyboard) came onto a theme: it is put on in every window, nothing saved; the layout cards are drawn in it.</summary>
    private void PreviewTheme(string theme)
    {
        if (ThemeManager.Current == theme)
            return;

        ThemeManager.Apply(theme);
        MakeLayoutCards();
    }

    /// <summary>A theme clicked: kept and saved, and the selector closed.</summary>
    private void KeepTheme(string theme)
    {
        _themeKept = true;
        _viewModel.SavePaneLayout(s => s.Theme = theme);
        ThemeManager.Apply();
        CrashReporter.Note("settings", "theme " + theme);
        _layoutHover.Close();
        _viewModel.StatusText = $"Theme: {theme}.";
    }

    /// <summary>The pointer left the theme cards: the theme as it was.</summary>
    private void ThemeCards_MouseLeave(object sender, MouseEventArgs e) => RestoreThemeBeforePreview();

    private void RestoreThemeBeforePreview()
    {
        if (_themeKept || _themeBeforePreview is not { } theme || ThemeManager.Current == theme)
            return;

        ThemeManager.Apply(theme);
        if (LayoutSelector.IsOpen)
            MakeLayoutCards();
    }

    /// <summary>
    /// Where the selector opens: under the button, its right edge at the button's (the header is at the right), or beside
    /// the header while that is a strip; kept inside the panes' area. Placed once: it stays there while the window takes
    /// layout after layout under it (the button itself may move), so the card under the pointer never changes by itself.
    /// </summary>
    /// <remarks>
    /// All in DIPs of the panes' area (Pass 4.2B-2): the button's corners taken into <c>MainDockHost</c> (through the
    /// screen when the button is in another window, the side window), and the popup's own content measured, so its right
    /// edge is the button's at any DPI. At 150 % the selector flew left over the player.
    /// </remarks>
    private void PlaceLayoutSelector(FrameworkElement button)
    {
        var panel = (FrameworkElement)LayoutSelector.Child;
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (width, height) = (panel.DesiredSize.Width, panel.DesiredSize.Height);
        var corner = InDockHost(button, new Point(0, 0));
        var far = InDockHost(button, new Point(button.ActualWidth, button.ActualHeight));
        var (roomWidth, roomHeight) = (MainDockHost.ActualWidth, MainDockHost.ActualHeight);
        double x, y;
        if (ReferenceEquals(button, LayoutButton) && _headerShape == PaneShape.Column)
        {
            var onRight = (corner.X + far.X) / 2 > roomWidth / 2;
            (x, y) = (onRight ? corner.X - width - 6 : far.X + 6, corner.Y);
        }
        else
        {
            (x, y) = (far.X - width, far.Y + 6);
        }

        // Inside the panes' area, 8 DIPs from its left.
        LayoutSelector.HorizontalOffset = Math.Max(SelectorEdge, Math.Min(x, roomWidth - width));
        LayoutSelector.VerticalOffset = Math.Max(0, Math.Min(y, roomHeight - height));
    }

    private const double SelectorEdge = 8;

    /// <summary>A point of an element in the panes' area's DIPs, whichever window the element is in.</summary>
    private Point InDockHost(FrameworkElement element, Point point) =>
        MainDockHost.IsAncestorOf(element) || ReferenceEquals(element, MainDockHost)
            ? element.TranslatePoint(point, MainDockHost)
            : MainDockHost.PointFromScreen(element.PointToScreen(point));

    /// <summary>The pointer came onto a card: the window takes its layout, nothing saved.</summary>
    private void PreviewLayout(int index)
    {
        if (index == _previewing || index >= _cardLayouts.Count)
            return;

        _previewing = index;
        _dock.Preview(_cardLayouts[index]);
    }

    /// <summary>A card clicked: its layout is kept (it is already laid out) and saved.</summary>
    private void KeepLayout(int index, string name)
    {
        if (index >= _cardLayouts.Count)
            return;

        _layoutKept = true;
        _previewing = index;
        _dock.Apply(_cardLayouts[index]);
        _layoutHover.Close();
        _viewModel.StatusText = $"Layout: {name}.";
    }

    /// <summary>The pointer left the cards: the panes as they were.</summary>
    private void LayoutCards_MouseLeave(object sender, MouseEventArgs e) => RestoreLayoutBeforePreview();

    private void RestoreLayoutBeforePreview()
    {
        if (_previewing < 0 || _layoutKept)
            return;

        _previewing = -1;
        _dock.Load(_layoutBeforePreview);
    }

    private void LayoutSelector_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        var from = _layoutHover.OpenedFrom;
        _layoutHover.Close();
        if (from.IsVisible)
            from.Focus();
        e.Handled = true;
    }

    /// <summary>Closed without a card chosen (Escape, the pointer gone from it and the button): the panes and the theme as they were.</summary>
    private void LayoutSelector_Closed(object? sender, EventArgs e)
    {
        RestoreLayoutBeforePreview();
        RestoreThemeBeforePreview();
        (_layoutBeforePreview, _previewing, _themeBeforePreview) = (null, -1, null);
    }
}
