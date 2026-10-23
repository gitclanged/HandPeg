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

/// <summary>The arrangement of both windows' panes, for one mode.</summary>
public sealed class DockLayoutState
{
    public int Version { get; set; } = 1;

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
