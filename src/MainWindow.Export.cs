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
// The Export tab holds what Video and Audio hold while their own tabs are hidden (Video starts so; the chapters are on it),
// and under them the output: DestinationBar itself, moved into the tab while it is shown and back to the bottom of the
// window while it is not, so Save As and Start Encode are always somewhere and never twice. Each section's content is
// made once (from its tab's template, when first wanted) and moved between its own tab and the Export tab: the names
// the window finds in it (RealizeTabContent) stay the same.
public partial class MainWindow
{
    /// <summary>The tabs whose contents the Export tab holds while they are hidden, in its order, with the word its button shows.</summary>
    private static readonly (string Id, string Label)[] SectionTabs = [("Tab:Video", "Video"), ("Tab:Audio", "Audio")];

    private const string ExportId = "Tab:Export";

    // Each section tab's own pane content (a box its content is put into while the tab is shown), and each section's content once made.
    private readonly Dictionary<string, Border> _sectionSlots = [];
    private readonly Dictionary<string, FrameworkElement> _sectionContents = [];

    // The section the Export tab shows, and where DestinationBar lives at the bottom of the window.
    private string _exportSection = SectionTabs[0].Id;
    private (Panel Parent, int Index)? _destinationHome;

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

        // A single section needs no button to choose it; Easy Social Export shows none.
        ExportSections.Visibility = inExport.Count > 1 && !EasyExport ? Visibility.Visible : Visibility.Collapsed;
        if (_dock.IsRealized(ExportId))
            FillExportSection();
    }

    /// <summary>
    /// The Export tab's middle: Easy Social Export's cards, or the chosen section's content, or the hint that each section
    /// has a tab of its own. While the cards are shown no section is made or moved in (nothing made that is not on screen).
    /// </summary>
    private void FillExportSection()
    {
        if (ExportTab.Visibility != Visibility.Visible)
            return;

        ApplyExportMode();
        if (EasyExport)
            return;

        if (SectionTabs.All(s => TabOf(s.Id).Visibility == Visibility.Visible))
            ExportSectionHost.Child = ExportNoSections;
        else
            Put(_exportSection, ExportSectionHost);
    }

    // ----- Easy Social Export | Advanced Export (Pass 4.2B-2) -----

    private static bool EasyExport => AppSettings.Current.UseEasySocialExport;

    private bool _applyingExportMode;

    /// <summary>The switch, the cards or the sections, as AppSettings.UseEasySocialExport says.</summary>
    private void ApplyExportMode()
    {
        var easy = EasyExport;
        _applyingExportMode = true;
        try
        {
            (EasyExportButton.IsChecked, AdvancedExportButton.IsChecked) = (easy, !easy);
        }
        finally
        {
            _applyingExportMode = false;
        }

        EasyExportPanel.Visibility = easy ? Visibility.Visible : Visibility.Collapsed;
        ExportSectionHost.Visibility = easy ? Visibility.Collapsed : Visibility.Visible;
        if (easy)
        {
            ExportSections.Visibility = Visibility.Collapsed;
            MakeSocialCards();
        }
        else
        {
            ExportSections.Visibility = ExportSections.Children.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ExportMode_Checked(object sender, RoutedEventArgs e)
    {
        var easy = ReferenceEquals(sender, EasyExportButton);
        if (_applyingExportMode || easy == EasyExport)
            return;

        _viewModel.SetViewSetting(s => s.UseEasySocialExport = easy);
        FillExportSection();
    }

    // The cards, made once (their words are the view model's); the chosen one marked again after each click.
    private readonly List<(SocialExportTarget Target, FrameworkElement Card, List<(int SizeMb, Button Button)> Sizes)> _socialCards = [];

    private void MakeSocialCards()
    {
        if (_socialCards.Count == 0)
        {
            foreach (var target in _viewModel.SocialExportTargets)
            {
                var sizes = new List<(int, Button)>();
                FrameworkElement card;
                if (target.SizesMb.Count == 0)
                {
                    var button = new Button { Content = SocialCardFace(target, null) };
                    button.SetResourceReference(StyleProperty, "LayoutCard");
                    button.Click += (_, _) => ChooseSocialExport(target, 0);
                    AutomationProperties.SetName(button, target.Title);
                    card = button;
                }
                else
                {
                    // A card of several sizes: each size is its own one-click button.
                    var row = new WrapPanel { Margin = new Thickness(26, 6, 0, 0) };
                    foreach (var size in target.SizesMb)
                    {
                        var button = new Button { Content = $"< {size} MB", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
                        button.Click += (_, _) => ChooseSocialExport(target, size);
                        AutomationProperties.SetName(button, $"{target.Title} under {size} MB");
                        button.ToolTip = $"Aims the file under {size} MB: an average bitrate worked out from the output's length and the sound's.";
                        row.Children.Add(button);
                        sizes.Add((size, button));
                    }

                    var box = new Border { BorderThickness = new Thickness(1.5), Padding = new Thickness(6, 6, 6, 5), Margin = new Thickness(4), Child = SocialCardFace(target, row) };
                    box.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
                    box.SetResourceReference(Border.CornerRadiusProperty, "CardCornerRadius");
                    card = box;
                }

                card.Width = 260;
                card.ToolTip = target.Detail;
                EasyExportCards.Children.Add(card);
                _socialCards.Add((target, card, sizes));
            }
        }

        MarkSocialChoice();
    }

    /// <summary>A card's face: the glyph, the title and what it does (and, for a card of sizes, their buttons under them).</summary>
    private static FrameworkElement SocialCardFace(SocialExportTarget target, FrameworkElement? below)
    {
        var glyph = new TextBlock { Text = target.Glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 18, Width = 26, Margin = new Thickness(0, 1, 0, 0) };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var title = new TextBlock { Text = target.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { Text = target.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var words = new StackPanel { Children = { title, detail } };
        DockPanel.SetDock(glyph, Dock.Left);
        var face = new DockPanel { Children = { glyph, words }, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (below is null)
            return face;

        return new StackPanel { Children = { face, below } };
    }

    private void ChooseSocialExport(SocialExportTarget target, int sizeMb)
    {
        _viewModel.ApplySocialExport(target, sizeMb);
        MarkSocialChoice();
    }

    /// <summary>The card chosen last, outlined as the layout in use is (LayoutCard's "active"); a card of sizes, its size in bold.</summary>
    private void MarkSocialChoice()
    {
        var choice = _viewModel.SocialExportChoice;
        foreach (var (target, card, sizes) in _socialCards)
        {
            var chosen = choice?.Id == target.Id;
            if (card is Button button)
                button.Tag = chosen ? "active" : null;
            else if (card is Border box)
                box.SetResourceReference(Border.BorderBrushProperty, chosen ? "SelectedBorderBrush" : "ControlBorderBrush");
            foreach (var (size, sizeButton) in sizes)
                sizeButton.FontWeight = chosen && choice?.SizeMb == size ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>The Export tab's pane content, made when first shown (or at idle): its middle filled then.</summary>
    private FrameworkElement ExportContent()
    {
        var content = RealizeTabContent(ExportTab);
        AddExportRendererChoice();
        FillExportSection();
        return content;
    }

    // ----- What renders the export's frames (5.0, AppSettings.ExportRenderer) -----

    private ComboBox? _exportRendererBox;

    /// <summary>
    /// The Export tab's Render choice, after Easy Social Export | Advanced Export: the GPU compositor or FFmpeg's filter
    /// graph, the very setting Settings' Export Renderer row shows (shown afresh whenever the window is active again, so a
    /// change made there is seen here).
    /// </summary>
    private void AddExportRendererChoice()
    {
        if (_exportRendererBox is not null)
            return;

        var label = new TextBlock { Text = "Render:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var box = new ComboBox
        {
            ItemsSource = AppSettings.ExportRenderers,
            DisplayMemberPath = nameof(ExportRendererChoice.Display),
            SelectedValuePath = nameof(ExportRendererChoice.Value),
            MinWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "What draws the export's frames. GPU Compositor: the engine that draws the player renders every frame on the graphics card, "
                      + "and FFmpeg encodes them with the export's own sound, captions and subtitles. FFmpeg Filter Graph: FFmpeg composes the picture "
                      + "itself, as the Command Preview shows (the bit-exact reference). A Copy export, a GIF or WebP, a hand-edited command and anything "
                      + "the compositor cannot draw yet always go through FFmpeg, and so does a GPU export that fails, from the start. The same setting "
                      + "as Settings > Export & Hardware > Export Renderer.",
        };
        AutomationProperties.SetName(box, "Export Renderer");
        _exportRendererBox = box;
        SyncExportRenderer();
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedValue is string value && !string.Equals(value, AppSettings.Current.ExportRenderer, StringComparison.Ordinal))
                _viewModel.SetViewSetting(s => s.ExportRenderer = value);
        };

        ExportModeBar.Children.Add(label);
        ExportModeBar.Children.Add(box);
        Activated += (_, _) => SyncExportRenderer();
    }

    /// <summary>The Render choice as the setting is now; one saved as neither shows the GPU (what it is taken as).</summary>
    private void SyncExportRenderer()
    {
        if (_exportRendererBox is not { } box)
            return;
        var value = AppSettings.Current.WantsGpuExport ? AppSettings.ExportRendererGpu : AppSettings.ExportRendererFfmpeg;
        if (!Equals(box.SelectedValue, value))
            box.SelectedValue = value;
    }

    /// <summary>
    /// Save As, Browse, Add to Queue and Start Encode: in the Export tab while it is shown, else at the bottom of the
    /// window while Export is hidden.
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

    /// <summary>A tab or a pane the menu shows or hides: its name, whether it is shown, how to show or hide it, and what it is.</summary>
    private sealed record ViewToggle(string Name, Func<bool> IsOn, Action<bool> Set, string About, Func<bool>? Enabled = null);

    private IEnumerable<ViewToggle> TabToggles() => _tabPanes.Select(t => new ViewToggle(
        t.Tab.Header as string ?? t.Id, () => TabWanted(t.Id), on => SetTabShown(t.Id, on),
        t.Id switch
        {
            ExportId => "The Export tab: the video settings and the chapters, and the audio settings (those whose own tab is hidden), the source's subtitles, with Save As, Add to Queue and Start Encode.",
            "Tab:Video" => "The Video tab of its own: how the picture is encoded, and the chapters. Hidden, what it holds is in the Export tab.",
            "Tab:Audio" => "The Audio tab of its own: how the sound is encoded and mixed, ducking, the master mix and the voiceover (the sounds themselves are on the timeline). Hidden, what it holds is in the Export tab.",
            "Tab:Command Preview" => "The FFmpeg command, to read or edit (the same as the setting in Settings).",
            _ => $"The {t.Tab.Header} tab.",
        }));

    private IEnumerable<ViewToggle> PaneToggles()
    {
        void Refresh() => _dock.ScheduleRebuild();
        return
        [
            new("Overview Bar", () => AppSettings.Current.ShowMasterTimeline,
                on => { _viewModel.SavePaneLayout(s => s.ShowMasterTimeline = on); Refresh(); },
                "The overview bar under the player: the whole sequence at a glance, its cut segments, markers and thumbnails (the timeline itself is a pane of its own)."),
            new("Playback Controls", () => _viewModel.ShowAdvancedPlayback,
                on => _viewModel.SetViewSetting(s => s.ShowAdvancedPlayback = on, nameof(MainViewModel.ShowAdvancedPlayback), nameof(MainViewModel.ShowSimplePlayback)),
                "The playback controls under the player: play, the edit points, the speed and volume."),
            new("Preset Bar", () => _viewModel.ShowPresetBarAtTop,
                on => _viewModel.SetViewSetting(s => s.PresetBarLocation = on ? AppSettings.PresetBarAtTop : AppSettings.PresetBarInSummary,
                    nameof(MainViewModel.ShowPresetBarAtTop), nameof(MainViewModel.ShowPresetBarInSummary)),
                "The preset bar as a pane of its own. Hidden, it is on the Summary tab."),
            new("Canvas", () => _viewModel.IsArrangeActive, on => _viewModel.IsArrangeActive = on,
                "The Canvas beside the video: the output frame with the main video, the layers and the caption box. Drag to move, drag a corner handle to resize, double-click to center.",
                () => _viewModel.CanEditLayout),
            new("Media Browser", () => _viewModel.ShowMediaBrowser, on => _viewModel.ShowMediaBrowser = on,
                "The media browser: a folder's videos, sounds and pictures, to drag onto the timeline."),
            new("Keyframes", () => _viewModel.ShowKeyframesPane, on => _viewModel.ShowKeyframesPane = on,
                "The selected clip's keyframes on a timeline of their own, with the keyframe controls and its place, size and rotation at the playhead."),
            new("Effects", () => _viewModel.ShowEffectsPane, on => _viewModel.ShowEffectsPane = on,
                "The selected clip's effect stack (or the Sequence Output's), in the order the effects are applied."),
            new("Status Bar", () => AppSettings.Current.ShowStatusBar, SetStatusBarShown,
                "The bar along the bottom of the window. Hidden, the status and the progress of background work are in the title bar."),
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
