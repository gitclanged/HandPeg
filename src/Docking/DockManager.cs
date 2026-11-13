using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp.Docking;

/// <summary>
/// Arranges the panes of the main window and of the side windows (5.4C: any number, each a Pane Docker or a Live Video Preview),
/// as a tree of groups and splits (<see cref="DockNode"/>) for each window that holds panes, and lets them be rearranged by
/// dragging their tabs: onto a part (into its group, as a tab) or to one side of it (a new part beside it), to a window's edge,
/// or out of every window (into a new Pane Docker, opened where it is let go).
///
/// A pane's content is made once and kept; laying the windows out again (after a move, a pane shown or hidden, a mode
/// change) takes every content out of where it was and puts it where the tree says. A hidden pane keeps its place in
/// the tree and is simply not shown. A pane's tab is where its title was: in its <see cref="PaneTitleSlot"/>, over its
/// top-left corner when the pointer comes there (a pane with no title), or in a row of tabs (the settings tabs).
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
    /// One side window (5.4C): what is saved of it, and its window and what its panes are laid out in once it is open (null
    /// before the main window is shown: at launch the side windows open after the first frame).
    /// </summary>
    private sealed class SideHost(SideWindowState state)
    {
        public SideWindowState State { get; set; } = state;

        public SideWindow? Window { get; set; }

        public WindowRoot? Root { get; set; }

        /// <summary>Opens under the pointer (a pane let go outside every window), rather than where it was.</summary>
        public bool AtPointer { get; set; }
    }

    // The side windows, in the order they were opened (their numbers in the menus: 1 the first).
    private readonly List<SideHost> _sides = [];

    // The side window whose tree is being laid out now (Render): its groups are known as its own.
    private SideHost? _renderingFor;

    // A press on a tab, and the drag it may become.
    private (string Pane, FrameworkElement Tab, Point At)? _press;

    // A tab chosen from the keyboard: its strip is made again, and the new tab of that pane takes the focus.
    private string? _focusTabOf;
    private bool _dragging;

    // Where a dragged pane goes if let go now: a group (or a window's edge), the side of it, and, among a row of tabs,
    // the place there (counted among the tabs shown).
    private (SideHost? Side, DockNode? Group, DockZone Zone, bool Outside, int? At)? _target;

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
    private readonly Dictionary<string, (Border StandIn, Grid Box)> _floating = [];

    public DockManager(Window main, ContentControl mainHost, Func<DockLayoutState> defaults)
    {
        (_main, _mainHost, _defaults) = (main, mainHost, defaults);
        _mainHost.Content = _mainRoot;
        _main.PreviewKeyDown += (_, e) => CancelDragOnEscape(e);
    }

    /// <summary>Raised when the arrangement has changed, to be saved.</summary>
    public event Action? Changed;

    /// <summary>Raised when a side window has opened or closed, or changed its mode (5.4C): the Layout Selector's side-window rows and the mirrors follow.</summary>
    public event Action? SideChanged;

    /// <summary>Raised when a side window's window has been made, before it is shown: for the main window to hand it keys and drops.</summary>
    public event Action<SideWindow>? SideWindowOpened;

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

    /// <summary>What a tab's menu's Reset Panes Layout does (5.4D: the window's own, the user's Default layout put back); null, <see cref="Reset"/>.</summary>
    public Action? ResetLayout { get; init; }

    /// <summary>The side windows open now (5.4C), in the order they were opened; those whose window is not made yet are left out.</summary>
    public IReadOnlyList<SideWindow> SideWindows => _sides.Select(s => s.Window).OfType<SideWindow>().ToList();

    /// <summary>Whether any side window is open (or waits to open, at launch).</summary>
    public bool IsSideOpen => _sides.Count > 0;

    /// <summary>
    /// The side windows open now, for the Layout Selector's rows (5.4C): each one's id, number (1 the first opened), mode and name
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

    /// <summary>The tree a pane is in, in the main window or a side window, with the side window holding it (null: the main window).</summary>
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

    /// <summary>
    /// Takes up a saved arrangement (null or unreadable: the mode's own), puts in what it lacks, and lays the windows out. With
    /// <paramref name="showFirst"/> (5.4A: the Timeline at start-up), the group that holds that pane shows it, whatever tab was showing
    /// as the arrangement was saved: in the same one build, with nothing raised and nothing saved (the arrangement is saved as it is the
    /// next time the user changes it).
    /// <para>5.4C: <paramref name="sideWindows"/> are the side windows saved with it (AppSettings.SideWindows); null (none saved yet, or
    /// the arrangement taken up again after a preview) keeps the side windows as they are, and takes up the single side window of an
    /// arrangement saved before 5.4C as the first Pane Docker. At launch the side windows open after the main window's first frame,
    /// each laid out on its own: the main window's one build before its first frame is the only one it has.</para>
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
    /// Makes the group holding a pane show it (5.4A), in the main window and in the side window while it is open; the arrangement the
    /// side window closed on is left as the user made it. No other group is touched. True when anything changed. Window-free.
    /// 5.4C: in every Pane Docker given too (<paramref name="sides"/>).
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
    /// The side windows as a load has them (5.4C): one whose id is open already keeps its window and takes its new state (its
    /// tree; a change of mode is shown by its window), one no longer listed closes (its panes are in the main window already: the
    /// load placed them), and a new one is made once the main window can own it (ApplySideWindows).
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
    /// Sets the least width and height of every part laid out again, in place (5.4D): a pane's own least size can change with no layout to
    /// follow (the Media Browser's Filmstrip view is wider, MainWindow.MediaMinWidth). Nothing is laid out again, raised or saved.
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
    /// Whether a saved arrangement lays the main window out as a layout does (5.4D, the Default layout at start-up: <see cref="SameShape"/>),
    /// each taken up as <see cref="Load"/> and <see cref="Apply"/> would take it with the side windows saved: brought up to date, the panes
    /// it lacks put in, those a Pane Docker holds left out. The studios (DockPane.Optional) do not count: each is taken out of both first, so
    /// opening or closing one never decides whether the arrangement left is the layout's. Nothing is laid out, raised or saved; false when the
    /// arrangement cannot be read.
    /// </summary>
    public bool LoadsAlike(string? saved, DockLayoutState layout, string? sideWindows)
    {
        if (DockLayoutState.Read(saved) is not { } kept)
            return false;

        var sides = SideWindowStates.Read(sideWindows);
        return SameShape(WithoutStudios(Validate(kept, sides, migrate: true)), WithoutStudios(Validate(layout.Copy(), sides)));
    }

    /// <summary>A tree of its own (a copy) with the panes that are shown only while it holds them (DockPane.Optional: the studios) taken out, and tidied: for LoadsAlike.</summary>
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

    /// <summary>The main window's arrangement, as saved (AppSettings.DockLayout). The side windows are saved apart (<see cref="SaveSideWindows"/>).</summary>
    public string Save() => JsonSerializer.Serialize(_state, Services.AppSettingsJsonContext.Default.DockLayoutState);

    /// <summary>The side windows open now, as saved (AppSettings.SideWindows, 5.4C): each one's mode, panes and place ("[]" for none).</summary>
    public string SaveSideWindows()
    {
        RememberPlacements();
        return SideWindowStates.Write(_sides.Select(s => s.State));
    }

    /// <summary>
    /// Every pane back where the mode starts with it (5.4D: where <paramref name="layout"/> has it, the user's Default layout; null, the
    /// defaults): every Pane Docker closed (5.4C; a Live Video Preview, holding no pane, stays).
    /// </summary>
    public void Reset(DockLayoutState? layout = null)
    {
        foreach (var docker in _sides.Where(s => !s.State.IsPreview).ToList())
        {
            _sides.Remove(docker);
            CloseWindowOf(docker);
        }

        _state = Validate(layout?.Copy() ?? _defaults(), [.. _sides.Select(s => s.State)]);

        // As Load and Apply take a tree up: one laid out already (Reset twice; 5.4.4: or one that differs only in its shares and front tabs) is
        // taken up in place, so the borders and tabs change the tree that is saved, not the one it replaced.
        RebindOrRebuild();
        Changed?.Invoke();
        RaiseSideChanged();
    }

    /// <summary>
    /// A tree as it may be used: every pane known here in it exactly once (in the main window or a side window), panes no
    /// longer known taken out, and panes not in it yet put where the mode starts with them. 5.4C: a side window keeps the panes
    /// it holds (the main window's copy goes), a Live Video Preview holds none; with <paramref name="migrate"/> the single side
    /// window of an arrangement saved before 5.4C joins <paramref name="sides"/> as a Pane Docker (its legacy members are cleared
    /// in any case: they are never written again).
    /// </summary>
    private DockLayoutState Validate(DockLayoutState state, List<SideWindowState> sides, bool migrate = false)
    {
        // An older arrangement first takes the shape this HandPeg gives it (panes retired, their successors in their place).
        if (Upgrade is { } upgrade)
            state = upgrade(state);

        // A pane that replaced another goes where the old one was (the Media pane where the Source pane was, from before
        // Pass 1.5), unless the arrangement has it already.
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

        // The single side window of before 5.4C: the first Pane Docker (or, not migrating, nothing: it is never written again).
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

        // With the panes it is homed with (5.4C, a studio the mode's arrangement leaves out), as a tab of the first one's group.
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

    /// <summary>Whether the arrangement holds the pane, in the main window or a side window (5.4C).</summary>
    public bool Contains(string pane) => GroupAnywhere(pane) is not null;

    /// <summary>
    /// Shows a pane (5.4C, an optional studio): one the arrangement does not hold yet goes where the mode has it (or with its
    /// <see cref="DockPane.HomeWith"/> mates), then it is brought forward in its group.
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

    /// <summary>Takes a pane out of the arrangement (5.4C, an optional studio's close): it is shown again only by <see cref="Open"/>.</summary>
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

        // Nothing to do when the windows would come out as they are: every rebuild moves every pane.
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
        _roots.Add((_mainHost, null));

        foreach (var side in _sides)
            RenderSide(side);

        FillTitleSlots();
        PlaceFloating();
        WatchShapes();
    }

    /// <summary>
    /// Lays one side window out again (5.4C), and only it: its panes are taken out of where they were and put where its tree says;
    /// the main window and the other side windows are not touched (a side window opened after the first frame never lays the main
    /// window out again: Law 1's one dock build). A Live Video Preview holds no pane: only its picture is shown.
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

        // This window is as the arrangement says: a rebuild asked for now has nothing to do for it.
        MarkSideBuilt(side);
    }

    /// <summary>One side window's content: a Pane Docker's tree (its hint while it holds nothing), or nothing for a Live Video Preview.</summary>
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

    /// <summary>Puts each floating pane's box on the layer of the window its stand-in is in now; one not laid out stays where it is, hidden.</summary>
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
    /// Watches the slot of each pane that can change its shape and was laid out as a row (the header among tabs, alone
    /// in a window, or over nothing it can join): in a slot taller than wide, or too narrow for a row, it stands as a
    /// column, and as a row again once the slot is wide enough.
    /// </summary>
    private void WatchShapes()
    {
        // Afresh each time (a side window laid out on its own keeps the main window's panes, and their watches, where they are).
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

        // Its hover tab reads the way the pane stands now.
        if (slot is Panel host && host.Children.OfType<FrameworkElement>().Any(c => c.Tag as string == "dock-hover")
            && _groupHosts.FirstOrDefault(g => g.Group.Panes.Contains(pane)).Group is { } group)
        {
            RemoveHoverZones(host);
            host.Children.Add(HoverZone(group, pane));
        }
    }

    // What the windows were last laid out from: the arrangement, which panes are shown, and the side windows.
    private string? _builtSignature;

    // Which pane of a group is shown is not part of it: that is changed in place (see Activate), not by a rebuild.
    // 5.4C: each side window's mode, tree and window too (not its place: moving it lays nothing out).
    private string Signature() => MainSignature() + string.Concat(_sides.Select(SideSignature));

    private string MainSignature() => ActiveAttribute.Replace(JsonSerializer.Serialize(_state, Services.AppSettingsJsonContext.Default.DockLayoutState), "") + "|"
        + string.Join(",", _panes.Values.Where(p => p.IsShown()).Select(p => p.Id));

    private static string SideSignature(SideHost side) => "|" + side.State.Id + ":" + side.State.Mode + ":" + (side.Window?.GetHashCode() ?? 0) + ":"
        + (side.State.Tree is { } tree ? ActiveAttribute.Replace(JsonSerializer.Serialize(tree, Services.AppSettingsJsonContext.Default.DockNode), "") : "");

    // The parts of the last layout (5.4C): the main window's as it was last laid out, and each side window's.
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
    /// One side window laid out on its own, or one gone (<paramref name="side"/> null): the main window counts as it was last laid
    /// out, not as it may be now, so a layout of it still to come (a pane switched on) is never taken for done.
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

        // Right under the header, which joins it: a row of boxed tabs, as the main tab bar has, with the header at its end.
        // A pane headerless when alone (5.4.4, the Timeline) is one with a hover tab while it is: no title row, nothing over its top edge.
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
    /// second, flickering the tab and whatever the pointer was over beneath it (the header's Add Media flyout). 5.4C (the user's): the
    /// tab comes up only once the pointer has rested on the strip for <see cref="HoverTabRest"/>, so a pointer crossing it on its way to
    /// what is beside it (the Timeline's tabs under Playback Controls) never has a tab put over its target.
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
                // Past where the tab would be (the estimate was wide): the strip takes the tab's width, and nothing comes up.
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

    /// <summary>How long the pointer rests on a pane's hover strip before its tab comes up (5.4C).</summary>
    private static readonly TimeSpan HoverTabRest = TimeSpan.FromMilliseconds(350);

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
                // Boxed tabs: the panel the shown tab joins, as the settings tabs have always looked in the main tab bar. Square
                // (5.3-Stage2: edge to edge, no window showing at its corners).
                Frame = new Border { Child = Body, CornerRadius = new CornerRadius(0), Padding = TabPadding };
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
    /// The border between two parts of a split: dragged, it shares the room between them anew. Next to a part as tall as
    /// its content (the Sequence Overview Scrubber, the playback controls) it shares the room between the nearest parts above
    /// and below that are not (4.1.1: the player's row and the tabs; the timeline keeps the height its setting gives it);
    /// with no such part on one side it is only a line, or, beside a pane that can be made taller, it makes it so. Right under
    /// a pane that can be made taller (the Sequence Overview Scrubber), the border is that pane's lower edge: dragged, it makes
    /// it taller or shorter, and Ctrl+drag shares the room instead (5.2-AuditB, the user's bug: the bar could no longer be
    /// sized from its lower edge; the border there shared the room and moved it whole).
    /// <para>5.4D (the user's bug: a border that would not move): every border that shares the room drags across its whole range, the move
    /// taken from the part beside it and, once that one is at its least, from the parts beyond it (ShareColumns, ShareRows); a double-click
    /// evens the two parts beside it (EvenOut), or, on the Sequence Overview Scrubber's edge, puts its height back (DockPane.ResetSize).</para>
    /// </summary>
    private void AddBorder(SplitView view, int k)
    {
        var (grid, split, parts) = (view.Grid, view.Split, view.Parts);
        var (before, after) = (parts[k - 1], parts[k]);
        UIElement border;
        var (above, under) = (parts.Take(k).LastOrDefault(p => !p.Fits).Element, parts.Skip(k).FirstOrDefault(p => !p.Fits).Element);

        // Edge to edge (5.3-Stage2, the user's call): a border is a 1 px line (PaneSeamBrush) in a row or column of 1 px, and one
        // that is dragged is grabbed over 7 px, reaching 3 px into each part beside it (drawn over them: ZIndex). Beside a floating
        // pane (the player), whose native window covers what lies over its place, it reaches all 6 into the other part (SeamGrip).
        var (beforeEdge, afterEdge) = split.Vertical ? (DockZone.Bottom, DockZone.Top) : (DockZone.Right, DockZone.Left);
        var (grip, seam) = SeamGrip(split.Vertical, FloatsAt(split.Children[before.Index], beforeEdge), FloatsAt(split.Children[after.Index], afterEdge));
        if ((before.Fits || after.Fits) && split.Vertical && above is not null && under is not null)
        {
            // The pane beside it that fits its content is made taller or shorter (through its height setting), dragged away
            // from it or towards it: by a plain drag of its lower edge, by Ctrl+drag above it (4.2B-2).
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

                // The scrubber's edge is its height's: the keyboard shares the room only from a border whose plain drag does.
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
                // Dragged towards the pane that fits its content, it makes it smaller; away from it, taller. Its edge either way: a
                // double-click puts its height back (5.4D).
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
            // Between two parts that share the room (5.4D: a Thumb, where a GridSplitter traded room only between its two neighbours, so a
            // part at its least next to it froze it): the move cascades past a part at its least (ShareColumns, ShareRows). It takes no
            // keyboard focus, as the GridSplitter did not (a border taking it would take the arrow keys from the timeline); Nudged moves it
            // 16 DIPs a step for a caller that asks.
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

        // Over the parts beside it (added to the grid after it, the next one would cover half of its grip).
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

    /// <summary>The pane of a part that the border beside it can make taller or shorter (DockPane.ResizeBy), and its title; none, (null, "").</summary>
    private (Action<double>? Resize, string Title) Resizer(DockNode part)
    {
        foreach (var pane in part.AllPanes())
        {
            if (_panes[pane].ResizeBy is { } resize)
                return (resize, _panes[pane].Title);
        }

        return (null, "");
    }

    /// <summary>What a double-click on the edge of a part that can be made taller does (5.4D: DockPane.ResetSize, the Sequence Overview Scrubber's height put back).</summary>
    private Action? ResetterOf(DockNode part) => part.AllPanes().Select(p => _panes[p].ResetSize).FirstOrDefault(r => r is not null);

    /// <summary>
    /// During a border's drag, how far the pointer has moved down the split since the last time this was asked (since it was
    /// pressed, the first time), in the split's own DIPs: they stay where they are while the border moves with the pane it
    /// sizes, so that pane's edge keeps step with the pointer. A Thumb's own change is measured from the border, which has
    /// moved since: a pane sized by it ran ahead of the pointer, then waited for it.
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
    /// A split as laid out (5.4D): its grid, its parts (each a column or a row of it; Fits, as tall or as wide as its content), the side
    /// window it is in (null: the main window) and where it is in that window's tree (the children's indexes from its top), so a load of
    /// the same tree (Rebind) gives it the new tree's node and its borders change the arrangement that is saved.
    /// </summary>
    private sealed class SplitView(Grid grid, DockNode split, List<(FrameworkElement Element, int Index, bool Fits)> parts, SideHost? side, int[] path)
    {
        public Grid Grid { get; } = grid;

        public DockNode Split { get; set; } = split;

        public List<(FrameworkElement Element, int Index, bool Fits)> Parts { get; } = parts;

        public SideHost? Side { get; } = side;

        public int[] Path { get; } = path;
    }

    // The splits laid out now, in every window (5.4D): their least sizes are set again in place (RefreshMinSizes), and a load of the
    // same tree gives them its nodes (Rebind).
    private readonly List<SplitView> _splitViews = [];

    /// <summary>Where a node is in a tree: its children's indexes from the top (none for the top itself); null when it is not in it.</summary>
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
    /// A border dragged by <paramref name="change"/> DIPs (5.4D, the user's bug: the borders that would not move), the k-th of a side-by-side
    /// split: the part at its left gains what the parts at its right give up, the nearest first and, once that one is at its least, the next
    /// beyond it; dragged left, the other way round. So a border moves across its whole range, however narrow the part beside it already is.
    /// </summary>
    private static Dictionary<int, double> ShareColumns(Grid grid, List<(FrameworkElement Element, int Index, bool Fits)> parts, int k, double change) =>
        Share(grid, parts, k, change, vertical: false);

    /// <summary>
    /// <see cref="ShareColumns"/> for a split of parts over each other: the part above the k-th border gains what the parts under it give up,
    /// nearest first (4.1.1: the parts as tall as their content are skipped: they keep the height their setting gives them).
    /// </summary>
    private static Dictionary<int, double> ShareRows(Grid grid, List<(FrameworkElement Element, int Index, bool Fits)> parts, int k, double change) =>
        Share(grid, parts, k, change, vertical: true);

    /// <summary>
    /// The room of a split shared anew around its k-th border (the one before part k): every part with a share first has it put in DIPs (as
    /// TakeSizes keeps them), so the parts the move does not reach keep their size; then what the parts on one side give up, each down to its
    /// least, nearest first, goes to the nearest part on the other side. Whatever the move asks beyond what they can give is not moved.
    /// Returns every sharing part's size now (DIPs, by its place among the parts): the screen catches up at the next layout.
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

    /// <summary>The parts from <paramref name="from"/> to before <paramref name="to"/> that have a share of the room (not as wide or as tall as their content), by their place among the parts.</summary>
    private static IEnumerable<int> Sharing(List<(FrameworkElement Element, int Index, bool Fits)> parts, int from, int to) =>
        Enumerable.Range(from, Math.Max(to - from, 0)).Where(i => !parts[i].Fits);

    /// <summary>Every part with a share put in DIPs, its size on screen as a pixel-star share (at least 1): its size, by its place among the parts.</summary>
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
    /// A double-click on a border (5.4D): the two parts beside it that share the room (the nearest each side) each take half of what the two
    /// have together, each kept to its least; the others keep their size. Kept at once (the shares written, Changed raised).
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

    /// <summary>A border moved by a step (5.4D, the GridSplitter's KeyboardIncrement): 16 DIPs a step, shared as a drag is, and kept. The borders take no keyboard focus, as before.</summary>
    private void Nudge(SplitView view, int k, double change) =>
        KeepShares(view, Share(view.Grid, view.Parts, k, change * BorderKeyStep, view.Split.Vertical));

    // How far a border moves for each step (Nudge), DIPs.
    private const double BorderKeyStep = 16;

    /// <summary>The parts' sizes just given (in DIPs, by their place among the parts) as their shares, and the arrangement saved: the screen has not caught up yet, so they are not read from it (TakeSizes).</summary>
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
    /// A split's sizes just written from its borders (TakeSizes, KeepShares), the screen already showing them: when the windows were as the
    /// arrangement said just before, they still are (5.4D), so a later Load, Apply or Reset back to the sizes from before the drag sees what
    /// differs: its shares, set again in place (5.4.4, RebindOrRebuild; the whole layout again before). Not for a split a layout has replaced
    /// since (a canceled drag's end can come after a rebuild), nor when a layout was still to come.
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
    /// A border's grip (5.4D): a Thumb whose double-click is its own (taken on the second press, so no drag begins whose end would keep the
    /// sizes from before it), and whose arrow keys move it should it ever have the keyboard (it is not focusable: Thumb's default, as before 5.4D).
    /// </summary>
    private sealed class SeamThumb : Thumb
    {
        /// <summary>Across a split of parts over each other (Up and Down move it), rather than side by side (Left and Right).</summary>
        public bool Vertical { get; init; }

        /// <summary>What a double-click does; null for nothing.</summary>
        public Action? DoubleClicked { get; init; }

        /// <summary>An arrow key pressed while it has the keyboard: -1 (left or up) or 1 (right or down); null for nothing.</summary>
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

    // How wide a dragged border's grip is across it (5.3-Stage2): laid out in 1 px, reaching into the parts beside it (SeamGrip).
    internal const double SeamGrab = 7;

    /// <summary>
    /// Where a dragged border's grip lies (5.3-Stage2): <see cref="SeamGrab"/> px across the 1 px row (<paramref name="vertical"/>
    /// split) or column it is laid out in, reaching 3 px into each part beside it; or all 6 into one part when the other is a
    /// floating pane's (<paramref name="floatsBefore"/>: the part above or at its left; <paramref name="floatsAfter"/>: below or at
    /// its right), whose native window covers whatever lies over its place, so that half of the grip would never take the pointer.
    /// Its Margin, and where its 1 px line lies in it (the Thumb's line margin; App.xaml's QuietSplitter's Padding for a GridSplitter): on the seam either way.
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
    /// A dragged border's look: a 1 px line (PaneSeamBrush) across its grip, on the seam (<paramref name="line"/>: SeamGrip's), in the
    /// accent while the pointer is on it or it is dragged (5.4D: as App.xaml's QuietSplitter showed the borders that were GridSplitters).
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
    /// Whether a part's <paramref name="edge"/> is a floating pane's (DockPane.Floats: the player), shown: its native window covers
    /// anything laid over its place, so a border along that edge reaches into the other part only (SeamGrip).
    /// </summary>
    private bool FloatsAt(DockNode node, DockZone edge) =>
        FloatsAt(node, edge, id => _panes.TryGetValue(id, out var pane) && pane.IsShown(), id => _panes.TryGetValue(id, out var pane) && pane.Floats);

    /// <summary>
    /// <see cref="FloatsAt(DockNode, DockZone)"/> for the panes <paramref name="shown"/> and <paramref name="floats"/> say (window-free,
    /// for the goldens: layout-edge-to-edge). A group: one of its panes shown floats; a split across the edge (side by side, for the
    /// lower edge): any part's does; a split along it: the outermost part on that side showing anything.
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

    /// <summary>After a border was dragged: the parts' sizes on screen become their shares (of the split as the arrangement has it now: SplitView).</summary>
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
    /// A part's least width: a group's widest least among its panes shown; a split side by side, its parts' together; one over the other,
    /// its widest part's. A group with a pane joined to its title row (the header over the media) is no narrower than the joined bar and the
    /// tab shown beside it, up to <see cref="JoinedLeast"/> (5.4D: the whole bar, often 300 DIPs and more, held the column it was in at that
    /// width and froze the border beside it; narrower, the strip puts the tab in its "more" menu, the bar kept whole).
    /// </summary>
    private double MinWidthOf(DockNode node)
    {
        if (!node.IsGroup)
            return node.Vertical ? node.Children.Select(MinWidthOf).DefaultIfEmpty(0).Max() : node.Children.Sum(MinWidthOf);

        var least = node.Panes.Where(p => _panes[p].IsShown()).Select(p => _panes[p].MinWidth()).DefaultIfEmpty(0).Max();
        return _joined is { } joined && ReferenceEquals(joined.Under, node) ? Math.Max(least, Math.Min(_joinedWidth, Math.Max(least, JoinedLeast))) : least;
    }

    // The least width of the group a pane is joined to: the joined bar, the tab shown, and the row's margins.
    private double _joinedWidth;

    /// <summary>The most the joined bar holds its group's column at, beyond the group's own least (5.4D), DIPs.</summary>
    internal const double JoinedLeast = 240;

    /// <summary>The joined bar's own width with the tab shown beside it, as last laid out (5.4D, for the goldens: dock-splitter-range); 0 when no pane is joined.</summary>
    internal double JoinedWidth => _joined is null ? 0 : _joinedWidth;

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
}
