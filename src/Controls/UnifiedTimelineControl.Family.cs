using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// Drag families for the unified timeline: what a dragged block carries (children, selection group, captions beneath, linked
// audio) and how each follows the drag.
public sealed partial class UnifiedTimelineControl
{
    // ----- Drag family collection -----

    /// <summary>
    /// Collects everything that follows a dragged block across all lanes (expanded or collapsed): move = all riding descendants; trim
    /// or seam edge = descendants on that edge via an unbroken edge chain, plus direct children the trim may detach; roll = the earlier
    /// clip's end children and the later clip's start children; slip = none. Also adds the other members of a selected group (moved or
    /// trimmed at the same end) with their own families, and captions under every dragged clip (see <see cref="Captions"/>).
    /// </summary>
    private void CollectFamily(BlockDrag drag)
    {
        _coupled.Clear();
        _coupledSet.Clear();
        _loose.Clear();
        _groupSet.Clear();
        (drag.Group, drag.CarriedByGroup) = (null, false);

        // Index all lane blocks by parent key (including the dragged block and roll neighbour, since another group member may
        // parent either).
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

            // Group moves as one, clamped where its earliest member would cross the timeline start.
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

            // Each other group member's family follows it the same way.
            foreach (var member in drag.Group ?? [])
            {
                Gather(member.Block, drag.Kind switch { BlockEdit.TrimEnd => BlockEdges.End, BlockEdit.TrimStart => BlockEdges.Start, _ => null },
                    member.How, false);
            }
        }

        // Captions under every dragged clip, except at a folded dead-air seam (the fold owns what slides there).
        if (drag.Seam is null)
        {
            var how = drag.Kind switch { BlockEdit.Move => Follows.Move, BlockEdit.Slip => Follows.SlipWithin, _ => Follows.Within };
            Captions(drag.Row, drag.Block, how);
            if (drag.Neighbor is { } next)
                Captions(drag.Row, next, Follows.Within);
            foreach (var member in drag.Group ?? [])
                Captions(member.Row, member.Block, how);
        }

        // BFS over riding descendants: edge children for a trim (recursively on the same edge), all for a move. Trims also add
        // direct children they may leave outside the parent (detachable). The dragged block, its neighbour and group members are
        // excluded as children: each is driven on its own.
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
                        // Off the trimmed edge: only the root's direct children can end up outside and detach.
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
    /// Other selected blocks that follow a drag of a selected block (multi-select): a move or plain end trim (no ripple, roll, slip,
    /// seam or Alt) applies to every member that supports it (draggable for moves; has that edge for trims). Members the model already
    /// carries with another (a riding child on the trimmed edge) are dropped; if the dragged block is such a child, CarriedByGroup is set.
    /// </summary>
    private List<(TimelineRow Row, TimelineBlock Block)> GroupOf(BlockDrag drag, Dictionary<object, List<(TimelineRow Row, TimelineBlock Block)>> byParent)
    {
        var found = new List<(TimelineRow Row, TimelineBlock Block)>();
        if (drag.Kind is not (BlockEdit.Move or BlockEdit.TrimEnd or BlockEdit.TrimStart) || drag.Ripple || drag.Seam is not null || drag.Alt || drag.Clone
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

        // Remove blocks the model already carries with a member (or the dragged block) so none is edited twice.
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
    /// Couples caption blocks under a clip: captions-lane blocks parented to the clip's row (anchored to its track audio) overlapping
    /// the clip at drag start. They follow like its HUD parts: moved by the same delta; on trim or roll, clipped to the current clip
    /// bounds (parts outside are hidden); on slip, shifted with the source content.
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

    // Children a trim may leave outside their parent; they stay in place and render as detaching once outside.
    private readonly HashSet<TimelineBlock> _loose = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Blocks whose audio beats move with the drag (Snap to Audio Beats), so their beats are excluded as snap targets: for a move, the
    /// dragged block plus its coupled family (<see cref="CollectFamily"/>); for a slip, the dragged block (its source slides beneath it).
    /// Trims, rolls and seam edges leave beats fixed, so none are returned and a clip's own beats remain snap targets for its edges.
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

    /// <summary>Snap times minus the original edges of coupled blocks (they move with the drag); the dragged block's and neighbour's edges are kept.</summary>
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
    /// Positions the coupled family for the current drag: moved by the block's delta, or trimmed with the dragged edge (edge children
    /// follow). A child left wholly outside its parent detaches (stays put, drawn detaching). A child crossing the timeline start
    /// clears FamilyFits, refusing the move.
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
                // Caption: original position (shifted by the slip when slipping), clipped to the current clip; zero width when
                // fully outside.
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

            // Group member: never detaches; minimum drawn width 4 px, and start trims clamp at the timeline start.
            if (child.Member)
            {
                if (left < 0 && !child.Block.MayStartBeforeZero && drag.Kind == BlockEdit.TrimStart)
                    (left, width) = (0, width + left);
                (child.Block.Left, child.Block.Width) = (left, Math.Max(width, 4));
                continue;
            }

            // Trimmed past: a child wholly outside its parent detaches and keeps its original position.
            var (parentLeft, parentRight) = (root.Left, root.Left + root.Width);
            var loose = _loose.Contains(child.Block)
                ? drag.Kind == BlockEdit.TrimEnd && child.Left0 >= parentRight - 0.5 || drag.Kind == BlockEdit.TrimStart && child.Left0 + child.Width0 <= parentLeft + 0.5
                : width < 1;
            child.Detaching = loose && drag.Seam is null;
            (child.Block.Left, child.Block.Width) = loose ? (child.Left0, child.Width0) : (left, Math.Max(width, 4));
        }

        drag.FamilyFits = fits;
    }

    // ----- Re-seating a drag on relaid rows (Spring-Zoom, Pass 5.7.1) -----

    // PixelsPerSecond the live drag's pixel state is in (BeginDrag; every re-seat moves it on), and the drag's last control-space pointer.
    private double _dragScale;
    private Point? _dragPointer;

    /// <summary>
    /// Rows were relaid at a new scale mid-drag (a Spring-Zoom view): carries the drag onto the rebuilt blocks (same family key, else the
    /// same Clip) with its pixel state scaled by new/old scale, so the drag goes on at the same times and commits through the rebuilt
    /// blocks' callbacks (built at the new scale). False when nothing re-seats (no drag, same scale, a J/L or header drag, or a block gone:
    /// the caller then lets go as before).
    /// </summary>
    private bool ReseatDrag()
    {
        var scale = PixelsPerSecond;
        if (scale <= 0 || _dragScale <= 0 || Math.Abs(scale / _dragScale - 1) < 1e-9 || _splitDrag is not null || _rowDrag is not null
            || (_drag is null && _fadeDrag is null && _crossfadeDrag is null && _gainDrag is null && _duckDrag is null))
        {
            return false;
        }

        var k = scale / _dragScale;
        if (_drag is { } drag)
        {
            if (Again(drag.Block, drag.Row) is not { } own)
                return false;

            TimelineBlock? neighbor = null;
            if (drag.Neighbor is { } next)
            {
                if (Again(next, drag.Row) is not { } found)
                    return false;
                neighbor = found.Block;
            }

            var seated = new BlockDrag
            {
                Row = own.Row, Block = own.Block, Kind = drag.Kind, PointerX = drag.PointerX * k, PointerY = drag.PointerY, Left = own.Block.Left,
                Width = own.Block.Width, Ripple = drag.Ripple, Neighbor = neighbor, NeighborLeft = neighbor?.Left ?? 0, NeighborWidth = neighbor?.Width ?? 0,
                Alt = drag.Alt, Clone = drag.Clone,
                Seam = drag.Seam is { } grip
                    ? new SeamGrip { Mark = grip.Mark, AtStart = grip.AtStart, Reveal = grip.Reveal, Fold = grip.Fold, Cut = grip.Cut * k, Rows = grip.Rows, Seconds = grip.Seconds }
                    : null,
            };
            (seated.Slip, seated.Travel, seated.Keys, seated.TimeLocked, seated.Fits, seated.NewTrackFits) =
                (drag.Slip * k, drag.Travel, drag.Keys, drag.TimeLocked, drag.Fits, drag.NewTrackFits);
            seated.Target = drag.Target is { } target ? RowAgain(target) : null;
            seated.NewTrack = drag.NewTrack is { } seam && RowAgain(seam.Beside) is { } beside ? (beside, seam.Above, seam.Y) : null;
            seated.Attach = drag.Attach is { } attach && Again(attach.Block, attach.Row) is { } parent ? (parent.Row, parent.Block, attach.Name) : null;
            _drag = seated;
        }
        else if (_fadeDrag is { } fade)
        {
            if (Again(fade.Block, fade.Row) is not { } own)
                return false;
            (own.Block.Fades, own.Block.Gain) = (fade.Block.Fades, fade.Block.Gain ?? own.Block.Gain);
            _fadeDrag = (own.Row, own.Block, fade.In, fade.Start);
        }
        else if (_crossfadeDrag is { } crossfade)
        {
            if (Again(crossfade.Block, crossfade.Row) is not { } own)
                return false;
            own.Block.CrossfadeIn = crossfade.Block.CrossfadeIn;
            _crossfadeDrag = (own.Row, own.Block, crossfade.Start);
        }
        else if (_gainDrag is { } gain)
        {
            if (Again(gain.Block, gain.Row) is not { } own)
                return false;
            own.Block.Gain = gain.Block.Gain;
            _gainDrag = (own.Row, own.Block, new Point(gain.From.X * k, gain.From.Y), gain.Start);
        }
        else if (_duckDrag is { } duck)
        {
            if (Again(duck.Block, duck.Row) is not { } own)
                return false;
            _duckDrag = new DuckDrag { Row = own.Row, Block = own.Block, Keys = duck.Keys, Before = duck.Before, Index = duck.Index, Changed = duck.Changed };
        }

        _dragScale = scale;
        _lastPointer = new Point(_lastPointer.X * k, _lastPointer.Y);
        return true;
    }

    /// <summary>After a re-seat (lanes laid): collects the block drag's family afresh and replays the pointer, so everything is drawn live.</summary>
    private void ResumeDrag()
    {
        var pointer = _dragPointer ?? Mouse.GetPosition(this);
        var laid = Laid(pointer);
        if (_drag is { } drag)
        {
            CollectFamily(drag);
            if (!_dragParked)
            {
                _lastPointer = laid;
                Follow(drag, laid, pointerMoved: false, Now());
            }
        }
        else if (_fadeDrag is { } fade)
        {
            FollowFade(fade, laid);
        }
        else if (_crossfadeDrag is { } crossfade)
        {
            FollowCrossfade(crossfade, laid);
        }

        _contentDirty = _labelsDirty = _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>The rebuilt block for <paramref name="block"/> (same family key, else same Clip), preferring its old row's key; null when gone.</summary>
    private (TimelineRow Row, TimelineBlock Block)? Again(TimelineBlock block, TimelineRow row)
    {
        if (block.Clip is null && block.Family is null)
            return null;

        (TimelineRow Row, TimelineBlock Block)? elsewhere = null;
        foreach (var candidate in AllRows(_rows))
        {
            foreach (var other in candidate.Blocks)
            {
                if (!SameThing(other, block))
                    continue;
                if (Equals(candidate.Key, row.Key))
                    return (candidate, other);
                elsewhere ??= (candidate, other);
            }
        }

        return elsewhere;
    }

    /// <summary>The rebuilt row with <paramref name="row"/>'s key; null when gone.</summary>
    private TimelineRow? RowAgain(TimelineRow row)
    {
        foreach (var candidate in AllRows(_rows))
        {
            if (Equals(candidate.Key, row.Key))
                return candidate;
        }

        return null;
    }

    // Every row, depth first (children after their parent).
    private static IEnumerable<TimelineRow> AllRows(List<TimelineRow> rows)
    {
        var stack = new Stack<TimelineRow>();
        for (var i = rows.Count - 1; i >= 0; i--)
            stack.Push(rows[i]);
        while (stack.Count > 0)
        {
            var row = stack.Pop();
            yield return row;
            for (var i = row.Children.Count - 1; i >= 0; i--)
                stack.Push(row.Children[i]);
        }
    }

    /// <summary>Test seam: the scale (px/s) the live drag's pixel state is in; 0 with no drag.</summary>
    internal double DragScale => IsDragging ? _dragScale : 0;
}
