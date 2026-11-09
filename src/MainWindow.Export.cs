using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- The Export tab, and Tabs and Panes (Pass 4.1B) -----
// Every settings tab and every pane but the player and the header can be shown or hidden (AppSettings.HiddenTabs and
// the panes' own settings), from the Views menu's Tabs and Panes list or the same list on any tab's right-click menu.
//
// The Export tab (5.4B) is three inspector cards, every export setting once: Preset & Format, Quality, Bitrate & Resolution, Audio &
// Output (ReflowExportColumns stands them side by side or one over the other). The Video tab of before is those cards; the Easy |
// Advanced switch and its cards are Export Presets. Under the cards, what the Audio tab holds while it is hidden, and in the third
// card the output: DestinationBar itself, moved into the tab while it is shown and back to the bottom of the window while it is not,
// so Save As and Start Encode are always somewhere and never twice. The Audio tab's content is made once (from its template, when
// first wanted) and moved between its own tab and the Export tab: the names the window finds in it (RealizeTabContent) stay the same.
public partial class MainWindow
{
    /// <summary>The tabs whose contents the Export tab holds while they are hidden, in its order, with the word its button shows (5.4B: the Audio tab alone).</summary>
    private static readonly (string Id, string Label)[] SectionTabs = [("Tab:Audio", "Audio")];

    private const string ExportId = "Tab:Export";

    // Each section tab's own pane content (a box its content is put into while the tab is shown), and each section's content once made.
    private readonly Dictionary<string, Border> _sectionSlots = [];
    private readonly Dictionary<string, FrameworkElement> _sectionContents = [];

    // The section the Export tab shows, and where DestinationBar lives at the bottom of the window.
    private string _exportSection = SectionTabs[0].Id;
    private (Panel Parent, int Index)? _destinationHome;

    // The Export tab's named parts, found as its template is made (RealizeTabContent, 5.3-Stage2: the tab is not built with the
    // window). The three reached before that (UpdateTabVisibility before the first frame, the Settings mirror) are null until then;
    // the rest are reached only from inside the tab, once it is made.
    private DockPanel? ExportPanel;
    private Border? ExportBarSlot;
    private StackPanel? ExportSections;
    private Border ExportSectionHost = null!;
    private Grid ExportColumns = null!;

    private static bool IsSection(string id) => SectionTabs.Any(s => s.Id == id);

    private TabItem TabOf(string id) => _tabPanes.First(t => t.Id == id).Tab;

    // ----- Which tabs are shown -----

    /// <summary>The tabs hidden: as saved, or the defaults.</summary>
    private static IReadOnlyList<string> HiddenTabs => AppSettings.Current.HiddenTabs ?? AppSettings.DefaultHiddenTabs;

    /// <summary>Whether a tab is shown: it is not hidden, and (Command Preview) its own setting is on.</summary>
    private bool TabWanted(string id) => id == "Tab:Command Preview" ? _viewModel.ShowCommandPreviewTab : !HiddenTabs.Contains(id);

    /// <summary>Shows or hides a tab (Command Preview: its own setting).</summary>
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

    // ----- The sections, moved between their own tabs and the Export tab -----

    /// <summary>A section tab's pane content: its box, with the section put in it when the tab is shown.</summary>
    private FrameworkElement SectionSlot(string id)
    {
        if (!_sectionSlots.TryGetValue(id, out var slot))
            _sectionSlots[id] = slot = new Border();
        if (TabOf(id).Visibility == Visibility.Visible)
            Put(id, slot);
        return slot;
    }

    /// <summary>A section's content, made from its tab's template the first time it is wanted, its data the window's own.</summary>
    private FrameworkElement SectionContent(string id)
    {
        if (!_sectionContents.TryGetValue(id, out var content))
        {
            content = RealizeTabContent(TabOf(id));

            // Its data is always the window's: moved, it is out of every window for a moment (as DockManager.Realize does).
            content.SetBinding(DataContextProperty, new Binding(nameof(DataContext)) { Source = this });
            _sectionContents[id] = content;
        }

        return content;
    }

    /// <summary>Puts a section into a box, taking it out of wherever it was.</summary>
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
    /// Puts each section where it is shown: in its own tab while that is shown, else (for the one chosen) in the Export
    /// tab; and the Export tab's buttons for the sections it holds. Nothing is made that is not on screen.
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
    /// The Export tab's buttons for the sections it holds (none for a single one). They are the tab's: until it is made (5.3-Stage2)
    /// there is nowhere to put them, and ExportContent makes them with it.
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

        // A single section needs no button to choose it.
        sections.Visibility = inExport.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Under the Export tab's cards: the chosen section's content while its own tab is hidden, else nothing (nothing made that is
    /// not on screen; the section stays in its own tab).
    /// </summary>
    private void FillExportSection()
    {
        if (ExportTab.Visibility != Visibility.Visible)
            return;

        // Every section in its own tab (PlaceSections has put it there): nothing under the cards.
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

    // ----- The three columns (5.4B) -----

    /// <summary>The least width of a card side by side with the others, and the gap between two.</summary>
    internal const double ExportCardMinWidth = 300, ExportCardGap = 12;

    /// <summary>How many columns the Export tab's cards stand in at a width: three while each has its least width, else one (one over the other).</summary>
    internal static int ExportColumnCount(double width) => width >= 3 * ExportCardMinWidth + 2 * ExportCardGap ? 3 : 1;

    /// <summary>
    /// Stands the three cards side by side (three equal columns, a gap between) or one over the other (the full width, a gap under each),
    /// as the tab is wide (<see cref="ExportColumnCount"/>). Called as the columns' grid is sized: only the places change, not its width.
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
    /// The Export tab's pane content, made when first shown (or at idle; RealizeTabContent finds its parts): its section buttons,
    /// the destination bar parked until now, and its middle, filled then. The dock counts it as made only once this returns,
    /// which is why these are called here.
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
    /// Save As, Browse, Add to Queue and Start Encode: in the Export tab while it is shown, else at the bottom of the
    /// window while Export is hidden. While Export is shown but not made yet (5.3-Stage2: made when first shown, or at idle)
    /// the bar is out of the window, parked, as it was inside the unmade tab before; ExportContent puts it in.
    /// </summary>
    private void PlaceDestinationBar()
    {
        _destinationHome ??= DestinationBar.Parent is Panel home ? (home, home.Children.IndexOf(DestinationBar)) : null;
        if (_destinationHome is not { } place)
            return;

        // Out of the window for a moment (parked, or moved): its data stays the window's, as a section's does (SectionContent).
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

            // Right under the panes (5.3-Stage2): a hairline apart, no band.
            DestinationBar.Margin = new Thickness(0, 1, 0, 0);
            place.Parent.Children.Insert(Math.Min(place.Index, place.Parent.Children.Count), DestinationBar);
        }

        UpdateBottomBar();
    }

    // ----- Tabs and Panes: one list for the Views menu and a tab's right-click menu -----

    /// <summary>A tab or a pane the menu shows or hides: its name, whether it is shown, how to show or hide it, and what it is.</summary>
    private sealed record ViewToggle(string Name, Func<bool> IsOn, Action<bool> Set, string About, Func<bool>? Enabled = null);

    private IEnumerable<ViewToggle> TabToggles() => _tabPanes.Select(t => new ViewToggle(
        t.Tab.Header as string ?? t.Id, () => TabWanted(t.Id), on => SetTabShown(t.Id, on),
        t.Id switch
        {
            ExportId => "The Export tab: the Export Presets, the format, the video and audio settings and the chapters in three cards, the Live Encoded Preview, the Projected Output, with Save As, Add to Queue and Start Encode.",
            "Tab:Audio" => "The Audio tab of its own: ducking, the master mix and the voiceover (the sounds themselves are on the timeline; how they are encoded is the Export tab's). Hidden, what it holds is in the Export tab.",
            "Tab:Command Preview" => "The FFmpeg command, to read or edit (the same as the setting in Settings).",
            _ => $"The {t.Tab.Header} tab.",
        }));

    /// <summary>The Sequence Overview Scrubber shown or hidden (Tabs and Panes, and the Timeline Settings menu, 5.3): saved, the panes laid out again.</summary>
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
            new("Status Bar", () => AppSettings.Current.ShowStatusBar, SetStatusBarShown,
                "The bar along the bottom of the window. Hidden, the status and the progress of background work are in the title bar."),
            .. StudioToggles(),
        ];
    }

    /// <summary>The Views menu is opening: its Tabs and Panes lists made as things are now.</summary>
    private void FillViewsLists()
    {
        static void Fill(Panel list, IEnumerable<ViewToggle> toggles)
        {
            list.Children.Clear();
            foreach (var toggle in toggles)
            {
                var box = new CheckBox { Content = toggle.Name, IsChecked = toggle.IsOn(), Margin = new Thickness(0, 2, 16, 2), ToolTip = toggle.About, IsEnabled = toggle.Enabled?.Invoke() ?? true };
                var kind = list.Name == nameof(ViewsTabList) ? " Tab" : toggle.Name.EndsWith("Pane", StringComparison.Ordinal) || toggle.Name.EndsWith("Bar", StringComparison.Ordinal)
                    || toggle.Name == ScrubberTitle ? "" : " Pane";
                AutomationProperties.SetName(box, toggle.Name + kind);

                // Checked and Unchecked, not Click: the keyboard and UI Automation change a box without a click.
                box.Checked += (_, _) => toggle.Set(true);
                box.Unchecked += (_, _) => toggle.Set(false);
                list.Children.Add(box);
            }
        }

        Fill(ViewsTabList, TabToggles());
        Fill(ViewsPaneList, PaneToggles());
    }

    /// <summary>A tab's right-click menu (DockManager.ExtendTabMenu): Tabs and Panes, as in the Views menu.</summary>
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
