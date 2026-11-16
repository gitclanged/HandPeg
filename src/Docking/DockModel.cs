namespace HandPegApp.Docking;

/// <summary>
/// Dock tree node: a tabbed group (one visible pane) or a split of parts with star shares. One class for both kinds so it
/// serializes simply as JSON in per-mode settings.
/// </summary>
public sealed class DockNode
{
    public const string GroupKind = "group", SplitKind = "split";

    public string Kind { get; set; } = GroupKind;

    /// <summary>Vertical split (true) or horizontal.</summary>
    public bool Vertical { get; set; }

    /// <summary>Split children, left-to-right or top-to-bottom.</summary>
    public List<DockNode> Children { get; set; } = [];

    /// <summary>Star-size shares, one per child.</summary>
    public List<double> Sizes { get; set; } = [];

    /// <summary>Group pane ids in tab order.</summary>
    public List<string> Panes { get; set; } = [];

    /// <summary>Active pane of a group.</summary>
    public string? Active { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGroup => Kind == GroupKind;

    public static DockNode Group(params string[] panes) => new() { Kind = GroupKind, Panes = [.. panes], Active = panes.FirstOrDefault() };

    public static DockNode Split(bool vertical, params (DockNode Node, double Size)[] parts) =>
        new() { Kind = SplitKind, Vertical = vertical, Children = [.. parts.Select(p => p.Node)], Sizes = [.. parts.Select(p => p.Size)] };

    /// <summary>Every pane under this part, in order.</summary>
    public IEnumerable<string> AllPanes() => IsGroup ? Panes : Children.SelectMany(c => c.AllPanes());

    /// <summary>Every group under this part, in order.</summary>
    public IEnumerable<DockNode> AllGroups() => IsGroup ? [this] : Children.SelectMany(c => c.AllGroups());

    /// <summary>The group a pane is in, under this part; null when it is not here.</summary>
    public DockNode? GroupOf(string pane) => AllGroups().FirstOrDefault(g => g.Panes.Contains(pane));

    /// <summary>The split directly holding a part, under this one; null for the top part (or one not here).</summary>
    public DockNode? ParentOf(DockNode node)
    {
        if (IsGroup)
            return null;

        foreach (var child in Children)
        {
            if (ReferenceEquals(child, node))
                return this;
            if (child.ParentOf(node) is { } found)
                return found;
        }

        return null;
    }
}

/// <summary>Drop zone relative to the target part: centre (as a tab) or one side.</summary>
public enum DockZone
{
    Center,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// Main-window dock layout for one mode. Legacy <c>Side*</c> members (single side window) are read once to migrate into the first Pane
/// Docker (<see cref="SideWindowStates.Migrate"/>) and never written; side windows now live in AppSettings.SideWindows.
/// </summary>
public sealed class DockLayoutState
{
    /// <summary>
    /// Current layout format version. 2: UnifiedTimeline replaced the Layers and Subtitles tabs. 3: Timeline is the first tab of its
    /// group with the settings tabs. 4: Standard Editing's Canvas tab sits beside Effects/Keyframes. 5: Standard Editing's settings tabs
    /// sit beside Media, Timeline alone at the bottom. Older layouts upgrade stepwise in memory on load (DockManager.Upgrade).
    /// </summary>
    public const int CurrentVersion = 5;

    /// <summary>Format version this layout was saved with (1 when absent).</summary>
    public int Version { get; set; } = CurrentVersion;

    public DockNode Main { get; set; } = DockNode.Group();

    /// <summary>Legacy side window panes; null for none.</summary>
    public DockNode? Side { get; set; }

    /// <summary>Legacy: whether the side window was open.</summary>
    public bool SideOpen { get; set; }

    /// <summary>Legacy side window bounds (left, top, width, height in DIPs); null for default.</summary>
    public double[]? SideBounds { get; set; }

    public bool SideMaximized { get; set; }

    /// <summary>Legacy side window panes at last close, restored on reopen.</summary>
    public DockNode? SideClosed { get; set; }

    /// <summary>Deep copy.</summary>
    public DockLayoutState Copy() => System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(this, Json), Json)!;

    // Source-generated metadata (Services.AppSettingsJsonContext): layouts are read on the UI thread before Frame 1.
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<DockLayoutState> Json => Services.AppSettingsJsonContext.Default.DockLayoutState;

    /// <summary>Deserializes a saved layout; null when empty or invalid.</summary>
    public static DockLayoutState? Read(string? saved)
    {
        try
        {
            return saved is { Length: > 0 } ? System.Text.Json.JsonSerializer.Deserialize(saved, Json) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Side window mode: player mirror or free pane layout.</summary>
public enum SideWindowMode
{
    LiveVideoPreview,
    PaneDocker,
}

/// <summary>
/// Persisted side window state: mode, panes (Pane Docker only) and placement; stored as a JSON list in AppSettings.SideWindows.
/// </summary>
public sealed class SideWindowState
{
    public const string PanesMode = "panes", PreviewMode = "preview";

    /// <summary>Stable side window id (persisted).</summary>
    public string Id { get; set; } = "";

    /// <summary><see cref="PanesMode"/> (a Pane Docker) or <see cref="PreviewMode"/> (a Live Video Preview).</summary>
    public string Mode { get; set; } = PanesMode;

    /// <summary>A Pane Docker's panes; null while it holds none, and always for a Live Video Preview.</summary>
    public DockNode? Tree { get; set; }

    /// <summary>
    /// Restored rect from GetWindowPlacement (left, top, right, bottom in workspace pixels), DPI-independent; null for default placement.
    /// </summary>
    public int[]? Placement { get; set; }

    /// <summary>Legacy DIP bounds (left, top, width, height), used once when Placement is absent.</summary>
    public double[]? Bounds { get; set; }

    public bool Maximized { get; set; }

    /// <summary>Full screen over the whole monitor including taskbar (F11 or picture double-click).</summary>
    public bool FullScreen { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPreview => Mode == PreviewMode;

    [System.Text.Json.Serialization.JsonIgnore]
    public SideWindowMode WindowMode => IsPreview ? SideWindowMode.LiveVideoPreview : SideWindowMode.PaneDocker;

    public static string ModeOf(SideWindowMode mode) => mode == SideWindowMode.LiveVideoPreview ? PreviewMode : PanesMode;
}

/// <summary>AppSettings.SideWindows serialization via generated metadata; UI-free.</summary>
public static class SideWindowStates
{
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<SideWindowState>> Json => Services.AppSettingsJsonContext.Default.ListSideWindowState;

    /// <summary>Deserializes side windows; empty when missing or invalid. Unknown modes become Pane Dockers.</summary>
    public static List<SideWindowState> Read(string? saved)
    {
        try
        {
            var list = saved is { Length: > 0 } ? System.Text.Json.JsonSerializer.Deserialize(saved, Json) ?? [] : [];
            list.RemoveAll(s => s is null);
            foreach (var side in list)
            {
                if (side.Mode != SideWindowState.PreviewMode)
                    side.Mode = SideWindowState.PanesMode;
            }

            return list;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static string Write(IEnumerable<SideWindowState> sides) => System.Text.Json.JsonSerializer.Serialize(sides.ToList(), Json);

    /// <summary>Deep copy of a side window state.</summary>
    public static SideWindowState Copy(SideWindowState side) =>
        System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(side, Services.AppSettingsJsonContext.Default.SideWindowState), Services.AppSettingsJsonContext.Default.SideWindowState)!;

    /// <summary>
    /// Migrates a legacy single side window into <paramref name="sides"/> as the first Pane Docker when it was open with panes (keeping
    /// placement and maximized state) and its id is not already present. Legacy fields are always cleared (a closed one's panes are
    /// already in the main window). Returns true when a docker was added.
    /// </summary>
    public static bool Migrate(DockLayoutState state, List<SideWindowState> sides)
    {
        var added = false;
        if (state.SideOpen && state.Side is { } tree && tree.AllPanes().Any() && sides.All(s => s.Id != LegacyId))
        {
            sides.Insert(0, new SideWindowState
            {
                Id = LegacyId, Mode = SideWindowState.PanesMode, Tree = tree,
                Bounds = state.SideBounds is { Length: 4 } bounds ? [.. bounds] : null, Maximized = state.SideMaximized,
            });
            added = true;
        }

        (state.Side, state.SideOpen, state.SideBounds, state.SideMaximized, state.SideClosed) = (null, false, null, false, null);
        return added;
    }

    /// <summary>Id assigned to the migrated legacy side window.</summary>
    public const string LegacyId = "side-1";

    /// <summary>New unique id: "side-" plus the lowest free number in <paramref name="sides"/>.</summary>
    public static string NewId(IEnumerable<SideWindowState> sides)
    {
        var taken = sides.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var n = 1;
        while (taken.Contains("side-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            n++;
        return "side-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Enforces each pane in at most one tree (side windows win over the main window), removes unknown panes, empties previews and
    /// tidies trees (an empty Pane Docker holds null and stays open). The main window root is never null.
    /// </summary>
    public static void Clean(DockLayoutState state, IEnumerable<SideWindowState> sides, Func<string, bool> known)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DockNode? Clean(DockNode? node)
        {
            if (node is null)
                return null;
            foreach (var group in node.AllGroups())
            {
                group.Panes = group.Panes.Where(p => known(p) && seen.Add(p)).ToList();
                if (group.Active is { } active && !group.Panes.Contains(active))
                    group.Active = group.Panes.FirstOrDefault();
            }

            return DockTree.Tidy(node);
        }

        foreach (var side in sides)
            side.Tree = side.IsPreview ? null : Clean(side.Tree);
        state.Main = Clean(state.Main) ?? DockNode.Group();
    }
}

/// <summary>Dock tree mutations; each keeps the tree tidy (no empty groups, no single-child splits).</summary>
public static class DockTree
{
    /// <summary>Removes a pane; returns the (possibly new) root, or null when empty.</summary>
    public static DockNode? Remove(DockNode root, string pane)
    {
        if (root.GroupOf(pane) is not { } group)
            return root;

        group.Panes.Remove(pane);
        if (group.Active == pane)
            group.Active = group.Panes.FirstOrDefault();
        return Tidy(root);
    }

    /// <summary>
    /// Normalises a tree: drops empty groups, collapses single-child splits, flattens same-direction nested splits. Returns the root,
    /// or null when empty.
    /// </summary>
    public static DockNode? Tidy(DockNode node)
    {
        if (node.IsGroup)
            return node.Panes.Count > 0 ? node : null;

        var children = new List<DockNode>();
        var sizes = new List<double>();
        for (var i = 0; i < node.Children.Count; i++)
        {
            var size = i < node.Sizes.Count && node.Sizes[i] > 0 ? node.Sizes[i] : 1;
            if (Tidy(node.Children[i]) is not { } child)
                continue;

            if (!child.IsGroup && child.Vertical == node.Vertical)
            {
                // Same-direction nested split: hoist its children, scaling shares by its share.
                var total = child.Sizes.Sum();
                for (var j = 0; j < child.Children.Count; j++)
                {
                    children.Add(child.Children[j]);
                    sizes.Add(size * (total > 0 ? child.Sizes[j] / total : 1.0 / child.Children.Count));
                }
            }
            else
            {
                children.Add(child);
                sizes.Add(size);
            }
        }

        if (children.Count == 0)
            return null;
        if (children.Count == 1)
            return children[0];

        (node.Children, node.Sizes) = (children, sizes);
        return node;
    }

    /// <summary>Docks a part beside another (or beside the whole tree at the root); returns the root.</summary>
    /// <param name="share">Fraction of the target's space given to the new part.</param>
    public static DockNode InsertBeside(DockNode root, DockNode target, DockZone zone, DockNode part, double share = 0.5)
    {
        var vertical = zone is DockZone.Top or DockZone.Bottom;
        var before = zone is DockZone.Left or DockZone.Top;
        share = Math.Clamp(share, 0.05, 0.95);
        var parent = root.ParentOf(target);
        if (parent is not null && parent.Vertical == vertical)
        {
            var index = parent.Children.IndexOf(target);
            var size = index < parent.Sizes.Count ? parent.Sizes[index] : 1;
            parent.Sizes[index] = size * (1 - share);
            parent.Children.Insert(before ? index : index + 1, part);
            parent.Sizes.Insert(before ? index : index + 1, size * share);
            return root;
        }

        var split = DockNode.Split(vertical, before ? (part, share) : (target, 1 - share), before ? (target, 1 - share) : (part, share));
        if (parent is null)
            return split;

        var at = parent.Children.IndexOf(target);
        parent.Children[at] = split;
        return root;
    }
}
