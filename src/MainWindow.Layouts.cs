using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The Layout button: built-in layouts, the user's own, the Layout Selector and the Default layout -----
// The header's Layout button has the Layout Selector (resting on it or a click, Pass 4.1B; 5.4D: the one Layout UI, the click's menu
// retired): a card for each layout, shown in the window while the pointer is on it, its pill making it the Default layout HandPeg opens
// in; a row of themes, put on live the same way; and a footer with each project tab's own layout, the side windows and Save Current
// Layout. A layout arranges the main window; the side windows are left as they are (DockManager.Prepared). The player floats over its
// place (DockPane.Floats), so a preview only moves it.
public partial class MainWindow
{
    /// <summary>
    /// A layout the Layout button offers: its name, what it is for, the arrangement, its place when it is one of the user's own, and
    /// what it sets besides the arrangement (5.4C: the Vertical &amp; Shorts Studio's Media Browser in its Filmstrip view), run when it is
    /// taken up, kept from the Layout Selector or chosen in the first-run window, never while a card is only previewed.
    /// </summary>
    private sealed record LayoutChoice(string Name, string About, DockLayoutState Layout, int? Slot, Action? Then = null);

    // The settings tabs as one group, in their order; the one shown first named.
    private static DockNode TabsGroup(IReadOnlyList<string> tabIds, string? active = null, params string[] without)
    {
        var tabs = DockNode.Group([.. tabIds.Where(id => !without.Contains(id))]);
        if (active is not null && tabs.Panes.Contains(active))
            tabs.Active = active;
        return tabs;
    }

    // The settings tabs' pane ids, in their order: what the window-free layouts (StandardLayout, UpgradeLayout) are given.
    private IReadOnlyList<string> TabIds => [.. _tabPanes.Select(t => t.Id)];

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
    /// playback controls under it; beside it one column of tabs, the effects first and shown, then the keyframes, the Canvas
    /// and the Master Meter (5.3-Stage2, the user's de-clutter: the Canvas no longer takes a part of its own); the header
    /// joined over the media browser at the right; the Sequence Overview Scrubber under all of it (shown: AppSettings.ShowMasterTimeline
    /// is on by default), and under that the timeline, the first tab of the group of settings tabs (4.4-BugHunt).
    /// </summary>
    private DockLayoutState StandardLayout() => StandardLayout(TabIds);

    /// <summary>Standard Editing for the settings tabs given (their pane ids, in order): window-free, for the goldens (dock-standard-layout) too.</summary>
    internal static DockLayoutState StandardLayout(IReadOnlyList<string> tabIds)
    {
        var player = DockNode.Split(true, (DockNode.Group("Player"), 1), (DockNode.Group("Playback"), 1));
        // The Master Meter (5.2B) is a tab beside the effects, keyframes and Canvas: hidden until Tabs and Panes shows it, and taken there
        // by every arrangement that has no place of its own for it (DockManager.PlaceAsDefault).
        var middle = DockNode.Group("Effects", "Keyframes", "Layout", "Meter");
        var side = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        var top = DockNode.Split(false, (player, 56), (middle, 22), (side, 22));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 46), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TimelineFirst(DockNode.Group([.. tabIds])), 38)),
        };
    }

    /// <summary>
    /// Standard Editing as 5.2B to 5.3-Stage1 made it (arrangement version 3): the Canvas over the effects, keyframes and Master
    /// Meter in a column of its own. What step 3 to 4 recognises (StackCanvasWithEffects), and what the 4.4 shape is derived from
    /// (StandardLayoutBeside). Frozen: never change it.
    /// </summary>
    internal static DockLayoutState StandardLayoutV3(IReadOnlyList<string> tabIds)
    {
        var player = DockNode.Split(true, (DockNode.Group("Player"), 1), (DockNode.Group("Playback"), 1));
        var middle = DockNode.Split(true, (DockNode.Group("Layout"), 1), (DockNode.Group("Effects", "Keyframes", "Meter"), 1.3));
        var side = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        var top = DockNode.Split(false, (player, 56), (middle, 22), (side, 22));
        return new DockLayoutState
        {
            Version = 3,
            Main = DockNode.Split(true, (top, 46), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TimelineFirst(DockNode.Group([.. tabIds])), 38)),
        };
    }

    /// <summary>
    /// Standard Editing as 4.4 first made it (the timeline in a group of its own, the settings tabs beside it): what an
    /// arrangement of version 2 taken up as Standard Editing is recognised by (PutTimelineFirst). From the frozen version 3
    /// shape (StandardLayoutV3), never from today's Standard Editing, so the 2 to 3 step keeps finding it.
    /// </summary>
    internal static DockLayoutState StandardLayoutBeside(IReadOnlyList<string> tabIds)
    {
        var state = StandardLayoutV3(tabIds);
        state.Version = 2;

        // 4.4 had no Master Meter (5.2B): its Standard Editing is told by the panes it had.
        state.Main.GroupOf("Meter")?.Panes.Remove("Meter");
        var tabs = state.Main.Children[^1];
        tabs.Panes.Remove(TimelinePaneId);
        tabs.Active = tabs.Panes.FirstOrDefault();
        state.Main.Children[^1] = TimelineBeside(tabs);
        return state;
    }

    /// <summary>
    /// Color and Effects: a large player with the Canvas beside it; two tall inspector columns at the right, the
    /// effects and keyframes, and the header joined over the media browser; under them the Sequence Overview Scrubber, and the
    /// timeline with the settings tabs at its right. (5.4A: the Summary tab that shared the media browser's group is retired.)
    /// </summary>
    internal static DockLayoutState InspectorLayout(IReadOnlyList<string> tabIds)
    {
        var player = DockNode.Split(true, (DockNode.Split(false, (DockNode.Group("Player"), 72), (DockNode.Group("Layout"), 28)), 1), (DockNode.Group("Playback"), 1));
        var inspector = DockNode.Group("Effects", "Keyframes");
        var media = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        var top = DockNode.Split(false, (player, 60), (inspector, 20), (media, 20));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 66), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TimelineBeside(TabsGroup(tabIds)), 34)),
        };
    }

    /// <summary>
    /// Assembly and Media: the player at the top left; a wide media column at the right, the header joined over it at
    /// the top right (as in the other layouts), the Canvas a tab beside the media; the Sequence Overview Scrubber across the whole
    /// window, and under it the timeline, the settings tabs (open on Export) and the effects and keyframes side by side.
    /// </summary>
    internal static DockLayoutState AssemblyLayout(IReadOnlyList<string> tabIds)
    {
        var player = DockNode.Split(true, (DockNode.Group("Player"), 1), (DockNode.Group("Playback"), 1));
        var media = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media", "Layout"), 1));
        var top = DockNode.Split(false, (player, 66), (media, 34));
        var bottom = DockNode.Split(false, (DockNode.Group(TimelinePaneId), 49), (TabsGroup(tabIds, ExportId), 21), (DockNode.Group("Effects", "Keyframes"), 30));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 54), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (bottom, 40)),
        };
    }

    /// <summary>
    /// Vertical and Shorts Studio, for 9:16 shorts and highlights (5.4C, the user's picture of it; 5.4D: the Subtitle Studio a tab of the
    /// left group): at the top a left column of the effects, keyframes, Canvas and Subtitle Studio as tabs, the player in the middle (a 9:16
    /// frame needs little width), and the header joined over the media at the right (its Media Browser in the Filmstrip view:
    /// <see cref="BuiltInLayouts"/>' Then); the playback controls along the bottom of the left column and the player; under all of it the
    /// Sequence Overview Scrubber across the whole window, the Preset Bar, and the timeline, the first tab of the settings tabs (Timeline |
    /// Filters | Audio | Export). The Master Meter, left out, goes in with the effects when shown (its home in Standard Editing).
    /// </summary>
    internal static DockLayoutState ShortsLayout(IReadOnlyList<string> tabIds)
    {
        var left = DockNode.Group("Effects", "Keyframes", "Layout", SubtitleStudioId);
        var stage = DockNode.Split(false, (left, 40), (DockNode.Group("Player"), 60));
        var body = DockNode.Split(true, (stage, 1), (DockNode.Group("Playback"), 1));
        var right = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        var top = DockNode.Split(false, (body, 70), (right, 30));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 60), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TimelineFirst(TabsGroup(tabIds)), 40)),
        };
    }

    /// <summary>
    /// Vertical Creator Split (5.4D, the user's picture of it): the player at the left, the whole height of the window (a 9:16 frame stands
    /// tall); in the middle the effects, keyframes, Canvas and Subtitle Studio as tabs over the playback controls, and under them the
    /// Sequence Overview Scrubber, the Preset Bar and the timeline, the first tab of the settings tabs; at the right the header joined over
    /// the media (its Media Browser in the Filmstrip view: <see cref="BuiltInLayouts"/>' Then). The Master Meter, left out, goes in with the
    /// effects when shown.
    /// </summary>
    internal static DockLayoutState VerticalCreatorLayout(IReadOnlyList<string> tabIds)
    {
        var tools = DockNode.Group("Effects", "Keyframes", "Layout", SubtitleStudioId);
        var center = DockNode.Split(true, (tools, 48), (DockNode.Group("Playback"), 1), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1),
            (TimelineFirst(TabsGroup(tabIds)), 52));
        var right = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        return new DockLayoutState { Main = DockNode.Split(false, (DockNode.Group("Player"), 28), (center, 46), (right, 26)) };
    }

    /// <summary>
    /// What taking up the Vertical &amp; Shorts Studio or the Vertical Creator Split sets besides the arrangement: the Media Browser in its
    /// Filmstrip view (kept in the settings).
    /// </summary>
    private void ShowShortsMedia() => MediaBrowser.ViewMode = Views.MediaBrowserView.Filmstrip;

    private IEnumerable<LayoutChoice> BuiltInLayouts() =>
    [
        new("Standard Editing", "The player at the top left, the effects, keyframes and Canvas as tabs beside it, the media at the right; the Sequence Overview Scrubber, and the timeline below, the first of the settings tabs. Where HandPeg starts.", StandardLayout(), null),
        new("Color & Effects", "A large player, tall effects and keyframes and media columns at the right; the timeline below, with the other tabs beside it.", InspectorLayout(TabIds), null),
        new("Assembly & Media", "The player at the left, a wide media column with the Canvas at the right; the timeline, the settings tabs and the effects and keyframes side by side below.", AssemblyLayout(TabIds), null),
        new("Vertical & Shorts Studio", "For 9:16 shorts: the effects, keyframes, Canvas and Subtitle Studio as tabs at the left, a tall player in the middle with the playback controls under both, the media as a filmstrip at the right; the timeline below, the first of the settings tabs.", ShortsLayout(TabIds), null, ShowShortsMedia),
        new(VerticalCreatorName, "For 9:16 creators: the player the whole height at the left; in the middle the effects, keyframes, Canvas and Subtitle Studio as tabs over the playback controls, the timeline below them; the media as a filmstrip at the right.", VerticalCreatorLayout(TabIds), null, ShowShortsMedia),
    ];

    /// <summary>The Vertical Creator Split's name (5.4D), as the Layout Selector, the first-run window and AppSettings.DefaultStartupLayout know it.</summary>
    internal const string VerticalCreatorName = "Vertical Creator Split";

    /// <summary>The five built-in layouts by name, for the settings tabs given: window-free, for the goldens (header-flyouts-placement, dock-splitter-range).</summary>
    internal static IReadOnlyList<(string Name, DockLayoutState Layout)> BuiltInLayoutStates(IReadOnlyList<string> tabIds) =>
    [
        ("Standard Editing", StandardLayout(tabIds)), ("Color & Effects", InspectorLayout(tabIds)),
        ("Assembly & Media", AssemblyLayout(tabIds)), ("Vertical & Shorts Studio", ShortsLayout(tabIds)),
        (VerticalCreatorName, VerticalCreatorLayout(tabIds)),
    ];

    /// <summary>
    /// The built-in layout chosen in the first-run window (AppSettings.FirstRunLayout) as an arrangement to load: written
    /// into AppSettings.DockLayout, and the choice cleared; it becomes the Default layout too (5.4D: HandPeg opens in it from then on).
    /// Null when no choice is waiting (or it names no layout).
    /// </summary>
    private string? TakeFirstRunLayout()
    {
        if (AppSettings.Current.FirstRunLayout is not { } name)
            return null;

        var chosen = BuiltInLayouts().FirstOrDefault(l => l.Name == name);
        var json = chosen is not null ? System.Text.Json.JsonSerializer.Serialize(chosen.Layout, AppSettingsJsonContext.Default.DockLayoutState) : null;
        _viewModel.SavePaneLayout(s => (s.DockLayout, s.FirstRunLayout, s.DefaultStartupLayout) = (json ?? s.DockLayout, null, chosen is not null ? chosen.Name : s.DefaultStartupLayout));
        chosen?.Then?.Invoke();
        return json;
    }

    // ----- The Default layout (5.4D): where HandPeg opens, and what Reset Layout puts back -----

    /// <summary>The Default layout when none is chosen (AppSettings.DefaultStartupLayout's own default), and when the user's own one chosen is deleted.</summary>
    internal const string DefaultLayoutName = "Standard Editing";

    /// <summary>How AppSettings.DefaultStartupLayout names a layout: a built-in one by its name, one of the user's own by its place ("saved:2"), so a rename never loses it.</summary>
    private static string LayoutKey(LayoutChoice choice) => choice.Slot is { } slot ? $"saved:{slot}" : choice.Name;

    /// <summary>The layout a key names (<see cref="LayoutKey"/>): among the built-in layouts, then the user's own; null for none, an empty key or one no longer there.</summary>
    private LayoutChoice? FindLayout(string? key) => string.IsNullOrWhiteSpace(key) ? null
        : BuiltInLayouts().FirstOrDefault(l => l.Name == key) ?? SavedLayoutChoices().FirstOrDefault(l => LayoutKey(l) == key);

    /// <summary>The user's Default layout (AppSettings.DefaultStartupLayout); null when it names none.</summary>
    private LayoutChoice? DefaultLayoutChoice() => FindLayout(AppSettings.Current.DefaultStartupLayout);

    /// <summary>
    /// The arrangement the window opens in (5.4D, CLAUDE.md 2 and 8: cold boot starts in the user's Default layout), for the one dock build
    /// before the first frame: the arrangement as it was left while it still has the Default layout's shape (DockManager.LoadsAlike: the
    /// borders the user dragged survive), else the Default layout itself, its Then run first (the Media Browser's Filmstrip view); with no
    /// Default layout found (an empty name, one unknown, a saved layout deleted), the arrangement as it was left. Builds nothing.
    /// <para>With no Default at all (null or empty: settings from before 5.4D, AppSettings.Load's step 9, so an upgrade keeps the arrangement
    /// the user had), the built-in layout the arrangement left has the shape of becomes the Default, saved (no layout made for it), so its
    /// card has the ★.</para>
    /// </summary>
    private string? StartupLayoutJson()
    {
        var last = AppSettings.Current.DockLayout;
        if (string.IsNullOrEmpty(AppSettings.Current.DefaultStartupLayout))
        {
            if (BuiltInLayouts().FirstOrDefault(l => _dock.LoadsAlike(last, l.Layout, AppSettings.Current.SideWindows)) is { } alike)
                _viewModel.SavePaneLayout(s => s.DefaultStartupLayout = alike.Name);
            return last;
        }

        if (DefaultLayoutChoice() is not { } chosen || _dock.LoadsAlike(last, chosen.Layout, AppSettings.Current.SideWindows))
            return last;

        chosen.Then?.Invoke();
        return System.Text.Json.JsonSerializer.Serialize(chosen.Layout, AppSettingsJsonContext.Default.DockLayoutState);
    }

    /// <summary>
    /// Reset Layout (the Views menu's Reset Panes Layout, 5.4D): the user's Default layout put back (Standard Editing when it names none),
    /// every Pane Docker closed (DockManager.Reset), and what the layout sets besides set (its Then).
    /// </summary>
    private void ResetToDefaultLayout()
    {
        var choice = DefaultLayoutChoice();
        _dock.Reset(choice?.Layout);
        FinishRecordingUnlessShown();
        choice?.Then?.Invoke();
        _viewModel.StatusText = $"Layout reset to {choice?.Name ?? DefaultLayoutName}.";
    }

    /// <summary>A card's pill clicked (5.4D): its layout is the one HandPeg opens in from now on, saved at once; the cards drawn again with the ★ on it.</summary>
    private void SetDefaultLayout(LayoutChoice choice)
    {
        _viewModel.SavePaneLayout(s => s.DefaultStartupLayout = LayoutKey(choice));
        RemakeLayoutCards();
        _viewModel.StatusText = $"Default layout: {choice.Name}. HandPeg opens in it, and Reset Layout puts it back.";
    }

    /// <summary>
    /// The cards drawn again with the selector open (the Default layout changed, a layout deleted). From the keyboard, the keyboard is kept on
    /// the card it was on; from the pointer, it goes back where it was before the selector opened (<see cref="LeaveSelectorToPointer"/>).
    /// </summary>
    private void RemakeLayoutCards()
    {
        var focused = LayoutCards.Children.OfType<Button>().ToList().FindIndex(c => c.IsKeyboardFocusWithin);
        _cardsKey = null;
        _remakingCards = true;
        try
        {
            MakeLayoutCards();
        }
        finally
        {
            _remakingCards = false;
        }

        if (!FromKeyboard)
            LeaveSelectorToPointer();
        else if (focused >= 0 && LayoutCards.Children.Count > 0)
            (LayoutCards.Children[Math.Min(focused, LayoutCards.Children.Count - 1)] as Button)?.Focus();
    }

    // The cards being drawn again (RemakeLayoutCards): the card with the keyboard taken out is no move of the keyboard off the cards.
    private bool _remakingCards;

    // What had the keyboard as the Layout Selector opened (OpeningLayoutSelector), to give it back after a click in the selector.
    private IInputElement? _focusBeforeSelector;

    /// <summary>Whether what is being done now came from the keyboard (Enter or Space on a pill, a CheckBox, the Layout button), not the pointer, a touch or a pen.</summary>
    private static bool FromKeyboard => InputManager.Current.MostRecentInputDevice is KeyboardDevice;

    /// <summary>
    /// After a click with the pointer on a pill, a "×" or the footer's CheckBox (5.4D): the keyboard goes back where it was before the selector
    /// opened (nothing has it when that is gone), and the selector lets go, so the pointer leaving it closes it again (HoverFlyout holds it
    /// open while it has the keyboard).
    /// </summary>
    private void LeaveSelectorToPointer()
    {
        if (!(_focusBeforeSelector is UIElement { IsVisible: true } before && !before.IsDescendantOf(LayoutSelectorPanel) && before.Focus()))
            Keyboard.ClearFocus();
        _layoutHover.Release();
    }

    /// <summary>The first-run window opened again from the settings: the layout chosen there, put on at once.</summary>
    public void ApplyFirstRunLayout()
    {
        if (TakeFirstRunLayout() is { } json && DockLayoutState.Read(json) is { } layout)
        {
            _dock.Apply(_dock.Prepared(layout));
            FinishRecordingUnlessShown();
        }
    }

    /// <summary>The user's own layouts, by place.</summary>
    private IEnumerable<LayoutChoice> SavedLayoutChoices() => AppSettings.Current.SavedLayouts.OrderBy(s => s.Slot)
        .Select(s => DockLayoutState.Read(s.Layout) is { } layout ? new LayoutChoice(s.Name, $"Your layout, kept in place {s.Slot}.", layout, s.Slot) : null)
        .OfType<LayoutChoice>();

    // ----- Opening the Layout Selector (5.4D: a click as well as resting on the button; its menu retired) -----

    /// <summary>
    /// A click on the Layout button: the Layout Selector, when it is not open already (a click usually lands after resting on the button
    /// has opened it, so it never closes it: Escape and the pointer leaving it do). Enter or Space (a click from the keyboard) opens it with
    /// its first card chosen, as Down does.
    /// </summary>
    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button)
            return;

        if (FromKeyboard)
            OpenLayoutSelectorOnFirstCard(button);
        else if (!LayoutSelector.IsOpen)
            _layoutHover.Open(button);
    }

    /// <summary>
    /// From the keyboard: Down or the menu key on the Layout button opens the Layout Selector (when it is not open) with its first card
    /// chosen (a card with the keyboard is shown as one under the pointer is). Enter and Space click it (the same). Escape closes it, the
    /// keyboard still on the button.
    /// </summary>
    private void LayoutButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && LayoutSelector.IsOpen)
        {
            _layoutHover.Close();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Down or Key.Apps) || sender is not FrameworkElement button)
            return;

        OpenLayoutSelectorOnFirstCard(button);
        e.Handled = true;
    }

    /// <summary>The Layout Selector opened from the keyboard (when it is not open), its first card given the keyboard once it is up.</summary>
    private void OpenLayoutSelectorOnFirstCard(FrameworkElement button)
    {
        if (!LayoutSelector.IsOpen)
            _layoutHover.Open(button);
        Dispatcher.BeginInvoke(() => LayoutCards.Children.OfType<Button>().FirstOrDefault()?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ----- The user's own layouts -----

    /// <summary>
    /// Save Current Layout (the Layout Selector's footer): the panes as they are now, named, into one of the three places. A layout only
    /// previewed is put back and the selector closed first, so the modal window never saves a preview (nor opens under an open popup).
    /// </summary>
    private void SaveCurrentLayout_Click(object sender, RoutedEventArgs e)
    {
        RestoreLayoutBeforePreview();
        _layoutHover.Close();
        var kept = AppSettings.Current.SavedLayouts;
        var dialog = new SaveLayoutDialog(kept) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var layout = _dock.Save();
        var saved = kept.Where(s => s.Slot != dialog.Slot).Append(new SavedDockLayout { Slot = dialog.Slot, Name = dialog.LayoutName, Layout = layout }).OrderBy(s => s.Slot).ToList();
        _viewModel.SavePaneLayout(s => s.SavedLayouts = saved);
        _cardsKey = null;

        // Written over the Default layout's place, it is the Default still (the place is the key, LayoutKey): said, so it is never a surprise.
        _viewModel.StatusText = AppSettings.Current.DefaultStartupLayout == $"saved:{dialog.Slot}"
            ? $"Layout saved as \"{dialog.LayoutName}\": your Default layout (place {dialog.Slot}), so HandPeg opens in it and Reset Layout puts it back."
            : $"Layout saved as \"{dialog.LayoutName}\" (place {dialog.Slot}): the Layout Selector offers it.";
    }

    /// <summary>
    /// A saved card's "×" (5.4D): the user's own layout in that place deleted, the selector left open and its cards drawn again; the layout
    /// only previewed is put back first. When it was the Default layout, Standard Editing is the Default again.
    /// </summary>
    private void DeleteSavedLayout(int slot)
    {
        RestoreLayoutBeforePreview();
        var name = AppSettings.Current.SavedLayouts.FirstOrDefault(s => s.Slot == slot)?.Name;
        var wasDefault = AppSettings.Current.DefaultStartupLayout == $"saved:{slot}";
        _viewModel.SavePaneLayout(s =>
        {
            s.SavedLayouts = s.SavedLayouts.Where(l => l.Slot != slot).ToList();
            if (wasDefault)
                s.DefaultStartupLayout = DefaultLayoutName;
        });
        RemakeLayoutCards();
        _viewModel.StatusText = wasDefault ? $"Layout \"{name}\" deleted: HandPeg opens in {DefaultLayoutName} again." : $"Layout \"{name}\" deleted.";
    }

    /// <summary>The footer's "Each project tab remembers its own layout" (5.4D; the same setting as the Settings window's): saved at once.</summary>
    private void PerTabLayoutsBox_Click(object sender, RoutedEventArgs e)
    {
        var on = PerTabLayoutsBox.IsChecked == true;
        _viewModel.SavePaneLayout(s => s.PerTabWindowLayouts = on);
        _viewModel.StatusText = on ? "Each project tab remembers its own layout." : "Every project tab shares the window's layout.";
        if (!FromKeyboard)
            LeaveSelectorToPointer();
    }

    /// <summary>The footer's "Projects open in their saved window" (5.4.1-M5; the same setting as the Settings window's): saved at once.</summary>
    private void RestoreWorkspaceBox_Click(object sender, RoutedEventArgs e)
    {
        var on = RestoreWorkspaceBox.IsChecked == true;
        _viewModel.SavePaneLayout(s => s.RestoreProjectWorkspaceOnOpen = on);
        _viewModel.StatusText = on ? "Projects open in the window they were saved in." : "Projects open in the window as it is.";
        if (!FromKeyboard)
            LeaveSelectorToPointer();
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
        _focusBeforeSelector = Keyboard.FocusedElement;
        _layoutBeforePreview = _dock.Save();
        _themeBeforePreview = ThemeManager.Current;
        (_previewing, _layoutKept, _themeKept) = (-1, false, false);
        MakeLayoutCards();
        MakeThemeCards();

        // The footer (5.4D): the setting as it is now (the Settings window sets it too), and a row for each side window open.
        PerTabLayoutsBox.IsChecked = AppSettings.Current.PerTabWindowLayouts;
        RestoreWorkspaceBox.IsChecked = AppSettings.Current.RestoreProjectWorkspaceOnOpen;
        UpdateSideWindowCheck();
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

        // The Default layout's card (5.4D): its key, as AppSettings.DefaultStartupLayout has it (and so in the cards' key, below).
        var defaultKey = DefaultLayoutChoice() is { } chosen ? LayoutKey(chosen) : null;
        var key = string.Join("", active.Select(a => a ? '1' : '0')) + "|" + string.Join("|", _cardLayouts.Select(l => System.Text.Json.JsonSerializer.Serialize(l, AppSettingsJsonContext.Default.DockLayoutState)))
            + "|" + string.Join(",", choices.Select(c => c.Name)) + "|" + defaultKey
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

            // Its pill at the picture's top right (5.4D): "★ Default" on the Default layout, always shown; "☆ Set Default" on the others, and
            // "×" on the user's own, while the card is pointed at or has the keyboard. Their clicks are their own (Handled), never the card's.
            var isDefault = LayoutKey(choice) == defaultKey;
            var pills = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0) };
            var pill = new Button
            {
                Content = isDefault ? "★ Default" : "☆ Set Default", Tag = isDefault ? "default" : null,
                ToolTip = isDefault ? $"{choice.Name} is your Default layout: HandPeg opens in it, and Reset Layout puts it back." : $"Make {choice.Name} your Default layout: HandPeg opens in it, and Reset Layout puts it back.",
            };
            pill.SetResourceReference(StyleProperty, "LayoutPill");
            AutomationProperties.SetName(pill, isDefault ? $"{choice.Name}: the Default layout" : $"Set {choice.Name} as the Default layout");
            pill.Click += (_, e) =>
            {
                e.Handled = true;
                if (!isDefault)
                    SetDefaultLayout(choice);
            };
            pills.Children.Add(pill);
            Button? delete = null;
            if (choice.Slot is { } place)
            {
                delete = new Button { Content = "×", ToolTip = $"Deletes your layout \"{choice.Name}\" (place {place})." };
                delete.SetResourceReference(StyleProperty, "LayoutPill");
                AutomationProperties.SetName(delete, $"Delete {choice.Name}");
                delete.Click += (_, e) =>
                {
                    e.Handled = true;
                    DeleteSavedLayout(place);
                };
                pills.Children.Add(delete);
            }

            var face = new Grid { Children = { picture, pills } };
            var name = new TextBlock { Text = choice.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 6, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = CardPictureWidth + 4 };
            var kind = new TextBlock { Text = choice.Slot is { } slot ? $"Saved, place {slot}" : "Built-in", Margin = new Thickness(2, 1, 2, 0), FontSize = 11 };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var footer = new DockPanel { LastChildFill = true };
            if (active[i])
                footer.Children.Add(InUseBadge());
            footer.Children.Add(kind);

            var card = new Button { Content = new StackPanel { Children = { face, name, footer } }, ToolTip = active[i] ? $"In use. {choice.About}" : choice.About };
            card.SetResourceReference(StyleProperty, "LayoutCard");
            card.Tag = active[i] ? "active" : null;
            var named = isDefault ? $"{choice.Name}, the Default layout" : choice.Name;
            AutomationProperties.SetName(card, active[i] ? $"{named} (in use)" : named);
            AutomationProperties.SetHelpText(card, choice.About);
            void ShowPills()
            {
                var pointed = card.IsMouseOver || card.IsKeyboardFocusWithin;
                pill.Visibility = isDefault || pointed ? Visibility.Visible : Visibility.Hidden;
                if (delete is not null)
                    delete.Visibility = pointed ? Visibility.Visible : Visibility.Collapsed;
            }

            ShowPills();
            card.MouseEnter += (_, _) =>
            {
                ShowPills();
                PreviewLayout(index);
            };
            card.MouseLeave += (_, _) => ShowPills();
            card.IsKeyboardFocusWithinChanged += (_, _) => ShowPills();
            card.GotKeyboardFocus += (_, _) => PreviewLayout(index);
            card.Click += (_, _) => KeepLayout(index, choice);
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
    /// the header while that is a strip; kept inside the panes' area, 8 DIPs from its left. Placed once: it stays there while
    /// the window takes layout after layout under it (the button itself moves, and is taken out and put back), so the card
    /// under the pointer never changes by itself.
    /// </summary>
    /// <remarks>
    /// 5.3-Stage3 (the user's bug): a placement of its own in DIPs of the panes' area, given to WPF as a Relative popup, was mirrored
    /// on a touch PC set right-handed (SystemParameters.MenuDropAlignment), a whole selector's width to the left. Now the header's
    /// one device-pixel placement (UiScale.PlaceAgainst), the button's rectangle taken as it opens and kept, measured from the panes'
    /// area, which no layout moves: proof, header-flyouts-placement.
    /// </remarks>
    private void PlaceLayoutSelector(FrameworkElement button) => PlaceHeaderFlyout(LayoutSelector, button, MainDockHost, SelectorEdge);

    private const double SelectorEdge = 8;

    /// <summary>The pointer came onto a card: the window takes its layout, nothing saved.</summary>
    private void PreviewLayout(int index)
    {
        if (index == _previewing || index >= _cardLayouts.Count)
            return;

        _previewing = index;
        _dock.Preview(_cardLayouts[index]);
    }

    /// <summary>A card clicked: its layout is kept (it is already laid out) and saved, and what it sets besides is set (the preview set nothing).</summary>
    private void KeepLayout(int index, LayoutChoice choice)
    {
        if (index >= _cardLayouts.Count)
            return;

        _layoutKept = true;
        _previewing = index;
        _dock.Apply(_cardLayouts[index]);
        FinishRecordingUnlessShown();
        choice.Then?.Invoke();
        _layoutHover.Close();
        _viewModel.StatusText = $"Layout: {choice.Name}.";
    }

    /// <summary>The pointer left the cards: the panes as they were.</summary>
    private void LayoutCards_MouseLeave(object sender, MouseEventArgs e) => RestoreLayoutBeforePreview();

    /// <summary>
    /// The keyboard left the cards (5.4D: Tab to the footer, a click elsewhere) with the selector still open: the panes as they were, as when
    /// the pointer leaves them, so nothing done next (+ New Side Window, a side window closed) saves a layout only previewed. Not while the
    /// cards are drawn again, nor while the pointer is on them (the card under it is the one shown).
    /// </summary>
    private void LayoutCards_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && LayoutSelector.IsOpen && !_remakingCards && !LayoutCards.IsMouseOver)
            RestoreLayoutBeforePreview();
    }

    /// <summary>The window no longer the active one (5.4D, Alt+Tab): a layout only previewed put back, and the Layout Selector closed (it is topmost).</summary>
    private void CloseLayoutSelectorOnDeactivated()
    {
        if (!LayoutSelector.IsOpen)
            return;

        RestoreLayoutBeforePreview();
        _layoutHover.Close();
    }

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
        (_layoutBeforePreview, _previewing, _themeBeforePreview, _focusBeforeSelector) = (null, -1, null, null);
    }
}
