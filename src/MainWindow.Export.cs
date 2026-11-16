using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Export tab and Tabs & Panes visibility -----
// Every settings tab and pane except the player and header can be shown or hidden (AppSettings.HiddenTabs plus per-pane settings)
// from the Views menu's Tabs and Panes list or any tab's context menu.
//
// The Export tab is three inspector cards holding each export setting once: Preset & Format; Quality, Bitrate & Resolution; Audio &
// Output (ReflowExportColumns lays them side by side or stacked). Below the cards it hosts the Audio tab's content while that tab is
// hidden. The third card hosts DestinationBar itself while Export is shown (otherwise it returns to the window bottom), so Save As and
// Start Encode always exist exactly once. The Audio content is realized once from its template and reparented between its own tab and
// Export, so names resolved by RealizeTabContent stay valid.
public partial class MainWindow
{
    /// <summary>Section tabs hosted by Export while hidden, with their button labels (currently Audio only).</summary>
    private static readonly (string Id, string Label)[] SectionTabs = [("Tab:Audio", "Audio")];

    private const string ExportId = "Tab:Export";

    // Per-section host Border in its own tab, and each section's realized content.
    private readonly Dictionary<string, Border> _sectionSlots = [];
    private readonly Dictionary<string, FrameworkElement> _sectionContents = [];

    // Section shown in Export, and DestinationBar's home slot at the window bottom.
    private string _exportSection = SectionTabs[0].Id;
    private (Panel Parent, int Index)? _destinationHome;

    // Export tab parts resolved at realization (RealizeTabContent; not built with the window). The three nullable ones may be queried
    // earlier (UpdateTabVisibility before Frame 1, Settings mirror); the rest are only touched from inside the realized tab.
    private DockPanel? ExportPanel;
    private Border? ExportBarSlot;
    private StackPanel? ExportSections;
    private Border ExportSectionHost = null!;
    private Grid ExportColumns = null!;

    private static bool IsSection(string id) => SectionTabs.Any(s => s.Id == id);

    private TabItem TabOf(string id) => _tabPanes.First(t => t.Id == id).Tab;

    // ----- Tab visibility -----

    /// <summary>Hidden tab ids: saved, or defaults.</summary>
    private static IReadOnlyList<string> HiddenTabs => AppSettings.Current.HiddenTabs ?? AppSettings.DefaultHiddenTabs;

    /// <summary>True when a tab should show (not hidden; Command Preview uses its own setting).</summary>
    private bool TabWanted(string id) => id == "Tab:Command Preview" ? _viewModel.ShowCommandPreviewTab : !HiddenTabs.Contains(id);

    /// <summary>Shows or hides a tab (Command Preview via its own setting).</summary>
    private void SetTabShown(string id, bool shown)
    {
        if (id == "Tab:Command Preview")
        {
            _viewModel.SetViewSetting(s => s.ShowCommandPreviewTab = shown, nameof(MainViewModel.ShowCommandPreviewTab));
            return;
        }

        var hidden = HiddenTabs.Where(t => t != id).ToList();
        if (!shown)
            hidden.Add(id);
        _viewModel.SavePaneLayout(s => s.HiddenTabs = hidden);
        UpdateTabVisibility();
    }

    // ----- Sections reparented between their own tabs and Export -----

    /// <summary>Section tab pane content: its host Border, filled with the section when the tab is visible.</summary>
    private FrameworkElement SectionSlot(string id)
    {
        if (!_sectionSlots.TryGetValue(id, out var slot))
            _sectionSlots[id] = slot = new Border();
        if (TabOf(id).Visibility == Visibility.Visible)
            Put(id, slot);
        return slot;
    }

    /// <summary>Section content, realized from its tab template on first use with DataContext bound to the window.</summary>
    private FrameworkElement SectionContent(string id)
    {
        if (!_sectionContents.TryGetValue(id, out var content))
        {
            content = RealizeTabContent(TabOf(id));

            // Bind DataContext explicitly: while reparenting it is briefly detached from any window (as in DockManager.Realize).
            content.SetBinding(DataContextProperty, new Binding(nameof(DataContext)) { Source = this });
            _sectionContents[id] = content;
        }

        return content;
    }

    /// <summary>Reparents a section into a Border, detaching it from its previous host.</summary>
    private void Put(string id, Border into)
    {
        var content = SectionContent(id);
        if (ReferenceEquals(into.Child, content))
            return;
        if (content.Parent is Border from)
            from.Child = null;
        into.Child = content;
    }

    /// <summary>
    /// Places each section in its own tab when visible, else (the selected one) in Export, and updates Export's section buttons. Realizes
    /// nothing off screen.
    /// </summary>
    private void PlaceSections()
    {
        var inExport = SectionTabs.Where(s => TabOf(s.Id).Visibility != Visibility.Visible).ToList();
        foreach (var (id, _) in SectionTabs)
        {
            var own = TabOf(id).Visibility == Visibility.Visible;
            if (own && _sectionSlots.TryGetValue(id, out var slot))
                Put(id, slot);
            else if (!own && _sectionSlots.TryGetValue(id, out var left) && left.Child is not null)
                left.Child = null;
        }

        if (inExport.Count > 0 && inExport.All(s => s.Id != _exportSection))
            _exportSection = inExport[0].Id;

        FillExportSectionButtons(inExport);
        if (_dock.IsRealized(ExportId))
            FillExportSection();
    }

    /// <summary>
    /// Builds Export's section buttons (hidden for a single section). No-op until the tab is realized; ExportContent builds them then.
    /// </summary>
    private void FillExportSectionButtons(List<(string Id, string Label)> inExport)
    {
        if (ExportSections is not { } sections)
            return;

        sections.Children.Clear();
        foreach (var (id, label) in inExport)
        {
            var button = new RadioButton { Content = label, GroupName = "ExportSection", IsChecked = id == _exportSection };
            button.SetResourceReference(StyleProperty, "ExportSectionButton");
            AutomationProperties.SetName(button, "Export " + label);
            button.Checked += (_, _) =>
            {
                _exportSection = id;
                FillExportSection();
            };
            sections.Children.Add(button);
        }

        // No chooser needed for a single section.
        sections.Visibility = inExport.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the selected section under Export's cards while its own tab is hidden; otherwise collapses the host (nothing realized off
    /// screen).
    /// </summary>
    private void FillExportSection()
    {
        if (ExportTab.Visibility != Visibility.Visible)
            return;

        // All sections in their own tabs (placed by PlaceSections): nothing under the cards.
        if (SectionTabs.All(s => TabOf(s.Id).Visibility == Visibility.Visible))
        {
            ExportSectionHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            ExportSectionHost.Visibility = Visibility.Visible;
            Put(_exportSection, ExportSectionHost);
        }
    }

    // ----- Card columns -----

    /// <summary>Minimum card width in side-by-side layout, and the inter-card gap (DIPs).</summary>
    internal const double ExportCardMinWidth = 300, ExportCardGap = 12;

    /// <summary>Column count for a width: 3 when each card fits its minimum, else 1 (stacked).</summary>
    internal static int ExportColumnCount(double width) => width >= 3 * ExportCardMinWidth + 2 * ExportCardGap ? 3 : 1;

    /// <summary>
    /// Lays the cards out in three equal columns or stacked full width per <see cref="ExportColumnCount"/>. Called from the grid's
    /// SizeChanged; it only reassigns cells and margins, never the grid width.
    /// </summary>
    private void ReflowExportColumns(double width)
    {
        var columns = ExportColumnCount(width);
        var cards = ExportColumns.Children.OfType<FrameworkElement>().ToList();
        for (var i = 0; i < ExportColumns.ColumnDefinitions.Count; i++)
            ExportColumns.ColumnDefinitions[i].Width = i < columns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        for (var i = 0; i < cards.Count; i++)
        {
            var side = columns > 1;
            Grid.SetColumn(cards[i], side ? i : 0);
            Grid.SetRow(cards[i], side ? 0 : i);
            cards[i].Margin = side
                ? new Thickness(i == 0 ? 0 : ExportCardGap / 2, 0, i == cards.Count - 1 ? 0 : ExportCardGap / 2, 0)
                : new Thickness(0, 0, 0, i == cards.Count - 1 ? 0 : ExportCardGap);
        }
    }

    /// <summary>
    /// Realizes the Export tab content on first show or at idle (RealizeTabContent resolves parts), then section buttons, the parked
    /// DestinationBar and section host. Done here because the dock treats the pane as realized only after this returns.
    /// </summary>
    private FrameworkElement ExportContent()
    {
        var content = RealizeTabContent(ExportTab);
        ExportColumns.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && ExportColumnCount(e.NewSize.Width) != ExportColumnCount(e.PreviousSize.Width))
                ReflowExportColumns(e.NewSize.Width);
        };
        ReflowExportColumns(0);
        FillExportSectionButtons(SectionTabs.Where(s => TabOf(s.Id).Visibility != Visibility.Visible).ToList());
        PlaceDestinationBar();
        FillExportSection();
        return content;
    }

    /// <summary>
    /// Places DestinationBar (Save As, Browse, Add to Queue, Start Encode) inside Export while it is shown, else at the window bottom.
    /// While Export is visible but not yet realized the bar stays parked off-tree; ExportContent inserts it.
    /// </summary>
    private void PlaceDestinationBar()
    {
        _destinationHome ??= DestinationBar.Parent is Panel home ? (home, home.Children.IndexOf(DestinationBar)) : null;
        if (_destinationHome is not { } place)
            return;

        // Bind DataContext explicitly since the bar can be detached while parked or moved (as in SectionContent).
        if (DestinationBar.ReadLocalValue(DataContextProperty) == DependencyProperty.UnsetValue)
            DestinationBar.SetBinding(DataContextProperty, new Binding(nameof(DataContext)) { Source = this });

        if (ExportTab.Visibility == Visibility.Visible)
        {
            if (ExportBarSlot is not null && ReferenceEquals(DestinationBar.Parent, ExportBarSlot))
                return;
            if (ReferenceEquals(DestinationBar.Parent, place.Parent))
                place.Parent.Children.Remove(DestinationBar);
            DestinationBar.Margin = new Thickness(0);
            if (ExportBarSlot is { } slot)
                slot.Child = DestinationBar;
        }
        else if (!ReferenceEquals(DestinationBar.Parent, place.Parent))
        {
            if (ExportBarSlot is { } slot && ReferenceEquals(slot.Child, DestinationBar))
                slot.Child = null;

            // Directly under the panes with a 1 DIP gap.
            DestinationBar.Margin = new Thickness(0, 1, 0, 0);
            place.Parent.Children.Insert(Math.Min(place.Index, place.Parent.Children.Count), DestinationBar);
        }

        UpdateBottomBar();
    }

    // ----- Tabs and Panes list (Views menu and tab context menu) -----

    /// <summary>Visibility toggle entry: name, state getter, setter, description, and optional enabled predicate.</summary>
    private sealed record ViewToggle(string Name, Func<bool> IsOn, Action<bool> Set, string About, Func<bool>? Enabled = null);

    private IEnumerable<ViewToggle> TabToggles() => _tabPanes.Select(t => new ViewToggle(
        t.Tab.Header as string ?? t.Id, () => TabWanted(t.Id), on => SetTabShown(t.Id, on),
        t.Id switch
        {
            ExportId => "The Export tab: the Export Presets, the format, the video and audio settings and the chapters in three cards, the Live Encoded Preview, the Projected Output, with Save As, Add to Queue and Start Encode.",
            "Tab:Audio" => "The Audio tab of its own: ducking, the master mix and the voiceover (the sounds themselves are on the timeline; how they are encoded is the Export tab's). Hidden, what it holds is in the Export tab.",
            "Tab:Command Preview" => "The FFmpeg command, to read or edit (the same as the setting in Settings).",
            ColorTabId => "The Color tab (the Color Studio): wheels, curves, LUT, Before/After.",
            _ => $"The {t.Tab.Header} tab.",
        }));

    /// <summary>Shows or hides the Overview Scrubber (Tabs and Panes, Timeline Settings menu); saves and rebuilds the dock.</summary>
    private void SetScrubberShown(bool on)
    {
        _viewModel.SavePaneLayout(s => s.ShowMasterTimeline = on);
        _dock.ScheduleRebuild();
    }

    private IEnumerable<ViewToggle> PaneToggles()
    {
        return
        [
            new(ScrubberTitle, () => AppSettings.Current.ShowMasterTimeline, SetScrubberShown,
                "The Sequence Overview Scrubber under the player: the whole sequence at a glance, its cut segments, markers and thumbnails, scrubbed by a click or a drag; its lower edge dragged makes it taller or shorter (the timeline itself is a pane of its own)."),
            new("Playback Controls", () => _viewModel.ShowAdvancedPlayback,
                on => _viewModel.SetViewSetting(s => s.ShowAdvancedPlayback = on, nameof(MainViewModel.ShowAdvancedPlayback), nameof(MainViewModel.ShowSimplePlayback)),
                "The playback controls under the player: play, the edit points, the speed and volume."),
            new("Preset Bar", () => _viewModel.ShowPresetBarAtTop,
                on => _viewModel.SetViewSetting(s => s.PresetBarLocation = on ? AppSettings.PresetBarAtTop : AppSettings.PresetBarInExport,
                    nameof(MainViewModel.ShowPresetBarAtTop), nameof(MainViewModel.ShowPresetBarInExport)),
                "The Export Presets selector as a pane of its own. Hidden, it is in the Export tab's first card."),
            new("Canvas", () => _viewModel.IsArrangeActive, on => _viewModel.IsArrangeActive = on,
                "The Canvas beside the video: the output frame with its clips and the caption box. Drag to move, drag a corner handle to resize, double-click to center.",
                () => _viewModel.CanEditLayout),
            new("Media Browser", () => _viewModel.ShowMediaBrowser, on => _viewModel.ShowMediaBrowser = on,
                "The media browser: a folder's videos, sounds and pictures, to drag onto the timeline."),
            new("Keyframes", () => _viewModel.ShowKeyframesPane, on => _viewModel.ShowKeyframesPane = on,
                "The selected clip's keyframes on a timeline of their own, with the keyframe controls and its place, size and rotation at the playhead."),
            new("Effects", () => _viewModel.ShowEffectsPane, on => _viewModel.ShowEffectsPane = on,
                "The selected clip's effect stack (or the Sequence Output's), in the order the effects are applied."),
            new("Master Meter", () => _viewModel.ShowMasterMeter, on => _viewModel.ShowMasterMeter = on,
                "The Sequence's master meter and fader, upright: each side's peak and RMS, the loudness in LUFS with the -14 and -16 targets, the true peak, and the master gain (the Audio tab has the same meter across)."),
            .. StudioToggles(),
        ];
    }

    /// <summary>
    /// Fills the Views menu's merged Tabs and Panes list from current state, tabs first. UI Automation names keep their suffixes
    /// ("Export Tab", "Keyframes Pane").
    /// </summary>
    private void FillViewsLists()
    {
        ViewsPaneList.Children.Clear();
        void Fill(IEnumerable<ViewToggle> toggles, bool tabs)
        {
            foreach (var toggle in toggles)
            {
                var box = new CheckBox { Content = toggle.Name, IsChecked = toggle.IsOn(), Margin = new Thickness(0, 2, 24, 2), ToolTip = toggle.About, IsEnabled = toggle.Enabled?.Invoke() ?? true };
                var kind = tabs ? " Tab" : toggle.Name.EndsWith("Pane", StringComparison.Ordinal) || toggle.Name.EndsWith("Bar", StringComparison.Ordinal)
                    || toggle.Name == ScrubberTitle ? "" : " Pane";
                AutomationProperties.SetName(box, toggle.Name + kind);

                // Checked/Unchecked rather than Click: keyboard and UI Automation toggle without clicking.
                box.Checked += (_, _) => toggle.Set(true);
                box.Unchecked += (_, _) => toggle.Set(false);
                ViewsPaneList.Children.Add(box);
            }
        }

        Fill(TabToggles(), tabs: true);
        Fill(PaneToggles(), tabs: false);
    }

    /// <summary>Adds the Tabs and Panes submenu to a tab context menu (DockManager.ExtendTabMenu), mirroring the Views menu.</summary>
    private void AddTabsAndPanesMenu(ContextMenu menu)
    {
        var root = new MenuItem { Header = "Tabs and Panes" };
        void Fill(IEnumerable<ViewToggle> toggles)
        {
            foreach (var toggle in toggles)
            {
                var item = new MenuItem { Header = toggle.Name, IsCheckable = true, IsChecked = toggle.IsOn(), ToolTip = toggle.About, IsEnabled = toggle.Enabled?.Invoke() ?? true };
                item.Checked += (_, _) => toggle.Set(true);
                item.Unchecked += (_, _) => toggle.Set(false);
                root.Items.Add(item);
            }
        }

        Fill(TabToggles());
        root.Items.Add(new Separator());
        Fill(PaneToggles());
        menu.Items.Insert(0, root);
        menu.Items.Insert(1, new Separator());
    }
}
