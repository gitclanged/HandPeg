using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Layouts: built-in, saved, Layout Selector, Default layout -----
// The header Layout button opens the Layout Selector (hover or click): layout cards previewed live on hover (pill sets the Default
// startup layout), a live-preview theme row, and a footer (per-tab layouts, side windows, Save Current Layout). Layouts arrange the
// main window only; side windows are untouched (DockManager.Prepared). The player floats over its slot (DockPane.Floats), so previews
// only move it.
public partial class MainWindow
{
    /// <summary>
    /// A selectable layout: name, description, arrangement, saved slot (user layouts), and <c>Then</c>, a side effect (e.g. Media Browser
    /// Filmstrip view) run when applied, kept, or chosen at first run; never during a hover preview.
    /// </summary>
    private sealed record LayoutChoice(string Name, string About, DockLayoutState Layout, int? Slot, Action? Then = null);

    // Settings tabs as one group, in order, with the initially selected tab.
    private static DockNode TabsGroup(IReadOnlyList<string> tabIds, string? active = null, params string[] without)
    {
        var tabs = DockNode.Group([.. tabIds.Where(id => !without.Contains(id))]);
        if (active is not null && tabs.Panes.Contains(active))
            tabs.Active = active;
        return tabs;
    }

    // Settings tab pane ids in order, passed to window-free layout builders (StandardLayout, UpgradeLayout).
    private IReadOnlyList<string> TabIds => [.. _tabPanes.Select(t => t.Id)];

    /// <summary>
    /// Bottom row of every built-in layout except Standard Editing: timeline with settings tabs to its right (70:30, TimelineShare). Also
    /// the target shape of UpgradeLayout, so "In use" still matches upgraded legacy layouts; legacy Standard Editing continues via
    /// PutTimelineFirst.
    /// </summary>
    private static DockNode TimelineBeside(DockNode tabs) => DockNode.Split(false, (DockNode.Group(TimelinePaneId), 70), (tabs, 30));

    /// <summary>
    /// Main tab group with the timeline as first, selected tab: full bottom width for the timeline, settings tabs one click away.
    /// </summary>
    private static DockNode TimelineFirst(DockNode tabs)
    {
        tabs.Panes.Remove(TimelinePaneId);
        tabs.Panes.Insert(0, TimelinePaneId);
        tabs.Active = TimelinePaneId;
        return tabs;
    }

    /// <summary>
    /// Standard Editing (default start and Reset Layout target). Top row: player with playback controls below | tab column (Effects
    /// selected, Keyframes, Canvas, Master Meter) | header over Media | Export | settings tabs (Media selected). Then the Sequence
    /// Overview Scrubber (AppSettings.ShowMasterTimeline, on by default) and the timeline alone, headerless (DockPane.HeaderlessAlone).
    /// </summary>
    private DockLayoutState StandardLayout() => StandardLayout(TabIds);

    /// <summary>Window-free Standard Editing builder for the given settings tab ids (golden dock-standard-layout).</summary>
    internal static DockLayoutState StandardLayout(IReadOnlyList<string> tabIds)
    {
        var player = DockNode.Split(true, (DockNode.Group("Player"), 1), (DockNode.Group("Playback"), 1));
        // Master Meter: a hidden tab beside Effects/Keyframes/Canvas until shown via Tabs and Panes; also its fallback home in
        // arrangements without a slot for it (DockManager.PlaceAsDefault).
        var middle = DockNode.Group("Effects", "Keyframes", "Layout", "Meter");
        var side = DockNode.Split(true, (DockNode.Group("Header"), 1), (MediaFirst(DockNode.Group([.. tabIds])), 1));
        var top = DockNode.Split(false, (player, StandardTopShares[0]), (middle, StandardTopShares[1]), (side, StandardTopShares[2]));
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 46), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (DockNode.Group(TimelinePaneId), 38)),
        };
    }

    /// <summary>
    /// Standard Editing top-row shares: player 54, effects 20, Media 26 (Media also hosts Export, which needs 300 DIPs; at 1280 DIPs the
    /// splitter still has room both ways).
    /// </summary>
    internal static readonly double[] StandardTopShares = [54, 20, 26];

    /// <summary>Settings tab group with the Media Browser first and selected (Media | Export | ...).</summary>
    private static DockNode MediaFirst(DockNode tabs)
    {
        tabs.Panes.Remove("Media");
        tabs.Panes.Insert(0, "Media");
        tabs.Active = "Media";
        return tabs;
    }

    /// <summary>
    /// Frozen arrangement v4 Standard Editing (media browser alone under the header; timeline first in the bottom settings group), matched
    /// by the v4-to-v5 migration (MoveExportBesideMedia). Never change it.
    /// </summary>
    internal static DockLayoutState StandardLayoutV4(IReadOnlyList<string> tabIds)
    {
        var player = DockNode.Split(true, (DockNode.Group("Player"), 1), (DockNode.Group("Playback"), 1));
        var middle = DockNode.Group("Effects", "Keyframes", "Layout", "Meter");
        var side = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        var top = DockNode.Split(false, (player, 56), (middle, 22), (side, 22));
        return new DockLayoutState
        {
            Version = 4,
            Main = DockNode.Split(true, (top, 46), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (TimelineFirst(DockNode.Group([.. tabIds])), 38)),
        };
    }

    /// <summary>
    /// Frozen arrangement v3 Standard Editing (Canvas above Effects/Keyframes/Master Meter in its own column), matched by the v3-to-v4
    /// migration (StackCanvasWithEffects) and the base of the v2 shape (StandardLayoutBeside). Never change it.
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
    /// Frozen arrangement v2 Standard Editing (timeline in its own group beside the settings tabs), matched by the v2-to-v3 migration
    /// (PutTimelineFirst). Derived from the frozen v3 shape (StandardLayoutV3), never the current one, so the match stays stable.
    /// </summary>
    internal static DockLayoutState StandardLayoutBeside(IReadOnlyList<string> tabIds)
    {
        var state = StandardLayoutV3(tabIds);
        state.Version = 2;

        // v2 predates the Master Meter: match on the panes it had.
        state.Main.GroupOf("Meter")?.Panes.Remove("Meter");
        var tabs = state.Main.Children[^1];
        tabs.Panes.Remove(TimelinePaneId);
        tabs.Active = tabs.Panes.FirstOrDefault();
        state.Main.Children[^1] = TimelineBeside(tabs);
        return state;
    }

    /// <summary>
    /// Color and Effects: large player with Canvas beside it; tall Effects and Keyframes columns plus header over the media browser at
    /// the right; then the Sequence Overview Scrubber and the timeline with settings tabs to its right.
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
    /// Assembly and Media: player top-left; wide media column at the right under the header, Canvas as a tab beside Media; full-width
    /// Sequence Overview Scrubber; bottom row of timeline, settings tabs (Export selected), and Effects/Keyframes side by side.
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
    /// Vertical and Shorts Studio (9:16): top row of a left tab column (Effects, Keyframes, Canvas, Subtitle Studio), a narrow centre
    /// player, and header over Media at the right (Filmstrip view via <see cref="BuiltInLayouts"/>' Then); playback controls under the left
    /// column and player; then full-width Scrubber, Preset Bar, and timeline first in the settings tabs (Timeline | Filters | Audio |
    /// Export). Master Meter, when shown, joins the Effects group.
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
    /// Vertical Creator Split: full-height player at the left (9:16); centre column of tabs (Effects, Keyframes, Canvas, Subtitle Studio)
    /// over playback controls, Scrubber, Preset Bar and timeline (first settings tab); header over Media at the right (Filmstrip view via
    /// <see cref="BuiltInLayouts"/>' Then). Master Meter, when shown, joins the Effects group.
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
    /// <c>Then</c> for both vertical layouts: switch the Media Browser to Filmstrip view (persisted in settings).
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

    /// <summary>Vertical Creator Split name (shared by the Layout Selector, first-run window and AppSettings.DefaultStartupLayout).</summary>
    internal const string VerticalCreatorName = "Vertical Creator Split";

    /// <summary>Window-free list of the five built-in layouts for the given settings tabs (goldens header-flyouts-placement, dock-splitter-range).</summary>
    internal static IReadOnlyList<(string Name, DockLayoutState Layout)> BuiltInLayoutStates(IReadOnlyList<string> tabIds) =>
    [
        ("Standard Editing", StandardLayout(tabIds)), ("Color & Effects", InspectorLayout(tabIds)),
        ("Assembly & Media", AssemblyLayout(tabIds)), ("Vertical & Shorts Studio", ShortsLayout(tabIds)),
        (VerticalCreatorName, VerticalCreatorLayout(tabIds)),
    ];

    /// <summary>
    /// Consumes AppSettings.FirstRunLayout: writes the chosen built-in arrangement to AppSettings.DockLayout, makes it the Default layout,
    /// and clears the choice. Null when none is pending or the name is unknown.
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

    // ----- Default layout: startup arrangement and Reset Layout target -----

    /// <summary>Fallback Default layout (AppSettings.DefaultStartupLayout default; also used when a chosen saved layout is deleted).</summary>
    internal const string DefaultLayoutName = "Standard Editing";

    /// <summary>Layout key for AppSettings.DefaultStartupLayout: built-in name, or saved slot ("saved:2") so renames keep the link.</summary>
    private static string LayoutKey(LayoutChoice choice) => choice.Slot is { } slot ? $"saved:{slot}" : choice.Name;

    /// <summary>Resolves a <see cref="LayoutKey"/> against built-in then saved layouts; null for empty or missing.</summary>
    private LayoutChoice? FindLayout(string? key) => string.IsNullOrWhiteSpace(key) ? null
        : BuiltInLayouts().FirstOrDefault(l => l.Name == key) ?? SavedLayoutChoices().FirstOrDefault(l => LayoutKey(l) == key);

    /// <summary>The configured Default layout (AppSettings.DefaultStartupLayout); null if it resolves to none.</summary>
    private LayoutChoice? DefaultLayoutChoice() => FindLayout(AppSettings.Current.DefaultStartupLayout);

    /// <summary>
    /// Startup arrangement for the single pre-Frame 1 dock build (Law 1, Default layout): the saved arrangement if it still matches the
    /// Default's shape (DockManager.LoadsAlike keeps dragged splitters), else the Default itself after running its Then; if the Default
    /// does not resolve, the saved arrangement. Builds nothing.
    /// <para>With no Default set (legacy settings, AppSettings.Load step 9), the built-in layout matching the saved shape becomes the
    /// Default and is saved, so its card shows the star.</para>
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
    /// Views &gt; Reset Panes Layout: applies the Default layout (Standard Editing if none), closes every Pane Docker (DockManager.Reset),
    /// and runs its Then.
    /// </summary>
    private void ResetToDefaultLayout()
    {
        var choice = DefaultLayoutChoice();
        _dock.Reset(choice?.Layout);
        FinishRecordingUnlessShown();
        choice?.Then?.Invoke();
        _viewModel.StatusText = $"Layout reset to {choice?.Name ?? DefaultLayoutName}.";
    }

    /// <summary>Card pill clicked: saves that layout as the Default immediately and redraws cards with the star on it.</summary>
    private void SetDefaultLayout(LayoutChoice choice)
    {
        _viewModel.SavePaneLayout(s => s.DefaultStartupLayout = LayoutKey(choice));
        RemakeLayoutCards();
        _viewModel.StatusText = $"Default layout: {choice.Name}. HandPeg opens in it, and Reset Layout puts it back.";
    }

    /// <summary>
    /// Rebuilds cards while the selector is open (Default changed, layout deleted). Keyboard-initiated: focus stays on the same card;
    /// pointer-initiated: focus returns to its pre-selector owner (<see cref="LeaveSelectorToPointer"/>).
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

    // True during RemakeLayoutCards: removing the focused card is not treated as keyboard focus leaving the cards.
    private bool _remakingCards;

    // Focus owner when the selector opened (OpeningLayoutSelector), restored after a pointer click inside it.
    private IInputElement? _focusBeforeSelector;

    /// <summary>True when the current action came from the keyboard (Enter/Space on pill, CheckBox or Layout button), not pointer, touch or pen.</summary>
    private static bool FromKeyboard => InputManager.Current.MostRecentInputDevice is KeyboardDevice;

    /// <summary>
    /// After a pointer click on a pill, "×" or footer CheckBox: restores pre-selector focus (or none if that element is gone) so the selector
    /// no longer holds focus and closes when the pointer leaves (HoverFlyout stays open while focused).
    /// </summary>
    private void LeaveSelectorToPointer()
    {
        if (!(_focusBeforeSelector is UIElement { IsVisible: true } before && !before.IsDescendantOf(LayoutSelectorPanel) && before.Focus()))
            Keyboard.ClearFocus();
        _layoutHover.Release();
    }

    /// <summary>First-run window reopened from Settings: applies its chosen layout immediately.</summary>
    public void ApplyFirstRunLayout()
    {
        if (TakeFirstRunLayout() is { } json && DockLayoutState.Read(json) is { } layout)
        {
            _dock.Apply(_dock.Prepared(layout));
            FinishRecordingUnlessShown();
        }
    }

    /// <summary>Saved layouts by slot.</summary>
    private IEnumerable<LayoutChoice> SavedLayoutChoices() => AppSettings.Current.SavedLayouts.OrderBy(s => s.Slot)
        .Select(s => DockLayoutState.Read(s.Layout) is { } layout ? new LayoutChoice(s.Name, $"Your layout, kept in place {s.Slot}.", layout, s.Slot) : null)
        .OfType<LayoutChoice>();

    // ----- Opening the Layout Selector (click or hover) -----

    /// <summary>
    /// Layout button click: opens the selector if closed (never toggles closed, since hover usually opened it; Escape or pointer leave
    /// closes). Enter/Space opens with the first card focused, like Down.
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
    /// Layout button keys: Down or Menu opens the selector with the first card focused (focused cards preview like hovered ones);
    /// Enter/Space click; Escape closes it, leaving focus on the button.
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

    /// <summary>Opens the selector from the keyboard (if closed) and focuses its first card once shown.</summary>
    private void OpenLayoutSelectorOnFirstCard(FrameworkElement button)
    {
        if (!LayoutSelector.IsOpen)
            _layoutHover.Open(button);
        Dispatcher.BeginInvoke(() => LayoutCards.Children.OfType<Button>().FirstOrDefault()?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ----- Saved layouts -----

    /// <summary>
    /// Footer Save Current Layout: saves the current arrangement, named, into one of three slots. Reverts any preview and closes the
    /// selector first so the modal dialog never saves a preview or opens beneath a popup.
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

        // Overwriting the Default's slot keeps it the Default (slot is the key, LayoutKey); tell the user.
        _viewModel.StatusText = AppSettings.Current.DefaultStartupLayout == $"saved:{dialog.Slot}"
            ? $"Layout saved as \"{dialog.LayoutName}\": your Default layout (place {dialog.Slot}), so HandPeg opens in it and Reset Layout puts it back."
            : $"Layout saved as \"{dialog.LayoutName}\" (place {dialog.Slot}): the Layout Selector offers it.";
    }

    /// <summary>
    /// Saved card "×": reverts any preview, deletes that slot, and rebuilds cards with the selector open. Deleting the Default reverts
    /// the Default to Standard Editing.
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

    /// <summary>Footer "Each project tab remembers its own layout" (same setting as in Settings); saved immediately.</summary>
    private void PerTabLayoutsBox_Click(object sender, RoutedEventArgs e)
    {
        var on = PerTabLayoutsBox.IsChecked == true;
        _viewModel.SavePaneLayout(s => s.PerTabWindowLayouts = on);
        _viewModel.StatusText = on ? "Each project tab remembers its own layout." : "Every project tab shares the window's layout.";
        if (!FromKeyboard)
            LeaveSelectorToPointer();
    }

    /// <summary>Footer "Projects open in their saved window" (same setting as in Settings); saved immediately.</summary>
    private void RestoreWorkspaceBox_Click(object sender, RoutedEventArgs e)
    {
        var on = RestoreWorkspaceBox.IsChecked == true;
        _viewModel.SavePaneLayout(s => s.RestoreProjectWorkspaceOnOpen = on);
        _viewModel.StatusText = on ? "Projects open in the window they were saved in." : "Projects open in the window as it is.";
        if (!FromKeyboard)
            LeaveSelectorToPointer();
    }

    // ----- The Layout Selector -----

    // Arrangement at selector open (restore target), prepared card layouts, the one being previewed, and whether one was kept.
    private string? _layoutBeforePreview;
    private List<DockLayoutState> _cardLayouts = [];
    private int _previewing = -1;
    private bool _layoutKept;

    // Card cache key (layouts, visible panes, theme); unchanged inputs reuse the cards.
    private string? _cardsKey;

    private const double CardPictureWidth = 164, CardPictureHeight = 92;

    /// <summary>
    /// HoverFlyout opening: places the selector below the button (beside it for a side-strip header) and snapshots arrangement and
    /// theme as restore targets.
    /// </summary>
    private void OpeningLayoutSelector()
    {
        _focusBeforeSelector = Keyboard.FocusedElement;
        _layoutBeforePreview = _dock.Save();
        _themeBeforePreview = ThemeManager.Current;
        (_previewing, _layoutKept, _themeKept) = (-1, false, false);
        MakeLayoutCards();
        MakeThemeCards();

        // Footer: current setting value (also editable in Settings) and one row per open side window.
        PerTabLayoutsBox.IsChecked = AppSettings.Current.PerTabWindowLayouts;
        RestoreWorkspaceBox.IsChecked = AppSettings.Current.RestoreProjectWorkspaceOnOpen;
        UpdateSideWindowCheck();
        PlaceLayoutSelector(_layoutHover.OpenedFrom);
    }

    /// <summary>
    /// Heat-lamp pre-warm (pointer resting on the header): builds selector cards ahead using the current arrangement for "In use", as
    /// opening would; no-op while open.
    /// </summary>
    private void WarmLayoutCards()
    {
        if (LayoutSelector.IsOpen)
            return;
        _layoutBeforePreview = _dock.Save();
        MakeLayoutCards();
        MakeThemeCards();
    }

    /// <summary>
    /// Builds cards: each layout prepared as it would apply now, with a frozen DockManager.Miniature thumbnail. Reuses cached cards when
    /// inputs are unchanged, so reopening is free.
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

        // "In use": cards whose arrangement matches the snapshot topology (DockManager.SameShape: same splits and groups, any sizes).
        var inUse = DockLayoutState.Read(_layoutBeforePreview) ?? _dock.Current;
        var active = _cardLayouts.Select(l => DockManager.SameShape(l, inUse)).ToList();

        // Default layout key (AppSettings.DefaultStartupLayout), also part of the card cache key.
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

            // Top-right pills: "★ Default" (always shown on the Default), "☆ Set Default" and "×" (saved layouts) on hover/focus. Their
            // clicks are Handled so they never trigger the card.
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
                PreviewWhenStill(card, () => PreviewLayout(index));
            };
            card.MouseMove += (_, _) => PreviewWhenStill(card, () => PreviewLayout(index));
            card.MouseLeave += (_, _) =>
            {
                ShowPills();
                CancelPreviewOf(card);
            };
            card.IsKeyboardFocusWithinChanged += (_, _) => ShowPills();
            card.GotKeyboardFocus += (_, _) => PreviewLayout(index);
            card.Click += (_, _) => KeepLayout(index, choice);
            LayoutCards.Children.Add(card);
        }
    }

    /// <summary>"In use" badge (tick + text) in the selection edge colour.</summary>
    private static FrameworkElement InUseBadge()
    {
        var tick = new TextBlock { Text = Glyphs.CheckMark, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) };
        var words = new TextBlock { Text = "In use", FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        tick.SetResourceReference(TextBlock.ForegroundProperty, "SelectedBorderBrush");
        words.SetResourceReference(TextBlock.ForegroundProperty, "SelectedBorderBrush");
        var badge = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 1, 2, 0), Children = { tick, words } };
        DockPanel.SetDock(badge, Dock.Right);
        return badge;
    }

    // ----- Themes row -----
    // Below the layout cards: one card per theme (~1/3 card height) showing window, panel, accent and name in its own colours. Hover or
    // focus applies it live to all windows (layout cards redrawn); leaving, Escape or closing reverts to the snapshot; click saves
    // (AppSettings.Theme).

    private string? _themeBeforePreview;
    private bool _themeKept;

    private const double ThemeSwatchHeight = 30;

    // Theme card cache key (saved theme, shown theme); unchanged inputs reuse the cards (the heat lamp pre-builds them).
    private string? _themeCardsKey;

    private void MakeThemeCards()
    {
        var saved = ThemeManager.Normalize(AppSettings.Current.Theme);
        var key = saved + "|" + ThemeManager.Current;
        if (key == _themeCardsKey && ThemeCards.Children.Count == ThemeManager.Themes.Count)
            return;
        _themeCardsKey = key;
        ThemeCards.Children.Clear();
        foreach (var theme in ThemeManager.Themes)
        {
            // Window colour with the name in text colour, over a panel-colour band with the accent (tick on the active theme). Long
            // names shrink to fit, never truncate.
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
                band.Children.Add(new TextBlock { Text = Glyphs.CheckMark, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, Foreground = Frozen(accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
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
            card.MouseEnter += (_, _) => PreviewWhenStill(card, () => PreviewTheme(theme));
            card.MouseMove += (_, _) => PreviewWhenStill(card, () => PreviewTheme(theme));
            card.MouseLeave += (_, _) => CancelPreviewOf(card);
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

    /// <summary>Theme hover/focus: applies it live to all windows (unsaved) and redraws layout cards in it.</summary>
    private void PreviewTheme(string theme)
    {
        if (ThemeManager.Current == theme)
            return;

        ThemeManager.Apply(theme);
        MakeLayoutCards();
    }

    /// <summary>Theme click: saves it and closes the selector.</summary>
    private void KeepTheme(string theme)
    {
        _themeKept = true;
        _viewModel.SavePaneLayout(s => s.Theme = theme);
        ThemeManager.Apply();
        CrashReporter.Note("settings", "theme " + theme);
        _layoutHover.Close();
        _viewModel.StatusText = $"Theme: {theme}.";
    }

    /// <summary>Pointer left the theme row: revert to the snapshot theme.</summary>
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
    /// Selector placement: below the button, right-aligned (or beside a side-strip header), clamped 8 DIPs inside the panes area. Computed
    /// once at open so previews (which move and re-parent the button) never shift the card under the pointer.
    /// </summary>
    /// <remarks>
    /// Uses UiScale.PlaceAgainst in device pixels with the button rect captured at open, relative to the panes area (which layouts do not
    /// move). A Relative popup was mirrored on right-handed touch PCs (SystemParameters.MenuDropAlignment). Golden: header-flyouts-placement.
    /// </remarks>
    private void PlaceLayoutSelector(FrameworkElement button) => PlaceHeaderFlyout(LayoutSelector, button, MainDockHost, SelectorEdge);

    private const double SelectorEdge = 8;

    /// <summary>
    /// Card hover: previews its layout (unsaved); skipped when it already matches the current shape (the "In use" card).
    /// </summary>
    private void PreviewLayout(int index)
    {
        if (index == _previewing || index >= _cardLayouts.Count)
            return;

        _previewing = index;
        if (!DockManager.SameShape(_dock.Current, _cardLayouts[index]))
            _dock.Preview(_cardLayouts[index]);
    }

    // ----- Hover preview dwell (a sweep across cards must not apply every layout crossed) -----

    /// <summary>Hover dwell in ms before a layout or theme card previews.</summary>
    private const int CardPreviewRestMs = 90;

    private DispatcherTimer? _cardPreviewTimer;
    private (FrameworkElement Card, Action Preview)? _cardPreview;

    /// <summary>Previews a card after CardPreviewRestMs without movement (each move restarts the timer).</summary>
    private void PreviewWhenStill(FrameworkElement card, Action preview)
    {
        if (_cardPreviewTimer is null)
        {
            _cardPreviewTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(CardPreviewRestMs) };
            _cardPreviewTimer.Tick += (_, _) =>
            {
                _cardPreviewTimer.Stop();
                if (_cardPreview is { } wanted && LayoutSelector.IsOpen && wanted.Card.IsMouseOver)
                    wanted.Preview();
                _cardPreview = null;
            };
        }

        _cardPreview = (card, preview);
        _cardPreviewTimer.Stop();
        _cardPreviewTimer.Start();
    }

    /// <summary>Pointer left before the dwell elapsed: cancel the pending preview.</summary>
    private void CancelPreviewOf(FrameworkElement card)
    {
        if (_cardPreview is { } wanted && ReferenceEquals(wanted.Card, card))
        {
            _cardPreviewTimer?.Stop();
            _cardPreview = null;
        }
    }

    /// <summary>Card click: keeps the previewed layout, saves it, and runs its Then (previews never do).</summary>
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

    /// <summary>Pointer left the cards: revert to the snapshot arrangement.</summary>
    private void LayoutCards_MouseLeave(object sender, MouseEventArgs e) => RestoreLayoutBeforePreview();

    /// <summary>
    /// Keyboard focus left the cards with the selector open (Tab to footer, click elsewhere): revert the preview so later actions (+ New
    /// Side Window, closing one) never save it. Skipped during card rebuilds or while the pointer is over a card.
    /// </summary>
    private void LayoutCards_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && LayoutSelector.IsOpen && !_remakingCards && !LayoutCards.IsMouseOver)
            RestoreLayoutBeforePreview();
    }

    /// <summary>Window deactivated (Alt+Tab): revert any preview and close the (topmost) selector.</summary>
    private void CloseLayoutSelectorOnDeactivated()
    {
        if (!LayoutSelector.IsOpen)
            return;

        RestoreLayoutBeforePreview();
        _layoutHover.Close();
    }

    private void RestoreLayoutBeforePreview()
    {
        _cardPreviewTimer?.Stop();
        _cardPreview = null;
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

    /// <summary>Closed without a choice (Escape, pointer left selector and button): revert arrangement and theme.</summary>
    private void LayoutSelector_Closed(object? sender, EventArgs e)
    {
        RestoreLayoutBeforePreview();
        RestoreThemeBeforePreview();
        (_layoutBeforePreview, _previewing, _themeBeforePreview, _focusBeforeSelector) = (null, -1, null, null);
    }
}
