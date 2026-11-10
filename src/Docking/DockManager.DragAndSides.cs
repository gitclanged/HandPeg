using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp.Docking;

// ----- DockManager: tabs, dragging a pane, whole arrangements and the side windows (split from DockManager.cs, 5.4.1-M5) -----
public sealed partial class DockManager
{
    // ----- Tabs -----

    /// <summary>Puts each group's tabs in the title slots of its panes, or takes the title away where a row of tabs above says it.</summary>
    /// <param name="only">The panes whose slots to fill (5.4C: one side window's, laid out on its own); null for every pane.</param>
    private void FillTitleSlots(IReadOnlyCollection<string>? only = null)
    {
        foreach (var pane in only ?? _panes.Keys)
        {
            if (!_panes.ContainsKey(pane))
                continue;
            if (PaneTitleSlot.Of(pane) is not { } slot)
                continue;

            var entry = _groupHosts.FirstOrDefault(g => g.Group.Panes.Contains(pane));
            if (entry.Host is null or TabGroupHost)
            {
                (slot.Content, slot.Visibility) = (null, entry.Host is TabGroupHost ? Visibility.Collapsed : Visibility.Visible);
                continue;
            }

            // On one line: the pane shown always has room; the others give theirs up first, into a menu at the end.
            var shown = entry.Group.Panes.Where(p => _panes[p].IsShown()).ToList();
            var tabs = new Dictionary<UIElement, string>();
            var strip = new OverflowStrip { ClipToBounds = true };
            foreach (var mate in shown)
            {
                var tab = MakeTab(entry.Group, mate, active: mate == pane, hover: false, inline: shown.Count > 1);
                System.Windows.Automation.AutomationProperties.SetName(tab, _panes[mate].Title);
                OverflowStrip.SetPriority(tab, mate == pane ? 100 : 0);
                tab.VerticalAlignment = VerticalAlignment.Center;
                strip.Children.Add(tab);
                tabs[tab] = mate;
            }

            if (shown.Count > 1)
            {
                var more = new System.Windows.Controls.Primitives.ToggleButton { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Padding = new Thickness(6, 2, 6, 2), ToolTip = "The other panes of this group" };
                OverflowStrip.SetIsOverflowButton(more, true);
                strip.Children.Add(more);
                strip.MenuEntryFor = child => tabs.TryGetValue(child, out var mate) ? (_panes[mate].Title, () => Activate(mate)) : null;
            }

            (slot.Content, slot.Visibility) = (strip, Visibility.Visible);
        }
    }

    /// <summary>
    /// A pane's tab: its name, to drag it elsewhere (or click it, among others in its group, to show it), and a menu
    /// on a right-click (move to the other window, close).
    /// </summary>
    /// <param name="upright">A hover tab read top to bottom, for a pane standing as a narrow column (the header at a side).</param>
    private FrameworkElement MakeTab(DockNode group, string pane, bool active, bool hover, bool inline = false, bool boxed = false, bool upright = false)
    {
        var info = _panes[pane];
        var text = new TextBlock { Text = info.Title, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement tab;
        if (boxed)
        {
            // In the main tab bar: the classic boxed tab (the app's TabItem look), joined to the panel below while shown and
            // traced along its top edge (the theme's ActiveTabTraceBrush, 2 px; every tab keeps the room for it).
            text.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextBrush" : "MutedTextBrush");
            var border = new PaneTab(byKey =>
            {
                _focusTabOf = byKey && !active ? pane : null;
                Activate(pane);
            }, active) { Child = text, Padding = new Thickness(16, 6, 16, 8), Margin = new Thickness(0, 0, 2, 0), BorderThickness = new Thickness(0, 2, 0, 0), Outlined = true };
            border.SetResourceReference(Border.CornerRadiusProperty, "TabCornerRadius");
            if (active)
            {
                border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
                border.SetResourceReference(Border.BorderBrushProperty, "ActiveTabTraceBrush");
            }
            else
            {
                // The tabs not shown: the theme's InactiveTabBrush (open to what is behind them in most themes; Reno's darker stone).
                border.SetResourceReference(Border.BackgroundProperty, "InactiveTabBrush");
                border.MouseEnter += (_, _) => text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                border.MouseLeave += (_, _) => text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            }

            if (active && _focusTabOf == pane)
            {
                _focusTabOf = null;
                border.Loaded += (_, _) => border.Focus();
            }

            tab = border;
        }
        else if (hover)
        {
            text.FontWeight = FontWeights.SemiBold;
            text.FontSize = 11.5;
            if (upright)
                text.LayoutTransform = new RotateTransform(90);
            var border = new Border { Child = text, Padding = upright ? new Thickness(2, 9, 3, 9) : new Thickness(9, 2, 9, 3), CornerRadius = new CornerRadius(0, 0, 5, 0), BorderThickness = new Thickness(0, 0, 1, 1) };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "ActiveTabTraceBrush");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            tab = border;
        }
        else if (inline)
        {
            // Among others in a pane's title row: the one shown is in the title's own type, the others are muted.
            text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            if (!active)
                text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var border = new PaneTab(byKey =>
            {
                _focusTabOf = byKey && !active ? pane : null;
                Activate(pane);
            }, active) { Child = text, Padding = new Thickness(0, 0, 0, 2), Margin = new Thickness(0, 0, 14, 0), BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0) };
            border.SetResourceReference(Border.BorderBrushProperty, "ActiveTabTraceBrush");
            if (active && _focusTabOf == pane)
            {
                _focusTabOf = null;
                border.Loaded += (_, _) => border.Focus();
            }

            tab = border;
        }
        else
        {
            text.FontWeight = active ? FontWeights.SemiBold : text.FontWeight;
            tab = text;
        }

        tab.ToolTip = $"{info.Title}: drag this tab to move the pane (onto another pane to tab them together, to a side of it to put it beside, or out of the window into a new side window). Right-click for more.";

        // Something to take the pointer all over it, not only on the letters.
        if (tab is TextBlock title)
            title.Background = Brushes.Transparent;
        else if (tab is Border { Background: null } plain)
            plain.Background = Brushes.Transparent;

        WireTab(tab, pane, activates: inline || boxed);
        return tab;
    }

    /// <summary>
    /// Makes something a pane's handle: pressed and moved, it drags the pane; let go without moving, it shows the pane
    /// (<paramref name="activates"/>, among the tabs of a group); right-clicked, it opens the pane's menu.
    /// </summary>
    private void WireTab(FrameworkElement tab, string pane, bool activates)
    {
        // The move pointer only while the button is held on it (4.1.1): at rest and under a passing pointer a tab is a
        // tab, with the ordinary arrow.
        tab.Cursor = null;

        // The tab holds the pointer from the press on, so that a drag that soon leaves its few letters still starts.
        tab.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _press = (pane, tab, e.GetPosition(tab));
            tab.Cursor = Cursors.SizeAll;
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
                if (pressed && activates)
                    Activate(pane);
            }

            _press = null;
        };
        tab.LostMouseCapture += (_, _) =>
        {
            tab.Cursor = null;
            if (_dragging)
                EndDrag(commit: false);
        };
        tab.MouseRightButtonUp += (_, e) =>
        {
            OpenTabMenu(tab, pane);
            e.Handled = true;
        };
    }

    /// <summary>
    /// Shows a pane: it becomes the one shown in its group. Only that group changes, in place: no other pane is taken
    /// out of the window and put back (which made the player flicker on every tab click).
    /// </summary>
    public void Activate(string pane)
    {
        var group = GroupAnywhere(pane);
        if (group is null || group.Active == pane)
            return;

        group.Active = pane;
        var shown = group.Panes.Where(p => _panes[p].IsShown()).ToList();
        switch (_groupHosts.FirstOrDefault(g => ReferenceEquals(g.Group, group)).Host)
        {
            case TabGroupHost tabs:
                ShowInTabs(tabs, shown, pane);
                break;

            // A group of panes with titles: the one shown is swapped, and the tabs in the titles follow.
            case Border plain:
                plain.Child = null;
                plain.Child = Placed(pane);
                FillTitleSlots();
                PaneShown?.Invoke(pane);
                break;

            // No host of its own on screen: the windows are laid out again (the active pane is not in the signature,
            // so the rebuild must be asked for outright).
            default:
                _builtSignature = null;
                Rebuild();
                break;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Shows a pane that has just been switched on in front of its group once the windows have it: after the layout that
    /// <see cref="ScheduleRebuild"/> asked for, in place (<see cref="Activate"/>, as a click on its tab), never a layout of its own.
    /// A pane that is not shown then, or whose group has no host on screen, is left as it is.
    /// </summary>
    public void BringForward(string pane) => _main.Dispatcher.BeginInvoke(() =>
    {
        var group = GroupAnywhere(pane);
        if (!_dragging && group is not null && _panes.TryGetValue(pane, out var known) && known.IsShown() && _groupHosts.Any(g => ReferenceEquals(g.Group, group)))
            Activate(pane);
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void OpenTabMenu(FrameworkElement tab, string pane)
    {
        var info = _panes[pane];
        var menu = new ContextMenu { PlacementTarget = tab };
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        // 5.4C: to a new Pane Docker, to any other one open (by its number), or back to the main window.
        foreach (var (line, action) in TabMenuMoves(pane))
            Add(line, action);
        if (info.Close is { } close)
            Add($"Close {info.Title}", close);
        menu.Items.Add(new Separator());
        Add("Reset Panes Layout", ResetLayout ?? (() => Reset()));
        ExtendTabMenu?.Invoke(menu, pane);
        menu.IsOpen = true;
    }

    /// <summary>
    /// The window moves a pane's right-click menu offers (5.4C): "Move to New Side Window", "Move to Side Window N" for each other Pane
    /// Docker open (N its number among the side windows), and "Move to Main Window" for a pane in a side window.
    /// </summary>
    internal IReadOnlyList<(string Line, Action Do)> TabMenuMoves(string pane)
    {
        var holder = Find(pane).Side;
        var moves = new List<(string, Action)> { ("Move to New Side Window", () => MoveToWindow(pane, side: true)) };
        foreach (var docker in _sides.Where(s => !s.State.IsPreview && !ReferenceEquals(s, holder)))
        {
            var id = docker.State.Id;
            moves.Add(($"Move to Side Window {NumberOf(docker)}", () => MoveToWindow(pane, id)));
        }

        if (holder is not null)
            moves.Add(("Move to Main Window", () => MoveToWindow(pane, sideId: null)));
        return moves;
    }

    /// <summary>Adds the window's own items to a tab's right-click menu (the pane's own settings, 4.2B-2, and the main window's Tabs and Panes list).</summary>
    public Action<ContextMenu, string>? ExtendTabMenu { get; set; }

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
            foreach (var window in SideWindows)
                window.PreviewKeyDown += SideKeyDown;
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
        // The window under the point, the one on top where windows overlap (5.4C: by their order on the screen, so a Pane Docker over
        // the main window takes the pane, and the main window over a docker does). A Live Video Preview takes no pane.
        if (RootAt(point) is { } at)
        {
            if (at.Host is not { } host)
            {
                _guide!.Hide();
                return;
            }

            var (side, bounds) = (at.Side, at.Bounds);
            // At a window's very edge: beside everything in it.
            var edge = 14 * dpi;
            DockZone? atEdge = point.X - bounds.Left < edge ? DockZone.Left : bounds.Right - point.X < edge ? DockZone.Right
                : point.Y - bounds.Top < edge ? DockZone.Top : bounds.Bottom - point.Y < edge ? DockZone.Bottom : null;
            if (atEdge is { } windowEdge)
            {
                _target = (side, null, windowEdge, false, null);
                _guide!.ShowAt(Half(bounds, windowEdge, 0.25), $"{_panes[press.Pane].Title}: along the {windowEdge.ToString().ToLowerInvariant()} of the window");
                return;
            }

            foreach (var (groupHost, group, groupSide) in _groupHosts.Where(g => ReferenceEquals(g.Side, side)))
            {
                var area = ScreenRect(groupHost);
                if (!area.Contains(point))
                    continue;

                var shownPanes = group.Panes.Where(p => _panes[p].IsShown()).ToList();

                // Over a row of tabs (a group's own, or the tabs in its pane's title): in among them, where the line shows.
                if (TabStripOf(groupHost, group) is { } strip && TabGap(strip, shownPanes, point, dpi) is { } gap)
                {
                    // Back where it is (its own place, either side of its own tab): it would stay as it is.
                    var own = shownPanes.IndexOf(press.Pane);
                    if (own >= 0 && (gap.At == own || gap.At == own + 1))
                    {
                        _guide!.Hide();
                        return;
                    }

                    _target = (groupSide, group, DockZone.Center, false, gap.At);
                    _guide!.ShowLine(gap.Line);
                    return;
                }

                var (x, y) = ((point.X - area.Left) / area.Width, (point.Y - area.Top) / area.Height);
                var zone = x < 0.25 ? DockZone.Left : x > 0.75 ? DockZone.Right : y < 0.25 ? DockZone.Top : y > 0.75 ? DockZone.Bottom : DockZone.Center;

                // Over the rest of a row of tabs: in among them, at the end.
                if (groupHost is TabGroupHost tabs && point.Y - area.Top < (tabs.Strip.ActualHeight + 8) * dpi)
                    zone = DockZone.Center;

                // Onto its own group, alone there, or among its own tabs: it would stay where it is.
                if (group.Panes.Contains(press.Pane) && (zone == DockZone.Center || shownPanes.Count == 1))
                {
                    _guide!.Hide();
                    return;
                }

                var title = _panes[press.Pane].Title;
                var with = group.Active is { } shown && _panes.TryGetValue(shown, out var other) ? other.Title : "this pane";
                _target = (groupSide, group, zone, false, null);
                _guide!.ShowAt(zone == DockZone.Center ? area : Half(area, zone, 0.5),
                    zone == DockZone.Center ? $"{title}: a tab beside {with}" : $"{title}: {zone.ToString().ToLowerInvariant()} of {with}");
                return;
            }

            // An empty Pane Docker (one just switched from a preview, or one whose last tab was dragged out; 5.4C review): its whole
            // middle takes the pane (Move makes its first group, or puts it beside what is left). Anywhere else with no group under it: no drop, and no stale guide.
            if (side is { State.IsPreview: false } && !_groupHosts.Any(g => ReferenceEquals(g.Side, side)))
            {
                _target = (side, null, DockZone.Right, false, null);
                _guide!.ShowAt(bounds, $"{_panes[press.Pane].Title}: into this window");
                return;
            }

            _target = null;
            _guide!.Hide();
            return;
        }

        // Outside every window: a new Pane Docker, opened where it is let go.
        _target = (null, null, DockZone.Right, true, null);
        _guide!.ShowAt(new Rect(point.X - 160 * dpi, point.Y - 100 * dpi, 320 * dpi, 200 * dpi), $"{_panes[press.Pane].Title}: in a new side window");
    }

    /// <summary>
    /// The window a dragged tab is over (5.4C): of the windows laid out with panes (the main window, each Pane Docker) and the Live Video
    /// Previews whose area holds the point, the one highest on the screen (WindowOrder.Topmost); its host, side window and area, or a
    /// null host for a Live Video Preview (no drop root). Null when the point is over none of them.
    /// </summary>
    private (FrameworkElement? Host, SideHost? Side, Rect Bounds)? RootAt(Point point)
    {
        var under = new List<(IntPtr Handle, FrameworkElement? Host, SideHost? Side, Rect Bounds)>();
        foreach (var (host, side) in _roots)
        {
            var bounds = ScreenRect(host);
            if (bounds.Contains(point) && Window.GetWindow(host) is { } window)
                under.Add((new System.Windows.Interop.WindowInteropHelper(window).Handle, host, side, bounds));
        }

        foreach (var preview in _sides.Where(s => s.State.IsPreview && s.Window is { IsVisible: true }))
        {
            var bounds = ScreenRect(preview.Window!);
            if (bounds.Contains(point))
                under.Add((new System.Windows.Interop.WindowInteropHelper(preview.Window!).Handle, null, preview, bounds));
        }

        if (under.Count == 0)
            return null;
        var top = under.Count == 1 ? under[0].Handle : WindowOrder.Topmost(under.Select(u => u.Handle).ToList());
        var found = under.FirstOrDefault(u => u.Handle == top);
        if (found.Handle == IntPtr.Zero)
            found = under[0];
        return (found.Host, found.Side, found.Bounds);
    }

    /// <summary>A group's row of tabs on screen: its own (a group shown with tabs), or the one in its shown pane's title; null for a pane with a hover tab.</summary>
    private static OverflowStrip? TabStripOf(FrameworkElement host, DockNode group)
    {
        if (host is TabGroupHost tabs)
            return tabs.Strip;

        // The pane shown is the only one of the group on screen, so its slot is the one seen.
        return host is Border ? group.Panes.Select(PaneTitleSlot.Of).FirstOrDefault(s => s is { Content: OverflowStrip, IsVisible: true })?.Content as OverflowStrip : null;
    }

    /// <summary>
    /// Where among a row of tabs a point is, while it is level with them: the place a tab let go there takes (counted
    /// among the group's tabs shown), and the line to show there, between two tabs (or before the first, after the
    /// last), in the screen's pixels. Tabs listed in the row's "more" menu (laid out with no room) are passed over.
    /// </summary>
    private static (int At, Rect Line)? TabGap(OverflowStrip strip, List<string> shown, Point point, double dpi)
    {
        var tabs = strip.Children.OfType<FrameworkElement>().Where(c => !OverflowStrip.GetIsOverflowButton(c)).Take(shown.Count)
            .Select((tab, index) => (Index: index, Rect: ScreenRect(tab))).Where(t => !t.Rect.IsEmpty && t.Rect.Width >= 1).ToList();
        if (tabs.Count == 0)
            return null;

        // Level with the tabs (a little above and below them too), and not far past the last one.
        var (top, bottom) = (tabs.Min(t => t.Rect.Top), tabs.Max(t => t.Rect.Bottom));
        if (point.Y < top - 6 * dpi || point.Y > bottom + 6 * dpi || point.X < tabs[0].Rect.Left - 12 * dpi || point.X > tabs[^1].Rect.Right + 60 * dpi)
            return null;

        // Before the first tab whose middle is past the point; after the last one otherwise.
        var next = tabs.FindIndex(t => point.X < t.Rect.Left + t.Rect.Width / 2);
        double x;
        int at;
        if (next < 0)
            (x, at) = (tabs[^1].Rect.Right + 2 * dpi, tabs[^1].Index + 1);
        else if (next == 0)
            (x, at) = (tabs[0].Rect.Left - 2 * dpi, tabs[0].Index);
        else
            (x, at) = ((tabs[next - 1].Rect.Right + tabs[next].Rect.Left) / 2, tabs[next].Index);

        var width = Math.Max(2, Math.Round(3 * dpi));
        return (at, new Rect(x - width / 2, top, width, bottom - top));
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
        foreach (var window in SideWindows)
            window.PreviewKeyDown -= SideKeyDown;
        if (press is { } pressed && pressed.Tab.IsMouseCaptured)
            pressed.Tab.ReleaseMouseCapture();

        if (!commit || press is not { } drag || target is not { } to)
            return;

        if (to.Outside)
        {
            MoveToWindow(drag.Pane, side: true, atPointer: true);
            return;
        }

        Move(drag.Pane, to.Side, to.Group, to.Zone, to.At);
    }

    /// <summary>
    /// Moves a pane, and only that pane (the others of its group stay where they are): into a group (as a tab, at a
    /// place among its tabs or at the end), beside a group, or along an edge of a window (no group).
    /// </summary>
    /// <param name="to">The side window it goes to (5.4C: a Pane Docker); null for the main window.</param>
    /// <param name="at">Where among the group's tabs shown it goes; null for after them.</param>
    private void Move(string pane, SideHost? to, DockNode? group, DockZone zone, int? at = null)
    {
        // Among its own group's tabs: to its new place there.
        if (group is not null && group.Panes.Contains(pane) && zone == DockZone.Center && at is { } place)
        {
            Reorder(group, pane, place);
            return;
        }

        // Onto itself: nothing to do.
        if (group is not null && group.Panes.Contains(pane) && (zone == DockZone.Center || group.Panes.Count == 1))
            return;

        TakeOut(pane);
        var root = to is null ? _state.Main : to.State.Tree;
        if (root is null || (group is not null && root.AllGroups().All(g => !ReferenceEquals(g, group))))
            root = root is null ? DockNode.Group(pane) : DockTree.InsertBeside(root, root, DockZone.Right, DockNode.Group(pane));
        else if (group is null)
            root = DockTree.InsertBeside(root, root, zone, DockNode.Group(pane));
        else if (zone == DockZone.Center)
        {
            group.Panes.Insert(at is { } index ? PanesIndex(group, index) : group.Panes.Count, pane);
            group.Active = pane;
        }
        else
        {
            root = DockTree.InsertBeside(root, group, zone, DockNode.Group(pane));
        }

        if (to is not null)
            to.State.Tree = root;
        else
            _state.Main = root;

        Rebuild();
        Changed?.Invoke();
        RaiseSideChanged();
    }

    /// <summary>Takes a pane out of whichever window has it.</summary>
    private void TakeOut(string pane)
    {
        var (group, side) = Find(pane);
        if (group is null)
            return;
        if (side is not null)
            side.State.Tree = DockTree.Remove(side.State.Tree!, pane);
        else
            _state.Main = DockTree.Remove(_state.Main, pane) ?? DockNode.Group();
    }
    /// <summary>Where in a group's list of panes (hidden ones too) a place among its tabs shown is: before the pane shown there, or after the last one shown.</summary>
    private int PanesIndex(DockNode group, int at)
    {
        var shown = group.Panes.Where(p => _panes[p].IsShown()).ToList();
        if (at < shown.Count)
            return group.Panes.IndexOf(shown[Math.Max(at, 0)]);
        return shown.Count > 0 ? group.Panes.IndexOf(shown[^1]) + 1 : group.Panes.Count;
    }

    /// <summary>
    /// A tab dragged along its own row: it goes to its new place there and is shown, the hidden panes of the group
    /// keeping theirs. Only the row is made again (the panes stay where they are); the order is saved with the arrangement.
    /// </summary>
    private void Reorder(DockNode group, string pane, int at)
    {
        var shown = group.Panes.Where(p => _panes[p].IsShown()).ToList();
        var from = shown.IndexOf(pane);
        if (from < 0)
            return;

        shown.RemoveAt(from);
        shown.Insert(Math.Clamp(at > from ? at - 1 : at, 0, shown.Count), pane);
        var order = new Queue<string>(shown);
        group.Panes = group.Panes.Select(p => _panes[p].IsShown() ? order.Dequeue() : p).ToList();
        var switched = group.Active != pane;
        group.Active = pane;

        switch (_groupHosts.FirstOrDefault(g => ReferenceEquals(g.Group, group)).Host)
        {
            case TabGroupHost tabs:
                ShowInTabs(tabs, shown, pane);
                break;

            case Border plain:
                if (switched)
                {
                    plain.Child = null;
                    plain.Child = Placed(pane);
                    PaneShown?.Invoke(pane);
                }

                FillTitleSlots();
                break;

            default:
                _builtSignature = null;
                Rebuild();
                break;
        }

        // The windows are as the arrangement now says: nothing for a rebuild to do.
        MarkAllBuilt();
        Changed?.Invoke();
    }

    /// <summary>
    /// Moves a pane to another window: with <paramref name="side"/> into a new Pane Docker (5.4C; opened under the pointer when
    /// <paramref name="atPointer"/>: a tab let go outside every window), else back where the main window started it.
    /// </summary>
    public void MoveToWindow(string pane, bool side, bool atPointer = false)
    {
        if (!side)
        {
            MoveToWindow(pane, sideId: null);
            return;
        }

        if (_shuttingDown || !_panes.ContainsKey(pane))
            return;

        // The new window's arrangement holds the pane before the window is made, so it opens with it (never empty for a moment).
        TakeOut(pane);
        var docker = AddSide(SideWindowMode.PaneDocker, atPointer);
        docker.State.Tree = DockNode.Group(pane);
        ApplySideWindows();
        Rebuild();
        Changed?.Invoke();
        RaiseSideChanged();
    }

    /// <summary>
    /// Moves a pane to the side window <paramref name="sideId"/> (5.4C: a Pane Docker open now, the pane put at its right-hand side),
    /// or with null back where the main window started it. A Live Video Preview, or a side window not open, takes nothing.
    /// </summary>
    public void MoveToWindow(string pane, string? sideId, bool atPointer = false)
    {
        if (!_panes.ContainsKey(pane))
            return;

        if (sideId is not null)
        {
            if (_sides.FirstOrDefault(s => s.State.Id == sideId) is { State.IsPreview: false } docker)
            {
                docker.AtPointer |= atPointer && docker.Window is null;
                Move(pane, docker, group: null, DockZone.Right);
            }

            return;
        }

        if (Find(pane).Side is null && _state.Main.GroupOf(pane) is not null)
            return;
        TakeOut(pane);
        _state.Main = PlaceAsDefault(_state.Main, pane);
        Rebuild();
        Changed?.Invoke();
        RaiseSideChanged();
    }
    // ----- Arrangements taken up whole (the Layout button: built-in layouts, the user's own) -----

    /// <summary>The arrangement as it is now, as a copy (to keep as a layout of the user's own, or to come back to).</summary>
    public DockLayoutState Current
    {
        get => _state.Copy();
    }

    /// <summary>
    /// Lays the windows out as an arrangement for a moment (a layout the pointer is on, in the Layout Selector): nothing
    /// is saved, and the side windows stay as they are. The player floats, so it only moves (see <see cref="WindowRoot"/>).
    /// </summary>
    public void Preview(DockLayoutState layout)
    {
        _state = KeepingSideWindow(layout);
        Rebuild();
    }

    /// <summary>
    /// Takes an arrangement up for good, the side windows left as they are, and saves it. 5.4D: one laid out already (the card previewed,
    /// then kept) is taken up in place, as Load does (Rebind): the windows stay, and its tree is the one the borders and tabs now change.
    /// </summary>
    public void Apply(DockLayoutState layout)
    {
        _state = KeepingSideWindow(layout);
        if (Signature() == _builtSignature)
            Rebind();
        else
            Rebuild();
        Changed?.Invoke();
    }

    /// <summary>An arrangement as it would be taken up now (panes no longer known left out, missing ones put in, the side windows as they are): to preview and draw.</summary>
    public DockLayoutState Prepared(DockLayoutState layout) => KeepingSideWindow(layout);

    /// <summary>
    /// Whether two arrangements lay the main window out alike: the same panes in the same groups, in the same splits
    /// the same way round. How wide each part is, which tab is shown and the tabs' order do not count (the Layout
    /// Selector's mark of the layout in use, Pass 4.1C: dragging a splitter or a tab does not make it another layout).
    /// </summary>
    public static bool SameShape(DockLayoutState a, DockLayoutState b) => Shape(a.Main) == Shape(b.Main);

    /// <summary>A part's shape as text (what SameShape compares): its groups' panes in name order, its splits' way round; also what the golden dock-standard-layout prints.</summary>
    internal static string Shape(DockNode node) => node.IsGroup
        ? "[" + string.Join(",", node.Panes.Order(StringComparer.Ordinal)) + "]"
        : (node.Vertical ? "V(" : "H(") + string.Join(";", node.Children.Select(Shape)) + ")";

    /// <summary>
    /// An arrangement for the main window with the side windows as they are now: the panes the Pane Dockers hold stay there
    /// (taken out of the arrangement's main window), so taking a layout up never opens, closes or empties one (5.4C: nor is a
    /// side window inside a saved layout ever taken up: the side windows are saved apart).
    /// </summary>
    private DockLayoutState KeepingSideWindow(DockLayoutState layout)
    {
        var state = layout.Copy();
        (state.Side, state.SideOpen, state.SideBounds, state.SideMaximized, state.SideClosed) = (null, false, null, false, null);
        foreach (var pane in _sides.SelectMany(s => s.State.Tree?.AllPanes() ?? []).ToList())
            state.Main = DockTree.Remove(state.Main, pane) ?? DockNode.Group();
        return Validate(state, [.. _sides.Select(s => s.State)]);
    }

    /// <summary>
    /// A small picture of an arrangement's main window, for the Layout Selector's cards: each part a rounded box named
    /// after its panes (as this mode shows them: hidden panes left out, and a part with none takes no room), the rows
    /// sized to their content (the Sequence Overview Scrubber, the playback controls) a thin band, the header a small mark at the
    /// end of the row it joins. Drawn once and frozen, so showing it costs nothing.
    /// </summary>
    public DrawingImage Miniature(DockLayoutState layout, Size size, Brush part, Brush band, Brush text, Brush accent)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(size));
            var dpi = VisualTreeHelper.GetDpi(_main).PixelsPerDip;
            var typeface = new Typeface(_main.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            DrawPart(context, layout.Main, new Rect(size), joining: false);

            void DrawPart(DrawingContext dc, DockNode node, Rect room, bool joining)
            {
                if (room.Width < 2 || room.Height < 2)
                    return;

                if (node.IsGroup)
                {
                    var shown = node.Panes.Where(p => _panes.ContainsKey(p) && _panes[p].IsShown()).ToList();
                    if (shown.Count == 0)
                        return;

                    var box = new Rect(room.X + 1.5, room.Y + 1.5, Math.Max(room.Width - 3, 1), Math.Max(room.Height - 3, 1));
                    var thin = shown.All(p => _panes[p].FitsContent && _panes[p].Handle != PaneHandle.Tab);
                    dc.DrawRoundedRectangle(thin ? band : part, null, box, 3, 3);
                    if (joining)
                        dc.DrawRoundedRectangle(accent, null, new Rect(box.Right - Math.Min(18, box.Width / 3) - 2, box.Y + 2, Math.Min(18, box.Width / 3), 4), 1.5, 1.5);

                    var active = node.Active is { } chosen && shown.Contains(chosen) ? chosen : shown[0];
                    var title = shown.Count switch
                    {
                        1 => _panes[active].Title,
                        2 => string.Join(" | ", shown.Select(p => _panes[p].Title)),
                        _ => $"{_panes[active].Title} +{shown.Count - 1}",
                    };
                    var words = new FormattedText(title, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 8.5, text, dpi)
                    {
                        MaxTextWidth = Math.Max(box.Width - 6, 1), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                    };
                    if (box.Height >= words.Height + 2)
                        dc.DrawText(words, new Point(box.X + 3, thin ? box.Y + (box.Height - words.Height) / 2 : box.Y + 3 + (joining ? 4 : 0)));
                    return;
                }

                // The parts that show anything: those sized to their content get a fixed band, the others share the rest.
                var parts = node.Children.Select((child, i) => (Child: child, Size: i < node.Sizes.Count && node.Sizes[i] > 0 ? node.Sizes[i] : 1))
                    .Where(p => p.Child.AllPanes().Any(id => _panes.ContainsKey(id) && _panes[id].IsShown())).ToList();

                // Over each other, the header joins the part under it when that can have it (its mark is drawn there).
                var joins = new HashSet<DockNode>();
                for (var i = parts.Count - 2; node.Vertical && i >= 0; i--)
                {
                    if (Alone(parts[i].Child) is { } alone && _panes[alone].JoinsPaneBelow && CanJoin(parts[i + 1].Child))
                    {
                        joins.Add(parts[i + 1].Child);
                        parts.RemoveAt(i);
                    }
                }

                double Fixed(DockNode child) => node.Vertical
                    ? child.IsGroup && FitsContent(child) ? Math.Min(room.Height * 0.12, 11) : 0
                    : AloneIn(child) is { } one && _panes[one].Reshape is not null ? Math.Min(room.Width * 0.08, 9) : 0;

                var length = node.Vertical ? room.Height : room.Width;
                var fixedTotal = parts.Sum(p => Fixed(p.Child));
                var shares = parts.Where(p => Fixed(p.Child) == 0).Sum(p => p.Size);
                var at = node.Vertical ? room.Y : room.X;
                foreach (var (child, share) in parts)
                {
                    var fixedLength = Fixed(child);
                    var extent = fixedLength > 0 ? fixedLength : shares > 0 ? (length - fixedTotal) * share / shares : 0;
                    var slice = node.Vertical ? new Rect(room.X, at, room.Width, extent) : new Rect(at, room.Y, extent, room.Height);
                    DrawPart(dc, child, slice, joins.Contains(child));
                    at += extent;
                }
            }
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    // ----- The side windows (5.4C: any number, each a Live Video Preview or a Pane Docker) -----

    /// <summary>A new side window's state, listed (its window is made by ApplySideWindows).</summary>
    private SideHost AddSide(SideWindowMode mode, bool atPointer)
    {
        var state = new SideWindowState { Id = SideWindowStates.NewId(_sides.Select(s => s.State)), Mode = SideWindowState.ModeOf(mode) };
        var host = new SideHost(state) { AtPointer = atPointer };
        _sides.Add(host);
        return host;
    }

    /// <summary>
    /// Opens a new side window (5.4C): a Live Video Preview (the Layout Selector's "+ New Side Window") or an empty Pane Docker. Laid out on
    /// its own: the main window is not laid out again. Its window, or null while the main window is not shown yet (it opens then).
    /// </summary>
    /// <param name="atPointer">Whether it opens under the pointer, on whichever monitor that is, rather than over the main window.</param>
    public SideWindow? OpenSideWindow(SideWindowMode mode, bool atPointer = false)
    {
        if (_shuttingDown)
            return null;

        var host = AddSide(mode, atPointer);
        ApplySideWindows();
        RebuildSide(host);
        Changed?.Invoke();
        RaiseSideChanged();
        return host.Window;
    }

    /// <summary>Closes a side window (5.4C): a Pane Docker's panes go back where the main window started them.</summary>
    public void CloseSideWindow(string id)
    {
        if (_sides.FirstOrDefault(s => s.State.Id == id) is { } host)
            CloseSide(host, closeWindow: true);
    }

    /// <summary>Brings a side window to the front (the Layout Selector's row for it), restored if it was minimized.</summary>
    public void BringSideWindowForward(string id)
    {
        if (_sides.FirstOrDefault(s => s.State.Id == id)?.Window is not { } window)
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Switches a side window between a Live Video Preview and a Pane Docker (its title bar's choice, 5.4C). A docker that becomes a
    /// preview hands its panes back where the main window started them (a preview holds none); a preview becomes an empty docker.
    /// </summary>
    public void SetSideWindowMode(string id, SideWindowMode mode)
    {
        if (_sides.FirstOrDefault(s => s.State.Id == id) is not { } host || host.State.WindowMode == mode)
            return;

        var panes = host.State.Tree?.AllPanes().ToList() ?? [];
        (host.State.Tree, host.State.Mode) = (null, SideWindowState.ModeOf(mode));
        foreach (var pane in panes)
            _state.Main = PlaceAsDefault(_state.Main, pane);
        host.Window?.ShowMode(mode);
        if (panes.Count > 0)
            Rebuild();
        else
            RebuildSide(host);
        Changed?.Invoke();
        RaiseSideChanged();
    }

    /// <summary>
    /// A side window goes (its close button, the Layout Selector's row, a load without it): its panes go back where the main window started
    /// them, now, before its window (and the player's, if it is there) is destroyed; <paramref name="closeWindow"/> false when it is
    /// closing already.
    /// </summary>
    private void CloseSide(SideHost host, bool closeWindow)
    {
        if (!_sides.Remove(host))
            return;

        var panes = host.State.Tree?.AllPanes().ToList() ?? [];
        host.State.Tree = null;
        foreach (var pane in panes)
            _state.Main = PlaceAsDefault(_state.Main, pane);
        CloseWindowOf(host, closeWindow);
        if (panes.Count > 0)
            Rebuild();
        else
            MarkSideBuilt(null);
        Changed?.Invoke();
        RaiseSideChanged();
    }

    /// <summary>Takes everything of the dock's out of a side window's window (so nothing it holds is destroyed with it), and closes it.</summary>
    private void CloseWindowOf(SideHost host, bool close = true)
    {
        var window = host.Window;
        (host.Window, host.Root) = (null, null);
        _groupHosts.RemoveAll(g => ReferenceEquals(g.Side, host));
        _splitViews.RemoveAll(v => ReferenceEquals(v.Side, host));
        _roots.RemoveAll(r => ReferenceEquals(r.Side, host));
        if (window is null)
            return;

        foreach (var content in Movables().Where(c => Window.GetWindow(c) == window).ToList())
            Detach(content);
        if (close)
            window.Close();
    }

    /// <summary>Makes the window of every side window listed without one, and shows it; at launch, after the main window's first frame.</summary>
    private void ApplySideWindows()
    {
        if (_shuttingDown || _sides.All(s => s.Window is not null))
            return;

        // A window can only be owned by one already shown: at launch, the side windows follow the main one.
        if (new System.Windows.Interop.WindowInteropHelper(_main).Handle == IntPtr.Zero)
        {
            if (!_sideWaiting)
            {
                _sideWaiting = true;
                _main.ContentRendered += OpenSidesWhenShown;
            }

            return;
        }

        foreach (var host in _sides.Where(s => s.Window is null).ToList())
        {
            MakeWindow(host);
            host.Window!.Show();
        }
    }

    private bool _sideWaiting;

    /// <summary>
    /// The side windows saved open come back after the main window's first frame (Law 1), at Background priority, each made and laid
    /// out on its own before it is shown: the main window is never laid out again for them (its one dock build was before Frame 1).
    /// </summary>
    private void OpenSidesWhenShown(object? sender, EventArgs e)
    {
        _main.ContentRendered -= OpenSidesWhenShown;
        _main.Dispatcher.BeginInvoke(() =>
        {
            _sideWaiting = false;
            if (_shuttingDown)
                return;
            foreach (var host in _sides.Where(s => s.Window is null).ToList())
            {
                MakeWindow(host, activate: false);
                RebuildSide(host);
                host.Window!.Show();
            }

            RaiseSideChanged();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// A side window's window, placed as it was left (or under the pointer), not shown yet; closing it hands its panes back. One
    /// brought back at launch (<paramref name="activate"/> false) leaves the keyboard with the main window as it is shown.
    /// </summary>
    private void MakeWindow(SideHost host, bool activate = true)
    {
        var state = host.State;
        var window = new SideWindow(_main, state.WindowMode) { SideId = state.Id, Number = NumberOf(host), ShowActivated = activate };
        window.Place(state.Placement, state.Bounds, state.Maximized, state.FullScreen, host.AtPointer && state.Placement is null && state.Bounds is null);
        host.AtPointer = false;
        window.ModeRequested += mode => SetSideWindowMode(host.State.Id, mode);

        // Closed by its own button: its panes go back to the main window, now, before its window (and the player's, if it is
        // there) is destroyed. Closed because the app is closing: nothing changes (it is saved open).
        window.Closing += (_, _) =>
        {
            if (_shuttingDown || !ReferenceEquals(host.Window, window))
                return;
            CloseSide(host, closeWindow: false);
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(host.Window, window))
                (host.Window, host.Root) = (null, null);
        };
        host.Window = window;
        SideWindowOpened?.Invoke(window);
    }

    /// <summary>Each side window's number in its title follows the order they are open in; then <see cref="SideChanged"/>.</summary>
    private void RaiseSideChanged()
    {
        foreach (var host in _sides.Where(s => s.Window is not null))
            host.Window!.Number = NumberOf(host);
        SideChanged?.Invoke();
    }

    /// <summary>
    /// The app is closing (the arrangement has been saved): every side window's panes are taken out of it before it goes, so the
    /// player's own window outlives it and nothing it holds is destroyed under it.
    /// </summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        _guide?.Close();
        _guide = null;
        foreach (var window in SideWindows)
        {
            foreach (var content in Movables().Where(c => Window.GetWindow(c) == window).ToList())
                Detach(content);
        }
    }

    /// <summary>Each open side window's place as Windows keeps it (its restored place, maximized, full screen), to be saved.</summary>
    private void RememberPlacements()
    {
        foreach (var host in _sides)
        {
            if (host.Window?.CurrentPlacement() is not { } placed)
                continue;
            (host.State.Placement, host.State.Maximized, host.State.FullScreen) = placed;
            host.State.Bounds = null;
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
