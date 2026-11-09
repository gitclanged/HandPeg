namespace HandPegApp.Docking;

/// <summary>
/// One part of a window's arrangement of panes: a group of panes shown one at a time (with tabs to choose between
/// them), or a split of parts side by side (or over each other), each with its share of the room. Saved as JSON with
/// each mode's settings, so the two kinds are one class.
/// </summary>
public sealed class DockNode
{
    public const string GroupKind = "group", SplitKind = "split";

    public string Kind { get; set; } = GroupKind;

    /// <summary>A split's parts lie over each other (true) or side by side.</summary>
    public bool Vertical { get; set; }

    /// <summary>A split's parts, in order: left to right, or top to bottom.</summary>
    public List<DockNode> Children { get; set; } = [];

    /// <summary>A split's parts' shares of the room, as star sizes, one for each part.</summary>
    public List<double> Sizes { get; set; } = [];

    /// <summary>A group's panes, by name, in the order of their tabs.</summary>
    public List<string> Panes { get; set; } = [];

    /// <summary>The pane of a group that is shown.</summary>
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

/// <summary>Where a pane is let go of, relative to the part it is over: in it (as a tab), or beside it on one side.</summary>
public enum DockZone
{
    Center,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// The main window's arrangement of panes, for one mode. The <c>Side*</c> members are the single side window of HandPeg before 5.4C:
/// read once, to become the first Pane Docker (<see cref="SideWindowStates.Migrate"/>), and never written again (each side window is
/// a <see cref="SideWindowState"/> in AppSettings.SideWindows now).
/// </summary>
public sealed class DockLayoutState
{
    /// <summary>
    /// The version of the arrangements this HandPeg makes. 2 (4.4): the Timeline pane ("UnifiedTimeline") took the Layers
    /// and Subtitles tabs' place. 3 (4.4-BugHunt): the Timeline is the first tab of the group it shares with the settings
    /// tabs, and Standard Editing has it so. 4 (5.3-Stage2): Standard Editing's Canvas is a tab beside the effects and keyframes.
    /// An older arrangement is brought up to it, step by step, as it is taken up (DockManager.Upgrade), in memory: it is saved as
    /// it is the next time the user changes it.
    /// </summary>
    public const int CurrentVersion = 4;

    /// <summary>The version of HandPeg's arrangements this one was made as (read back from what was saved; 1 before 4.4).</summary>
    public int Version { get; set; } = CurrentVersion;

    public DockNode Main { get; set; } = DockNode.Group();

    /// <summary>The side window's panes; null for none.</summary>
    public DockNode? Side { get; set; }

    /// <summary>Whether the side window is open.</summary>
    public bool SideOpen { get; set; }

    /// <summary>The side window's place on the screen: left, top, width, height; null for where it first opens.</summary>
    public double[]? SideBounds { get; set; }

    public bool SideMaximized { get; set; }

    /// <summary>The side window's panes as they were when it was last closed, to come back when it is opened again.</summary>
    public DockNode? SideClosed { get; set; }

    /// <summary>A copy sharing nothing with this one, to change or to keep as it is now.</summary>
    public DockLayoutState Copy() => System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(this, Json), Json)!;

    // The generated metadata (Services.AppSettingsJsonContext): the arrangement is read on the window's thread before its first frame.
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<DockLayoutState> Json => Services.AppSettingsJsonContext.Default.DockLayoutState;

    /// <summary>An arrangement as saved (JSON), or null when there is none or it cannot be read.</summary>
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

/// <summary>What a side window shows (5.4C): a mirror of the player's picture, or panes arranged as freely as in the main window.</summary>
public enum SideWindowMode
{
    LiveVideoPreview,
    PaneDocker,
}

/// <summary>
/// One side window (5.4C: as many as wanted, each its own monitor's): what it shows, the panes it holds (a Pane Docker's; a Live
/// Video Preview holds none), and where it was. Saved as a list, JSON, in AppSettings.SideWindows.
/// </summary>
public sealed class SideWindowState
{
    public const string PanesMode = "panes", PreviewMode = "preview";

    /// <summary>Its name among the side windows, kept while it is open (and saved with it).</summary>
    public string Id { get; set; } = "";

    /// <summary><see cref="PanesMode"/> (a Pane Docker) or <see cref="PreviewMode"/> (a Live Video Preview).</summary>
    public string Mode { get; set; } = PanesMode;

    /// <summary>A Pane Docker's panes; null while it holds none, and always for a Live Video Preview.</summary>
    public DockNode? Tree { get; set; }

    /// <summary>
    /// Its restored place, as Windows keeps it (GetWindowPlacement's normal position: left, top, right, bottom in workspace pixels),
    /// so it comes back on the monitor it was on whatever that one's scale; null for where it first opens.
    /// </summary>
    public int[]? Placement { get; set; }

    /// <summary>A place in DIPs (left, top, width, height) from the single side window before 5.4C: used once, when it has no Placement.</summary>
    public double[]? Bounds { get; set; }

    public bool Maximized { get; set; }

    /// <summary>Whether it covers its whole monitor, title bar and taskbar included (F11, or a double-click on the picture).</summary>
    public bool FullScreen { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPreview => Mode == PreviewMode;

    [System.Text.Json.Serialization.JsonIgnore]
    public SideWindowMode WindowMode => IsPreview ? SideWindowMode.LiveVideoPreview : SideWindowMode.PaneDocker;

    public static string ModeOf(SideWindowMode mode) => mode == SideWindowMode.LiveVideoPreview ? PreviewMode : PanesMode;
}

/// <summary>The side windows as saved (AppSettings.SideWindows), read and written through the generated metadata; window-free.</summary>
public static class SideWindowStates
{
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<SideWindowState>> Json => Services.AppSettingsJsonContext.Default.ListSideWindowState;

    /// <summary>The side windows saved (JSON); none when there are none or they cannot be read. Unknown modes read as Pane Dockers.</summary>
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

    /// <summary>A copy sharing nothing with the one given.</summary>
    public static SideWindowState Copy(SideWindowState side) =>
        System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(side, Services.AppSettingsJsonContext.Default.SideWindowState), Services.AppSettingsJsonContext.Default.SideWindowState)!;

    /// <summary>
    /// The single side window of an arrangement saved before 5.4C, as the first Pane Docker: added to <paramref name="sides"/> when it
    /// was open and held panes (its place and maximized state kept), unless one with its id is there already. The arrangement's legacy
    /// members are cleared in any case (the closed side window's arrangement goes: its panes are in the main window already), so they
    /// are never written again. True when a docker was added.
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

    /// <summary>The id the single side window of before 5.4C takes as a Pane Docker.</summary>
    public const string LegacyId = "side-1";

    /// <summary>A new id, unlike every one in <paramref name="sides"/>: "side-" and the lowest free number.</summary>
    public static string NewId(IEnumerable<SideWindowState> sides)
    {
        var taken = sides.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var n = 1;
        while (taken.Contains("side-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            n++;
        return "side-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Every pane in at most one place, across the side windows and then the main window (a side window keeps what the user put
    /// there; the main window loses a pane a side window holds): unknown panes out, a Live Video Preview emptied, each tree tidied
    /// (a Pane Docker left with nothing holds null, and stays open). The main window's top part is never null.
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

/// <summary>Changes to a tree of parts. Every change keeps the tree tidy: no empty groups, no split of one part.</summary>
public static class DockTree
{
    /// <summary>Takes a pane out of a tree; returns the tree's top part, which may be another one now (or null when nothing is left).</summary>
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
    /// Removes empty groups and splits of one part (the part takes the split's place), and splits within a split of
    /// the same direction (their parts join it). Returns the top part, or null when nothing is left.
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
                // A split of the same direction inside: its parts are this one's, sharing its room.
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

    /// <summary>Puts a part beside another (or beside the whole tree, when that is the top part); returns the top part.</summary>
    /// <param name="share">How much of the other part's room the new one takes.</param>
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
