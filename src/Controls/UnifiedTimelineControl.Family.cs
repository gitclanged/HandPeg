using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// The unified timeline's dragged families (moved out of UnifiedTimelineControl.Input.cs in Pass 5.5A, unchanged): what a dragged
// block carries along (its children, its group, the captions under it, the sound taken with it) and how they follow the drag.
public sealed partial class UnifiedTimelineControl
{
    // ----- What a dragged block carries along (4.4 BugHunt) -----

    /// <summary>
    /// Finds what goes with a dragged block, on every lane (open or collapsed): moved, the children that ride with it
    /// (and theirs); a trim (or a seam's edge), the children on that edge of it, through an unbroken line of children on
    /// that edge, and the children the trim may leave outside it; a roll, the earlier clip's on its end and the later's on
    /// its start; a slip, none. 5.0: the other blocks of a selected group the block is one of (moved, or trimmed at the same
    /// end) with what each of them carries along, and the captions under every clip dragged (see <see cref="Captions"/>).
    /// </summary>
    private void CollectFamily(BlockDrag drag)
    {
        _coupled.Clear();
        _coupledSet.Clear();
        _loose.Clear();
        _groupSet.Clear();
        (drag.Group, drag.CarriedByGroup) = (null, false);

        // The blocks of every lane by the parent they belong to (the dragged block and its roll's neighbour among them: a
        // group's other block may be the parent of either).
        var byParent = new Dictionary<object, List<(TimelineRow Row, TimelineBlock Block)>>();
        foreach (var lane in _lanes)
        {
            foreach (var block in lane.Row.Blocks)
            {
                if (block.Family is not { Parent: { } parent })
                    continue;
                if (!byParent.TryGetValue(parent, out var children))
                    byParent[parent] = children = [];
                children.Add((lane.Row, block));
            }
        }

        var members = GroupOf(drag, byParent);
        if (members.Count > 0)
        {
            var how = drag.Kind switch { BlockEdit.TrimEnd => Follows.End, BlockEdit.TrimStart => Follows.Start, _ => Follows.Move };
            var lowest = drag.Block.MayStartBeforeZero ? double.PositiveInfinity : drag.Left;
            drag.Group = new List<Coupled>(members.Count);
            foreach (var (row, member) in members)
            {
                var entry = new Coupled { Row = row, Block = member, How = how, Root = member, Member = true, Left0 = member.Left, Width0 = member.Width };
                drag.Group.Add(entry);
                _coupled.Add(entry);
                _coupledSet.Add(member);
                _groupSet.Add(member);
                if (!member.MayStartBeforeZero)
                    lowest = Math.Min(lowest, member.Left);
            }

            // Moved as one, the group stops where the first of it would pass the timeline's start.
            drag.GroupFloor = double.IsPositiveInfinity(lowest) ? double.NegativeInfinity : drag.Left - lowest;
        }

        if (drag.Kind != BlockEdit.Slip && byParent.Count > 0)
        {
            switch (drag.Kind)
            {
                case BlockEdit.Move:
                    Gather(drag.Block, null, Follows.Move, false);
                    break;
                case BlockEdit.TrimEnd:
                    Gather(drag.Block, BlockEdges.End, drag.Seam is null ? Follows.End : Follows.SeamEdge, false);
                    break;
                case BlockEdit.TrimStart:
                    Gather(drag.Block, BlockEdges.Start, drag.Seam is null ? Follows.Start : Follows.SeamEdge, false);
                    break;
                case BlockEdit.Roll:
                    Gather(drag.Block, BlockEdges.End, Follows.End, false);
                    if (drag.Neighbor is { } next)
                        Gather(next, BlockEdges.Start, Follows.Start, true);
                    break;
            }

            // What each other block of the group carries along goes with it the same way.
            foreach (var member in drag.Group ?? [])
            {
                Gather(member.Block, drag.Kind switch { BlockEdit.TrimEnd => BlockEdges.End, BlockEdit.TrimStart => BlockEdges.Start, _ => null },
                    member.How, false);
            }
        }

        // The captions under every clip dragged (5.0): never at a seam of folded dead air (what slides there is the fold's).
        if (drag.Seam is null)
        {
            var how = drag.Kind switch { BlockEdit.Move => Follows.Move, BlockEdit.Slip => Follows.SlipWithin, _ => Follows.Within };
            Captions(drag.Row, drag.Block, how);
            if (drag.Neighbor is { } next)
                Captions(drag.Row, next, Follows.Within);
            foreach (var member in drag.Group ?? [])
                Captions(member.Row, member.Block, how);
        }

        // Every child (and theirs, the children on the same edge) that rides with the block: on that edge (a trim), or
        // all of them (a move). A trim also shows the children it may leave outside the parent, which come loose. The
        // dragged block, its neighbour and the group's own blocks are never anyone's children here: each goes by itself.
        void Gather(TimelineBlock root, BlockEdges? edge, Follows how, bool ofNeighbor)
        {
            var queue = new Queue<TimelineBlock>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                if (parent.Family is not { } family || !byParent.TryGetValue(family.Key, out var children))
                    continue;

                foreach (var (row, child) in children)
                {
                    if (child.Family is not { Rides: true } childFamily || _coupledSet.Contains(child) || ReferenceEquals(child, drag.Block)
                        || ReferenceEquals(child, drag.Neighbor) || _groupSet.Contains(child))
                    {
                        continue;
                    }

                    var onEdge = edge is { } wanted && (childFamily.OnParentEdges & wanted) != 0;
                    if (edge is not null && !onEdge)
                    {
                        // Off the dragged edge: only the root's own children can be left outside by a trim, and come loose.
                        if (ReferenceEquals(parent, root) && drag.Seam is null && drag.Kind is BlockEdit.TrimEnd or BlockEdit.TrimStart)
                            Add(row, child, how, ofNeighbor, mayDetach: true, root);
                        continue;
                    }

                    Add(row, child, how, ofNeighbor, mayDetach: false, root);
                    queue.Enqueue(child);
                }
            }
        }

        void Add(TimelineRow row, TimelineBlock child, Follows how, bool ofNeighbor, bool mayDetach, TimelineBlock root)
        {
            _coupled.Add(new Coupled
            {
                Row = row, Block = child, How = mayDetach ? Follows.Move : how, OfNeighbor = ofNeighbor, Left0 = child.Left, Width0 = child.Width,
                Detaching = false, Root = root,
            });
            _coupledSet.Add(child);
            if (mayDetach)
                _loose.Add(child);
        }
    }

    /// <summary>
    /// The other selected blocks a drag of a selected block takes along (5.0, multi-select): a move, or a plain trim of either
    /// end (no ripple, roll, slip or seam), of one of them is made to every one that can take it (moved: it can be dragged;
    /// trimmed: it has that edge). One the model moves or trims with another of them (a child riding with its parent, on
    /// the edge trimmed) is left to it; when the dragged block itself is such a child, its own edit is left out.
    /// </summary>
    private List<(TimelineRow Row, TimelineBlock Block)> GroupOf(BlockDrag drag, Dictionary<object, List<(TimelineRow Row, TimelineBlock Block)>> byParent)
    {
        var found = new List<(TimelineRow Row, TimelineBlock Block)>();
        if (drag.Kind is not (BlockEdit.Move or BlockEdit.TrimEnd or BlockEdit.TrimStart) || drag.Ripple || drag.Seam is not null || drag.Alt
            || IsSelected is not { } isSelected || drag.Block.Clip is not { } pressed)
        {
            return found;
        }

        var selected = false;
        Raise(() => selected = isSelected(pressed));
        if (!selected)
            return found;

        foreach (var lane in _lanes)
        {
            foreach (var block in lane.Row.Blocks)
            {
                if (ReferenceEquals(block, drag.Block) || block.Clip is not { } clip || !Takes(block))
                    continue;

                var chosen = false;
                if (Raise(() => chosen = isSelected(clip)) && chosen)
                    found.Add((lane.Row, block));
            }
        }

        if (found.Count == 0)
            return found;

        // What the model takes along with each of them (and with the dragged block): never edited twice.
        var edge = drag.Kind switch { BlockEdit.TrimEnd => BlockEdges.End, BlockEdit.TrimStart => BlockEdges.Start, _ => BlockEdges.None };
        var carried = new HashSet<TimelineBlock>(ReferenceEqualityComparer.Instance);
        Reach(drag.Block);
        foreach (var (_, member) in found)
            Reach(member);
        found.RemoveAll(m => carried.Contains(m.Block));
        drag.CarriedByGroup = carried.Contains(drag.Block);
        return found;

        bool Takes(TimelineBlock block) => drag.Kind switch
        {
            BlockEdit.Move => block.Commit is not null && block.CanDrag is { } can && CanDragNow(can),
            BlockEdit.TrimEnd => block.Resizable && block.Commit is not null && block.CanDrag is not null,
            _ => block.TrimmableStart && block.Edit is not null && block.CanDrag is not null,
        };

        bool CanDragNow(Func<bool> can)
        {
            var yes = false;
            return Raise(() => yes = can()) && yes;
        }

        void Reach(TimelineBlock root)
        {
            var queue = new Queue<TimelineBlock>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                if (parent.Family is not { } family || !byParent.TryGetValue(family.Key, out var children))
                    continue;

                foreach (var (_, child) in children)
                {
                    if (child.Family is not { Rides: true } childFamily || (edge != BlockEdges.None && (childFamily.OnParentEdges & edge) == 0) || !carried.Add(child))
                        continue;
                    queue.Enqueue(child);
                }
            }
        }
    }

    /// <summary>
    /// The captions under a clip's row (5.0, the user's directive 2): the blocks of a captions lane in the row's own stack
    /// (the captions are anchored to the sound of its track) that lie within the clip as the drag began. They go with it
    /// every move, as its HUD pieces do: moved by as much; trimmed or rolled, cut to the clip as it is now (what its new
    /// edge leaves outside it is not shown); slipped, slid with what of its file it plays.
    /// </summary>
    private void Captions(TimelineRow row, TimelineBlock clip, Follows how)
    {
        var (left, right) = (clip.Left, clip.Left + clip.Width);
        foreach (var lane in _lanes)
        {
            if (lane.Row.Kind != TimelineRowKind.Captions || !ReferenceEquals(lane.Row.Parent, row))
                continue;

            foreach (var caption in lane.Row.Blocks)
            {
                if (_coupledSet.Contains(caption) || Math.Min(caption.Left + caption.Width, right) - Math.Max(caption.Left, left) < 0.5)
                    continue;

                _coupled.Add(new Coupled { Row = lane.Row, Block = caption, How = how, Root = clip, Left0 = caption.Left, Width0 = caption.Width });
                _coupledSet.Add(caption);
            }
        }
    }

    // The children a trim may leave outside their parent (they stay where they are, and are shown coming loose once it does).
    private readonly HashSet<TimelineBlock> _loose = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The blocks whose sound a drag takes along with it (5.2B, Snap to Audio Beats): a move's (the dragged block, the rest of its group and
    /// what rides with them, as <see cref="CollectFamily"/> found them) and a slip's (what of its file the block plays slides under it). Their
    /// clips' beats move with the drag, so they are no moments to snap to. A trim, a roll or a seam's edge leaves every beat where it is (what
    /// of the file plays moves with the edge): none then, and a clip's own beats stay for its edges to catch on.
    /// </summary>
    private List<TimelineBlock> SoundTakenAlong(BlockDrag? drag)
    {
        var moving = new List<TimelineBlock>();
        if (drag is not { Kind: BlockEdit.Move or BlockEdit.Slip })
            return moving;

        moving.Add(drag.Block);
        if (drag.Kind == BlockEdit.Move)
            moving.AddRange(_coupled.Select(c => c.Block));
        return moving;
    }

    /// <summary>The snapping moments less the edges of what the drag carries along (each its own child's), but never the dragged block's own.</summary>
    private IReadOnlyList<double> WithoutFamily(IReadOnlyList<double> times, BlockDrag drag)
    {
        var scale = PixelsPerSecond;
        if (_coupled.Count == 0 || times.Count == 0 || scale <= 0)
            return times;

        var own = new List<double> { drag.Left, drag.Left + drag.Width };
        if (drag.Neighbor is not null)
            own.AddRange([drag.NeighborLeft, drag.NeighborLeft + drag.NeighborWidth]);

        var kept = new List<double>(times.Count);
        foreach (var time in times)
        {
            var x = time * scale;
            if (own.Exists(edge => Math.Abs(edge - x) <= 0.5) || !_coupled.Exists(c => Math.Abs(c.Left0 - x) <= 0.5 || Math.Abs(c.Left0 + c.Width0 - x) <= 0.5))
                kept.Add(time);
        }

        return kept;
    }

    /// <summary>
    /// Lays out what the dragged block carries along for where it is now: moved by as much as it moved, or with its dragged
    /// edge (the edge's own children trimmed with it); a child the trim leaves wholly outside the parent comes loose (it
    /// stays where it is, drawn coming away). A child that would start before the timeline's start makes the move refused.
    /// </summary>
    private void FollowFamily(BlockDrag drag)
    {
        if (_coupled.Count == 0)
            return;

        var block = drag.Block;
        var (startMoved, endMoved) = (block.Left - drag.Left, block.Left + block.Width - (drag.Left + drag.Width));
        var cutMoved = drag.Neighbor is { } next ? next.Left - drag.NeighborLeft : 0;
        var seamGrowth = drag.Seam is { } grip ? grip.Seconds * PixelsPerSecond : 0;
        var fits = true;
        foreach (var child in _coupled)
        {
            var (left, width) = (child.Left0, child.Width0);
            var root = child.Root ?? block;
            switch (child.How)
            {
                // A caption: where it was (slid with its clip's file, slipped), cut to its clip as it is now; nothing of it
                // when it lies outside.
                case Follows.Within or Follows.SlipWithin:
                {
                    var from = child.Left0 + (child.How == Follows.SlipWithin ? drag.Slip : 0);
                    var (cutLeft, cutRight) = (Math.Max(from, root.Left), Math.Min(from + child.Width0, root.Left + root.Width));
                    (child.Block.Left, child.Block.Width) = (cutLeft, Math.Max(cutRight - cutLeft, 0));
                    continue;
                }

                case Follows.Move when drag.Kind == BlockEdit.Move:
                    left += startMoved;
                    if (!child.Block.MayStartBeforeZero && left < -0.5)
                        fits = false;
                    break;
                case Follows.End:
                    width = child.Width0 + (child.OfNeighbor ? 0 : endMoved);
                    break;
                case Follows.Start:
                {
                    var moved = child.OfNeighbor ? cutMoved : startMoved;
                    (left, width) = (child.Left0 + moved, child.Width0 - moved);
                    break;
                }

                case Follows.SeamEdge:
                    width = child.Width0 + seamGrowth;
                    break;
            }

            // One of a selected group: never loose; trimmed no shorter than a block is drawn, nor before the timeline's start.
            if (child.Member)
            {
                if (left < 0 && !child.Block.MayStartBeforeZero && drag.Kind == BlockEdit.TrimStart)
                    (left, width) = (0, width + left);
                (child.Block.Left, child.Block.Width) = (left, Math.Max(width, 4));
                continue;
            }

            // Trimmed past: left wholly outside its parent, it comes loose and stays where it was.
            var (parentLeft, parentRight) = (root.Left, root.Left + root.Width);
            var loose = _loose.Contains(child.Block)
                ? drag.Kind == BlockEdit.TrimEnd && child.Left0 >= parentRight - 0.5 || drag.Kind == BlockEdit.TrimStart && child.Left0 + child.Width0 <= parentLeft + 0.5
                : width < 1;
            child.Detaching = loose && drag.Seam is null;
            (child.Block.Left, child.Block.Width) = loose ? (child.Left0, child.Width0) : (left, Math.Max(width, 4));
        }

        drag.FamilyFits = fits;
    }
}
