using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The Layout button: built-in layouts, the user's own, its flyout and the Layout Selector -----
// The header's Layout button and Encoder Mode's top bar's share one flyout (resting on either: the side window, the
// layouts, Save Current Layout, Reset Layout) and one Layout Selector (a click: a card for each layout, shown in the
// window while the pointer is on it). A layout arranges the main window; the side window is left as it is
// (DockManager.Prepared). The player floats over its place (DockPane.Floats), so a preview only moves it.
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
    /// Standard Editing (where both modes start, and what Reset Layout puts back): the player at the top left with the
    /// playback controls under it; the layout pane over the effects and keyframes in a column beside it; the header
    /// joined over the media browser, and the cut segments, at the right; the master timeline and the settings tabs
    /// under all of it.
    /// </summary>
    private DockLayoutState StandardLayout()
    {
        var player = DockNode.Split(true, (DockNode.Group("Player"), 1), (DockNode.Group("Playback"), 1));
        var middle = DockNode.Split(true, (DockNode.Group("Layout"), 1), (DockNode.Group("Effects", "Keyframes"), 1.3));
        var side = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1.6), (DockNode.Group("CutSegments"), 1));
        var top = DockNode.Split(false, (player, 56), (middle, 22), (side, 22));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 46), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TabsGroup(), 38)),
        };
    }

    /// <summary>
    /// Color and Effects: a large player with the layout pane beside it; two tall inspector columns at the right, the
    /// effects and keyframes (and the cut segments), and the header joined over the media browser with the Summary;
    /// a short timeline and the other settings tabs under them.
    /// </summary>
    private DockLayoutState InspectorLayout()
    {
        var player = DockNode.Split(true, (DockNode.Split(false, (DockNode.Group("Player"), 72), (DockNode.Group("Layout"), 28)), 1), (DockNode.Group("Playback"), 1));
        var inspector = DockNode.Group("Effects", "Keyframes", "CutSegments");
        var media = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media", "Tab:Summary"), 1));
        var top = DockNode.Split(false, (player, 60), (inspector, 20), (media, 20));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 66), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TabsGroup(null, "Tab:Summary"), 22)),
        };
    }

    /// <summary>
    /// Assembly and Media: a tall media browser column at the left (the header joined over it), the player and the
    /// layout pane beside it; the timeline across the whole window, and under it the settings tabs open on Layers, with
    /// the effects and keyframes (and the cut segments) beside them.
    /// </summary>
    private DockLayoutState AssemblyLayout()
    {
        var media = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        var player = DockNode.Split(true, (DockNode.Split(false, (DockNode.Group("Player"), 70), (DockNode.Group("Layout"), 30)), 1), (DockNode.Group("Playback"), 1));
        var top = DockNode.Split(false, (media, 26), (player, 74));
        var bottom = DockNode.Split(false, (TabsGroup("Tab:Layers"), 70), (DockNode.Group("Effects", "Keyframes", "CutSegments"), 30));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 54), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (bottom, 40)),
        };
    }

    private IEnumerable<LayoutChoice> BuiltInLayouts() =>
    [
        new("Standard Editing", "The player at the top left, the layout and effects beside it, the media at the right, the timeline and tabs below. Where HandPeg starts.", StandardLayout(), null),
        new("Color & Effects", "A large player, tall effects and keyframes and media and Summary columns at the right, a short timeline below.", InspectorLayout(), null),
        new("Assembly & Media", "A tall media column at the left, the player beside it, the whole-width timeline and the Layers tab below.", AssemblyLayout(), null),
    ];

    /// <summary>The user's own layouts in this mode, by place.</summary>
    private IEnumerable<LayoutChoice> SavedLayoutChoices() => AppSettings.Current.SavedLayouts.OrderBy(s => s.Slot)
        .Select(s => DockLayoutState.Read(s.Layout) is { } layout ? new LayoutChoice(s.Name, $"Your layout, kept in place {s.Slot}.", layout, s.Slot) : null)
        .OfType<LayoutChoice>();

    // ----- The flyout -----

    /// <summary>The flyout is about to open: its rows made for this mode's layouts, the side window's tick, and its place beside the button rested on.</summary>
    private void OpeningLayoutFlyout()
    {
        UpdateSideWindowCheck();
        FillLayoutRows(BuiltInLayoutRows, BuiltInLayouts());
        var saved = SavedLayoutChoices().ToList();
        FillLayoutRows(SavedLayoutRows, saved);
        SavedLayoutsTitle.Visibility = saved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var host = _layoutHover.OpenedFrom;
        LayoutFlyout.PlacementTarget = host;
        LayoutFlyoutPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = LayoutFlyoutPanel.DesiredSize.Width;
        if (ReferenceEquals(host, LayoutButton))
        {
            PlaceHeaderFlyout(LayoutFlyout, LayoutButton, width);
        }
        else
        {
            LayoutFlyout.Placement = PlacementMode.Bottom;
            (LayoutFlyout.HorizontalOffset, LayoutFlyout.VerticalOffset) = (host.ActualWidth - width, 6);
        }
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
                _layoutHover.Close();
                TakeUpLayout(choice);
            };
            if (choice.Slot is { } slot)
            {
                // The flyout stays while its menu is open (the pointer is on the menu, not on the flyout).
                var delete = new MenuItem { Header = $"Delete \"{choice.Name}\"" };
                delete.Click += (_, _) => DeleteSavedLayout(slot);
                var menu = new ContextMenu { Items = { delete } };
                menu.Opened += (_, _) => _layoutRowMenuOpen = true;
                menu.Closed += (_, _) =>
                {
                    _layoutRowMenuOpen = false;
                    _layoutHover.Release();
                };
                row.ContextMenu = menu;
            }

            rows.Children.Add(row);
        }
    }

    // A saved layout's right-click menu is open (the flyout stays open under it).
    private bool _layoutRowMenuOpen;

    private void TakeUpLayout(LayoutChoice choice)
    {
        _dock.Apply(_dock.Prepared(choice.Layout));
        _viewModel.StatusText = $"Layout: {choice.Name}.";
    }

    private void LayoutFlyout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        _layoutHover.Close();
        e.Handled = true;
    }

    /// <summary>From the keyboard: Down (or the menu key) on the Layout button opens its flyout, with the first row chosen.</summary>
    private void LayoutButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Apps) || sender is not FrameworkElement button)
            return;

        _layoutHover.Open(button);
        Dispatcher.BeginInvoke(() => LayoutFlyoutPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), System.Windows.Threading.DispatcherPriority.Input);
        e.Handled = true;
    }

    // ----- The user's own layouts -----

    /// <summary>Save Current Layout: the panes as they are now, named, into one of this mode's three places.</summary>
    private void SaveCurrentLayout_Click(object sender, RoutedEventArgs e)
    {
        _layoutHover.Close();
        var kept = AppSettings.Current.SavedLayouts;
        var dialog = new SaveLayoutDialog(_viewModel.IsEditorMode ? AppSettings.EditorMode : AppSettings.EncoderMode, kept) { Owner = this };
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
        _layoutHover.Close();
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

    /// <summary>A click on the Layout button: the Layout Selector, under it (beside it, while the header is a strip).</summary>
    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _layoutHover.Close();
        if (sender is not FrameworkElement button)
            return;

        _layoutBeforePreview = _dock.Save();
        (_previewing, _layoutKept) = (-1, false);
        MakeLayoutCards();
        PlaceLayoutSelector(button);
        LayoutSelector.IsOpen = true;
        Dispatcher.BeginInvoke(() => LayoutCards.Children.OfType<Button>().FirstOrDefault()?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
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
        var key = string.Join("|", _cardLayouts.Select(l => System.Text.Json.JsonSerializer.Serialize(l))) + "|" + string.Join(",", choices.Select(c => c.Name))
            + "|" + string.Join(",", _viewModel.IsEditorMode, _viewModel.IsMediaBrowserVisible, _viewModel.ShowKeyframesPane, _viewModel.ShowEffectsPane,
                _viewModel.ShowCutSegmentsPane, _viewModel.IsArrangeActive, _viewModel.ShowAdvancedPlayback, _viewModel.ShowPresetBarAtTop, _viewModel.ShowCommandPreviewTab)
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

            var card = new Button { Content = new StackPanel { Children = { picture, name, kind } }, ToolTip = choice.About };
            card.SetResourceReference(StyleProperty, "LayoutCard");
            AutomationProperties.SetName(card, choice.Name);
            AutomationProperties.SetHelpText(card, choice.About);
            card.MouseEnter += (_, _) => PreviewLayout(index);
            card.Click += (_, _) => KeepLayout(index, choice.Name);
            LayoutCards.Children.Add(card);
        }
    }

    /// <summary>
    /// Where the selector opens: under the button, its right edge at the button's (the header is at the right), or beside
    /// the header while that is a strip; kept inside the panes' area. Placed once: it stays there while the window takes
    /// layout after layout under it (the button itself may move), so the card under the pointer never changes by itself.
    /// </summary>
    private void PlaceLayoutSelector(FrameworkElement button)
    {
        LayoutSelectorPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (width, height) = (LayoutSelectorPanel.DesiredSize.Width, LayoutSelectorPanel.DesiredSize.Height);
        var corner = button.TranslatePoint(new Point(0, 0), MainDockHost);
        double x, y;
        if (ReferenceEquals(button, LayoutButton) && _headerShape == PaneShape.Column)
        {
            var onRight = corner.X + button.ActualWidth / 2 > MainDockHost.ActualWidth / 2;
            (x, y) = (onRight ? corner.X - width - 6 : corner.X + button.ActualWidth + 6, corner.Y);
        }
        else
        {
            (x, y) = (corner.X + button.ActualWidth - width, corner.Y + button.ActualHeight + 6);
        }

        // Inside the panes' area (Encoder Mode's top bar is above it: there it opens just under the bar, above the area's top).
        LayoutSelector.HorizontalOffset = Math.Clamp(x, 0, Math.Max(MainDockHost.ActualWidth - width, 0));
        LayoutSelector.VerticalOffset = Math.Min(y, Math.Max(MainDockHost.ActualHeight - height, 0));
    }

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
        LayoutSelector.IsOpen = false;
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

        LayoutSelector.IsOpen = false;
        e.Handled = true;
    }

    /// <summary>Closed without a card chosen (Escape, a click elsewhere): the panes as they were.</summary>
    private void LayoutSelector_Closed(object? sender, EventArgs e)
    {
        RestoreLayoutBeforePreview();
        (_layoutBeforePreview, _previewing) = (null, -1);
    }
}
