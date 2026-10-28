using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The panes: arranged freely, in this window and the side window -----
// Every pane (the player, the playback controls, the master timeline, the layout, the header, the side panes, the
// preset bar and each settings tab) is made in PaneLibrary and put where the arrangement says by the dock
// manager (Docking/DockManager.cs). The arrangement is kept in AppSettings.DockLayout.
public partial class MainWindow
{
    private DockManager _dock = null!;

    // The settings tabs, as panes: their names, by the tab they came from.
    private readonly List<(string Id, TabItem Tab)> _tabPanes = [];

    // The master timeline made taller by the border beside it: what a drag has moved but not yet made a step.
    private double _timelineResizeRest;

    /// <summary>Takes the panes out of the library, tells the dock manager what each is, and lays the window out as it was left.</summary>
    private void InitializeDocking()
    {
        // The Source pane (gone since Pass 1.5: Add Media in the header took its job) is where saved arrangements may still
        // have it: the Media pane goes there, and is shown.
        _dock = new DockManager(this, MainDockHost, DefaultDockLayout) { Replaced = new Dictionary<string, string> { ["Source"] = "Media" } };
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
        Add("Timeline", "Master Timeline", TimelinePane, PaneHandle.Hover, fits: true, shown: () => AppSettings.Current.ShowMasterTimeline, minWidth: () => 300, resize: ResizeMasterTimeline);
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
        _dock.ExtendTabMenu = AddTabsAndPanesMenu;

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

    /// <summary>The border beside the master timeline, dragged: the timeline's height setting, four pixels a step.</summary>
    private void ResizeMasterTimeline(double pixels)
    {
        _timelineResizeRest += pixels / 4;
        var steps = Math.Truncate(_timelineResizeRest);
        if (steps == 0)
            return;

        _timelineResizeRest -= steps;
        _viewModel.MasterTimelineHeight = Math.Clamp(_viewModel.MasterTimelineHeight + steps, 1, 50);
    }

    /// <summary>A pane was put on screen: the Canvas draws its frame once it has a size.</summary>
    private void OnPaneShown(string id)
    {
        if (id == "Layout")
            Dispatcher.BeginInvoke(RedrawLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Makes what is in a settings tab: from its template (every tab but Summary is left as one until wanted), or the
    /// content it was made with. The named parts this window uses are found in it as it is made.
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
        else if (content.FindName(nameof(LayerAddBar)) is WrapPanel bar)
        {
            LayerAddBar = bar;
            KeyBarInline = (StackPanel)content.FindName(nameof(KeyBarInline));
            KeyBarButton = (System.Windows.Controls.Primitives.ToggleButton)content.FindName(nameof(KeyBarButton));
            AddLayerHost = (Grid)content.FindName(nameof(AddLayerHost));
            AddLayerPopup = (System.Windows.Controls.Primitives.Popup)content.FindName(nameof(AddLayerPopup));
            AddLayerFlyoutPanel = (Border)content.FindName(nameof(AddLayerFlyoutPanel));
            LayersList = (ListBox)content.FindName(nameof(LayersList));
            _addLayerHover = new HoverFlyout(AddLayerHost, AddLayerPopup, AddLayerFlyoutPanel) { HoldsWhileFocused = true };
        }

        return content;
    }

    /// <summary>
    /// Makes the tabs nobody has opened yet, one at a time and only while nothing else is wanted of this thread, so
    /// that the first click on any of them finds it ready.
    /// </summary>
    private void RealizeTabsWhenIdle()
    {
        if (_tabPanes.FirstOrDefault(t => !_dock.IsRealized(t.Id)) is not { Id: { } next })
        {
            GiveBackStartupMemory();
            return;
        }

        _dock.Realize(next);
        Dispatcher.BeginInvoke(RealizeTabsWhenIdle, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// The start's warm-up is over (every tab made): what it took and let go is given back to the system once, away
    /// from the interface's thread (v4.1.0). A start allocates little after its first frame, so the collector would not
    /// run again for a long while and kept about 40 MB of the start's garbage committed: 10 s after the first frame
    /// HandPeg read 212 to 215 MB where the collection leaves it under 200 (the Media pane did the same by chance in
    /// 4.0, after making its pictures). Done once per start, at ApplicationIdle, as the Media pane's GiveBackMemory.
    /// </summary>
    private void GiveBackStartupMemory()
    {
        if (_startupMemoryGivenBack)
            return;

        _startupMemoryGivenBack = true;
        Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });
    }

    private bool _startupMemoryGivenBack;

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
        side.AllowDrop = true;
        side.PreviewDragOver += Window_PreviewDragOver;
        side.PreviewDrop += Window_PreviewDrop;
        side.Closed += (_, _) => _hookedSides.Remove(side);
    }
}
