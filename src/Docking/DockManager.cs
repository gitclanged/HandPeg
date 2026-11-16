using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static HandPegApp.UiBrushes;

namespace HandPegApp.Docking;

/// <summary>
/// Docking layout for the main window and any number of side windows (Pane Docker or Live Video Preview): one tree of groups
/// and splits (<see cref="DockNode"/>) per window. Tab drags dock into a group, beside a part, at a window edge, or into a
/// new Pane Docker at the drop point.
/// <para>Pane content is created once and re-parented on every layout pass; hidden panes keep their tree position. A pane's tab
/// lives in its <see cref="PaneTitleSlot"/>, as a hover tab at its top-left (titleless panes), or in a tab row.</para>
/// </summary>
public sealed partial class DockManager
{
    private const double DragStart = 6;

    private readonly Window _main;
    private readonly ContentControl _mainHost;
    private readonly Func<DockLayoutState> _defaults;
    private readonly Dictionary<string, DockPane> _panes = [];
    private readonly Dictionary<string, FrameworkElement> _made = [];
    private readonly List<(FrameworkElement Host, DockNode Group, SideHost? Side)> _groupHosts = [];
    private readonly List<(FrameworkElement Host, SideHost? Side)> _roots = [];
    private DockLayoutState _state = new();
    private DockGuide? _guide;
    private bool _rebuildQueued;
    private bool _shuttingDown;

    /// <summary>
    /// A side window: its saved state plus its window and root once open (null before the main window shows; side windows open
    /// after Frame 1).
    /// </summary>
    private sealed class SideHost(SideWindowState state)
    {
        public SideWindowState State { get; set; } = state;

        public SideWindow? Window { get; set; }

        public WindowRoot? Root { get; set; }

        /// <summary>Open at the pointer (pane dropped outside every window) instead of the saved position.</summary>
        public bool AtPointer { get; set; }
    }

    // Side windows in opening order (menu ordinal 1 = first opened).
    private readonly List<SideHost> _sides = [];

    // Side window currently being rendered (Render), so its groups are attributed to it.
    private SideHost? _renderingFor;

    // Tab press state and the drag it may start.
    private (string Pane, FrameworkElement Tab, Point At)? _press;

    // Keyboard-activated tab: refocused on the pane's new tab after the strip rebuilds.
    private string? _focusTabOf;
    private bool _dragging;

    // Current drop target: group or window edge, side, and insertion index among visible tabs.
    private (SideHost? Side, DockNode? Group, DockZone Zone, bool Outside, int? At)? _target;

    // Shape assigned to each reshapeable pane at the last layout (DockPane.Reshape).
    private readonly Dictionary<string, PaneShape> _shapes = [];

    // The joining pane (header): its bar host in the tab row and the group below it.
    private (string Pane, FrameworkElement Bar, DockNode Under)? _joined;

    // Group being rendered directly under the header; it gets a boxed tab row to host the header.
    private DockNode? _joinUnder;

    // Extra width for a joined header's tab row (boxed tab padding/margin plus the overflow button); added to the group minimum.
    private const double JoinedTabRoom = 70;

    // Slot width below which a reshapeable row pane switches to a column.
    private const double RowLeastWidth = 180;

    // Watched slots of reshapeable row panes.
    private readonly List<(FrameworkElement Slot, SizeChangedEventHandler Handler)> _shapeWatches = [];

    // Per-window roots (WindowRoot) and floating panes (DockPane.Floats): each one's tree stand-in and overlay host.
    private readonly WindowRoot _mainRoot = new();
    private readonly Dictionary<string, (Border StandIn, Grid Box)> _floating = [];

    public DockManager(Window main, ContentControl mainHost, Func<DockLayoutState> defaults)
    {
        (_main, _mainHost, _defaults) = (main, mainHost, defaults);
        _mainHost.Content = _mainRoot;
        _main.PreviewKeyDown += (_, e) => CancelDragOnEscape(e);
    }

    /// <summary>Raised when the arrangement has changed, to be saved.</summary>
    public event Action? Changed;

    /// <summary>Raised when a side window opens, closes or changes mode; drives the Layout Selector rows and mirrors.</summary>
    public event Action? SideChanged;

    /// <summary>Raised after a side window is created, before it shows, so the main window can wire keys and drops.</summary>
    public event Action<SideWindow>? SideWindowOpened;

    /// <summary>Raised when a pane has been put on screen (made, the first time), by its name.</summary>
    public event Action<string>? PaneShown;

    /// <summary>
    /// Renamed panes, old id -> new id: a saved layout naming only the old id gets the new pane in its position.
    /// </summary>
    public IReadOnlyDictionary<string, string> Replaced { get; init; } = new Dictionary<string, string>();

    /// <summary>Raised when a load substitutes a renamed pane (<see cref="Replaced"/>): old id, new id.</summary>
    public event Action<string, string>? PaneReplaced;

    /// <summary>
    /// Migrates any loaded layout (saved, built-in, custom, first-run) to the current DockLayoutState.Version before use.
    /// Idempotent and never triggers a save (Load writes no settings before Frame 1).
    /// </summary>
    public Func<DockLayoutState, DockLayoutState>? Upgrade { get; init; }

    /// <summary>Handler for the tab menu's Reset Panes Layout (the window restores the Default layout); null falls back to <see cref="Reset"/>.</summary>
    public Action? ResetLayout { get; init; }

    /// <summary>Open side windows in opening order, excluding those whose window is not yet created.</summary>
    public IReadOnlyList<SideWindow> SideWindows => _sides.Select(s => s.Window).OfType<SideWindow>().ToList();

    /// <summary>Whether any side window is open (or waits to open, at launch).</summary>
    public bool IsSideOpen => _sides.Count > 0;

    /// <summary>
    /// Layout Selector rows for open side windows: id, ordinal (1 = first opened), mode and display name
    /// ("Live Video Preview 2", "Pane Docker 1: Media, Effects").
    /// </summary>
    public IReadOnlyList<(string Id, int Number, SideWindowMode Mode, string Name)> SideWindowList =>
        _sides.Select((s, i) => (s.State.Id, i + 1, s.State.WindowMode, SideName(s, i + 1))).ToList();

    private string SideName(SideHost side, int number)
    {
        if (side.State.IsPreview)
            return $"Live Video Preview {number}";
        var panes = side.State.Tree?.AllPanes().Where(_panes.ContainsKey).Select(p => _panes[p].Title).ToList() ?? [];
        return panes.Count == 0 ? $"Pane Docker {number}" : $"Pane Docker {number}: {string.Join(", ", panes)}";
    }

    private int NumberOf(SideHost side) => _sides.IndexOf(side) + 1;

    /// <summary>The tree containing a pane and its side window (null = main window).</summary>
    private (DockNode? Group, SideHost? Side) Find(string pane)
    {
        if (_state.Main.GroupOf(pane) is { } group)
            return (group, null);
        foreach (var side in _sides)
        {
            if (side.State.Tree?.GroupOf(pane) is { } found)
                return (found, side);
        }

        return (null, null);
    }

    private DockNode? GroupAnywhere(string pane) => Find(pane).Group;

    public void Register(DockPane pane) => _panes[pane.Id] = pane;

    public DockPane? Pane(string id) => _panes.GetValueOrDefault(id);

    /// <summary>Makes a pane's content now (if it was not made before), without showing it.</summary>
    public FrameworkElement Realize(string id)
    {
        if (!_made.TryGetValue(id, out var content))
        {
            content = _panes[id].Content();
            _made[id] = content;

            // Pin DataContext on the pane: while re-parented it is briefly outside every window, and an inherited context would
            // drop, letting bound boxes write empty values back.
            if (content.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
                content.SetBinding(FrameworkElement.DataContextProperty, new System.Windows.Data.Binding(nameof(FrameworkElement.DataContext)) { Source = _main });
        }

        return content;
    }

    public bool IsRealized(string id) => _made.ContainsKey(id);

    /// <summary>
    /// The tree element for a pane: its content, or for floating panes (native child window) a stand-in whose bounds the
    /// overlay host tracks (<see cref="WindowRoot"/>).
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

    /// <summary>The element detached with a pane: its content, or its overlay host for floating panes.</summary>
    private IEnumerable<FrameworkElement> Movables() => _made.Select(m => _floating.TryGetValue(m.Key, out var floating) ? floating.Box : m.Value);

    /// <summary>
    /// A window's dock root: the tree plus an overlay for floating panes (native windows such as the player), each kept over its
    /// stand-in and hidden when that is not visible. Floating panes never leave the overlay, so relayout only moves them.
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

        // After each layout pass, move hosts onto their stand-ins; only on change, so this cannot trigger another pass.
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

                // Snap to device pixels like the surrounding panes.
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

    /// <summary>
    /// Loads a saved layout (the mode default if null/unreadable), adds missing panes and lays out. <paramref name="showFirst"/>
    /// (the Timeline at start-up) activates that pane in its group within the same single build, raising and saving nothing.
    /// <para><paramref name="sideWindows"/> come from AppSettings.SideWindows; null keeps current side windows and migrates a legacy
    /// single side window to the first Pane Docker. At launch side windows open after Frame 1, each laid out separately, so the
    /// main window keeps its single pre-Frame-1 build.</para>
    /// </summary>
    public void Load(string? saved, string? showFirst = null, string? sideWindows = null)
    {
        var sides = sideWindows is null ? _sides.Select(s => s.State).ToList() : SideWindowStates.Read(sideWindows);
        _state = Validate(DockLayoutState.Read(saved) ?? _defaults(), sides, migrate: sideWindows is null);
        if (showFirst is not null)
            ShowInItsGroup(_state, showFirst, sides);
        TakeSides(sides);
        ApplySideWindows();
        RebindOrRebuild();
    }

    /// <summary>
    /// Activates a pane in its group in the main tree and in each given Pane Docker (<paramref name="sides"/>); closed side
    /// windows keep their saved layout and other groups are untouched. Window-free; returns true if anything changed.
    /// </summary>
    internal static bool ShowInItsGroup(DockLayoutState state, string pane, IEnumerable<SideWindowState>? sides = null)
    {
        var changed = false;
        var roots = new[] { state.Main, state.SideOpen ? state.Side : null }.Concat(sides?.Select(s => s.Tree) ?? []);
        foreach (var root in roots)
        {
            if (root?.GroupOf(pane) is { } group && group.Active != pane)
            {
                group.Active = pane;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Reconciles side windows with a load: open ids keep their window and take the new state (tree, mode); unlisted ones close
    /// (the load already placed their panes in the main window); new ones are created once the main window can own them
    /// (ApplySideWindows).
    /// </summary>
    private void TakeSides(List<SideWindowState> sides)
    {
        foreach (var gone in _sides.Where(h => sides.All(s => s.Id != h.State.Id)).ToList())
        {
            _sides.Remove(gone);
            CloseWindowOf(gone);
        }

        var kept = new List<SideHost>();
        foreach (var state in sides)
        {
            if (string.IsNullOrEmpty(state.Id) || kept.Any(k => k.State.Id == state.Id))
                state.Id = SideWindowStates.NewId(sides.Where(s => !ReferenceEquals(s, state)).Concat(kept.Select(k => k.State)));
            var host = _sides.FirstOrDefault(h => h.State.Id == state.Id) ?? new SideHost(state);
            host.State = state;
            host.Window?.ShowMode(state.WindowMode);
            kept.Add(host);
        }

        _sides.Clear();
        _sides.AddRange(kept);
    }

    /// <summary>
    /// Recomputes every part's minimum size in place when a pane's minimum changes without a relayout (Media Browser Filmstrip
    /// view, MainWindow.MediaMinWidth). No layout, events or save.
    /// </summary>
    public void RefreshMinSizes()
    {
        foreach (var view in _splitViews)
        {
            foreach (var (element, index, fits) in view.Parts)
            {
                if (index >= view.Split.Children.Count)
                    continue;
                var part = view.Split.Children[index];
                if (view.Split.Vertical)
                    view.Grid.RowDefinitions[Grid.GetRow(element)].MinHeight = fits ? 0 : MinHeightOf(part);
                else
                    view.Grid.ColumnDefinitions[Grid.GetColumn(element)].MinWidth = MinWidthOf(part);
            }
        }
    }

    /// <summary>
    /// Whether a saved layout normalises (as <see cref="Load"/>/<see cref="Apply"/> would: migrated, filled, Pane Docker panes removed)
    /// to the same main-window shape as a reference layout (<see cref="SameShape"/>). Optional studios are stripped from both first.
    /// Side-effect free; false if unreadable.
    /// </summary>
    public bool LoadsAlike(string? saved, DockLayoutState layout, string? sideWindows)
    {
        if (DockLayoutState.Read(saved) is not { } kept)
            return false;

        var sides = SideWindowStates.Read(sideWindows);
        return SameShape(WithoutStudios(Validate(kept, sides, migrate: true)), WithoutStudios(Validate(layout.Copy(), sides)));
    }

    /// <summary>A tidied copy of a tree without optional studio panes (DockPane.Optional), for LoadsAlike.</summary>
    private DockLayoutState WithoutStudios(DockLayoutState state)
    {
        foreach (var group in state.Main.AllGroups())
        {
            group.Panes.RemoveAll(p => _panes.TryGetValue(p, out var pane) && pane.Optional);
            if (group.Active is { } active && !group.Panes.Contains(active))
                group.Active = group.Panes.FirstOrDefault();
        }

        state.Main = DockTree.Tidy(state.Main) ?? DockNode.Group();
        return state;
    }

    /// <summary>Serialized main-window layout (AppSettings.DockLayout); side windows are saved separately (<see cref="SaveSideWindows"/>).</summary>
    public string Save() => JsonSerializer.Serialize(_state, Services.AppSettingsJsonContext.Default.DockLayoutState);

    /// <summary>Serialized open side windows (AppSettings.SideWindows): mode, panes and bounds each ("[]" for none).</summary>
    public string SaveSideWindows()
    {
        RememberPlacements();
        return SideWindowStates.Write(_sides.Select(s => s.State));
    }

    /// <summary>
    /// Restores every pane to <paramref name="layout"/> (the Default layout; null = built-in defaults) and closes every Pane Docker
    /// (Live Video Previews hold no panes and stay).
    /// </summary>
    public void Reset(DockLayoutState? layout = null)
    {
        foreach (var docker in _sides.Where(s => !s.State.IsPreview).ToList())
        {
            _sides.Remove(docker);
            CloseWindowOf(docker);
        }

        _state = Validate(layout?.Copy() ?? _defaults(), [.. _sides.Select(s => s.State)]);

        // Like Load/Apply: an equivalent tree (repeat Reset, or differing only in split shares and active tabs) is adopted in place,
        // so splitters and tabs edit the saved tree, not a replaced one.
        RebindOrRebuild();
        Changed?.Invoke();
        RaiseSideChanged();
    }

    /// <summary>
    /// Normalises a tree: every known pane exactly once across main and side windows, unknown panes removed, missing panes homed.
    /// Side windows keep their panes (the main copy is removed); Live Video Previews hold none. With <paramref name="migrate"/> a
    /// legacy single side window joins <paramref name="sides"/> as a Pane Docker; legacy fields are always cleared.
    /// </summary>
    private DockLayoutState Validate(DockLayoutState state, List<SideWindowState> sides, bool migrate = false)
    {
        // Migrate older layouts first (retired panes replaced by their successors).
        if (Upgrade is { } upgrade)
            state = upgrade(state);

        // A renamed pane takes the old id's position (e.g. Media replacing Source) unless already present.
        foreach (var (old, replacement) in Replaced)
        {
            var groups = new[] { state.Main, state.Side, state.SideClosed }.Concat(sides.Select(s => s.Tree)).OfType<DockNode>().SelectMany(n => n.AllGroups()).ToList();
            if (!_panes.ContainsKey(replacement) || groups.Any(g => g.Panes.Contains(replacement)) || groups.FirstOrDefault(g => g.Panes.Contains(old)) is not { } home)
                continue;

            home.Panes[home.Panes.IndexOf(old)] = replacement;
            if (home.Active == old)
                home.Active = replacement;
            PaneReplaced?.Invoke(old, replacement);
        }

        // Legacy single side window becomes the first Pane Docker when migrating; otherwise dropped (never written again).
        if (migrate)
            SideWindowStates.Migrate(state, sides);
        else
            (state.Side, state.SideOpen, state.SideBounds, state.SideMaximized, state.SideClosed) = (null, false, null, false, null);

        SideWindowStates.Clean(state, sides, _panes.ContainsKey);
        var seen = state.Main.AllPanes().Concat(sides.SelectMany(s => s.Tree?.AllPanes() ?? [])).ToHashSet();
        foreach (var missing in _panes.Keys.Where(p => !seen.Contains(p) && !_panes[p].Optional).ToList())
            state.Main = PlaceAsDefault(state.Main, missing);
        return state;
    }

    /// <summary>Homes a pane in the main window per the mode default: into its default group-mate's group, or beside its default neighbour.</summary>
    private DockNode PlaceAsDefault(DockNode main, string pane)
    {
        var defaults = _defaults().Main;
        if (defaults.GroupOf(pane) is { } home)
        {
            // Into the group of its default group-mate.
            foreach (var mate in home.Panes.Where(p => p != pane))
            {
                if (main.GroupOf(mate) is { } group)
                {
                    group.Panes.Insert(Math.Min(home.Panes.IndexOf(pane), group.Panes.Count), pane);
                    return main;
                }
            }

            // Beside the group of its default neighbour.
            var groups = defaults.AllGroups().ToList();
            var at = groups.IndexOf(home);
            foreach (var near in groups.Skip(at + 1).Concat(groups.Take(at).Reverse()))
            {
                if (near.Panes.Select(main.GroupOf).FirstOrDefault(g => g is not null) is { } neighbour)
                {
                    var parent = defaults.ParentOf(home);
                    var zone = parent is { Vertical: true } ? DockZone.Top : DockZone.Left;

                    // Use its default share beside that neighbour.
                    var share = 0.5;
                    if (parent is not null && parent.Children.IndexOf(home) is var mine and >= 0
                        && parent.Children.FindIndex(c => c.AllGroups().Contains(near)) is var theirs and >= 0
                        && mine < parent.Sizes.Count && theirs < parent.Sizes.Count)
                        share = parent.Sizes[mine] / (parent.Sizes[mine] + parent.Sizes[theirs]);
                    return DockTree.InsertBeside(main, neighbour, groups.IndexOf(near) > at ? zone : zone == DockZone.Top ? DockZone.Bottom : DockZone.Right, DockNode.Group(pane), share);
                }
            }
        }

        // Studios absent from the mode default: tab into the first HomeWith mate's group.
        if (_panes.TryGetValue(pane, out var known))
        {
            foreach (var mate in known.HomeWith)
            {
                if (main.GroupOf(mate) is { } group)
                {
                    group.Panes.Add(pane);
                    return main;
                }
            }
        }

        return DockTree.InsertBeside(main, main, DockZone.Right, DockNode.Group(pane));
    }

    /// <summary>Whether the layout contains the pane (main or side window).</summary>
    public bool Contains(string pane) => GroupAnywhere(pane) is not null;

    /// <summary>
    /// Opens an optional studio pane: homes it if absent (mode default or <see cref="DockPane.HomeWith"/> mates), then activates it.
    /// </summary>
    public void Open(string pane)
    {
        if (!_panes.ContainsKey(pane))
            return;
        if (!Contains(pane))
        {
            _state.Main = PlaceAsDefault(_state.Main, pane);
            Rebuild();
            Changed?.Invoke();
        }

        BringForward(pane);
    }

    /// <summary>Removes an optional studio pane from the layout; only <see cref="Open"/> restores it.</summary>
    public void Close(string pane)
    {
        if (!Contains(pane))
            return;
        TakeOut(pane);
        Rebuild();
        Changed?.Invoke();
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

    /// <summary>Takes every pane out of where it is and puts it where the tree says, in every window.</summary>
    public void Rebuild()
    {
        if (_dragging)
            return;

        // Skip when the signature is unchanged: every rebuild re-parents every pane.
        var signature = Signature();
        if (signature == _builtSignature)
            return;

        MarkAllBuilt();
        if (Services.StartupTrace.IsOn)
            Services.StartupTrace.Layout("dock rebuilt, showing " + string.Join(", ", _panes.Values.Where(p => p.IsShown()).Select(p => p.Id)));
        _groupHosts.Clear();
        _splitViews.Clear();
        _roots.Clear();
        _shapes.Clear();
        UnwatchShapes();

        // Detach and unhide every pane first: a pane hidden behind another tab once stayed hidden in its new spot, leaving an
        // empty group. Floating panes stay on their overlay; only their stand-ins move.
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
        _roots.Add((_mainHost, null));

        foreach (var side in _sides)
            RenderSide(side);

        FillTitleSlots();
        PlaceFloating();
        WatchShapes();
    }

    /// <summary>
    /// Lays out one side window only, leaving the main window untouched (Law 1: a single main-window dock build). A Live Video
    /// Preview has no panes, only its picture.
    /// </summary>
    private void RebuildSide(SideHost side)
    {
        if (_dragging || side.Window is null)
            return;

        var panes = side.State.Tree?.AllPanes().ToList() ?? [];
        _groupHosts.RemoveAll(g => ReferenceEquals(g.Side, side));
        _splitViews.RemoveAll(v => ReferenceEquals(v.Side, side));
        _roots.RemoveAll(r => ReferenceEquals(r.Side, side));
        foreach (var pane in panes)
        {
            _shapes.Remove(pane);
            if (_floating.TryGetValue(pane, out var floating))
            {
                Detach(floating.StandIn);
                floating.StandIn.Visibility = Visibility.Visible;
                RemoveHoverZones(floating.Box);
            }
            else if (_made.TryGetValue(pane, out var content))
            {
                Detach(content);
                content.Visibility = Visibility.Visible;
            }

            if (_panes.TryGetValue(pane, out var known) && known.HoverHost is { } hover)
                RemoveHoverZones(hover);
        }

        if (_joined is { Pane: var joinedPane, Bar: var bar } && panes.Contains(joinedPane))
        {
            Detach(bar);
            _joined = null;
        }

        RenderSide(side);
        FillTitleSlots(panes);
        PlaceFloating();
        WatchShapes();

        // Record this window as matching the layout so a pending rebuild skips it.
        MarkSideBuilt(side);
    }

    /// <summary>A side window's content: a Pane Docker's tree (or empty-state hint), or null for a Live Video Preview.</summary>
    private void RenderSide(SideHost side)
    {
        if (side.Window is not { } window)
            return;

        if (side.State.IsPreview)
        {
            if (side.Root is { } old)
                old.Tree.Child = null;
            return;
        }

        if (side.Root is null || !ReferenceEquals(window.Host.Content, side.Root))
            window.Host.Content = side.Root = new WindowRoot();
        _renderingFor = side;
        try
        {
            var content = side.State.Tree is { } tree ? Render(tree, side: true) : null;
            side.Root.Tree.Child = content;
            window.ShowHint(content is null);
        }
        finally
        {
            _renderingFor = null;
        }

        _roots.Add((window.Host, side));
    }

    /// <summary>Moves each floating host to the overlay of its stand-in's window; hosts without a stand-in stay put, hidden.</summary>
    private void PlaceFloating()
    {
        foreach (var (standIn, box) in _floating.Values)
        {
            var root = standIn.IsDescendantOf(_mainRoot) ? _mainRoot
                : _sides.Select(s => s.Root).FirstOrDefault(r => r is not null && standIn.IsDescendantOf(r));
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
    /// Watches the slot of each reshapeable row pane (the header): switches to a column when the slot is taller than wide or too
    /// narrow, and back to a row when it widens.
    /// </summary>
    private void WatchShapes()
    {
        // Rebuilt per layout (a side-window-only layout leaves main-window watches intact).
        UnwatchShapes();
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

    private void UnwatchShapes()
    {
        foreach (var (slot, handler) in _shapeWatches)
            slot.SizeChanged -= handler;
        _shapeWatches.Clear();
    }

    private void FitShape(string pane, FrameworkElement slot, Size size)
    {
        var shape = size.Height > size.Width || size.Width < RowLeastWidth ? PaneShape.Column : PaneShape.Row;
        if (_shapes.GetValueOrDefault(pane) == shape)
            return;

        SetShape(pane, shape);

        // Refresh the hover tab for the new shape.
        if (slot is Panel host && host.Children.OfType<FrameworkElement>().Any(c => c.Tag as string == "dock-hover")
            && _groupHosts.FirstOrDefault(g => g.Group.Panes.Contains(pane)).Group is { } group)
        {
            RemoveHoverZones(host);
            host.Children.Add(HoverZone(group, pane));
        }
    }

    // Inputs of the last layout: tree, visible panes and side windows.
    private string? _builtSignature;

    // Layout signature: excludes the active tab (changed in place by Activate) and side window bounds; includes side window
    // mode, tree and window.
    private string Signature() => MainSignature() + string.Concat(_sides.Select(SideSignature));

    private string MainSignature() => ActiveAttribute.Replace(JsonSerializer.Serialize(_state, Services.AppSettingsJsonContext.Default.DockLayoutState), "") + "|"
        + string.Join(",", _panes.Values.Where(p => p.IsShown()).Select(p => p.Id));

    private static string SideSignature(SideHost side) => "|" + side.State.Id + ":" + side.State.Mode + ":" + (side.Window?.GetHashCode() ?? 0) + ":"
        + (side.State.Tree is { } tree ? ActiveAttribute.Replace(JsonSerializer.Serialize(tree, Services.AppSettingsJsonContext.Default.DockNode), "") : "");

    // Signatures of the last layout: main window and each side window.
    private string? _builtMain;
    private readonly Dictionary<SideHost, string> _builtSides = [];

    /// <summary>Every window is as the arrangement says now (a whole layout, or a change made in place).</summary>
    private void MarkAllBuilt()
    {
        _builtMain = MainSignature();
        _builtSides.Clear();
        foreach (var side in _sides)
            _builtSides[side] = SideSignature(side);
        _builtSignature = Signature();
    }

    /// <summary>
    /// Records a side-window-only layout (or removal when <paramref name="side"/> is null) using the main window's last laid-out
    /// signature, so a pending main relayout is never mistaken for done.
    /// </summary>
    private void MarkSideBuilt(SideHost? side)
    {
        if (side is not null)
            _builtSides[side] = SideSignature(side);
        foreach (var gone in _builtSides.Keys.Where(k => !_sides.Contains(k)).ToList())
            _builtSides.Remove(gone);
        _builtSignature = _builtMain is null ? null : _builtMain + string.Concat(_sides.Select(s => _builtSides.TryGetValue(s, out var built) ? built : "|?"));
    }

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

        // Below a joined header: a boxed tab row like the main tab bar, header at its end. A pane that is headerless when alone
        // (Timeline) uses a hover tab instead: no title row over its top edge.
        var joined = ReferenceEquals(node, _joinUnder);
        var host = joined || shown.Any(p => _panes[p].Handle == PaneHandle.Tab) || (shown.Count > 1 && shown.Any(p => _panes[p].Handle == PaneHandle.Hover))
            ? RenderTabs(node, shown, active, side, joined)
            : shown.Count == 1 && (_panes[active].Handle == PaneHandle.Hover || _panes[active].HeaderlessAlone)
                ? RenderHover(node, active)
                : RenderPlain(active);
        _groupHosts.Add((host, node, side ? _renderingFor : null));
        PaneShown?.Invoke(active);
        return host;
    }

    // Titled group: shows the active pane with tabs in its title slot (FillTitleSlots).
    private FrameworkElement RenderPlain(string active) => new Border { Child = Placed(active), Background = Brushes.Transparent };

    /// <summary>Titleless pane: its name tab appears over the top-left corner on hover.</summary>
    private FrameworkElement RenderHover(DockNode group, string pane)
    {
        var host = new Grid { Background = Brushes.Transparent };
        host.Children.Add(Placed(pane));

        // For floating panes the zone sits on the overlay with the host.
        (_floating.TryGetValue(pane, out var floating) ? floating.Box : host).Children.Add(HoverZone(group, pane));

        // Panes under a native window (player) also get a hover zone on the overlay; the in-pane zone serves only while the
        // native window is hidden (nothing loaded).
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
    /// Near-invisible hover strip at a pane's top-left that reveals its tab. It is exactly the tab's width so the pointer stays
    /// over the tab once shown (a wider strip oscillated enter/leave and flickered). The tab appears only after the pointer rests
    /// for <see cref="HoverTabRest"/>, so passing pointers never get a tab over their target.
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
        System.Windows.Threading.DispatcherTimer? rest = null;
        void ShowTab()
        {
            rest?.Stop();
            if (!zone.IsMouseOver || tab.Visibility == Visibility.Visible)
                return;
            tab.Visibility = Visibility.Visible;
            tab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            strip = Math.Ceiling(tab.DesiredSize.Width);
            if (Mouse.GetPosition(zone).X > strip)
            {
                // Pointer beyond the real tab width (estimate was wide): shrink the strip to the tab and show nothing.
                tab.Visibility = Visibility.Collapsed;
                zone.Width = strip;
                return;
            }

            zone.Height = double.NaN;
            zone.Width = double.NaN;
        }

        zone.MouseEnter += (_, _) =>
        {
            if (rest is null)
            {
                rest = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = HoverTabRest };
                rest.Tick += (_, _) => ShowTab();
            }

            rest.Start();
        };
        zone.MouseLeave += (_, _) =>
        {
            rest?.Stop();
            if (_dragging)
                return;
            tab.Visibility = Visibility.Collapsed;
            (zone.Height, zone.Width) = (7, strip);
        };
        return zone;
    }

    /// <summary>Pointer rest time on a hover strip before its tab appears.</summary>
    private static readonly TimeSpan HoverTabRest = TimeSpan.FromMilliseconds(350);

    /// <summary>Estimated hover tab width (text in the window font plus MakeTab padding and border) before first display.</summary>
    private double HoverTabWidth(string title, bool upright)
    {
        var words = new FormattedText(title, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_main.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11.5, Brushes.Black,
            VisualTreeHelper.GetDpi(_main).PixelsPerDip);
        return Math.Ceiling(upright ? words.Height + 6 : words.WidthIncludingTrailingWhitespace + 19);
    }

    /// <summary>
    /// A tabbed group (settings tabs, groups with titleless panes, the header's joined group): one tab row with an overflow menu,
    /// and all realised panes stacked in one grid with only the active one visible. Switching toggles visibility without
    /// re-parenting, so a native player window stays attached (a TabControl would detach inactive content).
    /// </summary>
    private sealed class TabGroupHost : DockPanel
    {
        public TabGroupHost(DockNode group, bool classic)
        {
            (Group, Classic) = (group, classic);
            Strip = new OverflowStrip();
            Body = new Grid { Margin = classic ? new Thickness(0) : new Thickness(10, 4, 10, 6) };

            // Tab row with a right-end host for the joined header.
            Trailing = new Border { VerticalAlignment = classic ? VerticalAlignment.Bottom : VerticalAlignment.Center, Margin = classic ? new Thickness(0, 0, 0, 4) : new Thickness(0) };
            var head = new DockPanel { Margin = classic ? new Thickness(0) : new Thickness(12, 6, 12, 2) };
            DockPanel.SetDock(Trailing, Dock.Right);
            head.Children.Add(Trailing);
            head.Children.Add(Strip);
            SetDock(head, Dock.Top);
            Children.Add(head);
            if (classic)
            {
                // Boxed tabs: the active tab merges into the panel below; square corners so the layout stays edge to edge.
                Frame = new Border { Child = Body, CornerRadius = new CornerRadius(0), Padding = TabPadding };
                Frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
                Children.Add(Frame);
            }
            else
            {
                Children.Add(Body);
            }
        }

        /// <summary>Panel padding around a settings tab; panes that are panels themselves fill it.</summary>
        public static readonly Thickness TabPadding = new(16, 8, 16, 8);

        public DockNode Group { get; set; }

        /// <summary>Boxed tabs over a panel (main tab bar, header-joined group) instead of compact inline tabs.</summary>
        public bool Classic { get; }

        public OverflowStrip Strip { get; }

        /// <summary>Host at the right end of the tab row for a joined pane's bar (<see cref="PaneShape.Joined"/>).</summary>
        public Border Trailing { get; }

        /// <summary>The panel under boxed tabs; null for inline ones.</summary>
        public Border? Frame { get; }

        public Grid Body { get; }
        public Dictionary<string, FrameworkElement> Shown { get; } = [];
    }

    /// <summary>
    /// The main tab bar: the main-window group with the most settings tabs. It and the header-joined group use boxed tabs; all
    /// other groups use compact inline tabs.
    /// </summary>
    private DockNode? MainTabBar() => _state.Main.AllGroups()
        .Select(g => (Group: g, Count: g.Panes.Count(p => _panes[p].Handle == PaneHandle.Tab && _panes[p].IsShown())))
        .Where(g => g.Count > 0).OrderByDescending(g => g.Count).Select(g => g.Group).FirstOrDefault();

    /// <param name="joined">Directly under the header, which joins its tab row: always boxed tabs.</param>
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

    /// <summary>Activates a pane in a tab group: realises it if needed, hides the others and marks its tab active.</summary>
    private void ShowInTabs(TabGroupHost host, List<string> shown, string active)
    {
        if (!host.Shown.ContainsKey(active))
            AddToTabs(host, active);
        foreach (var (pane, content) in host.Shown)
            content.Visibility = pane == active ? Visibility.Visible : Visibility.Collapsed;

        // Under boxed tabs, settings tabs get panel padding; panel-style panes (Media, Keyframes) fill it in the panel colour.
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

        var more = new System.Windows.Controls.Primitives.ToggleButton { Content = Glyphs.ChevronDown, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Padding = new Thickness(6, 2, 6, 2), ToolTip = "The other panes of this group" };
        OverflowStrip.SetIsOverflowButton(more, true);
        host.Strip.Children.Add(more);
        host.Strip.MenuEntryFor = child => tabs.TryGetValue(child, out var pane) ? (_panes[pane].Title, () => Activate(pane)) : null;
        PaneShown?.Invoke(active);
    }

    /// <summary>
    /// Builds a split with star shares and draggable borders. Horizontally, a column-capable pane (the header) is sized to its
    /// content; vertically, a joining pane (the header) docks at the end of the tab row below when that part has tabs or
    /// titles, else it is its own row.
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
        var owner = side ? _renderingFor : null;
        var view = new SplitView(grid, split, parts, owner, PathOf((owner is null ? _state.Main : owner.State.Tree) ?? split, split) ?? []);
        _splitViews.Add(view);
        for (var k = 0; k < parts.Count; k++)
        {
            var (element, index, fits) = parts[k];
            if (k > 0)
                AddBorder(view, k);

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
    /// A split border. Next to content-sized parts (Overview Scrubber, playback controls) it redistributes between the nearest
    /// star-sized parts; with none on one side it is a plain line, or resizes an adjacent resizable pane. Directly below a
    /// resizable pane (Overview Scrubber) a plain drag resizes that pane and Ctrl+drag redistributes instead.
    /// <para>Sharing borders move across their full range, cascading past parts at minimum (ShareColumns, ShareRows); double-click
    /// evens the two neighbours (EvenOut) or resets the scrubber height (DockPane.ResetSize).</para>
    /// </summary>
    private void AddBorder(SplitView view, int k)
    {
        var (grid, split, parts) = (view.Grid, view.Split, view.Parts);
        var (before, after) = (parts[k - 1], parts[k]);
        UIElement border;
        var (above, under) = (parts.Take(k).LastOrDefault(p => !p.Fits).Element, parts.Skip(k).FirstOrDefault(p => !p.Fits).Element);

        // Edge to edge: a 1 px line (PaneSeamBrush) in a 1 px track with a 7 px grip overlapping 3 px into each side (ZIndex).
        // Beside a floating pane, whose native window covers overlays, the grip extends 6 px into the other side (SeamGrip).
        var (beforeEdge, afterEdge) = split.Vertical ? (DockZone.Bottom, DockZone.Top) : (DockZone.Right, DockZone.Left);
        var (grip, seam) = SeamGrip(split.Vertical, FloatsAt(split.Children[before.Index], beforeEdge), FloatsAt(split.Children[after.Index], afterEdge));
        if ((before.Fits || after.Fits) && split.Vertical && above is not null && under is not null)
        {
            // Resizes the adjacent content-sized pane via its height setting: plain drag on its lower edge, Ctrl+drag above it.
            var (fitting, below) = before.Fits ? (split.Children[before.Index], false) : (split.Children[after.Index], true);
            var (resize, title) = Resizer(fitting);
            var edge = resize is not null && !below;
            var reset = edge ? ResetterOf(fitting) : null;
            var thumb = new SeamThumb
            {
                Vertical = true, Height = SeamGrab, Margin = grip, Cursor = Cursors.SizeNS, Template = SplitterLook(seam),
                ToolTip = resize is null ? "Drag to share the room between the parts above and below; double-click to make them as tall as each other"
                    : edge ? $"Drag to make the {title} taller or shorter (double-click: its height put back); Ctrl+drag to share the room between the parts above and below"
                    : $"Drag to share the room between the parts above and below (double-click: as tall as each other); Ctrl+drag to make the {title} taller or shorter",
                DoubleClicked = reset ?? (() => EvenOut(view, k)),

                // Keyboard nudges redistribute only on borders whose plain drag redistributes (not the scrubber edge).
                Nudged = edge ? null : change => Nudge(view, k, change),
            };
            var moved = PointerMoves(thumb, grid);
            thumb.DragDelta += (_, e) =>
            {
                var change = moved();
                if (resize is not null && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) != edge)
                    resize(below ? -change : change);
                else
                    ShareRows(grid, parts, k, e.VerticalChange);
            };
            thumb.DragCompleted += (_, _) => TakeSizes(view);
            border = thumb;
        }
        else if (before.Fits || after.Fits)
        {
            var (fitting, below) = before.Fits ? (split.Children[before.Index], false) : (split.Children[after.Index], true);
            var (resize, title) = split.Vertical ? Resizer(fitting) : ((Action<double>?)null, "");
            if (resize is null)
            {
                // Nothing to drag: only the line.
                var line = split.Vertical ? new Border { Height = 1 } : new Border { Width = 1 };
                line.SetResourceReference(Border.BackgroundProperty, "PaneSeamBrush");
                border = line;
            }
            else
            {
                // Toward the content-sized pane shrinks it, away grows it; double-click resets its height.
                var thumb = new SeamThumb
                {
                    Vertical = true, Height = SeamGrab, Margin = grip, Cursor = Cursors.SizeNS, Template = SplitterLook(seam),
                    ToolTip = $"Drag to make the {title} taller or shorter; double-click to put its height back",
                    DoubleClicked = ResetterOf(fitting),
                };
                var moved = PointerMoves(thumb, grid);
                thumb.DragDelta += (_, _) =>
                {
                    var change = moved();
                    resize(below ? -change : change);
                };
                border = thumb;
            }
        }
        else
        {
            // Sharing border: a Thumb (GridSplitter only traded between neighbours and froze at a minimum-size part) that cascades
            // (ShareColumns, ShareRows). Not focusable, so the timeline keeps arrow keys; Nudged moves it 16 DIPs per step.
            var thumb = new SeamThumb
            {
                Vertical = split.Vertical, Margin = grip, Cursor = split.Vertical ? Cursors.SizeNS : Cursors.SizeWE, Template = SplitterLook(seam),
                ToolTip = split.Vertical ? "Drag to share the room between the parts above and below; double-click to make the two beside it as tall as each other"
                    : "Drag to share the room between the parts at the left and right; double-click to make the two beside it as wide as each other",
                DoubleClicked = () => EvenOut(view, k),
                Nudged = change => Nudge(view, k, change),
            };
            if (split.Vertical)
                (thumb.Height, thumb.VerticalAlignment) = (SeamGrab, VerticalAlignment.Center);
            else
                (thumb.Width, thumb.HorizontalAlignment) = (SeamGrab, HorizontalAlignment.Center);
            thumb.DragDelta += (_, e) =>
            {
                if (split.Vertical)
                    ShareRows(grid, parts, k, e.VerticalChange);
                else
                    ShareColumns(grid, parts, k, e.HorizontalChange);
            };
            thumb.DragCompleted += (_, _) => TakeSizes(view);
            border = thumb;
        }

        // Raise above neighbours; the next part added would otherwise cover half the grip.
        Panel.SetZIndex(border, 1);
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

    /// <summary>The part's border-resizable pane (DockPane.ResizeBy) and its title; (null, "") if none.</summary>
    private (Action<double>? Resize, string Title) Resizer(DockNode part)
    {
        foreach (var pane in part.AllPanes())
        {
            if (_panes[pane].ResizeBy is { } resize)
                return (resize, _panes[pane].Title);
        }

        return (null, "");
    }

    /// <summary>Double-click handler on a resizable pane's edge (DockPane.ResetSize restores the scrubber height).</summary>
    private Action? ResetterOf(DockNode part) => part.AllPanes().Select(p => _panes[p].ResetSize).FirstOrDefault(r => r is not null);

    /// <summary>
    /// Pointer delta along the split since the previous call (or press), in split DIPs, which stay fixed while the border moves.
    /// Thumb deltas are border-relative and made the resized pane overshoot then lag.
    /// </summary>
    private static Func<double> PointerMoves(Thumb thumb, IInputElement split)
    {
        double? last = null;
        thumb.DragStarted += (_, _) => last = Mouse.GetPosition(split).Y;
        return () =>
        {
            var at = Mouse.GetPosition(split).Y;
            var change = last is { } from ? at - from : 0;
            last = at;
            return change;
        };
    }

    /// <summary>
    /// A realised split: grid, parts (Fits = content-sized), side window (null = main) and tree path, so Rebind can attach a
    /// reloaded equivalent tree's node and borders keep editing the saved layout.
    /// </summary>
    private sealed class SplitView(Grid grid, DockNode split, List<(FrameworkElement Element, int Index, bool Fits)> parts, SideHost? side, int[] path)
    {
        public Grid Grid { get; } = grid;

        public DockNode Split { get; set; } = split;

        public List<(FrameworkElement Element, int Index, bool Fits)> Parts { get; } = parts;

        public SideHost? Side { get; } = side;

        public int[] Path { get; } = path;
    }

    // Realised splits across all windows, for in-place minimum refresh (RefreshMinSizes) and Rebind.
    private readonly List<SplitView> _splitViews = [];

    /// <summary>A node's child-index path from the root (empty for the root); null when absent.</summary>
    private static int[]? PathOf(DockNode root, DockNode node)
    {
        if (ReferenceEquals(root, node))
            return [];
        for (var i = 0; i < root.Children.Count; i++)
        {
            if (PathOf(root.Children[i], node) is { } rest)
                return [i, .. rest];
        }

        return null;
    }

    /// <summary>The node at a place in a tree (<see cref="PathOf"/>); null when the tree has no such place.</summary>
    private static DockNode? NodeAt(DockNode root, int[] path)
    {
        var node = root;
        foreach (var i in path)
        {
            if (i >= node.Children.Count)
                return null;
            node = node.Children[i];
        }

        return node;
    }

    /// <summary>
    /// Moves the k-th border of a horizontal split by <paramref name="change"/> DIPs: the left side gains what the right side gives,
    /// nearest part first, cascading past parts at minimum (and vice versa), so the border spans its full range.
    /// </summary>
    private static Dictionary<int, double> ShareColumns(Grid grid, List<(FrameworkElement Element, int Index, bool Fits)> parts, int k, double change) =>
        Share(grid, parts, k, change, vertical: false);

    /// <summary>
    /// <see cref="ShareColumns"/> for vertical splits; content-sized parts are skipped and keep their setting-driven height.
    /// </summary>
    private static Dictionary<int, double> ShareRows(Grid grid, List<(FrameworkElement Element, int Index, bool Fits)> parts, int k, double change) =>
        Share(grid, parts, k, change, vertical: true);

    /// <summary>
    /// Redistributes around border k (before part k): converts star shares to DIPs (as TakeSizes) so unaffected parts keep size,
    /// then takes from one side nearest-first down to minimums and gives to the nearest part opposite; excess is dropped.
    /// Returns every sharing part's new DIP size by index; the screen updates on the next layout.
    /// </summary>
    private static Dictionary<int, double> Share(Grid grid, List<(FrameworkElement Element, int Index, bool Fits)> parts, int k, double change, bool vertical)
    {
        var sizes = InDips(grid, parts, vertical);
        var (before, after) = (Sharing(parts, 0, k).Reverse().ToList(), Sharing(parts, k, parts.Count).ToList());
        if (before.Count == 0 || after.Count == 0 || !double.IsFinite(change) || change == 0)
            return sizes;

        var (givers, gainer) = change > 0 ? (after, before[0]) : (before, after[0]);
        var (wanted, taken) = (Math.Abs(change), 0.0);
        foreach (var giver in givers)
        {
            var take = Math.Min(Math.Max(sizes[giver] - LeastOf(grid, parts[giver].Element, vertical), 0), wanted - taken);
            if (take <= 0)
                continue;
            sizes[giver] -= take;
            taken += take;
            if (taken >= wanted)
                break;
        }

        sizes[gainer] += taken;
        foreach (var (i, size) in sizes)
            SetDips(grid, parts[i].Element, vertical, size);
        return sizes;
    }

    /// <summary>Indexes of star-sized (not content-sized) parts in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    private static IEnumerable<int> Sharing(List<(FrameworkElement Element, int Index, bool Fits)> parts, int from, int to) =>
        Enumerable.Range(from, Math.Max(to - from, 0)).Where(i => !parts[i].Fits);

    /// <summary>Converts each star part to a pixel-star share equal to its on-screen size (min 1); returns sizes by index.</summary>
    private static Dictionary<int, double> InDips(Grid grid, List<(FrameworkElement Element, int Index, bool Fits)> parts, bool vertical)
    {
        var sizes = new Dictionary<int, double>();
        foreach (var i in Sharing(parts, 0, parts.Count))
        {
            var element = parts[i].Element;
            sizes[i] = Math.Max(vertical ? grid.RowDefinitions[Grid.GetRow(element)].ActualHeight : grid.ColumnDefinitions[Grid.GetColumn(element)].ActualWidth, 1);
            SetDips(grid, element, vertical, sizes[i]);
        }

        return sizes;
    }

    private static void SetDips(Grid grid, FrameworkElement element, bool vertical, double size)
    {
        var length = new GridLength(Math.Max(size, 1), GridUnitType.Star);
        if (vertical)
            grid.RowDefinitions[Grid.GetRow(element)].Height = length;
        else
            grid.ColumnDefinitions[Grid.GetColumn(element)].Width = length;
    }

    private static double LeastOf(Grid grid, FrameworkElement element, bool vertical) =>
        vertical ? grid.RowDefinitions[Grid.GetRow(element)].MinHeight : grid.ColumnDefinitions[Grid.GetColumn(element)].MinWidth;

    /// <summary>
    /// Border double-click: splits the combined size of the nearest star part on each side evenly (respecting minimums); others
    /// unchanged. Persists immediately (shares written, Changed raised).
    /// </summary>
    private void EvenOut(SplitView view, int k)
    {
        var (grid, parts, vertical) = (view.Grid, view.Parts, view.Split.Vertical);
        var sizes = InDips(grid, parts, vertical);
        if (Sharing(parts, 0, k).Cast<int?>().LastOrDefault() is not { } first || Sharing(parts, k, parts.Count).Cast<int?>().FirstOrDefault() is not { } second)
            return;

        var total = sizes[first] + sizes[second];
        var half = Math.Max(LeastOf(grid, parts[first].Element, vertical), Math.Min(total / 2, total - LeastOf(grid, parts[second].Element, vertical)));
        (sizes[first], sizes[second]) = (half, Math.Max(total - half, 1));
        foreach (var (i, size) in sizes)
            SetDips(grid, parts[i].Element, vertical, size);
        KeepShares(view, sizes);
    }

    /// <summary>Moves a border by steps of 16 DIPs (like GridSplitter.KeyboardIncrement), shared as a drag and persisted. Borders are not focusable.</summary>
    private void Nudge(SplitView view, int k, double change) =>
        KeepShares(view, Share(view.Grid, view.Parts, k, change * BorderKeyStep, view.Split.Vertical));

    // How far a border moves for each step (Nudge), DIPs.
    private const double BorderKeyStep = 16;

    /// <summary>Writes the given DIP sizes as shares and saves; used instead of TakeSizes because the screen has not updated yet.</summary>
    private void KeepShares(SplitView view, Dictionary<int, double> sizes)
    {
        var wasBuilt = Signature() == _builtSignature;
        foreach (var (i, size) in sizes)
        {
            var index = view.Parts[i].Index;
            if (index < view.Split.Sizes.Count)
                view.Split.Sizes[index] = Math.Max(size, 1);
        }

        StillBuilt(view, wasBuilt);
        Changed?.Invoke();
    }

    /// <summary>
    /// After border-driven share writes (TakeSizes, KeepShares) that the screen already shows, updates the last-layout signature so a
    /// later Load/Apply/Reset to pre-drag sizes is detected and re-applied in place (RebindOrRebuild). Skipped for splits replaced
    /// by a rebuild (a cancelled drag can end after one) or when a layout is pending.
    /// </summary>
    private void StillBuilt(SplitView view, bool wasBuilt)
    {
        if (!wasBuilt || !_splitViews.Contains(view))
            return;

        if (view.Side is null)
            MarkAllBuilt();
        else
            MarkSideBuilt(view.Side);
    }

    /// <summary>
    /// Border grip Thumb: handles double-click on the second press (so no drag starts whose end would restore pre-click sizes) and
    /// arrow keys if ever focused (not focusable by default).
    /// </summary>
    private sealed class SeamThumb : Thumb
    {
        /// <summary>Vertical split (Up/Down move it) rather than horizontal (Left/Right).</summary>
        public bool Vertical { get; init; }

        /// <summary>What a double-click does; null for nothing.</summary>
        public Action? DoubleClicked { get; init; }

        /// <summary>Raised on an arrow key: -1 (left/up) or 1 (right/down).</summary>
        public Action<double>? Nudged { get; init; }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && DoubleClicked is { } doubleClicked)
            {
                e.Handled = true;
                doubleClicked();
                return;
            }

            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            var way = e.Key switch
            {
                Key.Left when !Vertical => -1,
                Key.Up when Vertical => -1,
                Key.Right when !Vertical => 1,
                Key.Down when Vertical => 1,
                _ => 0,
            };
            if (way != 0 && Nudged is { } nudged)
            {
                e.Handled = true;
                nudged(way);
                return;
            }

            base.OnKeyDown(e);
        }
    }

    // Border grip thickness across a 1 px track, overlapping adjacent parts (SeamGrip).
    internal const double SeamGrab = 7;

    /// <summary>
    /// Grip geometry: <see cref="SeamGrab"/> px across the 1 px track, 3 px into each side, or 6 px into one side when the other is a
    /// floating pane (<paramref name="floatsBefore"/> above/left, <paramref name="floatsAfter"/> below/right) whose native window
    /// would swallow input. Returns the Margin and the 1 px line's offset within it (Thumb line margin, or QuietSplitter Padding).
    /// </summary>
    internal static (Thickness Grip, Thickness Line) SeamGrip(bool vertical, bool floatsBefore, bool floatsAfter)
    {
        const double reach = SeamGrab - 1;
        var (back, ahead) = floatsBefore == floatsAfter ? (reach / 2, reach / 2) : floatsBefore ? (0.0, reach) : (reach, 0.0);
        return vertical
            ? (new Thickness(0, -back, 0, -ahead), new Thickness(0, back, 0, ahead))
            : (new Thickness(-back, 0, -ahead, 0), new Thickness(back, 0, ahead, 0));
    }

    /// <summary>
    /// Border template: a 1 px PaneSeamBrush line on the seam (<paramref name="line"/> from SeamGrip), accent-coloured on hover or
    /// drag (matching App.xaml's QuietSplitter).
    /// </summary>
    private static ControlTemplate SplitterLook(Thickness line)
    {
        var template = new ControlTemplate(typeof(Thumb));
        var bar = new FrameworkElementFactory(typeof(Border));
        bar.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var seam = new FrameworkElementFactory(typeof(Border), "line");
        seam.SetValue(FrameworkElement.MarginProperty, line);
        seam.SetResourceReference(Border.BackgroundProperty, "PaneSeamBrush");
        bar.AppendChild(seam);
        template.VisualTree = bar;
        foreach (var (property, brush) in new[] { (UIElement.IsMouseOverProperty, "AccentBrush"), (Thumb.IsDraggingProperty, "AccentBrush"), (UIElement.IsKeyboardFocusedProperty, "AccentHoverBrush") })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(brush), "line"));
            template.Triggers.Add(trigger);
        }

        return template;
    }

    /// <summary>
    /// Whether a part's <paramref name="edge"/> belongs to a visible floating pane (DockPane.Floats), whose native window covers
    /// overlays, so the border grip extends only into the other part (SeamGrip).
    /// </summary>
    private bool FloatsAt(DockNode node, DockZone edge) =>
        FloatsAt(node, edge, id => _panes.TryGetValue(id, out var pane) && pane.IsShown(), id => _panes.TryGetValue(id, out var pane) && pane.Floats);

    /// <summary>
    /// Window-free <see cref="FloatsAt(DockNode, DockZone)"/> driven by <paramref name="shown"/>/<paramref name="floats"/> (golden
    /// layout-edge-to-edge). Group: its visible pane floats; split across the edge: any part; split along it: the outermost visible part.
    /// </summary>
    internal static bool FloatsAt(DockNode node, DockZone edge, Func<string, bool> shown, Func<string, bool> floats)
    {
        if (node.IsGroup)
            return node.Panes.Any(p => shown(p) && floats(p));

        var along = node.Vertical == (edge is DockZone.Top or DockZone.Bottom);
        if (!along)
            return node.Children.Any(c => FloatsAt(c, edge, shown, floats));

        var showing = node.Children.Where(c => c.AllPanes().Any(shown)).ToList();
        var outer = edge is DockZone.Bottom or DockZone.Right ? showing.LastOrDefault() : showing.FirstOrDefault();
        return outer is not null && FloatsAt(outer, edge, shown, floats);
    }

    /// <summary>After a border drag, writes on-screen part sizes as shares on the current split node (SplitView).</summary>
    private void TakeSizes(SplitView view)
    {
        var wasBuilt = Signature() == _builtSignature;
        var split = view.Split;
        foreach (var (element, index, fits) in view.Parts)
        {
            if (fits || index >= split.Sizes.Count)
                continue;
            split.Sizes[index] = Math.Max(split.Vertical ? element.ActualHeight : element.ActualWidth, 1);
        }

        StillBuilt(view, wasBuilt);
        Changed?.Invoke();
    }

    private bool FitsContent(DockNode node)
    {
        var shown = node.AllPanes().Where(p => _panes[p].IsShown()).ToList();
        return node.IsGroup && shown.Count > 0 && shown.All(p => _panes[p].FitsContent && _panes[p].Handle != PaneHandle.Tab);
    }

    /// <summary>
    /// A part's minimum width: group = widest visible pane minimum; horizontal split = sum; vertical split = max. A group with a
    /// joined header is at least the bar plus active tab, capped at <see cref="JoinedLeast"/> over its own minimum (an uncapped
    /// 300+ DIP bar froze the adjacent border); narrower, the tab moves to the overflow menu.
    /// </summary>
    private double MinWidthOf(DockNode node)
    {
        if (!node.IsGroup)
            return node.Vertical ? node.Children.Select(MinWidthOf).DefaultIfEmpty(0).Max() : node.Children.Sum(MinWidthOf);

        var least = node.Panes.Where(p => _panes[p].IsShown()).Select(p => _panes[p].MinWidth()).DefaultIfEmpty(0).Max();
        return _joined is { } joined && ReferenceEquals(joined.Under, node) ? Math.Max(least, Math.Min(_joinedWidth, Math.Max(least, JoinedLeast))) : least;
    }

    // Minimum width of the header-joined group: bar + active tab + row margins.
    private double _joinedWidth;

    /// <summary>Cap (DIPs) on the extra minimum width a joined bar adds to its group.</summary>
    internal const double JoinedLeast = 240;

    /// <summary>Last laid-out joined bar width plus active tab (golden dock-splitter-range); 0 when nothing is joined.</summary>
    internal double JoinedWidth => _joined is null ? 0 : _joinedWidth;

    /// <summary>The one pane a part shows, when it is a group showing only one; null otherwise.</summary>
    private string? Alone(DockNode node) =>
        node.IsGroup && node.Panes.Where(p => _panes[p].IsShown()).Take(2).ToList() is [var only] ? only : null;

    /// <summary>The single visible pane of a part (group or split); null for none or several.</summary>
    private string? AloneIn(DockNode node) => node.AllPanes().Where(p => _panes[p].IsShown()).Take(2).ToList() is [var only] ? only : null;

    /// <summary>
    /// Whether the part under the header can host it in its tab row: a tabbed group or one whose panes all have titles; a lone
    /// titleless pane (the player) cannot.
    /// </summary>
    private bool CanJoin(DockNode node)
    {
        if (!node.IsGroup || node.Panes.Where(p => _panes[p].IsShown()).ToList() is not { Count: > 0 } shown)
            return false;

        return shown.Any(p => _panes[p].Handle == PaneHandle.Tab) || (shown.Count > 1 && shown.Any(p => _panes[p].Handle == PaneHandle.Hover))
            || shown.All(p => PaneTitleSlot.Of(p)?.Parent is PaneTitleRow);
    }

    /// <summary>Applies the layout's shape to a reshapeable pane (<see cref="DockPane.Reshape"/>).</summary>
    private void SetShape(string pane, PaneShape shape)
    {
        _shapes[pane] = shape;
        _panes[pane].Reshape?.Invoke(shape);
    }

    /// <summary>
    /// Docks a joining pane (the header) at the right end of the boxed tab row below it (<see cref="CanJoin"/>), merging both
    /// headers into one bar; the pane's own controls stay in its panel.
    /// </summary>
    private bool JoinTo(TabGroupHost tabs, DockNode under, string pane)
    {
        SetShape(pane, PaneShape.Joined);
        var bar = JoinedBar(pane);
        tabs.Trailing.Child = bar;
        _joined = (pane, bar, under);

        // Width for the whole bar plus the widest tab (so any active tab fits; others overflow) and both margins.
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
    /// A joined pane's slot in the tab row: a drag grip (it has no tab there) followed by the pane, right-aligned and unclipped.
    /// </summary>
    private FrameworkElement JoinedBar(string pane)
    {
        var dots = new TextBlock { Text = Glyphs.GripperBarVertical, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
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
}
