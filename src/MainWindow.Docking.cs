using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HandPegApp.Docking;
using HandPegApp.Services;
using HandPegApp.Views;

namespace HandPegApp;

// ----- Docked panes (main window and side windows) -----
// Every pane (player, playback controls, timeline, Scrubber, Canvas, header, side panes, Preset Bar, settings tabs) is created in
// PaneLibrary and placed by DockManager (Docking/DockManager.cs). The arrangement persists in AppSettings.DockLayout; side windows
// (any number, each a Live Video Preview or Pane Docker) in AppSettings.SideWindows.
public partial class MainWindow
{
    private DockManager _dock = null!;

    // Settings tab pane names, keyed by source tab.
    private readonly List<(string Id, TabItem Tab)> _tabPanes = [];

    // Scrubber resize via its border: sub-step drag remainder not yet applied.
    private double _timelineResizeRest;

    /// <summary>
    /// Pane id of the unified timeline (Views/UnifiedTimelinePane.xaml). Not "Timeline", which is the Scrubber's legacy id, kept so
    /// saved arrangements still resolve.
    /// </summary>
    private const string TimelinePaneId = "UnifiedTimeline";

    /// <summary>
    /// Legacy pane id remaps applied to every tree on load (DockManager.Replaced): Source to Media, plus the hidden-tab renames in
    /// <see cref="SettingsMigrations.PaneRenames"/> (e.g. Filters to Color). Shared with goldens.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ReplacedPanes = new Dictionary<string, string>(SettingsMigrations.PaneRenames)
    {
        ["Source"] = "Media",
    };

    /// <summary>Moves panes out of PaneLibrary, registers them with DockManager, and performs the single startup layout.</summary>
    private void InitializeDocking()
    {
        // Legacy Source pane slots host the Media pane (shown). Older arrangements are migrated by UpgradeLayout (timeline into the
        // retired Layers slot, then first among the settings tabs).
        _dock = new DockManager(this, MainDockHost, DefaultDockLayout)
        {
            Replaced = ReplacedPanes,
            Upgrade = UpgradeLayout,

            // Tab-menu Reset Panes Layout = Views menu's: Default layout plus default Scrubber height.
            ResetLayout = () => ResetPanesLayout_Click(this, new RoutedEventArgs()),
        };
        _dock.PaneReplaced += (_, replacement) =>
        {
            if (replacement == "Media")
                _viewModel.ShowMediaBrowser = true;
        };

        // Detach contents from PaneLibrary; DockManager owns placement from here.
        var contents = PaneLibrary.Children.OfType<FrameworkElement>().ToList();
        foreach (var content in contents.Where(c => !ReferenceEquals(c, SettingsTabs)))
            PaneLibrary.Children.Remove(content);

        // Pane minimum widths (splitter range): 180 unless overridden below (DockPane.MinWidth default), so borders can move at 1280 DIPs;
        // sums pin columns (DockManager.MinWidthOf).
        void Add(string id, string title, FrameworkElement content, PaneHandle handle = PaneHandle.TitleSlot, bool fits = false,
            Func<bool>? shown = null, Action? close = null, Func<double>? minWidth = null, double minHeight = 60, Panel? hoverHost = null, Action<double>? resize = null,
            Action<PaneShape>? reshape = null, bool joinsBelow = false, bool floats = false, Action? resetSize = null, bool headerlessAlone = false) =>
            _dock.Register(new DockPane
            {
                Id = id, Title = title, Content = () => content, Handle = handle, FitsContent = fits, IsShown = shown ?? (() => true), Close = close,
                MinWidth = minWidth ?? (() => 180), MinHeight = minHeight, HoverHost = hoverHost, ResizeBy = resize, ResetSize = resetSize, Reshape = reshape,
                JoinsPaneBelow = joinsBelow, Floats = floats, HeaderlessAlone = headerlessAlone,
            });

        // Player floats over its slot (DockPane.Floats) so relayouts never blink the video; min 320 (matches XAML) for the transport overlay
        // and a 16:9 picture.
        Add("Player", "Player", PlayerPane, PaneHandle.Hover, minWidth: () => 320, minHeight: 180, hoverHost: PlayerOverlay, floats: true);
        // Playback controls and Scrubber: min 300 (content-height rows sharing a column with the player or timeline in built-in layouts).
        Add("Playback", "Playback Controls", TransportRow, PaneHandle.Hover, fits: true, shown: () => _viewModel.ShowAdvancedPlayback, minWidth: () => 300);
        // Sequence Overview Scrubber: whole-sequence overview with segments and markers, click/drag to scrub. Legacy id "Timeline" and
        // setting ShowMasterTimeline are kept for compatibility. Double-clicking its lower border (or grip) resets its height.
        Add("Timeline", ScrubberTitle, TimelinePane, PaneHandle.Hover, fits: true, shown: () => AppSettings.Current.ShowMasterTimeline, minWidth: () => 300, resize: ResizeMasterTimeline,
            resetSize: OverviewGrip_ResetRequested);
        // Timeline: always shown. Shared group: normal tab row; alone (Standard Editing bottom): headerless, tab revealed by hovering the
        // top-left corner (DockPane.HeaderlessAlone). Min 300 for track headers plus some ruler.
        Add(TimelinePaneId, "Timeline", UnifiedTimelinePanel, minWidth: () => 300, minHeight: 140, headerlessAlone: true);
        // Canvas: output frame for arranging layers; legacy id "Layout" kept for saved arrangements. Min 180.
        Add("Layout", "Canvas", LayoutPane, shown: () => _viewModel.IsArrangeActive, close: () => _viewModel.IsArrangeActive = false);
        Add("Header", "Header", EditorHeader, PaneHandle.Hover, fits: true, minWidth: HeaderWidth, reshape: ApplyHeaderShape, joinsBelow: true);
        // Media Browser: min 180, or 280 in Filmstrip view (list + preview, MediaMinWidth), updated live (DockManager.RefreshMinSizes).
        Add("Media", "Media", MediaBrowserPanel, shown: () => _viewModel.IsMediaBrowserVisible, close: () => _viewModel.ShowMediaBrowser = false, minWidth: MediaMinWidth, minHeight: 140);
        MediaBrowser.ViewModeChanged += _ => _dock.RefreshMinSizes();
        // Keyframes and Effects: min 180; list names trim instead of pinning the column.
        Add("Keyframes", "Keyframes", KeyframesPane, shown: () => _viewModel.ShowKeyframesPane, close: () => _viewModel.ShowKeyframesPane = false, minHeight: 110);
        Add("Effects", "Effects", EffectsPanePanel, shown: () => _viewModel.ShowEffectsPane, close: () => _viewModel.ShowEffectsPane = false, minHeight: 110);
        // Master Meter: vertical Sequence meter and master fader; hidden by default, tabbed beside Effects/Keyframes (StandardLayout, also
        // the fallback for arrangements without it), shown via Tabs and Panes. Min 104.
        Add("Meter", "Master Meter", MasterMeterPanel, shown: () => _viewModel.ShowMasterMeter, close: () => _viewModel.ShowMasterMeter = false, minWidth: () => 104, minHeight: 180);
        // Preset Bar: min 180.
        Add("Presets", "Preset Bar", PresetBarPane, PaneHandle.Hover, fits: true, shown: () => _viewModel.ShowPresetBarAtTop);
        // Studios: created by factories on first show, never in PaneLibrary (MainWindow.Studios.cs).
        RegisterStudioPanes();

        // The Effects pane's Color Grade card opens the Color tab (MainWindow.ColorStudio.cs).
        EffectsView.OpenColorStudioRequested += OpenColorStudio;

        // Settings tabs: one pane each, realized on first show (RealizeTabContent). Audio is a host whose content moves between it and
        // Export (MainWindow.Export.cs). A saved Tab:Video (retired) is dropped by DockManager validation.
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            var title = tab.Header as string ?? "Tab";
            var id = "Tab:" + title;
            _tabPanes.Add((id, tab));
            Func<FrameworkElement> content = id == ExportId ? ExportContent : IsSection(id) ? () => SectionSlot(id) : () => RealizeTabContent(tab);
            _dock.Register(new DockPane
            {
                Id = id, Title = title, Handle = PaneHandle.Tab, Content = content,
                // Min 300: Export cards (ExportCardMinWidth) and other tabs' setting rows.
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
        // First run uses the first-run layout choice; afterwards the Default layout (StartupLayoutJson keeps the saved arrangement while it
        // matches), always opening on the Timeline tab. Saved side windows reopen after Frame 1 (DockManager.OpenSidesWhenShown). This is
        // the window's single pre-Frame 1 dock build (Law 1).
        _dock.Load(sideWindows: AppSettings.Current.SideWindows, saved: TakeFirstRunLayout() ?? StartupLayoutJson(), showFirst: TimelinePaneId);
        UpdateSideWindowCheck();

        // Pane visibility bindings.
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

                // Enabling the Canvas selects its tab once docked (it is a non-selected tab beside Effects in Standard Editing).
                case nameof(ViewModels.MainViewModel.IsArrangeActive):
                    _dock.ScheduleRebuild();
                    if (_viewModel.IsArrangeActive)
                        _dock.BringForward("Layout");
                    break;
            }
        };
    }

    /// <summary>
    /// Applies setting-hidden tabs (Command Preview, Tabs and Panes choices) and Export section/destination placement
    /// (MainWindow.Export.cs) before the first layout. Not XAML-bound: bindings attach after load, so Frame 1 would show a tab that
    /// Frame 2 removes.
    /// </summary>
    private void UpdateTabVisibility()
    {
        foreach (var (id, tab) in _tabPanes)
            tab.Visibility = TabWanted(id) ? Visibility.Visible : Visibility.Collapsed;
        PlaceSections();
        PlaceDestinationBar();
    }

    /// <summary>
    /// Baseline arrangement (Standard Editing, MainWindow.Layouts.cs): fallback for unreadable arrangements and home for missing panes
    /// (disabled panes are hidden, not moved). Reset Layout uses DefaultLayoutChoice, which equals this unless another is starred.
    /// </summary>
    private DockLayoutState DefaultDockLayout() => StandardLayout();

    /// <summary>Media pane min width: 280 in Filmstrip view (list + preview/cutter), else 180.</summary>
    private double MediaMinWidth() => MediaBrowser.ViewMode == Views.MediaBrowserView.Filmstrip ? 280 : 180;

    /// <summary>Combined header button width; the header pane's minimum so no button is clipped.</summary>
    private double HeaderWidth()
    {
        EditorHeader.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(Math.Ceiling(EditorHeader.DesiredSize.Width), _headerShape == PaneShape.Column ? 0 : 140);
    }

    // Last header slot shape from DockManager, and the buttons' original padding (restored for shapes that use it).
    private PaneShape _headerShape;
    private Dictionary<Control, Thickness>? _headerPadding;

    /// <summary>
    /// Adapts the header to its slot (called by DockManager before each layout and on slot shape changes): Row (right-aligned buttons
    /// at the top, never stretched), Joined (centred at the right end of the pane below's tab row, one combined bar), or Column (44 px
    /// strip of 32x32 icons: Add Media, Layout, Project, Settings) for tall or narrow slots; flyouts then open beside the strip
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

        // Column mode: reserve the 7 px hover strip at the top plus small side margins.
        EditorHeader.Margin = column ? new Thickness(6, 9, 6, 0) : shape == PaneShape.Joined ? new Thickness(0) : new Thickness(0, 0, 0, 2);
        foreach (var part in EditorHeader.Children.OfType<FrameworkElement>())
            part.Margin = column ? new Thickness(0, 0, 0, 6) : new Thickness(6, 0, 0, shape == PaneShape.Joined ? 0 : 6);
        foreach (var button in buttons)
        {
            (button.Width, button.Height) = column ? (32, 32) : (double.NaN, double.NaN);
            button.Padding = column ? new Thickness(0) : _headerPadding[button];
        }
    }

    /// <summary>Display name of the Scrubber pane (id remains "Timeline").</summary>
    internal const string ScrubberTitle = "Sequence Overview Scrubber";

    // Scrubber drag range 16..120 px. MainViewModel.MasterTimelineHeight steps are 12 + 4n px (MainViewModel.TimelineAreaHeight), so
    // valid steps are 1 (16 px) to 27 (120 px).
    private const double ScrubberLeastPixels = 16, ScrubberMostPixels = 120, ScrubberPixelsPerStep = 4, ScrubberBasePixels = 12;

    /// <summary>Clamps a Scrubber height setting to the draggable step range.</summary>
    private static double ScrubberStep(double step) => Math.Clamp(step,
        Math.Max(1, Math.Ceiling((ScrubberLeastPixels - ScrubberBasePixels) / ScrubberPixelsPerStep)),
        Math.Floor((ScrubberMostPixels - ScrubberBasePixels) / ScrubberPixelsPerStep));

    // Scrubber height setting at the start of an OverviewGrip drag.
    private double _overviewHeightAtDrag;

    private void OverviewGrip_DragStarted() => _overviewHeightAtDrag = _viewModel.MasterTimelineHeight;

    /// <summary>
    /// OverviewGrip (EdgeGrip) drag by <paramref name="pixels"/>: sets the height in 4 px steps so the edge tracks the pointer, clamped to
    /// 16..120 px (ScrubberStep).
    /// </summary>
    private void OverviewGrip_Dragged(double pixels) =>
        _viewModel.MasterTimelineHeight = ScrubberStep(_overviewHeightAtDrag + Math.Round(pixels / ScrubberPixelsPerStep));

    private void OverviewGrip_ResetRequested() => _viewModel.MasterTimelineHeight = AppSettings.DefaultMasterTimelineHeight;

    /// <summary>
    /// Dock border drag resizing the Scrubber (border below, or above with Ctrl / when nothing above shares space: DockManager.AddBorder),
    /// given the incremental pointer delta: 4 px steps, clamped to 16..120 px.
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

    /// <summary>Pane shown: the Canvas draws its frame once it has a size.</summary>
    private void OnPaneShown(string id)
    {
        if (id == "Layout")
            Dispatcher.BeginInvoke(RedrawLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Realizes a settings tab from its template (all tabs, Export included, stay templates until needed) or its initial content, and
    /// resolves the named parts this window uses (Audio options row that collapses into a button when narrow; Export parts).
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

        // Audio tab master meter and fader: wired like the Master Meter pane.
        if (content.FindName("AudioTabMeter") is AudioMeterControl tabMeter)
            WireMeter(tabMeter);

        // Color tab's Color Studio: wired on creation (builds its parts on first show).
        if ((content as ColorStudioPane ?? content.FindName("ColorStudio")) is ColorStudioPane studio)
            AttachColorStudio(studio);

        // Export parts (MainWindow.Export.cs); ExportContent fills them once resolved. ExportPanel is the template root.
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
    /// Idle pre-realization of unseen panes, one per ApplicationIdle slot, so first clicks are instant: the timeline (if it is a
    /// non-selected tab), then the settings tabs; when none remain, requests the start-up memory compaction.
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

    // ----- Legacy arrangement migration -----

    // Retired settings tabs, in priority order for hosting the timeline (Layers' track list and Subtitles' captions moved onto the
    // timeline; the Audio tab remains for audio settings).
    private static readonly string[] RetiredPanes = ["Tab:Layers", "Tab:Subtitles"];

    // Timeline share when inserted beside a group; the tabs keep the remainder to its right.
    private const double TimelineShare = 0.7;

    private static bool IsRetiredPane(string id) => Array.IndexOf(RetiredPanes, id) >= 0;

    /// <summary>
    /// Migrates an arrangement to the current version step by step (DockManager.Upgrade). Every applied arrangement passes through before
    /// Load's single dock build, so steps must be cheap, idempotent on current input, and side-effect free (nothing saved, built or laid
    /// out; re-runs each start until the next save). Unversioned legacy arrangements are detected by their retired tabs.
    /// </summary>
    private DockLayoutState UpgradeLayout(DockLayoutState state)
    {
        UpgradeLayout(state, TabIds);
        return state;
    }

    /// <summary>Window-free migration steps of <see cref="UpgradeLayout(DockLayoutState)"/> for the given settings tab ids (also used by goldens).</summary>
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
        if (from < 5)
            MoveExportBesideMedia(state, tabIds);
    }

    /// <summary>
    /// v4 to v5: only the exact v4 Standard Editing shape (with or without the Master Meter, which PlaceAsDefault adds later) moves the
    /// settings tabs beside Media (Media stays selected) and leaves the timeline alone at the bottom (headerless, DockPane.HeaderlessAlone).
    /// Custom arrangements and split shares are untouched.
    /// </summary>
    private static void MoveExportBesideMedia(DockLayoutState state, IReadOnlyList<string> tabIds)
    {
        // Match on shape excluding settings tabs and Meter: older arrangements may still hold the retired Summary tab (dropped later by
        // DockManager.Validate) or lack newer tabs; neither makes them custom.
        if (ShapeWithoutTabs(state.Main) != ShapeWithoutTabs(StandardLayoutV4(tabIds).Main)
            || state.Main.GroupOf(TimelinePaneId) is not { } bottom || state.Main.GroupOf("Media") is not { } media || ReferenceEquals(bottom, media)
            || !bottom.Panes.Any(p => p.StartsWith("Tab:", StringComparison.Ordinal)))
            return;

        var tabs = bottom.Panes.Where(p => p != TimelinePaneId).ToList();
        bottom.Panes.RemoveAll(p => p != TimelinePaneId);
        bottom.Active = TimelinePaneId;
        media.Panes.AddRange(tabs.Where(p => !media.Panes.Contains(p)));
        media.Active = "Media";

        // Untouched v4 default top-row shares become today's, giving the Media column (also hosting Export, min 300 DIPs) border room at
        // 1280 DIPs.
        if (state.Main is { IsGroup: false, Children: [{ IsGroup: false, Vertical: false, Sizes: [56.0, 22.0, 22.0] } top, ..] })
            top.Sizes = [.. StandardTopShares];
    }

    // DockManager.Shape excluding settings tabs and the Master Meter.
    private static string ShapeWithoutTabs(DockNode root)
    {
        var copy = new DockLayoutState { Main = root }.Copy().Main;
        foreach (var group in copy.AllGroups())
            group.Panes.RemoveAll(p => p.StartsWith("Tab:", StringComparison.Ordinal) || p == "Meter");
        return DockManager.Shape(copy);
    }

    /// <summary>
    /// v1 to v2: removes the retired Layers/Subtitles tabs and inserts the timeline to the left of their group (most of its space), or as
    /// that group if it held only retired tabs, so legacy built-ins map to their successors ("In use" still matches). Placed in the
    /// window that held Layers (main, or an open side window); a closed side arrangement gets it only if no shown window does, so it never
    /// exists in two trees. Arrangements already containing the timeline only lose the retired tabs.
    /// </summary>
    private static void WithoutRetiredTabs(DockLayoutState state)
    {
        // Target window: the one that held Layers (else Subtitles), unless a shown window already has the timeline.
        var side = state.SideOpen ? state.Side : null;
        DockNode? WindowWith(string id) => state.Main.GroupOf(id) is not null ? state.Main : side?.GroupOf(id) is not null ? side : null;
        var shown = state.Main.GroupOf(TimelinePaneId) is not null || side?.GroupOf(TimelinePaneId) is not null;
        var home = shown ? null : RetiredPanes.Select(WindowWith).FirstOrDefault(window => window is not null);

        state.Main = WithoutRetiredPanes(state.Main, ReferenceEquals(home, state.Main)) ?? DockNode.Group();
        state.Side = WithoutRetiredPanes(state.Side, home is not null && ReferenceEquals(home, state.Side));

        // Closed side arrangement gets it only if no shown window does: reopening steals its panes from the main window, which would
        // move the timeline away from the Layers slot.
        state.SideClosed = WithoutRetiredPanes(state.SideClosed, placeTimeline: !shown && home is null);
    }

    /// <summary>
    /// v2 to v3: the exact v2 Standard Editing shape (timeline group beside the settings group) merges into one group with the timeline
    /// first and selected, taking the pair's slot and share; elsewhere, a group with timeline and settings tabs just moves the timeline to
    /// first (selection unchanged). Nothing else moves.
    /// </summary>
    private static void PutTimelineFirst(DockLayoutState state, IReadOnlyList<string> tabIds)
    {
        if (DockManager.SameShape(state, StandardLayoutBeside(tabIds)) && state.Main.GroupOf(TimelinePaneId) is { } alone
            && state.Main.ParentOf(alone) is { IsGroup: false, Vertical: false, Children.Count: 2 } beside
            && ReferenceEquals(beside.Children[0], alone) && beside.Children[1] is { IsGroup: true } tabs
            && state.Main.ParentOf(beside) is { } parent)
        {
            // The merged group inherits the pair's split index (and thus its Sizes share).
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
    /// v3 to v4: only the exact v3 Standard Editing shape (Canvas above Effects/Keyframes/Master Meter, Meter optional since PlaceAsDefault
    /// adds it later) folds the Canvas into that group after Keyframes; the group takes the column's slot and share, selection unchanged.
    /// Custom arrangements are untouched.
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

        // Same split index, so the group inherits the column's Sizes share.
        parent.Children[parent.Children.IndexOf(column)] = effects;
    }

    /// <summary>
    /// Removes retired tabs from one tree (groups showing one select their first remaining pane). If this tree receives the timeline,
    /// inserts it beside the first retired tab's group, or replaces that group if it held nothing else. Returns the root, or null if empty.
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

    // ----- Side windows (any number; Live Video Preview or Pane Docker) -----

    /// <summary>
    /// Layout Selector footer "+ New Side Window": opens a Live Video Preview (switchable to Pane Docker from its title bar). Reverts any
    /// preview and closes the selector first, since the popup's async Closed would run after the new window's Changed saved the arrangement.
    /// </summary>
    private void SideWindowRow_Click(object sender, RoutedEventArgs e)
    {
        RestoreLayoutBeforePreview();
        _layoutHover.Close();
        _dock.OpenSideWindow(SideWindowMode.LiveVideoPreview);
        _viewModel.StatusText = "A new side window shows the player live: put it on another monitor (F11 or a double-click: full screen). Its title bar makes it a Pane Docker, or drag any pane's tab out of the window.";
    }

    /// <summary>Persists the arrangement and side windows to settings on every dock change and at close.</summary>
    private void SaveDockInto(AppSettings settings)
    {
        settings.DockLayout = _dock.Save();
        settings.SideWindows = _dock.SaveSideWindows();
    }

    /// <summary>
    /// Builds the selector footer rows for open side windows (name activates it; close button). Rebuilt on selector open
    /// (OpeningLayoutSelector) and whenever a side window opens, closes or changes mode.
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
                Content = Glyphs.Cancel, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 10, Padding = new Thickness(8, 4, 8, 4),
                ToolTip = $"Closes side window {number} (a Pane Docker's panes go back to this window).", Margin = new Thickness(2, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(close, $"Close Side Window {number}");

            // Revert any preview first: closing a Pane Docker merges its panes into the (saved) arrangement. The row, and with it the focused
            // button, is removed, so after a pointer click focus is released as with a card pill.
            close.Click += (_, _) =>
            {
                RestoreLayoutBeforePreview();
                _dock.CloseSideWindow(id);
                if (!FromKeyboard)
                    LeaveSelectorToPointer();
            };

            // Below the footer buttons with 8 DIP spacing (card row spacing), then tightly packed.
            var line = new DockPanel { Margin = new Thickness(0, SideWindowRows.Children.Count == 0 ? 8 : 2, 0, 0) };
            DockPanel.SetDock(close, Dock.Right);
            line.Children.Add(close);
            line.Children.Add(row);
            SideWindowRows.Children.Add(line);
        }
    }

    // Side windows route keys and file drops like the main window.
    private readonly HashSet<Window> _hookedSides = [];
    private bool _mirrorNotesHooked;

    /// <summary>Routes an overlay window's (Fullscreen Preview) keys and mouse buttons to the editor and 3D view handlers.</summary>
    private void HookSideKeys(Window side)
    {
        side.PreviewKeyDown += MainWindow_PreviewKeyDown;
        side.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ExplodedWindow_PreviewKeyDown));
        _hookedSides.Add(side);
    }

    /// <summary>Removes <see cref="HookSideKeys"/> handlers (window closing).</summary>
    private void UnhookSideKeys(Window side)
    {
        side.PreviewKeyDown -= MainWindow_PreviewKeyDown;
        side.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ExplodedWindow_PreviewKeyDown));
        _hookedSides.Remove(side);
    }

    private void HookSideWindow(SideWindow side)
    {
        if (!_hookedSides.Add(side))
            return;

        side.PreviewKeyDown += MainWindow_PreviewKeyDown;
        HookTimelineMenuKey(side);

        // Horizontal wheel for a Timeline docked here: each window receives its own WM_MOUSEHWHEEL; handled only while over the Timeline.
        void HookSidewaysWheel() =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(side).Handle)?.AddHook(TimelineSidewaysWheel);
        if (new System.Windows.Interop.WindowInteropHelper(side).Handle != IntPtr.Zero)
            HookSidewaysWheel();
        else
            side.SourceInitialized += (_, _) => HookSidewaysWheel();
        side.AllowDrop = true;
        side.PreviewDragOver += Window_PreviewDragOver;
        side.PreviewDrop += Window_PreviewDrop;

        // Live Video Preview surface created/destroyed: attach/detach its mirror (MainWindow.GpuPreview.cs).
        side.PreviewSurfaceChanged += _ => SyncMirrors();
        side.Closed += (_, _) =>
        {
            _hookedSides.Remove(side);
            SyncMirrors();
        };

        // Player hidden/shown: previews indicate whether they show its last frame.
        if (!_mirrorNotesHooked)
        {
            _mirrorNotesHooked = true;
            VideoView.IsVisibleChanged += (_, _) => UpdateMirrorNotes();
            VideoView.SizeChanged += (_, _) => UpdateMirrorNotes();
        }
    }
}
