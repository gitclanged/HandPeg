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

    // A tab chosen from the keyboard: its strip is made again, and the new tab of that pane takes the focus.
    private string? _focusTabOf;
    private bool _dragging;

    // Where a dragged pane goes if let go now: a group (or a window's edge), the side of it, and, among a row of tabs,
    // the place there (counted among the tabs shown).
    private (bool Side, DockNode? Group, DockZone Zone, bool Outside, int? At)? _target;

    // The shape each pane that can change it was given at the last layout (see DockPane.Reshape).
    private readonly Dictionary<string, PaneShape> _shapes = [];

    // The pane joined to the row of tabs of the pane under it (the header): its bar there, and the group under it.
    private (string Pane, FrameworkElement Bar, DockNode Under)? _joined;

    // The group being laid out right under a pane that joins it (the header): it is given a row of boxed tabs for it.
    private DockNode? _joinUnder;

    // Room kept beside a joined pane and the name of the tab shown in the row it joined: the boxed tab's padding and
    // margin, and the strip's "more" button. The group's least width grows by it.
    private const double JoinedTabRoom = 70;

    // A pane that can change its shape and was laid out as a row stands as a column in a slot narrower than this.
    private const double RowLeastWidth = 180;

    // The slots of the panes laid out as a row that can change their shape, watched while they are there.
    private readonly List<(FrameworkElement Slot, SizeChangedEventHandler Handler)> _shapeWatches = [];

    // What each window's panes are laid out in (see WindowRoot), and the panes that float there (DockPane.Floats): each
    // one's stand-in in the tree and the box on the layer holding its content.
    private readonly WindowRoot _mainRoot = new();
    private WindowRoot? _sideRoot;
    private readonly Dictionary<string, (Border StandIn, Grid Box)> _floating = [];

    public DockManager(Window main, ContentControl mainHost, Func<DockLayoutState> defaults)
    {
        (_main, _mainHost, _defaults) = (main, mainHost, defaults);
        _mainHost.Content = _mainRoot;
        _main.PreviewKeyDown += (_, e) => CancelDragOnEscape(e);
    }

    /// <summary>Raised when the arrangement has changed, to be saved.</summary>
    public event Action? Changed;

    /// <summary>Raised when the side window has opened or closed.</summary>
    public event Action? SideChanged;

    /// <summary>Raised when a pane has been put on screen (made, the first time), by its name.</summary>
    public event Action<string>? PaneShown;

    /// <summary>
    /// Panes that took the place of others, by the old name: a saved arrangement that still names the old one (and not
    /// the new) has the new one put in its place, as it was there, instead of losing it and placing the new one apart.
    /// </summary>
    public IReadOnlyDictionary<string, string> Replaced { get; init; } = new Dictionary<string, string>();

    /// <summary>Raised when a saved arrangement had a pane put in the place of an old one (see <see cref="Replaced"/>): old name, new name.</summary>
    public event Action<string, string>? PaneReplaced;

    /// <summary>
    /// Brings an arrangement made by an older HandPeg up to this one (DockLayoutState.Version), before anything else is done
    /// to it: every arrangement taken up passes through it (the one saved, a built-in layout, one of the user's own, the
    /// first run's choice). It leaves an arrangement that is up to date as it is, and is never a reason to save: the
    /// arrangement is saved as it is the next time the user changes it (4.4: Load writes no settings before the first frame).
    /// </summary>
    public Func<DockLayoutState, DockLayoutState>? Upgrade { get; init; }

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

    /// <summary>
    /// What stands for a pane in the tree: its content, or, for a pane that floats (holds a native window), its stand-in,
    /// with the content in a box on the window's layer, kept over the stand-in (see <see cref="WindowRoot"/>).
    /// </summary>
    private FrameworkElement Placed(string id)
    {
        var content = Realize(id);
        if (!_panes[id].Floats)
            return content;

        if (!_floating.TryGetValue(id, out var floating))
        {
            var standIn = new Border();
            var box = new Grid { ClipToBounds = true, Tag = standIn, Visibility = Visibility.Collapsed };
            box.Children.Add(content);
            _floating[id] = floating = (standIn, box);
        }

        return floating.StandIn;
    }

    /// <summary>What is taken out of a window with a pane: its content, or the box on the layer holding it (a floating pane's).</summary>
    private IEnumerable<FrameworkElement> Movables() => _made.Select(m => _floating.TryGetValue(m.Key, out var floating) ? floating.Box : m.Value);

    /// <summary>
    /// What a window's panes are laid out in: their tree, and over it a layer for the panes that float (hold a native
    /// window: the player), each kept exactly over its stand-in in the tree and hidden while that cannot be seen. A
    /// floating pane stays on its window's layer whatever the tree becomes, so laying the panes out again only moves it.
    /// </summary>
    private sealed class WindowRoot : Grid
    {
        public WindowRoot()
        {
            Children.Add(Tree);
            Children.Add(Layer);
            Layer.LayoutUpdated += (_, _) => Follow();
        }

        public Border Tree { get; } = new();

        public Canvas Layer { get; } = new();

        // After each layout pass: every box where its stand-in is now. Set only when it moved, so the pass that brings is the last.
        private void Follow()
        {
            foreach (var box in Layer.Children.OfType<FrameworkElement>())
            {
                if (box.Tag is not FrameworkElement standIn)
                    continue;

                if (!standIn.IsVisible || !standIn.IsDescendantOf(this))
                {
                    if (box.Visibility != Visibility.Collapsed)
                        box.Visibility = Visibility.Collapsed;
                    continue;
                }

                // On whole pixels of the screen, as the panes around it are laid out.
                var place = standIn.TransformToAncestor(this).TransformBounds(new Rect(standIn.RenderSize));
                var dpi = VisualTreeHelper.GetDpi(this);
                place = new Rect(Math.Round(place.X * dpi.DpiScaleX) / dpi.DpiScaleX, Math.Round(place.Y * dpi.DpiScaleY) / dpi.DpiScaleY,
                    Math.Round(place.Width * dpi.DpiScaleX) / dpi.DpiScaleX, Math.Round(place.Height * dpi.DpiScaleY) / dpi.DpiScaleY);
                if (box.Visibility != Visibility.Visible)
                    box.Visibility = Visibility.Visible;
                if (Canvas.GetLeft(box) != place.X || Canvas.GetTop(box) != place.Y)
                {
                    Canvas.SetLeft(box, place.X);
                    Canvas.SetTop(box, place.Y);
                }

                if (box.Width != place.Width || box.Height != place.Height)
                    (box.Width, box.Height) = (place.Width, place.Height);
            }
        }
    }

    // ----- The arrangement as saved -----

    /// <summary>Takes up a saved arrangement (null or unreadable: the mode's own), puts in what it lacks, and lays the windows out.</summary>
    public void Load(string? saved)
    {
        _state = Validate(DockLayoutState.Read(saved) ?? _defaults());
        ApplySideWindow();
        if (Signature() == _builtSignature)
            Rebind();
        else
            Rebuild();
    }

    /// <summary>
    /// The arrangement taken up again is the one laid out (a copy of it, read back from the settings): the windows stay
    /// as they are. The hosts are told which groups of the new tree they show, and a group that should show another
    /// pane shows it.
    /// </summary>
    private void Rebind()
    {
        for (var i = 0; i < _groupHosts.Count; i++)
        {
            var (host, old, side) = _groupHosts[i];
            if (old.Panes.FirstOrDefault() is not { } first || (side ? _state.Side : _state.Main)?.GroupOf(first) is not { } fresh)
                continue;

            var wanted = fresh.Active;
            fresh.Active = old.Active;
            _groupHosts[i] = (host, fresh, side);
            if (_joined is { } joined && ReferenceEquals(joined.Under, old))
                _joined = joined with { Under = fresh };
            if (host is TabGroupHost tabs)
                tabs.Group = fresh;
            if (wanted is not null && wanted != old.Active && _panes.TryGetValue(wanted, out var pane) && pane.IsShown())
                Activate(wanted);
            else
                fresh.Active = wanted;
        }
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
        // An older arrangement first takes the shape this HandPeg gives it (panes retired, their successors in their place).
        if (Upgrade is { } upgrade)
            state = upgrade(state);

        // A pane that replaced another goes where the old one was (the Media pane where the Source pane was, from before
        // Pass 1.5), unless the arrangement has it already.
        foreach (var (old, replacement) in Replaced)
        {
            var groups = new[] { state.Main, state.Side, state.SideClosed }.OfType<DockNode>().SelectMany(n => n.AllGroups()).ToList();
            if (!_panes.ContainsKey(replacement) || groups.Any(g => g.Panes.Contains(replacement)) || groups.FirstOrDefault(g => g.Panes.Contains(old)) is not { } home)
                continue;

            home.Panes[home.Panes.IndexOf(old)] = replacement;
            if (home.Active == old)
                home.Active = replacement;
            PaneReplaced?.Invoke(old, replacement);
        }

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
        if (Services.StartupTrace.IsOn)
            Services.StartupTrace.Layout("dock rebuilt, showing " + string.Join(", ", _panes.Values.Where(p => p.IsShown()).Select(p => p.Id)));
        _groupHosts.Clear();
        _roots.Clear();
        _shapes.Clear();
        foreach (var (slot, handler) in _shapeWatches)
            slot.SizeChanged -= handler;
        _shapeWatches.Clear();

        // Every pane out of where it was, and shown: a pane hidden behind another's tab stayed hidden in a place of its
        // own (dragging Video out of "Media | Video" left Media's spot, and the header joined to it, empty). Floating
        // panes stay on their layer; only their stand-ins move.
        foreach (var (id, content) in _made)
        {
            if (_floating.ContainsKey(id))
                continue;
            Detach(content);
            content.Visibility = Visibility.Visible;
        }

        foreach (var (standIn, box) in _floating.Values)
        {
            Detach(standIn);
            standIn.Visibility = Visibility.Visible;
            RemoveHoverZones(box);
        }

        if (_joined is { Bar: var bar })
            Detach(bar);
        _joined = null;
        foreach (var pane in _panes.Values.Where(p => p.HoverHost is not null))
            RemoveHoverZones(pane.HoverHost!);

        _mainRoot.Tree.Child = Render(_state.Main, side: false);
        _roots.Add((_mainHost, false));

        if (_side is { } window)
        {
            if (_sideRoot is null || !ReferenceEquals(window.Host.Content, _sideRoot))
                window.Host.Content = _sideRoot = new WindowRoot();
            var side = _state.Side is { } tree ? Render(tree, side: true) : null;
            _sideRoot.Tree.Child = side;
            window.ShowHint(side is null);
            _roots.Add((window.Host, true));
        }

        FillTitleSlots();
        PlaceFloating();
        WatchShapes();
    }

    /// <summary>Puts each floating pane's box on the layer of the window its stand-in is in now; one not laid out stays where it is, hidden.</summary>
    private void PlaceFloating()
    {
        foreach (var (standIn, box) in _floating.Values)
        {
            var root = standIn.IsDescendantOf(_mainRoot) ? _mainRoot : _sideRoot is { } side && standIn.IsDescendantOf(side) ? side : null;
            if (root is null)
            {
                box.Visibility = Visibility.Collapsed;
                continue;
            }

            if (!ReferenceEquals(box.Parent, root.Layer))
            {
                Detach(box);
                root.Layer.Children.Add(box);
            }
        }
    }

    /// <summary>
    /// Watches the slot of each pane that can change its shape and was laid out as a row (the header among tabs, alone
    /// in a window, or over nothing it can join): in a slot taller than wide, or too narrow for a row, it stands as a
    /// column, and as a row again once the slot is wide enough.
    /// </summary>
    private void WatchShapes()
    {
        foreach (var (pane, shape) in _shapes.ToList())
        {
            if (shape != PaneShape.Row || _panes[pane].Reshape is null || !_made.TryGetValue(pane, out var content)
                || VisualTreeHelper.GetParent(content) is not FrameworkElement slot)
                continue;

            SizeChangedEventHandler handler = (_, e) => FitShape(pane, slot, e.NewSize);
            slot.SizeChanged += handler;
            _shapeWatches.Add((slot, handler));
        }
    }

    private void FitShape(string pane, FrameworkElement slot, Size size)
    {
        var shape = size.Height > size.Width || size.Width < RowLeastWidth ? PaneShape.Column : PaneShape.Row;
        if (_shapes.GetValueOrDefault(pane) == shape)
            return;

        SetShape(pane, shape);

        // Its hover tab reads the way the pane stands now.
        if (slot is Panel host && host.Children.OfType<FrameworkElement>().Any(c => c.Tag as string == "dock-hover")
            && _groupHosts.FirstOrDefault(g => g.Group.Panes.Contains(pane)).Group is { } group)
        {
            RemoveHoverZones(host);
            host.Children.Add(HoverZone(group, pane));
        }
    }

    // What the windows were last laid out from: the arrangement, which panes are shown, and the side window.
    private string? _builtSignature;

    // Which pane of a group is shown is not part of it: that is changed in place (see Activate), not by a rebuild.
    private string Signature() => ActiveAttribute.Replace(JsonSerializer.Serialize(_state), "") + "|" + string.Join(",", _panes.Values.Where(p => p.IsShown()).Select(p => p.Id))
        + "|" + (_side?.GetHashCode() ?? 0);

    private static readonly System.Text.RegularExpressions.Regex ActiveAttribute = new("\"Active\":(\"(?:[^\"\\\\]|\\\\.)*\"|null),?", System.Text.RegularExpressions.RegexOptions.Compiled);

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
        foreach (var pane in shown.Where(p => !_shapes.ContainsKey(p)))
            SetShape(pane, PaneShape.Row);

        // Right under the header, which joins it: a row of boxed tabs, as the main tab bar has, with the header at its end.
        var joined = ReferenceEquals(node, _joinUnder);
        var host = joined || shown.Any(p => _panes[p].Handle == PaneHandle.Tab) || (shown.Count > 1 && shown.Any(p => _panes[p].Handle == PaneHandle.Hover))
            ? RenderTabs(node, shown, active, side, joined)
            : shown.Count == 1 && _panes[active].Handle == PaneHandle.Hover
                ? RenderHover(node, active)
                : RenderPlain(active);
        _groupHosts.Add((host, node, side));
        PaneShown?.Invoke(active);
        return host;
    }

    // A group whose panes have titles: the one shown, with the tabs in its title's place (see FillTitleSlots).
    private FrameworkElement RenderPlain(string active) => new Border { Child = Placed(active), Background = Brushes.Transparent };

    /// <summary>A pane with no title of its own: a tab with its name comes up over its top-left corner when the pointer goes there.</summary>
    private FrameworkElement RenderHover(DockNode group, string pane)
    {
        var host = new Grid { Background = Brushes.Transparent };
        host.Children.Add(Placed(pane));

        // A floating pane's lies over it, on the layer, with it.
        (_floating.TryGetValue(pane, out var floating) ? floating.Box : host).Children.Add(HoverZone(group, pane));

        // A pane under a native window (the player) has its zone on the window kept over that one too: the zone in the
        // pane itself is only there to be used while the native window is hidden (nothing loaded).
        if (_panes[pane].HoverHost is { } overlay)
            overlay.Children.Add(HoverZone(group, pane));
        return host;
    }

    private static readonly Brush ZoneBrush = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    private static void RemoveHoverZones(Panel host)
    {
        foreach (var zone in host.Children.OfType<FrameworkElement>().Where(c => c.Tag as string == "dock-hover").ToList())
            host.Children.Remove(zone);
    }

    /// <summary>
    /// A strip along the top-left of a pane, all but invisible, that shows the pane's tab while the pointer is on it. The
    /// strip is exactly as wide as the tab it grows into, so the tab, once up, is still under the pointer: a wider strip
    /// lost the pointer as its tab came up (narrower than it), took it back as the tab went, and so on, many times a
    /// second, flickering the tab and whatever the pointer was over beneath it (the header's Add Media flyout).
    /// </summary>
    private FrameworkElement HoverZone(DockNode group, string pane)
    {
        var upright = _shapes.GetValueOrDefault(pane) == PaneShape.Column;
        var tab = MakeTab(group, pane, active: true, hover: true, upright: upright);
        tab.Visibility = Visibility.Collapsed;
        var strip = HoverTabWidth(_panes[pane].Title, upright);
        var zone = new Border
        {
            Tag = "dock-hover", Background = ZoneBrush, Height = 7, Width = strip,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = tab,
        };
        Panel.SetZIndex(zone, 1000);
        zone.MouseEnter += (_, e) =>
        {
            tab.Visibility = Visibility.Visible;
            tab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            strip = Math.Ceiling(tab.DesiredSize.Width);
            if (e.GetPosition(zone).X > strip)
            {
                // Past where the tab would be (the estimate was wide): the strip takes the tab's width, and nothing comes up.
                tab.Visibility = Visibility.Collapsed;
                zone.Width = strip;
                return;
            }

            zone.Height = double.NaN;
            zone.Width = double.NaN;
        };
        zone.MouseLeave += (_, _) =>
        {
            if (_dragging)
                return;
            tab.Visibility = Visibility.Collapsed;
            (zone.Height, zone.Width) = (7, strip);
        };
        return zone;
    }

    /// <summary>How wide a hover tab is (its words in the window's type, and MakeTab's padding and edge), before it is first shown.</summary>
    private double HoverTabWidth(string title, bool upright)
    {
        var words = new FormattedText(title, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_main.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11.5, Brushes.Black,
            VisualTreeHelper.GetDpi(_main).PixelsPerDip);
        return Math.Ceiling(upright ? words.Height + 6 : words.WidthIncludingTrailingWhitespace + 19);
    }

    /// <summary>
    /// A group shown with a row of tabs above it (the settings tabs, any group with a pane that has no title of its own,
    /// and the group the header joins): its tabs on one line (those that do not fit are listed in a menu at its end),
    /// and below them every pane of the group made so far, kept in one grid with only the one shown visible. Switching
    /// tabs then shows one and hides another, and moves nothing: a player among them keeps its window where it is (a
    /// tab control would take the content of every other tab out of the window, and the player's with it).
    /// </summary>
    private sealed class TabGroupHost : DockPanel
    {
        public TabGroupHost(DockNode group, bool classic)
        {
            (Group, Classic) = (group, classic);
            Strip = new OverflowStrip();
            Body = new Grid { Margin = classic ? new Thickness(0) : new Thickness(10, 4, 10, 6) };

            // The row of tabs, with room at its right-hand end for a pane joined to it (the header right above it).
            Trailing = new Border { VerticalAlignment = classic ? VerticalAlignment.Bottom : VerticalAlignment.Center, Margin = classic ? new Thickness(0, 0, 0, 4) : new Thickness(0) };
            var head = new DockPanel { Margin = classic ? new Thickness(0) : new Thickness(12, 6, 12, 2) };
            DockPanel.SetDock(Trailing, Dock.Right);
            head.Children.Add(Trailing);
            head.Children.Add(Strip);
            SetDock(head, Dock.Top);
            Children.Add(head);
            if (classic)
            {
                // Boxed tabs: the panel the shown tab joins, as the settings tabs have always looked in the main tab bar.
                Frame = new Border { Child = Body, CornerRadius = new CornerRadius(0, 6, 6, 6), Padding = TabPadding };
                Frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
                Children.Add(Frame);
            }
            else
            {
                Children.Add(Body);
            }
        }

        /// <summary>The panel's padding around a settings tab, as the tab bar has always had. A pane that is a panel of its own fills it.</summary>
        public static readonly Thickness TabPadding = new(16, 8, 16, 8);

        public DockNode Group { get; set; }

        /// <summary>Whether its tabs are boxed over a panel (the main tab bar, and the group the header joins) rather than compact inline tabs.</summary>
        public bool Classic { get; }

        public OverflowStrip Strip { get; }

        /// <summary>At the right-hand end of the row of tabs: where a joined pane's bar goes (see <see cref="PaneShape.Joined"/>).</summary>
        public Border Trailing { get; }

        /// <summary>The panel under boxed tabs; null for inline ones.</summary>
        public Border? Frame { get; }

        public Grid Body { get; }
        public Dictionary<string, FrameworkElement> Shown { get; } = [];
    }

    /// <summary>
    /// The main tab bar: the group of the main window holding the most settings tabs (where the mode starts them). Its
    /// tabs keep the classic boxed look, as do the tabs of the group the header joins; every other group of tabs, a
    /// settings tab dragged out of it included, has the compact inline ones.
    /// </summary>
    private DockNode? MainTabBar() => _state.Main.AllGroups()
        .Select(g => (Group: g, Count: g.Panes.Count(p => _panes[p].Handle == PaneHandle.Tab && _panes[p].IsShown())))
        .Where(g => g.Count > 0).OrderByDescending(g => g.Count).Select(g => g.Group).FirstOrDefault();

    /// <param name="joined">Laid out right under the header, which joins its row of tabs: boxed tabs, wherever it is.</param>
    private FrameworkElement RenderTabs(DockNode group, List<string> shown, string active, bool side, bool joined = false)
    {
        var host = new TabGroupHost(group, classic: joined || (!side && ReferenceEquals(group, MainTabBar())));
        foreach (var pane in shown.Where(p => p == active || IsRealized(p)))
            AddToTabs(host, pane);
        ShowInTabs(host, shown, active);
        return host;
    }

    private void AddToTabs(TabGroupHost host, string pane)
    {
        var content = Placed(pane);
        host.Body.Children.Add(content);
        host.Shown[pane] = content;
    }

    /// <summary>Shows one pane of a tab group: made and added when it was not yet, the others hidden; its tab marked as the one shown.</summary>
    private void ShowInTabs(TabGroupHost host, List<string> shown, string active)
    {
        if (!host.Shown.ContainsKey(active))
            AddToTabs(host, active);
        foreach (var (pane, content) in host.Shown)
            content.Visibility = pane == active ? Visibility.Visible : Visibility.Collapsed;

        // Under boxed tabs, a settings tab has the panel's padding; a pane that is a panel of its own (Media, Keyframes...)
        // fills the panel, in its colour, so the two read as one.
        if (host.Frame is { } frame)
            frame.Padding = _panes[active].Handle == PaneHandle.Tab ? TabGroupHost.TabPadding : new Thickness(0);

        var tabs = new Dictionary<UIElement, string>();
        host.Strip.Children.Clear();
        foreach (var pane in shown)
        {
            var tab = MakeTab(host.Group, pane, active: pane == active, hover: false, inline: !host.Classic, boxed: host.Classic);
            System.Windows.Automation.AutomationProperties.SetName(tab, _panes[pane].Title);
            OverflowStrip.SetPriority(tab, pane == active ? 100 : 0);
            tab.VerticalAlignment = host.Classic ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            host.Strip.Children.Add(tab);
            tabs[tab] = pane;
        }

        var more = new System.Windows.Controls.Primitives.ToggleButton { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Padding = new Thickness(6, 2, 6, 2), ToolTip = "The other panes of this group" };
        OverflowStrip.SetIsOverflowButton(more, true);
        host.Strip.Children.Add(more);
        host.Strip.MenuEntryFor = child => tabs.TryGetValue(child, out var pane) ? (_panes[pane].Title, () => Activate(pane)) : null;
        PaneShown?.Invoke(active);
    }

    /// <summary>
    /// A split: its parts side by side (or over each other), each with its share of the room, and borders between them to
    /// drag. Side by side, a pane that can stand as a narrow column (the header) always does, as wide as its content
    /// (never a row of buttons stretched to the part's height); over each other, a pane that joins the pane under it (the
    /// header) goes at the end of that pane's row of tabs, boxed as the main tab bar's are, when the part right under it
    /// has tabs or titles, and is a row of its own otherwise.
    /// </summary>
    private FrameworkElement? RenderSplit(DockNode split, bool side)
    {
        var parts = new List<(FrameworkElement Element, int Index, bool Fits)>();
        (int Index, string Pane)? waiting = null;
        void RenderWaiting()
        {
            if (waiting is { } wait && Render(split.Children[wait.Index], side) is { } own)
                parts.Add((own, wait.Index, FitsContent(split.Children[wait.Index])));
            waiting = null;
        }

        for (var i = 0; i < split.Children.Count; i++)
        {
            var child = split.Children[i];
            var alone = Alone(child);
            if (split.Vertical && waiting is null && alone is { } joining && _panes[joining].JoinsPaneBelow)
            {
                waiting = (i, joining);
                continue;
            }

            var column = !split.Vertical && AloneIn(child) is { } upright && _panes[upright].Reshape is not null;
            if (column)
                SetShape(AloneIn(child)!, PaneShape.Column);

            var joinable = waiting is not null && CanJoin(child);
            _joinUnder = joinable ? child : null;
            var element = Render(child, side);
            _joinUnder = null;
            if (element is null)
                continue;

            if (waiting is { } wait && !(joinable && element is TabGroupHost tabs && JoinTo(tabs, child, wait.Pane)))
                RenderWaiting();
            waiting = null;
            parts.Add((element, i, split.Vertical ? FitsContent(child) : column));
        }

        RenderWaiting();
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
    /// its content (the overview bar, the playback controls) it shares the room between the nearest parts above and
    /// below that are not (4.1.1: the player's row and the tabs; the timeline keeps the height its setting gives it);
    /// with no such part on one side it is only a gap, or, beside a pane that can be made taller, it makes it so.
    /// </summary>
    private void AddBorder(Grid grid, DockNode split, List<(FrameworkElement Element, int Index, bool Fits)> parts, int k)
    {
        var (before, after) = (parts[k - 1], parts[k]);
        UIElement border;
        var (above, under) = (parts.Take(k).LastOrDefault(p => !p.Fits).Element, parts.Skip(k).FirstOrDefault(p => !p.Fits).Element);
        if ((before.Fits || after.Fits) && split.Vertical && above is not null && under is not null)
        {
            // Ctrl held (4.2B-2): the pane beside it that fits its content is made taller or shorter instead (the overview
            // bar, through its height setting), dragged away from it or towards it.
            var (fitting, below) = before.Fits ? (split.Children[before.Index], false) : (split.Children[after.Index], true);
            var resize = fitting.AllPanes().Select(p => _panes[p].ResizeBy).FirstOrDefault(r => r is not null);
            var thumb = new Thumb
            {
                Height = 10, Cursor = Cursors.SizeNS, Template = SplitterLook(),
                ToolTip = resize is null ? "Drag to share the room between the parts above and below"
                    : "Drag to share the room between the parts above and below; Ctrl+drag to make the overview bar taller or shorter",
            };
            thumb.DragDelta += (_, e) =>
            {
                if (resize is not null && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    resize(below ? -e.VerticalChange : e.VerticalChange);
                else
                    ShareRows(grid, parts, above, under, e.VerticalChange);
            };
            thumb.DragCompleted += (_, _) => TakeSizes(grid, split, parts);
            border = thumb;
        }
        else if (before.Fits || after.Fits)
        {
            var (fitting, below) = before.Fits ? (split.Children[before.Index], false) : (split.Children[after.Index], true);
            var resize = split.Vertical ? fitting.AllPanes().Select(p => _panes[p].ResizeBy).FirstOrDefault(r => r is not null) : null;
            if (resize is null)
            {
                border = split.Vertical ? new Border { Height = 8 } : new Border { Width = 8 };
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

    /// <summary>
    /// A border beside a part as tall as its content, dragged by <paramref name="change"/> pixels: the part above it gains
    /// what the part under it gives up, each kept to its least height. The shares of every part with a share are first
    /// put in pixels (as TakeSizes keeps them), so the parts not dragged keep their size.
    /// </summary>
    private static void ShareRows(Grid grid, List<(FrameworkElement Element, int Index, bool Fits)> parts, FrameworkElement above, FrameworkElement under, double change)
    {
        foreach (var (element, _, fits) in parts)
        {
            if (!fits)
                grid.RowDefinitions[Grid.GetRow(element)].Height = new GridLength(Math.Max(grid.RowDefinitions[Grid.GetRow(element)].ActualHeight, 1), GridUnitType.Star);
        }

        var (top, bottom) = (grid.RowDefinitions[Grid.GetRow(above)], grid.RowDefinitions[Grid.GetRow(under)]);
        var (least, most) = (top.MinHeight - top.ActualHeight, bottom.ActualHeight - bottom.MinHeight);
        if (least > most)
            return;

        var moved = Math.Clamp(change, least, most);
        top.Height = new GridLength(Math.Max(top.ActualHeight + moved, 1), GridUnitType.Star);
        bottom.Height = new GridLength(Math.Max(bottom.ActualHeight - moved, 1), GridUnitType.Star);
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

    // A group with a pane joined to its title row is never narrower than the joined bar and the tab shown beside it.
    private double MinWidthOf(DockNode node) => node.IsGroup
        ? Math.Max(node.Panes.Where(p => _panes[p].IsShown()).Select(p => _panes[p].MinWidth()).DefaultIfEmpty(0).Max(),
            _joined is { } joined && ReferenceEquals(joined.Under, node) ? _joinedWidth : 0)
        : node.Vertical ? node.Children.Select(MinWidthOf).DefaultIfEmpty(0).Max() : node.Children.Sum(MinWidthOf);

    // The least width of the group a pane is joined to: the joined bar, the tab shown, and the row's margins.
    private double _joinedWidth;

    /// <summary>The one pane a part shows, when it is a group showing only one; null otherwise.</summary>
    private string? Alone(DockNode node) =>
        node.IsGroup && node.Panes.Where(p => _panes[p].IsShown()).Take(2).ToList() is [var only] ? only : null;

    /// <summary>The one pane a part shows, a group or a split (whose other panes are all hidden); null when it shows none or several.</summary>
    private string? AloneIn(DockNode node) => node.AllPanes().Where(p => _panes[p].IsShown()).Take(2).ToList() is [var only] ? only : null;

    /// <summary>
    /// Whether a part right under the header can have it joined to its row of tabs: a group whose panes are shown with
    /// tabs, or all have titles (in a title row). A pane with no title of its own alone there (the player) cannot.
    /// </summary>
    private bool CanJoin(DockNode node)
    {
        if (!node.IsGroup || node.Panes.Where(p => _panes[p].IsShown()).ToList() is not { Count: > 0 } shown)
            return false;

        return shown.Any(p => _panes[p].Handle == PaneHandle.Tab) || (shown.Count > 1 && shown.Any(p => _panes[p].Handle == PaneHandle.Hover))
            || shown.All(p => PaneTitleSlot.Of(p)?.Parent is PaneTitleRow);
    }

    /// <summary>Gives a pane that can change its shape the one it has in this layout (see <see cref="DockPane.Reshape"/>).</summary>
    private void SetShape(string pane, PaneShape shape)
    {
        _shapes[pane] = shape;
        _panes[pane].Reshape?.Invoke(shape);
    }

    /// <summary>
    /// Puts a pane that joins the pane under it (the header) at the right-hand end of the row of boxed tabs the part just
    /// laid out under it was given (see <see cref="CanJoin"/>): the two headers are one bar, the pane's own title row
    /// (its controls) inside its panel under it.
    /// </summary>
    private bool JoinTo(TabGroupHost tabs, DockNode under, string pane)
    {
        SetShape(pane, PaneShape.Joined);
        var bar = JoinedBar(pane);
        tabs.Trailing.Child = bar;
        _joined = (pane, bar, under);

        // Room for the bar whole, any of its tabs in full when it is the one shown (the widest: choosing another tab
        // never leaves the shown one no room, only the strip's "more" button), and the margins either side.
        var shown = under.Panes.Where(p => _panes[p].IsShown()).ToList();
        bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _joinedWidth = Math.Ceiling(bar.DesiredSize.Width + shown.Max(p => TitleWidth(_panes[p].Title)) + JoinedTabRoom);
        PaneShown?.Invoke(pane);
        return true;
    }

    /// <summary>How wide a tab's name is in the window's type, shown (semibold).</summary>
    private double TitleWidth(string title) => new FormattedText(title, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(_main.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), _main.FontSize, Brushes.Black,
        VisualTreeHelper.GetDpi(_main).PixelsPerDip).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// A joined pane's place in the row of tabs under it: a grip to drag it by (it has no tab of its own there, and no
    /// room for one to come up over it), then the pane, kept whole at the row's right-hand end.
    /// </summary>
    private FrameworkElement JoinedBar(string pane)
    {
        var dots = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        dots.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var grip = new Border { Child = dots, Padding = new Thickness(5, 6, 3, 6), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        WireTab(grip, pane, activates: false);
        grip.ToolTip = $"{_panes[pane].Title}: drag here to move it (onto a pane to tab them together, to a side of one to put it beside). "
            + "Right above a pane with tabs or a title it joins that pane's row of tabs, as here. Right-click for more.";
        System.Windows.Automation.AutomationProperties.SetName(grip, _panes[pane].Title);

        var bar = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(grip, Dock.Left);
        bar.Children.Add(grip);
        bar.Children.Add(Placed(pane));
        return bar;
    }

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

        tab.ToolTip = $"{info.Title}: drag this tab to move the pane (onto another pane to tab them together, to a side of it to put it beside, or out of the window into the side window). Right-click for more.";

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
        var group = _state.Main.GroupOf(pane) ?? _state.Side?.GroupOf(pane);
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
        ExtendTabMenu?.Invoke(menu, pane);
        menu.IsOpen = true;
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
                _target = (side, null, windowEdge, false, null);
                _guide!.ShowAt(Half(bounds, windowEdge, 0.25), $"{_panes[press.Pane].Title}: along the {windowEdge.ToString().ToLowerInvariant()} of the window");
                return;
            }

            foreach (var (groupHost, group, groupSide) in _groupHosts.Where(g => g.Side == side))
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

            return;
        }

        // Outside both windows: to the side window, opened where it is let go.
        _target = (true, null, DockZone.Right, true, null);
        _guide!.ShowAt(new Rect(point.X - 160 * dpi, point.Y - 100 * dpi, 320 * dpi, 200 * dpi), $"{_panes[press.Pane].Title}: in the side window");
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

        Move(drag.Pane, to.Side, to.Group, to.Zone, to.At);
    }

    /// <summary>
    /// Moves a pane, and only that pane (the others of its group stay where they are): into a group (as a tab, at a
    /// place among its tabs or at the end), beside a group, or along an edge of a window (no group).
    /// </summary>
    /// <param name="at">Where among the group's tabs shown it goes; null for after them.</param>
    public void Move(string pane, bool toSide, DockNode? group, DockZone zone, int? at = null)
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
        var root = toSide ? _state.Side : _state.Main;
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
        _builtSignature = Signature();
        Changed?.Invoke();
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

    // ----- Arrangements taken up whole (the Layout button: built-in layouts, the user's own) -----

    /// <summary>The arrangement as it is now, as a copy (to keep as a layout of the user's own, or to come back to).</summary>
    public DockLayoutState Current
    {
        get
        {
            RememberSideBounds();
            return _state.Copy();
        }
    }

    /// <summary>
    /// Lays the windows out as an arrangement for a moment (a layout the pointer is on, in the Layout Selector): nothing
    /// is saved, and the side window stays as it is. The player floats, so it only moves (see <see cref="WindowRoot"/>).
    /// </summary>
    public void Preview(DockLayoutState layout)
    {
        _state = KeepingSideWindow(layout);
        Rebuild();
    }

    /// <summary>Takes an arrangement up for good, the side window left as it is, and saves it.</summary>
    public void Apply(DockLayoutState layout)
    {
        _state = KeepingSideWindow(layout);
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>An arrangement as it would be taken up now (panes no longer known left out, missing ones put in, the side window as it is): to preview and draw.</summary>
    public DockLayoutState Prepared(DockLayoutState layout) => KeepingSideWindow(layout);

    /// <summary>
    /// Whether two arrangements lay the main window out alike: the same panes in the same groups, in the same splits
    /// the same way round. How wide each part is, which tab is shown and the tabs' order do not count (the Layout
    /// Selector's mark of the layout in use, Pass 4.1C: dragging a splitter or a tab does not make it another layout).
    /// </summary>
    public static bool SameShape(DockLayoutState a, DockLayoutState b) => Shape(a.Main) == Shape(b.Main);

    private static string Shape(DockNode node) => node.IsGroup
        ? "[" + string.Join(",", node.Panes.Order(StringComparer.Ordinal)) + "]"
        : (node.Vertical ? "V(" : "H(") + string.Join(";", node.Children.Select(Shape)) + ")";

    /// <summary>
    /// An arrangement for the main window with the side window as it is now: the panes the side window holds stay there
    /// (taken out of the arrangement's main window), so taking a layout up never opens, closes or empties it.
    /// </summary>
    private DockLayoutState KeepingSideWindow(DockLayoutState layout)
    {
        var state = layout.Copy();
        (state.SideOpen, state.SideBounds, state.SideMaximized) = (_state.SideOpen, _state.SideBounds, _state.SideMaximized);
        (state.Side, state.SideClosed) = (_state.Side is { } side ? Copy(side) : null, _state.SideClosed is { } closed ? Copy(closed) : null);
        foreach (var pane in state.Side?.AllPanes().ToList() ?? [])
            state.Main = DockTree.Remove(state.Main, pane) ?? DockNode.Group();
        return Validate(state);
    }

    private static DockNode Copy(DockNode node) => JsonSerializer.Deserialize<DockNode>(JsonSerializer.Serialize(node))!;

    /// <summary>
    /// A small picture of an arrangement's main window, for the Layout Selector's cards: each part a rounded box named
    /// after its panes (as this mode shows them: hidden panes left out, and a part with none takes no room), the rows
    /// sized to their content (the overview bar, the playback controls) a thin band, the header a small mark at the
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
            _sideRoot = null;
            foreach (var content in Movables().Where(c => Window.GetWindow(c) == open).ToList())
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

        foreach (var content in Movables().Where(c => Window.GetWindow(c) == open).ToList())
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
