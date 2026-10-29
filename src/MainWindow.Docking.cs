using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The panes: arranged freely, in this window and the side window -----
// Every pane (the player, the playback controls, the timeline, the overview bar, the layout, the header, the side panes,
// the preset bar and each settings tab) is made in PaneLibrary and put where the arrangement says by the dock
// manager (Docking/DockManager.cs). The arrangement is kept in AppSettings.DockLayout.
public partial class MainWindow
{
    private DockManager _dock = null!;

    // The settings tabs, as panes: their names, by the tab they came from.
    private readonly List<(string Id, TabItem Tab)> _tabPanes = [];

    // The overview bar made taller by the border beside it: what a drag has moved but not yet made a step.
    private double _timelineResizeRest;

    /// <summary>
    /// The timeline (4.4: one drawn surface for every track, Views/UnifiedTimelinePane.xaml). Its pane's name is not
    /// "Timeline": that one is the overview bar's (the master timeline's until 4.4), kept so arrangements keep it where it was.
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

        void Add(string id, string title, FrameworkElement content, PaneHandle handle = PaneHandle.TitleSlot, bool fits = false,
            Func<bool>? shown = null, Action? close = null, Func<double>? minWidth = null, double minHeight = 60, Panel? hoverHost = null, Action<double>? resize = null,
            Action<PaneShape>? reshape = null, bool joinsBelow = false, bool floats = false) =>
            _dock.Register(new DockPane
            {
                Id = id, Title = title, Content = () => content, Handle = handle, FitsContent = fits, IsShown = shown ?? (() => true), Close = close,
                MinWidth = minWidth ?? (() => 160), MinHeight = minHeight, HoverHost = hoverHost, ResizeBy = resize, Reshape = reshape, JoinsPaneBelow = joinsBelow,
                Floats = floats,
            });

        // The player floats over its place (DockPane.Floats): the panes are laid out again around it, and its video never blinks.
        Add("Player", "Player", PlayerPane, PaneHandle.Hover, minWidth: () => 320, minHeight: 180, hoverHost: PlayerOverlay, floats: true);
        Add("Playback", "Playback Controls", TransportRow, PaneHandle.Hover, fits: true, shown: () => _viewModel.ShowAdvancedPlayback, minWidth: () => 300);
        // The overview bar: the whole sequence at a glance, its segments and markers (the master timeline until 4.4; its id
        // stays "Timeline", so saved arrangements and layouts keep it where they had it).
        Add("Timeline", "Overview Bar", TimelinePane, PaneHandle.Hover, fits: true, shown: () => AppSettings.Current.ShowMasterTimeline, minWidth: () => 300, resize: ResizeMasterTimeline);
        // The timeline: every track on one drawn surface (4.4). Always shown; its tab is in its own title row.
        Add(TimelinePaneId, "Timeline", UnifiedTimelinePanel, minWidth: () => 300, minHeight: 140);
        // The Canvas: the output frame the layers are arranged on. Its id stays "Layout" (its name until 4.2), so saved
        // arrangements and layouts keep it where they had it.
        Add("Layout", "Canvas", LayoutPane, shown: () => _viewModel.IsArrangeActive, close: () => _viewModel.IsArrangeActive = false, minWidth: () => 200);
        Add("Header", "Header", EditorHeader, PaneHandle.Hover, fits: true, minWidth: HeaderWidth, reshape: ApplyHeaderShape, joinsBelow: true);
        Add("Media", "Media", MediaBrowserPanel, shown: () => _viewModel.IsMediaBrowserVisible, close: () => _viewModel.ShowMediaBrowser = false, minWidth: () => 200, minHeight: 140);
        Add("Keyframes", "Keyframes", KeyframesPane, shown: () => _viewModel.ShowKeyframesPane, close: () => _viewModel.ShowKeyframesPane = false, minWidth: () => 200, minHeight: 110);
        Add("Effects", "Effects", EffectsPanePanel, shown: () => _viewModel.ShowEffectsPane, close: () => _viewModel.ShowEffectsPane = false, minWidth: () => 240, minHeight: 110);
        Add("Presets", "Preset Bar", PresetBarPane, PaneHandle.Hover, fits: true, shown: () => _viewModel.ShowPresetBarAtTop);

        // The settings tabs: each a pane with a tab, made when first shown (RealizeTabContent), shown while its tab would be.
        // Video and Audio are a box each, their contents moved between it and the Export tab (MainWindow.Export.cs).
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            var title = tab.Header as string ?? "Tab";
            var id = "Tab:" + title;
            _tabPanes.Add((id, tab));
            Func<FrameworkElement> content = id == ExportId ? ExportContent : IsSection(id) ? () => SectionSlot(id) : () => RealizeTabContent(tab);
            _dock.Register(new DockPane
            {
                Id = id, Title = title, Handle = PaneHandle.Tab, Content = content,
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

        _dock.Changed += () => _viewModel.SavePaneLayout(s => s.DockLayout = _dock.Save());
        _dock.SideChanged += () =>
        {
            UpdateSideWindowCheck();
            if (_dock.SideWindow is { } side)
                HookSideWindow(side);
        };
        _dock.PaneShown += OnPaneShown;
        // The layout chosen in the first-run window, the first time; then the arrangement as it was left.
        _dock.Load(TakeFirstRunLayout() ?? AppSettings.Current.DockLayout);
        UpdateSideWindowCheck();

        // What shows or hides a pane.
        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModels.MainViewModel.ShowCommandPreviewTab):
                    UpdateTabVisibility();
                    break;
                case nameof(ViewModels.MainViewModel.ShowAdvancedPlayback) or nameof(ViewModels.MainViewModel.IsArrangeActive)
                    or nameof(ViewModels.MainViewModel.IsMediaBrowserVisible) or nameof(ViewModels.MainViewModel.ShowKeyframesPane) or nameof(ViewModels.MainViewModel.ShowEffectsPane)
                    or nameof(ViewModels.MainViewModel.ShowPresetBarAtTop):
                    _dock.ScheduleRebuild();
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
    /// Where the panes start (a pane switched off is hidden, not moved): the Standard Editing layout (see
    /// MainWindow.Layouts.cs), which Reset Layout puts back.
    /// </summary>
    private DockLayoutState DefaultDockLayout() => StandardLayout();

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
    /// Settings. The flyouts open beside the strip then (PlaceHeaderFlyout).
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

    /// <summary>The border beside the overview bar, Ctrl-dragged: the bar's height setting, four pixels a step.</summary>
    private void ResizeMasterTimeline(double pixels)
    {
        _timelineResizeRest += pixels / 4;
        var steps = Math.Truncate(_timelineResizeRest);
        if (steps == 0)
            return;

        _timelineResizeRest -= steps;
        _viewModel.MasterTimelineHeight = Math.Clamp(_viewModel.MasterTimelineHeight + steps, ViewModels.MainViewModel.CompactMasterTimeline, ViewModels.MainViewModel.TallMasterTimeline);
    }

    /// <summary>A pane was put on screen: the Canvas draws its frame once it has a size.</summary>
    private void OnPaneShown(string id)
    {
        if (id == "Layout")
            Dispatcher.BeginInvoke(RedrawLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Makes what is in a settings tab: from its template (every tab but Summary and Export is left as one until wanted),
    /// or the content it was made with. The named parts this window uses are found in it as it is made (the Audio tab's
    /// options row, which folds into a button when it is narrow).
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
        var from = new[] { state.Main, state.Side, state.SideClosed }.OfType<DockNode>().Any(root => root.AllPanes().Any(IsRetiredPane))
            ? 1 : state.Version;
        state.Version = DockLayoutState.CurrentVersion;
        if (from < 2)
            WithoutRetiredTabs(state);
        if (from < 3)
            PutTimelineFirst(state);
        return state;
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
    private void PutTimelineFirst(DockLayoutState state)
    {
        if (DockManager.SameShape(state, StandardLayoutBeside()) && state.Main.GroupOf(TimelinePaneId) is { } alone
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
    /// heap is compacted (the native side's start: libmpv's and WPF's own allocations), and the pages the start touched
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

    // ----- The side window -----

    /// <summary>The Layout flyout's Side Window row: opens the side window, or closes it (its panes come back).</summary>
    private void SideWindowRow_Click(object sender, RoutedEventArgs e)
    {
        CloseLayoutMenu();
        _dock.SetSideOpen(!_dock.IsSideOpen);
        UpdateSideWindowCheck();
        _viewModel.StatusText = _dock.IsSideOpen
            ? "The side window is open: drag a pane's tab into it (or out of this window), or right-click a tab and choose Move to Side Window."
            : "The side window is closed; its panes are back in this window.";
    }

    /// <summary>The Side Window row of the Layout flyout is ticked while the side window is open.</summary>
    private void UpdateSideWindowCheck() => SideWindowCheck.Visibility = _dock.IsSideOpen ? Visibility.Visible : Visibility.Hidden;

    // The side window answers keys and dropped files as this window does.
    private readonly HashSet<Window> _hookedSides = [];

    private void HookSideWindow(Window side)
    {
        if (!_hookedSides.Add(side))
            return;

        side.PreviewKeyDown += MainWindow_PreviewKeyDown;
        HookTimelineMenuKey(side);
        side.AllowDrop = true;
        side.PreviewDragOver += Window_PreviewDragOver;
        side.PreviewDrop += Window_PreviewDrop;
        side.Closed += (_, _) => _hookedSides.Remove(side);
    }
}
