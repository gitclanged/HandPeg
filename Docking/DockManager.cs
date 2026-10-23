using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp.Docking;

/// <summary>
/// Arranges the panes of the main window and of the side window, as a tree of groups and splits (<see cref="DockNode"/>)
/// for each, and lets them be rearranged by dragging their tabs: onto a part (into its group, as a tab) or to one side
/// of it (a new part beside it), to a window's edge, or out of the windows (into the side window).
///
/// A pane's content is made once and kept; laying the windows out again (after a move, a pane shown or hidden, a mode
/// change) takes every content out of where it was and puts it where the tree says. A hidden pane keeps its place in
/// the tree and is simply not shown. A pane's tab is where its title was: in its <see cref="PaneTitleSlot"/>, over its
/// top-left corner when the pointer comes there (a pane with no title), or in a row of tabs (the settings tabs).
/// </summary>
public sealed class DockManager
{
    private const double DragStart = 6;

    private readonly Window _main;
    private readonly ContentControl _mainHost;
    private readonly Func<DockLayoutState> _defaults;
    private readonly Dictionary<string, DockPane> _panes = [];
    private readonly Dictionary<string, FrameworkElement> _made = [];
    private readonly List<(FrameworkElement Host, DockNode Group, bool Side)> _groupHosts = [];
    private readonly List<(FrameworkElement Host, bool Side)> _roots = [];
    private DockLayoutState _state = new();
    private SideWindow? _side;
    private DockGuide? _guide;
    private bool _rebuildQueued;
    private bool _shuttingDown;
    private bool _openAtPointer;

    // A press on a tab, and the drag it may become.
    private (string Pane, FrameworkElement Tab, Point At)? _press;
    private bool _dragging;
    private (bool Side, DockNode? Group, DockZone Zone, bool Outside)? _target;

    public DockManager(Window main, ContentControl mainHost, Func<DockLayoutState> defaults)
    {
        (_main, _mainHost, _defaults) = (main, mainHost, defaults);
        _main.PreviewKeyDown += (_, e) => CancelDragOnEscape(e);
    }

    /// <summary>Raised when the arrangement has changed, to be saved.</summary>
    public event Action? Changed;

    /// <summary>Raised when the side window has opened or closed.</summary>
    public event Action? SideChanged;

    /// <summary>Raised when a pane has been put on screen (made, the first time), by its name.</summary>
    public event Action<string>? PaneShown;

    /// <summary>The side window, while it is open; for the main window to hand it keys and drops.</summary>
    public Window? SideWindow => _side;

    public bool IsSideOpen => _state.SideOpen;

    public void Register(DockPane pane) => _panes[pane.Id] = pane;

    public DockPane? Pane(string id) => _panes.GetValueOrDefault(id);

    /// <summary>Makes a pane's content now (if it was not made before), without showing it.</summary>
    public FrameworkElement Realize(string id)
    {
        if (!_made.TryGetValue(id, out var content))
        {
            content = _panes[id].Content();
            _made[id] = content;

            // Its data is always the window's, held by the pane itself: moved, it is out of every window for a moment,
            // and anything it would have inherited would go, and a box bound to it would write its empty value back.
            if (content.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
                content.SetBinding(FrameworkElement.DataContextProperty, new System.Windows.Data.Binding(nameof(FrameworkElement.DataContext)) { Source = _main });
        }

        return content;
    }

    public bool IsRealized(string id) => _made.ContainsKey(id);

    // ----- The arrangement as saved -----

    /// <summary>Takes up a saved arrangement (null or unreadable: the mode's own), puts in what it lacks, and lays the windows out.</summary>
    public void Load(string? saved)
    {
        DockLayoutState? state = null;
        try
        {
            state = saved is { Length: > 0 } ? JsonSerializer.Deserialize<DockLayoutState>(saved) : null;
        }
        catch (JsonException)
        {
        }

        _state = Validate(state ?? _defaults());
        ApplySideWindow();
        Rebuild();
    }

    public string Save()
    {
        RememberSideBounds();
        return JsonSerializer.Serialize(_state);
    }

    /// <summary>Every pane back where the mode starts with it, and the side window closed.</summary>
    public void Reset()
    {
        _state = Validate(_defaults());
        ApplySideWindow();
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>
    /// A tree as it may be used: every pane known here in it exactly once (in the main window or the side window),
    /// panes no longer known taken out, and panes not in it yet put where the mode starts with them.
    /// </summary>
    private DockLayoutState Validate(DockLayoutState state)
    {
        var seen = new HashSet<string>();
        DockNode? Clean(DockNode? node)
        {
            if (node is null)
                return null;
            foreach (var group in node.AllGroups())
            {
                group.Panes = group.Panes.Where(p => _panes.ContainsKey(p) && seen.Add(p)).ToList();
                if (group.Active is { } active && !group.Panes.Contains(active))
                    group.Active = group.Panes.FirstOrDefault();
            }

            return DockTree.Tidy(node);
        }

        state.Main = Clean(state.Main) ?? DockNode.Group();
        state.Side = state.SideOpen ? Clean(state.Side) : null;

        foreach (var missing in _panes.Keys.Where(p => !seen.Contains(p)).ToList())
            state.Main = PlaceAsDefault(state.Main, missing);
        return state;
    }

    /// <summary>Puts a pane into the main window where the mode's own arrangement has it: with a pane it is grouped with there, or beside one it neighbours.</summary>
    private DockNode PlaceAsDefault(DockNode main, string pane)
    {
        var defaults = _defaults().Main;
        if (defaults.GroupOf(pane) is { } home)
        {
            // In the group of a pane it shares a group with, there.
            foreach (var mate in home.Panes.Where(p => p != pane))
            {
                if (main.GroupOf(mate) is { } group)
                {
                    group.Panes.Insert(Math.Min(home.Panes.IndexOf(pane), group.Panes.Count), pane);
                    return main;
                }
            }

            // Beside the group of the pane next to its own, there.
            var groups = defaults.AllGroups().ToList();
            var at = groups.IndexOf(home);
            foreach (var near in groups.Skip(at + 1).Concat(groups.Take(at).Reverse()))
            {
                if (near.Panes.Select(main.GroupOf).FirstOrDefault(g => g is not null) is { } neighbour)
                {
                    var parent = defaults.ParentOf(home);
                    var zone = parent is { Vertical: true } ? DockZone.Top : DockZone.Left;

                    // As much of the room as it has there, beside that neighbour.
                    var share = 0.5;
                    if (parent is not null && parent.Children.IndexOf(home) is var mine and >= 0
                        && parent.Children.FindIndex(c => c.AllGroups().Contains(near)) is var theirs and >= 0
                        && mine < parent.Sizes.Count && theirs < parent.Sizes.Count)
                        share = parent.Sizes[mine] / (parent.Sizes[mine] + parent.Sizes[theirs]);
                    return DockTree.InsertBeside(main, neighbour, groups.IndexOf(near) > at ? zone : zone == DockZone.Top ? DockZone.Bottom : DockZone.Right, DockNode.Group(pane), share);
                }
            }
        }

        return DockTree.InsertBeside(main, main, DockZone.Right, DockNode.Group(pane));
    }

    // ----- Laying the windows out -----

    /// <summary>Lays the windows out again, once, after whatever has just changed.</summary>
    public void ScheduleRebuild()
    {
        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        _main.Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Takes every pane out of where it is and puts it where the tree says, in both windows.</summary>
    public void Rebuild()
    {
        if (_dragging)
            return;

        // Nothing to do when the windows would come out as they are: every rebuild moves every pane.
        var signature = Signature();
        if (signature == _builtSignature)
            return;

        _builtSignature = signature;
        _groupHosts.Clear();
        _roots.Clear();
        foreach (var content in _made.Values)
            Detach(content);
        foreach (var pane in _panes.Values.Where(p => p.HoverHost is not null))
            RemoveHoverZones(pane.HoverHost!);

        var main = Render(_state.Main, side: false);
        _mainHost.Content = main;
        _roots.Add((_mainHost, false));

        if (_side is { } window)
        {
            var side = _state.Side is { } tree ? Render(tree, side: true) : null;
            window.Host.Content = side;
            window.ShowHint(side is null);
            _roots.Add((window.Host, true));
        }

        FillTitleSlots();
    }

    // What the windows were last laid out from: the arrangement, which panes are shown, and the side window.
    private string? _builtSignature;

    private string Signature() => JsonSerializer.Serialize(_state) + "|" + string.Join(",", _panes.Values.Where(p => p.IsShown()).Select(p => p.Id))
        + "|" + (_side?.GetHashCode() ?? 0);

    /// <summary>Takes an element out of whatever holds it, so it can be put somewhere else.</summary>
    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent ?? VisualTreeHelper.GetParent(element))
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case ContentControl control:
                control.Content = null;
                break;
            case ContentPresenter presenter:
                presenter.Content = null;
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
    }

    private FrameworkElement? Render(DockNode node, bool side)
    {
        if (!node.IsGroup)
            return RenderSplit(node, side);

        var shown = node.Panes.Where(p => _panes[p].IsShown()).ToList();
        if (shown.Count == 0)
            return null;

        var active = node.Active is { } chosen && shown.Contains(chosen) ? chosen : shown[0];
        var host = shown.Any(p => _panes[p].Handle == PaneHandle.Tab) || (shown.Count > 1 && shown.Any(p => _panes[p].Handle == PaneHandle.Hover))
            ? RenderTabs(node, shown, active)
            : shown.Count == 1 && _panes[active].Handle == PaneHandle.Hover
                ? RenderHover(node, active)
                : RenderPlain(active);
        _groupHosts.Add((host, node, side));
        PaneShown?.Invoke(active);
        return host;
    }

    // A group whose panes have titles: the one shown, with the tabs in its title's place (see FillTitleSlots).
    private FrameworkElement RenderPlain(string active) => new Border { Child = Realize(active), Background = Brushes.Transparent };

    /// <summary>A pane with no title of its own: a tab with its name comes up over its top-left corner when the pointer goes there.</summary>
    private FrameworkElement RenderHover(DockNode group, string pane)
    {
        var host = new Grid { Background = Brushes.Transparent };
        host.Children.Add(Realize(pane));
        var zone = HoverZone(group, pane);
        if (_panes[pane].HoverHost is { } overlay)
            overlay.Children.Add(zone);
        else
            host.Children.Add(zone);
        return host;
    }

    private static readonly Brush ZoneBrush = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    private static void RemoveHoverZones(Panel host)
    {
        foreach (var zone in host.Children.OfType<FrameworkElement>().Where(c => c.Tag as string == "dock-hover").ToList())
            host.Children.Remove(zone);
    }

    /// <summary>A strip along the top-left of a pane, all but invisible, that shows the pane's tab while the pointer is on it.</summary>
    private FrameworkElement HoverZone(DockNode group, string pane)
    {
        var tab = MakeTab(group, pane, active: true, hover: true);
        tab.Visibility = Visibility.Collapsed;
        var zone = new Border
        {
            Tag = "dock-hover", Background = ZoneBrush, Height = 7, Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = tab,
        };
        Panel.SetZIndex(zone, 1000);
        zone.MouseEnter += (_, _) =>
        {
            tab.Visibility = Visibility.Visible;
            zone.Height = double.NaN;
            zone.Width = double.NaN;
        };
        zone.MouseLeave += (_, _) =>
        {
            if (_dragging)
                return;
            tab.Visibility = Visibility.Collapsed;
            (zone.Height, zone.Width) = (7, 220);
        };
        return zone;
    }

    /// <summary>A group with a row of tabs above it, as the settings tabs have always been: a tab control of the app's own look.</summary>
    private FrameworkElement RenderTabs(DockNode group, List<string> shown, string active)
    {
        var tabs = new TabControl { Padding = new Thickness(16, 8, 16, 8) };
        foreach (var pane in shown)
        {
            var item = new TabItem { Header = MakeTab(group, pane, active: false, hover: false), Tag = pane };
            if (pane == active || IsRealized(pane))
                item.Content = Realize(pane);
            tabs.Items.Add(item);
            if (pane == active)
                tabs.SelectedItem = item;
        }

        tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, tabs) || tabs.SelectedItem is not TabItem { Tag: string pane } item)
                return;

            item.Content ??= Realize(pane);
            if (group.Active != pane)
            {
                group.Active = pane;
                Changed?.Invoke();
            }

            PaneShown?.Invoke(pane);
        };
        return tabs;
    }

    /// <summary>A split: its parts side by side (or over each other), each with its share of the room, and borders between them to drag.</summary>
    private FrameworkElement? RenderSplit(DockNode split, bool side)
    {
        var parts = new List<(FrameworkElement Element, int Index, bool Fits)>();
        for (var i = 0; i < split.Children.Count; i++)
        {
            if (Render(split.Children[i], side) is { } element)
                parts.Add((element, i, split.Vertical && FitsContent(split.Children[i])));
        }

        if (parts.Count == 0)
            return null;
        if (parts.Count == 1)
            return parts[0].Element;

        var grid = new Grid();
        for (var k = 0; k < parts.Count; k++)
        {
            var (element, index, fits) = parts[k];
            if (k > 0)
                AddBorder(grid, split, parts, k);

            var size = index < split.Sizes.Count && split.Sizes[index] > 0 ? split.Sizes[index] : 1;
            var length = fits ? GridLength.Auto : new GridLength(size, GridUnitType.Star);
            if (split.Vertical)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = length, MinHeight = fits ? 0 : MinHeightOf(split.Children[index]) });
                Grid.SetRow(element, grid.RowDefinitions.Count - 1);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length, MinWidth = MinWidthOf(split.Children[index]) });
                Grid.SetColumn(element, grid.ColumnDefinitions.Count - 1);
            }

            grid.Children.Add(element);
        }

        return grid;
    }

    /// <summary>
    /// The border between two parts of a split: dragged, it shares the room between them anew. Next to a part as tall as
    /// its content it is only a gap, or, beside a pane that can be made taller (the master timeline), it makes it so.
    /// </summary>
    private void AddBorder(Grid grid, DockNode split, List<(FrameworkElement Element, int Index, bool Fits)> parts, int k)
    {
        var (before, after) = (parts[k - 1], parts[k]);
        UIElement border;
        if (before.Fits || after.Fits)
        {
            var (fitting, below) = before.Fits ? (split.Children[before.Index], false) : (split.Children[after.Index], true);
            var resize = fitting.AllPanes().Select(p => _panes[p].ResizeBy).FirstOrDefault(r => r is not null);
            if (resize is null)
            {
                border = new Border { Height = 8 };
            }
            else
            {
                // Dragged towards the pane that fits its content, it makes it smaller; away from it, taller.
                var thumb = new Thumb { Height = 10, Cursor = Cursors.SizeNS, Template = SplitterLook(), ToolTip = "Drag to make it taller or shorter" };
                thumb.DragDelta += (_, e) => resize(below ? -e.VerticalChange : e.VerticalChange);
                border = thumb;
            }
        }
        else
        {
            var splitter = new GridSplitter
            {
                ResizeDirection = split.Vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                Cursor = split.Vertical ? Cursors.SizeNS : Cursors.SizeWE, KeyboardIncrement = 16, Focusable = false,
            };
            splitter.SetResourceReference(FrameworkElement.StyleProperty, "QuietSplitter");
            if (split.Vertical)
                (splitter.Height, splitter.Margin, splitter.VerticalAlignment) = (2, new Thickness(0, 4, 0, 4), VerticalAlignment.Center);
            else
                (splitter.Width, splitter.Margin, splitter.HorizontalAlignment) = (2, new Thickness(4, 0, 4, 0), HorizontalAlignment.Center);
            splitter.DragCompleted += (_, _) => TakeSizes(grid, split, parts);
            border = splitter;
        }

        if (split.Vertical)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(border, grid.RowDefinitions.Count - 1);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(border, grid.ColumnDefinitions.Count - 1);
        }

        grid.Children.Add(border);
    }

    private static ControlTemplate SplitterLook()
    {
        var template = new ControlTemplate(typeof(Thumb));
        var bar = new FrameworkElementFactory(typeof(Border));
        bar.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.HeightProperty, 2.0);
        line.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        line.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
        bar.AppendChild(line);
        template.VisualTree = bar;
        return template;
    }

    /// <summary>After a border was dragged: the parts' sizes on screen become their shares.</summary>
    private void TakeSizes(Grid grid, DockNode split, List<(FrameworkElement Element, int Index, bool Fits)> parts)
    {
        foreach (var (element, index, fits) in parts)
        {
            if (fits || index >= split.Sizes.Count)
                continue;
            split.Sizes[index] = Math.Max(split.Vertical ? element.ActualHeight : element.ActualWidth, 1);
        }

        Changed?.Invoke();
    }

    private bool FitsContent(DockNode node)
    {
        var shown = node.AllPanes().Where(p => _panes[p].IsShown()).ToList();
        return node.IsGroup && shown.Count > 0 && shown.All(p => _panes[p].FitsContent && _panes[p].Handle != PaneHandle.Tab);
    }

    private double MinWidthOf(DockNode node) => node.IsGroup
        ? node.Panes.Where(p => _panes[p].IsShown()).Select(p => _panes[p].MinWidth()).DefaultIfEmpty(0).Max()
        : node.Vertical ? node.Children.Select(MinWidthOf).DefaultIfEmpty(0).Max() : node.Children.Sum(MinWidthOf);

    private double MinHeightOf(DockNode node) => node.IsGroup
        ? node.Panes.Where(p => _panes[p].IsShown()).Select(p => _panes[p].MinHeight).DefaultIfEmpty(0).Max()
        : node.Vertical ? node.Children.Sum(MinHeightOf) : node.Children.Select(MinHeightOf).DefaultIfEmpty(0).Max();

    // ----- Tabs -----

    /// <summary>Puts each group's tabs in the title slots of its panes, or takes the title away where a row of tabs above says it.</summary>
    private void FillTitleSlots()
    {
        foreach (var pane in _panes.Keys)
        {
            if (PaneTitleSlot.Of(pane) is not { } slot)
                continue;

            var entry = _groupHosts.FirstOrDefault(g => g.Group.Panes.Contains(pane));
            if (entry.Host is null or TabControl)
            {
                (slot.Content, slot.Visibility) = (null, entry.Host is TabControl ? Visibility.Collapsed : Visibility.Visible);
                continue;
            }

            var shown = entry.Group.Panes.Where(p => _panes[p].IsShown()).ToList();
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var mate in shown)
                strip.Children.Add(MakeTab(entry.Group, mate, active: mate == pane, hover: false, inline: shown.Count > 1));
            (slot.Content, slot.Visibility) = (strip, Visibility.Visible);
        }
    }

    /// <summary>
    /// A pane's tab: its name, to drag it elsewhere (or click it, among others in its group, to show it), and a menu
    /// on a right-click (move to the other window, close).
    /// </summary>
    private FrameworkElement MakeTab(DockNode group, string pane, bool active, bool hover, bool inline = false)
    {
        var info = _panes[pane];
        var text = new TextBlock { Text = info.Title, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement tab;
        if (hover)
        {
            text.FontWeight = FontWeights.SemiBold;
            text.FontSize = 11.5;
            var border = new Border { Child = text, Padding = new Thickness(9, 2, 9, 3), CornerRadius = new CornerRadius(0, 0, 5, 0), BorderThickness = new Thickness(0, 0, 1, 1) };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            tab = border;
        }
        else if (inline)
        {
            // Among others in a pane's title row: the one shown is in the title's own type, the others are muted.
            text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            if (!active)
                text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var border = new Border { Child = text, Padding = new Thickness(0, 0, 0, 2), Margin = new Thickness(0, 0, 14, 0), BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0) };
            border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            tab = border;
        }
        else
        {
            text.FontWeight = active ? FontWeights.SemiBold : text.FontWeight;
            tab = text;
        }

        tab.Cursor = Cursors.SizeAll;
        tab.ToolTip = $"{info.Title}: drag this tab to move the pane (onto another pane to tab them together, to a side of it to put it beside, or out of the window into the side window). Right-click for more.";

        // Something to take the pointer all over it, not only on the letters.
        if (tab is TextBlock title)
            title.Background = Brushes.Transparent;
        else if (tab is Border { Background: null } plain)
            plain.Background = Brushes.Transparent;

        // The tab holds the pointer from the press on, so that a drag that soon leaves its few letters still starts.
        tab.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _press = (pane, tab, e.GetPosition(tab));
            tab.CaptureMouse();
        };
        tab.PreviewMouseMove += (_, e) => OnTabMove(tab, e);
        tab.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var pressed = _press is { } press && ReferenceEquals(press.Tab, tab);
            if (_dragging)
            {
                EndDrag(commit: true);
                e.Handled = true;
            }
            else
            {
                _press = null;
                if (tab.IsMouseCaptured)
                    tab.ReleaseMouseCapture();
                if (pressed && inline)
                    Activate(pane);
            }

            _press = null;
        };
        tab.LostMouseCapture += (_, _) =>
        {
            if (_dragging)
                EndDrag(commit: false);
        };
        tab.MouseRightButtonUp += (_, e) =>
        {
            OpenTabMenu(tab, pane);
            e.Handled = true;
        };
        return tab;
    }

    /// <summary>Shows a pane: it becomes the one shown in its group.</summary>
    public void Activate(string pane)
    {
        var group = _state.Main.GroupOf(pane) ?? _state.Side?.GroupOf(pane);
        if (group is null || group.Active == pane)
            return;

        group.Active = pane;
        Rebuild();
        Changed?.Invoke();
    }

    private void OpenTabMenu(FrameworkElement tab, string pane)
    {
        var info = _panes[pane];
        var inSide = _state.Side?.GroupOf(pane) is not null;
        var menu = new ContextMenu { PlacementTarget = tab };
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add(inSide ? "Move to Main Window" : "Move to Side Window", () => MoveToWindow(pane, side: !inSide));
        if (info.Close is { } close)
            Add($"Close {info.Title}", close);
        menu.Items.Add(new Separator());
        Add("Reset Panes Layout", Reset);
        menu.IsOpen = true;
    }

    // ----- Dragging a pane -----

    private void OnTabMove(FrameworkElement tab, MouseEventArgs e)
    {
        if (_press is not { } press || !ReferenceEquals(press.Tab, tab))
            return;

        if (!_dragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _press = null;
                if (tab.IsMouseCaptured)
                    tab.ReleaseMouseCapture();
                return;
            }

            var moved = e.GetPosition(tab) - press.At;
            if (Math.Abs(moved.X) < DragStart && Math.Abs(moved.Y) < DragStart)
                return;

            if (!tab.IsMouseCaptured && !tab.CaptureMouse())
                return;

            _dragging = true;
            // Owned by the main window, so it goes with it (the app ends when its last window closes).
            _guide ??= new DockGuide((Brush?)_main.TryFindResource("AccentBrush") ?? Brushes.SteelBlue) { Owner = _main };
            if (_side is { } side)
                side.PreviewKeyDown += SideKeyDown;
        }

        UpdateDrag(tab.PointToScreen(e.GetPosition(tab)));
        e.Handled = true;
    }

    private void SideKeyDown(object sender, KeyEventArgs e) => CancelDragOnEscape(e);

    private void CancelDragOnEscape(KeyEventArgs e)
    {
        if (!_dragging || e.Key != Key.Escape)
            return;

        EndDrag(commit: false);
        e.Handled = true;
    }

    private static Rect ScreenRect(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) is null || element.ActualWidth <= 0)
            return Rect.Empty;

        return new Rect(element.PointToScreen(new Point(0, 0)), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    }

    /// <summary>Works out where the pane would go if let go at a point of the screen (in real pixels), and shows it.</summary>
    private void UpdateDrag(Point point)
    {
        if (_press is not { } press)
            return;

        _target = null;
        var dpi = VisualTreeHelper.GetDpi(_main).DpiScaleX;
        foreach (var (host, side) in _roots.OrderByDescending(r => r.Side))
        {
            var bounds = ScreenRect(host);
            if (!bounds.Contains(point))
                continue;

            // At a window's very edge: beside everything in it.
            var edge = 14 * dpi;
            DockZone? atEdge = point.X - bounds.Left < edge ? DockZone.Left : bounds.Right - point.X < edge ? DockZone.Right
                : point.Y - bounds.Top < edge ? DockZone.Top : bounds.Bottom - point.Y < edge ? DockZone.Bottom : null;
            if (atEdge is { } windowEdge)
            {
                _target = (side, null, windowEdge, false);
                _guide!.ShowAt(Half(bounds, windowEdge, 0.25), $"{_panes[press.Pane].Title}: along the {windowEdge.ToString().ToLowerInvariant()} of the window");
                return;
            }

            foreach (var (groupHost, group, groupSide) in _groupHosts.Where(g => g.Side == side))
            {
                var area = ScreenRect(groupHost);
                if (!area.Contains(point))
                    continue;

                var (x, y) = ((point.X - area.Left) / area.Width, (point.Y - area.Top) / area.Height);
                var zone = x < 0.25 ? DockZone.Left : x > 0.75 ? DockZone.Right : y < 0.25 ? DockZone.Top : y > 0.75 ? DockZone.Bottom : DockZone.Center;

                // Over a row of tabs: in among them.
                if (groupHost is TabControl && point.Y - area.Top < 34 * dpi)
                    zone = DockZone.Center;

                // Onto its own group, alone there, or among its own tabs: it would stay where it is.
                if (group.Panes.Contains(press.Pane) && (zone == DockZone.Center || group.Panes.Count(p => _panes[p].IsShown()) == 1))
                {
                    _guide!.Hide();
                    return;
                }

                var title = _panes[press.Pane].Title;
                var with = group.Active is { } shown && _panes.TryGetValue(shown, out var other) ? other.Title : "this pane";
                _target = (groupSide, group, zone, false);
                _guide!.ShowAt(zone == DockZone.Center ? area : Half(area, zone, 0.5),
                    zone == DockZone.Center ? $"{title}: a tab beside {with}" : $"{title}: {zone.ToString().ToLowerInvariant()} of {with}");
                return;
            }

            return;
        }

        // Outside both windows: to the side window, opened where it is let go.
        _target = (true, null, DockZone.Right, true);
        _guide!.ShowAt(new Rect(point.X - 160 * dpi, point.Y - 100 * dpi, 320 * dpi, 200 * dpi), $"{_panes[press.Pane].Title}: in the side window");
    }

    private static Rect Half(Rect area, DockZone zone, double share) => zone switch
    {
        DockZone.Left => new Rect(area.Left, area.Top, area.Width * share, area.Height),
        DockZone.Right => new Rect(area.Right - area.Width * share, area.Top, area.Width * share, area.Height),
        DockZone.Top => new Rect(area.Left, area.Top, area.Width, area.Height * share),
        DockZone.Bottom => new Rect(area.Left, area.Bottom - area.Height * share, area.Width, area.Height * share),
        _ => area,
    };

    private void EndDrag(bool commit)
    {
        var (press, target) = (_press, _target);
        (_dragging, _press, _target) = (false, null, null);
        _guide?.Hide();
        if (_side is { } side)
            side.PreviewKeyDown -= SideKeyDown;
        if (press is { } pressed && pressed.Tab.IsMouseCaptured)
            pressed.Tab.ReleaseMouseCapture();

        if (!commit || press is not { } drag || target is not { } to)
            return;

        if (to.Outside)
        {
            MoveToWindow(drag.Pane, side: true, atPointer: true);
            return;
        }

        Move(drag.Pane, to.Side, to.Group, to.Zone);
    }

    /// <summary>Moves a pane: into a group (as a tab), beside a group, or along an edge of a window (no group).</summary>
    public void Move(string pane, bool toSide, DockNode? group, DockZone zone)
    {
        // Onto itself: nothing to do.
        if (group is not null && group.Panes.Contains(pane) && (zone == DockZone.Center || group.Panes.Count == 1))
            return;

        TakeOut(pane);
        var root = toSide ? _state.Side : _state.Main;
        if (root is null || (group is not null && root.AllGroups().All(g => !ReferenceEquals(g, group))))
            root = root is null ? DockNode.Group(pane) : DockTree.InsertBeside(root, root, DockZone.Right, DockNode.Group(pane));
        else if (group is null)
            root = DockTree.InsertBeside(root, root, zone, DockNode.Group(pane));
        else if (zone == DockZone.Center)
            (group.Panes, group.Active) = ([.. group.Panes, pane], pane);
        else
            root = DockTree.InsertBeside(root, group, zone, DockNode.Group(pane));

        if (toSide)
            _state.Side = root;
        else
            _state.Main = root!;

        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>Takes a pane out of whichever window has it.</summary>
    private void TakeOut(string pane)
    {
        if (_state.Side?.GroupOf(pane) is not null)
            _state.Side = DockTree.Remove(_state.Side, pane);
        else
            _state.Main = DockTree.Remove(_state.Main, pane) ?? DockNode.Group();
    }

    /// <summary>Moves a pane to the other window: into the side window (opening it), or back where the main window started it.</summary>
    public void MoveToWindow(string pane, bool side, bool atPointer = false)
    {
        if (side)
        {
            if (!_state.SideOpen)
                SetSideOpen(true, atPointer);
            Move(pane, toSide: true, group: null, DockZone.Right);
            return;
        }

        TakeOut(pane);
        _state.Main = PlaceAsDefault(_state.Main, pane);
        Rebuild();
        Changed?.Invoke();
    }

    // ----- The side window -----

    /// <summary>
    /// Opens or closes the side window. Closed, its panes go back where the main window started them, and the
    /// arrangement it had is kept, to be put back when it is opened again.
    /// </summary>
    /// <param name="atPointer">Whether a window that has never been placed opens where the pointer is (a pane let go outside the windows).</param>
    public void SetSideOpen(bool open, bool atPointer = false)
    {
        if (open == _state.SideOpen || _shuttingDown)
            return;

        if (open)
        {
            _state.SideOpen = true;
            if (_state.SideClosed is { } kept)
            {
                foreach (var pane in kept.AllPanes().Where(p => _panes.ContainsKey(p)).ToList())
                    _state.Main = DockTree.Remove(_state.Main, pane) ?? DockNode.Group();
                var seen = new HashSet<string>(_state.Main.AllPanes());
                foreach (var group in kept.AllGroups())
                    group.Panes = group.Panes.Where(p => _panes.ContainsKey(p) && seen.Add(p)).ToList();
                _state.Side = DockTree.Tidy(kept);
                _state.SideClosed = null;
            }

            _openAtPointer = atPointer && _state.SideBounds is null;
        }
        else
        {
            RememberSideBounds();
            _state.SideOpen = false;
            if (_state.Side is { } side)
            {
                _state.SideClosed = side;
                foreach (var pane in side.AllPanes().ToList())
                    _state.Main = PlaceAsDefault(_state.Main, pane);
            }

            _state.Side = null;
        }

        ApplySideWindow();
        Rebuild();
        Changed?.Invoke();
        SideChanged?.Invoke();
    }

    /// <summary>Opens the side window, or closes it, as the arrangement says.</summary>
    private void ApplySideWindow()
    {
        if (_state.SideOpen && _side is null)
        {
            // A window can only be owned by one already shown: at launch, the side window follows the main one.
            if (new System.Windows.Interop.WindowInteropHelper(_main).Handle == IntPtr.Zero)
            {
                if (!_sideWaiting)
                {
                    _sideWaiting = true;
                    _main.ContentRendered += OpenSideWhenShown;
                }

                return;
            }

            var window = new SideWindow(_main);
            if (_state.SideBounds is [var left, var top, var width, var height] && width > 100 && height > 100)
                (window.WindowStartupLocation, window.Left, window.Top, window.Width, window.Height) = (WindowStartupLocation.Manual, left, top, width, height);
            else if (_openAtPointer)
                (window.WindowStartupLocation, window.PlaceAtPointer) = (WindowStartupLocation.Manual, true);
            else
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _openAtPointer = false;
            if (_state.SideMaximized)
                window.WindowState = WindowState.Maximized;

            // Closed by its own button: its panes go back to the main window, now, before its window (and the
            // player's, if it is there) is destroyed. Closed because the app is closing: nothing changes.
            window.Closing += (_, _) =>
            {
                if (_shuttingDown || !ReferenceEquals(_side, window) || !_state.SideOpen)
                    return;

                RememberSideBounds();
                _side = null;
                SetSideOpen(false);
            };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_side, window))
                    _side = null;
            };
            _side = window;
            window.Show();
            SideChanged?.Invoke();
        }
        else if (!_state.SideOpen && _side is { } open)
        {
            _side = null;
            foreach (var content in _made.Values.Where(c => Window.GetWindow(c) == open))
                Detach(content);
            open.Close();
            SideChanged?.Invoke();
        }
    }

    /// <summary>
    /// The app is closing (the arrangement has been saved): the side window's panes are taken out of it before it
    /// goes, so the player's own window outlives it and nothing it holds is destroyed under it.
    /// </summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        _guide?.Close();
        _guide = null;
        if (_side is not { } open)
            return;

        foreach (var content in _made.Values.Where(c => Window.GetWindow(c) == open))
            Detach(content);
    }

    private bool _sideWaiting;

    private void OpenSideWhenShown(object? sender, EventArgs e)
    {
        _main.ContentRendered -= OpenSideWhenShown;
        _sideWaiting = false;
        ApplySideWindow();
        Rebuild();
    }

    private void RememberSideBounds()
    {
        if (_side is not { } window)
            return;

        var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width > 100)
            _state.SideBounds = [bounds.Left, bounds.Top, bounds.Width, bounds.Height];
        _state.SideMaximized = window.WindowState == WindowState.Maximized;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
