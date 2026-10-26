using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- The Export tab, and Tabs and Panes (Pass 4.1B) -----
// Every settings tab and every pane but the player and the header can be shown or hidden, for each mode on its own
// (AppSettings.HiddenTabs and the panes' own settings, kept in ModeProfile), from the Views menu's Tabs and Panes list
// or the same list on any tab's right-click menu.
//
// The Export tab holds what Video, Audio and Chapters hold while their own tabs are hidden (Editor Mode starts so),
// and under them the output: DestinationBar itself, moved into the tab while it is shown and back to the bottom of the
// window while it is not, so Save As and Start Encode are always somewhere and never twice. Each section's content is
// made once (from its tab's template, when first wanted) and moved between its own tab and the Export tab: the names
// the window finds in it (RealizeTabContent) stay the same.
public partial class MainWindow
{
    /// <summary>The tabs whose contents the Export tab holds while they are hidden, in its order, with the word its button shows.</summary>
    private static readonly (string Id, string Label)[] SectionTabs = [("Tab:Video", "Video"), ("Tab:Audio", "Audio"), ("Tab:Chapters", "Chapters")];

    private const string ExportId = "Tab:Export";

    // Each section tab's own pane content (a box its content is put into while the tab is shown), and each section's content once made.
    private readonly Dictionary<string, Border> _sectionSlots = [];
    private readonly Dictionary<string, FrameworkElement> _sectionContents = [];

    // The section the Export tab shows, and where DestinationBar lives at the bottom of the window.
    private string _exportSection = SectionTabs[0].Id;
    private (Panel Parent, int Index)? _destinationHome;

    private static bool IsSection(string id) => SectionTabs.Any(s => s.Id == id);

    private TabItem TabOf(string id) => _tabPanes.First(t => t.Id == id).Tab;

    // ----- Which tabs a mode shows -----

    /// <summary>The tabs the mode in use hides: as saved, or the mode's defaults.</summary>
    private static IReadOnlyList<string> HiddenTabs => AppSettings.Current.HiddenTabs ?? AppSettings.DefaultHiddenTabs(AppSettings.Current.UiMode);

    /// <summary>Whether a mode has a tab at all: Layers is Editor Mode's (Encoder Mode has no layers); every other tab is both modes'.</summary>
    private bool TabAvailable(string id) => id != "Tab:Layers" || _viewModel.IsEditorMode;

    /// <summary>Whether a tab is shown: the mode has it, it is not hidden, and (Command Preview) its own setting is on.</summary>
    private bool TabWanted(string id) => TabAvailable(id)
        && (id == "Tab:Command Preview" ? _viewModel.ShowCommandPreviewTab : !HiddenTabs.Contains(id));

    /// <summary>Shows or hides a tab for the mode in use (Command Preview: its own setting).</summary>
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

        ExportSections.Children.Clear();
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
            ExportSections.Children.Add(button);
        }

        // A single section needs no button to choose it.
        ExportSections.Visibility = inExport.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_dock.IsRealized(ExportId))
            FillExportSection();
    }

    /// <summary>The Export tab's middle: the chosen section's content, or the hint that each section has a tab of its own.</summary>
    private void FillExportSection()
    {
        if (ExportTab.Visibility != Visibility.Visible)
            return;

        if (SectionTabs.All(s => TabOf(s.Id).Visibility == Visibility.Visible))
            ExportSectionHost.Child = ExportNoSections;
        else
            Put(_exportSection, ExportSectionHost);
    }

    /// <summary>The Export tab's pane content, made when first shown (or at idle): its middle filled then.</summary>
    private FrameworkElement ExportContent()
    {
        var content = RealizeTabContent(ExportTab);
        FillExportSection();
        return content;
    }

    /// <summary>
    /// Save As, Browse, Add to Queue and Start Encode: in the Export tab while it is shown, else at the bottom of the
    /// window (Encoder Mode's place for them, and wherever Export is hidden).
    /// </summary>
    private void PlaceDestinationBar()
    {
        _destinationHome ??= DestinationBar.Parent is Panel home ? (home, home.Children.IndexOf(DestinationBar)) : null;
        if (_destinationHome is not { } place)
            return;

        if (ExportTab.Visibility == Visibility.Visible)
        {
            if (ReferenceEquals(DestinationBar.Parent, ExportBarSlot))
                return;
            place.Parent.Children.Remove(DestinationBar);
            DestinationBar.Margin = new Thickness(0);
            ExportBarSlot.Child = DestinationBar;
        }
        else if (!ReferenceEquals(DestinationBar.Parent, place.Parent))
        {
            ExportBarSlot.Child = null;
            DestinationBar.Margin = new Thickness(0, 10, 0, 0);
            place.Parent.Children.Insert(Math.Min(place.Index, place.Parent.Children.Count), DestinationBar);
        }

        UpdateBottomBar();
    }

    // ----- Tabs and Panes: one list for the Views menu and a tab's right-click menu -----

    /// <summary>A tab or a pane the menu shows or hides: its name, whether this mode has it, whether it is shown, how to show or hide it, and what it is.</summary>
    private sealed record ViewToggle(string Name, Func<bool> Available, Func<bool> IsOn, Action<bool> Set, string About, Func<bool>? Enabled = null);

    private IEnumerable<ViewToggle> TabToggles() => _tabPanes.Select(t => new ViewToggle(
        t.Tab.Header as string ?? t.Id, () => TabAvailable(t.Id), () => TabWanted(t.Id), on => SetTabShown(t.Id, on),
        t.Id switch
        {
            ExportId => "The Export tab: the video and audio settings and the chapters (those whose own tab is hidden), with Save As, Add to Queue and Start Encode.",
            "Tab:Video" or "Tab:Audio" or "Tab:Chapters" => $"The {t.Tab.Header} tab of its own. Hidden, what it holds is in the Export tab.",
            "Tab:Command Preview" => "The FFmpeg command, to read or edit (the same as the setting in Settings).",
            _ => $"The {t.Tab.Header} tab.",
        }));

    private IEnumerable<ViewToggle> PaneToggles()
    {
        var editor = () => _viewModel.IsEditorMode;
        var always = () => true;
        void Refresh() => _dock.ScheduleRebuild();
        return
        [
            new("Master Timeline", always, () => AppSettings.Current.ShowMasterTimeline,
                on => { _viewModel.SavePaneLayout(s => s.ShowMasterTimeline = on); Refresh(); },
                "The master timeline under the player: the whole sequence, its cut segments and markers."),
            new("Playback Controls", always, () => _viewModel.ShowAdvancedPlayback,
                on => _viewModel.SetViewSetting(s => s.ShowAdvancedPlayback = on, nameof(MainViewModel.ShowAdvancedPlayback), nameof(MainViewModel.ShowSimplePlayback)),
                "The playback controls under the player: play, the edit points, the speed and volume."),
            new("Preset Bar", always, () => _viewModel.ShowPresetBarAtTop,
                on => _viewModel.SetViewSetting(s => s.PresetBarLocation = on ? AppSettings.PresetBarAtTop : AppSettings.PresetBarInSummary,
                    nameof(MainViewModel.ShowPresetBarAtTop), nameof(MainViewModel.ShowPresetBarInSummary)),
                "The preset bar as a pane of its own. Hidden, it is on the Summary tab."),
            new("Layout Pane", editor, () => _viewModel.IsArrangeActive, on => _viewModel.IsArrangeActive = on,
                "The layout pane beside the video: the output frame with the main video, the layers and the caption box. Drag to move, drag a corner handle to resize, double-click to center.",
                () => _viewModel.CanEditLayout),
            new("Media Browser", editor, () => _viewModel.ShowMediaBrowser, on => _viewModel.ShowMediaBrowser = on,
                "The media browser: a folder's videos, sounds and pictures, to drag onto the timeline."),
            new("Cut Segments", always, () => _viewModel.ShowCutSegmentsPane, on => _viewModel.ShowCutSegmentsPane = on,
                "The list of cut segments beside the player."),
            new("Keyframes", editor, () => _viewModel.ShowKeyframesPane, on => _viewModel.ShowKeyframesPane = on,
                "The selected clip's keyframes on a timeline of their own, with the keyframe controls and its place, size and rotation at the playhead."),
            new("Effects", editor, () => _viewModel.ShowEffectsPane, on => _viewModel.ShowEffectsPane = on,
                "The selected clip's effect stack (or the Sequence Output's), in the order the effects are applied."),
            new("Status Bar", always, () => AppSettings.Current.ShowStatusBar, SetStatusBarShown,
                "The bar along the bottom of the window. Hidden, the status and the progress of background work are in the title bar."),
        ];
    }

    /// <summary>The Views menu is opening: its Tabs and Panes lists made for this mode as it is now.</summary>
    private void FillViewsLists()
    {
        static void Fill(Panel list, IEnumerable<ViewToggle> toggles)
        {
            list.Children.Clear();
            foreach (var toggle in toggles.Where(t => t.Available()))
            {
                var box = new CheckBox { Content = toggle.Name, IsChecked = toggle.IsOn(), Margin = new Thickness(0, 2, 0, 2), ToolTip = toggle.About, IsEnabled = toggle.Enabled?.Invoke() ?? true };
                var kind = list.Name == nameof(ViewsTabList) ? " Tab" : toggle.Name.EndsWith("Pane", StringComparison.Ordinal) || toggle.Name.EndsWith("Bar", StringComparison.Ordinal) ? "" : " Pane";
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
            foreach (var toggle in toggles.Where(t => t.Available()))
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
