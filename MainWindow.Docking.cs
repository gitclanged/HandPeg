using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The panes: arranged freely, in this window and the side window -----
// Every pane (the player, the playback controls, the master timeline, the layout, the header, the side panes, the
// preset bar and each settings tab) is made in PaneLibrary and put where this mode's arrangement says by the dock
// manager (Docking/DockManager.cs). Each mode keeps its own arrangement (AppSettings.DockLayout, per ModeProfile).
public partial class MainWindow
{
    private DockManager _dock = null!;

    // The settings tabs, as panes: their names, by the tab they came from.
    private readonly List<(string Id, TabItem Tab)> _tabPanes = [];

    // The master timeline made taller by the border beside it: what a drag has moved but not yet made a step.
    private double _timelineResizeRest;

    /// <summary>Takes the panes out of the library, tells the dock manager what each is, and lays the window out as this mode had it.</summary>
    private void InitializeDocking()
    {
        _dock = new DockManager(this, MainDockHost, DefaultDockLayout);

        // The panes' contents leave the library: from now on they are wherever the arrangement puts them.
        var contents = PaneLibrary.Children.OfType<FrameworkElement>().ToList();
        foreach (var content in contents.Where(c => !ReferenceEquals(c, SettingsTabs)))
            PaneLibrary.Children.Remove(content);

        void Add(string id, string title, FrameworkElement content, PaneHandle handle = PaneHandle.TitleSlot, bool fits = false,
            Func<bool>? shown = null, Action? close = null, Func<double>? minWidth = null, double minHeight = 60, Panel? hoverHost = null, Action<double>? resize = null) =>
            _dock.Register(new DockPane
            {
                Id = id, Title = title, Content = () => content, Handle = handle, FitsContent = fits, IsShown = shown ?? (() => true), Close = close,
                MinWidth = minWidth ?? (() => 160), MinHeight = minHeight, HoverHost = hoverHost, ResizeBy = resize,
            });

        Add("Player", "Player", PlayerPane, PaneHandle.Hover, minWidth: () => 320, minHeight: 180, hoverHost: PlayerOverlay);
        Add("Playback", "Playback Controls", TransportRow, PaneHandle.Hover, fits: true, shown: () => _viewModel.ShowAdvancedPlayback, minWidth: () => 300);
        Add("Timeline", "Master Timeline", TimelinePane, PaneHandle.Hover, fits: true, minWidth: () => 300, resize: ResizeMasterTimeline);
        Add("Layout", "Layout", LayoutPane, shown: () => _viewModel.IsArrangeActive, close: () => _viewModel.IsArrangeActive = false, minWidth: () => 200);
        Add("Header", "Header", EditorHeader, PaneHandle.Hover, fits: true, shown: () => _viewModel.IsEditorMode, minWidth: HeaderWidth);
        Add("Source", "Source", SourcePane, fits: true, shown: () => _viewModel.IsSourcePaneVisible, close: () => _viewModel.ShowSourcePane = false, minWidth: () => 200);
        Add("Media", "Media", MediaBrowserPanel, shown: () => _viewModel.IsMediaBrowserVisible, close: () => _viewModel.ShowMediaBrowser = false, minWidth: () => 200, minHeight: 140);
        Add("Keyframes", "Keyframes", KeyframesPane, shown: () => _viewModel.ShowKeyframesPane, close: () => _viewModel.ShowKeyframesPane = false, minWidth: () => 200, minHeight: 110);
        Add("CutSegments", "Cut Segments", CutSegmentsPane, shown: () => _viewModel.ShowCutSegmentsPane, close: () => _viewModel.ShowCutSegmentsPane = false, minWidth: () => 200, minHeight: 70);
        Add("Presets", "Preset Bar", PresetBarPane, PaneHandle.Hover, fits: true, shown: () => _viewModel.ShowPresetBarAtTop);

        // The settings tabs: each a pane with a tab, made when first shown (RealizeTabContent), shown while its tab would be.
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            var title = tab.Header as string ?? "Tab";
            var id = "Tab:" + title;
            _tabPanes.Add((id, tab));
            _dock.Register(new DockPane
            {
                Id = id, Title = title, Handle = PaneHandle.Tab, Content = () => RealizeTabContent(tab),
                IsShown = () => tab.Visibility == Visibility.Visible, MinWidth = () => 300, MinHeight = 140,
            });
            DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(TabItem)).AddValueChanged(tab, (_, _) => _dock.ScheduleRebuild());
        }

        _dock.Changed += () => _viewModel.SavePaneLayout(s => s.DockLayout = _dock.Save());
        _dock.SideChanged += () =>
        {
            (SidecarButton.IsChecked, SidecarButtonTop.IsChecked) = (_dock.IsSideOpen, _dock.IsSideOpen);
            if (_dock.SideWindow is { } side)
                HookSideWindow(side);
        };
        _dock.PaneShown += OnPaneShown;
        _dock.Load(AppSettings.Current.DockLayout);
        (SidecarButton.IsChecked, SidecarButtonTop.IsChecked) = (_dock.IsSideOpen, _dock.IsSideOpen);

        // What shows or hides a pane.
        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModels.MainViewModel.ShowAdvancedPlayback) or nameof(ViewModels.MainViewModel.IsArrangeActive) or nameof(ViewModels.MainViewModel.IsSourcePaneVisible)
                    or nameof(ViewModels.MainViewModel.IsMediaBrowserVisible) or nameof(ViewModels.MainViewModel.ShowKeyframesPane) or nameof(ViewModels.MainViewModel.ShowCutSegmentsPane)
                    or nameof(ViewModels.MainViewModel.ShowPresetBarAtTop):
                    _dock.ScheduleRebuild();
                    break;
            }
        };
    }

    /// <summary>
    /// Where the panes start, in both modes (a pane a mode does not have is hidden, not moved): the player with the
    /// layout pane beside it and the playback controls under both; the header, the source, the media browser, the
    /// keyframes and the cut segments in a column on the right; the master timeline, the preset bar and the
    /// settings tabs under all of it.
    /// </summary>
    private DockLayoutState DefaultDockLayout()
    {
        var player = DockNode.Split(true,
            (DockNode.Split(false, (DockNode.Group("Player"), 62), (DockNode.Group("Layout"), 38)), 1),
            (DockNode.Group("Playback"), 1));
        var side = DockNode.Split(true,
            (DockNode.Group("Header"), 1), (DockNode.Group("Source"), 1), (DockNode.Group("Media"), 1.2), (DockNode.Group("Keyframes"), 1), (DockNode.Group("CutSegments"), 1));
        var top = DockNode.Split(false, (player, 900), (side, 400));
        var tabs = DockNode.Group([.. _tabPanes.Select(t => t.Id)]);
        return new DockLayoutState
        {
            Main = DockNode.Split(true, (top, 46), (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (tabs, 38)),
        };
    }

    /// <summary>How wide the header's buttons are together: its pane is never narrower, so none of them is cut off.</summary>
    private double HeaderWidth()
    {
        EditorHeader.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(Math.Ceiling(EditorHeader.DesiredSize.Width), 170);
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

    /// <summary>A pane was put on screen: the layout pane draws its frame once it has a size.</summary>
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
            AddLayerButton = (System.Windows.Controls.Primitives.ToggleButton)content.FindName(nameof(AddLayerButton));
            LayersList = (ListBox)content.FindName(nameof(LayersList));
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
            return;

        _dock.Realize(next);
        Dispatcher.BeginInvoke(RealizeTabsWhenIdle, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // ----- The side window -----

    private void Sidecar_Click(object sender, RoutedEventArgs e)
    {
        _dock.SetSideOpen(!_dock.IsSideOpen);
        (SidecarButton.IsChecked, SidecarButtonTop.IsChecked) = (_dock.IsSideOpen, _dock.IsSideOpen);
        _viewModel.StatusText = _dock.IsSideOpen
            ? "The side window is open: drag a pane's tab into it (or out of this window), or right-click a tab and choose Move to Side Window."
            : "The side window is closed; its panes are back in this window.";
    }

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
