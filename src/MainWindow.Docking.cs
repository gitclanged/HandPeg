using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The panes: arranged freely, in this window and the side windows -----
// Every pane (the player, the playback controls, the timeline, the Sequence Overview Scrubber, the layout, the header, the side panes,
// the preset bar and each settings tab) is made in PaneLibrary and put where the arrangement says by the dock
// manager (Docking/DockManager.cs). The arrangement is kept in AppSettings.DockLayout, the side windows (5.4C: any number, each a
// Live Video Preview or a Pane Docker) in AppSettings.SideWindows.
public partial class MainWindow
{
    private DockManager _dock = null!;

    // The settings tabs, as panes: their names, by the tab they came from.
    private readonly List<(string Id, TabItem Tab)> _tabPanes = [];

    // The Sequence Overview Scrubber made taller by the border beside it: what a drag has moved but not yet made a step.
    private double _timelineResizeRest;

    /// <summary>
    /// The timeline (4.4: one drawn surface for every track, Views/UnifiedTimelinePane.xaml). Its pane's name is not
    /// "Timeline": that one is the Sequence Overview Scrubber's (the Overview Bar's until 5.2, the master timeline's until
    /// 4.4), kept so arrangements keep it where it was.
    /// </summary>
    private const string TimelinePaneId = "UnifiedTimeline";

    /// <summary>Takes the panes out of the library, tells the dock manager what each is, and lays the window out as it was left.</summary>
    private void InitializeDocking()
    {
        // The Source pane (gone since Pass 1.5: Add Media in the header took its job) is where saved arrangements may still
        // have it: the Media pane goes there, and is shown. An arrangement from before 4.4 has the timeline put where its
        // Layers tab was, and one from before 4.4-BugHunt the timeline first among the settings tabs (UpgradeLayout).
        _dock = new DockManager(this, MainDockHost, DefaultDockLayout)
        {
            Replaced = new Dictionary<string, string> { ["Source"] = "Media" },
            Upgrade = UpgradeLayout,

            // A tab's menu's Reset Panes Layout is the Views menu's (5.4D): the user's Default layout, the scrubber's height put back.
            ResetLayout = () => ResetPanesLayout_Click(this, new RoutedEventArgs()),
        };
        _dock.PaneReplaced += (_, replacement) =>
        {
            if (replacement == "Media")
                _viewModel.ShowMediaBrowser = true;
        };

        // The panes' contents leave the library: from now on they are wherever the arrangement puts them.
        var contents = PaneLibrary.Children.OfType<FrameworkElement>().ToList();
        foreach (var content in contents.Where(c => !ReferenceEquals(c, SettingsTabs)))
            PaneLibrary.Children.Remove(content);

        // Each pane's least width (5.4D, the splitter's range): 180 unless said otherwise below (DockPane.MinWidth's own), so a border always has
        // room to move in a 1280 DIP window; the sums of these are what pin a column (DockManager.MinWidthOf).
        void Add(string id, string title, FrameworkElement content, PaneHandle handle = PaneHandle.TitleSlot, bool fits = false,
            Func<bool>? shown = null, Action? close = null, Func<double>? minWidth = null, double minHeight = 60, Panel? hoverHost = null, Action<double>? resize = null,
            Action<PaneShape>? reshape = null, bool joinsBelow = false, bool floats = false, Action? resetSize = null) =>
            _dock.Register(new DockPane
            {
                Id = id, Title = title, Content = () => content, Handle = handle, FitsContent = fits, IsShown = shown ?? (() => true), Close = close,
                MinWidth = minWidth ?? (() => 180), MinHeight = minHeight, HoverHost = hoverHost, ResizeBy = resize, ResetSize = resetSize, Reshape = reshape,
                JoinsPaneBelow = joinsBelow, Floats = floats,
            });

        // The player floats over its place (DockPane.Floats): the panes are laid out again around it, and its video never blinks. Its least width
        // stays 320 (as PlayerPane's own MinWidth in the XAML): its transport overlay and a 16:9 picture need it.
        Add("Player", "Player", PlayerPane, PaneHandle.Hover, minWidth: () => 320, minHeight: 180, hoverHost: PlayerOverlay, floats: true);
        // The playback controls and the Sequence Overview Scrubber keep 300: rows as tall as their content, they share a column with the player
        // or the timeline, at least as wide, in every built-in layout.
        Add("Playback", "Playback Controls", TransportRow, PaneHandle.Hover, fits: true, shown: () => _viewModel.ShowAdvancedPlayback, minWidth: () => 300);
        // The Sequence Overview Scrubber: the whole sequence at a glance, its segments and markers, scrubbed by a click or a
        // drag (the Overview Bar until 5.2-AuditB, the master timeline until 4.4; its id stays "Timeline" and its setting
        // ShowMasterTimeline, so saved arrangements, layouts and settings keep it where and as they had it).
        // A double-click on the border under it puts its height back (5.4D), as one on its own grip does (OverviewGrip_ResetRequested).
        Add("Timeline", ScrubberTitle, TimelinePane, PaneHandle.Hover, fits: true, shown: () => AppSettings.Current.ShowMasterTimeline, minWidth: () => 300, resize: ResizeMasterTimeline,
            resetSize: OverviewGrip_ResetRequested);
        // The timeline: every track on one drawn surface (4.4). Always shown; its tab is in its own title row. 300: its track headers and a
        // stretch of the ruler.
        Add(TimelinePaneId, "Timeline", UnifiedTimelinePanel, minWidth: () => 300, minHeight: 140);
        // The Canvas: the output frame the layers are arranged on. Its id stays "Layout" (its name until 4.2), so saved
        // arrangements and layouts keep it where they had it. 180 (5.4D; 200 before).
        Add("Layout", "Canvas", LayoutPane, shown: () => _viewModel.IsArrangeActive, close: () => _viewModel.IsArrangeActive = false);
        Add("Header", "Header", EditorHeader, PaneHandle.Hover, fits: true, minWidth: HeaderWidth, reshape: ApplyHeaderShape, joinsBelow: true);
        // The Media Browser: 180, or 280 while it shows the Filmstrip view (its list and the preview beside it: MediaMinWidth), raised and
        // lowered in place as the view changes (DockManager.RefreshMinSizes).
        Add("Media", "Media", MediaBrowserPanel, shown: () => _viewModel.IsMediaBrowserVisible, close: () => _viewModel.ShowMediaBrowser = false, minWidth: MediaMinWidth, minHeight: 140);
        MediaBrowser.ViewModeChanged += _ => _dock.RefreshMinSizes();
        // The keyframes and the effects: 180 (5.4D; 200 and 240 before), their lists' names trimmed rather than the column pinned.
        Add("Keyframes", "Keyframes", KeyframesPane, shown: () => _viewModel.ShowKeyframesPane, close: () => _viewModel.ShowKeyframesPane = false, minHeight: 110);
        Add("Effects", "Effects", EffectsPanePanel, shown: () => _viewModel.ShowEffectsPane, close: () => _viewModel.ShowEffectsPane = false, minHeight: 110);
        // The Master Meter (5.2B): the Sequence's meter and master fader upright. Off to start with, a tab beside the effects and keyframes
        // (StandardLayout; an arrangement saved without it takes it there), and Tabs and Panes shows it. 104: a meter stands narrow.
        Add("Meter", "Master Meter", MasterMeterPanel, shown: () => _viewModel.ShowMasterMeter, close: () => _viewModel.ShowMasterMeter = false, minWidth: () => 104, minHeight: 180);
        // The Preset Bar: the panes' 180 (5.4D; 160 before).
        Add("Presets", "Preset Bar", PresetBarPane, PaneHandle.Hover, fits: true, shown: () => _viewModel.ShowPresetBarAtTop);
        // The studios (5.4C): made by their factories when first shown, never in PaneLibrary (MainWindow.Studios.cs).
        RegisterStudioPanes();

        // The settings tabs: each a pane with a tab, made when first shown (RealizeTabContent), shown while its tab would be.
        // Audio is a box, its content moved between it and the Export tab (MainWindow.Export.cs). (5.4B retired the Video tab: a saved
        // arrangement naming Tab:Video loads without it, DockManager's validation dropping the unknown pane.)
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            var title = tab.Header as string ?? "Tab";
            var id = "Tab:" + title;
            _tabPanes.Add((id, tab));
            Func<FrameworkElement> content = id == ExportId ? ExportContent : IsSection(id) ? () => SectionSlot(id) : () => RealizeTabContent(tab);
            _dock.Register(new DockPane
            {
                Id = id, Title = title, Handle = PaneHandle.Tab, Content = content,
                // 300: the Export tab's cards (ExportCardMinWidth) and the other tabs' rows of settings.
                IsShown = () => tab.Visibility == Visibility.Visible, MinWidth = () => 300, MinHeight = 140,
            });
        }

        UpdateTabVisibility();
        foreach (var (_, tab) in _tabPanes)
            DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(TabItem)).AddValueChanged(tab, (_, _) => _dock.ScheduleRebuild());
        _dock.ExtendTabMenu = (menu, pane) =>
        {
            AddTabsAndPanesMenu(menu);
            AddPaneSettingsMenu(menu, pane);
        };

        _dock.Changed += () => _viewModel.SavePaneLayout(SaveDockInto);
        _dock.SideWindowOpened += HookSideWindow;
        _dock.SideChanged += () =>
        {
            UpdateSideWindowCheck();
            SyncMirrors();
        };
        _dock.PaneShown += OnPaneShown;
        // The layout chosen in the first-run window, the first time; then the user's Default layout (5.4D, StartupLayoutJson: the arrangement as
        // it was left while it still has that layout's shape), always opening on the Timeline (5.4A: never on Export, Audio or Filters because
        // HandPeg was closed on one of them). The side windows saved open (5.4C) come back after the first frame, each laid out on its own
        // (DockManager.OpenSidesWhenShown); this is the window's one dock build.
        _dock.Load(sideWindows: AppSettings.Current.SideWindows, saved: TakeFirstRunLayout() ?? StartupLayoutJson(), showFirst: TimelinePaneId);
        UpdateSideWindowCheck();

        // What shows or hides a pane.
        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModels.MainViewModel.ShowCommandPreviewTab):
                    UpdateTabVisibility();
                    break;
                case nameof(ViewModels.MainViewModel.ShowAdvancedPlayback)
                    or nameof(ViewModels.MainViewModel.IsMediaBrowserVisible) or nameof(ViewModels.MainViewModel.ShowKeyframesPane) or nameof(ViewModels.MainViewModel.ShowEffectsPane)
                    or nameof(ViewModels.MainViewModel.ShowPresetBarAtTop) or nameof(ViewModels.MainViewModel.ShowMasterMeter):
                    _dock.ScheduleRebuild();
                    break;

                // The Canvas turned on comes to the front of its group once the dock has it (since 5.3-Stage2 a tab behind the effects
                // in Standard Editing; it had a part of its own before, and showed at once): in place, as a click on its tab.
                case nameof(ViewModels.MainViewModel.IsArrangeActive):
                    _dock.ScheduleRebuild();
                    if (_viewModel.IsArrangeActive)
                        _dock.BringForward("Layout");
                    break;
            }
        };
    }

    /// <summary>
    /// The settings tabs that a setting leaves out (Command Preview the setting's, and the Tabs and Panes choices: HandPeg
    /// starts with Export instead of Video), then where the sections and the destination bar go (MainWindow.Export.cs). Set
    /// here, before the panes are first laid out, not bound in the XAML: a binding there is attached only once the window
    /// is up, so the first frame would have a tab in its bar that the next frame took away.
    /// </summary>
    private void UpdateTabVisibility()
    {
        foreach (var (id, tab) in _tabPanes)
            tab.Visibility = TabWanted(id) ? Visibility.Visible : Visibility.Collapsed;
        PlaceSections();
        PlaceDestinationBar();
    }

    /// <summary>
    /// Where the panes start (a pane switched off is hidden, not moved): the Standard Editing layout (see MainWindow.Layouts.cs), what an
    /// arrangement that cannot be read loads as and where a pane missing from one goes. (5.4D: Reset Layout puts back the user's Default
    /// layout, DefaultLayoutChoice, which is this one unless a ★ says otherwise.)
    /// </summary>
    private DockLayoutState DefaultDockLayout() => StandardLayout();

    /// <summary>The Media pane's least width (5.4D): 280 while the Media Browser shows its Filmstrip view (the list with the preview and cutter beside it), else 180.</summary>
    private double MediaMinWidth() => MediaBrowser.ViewMode == Views.MediaBrowserView.Filmstrip ? 280 : 180;

    /// <summary>How wide the header's buttons are together: its pane is never narrower, so none of them is cut off.</summary>
    private double HeaderWidth()
    {
        EditorHeader.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(Math.Ceiling(EditorHeader.DesiredSize.Width), _headerShape == PaneShape.Column ? 0 : 140);
    }

    // The header's shape as the dock manager last laid it out, and its buttons' own padding (for the shapes that keep it).
    private PaneShape _headerShape;
    private Dictionary<Control, Thickness>? _headerPadding;

    /// <summary>
    /// Lays the header out for where it is (told by the dock manager before each layout, and when its slot changes shape):
    /// a row of buttons against the right edge, at the top of its slot (Row: its buttons are never stretched taller); the
    /// same, centred and without the room under it, at the right-hand end of the row of boxed tabs of the pane right under
    /// it (Joined: the two headers are one bar); or, beside any other part (and in any slot taller than wide, or too narrow
    /// for a row), an upright 44 px strip of 32 x 32 icon buttons from its top (Column): Add Media, Layout, Project and
    /// Settings. The flyouts open beside the strip then
    /// (PlaceHeaderFlyout).
    /// </summary>
    private void ApplyHeaderShape(PaneShape shape)
    {
        _headerShape = shape;
        var column = shape == PaneShape.Column;
        Control[] buttons = [AddMediaButton, LayoutButton, ProjectButton, SettingsButton];
        _headerPadding ??= buttons.ToDictionary(b => b, b => b.Padding);

        EditorHeader.Orientation = column ? Orientation.Vertical : Orientation.Horizontal;
        EditorHeader.HorizontalAlignment = column ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        EditorHeader.VerticalAlignment = shape == PaneShape.Joined ? VerticalAlignment.Center : VerticalAlignment.Top;

        // A column leaves the hover strip its 7 pixels at the top, and a little room at each side.
        EditorHeader.Margin = column ? new Thickness(6, 9, 6, 0) : shape == PaneShape.Joined ? new Thickness(0) : new Thickness(0, 0, 0, 2);
        foreach (var part in EditorHeader.Children.OfType<FrameworkElement>())
            part.Margin = column ? new Thickness(0, 0, 0, 6) : new Thickness(6, 0, 0, shape == PaneShape.Joined ? 0 : 6);
        foreach (var button in buttons)
        {
            (button.Width, button.Height) = column ? (32, 32) : (double.NaN, double.NaN);
            button.Padding = column ? new Thickness(0) : _headerPadding[button];
        }
    }

    /// <summary>What the pane of the overview of the whole sequence is called on screen (its id stays "Timeline").</summary>
    internal const string ScrubberTitle = "Sequence Overview Scrubber";

    // How tall the Sequence Overview Scrubber can be dragged (5.2-AuditB, the user's directive: 16 to 120 px). Its height
    // setting (MainViewModel.MasterTimelineHeight) is in steps of 4 px over 12 (MainViewModel.TimelineAreaHeight), so the
    // steps a drag keeps to are those whose bar lies within that: 1 (16 px) to 27 (120 px).
    private const double ScrubberLeastPixels = 16, ScrubberMostPixels = 120, ScrubberPixelsPerStep = 4, ScrubberBasePixels = 12;

    /// <summary>A height setting for the Sequence Overview Scrubber, kept to the steps a drag can give it.</summary>
    private static double ScrubberStep(double step) => Math.Clamp(step,
        Math.Max(1, Math.Ceiling((ScrubberLeastPixels - ScrubberBasePixels) / ScrubberPixelsPerStep)),
        Math.Floor((ScrubberMostPixels - ScrubberBasePixels) / ScrubberPixelsPerStep));

    // The Sequence Overview Scrubber's lower edge (5.1-Polish, the user's directive: OverviewGrip): its height setting when the drag began.
    private double _overviewHeightAtDrag;

    private void OverviewGrip_DragStarted() => _overviewHeightAtDrag = _viewModel.MasterTimelineHeight;

    /// <summary>
    /// The Sequence Overview Scrubber's lower edge dragged (by <paramref name="pixels"/> of the window's content, EdgeGrip): its
    /// height setting, four pixels a step, so the edge stays under the pointer; kept to 16 to 120 px (ScrubberStep).
    /// </summary>
    private void OverviewGrip_Dragged(double pixels) =>
        _viewModel.MasterTimelineHeight = ScrubberStep(_overviewHeightAtDrag + Math.Round(pixels / ScrubberPixelsPerStep));

    private void OverviewGrip_ResetRequested() => _viewModel.MasterTimelineHeight = AppSettings.DefaultMasterTimelineHeight;

    /// <summary>
    /// The border under the Sequence Overview Scrubber dragged, or the one above it Ctrl-dragged (plainly dragged, where nothing
    /// above it shares the room: DockManager.AddBorder), told by how far the pointer moved since the last time: the bar's height
    /// setting, four pixels a step, kept to 16 to 120 px.
    /// </summary>
    private void ResizeMasterTimeline(double pixels)
    {
        _timelineResizeRest += pixels / ScrubberPixelsPerStep;
        var steps = Math.Truncate(_timelineResizeRest);
        if (steps == 0)
            return;

        _timelineResizeRest -= steps;
        _viewModel.MasterTimelineHeight = ScrubberStep(_viewModel.MasterTimelineHeight + steps);
    }

    /// <summary>A pane was put on screen: the Canvas draws its frame once it has a size.</summary>
    private void OnPaneShown(string id)
    {
        if (id == "Layout")
            Dispatcher.BeginInvoke(RedrawLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Makes what is in a settings tab: from its template (every tab is left as one until wanted; Export too since 5.3-Stage2), or
    /// the content it was made with. The named parts this window uses are found in it as it is made (the Audio tab's options row,
    /// which folds into a button when it is narrow; the Export tab's parts). (5.4A retired the Summary tab: its controls live in the
    /// Export tab, the Timeline's Game Presets box and the Project Manager.)
    /// </summary>
    private FrameworkElement RealizeTabContent(TabItem tab)
    {
        StartupTrace.Layout($"tab made: {tab.Header}");
        FrameworkElement content;
        if (tab.Tag is DataTemplate template)
        {
            tab.Tag = null;
            content = (FrameworkElement)template.LoadContent();
        }
        else
        {
            content = (FrameworkElement)tab.Content;
            tab.Content = null;
        }

        if (content.FindName(nameof(AudioOptionsHost)) is Grid host)
        {
            AudioOptionsHost = host;
            AudioOptionsInline = (StackPanel)content.FindName(nameof(AudioOptionsInline));
            AudioOptionsButton = (System.Windows.Controls.Primitives.ToggleButton)content.FindName(nameof(AudioOptionsButton));
            AudioPanel = (DockPanel)content.FindName(nameof(AudioPanel));
        }

        // The Audio tab's master meter and fader (5.2B): fed and wired as the pane's is.
        if (content.FindName("AudioTabMeter") is AudioMeterControl tabMeter)
            WireMeter(tabMeter);

        // The Export tab's parts (MainWindow.Export.cs); ExportContent fills it once they are found. ExportPanel is the template's root.
        if ((content.Name == nameof(ExportPanel) ? content : content.FindName(nameof(ExportPanel))) is DockPanel export)
        {
            T Part<T>(string name) where T : class => (T)content.FindName(name);
            ExportPanel = export;
            ExportBarSlot = Part<Border>(nameof(ExportBarSlot));
            ExportSections = Part<StackPanel>(nameof(ExportSections));
            ExportSectionHost = Part<Border>(nameof(ExportSectionHost));
            ExportColumns = Part<Grid>(nameof(ExportColumns));
        }

        return content;
    }

    /// <summary>
    /// Makes the panes nobody has seen yet, one at a time and only while nothing else is wanted of this thread, so that
    /// the first click on any of them finds it ready: the timeline (when an arrangement keeps it behind another tab), then
    /// the settings tabs.
    /// </summary>
    private void RealizeTabsWhenIdle()
    {
        string? next = !_dock.IsRealized(TimelinePaneId) ? TimelinePaneId : _tabPanes.FirstOrDefault(t => !_dock.IsRealized(t.Id)).Id;
        if (next is null)
        {
            GiveBackStartupMemory();
            return;
        }

        _dock.Realize(next);
        Dispatcher.BeginInvoke(RealizeTabsWhenIdle, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // ----- Arrangements from before 4.4 -----

    // The settings tabs 4.4 retired, in the order their place is taken for the timeline: the Layers tab's track list and
    // the Subtitles tab's captions are on the timeline now. (The Audio tab stays, holding the sound's settings; its lanes
    // moved to the timeline.)
    private static readonly string[] RetiredPanes = ["Tab:Layers", "Tab:Subtitles"];

    // How much of the room of the group it is put beside the timeline takes: the tabs keep the rest, at its right.
    private const double TimelineShare = 0.7;

    private static bool IsRetiredPane(string id) => Array.IndexOf(RetiredPanes, id) >= 0;

    /// <summary>
    /// An arrangement saved by an older HandPeg, brought up to this one (DockManager.Upgrade), step by step from the version
    /// it was made as. Every arrangement taken up passes through here before the one dock build of Load (the saved one, a
    /// built-in layout, one of the user's own, the first run's choice), so each step is quick, changes nothing that is up to
    /// date, and may run again at every start until the arrangement is next saved: nothing is saved here (it is saved as
    /// it is the next time the user changes it), and nothing here builds or lays out a pane. A 4.3 arrangement saved no
    /// version (it reads as the newest): its retired tabs tell it.
    /// </summary>
    private DockLayoutState UpgradeLayout(DockLayoutState state)
    {
        UpgradeLayout(state, TabIds);
        return state;
    }

    /// <summary>The steps of <see cref="UpgradeLayout(DockLayoutState)"/>, for the settings tabs given (their pane ids, in order): window-free, for the goldens too.</summary>
    internal static void UpgradeLayout(DockLayoutState state, IReadOnlyList<string> tabIds)
    {
        var from = new[] { state.Main, state.Side, state.SideClosed }.OfType<DockNode>().Any(root => root.AllPanes().Any(IsRetiredPane))
            ? 1 : state.Version;
        state.Version = DockLayoutState.CurrentVersion;
        if (from < 2)
            WithoutRetiredTabs(state);
        if (from < 3)
            PutTimelineFirst(state, tabIds);
        if (from < 4)
            StackCanvasWithEffects(state, tabIds);
    }

    /// <summary>
    /// 1 to 2 (4.4): the Layers and Subtitles tabs are taken out, and the timeline is put beside the group the Layers tab was
    /// in (else the Subtitles tab), at its left, with most of its room; a group of nothing but retired tabs becomes the
    /// timeline's own. So a 4.3 built-in layout becomes the 4.4 one of the same name (the Layout Selector's "In use" mark
    /// still finds it), and a layout the user made keeps everything else where it was. In the window the Layers tab was in:
    /// the main window, or the side window while it is open; the side window's closed arrangement (put back when it is
    /// opened again) gets it only when neither of those has it or gets it, so it is never in two trees. An arrangement that
    /// already has the timeline only loses the retired tabs.
    /// </summary>
    private static void WithoutRetiredTabs(DockLayoutState state)
    {
        // The window the timeline goes into: the one the Layers tab (else the Subtitles tab) was in, unless one of the
        // windows shown already has it.
        var side = state.SideOpen ? state.Side : null;
        DockNode? WindowWith(string id) => state.Main.GroupOf(id) is not null ? state.Main : side?.GroupOf(id) is not null ? side : null;
        var shown = state.Main.GroupOf(TimelinePaneId) is not null || side?.GroupOf(TimelinePaneId) is not null;
        var home = shown ? null : RetiredPanes.Select(WindowWith).FirstOrDefault(window => window is not null);

        state.Main = WithoutRetiredPanes(state.Main, ReferenceEquals(home, state.Main)) ?? DockNode.Group();
        state.Side = WithoutRetiredPanes(state.Side, home is not null && ReferenceEquals(home, state.Side));

        // The closed side window's arrangement gets the timeline only when no window shown has it or gets it: opening the
        // side window takes its panes out of the main window, so a timeline in both would move away from where Layers was.
        state.SideClosed = WithoutRetiredPanes(state.SideClosed, placeTimeline: !shown && home is null);
    }

    /// <summary>
    /// 2 to 3 (4.4-BugHunt, the user's Q8): Standard Editing as 4.4 first made it (the timeline's own group beside the
    /// settings tabs) becomes Standard Editing as it is now, the timeline the tabs' first and shown, in the place and the
    /// share of the room the two had; in any other arrangement, a group holding the timeline and settings tabs gets the
    /// timeline as its first tab (the tab shown stays the one shown). Nothing else moves.
    /// </summary>
    private static void PutTimelineFirst(DockLayoutState state, IReadOnlyList<string> tabIds)
    {
        if (DockManager.SameShape(state, StandardLayoutBeside(tabIds)) && state.Main.GroupOf(TimelinePaneId) is { } alone
            && state.Main.ParentOf(alone) is { IsGroup: false, Vertical: false, Children.Count: 2 } beside
            && ReferenceEquals(beside.Children[0], alone) && beside.Children[1] is { IsGroup: true } tabs
            && state.Main.ParentOf(beside) is { } parent)
        {
            // The pair's place in its split (and so its share of the room, Sizes by index) goes to the tabs' group.
            parent.Children[parent.Children.IndexOf(beside)] = TimelineFirst(tabs);
            return;
        }

        foreach (var root in new[] { state.Main, state.Side, state.SideClosed }.OfType<DockNode>())
        {
            if (root.GroupOf(TimelinePaneId) is { Panes.Count: > 1 } group && group.Panes[0] != TimelinePaneId
                && group.Panes.Any(p => p.StartsWith("Tab:", StringComparison.Ordinal)))
            {
                group.Panes.Remove(TimelinePaneId);
                group.Panes.Insert(0, TimelinePaneId);
            }
        }
    }

    /// <summary>
    /// 3 to 4 (5.3-Stage2, the user's de-clutter): Standard Editing as version 3 had it (the Canvas in a part of its own over the
    /// effects, keyframes and Master Meter) gets the Canvas as a tab of that group, after the keyframes, the group taking the
    /// column's place and so its share of the room; the tab shown stays the one shown. Only that shape, with the Meter (5.2B on) or
    /// without it (a 4.4 arrangement just brought up by PutTimelineFirst: the Meter is put in afterwards, by DockManager.PlaceAsDefault);
    /// any arrangement the user made is left as it is.
    /// </summary>
    private static void StackCanvasWithEffects(DockLayoutState state, IReadOnlyList<string> tabIds)
    {
        var withoutMeter = StandardLayoutV3(tabIds);
        withoutMeter.Main.GroupOf("Meter")?.Panes.Remove("Meter");
        if (!(DockManager.SameShape(state, StandardLayoutV3(tabIds)) || DockManager.SameShape(state, withoutMeter))
            || state.Main.GroupOf("Layout") is not { Panes.Count: 1 } canvas
            || state.Main.ParentOf(canvas) is not { IsGroup: false, Vertical: true, Children.Count: 2 } column
            || column.Children.Find(c => !ReferenceEquals(c, canvas)) is not { IsGroup: true } effects
            || state.Main.ParentOf(column) is not { } parent)
            return;

        var keys = effects.Panes.IndexOf("Keyframes");
        effects.Panes.Insert(keys < 0 ? effects.Panes.Count : keys + 1, "Layout");

        // The same place in its split, so the column's share of the room (Sizes, by index) is the group's.
        parent.Children[parent.Children.IndexOf(column)] = effects;
    }

    /// <summary>
    /// One window's tree without the retired tabs (a group showing one of them shows its first pane left instead); when it
    /// is to have the timeline (and has none), the timeline beside the group the first retired tab was in, or in its place
    /// when that group held nothing else. Returns the tree's top part, null when nothing is left.
    /// </summary>
    private static DockNode? WithoutRetiredPanes(DockNode? root, bool placeTimeline)
    {
        if (root is null)
            return null;

        var anchor = placeTimeline && root.GroupOf(TimelinePaneId) is null
            ? RetiredPanes.Select(root.GroupOf).FirstOrDefault(group => group is not null)
            : null;
        foreach (var group in root.AllGroups())
        {
            group.Panes.RemoveAll(IsRetiredPane);
            if (group.Active is { } active && IsRetiredPane(active))
                group.Active = group.Panes.FirstOrDefault();
        }

        if (anchor is null)
            return DockTree.Tidy(root);

        if (anchor.Panes.Count == 0)
        {
            anchor.Panes.Add(TimelinePaneId);
            anchor.Active = TimelinePaneId;
            return DockTree.Tidy(root);
        }

        return DockTree.Tidy(DockTree.InsertBeside(root, anchor, DockZone.Left, DockNode.Group(TimelinePaneId), TimelineShare));
    }

    /// <summary>
    /// The start's warm-up is over (every tab made): what it took and let go is given back to the system once, away
    /// from the interface's thread (v4.1.0). A start allocates little after its first frame, so the collector would not
    /// run again for a long while and kept about 40 MB of the start's garbage committed: 10 s after the first frame
    /// HandPeg read 212 to 215 MB where the collection leaves it under 200 (the Media pane did the same by chance in
    /// 4.0, after making its pictures). Done once per start, at ApplicationIdle, as the Media pane's GiveBackMemory.
    ///
    /// 4.2D: 4.2's panes brought the 10 s reading back to 213 to 219 MB (4.2C), so the give-back goes further. The large
    /// object heap is compacted too (the start's big buffers: XAML, the JIT profile, the dock's first measure), the process
    /// heap is compacted (the native side's start: WPF's own allocations and the drivers'), and the pages the start touched
    /// once and never again (start-up code, resources read once) leave the working set. They stay in memory as standby
    /// pages, so what is touched again comes back at once, from RAM, without a disk read.
    /// </summary>
    private void GiveBackStartupMemory()
    {
        if (_startupMemoryGivenBack)
            return;

        _startupMemoryGivenBack = true;
        Task.Run(() =>
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            HeapCompact(GetProcessHeap(), 0);
            K32EmptyWorkingSet(GetCurrentProcess());
        });
    }

    private bool _startupMemoryGivenBack;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial IntPtr GetProcessHeap();

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial nuint HeapCompact(IntPtr heap, uint flags);

    // The pseudo-handle of this process (-1): never closed, needs no rights beyond the process's own.
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool K32EmptyWorkingSet(IntPtr process);

    // ----- The side windows (5.4C: any number, each a Live Video Preview or a Pane Docker) -----

    /// <summary>
    /// The Layout Selector's "+ New Side Window" (5.4D: in its footer): a new side window, a Live Video Preview to start with (its title bar
    /// switches it to a Pane Docker). A layout only previewed is put back first, and then the selector closed, before the window opens
    /// (5.4D: the popup's Closed comes later, after the new window's Changed has saved the arrangement).
    /// </summary>
    private void SideWindowRow_Click(object sender, RoutedEventArgs e)
    {
        RestoreLayoutBeforePreview();
        _layoutHover.Close();
        _dock.OpenSideWindow(SideWindowMode.LiveVideoPreview);
        _viewModel.StatusText = "A new side window shows the player live: put it on another monitor (F11 or a double-click: full screen). Its title bar makes it a Pane Docker, or drag any pane's tab out of the window.";
    }

    /// <summary>Keeps the arrangement, and (5.4C) the side windows, in the settings: on every dock change and at close.</summary>
    private void SaveDockInto(AppSettings settings)
    {
        settings.DockLayout = _dock.Save();
        settings.SideWindows = _dock.SaveSideWindows();
    }

    /// <summary>
    /// The Layout Selector's rows for the side windows open (5.4C; 5.4D: in its footer): each one's name (a click brings it to the front) and a
    /// close button. Made as the selector opens (OpeningLayoutSelector) and whenever a side window opens, closes or changes its mode.
    /// </summary>
    private void UpdateSideWindowCheck()
    {
        SideWindowRows.Children.Clear();
        foreach (var (id, number, _, name) in _dock.SideWindowList)
        {
            var row = new Button { Content = name, ToolTip = $"{name}: brings it to the front.", HorizontalContentAlignment = HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetName(row, name);
            row.Click += (_, _) =>
            {
                RestoreLayoutBeforePreview();
                _layoutHover.Close();
                _dock.BringSideWindowForward(id);
            };

            var close = new Button
            {
                Content = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 10, Padding = new Thickness(8, 4, 8, 4),
                ToolTip = $"Closes side window {number} (a Pane Docker's panes go back to this window).", Margin = new Thickness(2, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(close, $"Close Side Window {number}");

            // A layout only previewed is put back first: closing a Pane Docker puts its panes into the arrangement, which is saved. Its row goes
            // with the button that had the keyboard, so after a click with the pointer the selector lets the keyboard go (as a card's pill does).
            close.Click += (_, _) =>
            {
                RestoreLayoutBeforePreview();
                _dock.CloseSideWindow(id);
                if (!FromKeyboard)
                    LeaveSelectorToPointer();
            };

            // Under the footer's buttons, 8 DIPs down (the card's row spacing), then close together.
            var line = new DockPanel { Margin = new Thickness(0, SideWindowRows.Children.Count == 0 ? 8 : 2, 0, 0) };
            DockPanel.SetDock(close, Dock.Right);
            line.Children.Add(close);
            line.Children.Add(row);
            SideWindowRows.Children.Add(line);
        }
    }

    // The side windows answer keys and dropped files as this window does.
    private readonly HashSet<Window> _hookedSides = [];
    private bool _mirrorNotesHooked;

    private void HookSideWindow(SideWindow side)
    {
        if (!_hookedSides.Add(side))
            return;

        side.PreviewKeyDown += MainWindow_PreviewKeyDown;
        HookTimelineMenuKey(side);

        // The sideways wheel over a Timeline moved here (5.4C review): each window hears its own WM_MOUSEHWHEEL; the hook answers only
        // while the pointer is over the Timeline.
        void HookSidewaysWheel() =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(side).Handle)?.AddHook(TimelineSidewaysWheel);
        if (new System.Windows.Interop.WindowInteropHelper(side).Handle != IntPtr.Zero)
            HookSidewaysWheel();
        else
            side.SourceInitialized += (_, _) => HookSidewaysWheel();
        side.AllowDrop = true;
        side.PreviewDragOver += Window_PreviewDragOver;
        side.PreviewDrop += Window_PreviewDrop;

        // A Live Video Preview's surface made or gone: its mirror attached or let go (MainWindow.GpuPreview.cs).
        side.PreviewSurfaceChanged += _ => SyncMirrors();
        side.Closed += (_, _) =>
        {
            _hookedSides.Remove(side);
            SyncMirrors();
        };

        // The player hidden or shown again: the previews say whether they show its last picture.
        if (!_mirrorNotesHooked)
        {
            _mirrorNotesHooked = true;
            VideoView.IsVisibleChanged += (_, _) => UpdateMirrorNotes();
            VideoView.SizeChanged += (_, _) => UpdateMirrorNotes();
        }
    }
}
