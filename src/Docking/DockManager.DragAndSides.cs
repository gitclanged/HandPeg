using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp.Docking;

// ----- DockManager: tabs, pane dragging, whole layouts and side windows -----
public sealed partial class DockManager
{
    // ----- Tabs -----

    /// <summary>Fills each pane's title slot with its group's tabs, or clears it when a tab row above already shows them.</summary>
    /// <param name="only">Panes to fill (one side window laid out alone); null for all.</param>
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

            // Single line: the active tab always fits; others overflow into the trailing menu first. A lone headerless-when-alone pane
            // (Timeline) gets no title; its hover tab drags it (RenderHover).
            var shown = entry.Group.Panes.Where(p => _panes[p].IsShown()).ToList();
            if (shown.Count == 1 && _panes[pane].HeaderlessAlone)
            {
                (slot.Content, slot.Visibility) = (null, Visibility.Collapsed);
                continue;
            }

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
                var more = new System.Windows.Controls.Primitives.ToggleButton { Content = Glyphs.ChevronDown, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Padding = new Thickness(6, 2, 6, 2), ToolTip = "The other panes of this group" };
                OverflowStrip.SetIsOverflowButton(more, true);
                strip.Children.Add(more);
                strip.MenuEntryFor = child => tabs.TryGetValue(child, out var mate) ? (_panes[mate].Title, () => Activate(mate)) : null;
            }

            (slot.Content, slot.Visibility) = (strip, Visibility.Visible);
        }
    }

    /// <summary>
    /// Builds a pane tab: drag to move, click to activate within its group, right-click for the menu (move window, close).
    /// </summary>
    /// <param name="upright">Vertical hover tab for a pane laid out as a narrow column (side-docked header).</param>
    private FrameworkElement MakeTab(DockNode group, string pane, bool active, bool hover, bool inline = false, bool boxed = false, bool upright = false)
    {
        var info = _panes[pane];
        var text = new TextBlock { Text = info.Title, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement tab;
        if (boxed)
        {
            // Main tab bar: boxed TabItem look, merged with the panel when active, with a 2 px ActiveTabTraceBrush top edge
            // (space reserved on every tab).
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
                // Inactive tabs: theme InactiveTabBrush (transparent in most themes, darker stone in Reno).
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
            // In a title row: the active tab uses the title style, others are muted.
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

        // Transparent background so the whole tab is hit-testable, not just the glyphs.
        if (tab is TextBlock title)
            title.Background = Brushes.Transparent;
        else if (tab is Border { Background: null } plain)
            plain.Background = Brushes.Transparent;

        WireTab(tab, pane, activates: inline || boxed);
        return tab;
    }

    /// <summary>
    /// Wires an element as a pane handle: press-and-move drags, click activates (<paramref name="activates"/>), right-click opens the menu.
    /// </summary>
    private void WireTab(FrameworkElement tab, string pane, bool activates)
    {
        // Show the move cursor only while pressed; otherwise the normal arrow.
        tab.Cursor = null;

        // Capture the mouse on press so a drag that quickly leaves the tab still starts.
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
    /// Activates a pane in its group, in place: no other pane is re-parented (doing so flickered the player on every tab click).
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

            // Titled group: swap the visible pane and refresh the title-row tabs.
            case Border plain:
                plain.Child = null;
                plain.Child = Placed(pane);
                FillTitleSlots();
                PaneShown?.Invoke(pane);
                break;

            // No on-screen host: force a rebuild (the active pane is not part of the layout signature).
            default:
                _builtSignature = null;
                Rebuild();
                break;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Activates a newly enabled pane after the layout requested by <see cref="ScheduleRebuild"/>, in place via <see cref="Activate"/>
    /// (no extra layout). Skipped if the pane is hidden or its group has no on-screen host.
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

        // Targets: a new Pane Docker, another open one (by ordinal), or the main window.
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
    /// Window-move items for a pane's context menu: "Move to New Side Window", "Move to Side Window N" per other open Pane Docker,
    /// and "Move to Main Window" for panes in a side window.
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

    /// <summary>Hook for window-provided tab menu items (pane settings, the Tabs and Panes list).</summary>
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
            // Owned by the main window so it closes with it (the app exits when its last window closes).
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

    /// <summary>Computes and shows the drop target for a screen point (physical pixels).</summary>
    private void UpdateDrag(Point point)
    {
        if (_press is not { } press)
            return;

        _target = null;
        var dpi = VisualTreeHelper.GetDpi(_main).DpiScaleX;
        // Topmost window under the point by Z-order (a docker over the main window wins, and vice versa); Live Video Previews accept no panes.
        if (RootAt(point) is { } at)
        {
            if (at.Host is not { } host)
            {
                _guide!.Hide();
                return;
            }

            var (side, bounds) = (at.Side, at.Bounds);
            // At a window edge: dock beside the whole tree.
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

                // Over a tab row (group's or title's): insert at the indicator line.
                if (TabStripOf(groupHost, group) is { } strip && TabGap(strip, shownPanes, point, dpi) is { } gap)
                {
                    // Adjacent to its own tab: no-op.
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

                // Over the empty part of a tab row: append.
                if (groupHost is TabGroupHost tabs && point.Y - area.Top < (tabs.Strip.ActualHeight + 8) * dpi)
                    zone = DockZone.Center;

                // Onto its own single-pane group or its own tabs: no-op.
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

            // Empty Pane Docker (just switched from preview, or last tab dragged out): its whole centre accepts (Move creates the first
            // group or docks beside what remains). Elsewhere with no group: no drop and clear any stale guide.
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

        // Outside every window: open a new Pane Docker at the drop point.
        _target = (null, null, DockZone.Right, true, null);
        _guide!.ShowAt(new Rect(point.X - 160 * dpi, point.Y - 100 * dpi, 320 * dpi, 200 * dpi), $"{_panes[press.Pane].Title}: in a new side window");
    }

    /// <summary>
    /// Topmost window (WindowOrder.Topmost) under a dragged tab among the main window, Pane Dockers and Live Video Previews: its host,
    /// side window and bounds (host null for a preview, which has no drop root). Null when over none.
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

    /// <summary>A group's on-screen tab row: its own, or its active pane's title row; null for hover-tab panes.</summary>
    private static OverflowStrip? TabStripOf(FrameworkElement host, DockNode group)
    {
        if (host is TabGroupHost tabs)
            return tabs.Strip;

        // Only the active pane is on screen, so its slot is the visible one.
        return host is Border ? group.Panes.Select(PaneTitleSlot.Of).FirstOrDefault(s => s is { Content: OverflowStrip, IsVisible: true })?.Content as OverflowStrip : null;
    }

    /// <summary>
    /// For a point level with a tab row: the insertion index among visible tabs and the indicator line position (screen pixels).
    /// Overflowed tabs (in the "more" menu) are skipped.
    /// </summary>
    private static (int At, Rect Line)? TabGap(OverflowStrip strip, List<string> shown, Point point, double dpi)
    {
        var tabs = strip.Children.OfType<FrameworkElement>().Where(c => !OverflowStrip.GetIsOverflowButton(c)).Take(shown.Count)
            .Select((tab, index) => (Index: index, Rect: ScreenRect(tab))).Where(t => !t.Rect.IsEmpty && t.Rect.Width >= 1).ToList();
        if (tabs.Count == 0)
            return null;

        // Vertically near the row (with slack) and not far past the last tab.
        var (top, bottom) = (tabs.Min(t => t.Rect.Top), tabs.Max(t => t.Rect.Bottom));
        if (point.Y < top - 6 * dpi || point.Y > bottom + 6 * dpi || point.X < tabs[0].Rect.Left - 12 * dpi || point.X > tabs[^1].Rect.Right + 60 * dpi)
            return null;

        // Insert before the first tab whose midpoint is past the point, else after the last.
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
    /// Moves a single pane (group-mates stay): into a group as a tab (at an index or the end), beside a group, or at a window edge.
    /// </summary>
    /// <param name="to">Target Pane Docker; null for the main window.</param>
    /// <param name="at">Insertion index among visible tabs; null to append.</param>
    private void Move(string pane, SideHost? to, DockNode? group, DockZone zone, int? at = null)
    {
        // Within its own group: reorder.
        if (group is not null && group.Panes.Contains(pane) && zone == DockZone.Center && at is { } place)
        {
            Reorder(group, pane, place);
            return;
        }

        // Onto itself: no-op.
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

    /// <summary>Removes a pane from whichever window's tree holds it.</summary>
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
    /// <summary>Maps a visible-tab index to an index in the full pane list (including hidden panes).</summary>
    private int PanesIndex(DockNode group, int at)
    {
        var shown = group.Panes.Where(p => _panes[p].IsShown()).ToList();
        if (at < shown.Count)
            return group.Panes.IndexOf(shown[Math.Max(at, 0)]);
        return shown.Count > 0 ? group.Panes.IndexOf(shown[^1]) + 1 : group.Panes.Count;
    }

    /// <summary>
    /// Reorders a tab within its own row and activates it (hidden panes keep their positions). Only the row is rebuilt; the
    /// order persists with the layout.
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

        // Windows already match the layout: mark it so a rebuild is skipped.
        MarkAllBuilt();
        Changed?.Invoke();
    }

    /// <summary>
    /// Moves a pane to a new Pane Docker when <paramref name="side"/> (at the pointer when <paramref name="atPointer"/>), else back to
    /// its default main-window position.
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

        // Add the pane to the new window's tree before creating it so it never opens empty.
        TakeOut(pane);
        var docker = AddSide(SideWindowMode.PaneDocker, atPointer);
        docker.State.Tree = DockNode.Group(pane);
        ApplySideWindows();
        Rebuild();
        Changed?.Invoke();
        RaiseSideChanged();
    }

    /// <summary>
    /// Moves a pane to open Pane Docker <paramref name="sideId"/> (docked at its right), or with null to its default main-window
    /// position. Previews and closed side windows reject it.
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
    // ----- Whole-layout operations (Layout button: built-in and custom layouts) -----

    /// <summary>A copy of the current layout (for saving as a custom layout or restoring later).</summary>
    public DockLayoutState Current
    {
        get => _state.Copy();
    }

    /// <summary>
    /// Temporarily applies a layout (Layout Selector hover) without saving or touching side windows; the floating player only moves
    /// (<see cref="WindowRoot"/>).
    /// </summary>
    public void Preview(DockLayoutState layout)
    {
        _state = KeepingSideWindow(layout);
        Rebuild();
    }

    /// <summary>
    /// Applies and saves a layout, leaving side windows as they are. A layout already on screen (previewed then kept), or differing
    /// only in shares and active tabs, is adopted in place (RebindOrRebuild) so splitters and tabs edit the new tree.
    /// </summary>
    public void Apply(DockLayoutState layout)
    {
        _state = KeepingSideWindow(layout);
        RebindOrRebuild();
        Changed?.Invoke();
    }

    /// <summary>A layout normalised as Apply would (unknown panes dropped, missing homed, side windows kept), for preview and thumbnails.</summary>
    public DockLayoutState Prepared(DockLayoutState layout) => KeepingSideWindow(layout);

    /// <summary>
    /// Whether two layouts have the same main-window structure (pane grouping and split orientation), ignoring shares, active tabs
    /// and tab order, so the Layout Selector's current-layout mark survives splitter and tab drags.
    /// </summary>
    public static bool SameShape(DockLayoutState a, DockLayoutState b) => Shape(a.Main) == Shape(b.Main);

    /// <summary>Canonical shape string compared by SameShape (group panes sorted by name, split orientation); printed by golden dock-standard-layout.</summary>
    internal static string Shape(DockNode node) => node.IsGroup
        ? "[" + string.Join(",", node.Panes.Order(StringComparer.Ordinal)) + "]"
        : (node.Vertical ? "V(" : "H(") + string.Join(";", node.Children.Select(Shape)) + ")";

    /// <summary>
    /// Adapts a main-window layout to the current side windows: panes held by Pane Dockers are removed from it, so applying a
    /// layout never opens, closes or empties a docker (side windows are persisted separately).
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
    /// Layout Selector thumbnail: rounded boxes labelled by visible panes (empty parts collapse), content-sized rows (scrubber,
    /// playback controls) as thin bands, and the header as a mark at the end of its joined row. Rendered once and frozen.
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

                // Visible parts: content-sized ones get a fixed band, the rest share the remainder.
                var parts = node.Children.Select((child, i) => (Child: child, Size: i < node.Sizes.Count && node.Sizes[i] > 0 ? node.Sizes[i] : 1))
                    .Where(p => p.Child.AllPanes().Any(id => _panes.ContainsKey(id) && _panes[id].IsShown())).ToList();

                // Vertical split: the header joins the part below when allowed (drawn as a mark there).
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

    // ----- Side windows (any number; Live Video Preview or Pane Docker) -----

    /// <summary>Registers a new side window's state (ApplySideWindows creates the window).</summary>
    private SideHost AddSide(SideWindowMode mode, bool atPointer)
    {
        var state = new SideWindowState { Id = SideWindowStates.NewId(_sides.Select(s => s.State)), Mode = SideWindowState.ModeOf(mode) };
        var host = new SideHost(state) { AtPointer = atPointer };
        _sides.Add(host);
        return host;
    }

    /// <summary>
    /// Opens a side window (Live Video Preview from "+ New Side Window", or an empty Pane Docker), laid out alone without relaying
    /// the main window. Returns its window, or null before the main window shows (it opens then).
    /// </summary>
    /// <param name="atPointer">Open at the pointer's monitor position instead of over the main window.</param>
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

    /// <summary>Closes a side window; a Pane Docker's panes return to their default main-window positions.</summary>
    public void CloseSideWindow(string id)
    {
        if (_sides.FirstOrDefault(s => s.State.Id == id) is { } host)
            CloseSide(host, closeWindow: true);
    }

    /// <summary>Id of the side window holding a pane (used to bring Color Studio forward); null for the main window or none.</summary>
    public string? SideIdOf(string pane) => Find(pane).Side?.State.Id;

    /// <summary>Whether a pane's group currently has an on-screen host (so <see cref="BringForward"/> can show it).</summary>
    public bool IsInShownGroup(string pane) => GroupAnywhere(pane) is { } group && _groupHosts.Any(g => ReferenceEquals(g.Group, group));

    /// <summary>Activates a side window (Layout Selector row), restoring it if minimized.</summary>
    public void BringSideWindowForward(string id)
    {
        if (_sides.FirstOrDefault(s => s.State.Id == id)?.Window is not { } window)
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Toggles a side window between Live Video Preview and Pane Docker (title bar choice). Docker -> preview returns its panes to
    /// the main window; preview -> docker starts empty.
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
    /// Removes a side window (close button, Layout Selector, load without it): returns its panes to the main window before its HWND
    /// (and any hosted player HWND) is destroyed. <paramref name="closeWindow"/> is false when already closing.
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

    /// <summary>Detaches all dock content from a side window (so none is destroyed with it), then closes it.</summary>
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

    /// <summary>Creates and shows windows for listed side windows lacking one (at launch, after Frame 1).</summary>
    private void ApplySideWindows()
    {
        if (_shuttingDown || _sides.All(s => s.Window is not null))
            return;

        // An owner must already be shown, so at launch side windows wait for the main window.
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
    /// Restores saved side windows after Frame 1 at Background priority (Law 1), each created and laid out alone before showing;
    /// the main window keeps its single pre-Frame-1 dock build.
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
    /// Creates (unshown) a side window at its saved bounds or the pointer; closing returns its panes. Launch restores pass
    /// <paramref name="activate"/> false so keyboard focus stays in the main window.
    /// </summary>
    private void MakeWindow(SideHost host, bool activate = true)
    {
        var state = host.State;
        var window = new SideWindow(_main, state.WindowMode) { SideId = state.Id, Number = NumberOf(host), ShowActivated = activate };
        window.Place(state.Placement, state.Bounds, state.Maximized, state.FullScreen, host.AtPointer && state.Placement is null && state.Bounds is null);
        host.AtPointer = false;
        window.ModeRequested += mode => SetSideWindowMode(host.State.Id, mode);

        // User close: return panes before the HWND (and any player HWND) is destroyed. App shutdown: leave as-is (saved open).
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

    /// <summary>Renumbers side window titles by open order, then raises <see cref="SideChanged"/>.</summary>
    private void RaiseSideChanged()
    {
        foreach (var host in _sides.Where(s => s.Window is not null))
            host.Window!.Number = NumberOf(host);
        SideChanged?.Invoke();
    }

    /// <summary>
    /// On app shutdown (after saving the layout), detaches every side window's panes so the player HWND and other content outlive it.
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

    /// <summary>Captures each open side window's placement (restored bounds, maximized, full screen) for saving.</summary>
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

}
