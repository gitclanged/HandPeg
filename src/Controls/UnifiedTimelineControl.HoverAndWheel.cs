using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// ----- The unified timeline's mouse: what is under the pointer, moving, the tracks' height, the wheel and the middle button (split from UnifiedTimelineControl.Input.cs, 5.4.1-M5) -----
public sealed partial class UnifiedTimelineControl
{
    // ----- What is under the pointer -----

    /// <summary>The block under a point of the laid-out rows in a lane, the topmost (the last drawn) first; its J/L sound's reach counts.</summary>
    private TimelineBlock? BlockAt(int index, Point laid)
    {
        var lane = _lanes[index];
        var blocks = lane.Row.Blocks;
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var block = blocks[i];
            var rect = BlockRect(lane, block, 0);
            if (rect.Contains(laid))
                return block;
            if (lane.Detailed && SoundReach(block, rect) is { } reach && reach.Contains(laid))
                return block;
        }

        return null;
    }

    // Where a J/L cut's sound lies: the lower part of the block, reaching out past its edges.
    private Rect? SoundReach(TimelineBlock block, Rect rect)
    {
        var scale = PixelsPerSecond;
        if (block.AudioSplit is not { } split || (split.LeadIn == 0 && split.TailOut == 0) || scale <= 0)
            return null;

        return new Rect(rect.Left - split.LeadIn * scale, rect.Top + rect.Height * SoundBandTop,
            Math.Max(rect.Width + (split.LeadIn + split.TailOut) * scale, 1), rect.Height * (1 - SoundBandTop));
    }

    /// <summary>Which edge of a block's sound a point is on, in its lower part: true where it starts, false where it ends; null neither.</summary>
    private bool? SplitEdgeAt(TimelineBlock block, Rect rect, Point laid)
    {
        var scale = PixelsPerSecond;
        if (block.AudioSplitEdit is null || scale <= 0 || laid.Y < rect.Top + rect.Height * SoundBandTop || laid.Y > rect.Bottom)
            return null;

        var (lead, tail) = block.AudioSplit ?? (0.0, 0.0);
        if (Math.Abs(laid.X - (rect.Left - lead * scale)) <= TimelineMetrics.EdgePixels)
            return true;
        if (Math.Abs(laid.X - (rect.Right + tail * scale)) <= TimelineMetrics.EdgePixels)
            return false;
        return null;
    }

    /// <summary>Which of a block's fade handles a point is on: true the fade in's, false the fade out's; null neither.</summary>
    private bool? FadeHandleAt(in Lane lane, TimelineBlock block, Point laid)
    {
        if (block.Fades is not { } fades || block.FadeEdit is null)
            return null;

        var rect = BlockRect(lane, block, 0);
        if (laid.Y > rect.Top + FadeBand || laid.Y < rect.Top - 1)
            return null;

        var (fadeIn, fadeOut) = FadeSpan(rect, fades.In, fades.Out, PixelsPerSecond);
        var (toIn, toOut) = (Math.Abs(laid.X - HandleX(rect, rect.Left + fadeIn)), Math.Abs(laid.X - HandleX(rect, rect.Right - fadeOut)));
        if (Math.Min(toIn, toOut) > FadeReach)
            return null;
        return toIn <= toOut;
    }

    /// <summary>Whether a point is on a block's gain line (away from its ends, which trim).</summary>
    private static bool OnGainLine(in Lane lane, TimelineBlock block, Point laid)
    {
        if (block.Gain is not { } gain || block.GainEdit is null)
            return false;

        var rect = BlockRect(lane, block, 0);
        return Math.Abs(laid.Y - GainY(rect, gain.GainDb)) <= GainReach + 1
            && laid.X > rect.Left + TimelineMetrics.EdgePixels && laid.X < rect.Right - TimelineMetrics.EdgePixels;
    }

    /// <summary>Whether a point is on a fade handle of the block under it: a seam's tab there gives way to the handle.</summary>
    private bool OnFadeHandle(int index, Point laid) =>
        _lanes[index].Detailed && BlockAt(index, laid) is { } block && FadeHandleAt(_lanes[index], block, laid) is not null;

    /// <summary>The seam whose tab a point is on (an index into the row's seams); -1 for none.</summary>
    private int SeamAt(in Lane lane, Point laid)
    {
        var scale = PixelsPerSecond;
        if (!lane.Detailed || scale <= 0)
            return -1;

        var seams = lane.Row.Seams;
        for (var i = 0; i < seams.Count; i++)
        {
            if (Rect.Inflate(SeamTab(lane, seams[i].Seconds * scale), 2, 2).Contains(laid))
                return i;
        }

        return -1;
    }

    /// <summary>The gap whose pill a point is on (an index into the row's gaps): only the pill of the gap the pointer is in shows, so only it counts; -1 for none.</summary>
    private int GapAt(in Lane lane, Point laid)
    {
        if (!lane.Detailed || IsDragging || _hoveredGap is not { } hovered || !ReferenceEquals(hovered.Row, lane.Row) || hovered.Index >= lane.Row.Gaps.Count)
            return -1;

        var (left, right) = lane.Row.Gaps[hovered.Index];
        return right - left >= GapLeast && Rect.Inflate(GapPill(lane, left, right), 2, 2).Contains(laid) ? hovered.Index : -1;
    }

    /// <summary>The gap a point of a lane drawn in full lies in (an index into the row's gaps), whose pill then shows; -1 for none.</summary>
    private int GapUnder(in Lane lane, Point laid)
    {
        if (!lane.Detailed || IsDragging)
            return -1;

        var gaps = lane.Row.Gaps;
        for (var i = 0; i < gaps.Count; i++)
        {
            if (laid.X >= gaps[i].Left && laid.X <= gaps[i].Right)
                return i;
        }

        return -1;
    }

    /// <summary>The chevron a point is on (x on the control, y on the rows): the stack it opens and closes; null for none.</summary>
    private static ChildStack? ChevronAt(in Lane lane, Point at)
    {
        if (lane.Stacks == 0 || lane.Height < 12)
            return null;

        var (slot, apart) = (0, lane.ChevronCount > 1 ? 0.5 : 2);
        foreach (var stack in StackOrder)
        {
            if (!lane.Has(stack))
                continue;
            if (Rect.Inflate(ChevronRect(lane, slot++), apart, 2).Contains(at))
                return stack;
        }

        return null;
    }

    // What a chevron does, in words: with one stack, its lanes; with two, which of them. A stack of effect lanes alone (5.3: a
    // Cutout with an effect of its own) shows them or hides them, having no thin bars; a stack with some among others says so.
    private static string ChevronTip(in Lane lane, ChildStack stack, bool open)
    {
        var (inStack, hiding) = (0, 0);
        foreach (var child in lane.Row.Children)
        {
            if (child.Stack != stack)
                continue;
            inStack++;
            hiding += child.HidesWhenClosed ? 1 : 0;
        }

        if (inStack > 0 && hiding == inStack)
            return open ? "Hide its effect lanes" : "Show its effect lanes";

        var lanes = lane.ChevronCount < 2 ? "its lanes"
            : stack == ChildStack.Audio ? "its Audio Sub-Lanes (the unlinked sound, the file's audio streams)"
            : "its Visual Sub-Lanes (the HUD Cutouts and masks, the captions)";
        var bars = hiding > 0 ? "thin bars, its effect lanes hidden" : "thin bars";
        return open ? $"Fold {lanes} to {bars}" : $"Open {lanes} to their full height";
    }

    /// <summary>The stretch put back whose Hide pill a point is on (an index into the row's reveals); -1 for none.</summary>
    private int RevealAt(in Lane lane, Point laid)
    {
        var scale = PixelsPerSecond;
        if (!lane.Detailed || scale <= 0 || lane.Row.Reveals.Count == 0)
            return -1;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var reveals = lane.Row.Reveals;
        for (var i = 0; i < reveals.Count; i++)
        {
            if (RevealPill(lane, reveals[i].From * scale, reveals[i].To * scale, pixelsPerDip, out _) is { IsEmpty: false } pill
                && Rect.Inflate(pill, 2, 2).Contains(laid))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The header switch a point is on (x on the control, y on the rows); null for none.</summary>
    private RowSwitches? SwitchAt(in Lane lane, Point at)
    {
        if (!lane.Detailed)
            return null;

        var slot = 0;
        foreach (var flag in SwitchOrder)
        {
            if ((lane.Row.Offered & flag) == 0)
                continue;
            if (SwitchRect(lane, slot++).Contains(at))
                return flag;
        }

        return null;
    }

    /// <summary>Whether a point (x on the control, y on the rows) is on a header's preset badge (5.2A).</summary>
    private bool BadgeAt(in Lane lane, Point at) =>
        (lane.Row.Badge is { Length: > 0 } || lane.Row.OffersBadge) && BadgeRect(lane, VisualTreeHelper.GetDpi(this).PixelsPerDip) is { IsEmpty: false } badge
        && badge.Contains(at);

    // ----- Moving -----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_panFrom is not null)
        {
            MiddlePanMove(point.X, MomentumScroller.Now());
            NotePointer(point);
            return;
        }

        NotePointer(point);

        if (_heightDrag is { } height)
        {
            var moved = point.Y - height.FromY;
            if (height.Row is { } alone)
                Raise(() => RowHeightDragged?.Invoke(alone, height.FromHeight + moved));
            else
                Raise(() => TrackHeightDragged?.Invoke(moved / Math.Max(height.Rows, 1)));
            return;
        }

        var laid = Laid(point);
        if (FollowMarquee(laid))
            return;

        if (_splitDrag is { } split)
        {
            FollowSplit(split, laid);
            return;
        }

        if (_fadeDrag is { } fadeDrag)
        {
            FollowFade(fadeDrag, laid);
            return;
        }

        if (_crossfadeDrag is { } crossfadeDrag)
        {
            FollowCrossfade(crossfadeDrag, laid);
            return;
        }

        if (_gainDrag is { } gainDrag)
        {
            FollowGain(gainDrag, laid);
            return;
        }

        if (_drag is not { } drag)
        {
            HoverAt(point, laid);
            return;
        }

        _lastPointer = laid;
        Follow(drag, laid, pointerMoved: true);

        // Near an end of the time area, the view moves along by itself while the pointer stays there.
        var seen = point.X - _headerWidth;
        var nearEdge = seen < AutoScrollZone || seen > TimeAreaWidth - AutoScrollZone;
        if (nearEdge && !_autoScroll.IsEnabled)
            _autoScroll.Start();
        else if (!nearEdge)
            _autoScroll.Stop();
    }

    /// <summary>Lays a dragged block (and a rolled neighbour) out for where the pointer is.</summary>
    private void Follow(BlockDrag drag, Point point, bool pointerMoved)
    {
        // A clip may be dragged, or stretched, past the end of the sequence: the sequence is as long as its clips reach,
        // and the rows are laid out to the new length when it is let go.
        var moved = point.X - drag.PointerX;
        if (pointerMoved)
            drag.Travel = Math.Max(drag.Travel, Math.Abs(moved));

        switch (drag.Kind)
        {
            // An edge at a seam: outward shows the fold again, inward folds more of the clip into it, within what the fold
            // and the clip have (seconds; Shift: whole frames). The clip grows or shrinks at that edge, its start staying at
            // the cut (what follows slides by as much: drawn shifted, see CurrentShift).
            case BlockEdit.TrimEnd or BlockEdit.TrimStart when drag.Seam is { } grip && PixelsPerSecond > 0:
            {
                var scale = PixelsPerSecond;
                var outward = (grip.AtStart ? -moved : moved) / scale;
                if (PrecisionRate() is var rate and > 0)
                    outward = Math.Round(outward * rate) / rate;
                grip.Seconds = Math.Clamp(outward, -grip.Fold, grip.Reveal);
                drag.Block.Width = Math.Max(drag.Width + grip.Seconds * scale, 4);
                _contentDirty = _labelsDirty = true;
                break;
            }

            case BlockEdit.TrimEnd:
                drag.Block.Width = Math.Max(Snap(drag.Left + drag.Width + moved) - drag.Left, 4);
                break;

            case BlockEdit.TrimStart:
            {
                var right = drag.Left + drag.Width;
                var left = Math.Min(Snap(drag.Left + moved), right - 4);
                (drag.Block.Left, drag.Block.Width) = (Math.Max(left, 0), right - Math.Max(left, 0));
                break;
            }

            case BlockEdit.Roll when drag.Neighbor is { } next:
            {
                var cut = Math.Clamp(Snap(drag.Left + drag.Width + moved), drag.Left + 4, drag.NeighborLeft + drag.NeighborWidth - 4);
                drag.Block.Width = cut - drag.Left;
                (next.Left, next.Width) = (cut, drag.NeighborLeft + drag.NeighborWidth - cut);
                break;
            }

            case BlockEdit.Slip:
                drag.Slip = moved;
                break;

            default:
            {
                // Either edge of a moved block snaps, whichever is the nearer to something. A selected group (5.0) moves as
                // one along its rows, stopping where the first of it would pass the timeline's start; it goes onto no other row.
                var left = drag.Left + moved;
                var (toLeft, toRight) = (Snap(left) - left, Snap(left + drag.Width) - (left + drag.Width));
                left += Math.Abs(toLeft) <= Math.Abs(toRight) ? toLeft : toRight;
                drag.Block.Left = drag.Group is { Count: > 0 } ? Math.Max(left, drag.GroupFloor) : drag.Block.MayStartBeforeZero ? left : Math.Max(left, 0);
                if (drag.Group is { Count: > 0 })
                {
                    (drag.Target, drag.NewTrack) = (null, null);
                }
                else if (pointerMoved)
                {
                    // At a seam above, below or between the picture tracks (5.4A): onto a new track there, where the host takes it;
                    // anywhere else (or refused there), the row under the pointer as before.
                    if (NewTrackSeamAt(point, drag) is { } seam && OffersNewTrack(drag, seam))
                    {
                        drag.Target = null;
                    }
                    else
                    {
                        drag.NewTrack = null;
                        TargetUnder(drag, point);
                    }
                }
                else if (drag.NewTrack is { } seam && drag.Block.CanMoveToNewTrack is { } canNew)
                {
                    var (fits, at) = (false, drag.Block.Left);
                    Raise(() => fits = canNew(seam.Beside.Data, seam.Above, at));
                    drag.NewTrackFits = fits;
                }
                else if (drag.Target is { } target && drag.Block.CanMoveTo is { } canMoveTo)
                {
                    var (fits, at) = (false, drag.Block.Left);
                    Raise(() => fits = canMoveTo(target.Data, at));
                    drag.Fits = fits;
                }

                break;
            }
        }

        // What it carries along (its children, its unlinked sound, its file's sound on its Audio Sub-Lanes) goes with it, every move.
        FollowFamily(drag);
        _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// The seam a moved block is over (5.4A, the user's directive: a clip dragged above the top picture track, below the bottom one, or
    /// into the band along the seam between two of them goes onto a new track there): the top-level picture row the new track goes
    /// beside, above it or below it, and where the insertion bar is drawn (laid out). A seam between two rows is said as the lower row's,
    /// above it. The band on each side of a seam is the outer min(14, a quarter) DIPs of the top-level row's own lane, so a row at the
    /// least height keeps a middle to land on. Only for a block that can go onto a new track, never for a group, and only once the
    /// pointer has gone twice the system's drag distance up or down from where it was pressed (a move along its own row near its edge
    /// is never taken for one); at the edges of the block's own track only the one it is heading for. Null for none.
    /// </summary>
    private (TimelineRow Beside, bool Above, double Y)? NewTrackSeamAt(Point point, BlockDrag drag)
    {
        if (drag.Group is { Count: > 0 } || drag.Block.MoveToNewTrack is null || drag.Block.CanMoveToNewTrack is null
            || Math.Abs(point.Y - drag.PointerY) <= 2 * SystemParameters.MinimumVerticalDragDistance)
        {
            return null;
        }

        // The picture groups, top to bottom: each top-level Video (or empty) row's run of lanes (its lanes in front above it, its
        // stacks under it), with its band.
        _seamGroups.Clear();
        for (var i = 0; i < _lanes.Count; i++)
        {
            var root = i;
            for (var steps = 0; steps <= _lanes.Count && _lanes[root].Depth > 0 && _lanes[root].Parent >= 0; steps++)
                root = _lanes[root].Parent;
            if (_lanes[root] is not { Depth: 0, Row.Kind: TimelineRowKind.Video or TimelineRowKind.Empty })
                continue;

            if (_seamGroups.Count > 0 && _seamGroups[^1].Root == root && _seamGroups[^1].Last == i - 1)
            {
                _seamGroups[^1] = _seamGroups[^1] with { Last = i, Bottom = _lanes[i].Bottom };
                continue;
            }

            _seamGroups.Add(new SeamGroup(root, i, i, _lanes[i].Top, _lanes[i].Bottom, Math.Min(SeamBand, _lanes[root].Height / 4)));
        }

        if (_seamGroups.Count == 0)
            return null;

        var y = point.Y;
        var own = -1;
        for (var g = 0; g < _seamGroups.Count && own < 0; g++)
        {
            if (ReferenceEquals(_lanes[_seamGroups[g].Root].Row, drag.Row))
                own = g;
        }

        // Which group is above the seam and which below it (-1: the end of the stack), the row the new track goes beside, where the bar is.
        (int Upper, int Lower, TimelineRow Beside, bool Above, double Y)? found = null;
        var (first, last) = (_seamGroups[0], _seamGroups[^1]);
        if (y < first.Top + first.Band)
        {
            found = (-1, 0, _lanes[first.Root].Row, true, first.Top);
        }
        else if (y >= last.Bottom - last.Band && (y < last.Bottom + LaneGap || BelowPictures(y)))
        {
            found = (_seamGroups.Count - 1, -1, _lanes[last.Root].Row, false, last.Bottom);
        }
        else
        {
            for (var g = 0; g + 1 < _seamGroups.Count; g++)
            {
                var (upper, lower) = (_seamGroups[g], _seamGroups[g + 1]);
                if (lower.First == upper.Last + 1 && y >= upper.Bottom - upper.Band && y < lower.Top + lower.Band)
                {
                    found = (g, g + 1, _lanes[lower.Root].Row, true, (upper.Bottom + lower.Top) / 2);
                    break;
                }
            }
        }

        if (found is not { } seam)
            return null;

        // At its own track's edges, only the one the pointer is heading for: up for the seam over it, down for the one under it.
        if (own >= 0 && ((seam.Lower == own && y >= drag.PointerY) || (seam.Upper == own && y <= drag.PointerY)))
            return null;
        return (seam.Beside, seam.Above, seam.Y);
    }

    /// <summary>Whether a height under the picture rows is still below them for a new track: empty space under every row, or the Background row.</summary>
    private bool BelowPictures(double y)
    {
        var under = LaneAt(y);
        return under < 0 || _lanes[under] is { Depth: 0, Row.Kind: TimelineRowKind.Background };
    }

    // How deep a seam's band is on each side at most (DIPs of the row's own lane): a quarter of the lane where that is less.
    private const double SeamBand = 14;

    // A run of lanes that one top-level picture row and its stacks make (NewTrackSeamAt): its root lane, first and last lane, extent and band.
    private readonly record struct SeamGroup(int Root, int First, int Last, double Top, double Bottom, double Band);

    private readonly List<SeamGroup> _seamGroups = [];

    /// <summary>Asks the host whether the block may go onto a new track at this seam; when it may, the drag is over it (true). Refused, nothing changes.</summary>
    private bool OffersNewTrack(BlockDrag drag, (TimelineRow Beside, bool Above, double Y) seam)
    {
        if (drag.Block.CanMoveToNewTrack is not { } canNew)
            return false;

        var (fits, at) = (false, drag.Block.Left);
        Raise(() => fits = canNew(seam.Beside.Data, seam.Above, at));
        if (!fits)
            return false;

        (drag.NewTrack, drag.NewTrackFits) = (seam, true);
        return true;
    }

    // The other row a moved block is over (one it may be let go on, its CanMoveTo says whether), or none.
    private void TargetUnder(BlockDrag drag, Point point)
    {
        var index = OpenLaneOf(LaneAt(point.Y));
        var row = index >= 0 ? _lanes[index].Row : null;
        if (row is null || ReferenceEquals(row, drag.Row) || drag.Block.CanMoveTo is not { } canMoveTo)
        {
            drag.Target = null;
            return;
        }

        drag.Target = row;
        var (fits, at) = (false, drag.Block.Left);
        Raise(() => fits = canMoveTo(row.Data, at));
        drag.Fits = fits;
    }

    /// <summary>A gain line dragged up or down: the gain.</summary>
    private void FollowGain((TimelineRow Row, TimelineBlock Block, Point From, (double GainDb, double FadeIn, double FadeOut) Start) drag, Point point)
    {
        var index = LaneOf(drag.Row);
        if (index < 0)
            return;

        var rect = BlockRect(_lanes[index], drag.Block, 0);
        drag.Block.Gain = drag.Start with { GainDb = GainAt(rect, GainY(rect, drag.Start.GainDb) + point.Y - drag.From.Y) };
        _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// A fade handle dragged along: its fade's length, from the block's edge to where the handle is, snapped (to the other
    /// clips' edges and the playhead) and no longer than the block leaves beside its other fade. The gain line's slope follows.
    /// </summary>
    private void FollowFade((TimelineRow Row, TimelineBlock Block, bool In, (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) Start) drag, Point point)
    {
        var scale = PixelsPerSecond;
        var index = LaneOf(drag.Row);
        if (scale <= 0 || index < 0)
            return;

        var (block, start) = (drag.Block, drag.Start);
        var rect = BlockRect(_lanes[index], block, 0);
        var x = Snap(point.X);
        var room = Math.Max(rect.Width / scale - (drag.In ? start.Out : start.In), 0);
        var seconds = Math.Clamp((drag.In ? x - rect.Left : rect.Right - x) / scale, 0, room);
        seconds = PrecisionRate() is var rate and > 0 ? Math.Floor(seconds * rate + 0.5) / rate : Math.Round(seconds, 2);
        block.Fades = drag.In ? start with { In = seconds } : start with { Out = seconds };
        if (block.Gain is { } gain)
            block.Gain = drag.In ? gain with { FadeIn = seconds } : gain with { FadeOut = seconds };
        _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// A J/L cut dragged: the sound's start (or end) follows the pointer, snapped as an edge is, the picture staying where
    /// it is. The sound keeps at least a frame.
    /// </summary>
    private void FollowSplit(SplitDrag split, Point point)
    {
        var scale = PixelsPerSecond;
        var index = LaneOf(split.Row);
        if (scale <= 0 || index < 0)
            return;

        var rect = BlockRect(_lanes[index], split.Block, 0);
        var picture = rect.Width / scale;
        var least = 1 / Math.Max(FrameRate?.Invoke() ?? 0, 30);
        var moved = point.X - split.PointerX;
        if (split.AtLead)
        {
            var x = Snap(rect.Left - split.StartLead * scale + moved);
            split.Lead = Math.Round(Math.Max((rect.Left - x) / scale, -(picture - split.Tail - least)), 3);
        }
        else
        {
            var x = Snap(rect.Right + split.StartTail * scale + moved);
            split.Tail = Math.Round(Math.Max((x - rect.Right) / scale, -(picture - split.Lead - least)), 3);
        }

        _overlayDirty = true;
        InvalidateVisual();
    }

    // The pointer moved with nothing held: what it is over shows its cursor, its tooltip and its fade handles, and the skim
    // line follows it over the time area.
    private void HoverAt(Point point, Point laid)
    {
        var index = LaneAt(laid.Y);

        // The pill that closes a gap shows while the pointer is in that gap.
        var inTime = point.X >= _headerWidth && index >= 0;
        HoverGap(inTime ? _lanes[index].Row : null, inTime ? GapUnder(_lanes[index], laid) : -1);
        if (point.X < _headerWidth)
        {
            Skim(double.NaN);
            Hover(null, null);
            if (index < 0)
            {
                NoTip();
                Cursor = null;
                return;
            }

            var header = _lanes[index];
            var at = new Point(point.X, laid.Y);
            if (HeightEdgeAt(laid.Y) >= 0)
            {
                Cursor = Cursors.SizeNS;
                if (TipFor(header.Row, 30))
                    ToolTip = "Drag to make every track taller or lower (Alt + the wheel over the tracks does too). A double-click puts them back to their usual height.";
                return;
            }

            if (ChevronAt(header, at) is { } stack)
            {
                Cursor = Cursors.Hand;
                if (TipFor(header.Row, stack == ChildStack.Audio ? 5 : 1))
                    ToolTip = ChevronTip(header, stack, IsExpanded(header.Row, stack));
                return;
            }

            if (SwitchAt(header, at) is { } flag)
            {
                Cursor = Cursors.Hand;
                if (TipFor(header.Row, 10 + (int)flag))
                    ToolTip = SwitchTip(flag, (header.Row.State & flag) != 0);
                return;
            }

            if (BadgeAt(header, at))
            {
                Cursor = Cursors.Hand;
                if (TipFor(header.Row, 6))
                {
                    ToolTip = header.Row.Badge is { Length: > 0 } preset
                        ? $"Track Preset: {preset}. Every video dropped or moved onto this track takes it, with Cutouts of its own. Click to choose another, or clear it."
                        : "Set a Track Preset: a Game Preset every video dropped or moved onto this track takes, with Cutouts of its own.";
                }

                return;
            }

            Cursor = null;
            if (TipFor(header.Row, 2))
                ToolTip = header.Row.Tip;
            return;
        }

        Skim(TimeAt(point.X));
        if (index < 0)
        {
            Hover(null, null);
            NoTip();
            Cursor = Cursors.Hand;
            return;
        }

        var lane = _lanes[index];
        if (CrossfadeAt(lane, laid) is { } crossfade)
        {
            Hover(null, null);
            Cursor = crossfade.Part == CrossfadePart.Edge ? Cursors.SizeWE : crossfade.Part == CrossfadePart.Remove ? Cursors.Hand : Cursors.Arrow;
            if (TipFor(lane.Row, 40 + (int)crossfade.Part))
            {
                ToolTip = crossfade.Part switch
                {
                    CrossfadePart.Edge => "Drag to make the crossfade longer or shorter.",
                    CrossfadePart.Remove => "Remove this crossfade.",
                    _ => "A crossfade: drag its edges to size it, click it and press Delete to take it away.",
                };
            }

            return;
        }

        var row = lane.Row;
        var scale = PixelsPerSecond;
        if (RevealAt(lane, laid) is >= 0 and var reveal && !OnFadeHandle(index, laid))
        {
            Hover(null, null);
            Cursor = Cursors.Hand;
            if (TipFor(row, 1_000 + reveal))
            {
                var mark = row.Reveals[reveal];
                ToolTip = string.Create(CultureInfo.InvariantCulture,
                    $"Dead air ({mark.To - mark.From:0.00} s) that was put back here. Click to fold it out again; Undo puts it back.");
            }

            return;
        }

        if (SeamAt(lane, laid) is >= 0 and var seam && !OnFadeHandle(index, laid))
        {
            Hover(null, null);
            Cursor = Cursors.Hand;
            if (TipFor(row, 100 + seam))
                ToolTip = string.Create(CultureInfo.InvariantCulture, $"Dead air was folded out here ({row.Seams[seam].FoldedSeconds:0.00} s). Click to put it back.");
            return;
        }

        if (GapAt(lane, laid) is >= 0 and var gap && scale > 0)
        {
            Hover(null, null);
            Cursor = Cursors.Hand;
            var (left, right) = row.Gaps[gap];
            if (TipFor(row, 100_000 + gap))
                ToolTip = string.Create(CultureInfo.InvariantCulture, $"A gap of {(right - left) / scale:0.00} s. Click to close it: what follows on this track moves up to meet what is before it.");
            return;
        }

        var block = BlockAt(index, laid);
        Hover(block is null ? null : row, block);
        if (block is null || !lane.Detailed)
        {
            NoTip();
            Cursor = Cursors.Hand;
            return;
        }

        // The edges are the handles for the length and the start; the cut between two clips rolls; Alt slips (and, on the
        // lower part of an edge of a clip with linked sound, makes a J/L cut); the top corners (or as far in as a fade) are
        // the fades' handles.
        var rect = BlockRect(lane, block, 0);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var fadeHandle = FadeHandleAt(lane, block, laid);
        var edge = TimelineMetrics.EdgePixels;

        // An edge at a seam of folded dead air: dragged, it shows the fold again or folds more into it (never a roll there).
        var atSeam = fadeHandle is null && !alt && block.SeamTrim is not null
            && (laid.X < block.Left + edge ? SeamNear(row, block.Left) : laid.X > block.Left + block.Width - edge ? SeamNear(row, block.Left + block.Width) : null) is not null;
        if (atSeam)
        {
            if (TipFor(block, 6))
                ToolTip = "An edge at folded dead air: drag it outward to show some of the folded silence again, or inward to fold more of this clip into it. What follows slides with it.";
        }
        else
        {
            FadeTip(block, fadeHandle);
        }
        Cursor = fadeHandle is not null ? Cursors.Cross
            : alt && SplitEdgeAt(block, rect, laid) is not null ? Cursors.SizeWE
            : OnGainLine(lane, block, laid) ? Cursors.SizeNS
            : block is { Resizable: true, CanDrag: not null } && laid.X > block.Left + block.Width - edge ? Cursors.SizeWE
            : block is { TrimmableStart: true, CanDrag: not null, Edit: not null } && laid.X < block.Left + edge ? Cursors.SizeWE
            : block is { Slippable: true, Edit: not null } && alt ? Cursors.ScrollWE
            : block.CanDrag is not null ? Cursors.SizeAll
            : Cursors.Hand;
    }

    /// <summary>The pointer is over another block (or none): the one it left hides its corner handles, the one it is over shows them.</summary>
    private void Hover(TimelineRow? row, TimelineBlock? block)
    {
        if (ReferenceEquals(block, _hovered?.Block))
            return;

        var shows = _hovered?.Block.Fades is not null || block?.Fades is not null;
        _hovered = row is not null && block is not null ? (row, block) : null;
        if (shows)
        {
            _overlayDirty = true;
            InvalidateVisual();
        }
    }

    /// <summary>The pointer is in another gap (or none, -1): the pill of the one it left goes, the one it is in shows its own.</summary>
    private void HoverGap(TimelineRow? row, int gap)
    {
        if (gap < 0)
            row = null;
        if (ReferenceEquals(row, _hoveredGap?.Row) && (row is null || gap == _hoveredGap?.Index))
            return;

        _hoveredGap = row is not null ? (row, gap) : null;
        _overlayDirty = true;
        InvalidateVisual();
    }

    // Whether the tooltip is now for something else (and is to be set): it is set once per thing the pointer is over.
    private bool TipFor(object? target, int part)
    {
        if (ReferenceEquals(target, _tipTarget) && part == _tipPart)
            return false;

        (_tipTarget, _tipPart) = (target, part);
        return true;
    }

    private void NoTip()
    {
        if (TipFor(null, 0))
            ToolTip = null;
    }

    private void ForgetTip()
    {
        _tipTarget = null;
        _tipPart = -1;
    }

    /// <summary>Over a fade handle, a tooltip saying what it is and what it does; elsewhere on the block, none.</summary>
    private void FadeTip(TimelineBlock block, bool? fadeIn)
    {
        if (fadeIn is not { } isIn || block.Fades is not { } fades)
        {
            if (TipFor(block, 0))
                ToolTip = null;
            return;
        }

        if (!TipFor(block, isIn ? 3 : 4))
            return;

        var seconds = isIn ? fades.In : fades.Out;
        ToolTip = (seconds > 0.005 ? FadeWords(isIn, seconds, isIn ? fades.InStyle : fades.OutStyle) : $"Fade {(isIn ? "In" : "Out")}: none")
            + $". Drag {(isIn ? "right" : "left")} to fade the clip's picture and sound {(isIn ? "in" : "out")} over what is under it; it snaps to where clips on the other tracks start and end"
            + ", for a crossfade with one. Right-click for the transition style.";
    }

    private static string SwitchTip(RowSwitches flag, bool on) => flag switch
    {
        RowSwitches.Hidden => on ? "Hidden: click to show this track again" : "Hide this track (it is left out of the picture)",
        RowSwitches.Muted => on ? "Muted: click to hear this track again" : "Mute this track",
        RowSwitches.Soloed => on ? "Soloed: click to stop soloing it" : "Solo: hear only the soloed tracks. Alt+click: solo this track alone",
        RowSwitches.Voice => on ? "A voice track: click to unmark it" : "Mark as a voice track (what ducking and captions listen to)",
        RowSwitches.Locked => on ? "Locked: click to unlock this track" : "Lock this track against edits",
        _ => "",
    };

    /// <summary>
    /// The moment under a point of the control told to Instant Seek (5.0, <see cref="PointerSecondsChanged"/>): in the time
    /// area, the sequence's seconds there (never before 0); over the headers or outside, none.
    /// </summary>
    private void NotePointer(Point point) =>
        PointerSeconds(point.X >= _headerWidth && point.X <= ActualWidth && point.Y >= 0 && point.Y <= ActualHeight ? Math.Max(TimeAt(point.X), 0) : double.NaN);

    /// <summary>
    /// The pointer is over the ruler beside the rows at a moment (seconds), or has left it (null): the host relays the ruler's
    /// own pointer (TimelineRuler.PointerSecondsChanged) so that <see cref="PointerSecondsChanged"/> tells of both (5.0).
    /// </summary>
    public void NotePointerOverRuler(double? seconds) => PointerSeconds(seconds is { } at ? Math.Max(at, 0) : double.NaN);

    // Tells of the moment under the pointer when it is another (NaN: none, told once).
    private void PointerSeconds(double seconds)
    {
        if (seconds.Equals(_pointerSeconds))
            return;

        var was = _pointerSeconds;
        _pointerSeconds = seconds;
        if (PointerSecondsChanged is not { } changed)
            return;

        try
        {
            if (!double.IsNaN(seconds))
                changed(seconds);
            else if (!double.IsNaN(was))
                changed(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A hint is never worth a fault: told once in the log, the pointer goes on.
            Services.AppLog.Write("Timeline: telling of the moment under the pointer failed", ex);
        }
    }

    /// <summary>
    /// The blocks between two (found by what they stand for), for Shift + click (5.0): those of every lane from the one's to
    /// the other's (the lanes shown in full between them, and the two lanes themselves) that overlap the time from the earlier
    /// one's start to the later one's end, top to bottom. Empty when either is not on the timeline.
    /// </summary>
    public List<TimelineBlock> BlocksInRange(object from, object to)
    {
        var found = new List<TimelineBlock>();
        var (first, last) = (LaneAndBlockOf(from), LaneAndBlockOf(to));
        if (first is not { } a || last is not { } b)
            return found;

        var (top, bottom) = (Math.Min(a.Lane, b.Lane), Math.Max(a.Lane, b.Lane));
        var (left, right) = (Math.Min(a.Block.Left, b.Block.Left), Math.Max(a.Block.Left + a.Block.Width, b.Block.Left + b.Block.Width));
        for (var i = top; i <= bottom; i++)
        {
            if (!_lanes[i].Detailed && i != a.Lane && i != b.Lane)
                continue;

            foreach (var block in _lanes[i].Row.Blocks)
            {
                if (Math.Min(block.Left + block.Width, right) - Math.Max(block.Left, left) >= 0.5 && !found.Contains(block))
                    found.Add(block);
            }
        }

        return found;
    }

    // The lane and the block that stand for something (the first found, top to bottom); null when none does.
    private (int Lane, TimelineBlock Block)? LaneAndBlockOf(object clip)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            foreach (var block in _lanes[i].Row.Blocks)
            {
                if (Equals(block.Clip, clip))
                    return (i, block);
            }
        }

        return null;
    }

    /// <summary>Moves the skim line to a moment (NaN: none) and says so.</summary>
    private void Skim(double seconds)
    {
        if (seconds.Equals(_skimSeconds))
            return;

        var was = _skimSeconds;
        _skimSeconds = seconds;
        PlaceLine(_skim, seconds, ref _skimHeight, playhead: false);
        if (!double.IsNaN(seconds))
            SkimChanged?.Invoke(seconds);
        else if (!double.IsNaN(was))
            SkimChanged?.Invoke(null);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        // A right-click under way is not captured: let go elsewhere, its release never comes here, so it opens no menu and
        // stops listening to the window's keys now.
        DisarmMenu();
        if (IsDragging)
            return;

        Hover(null, null);
        HoverGap(null, -1);
        NoTip();
        Skim(double.NaN);
        PointerSeconds(double.NaN);
    }

    private void AutoScroll_Tick(object? sender, EventArgs e)
    {
        var width = TimeAreaWidth;
        if (_drag is not { } drag || Mouse.LeftButton != MouseButtonState.Pressed || width <= 0)
        {
            _autoScroll.Stop();
            return;
        }

        // Faster the further into the end the pointer is: up to about 3% of the time area every tick.
        var seen = _lastPointer.X - ViewOffsetX;
        var depth = seen < AutoScrollZone ? seen - AutoScrollZone : seen > width - AutoScrollZone ? seen - (width - AutoScrollZone) : 0;
        var pixels = Math.Clamp(depth / AutoScrollZone, -1.5, 1.5) * width * 0.03;
        if (Math.Abs(pixels) < 0.5)
            return;

        // The host moves the view (SetView), and the pointer is where it was on screen: further along the rows.
        var before = ViewOffsetX;
        PanRequested?.Invoke(pixels);
        _lastPointer.X += ViewOffsetX - before;
        Follow(drag, _lastPointer, pointerMoved: false);
    }

    /// <summary>The frame rate to step by while Shift is held (Precision Unsnapped mode); 0 when Shift is not held or no rate is known.</summary>
    private double PrecisionRate() =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && FrameRate?.Invoke() is > 0 and var rate ? rate : 0;

    /// <summary>
    /// A point along the rows moved onto the nearest moment to snap to, when one is close enough; as it was otherwise.
    /// Shift held (Precision Unsnapped mode): no snapping, and the point is put on the nearest frame of the sequence, so an
    /// edge dragged with Shift trims frame by frame. Alt held: no snapping either (Alt-drag inside a clip slips).
    /// </summary>
    private double Snap(double x)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0)
            return x;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return PrecisionRate() is var rate and > 0 ? Math.Floor(x / scale * rate + 0.5) / rate * scale : x;

        if (_snapTimes.Count == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return x;

        var best = x;
        var nearest = TimelineMetrics.SnapPixels;
        for (var i = 0; i < _snapTimes.Count; i++)
        {
            var at = _snapTimes[i] * scale;
            if (Math.Abs(at - x) < nearest)
                (best, nearest) = (at, Math.Abs(at - x));
        }

        return best;
    }

    // ----- The tracks' height (5.1-Polish, the user's directive) -----

    /// <summary>The lane whose lower edge is at <paramref name="laidY"/> (within <see cref="HeightEdgeReach"/>): a picture's or a sound's row shown in full; -1 for none.</summary>
    private int HeightEdgeAt(double laidY)
    {
        foreach (var candidate in (ReadOnlySpan<int>)[LaneAt(laidY), LaneAt(laidY - HeightEdgeReach - LaneGap)])
        {
            if (candidate < 0)
                continue;
            var lane = _lanes[candidate];
            if (!lane.Mini && lane.Row.Kind is TimelineRowKind.Video or TimelineRowKind.Audio && Math.Abs(laidY - lane.Bottom) <= HeightEdgeReach)
                return candidate;
        }

        return -1;
    }

    /// <summary>How many rows that grow with the tracks' height lie from the top down to lane <paramref name="index"/>, itself included.</summary>
    private int RowsGrowingDownTo(int index)
    {
        var rows = 0;
        for (var i = 0; i <= index && i < _lanes.Count; i++)
        {
            if (!_lanes[i].Mini && !_lanes[i].Row.HasOwnHeight && _lanes[i].Row.Kind is TimelineRowKind.Video or TimelineRowKind.Audio)
                rows++;
        }

        return Math.Max(rows, 1);
    }

    // ----- The wheel and the middle button -----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Always answered here (5.1-DogfoodPolish, the user's bug: the wheel with the left button held), and it works under a drag.
        e.Handled = true;
        var x = e.GetPosition(this).X;
        WheelAt(e.Delta, Keyboard.Modifiers, MomentumScroller.Now(), Math.Max(x - _headerWidth, 0), overTime: x >= _headerWidth);
    }

    // The wheel's glide (5.4.1-M1): the plain wheel's pan over the clips and Ctrl + wheel's zoom, chased smoothly (made at the first use).
    private WheelGlide? _wheelGlide;

    private WheelGlide Glide => _wheelGlide ??= new(pixels => PanRequested?.Invoke(pixels), (factor, at) => ZoomRequested?.Invoke(factor, at), () => IsVisible);

    /// <summary>A notch of the plain wheel over the clips pans this share of the view's width (5.4.1-M1).</summary>
    internal const double WheelPanShare = 0.10;

    /// <summary>
    /// The wheel (<paramref name="delta"/>, 120 a notch, up positive) with these keys at <paramref name="nowMs"/>. 5.4.1-M1: over the clips
    /// (<paramref name="overTime"/>) the plain wheel pans the view, a tenth of its width a notch (down: later), gliding there; over the
    /// track headers, or with Alt, it scrolls the rows with momentum (5.4C); Alt + Shift steps the tracks' height (5.1-Polish); Ctrl zooms
    /// about <paramref name="zoomAt"/> (pixels from the time area's left), gliding there with the pointer's moment pinned; Shift pans the
    /// view with momentum (a fast stream coasts on, a tick against the coast stops it). Under a drag the wheel moves exactly a notch, at
    /// once, and never coasts. Test seam; <see cref="OnMouseWheel"/> calls it.
    /// </summary>
    internal void WheelAt(int delta, ModifierKeys modifiers, double nowMs, double zoomAt = 0, bool overTime = false)
    {
        // Alt + Shift + the wheel (5.1-Polish, the user's directive; Alt alone scrolls the rows since 5.4.1-M1): every track taller (a
        // notch up) or lower.
        if (modifiers.HasFlag(ModifierKeys.Alt) && modifiers.HasFlag(ModifierKeys.Shift))
        {
            StopMomentum();
            var notches = (int)Math.Round(delta / 120.0);
            var steps = notches != 0 ? notches : Math.Sign(delta);
            Raise(() => TrackHeightStepped?.Invoke(steps));
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            _momentumX?.Stop();
            _momentumY?.Stop();
            _wheelGlide?.StopPan();
            if (IsDragging)
                ZoomRequested?.Invoke(Math.Pow(1.25, delta / 120.0), zoomAt);
            else
                Glide.Zoom(Math.Pow(1.25, delta / 120.0), zoomAt);
        }
        else if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            PanWheelAt(-delta, nowMs);
        }
        else if (overTime && !modifiers.HasFlag(ModifierKeys.Alt))
        {
            // The plain wheel over the clips: along time, a tenth of the view a notch, gliding (a coast of the rows or the view stops).
            _momentumX?.Stop();
            _momentumY?.Stop();
            var pixels = -delta / 120.0 * WheelPanShare * TimeAreaWidth;
            if (IsDragging)
                PanRequested?.Invoke(pixels);
            else
                Glide.Pan(pixels);
        }
        else
        {
            var rows = -delta / 120.0 * WheelStep;
            if (IsDragging)
            {
                VerticalOffset = _verticalOffset + rows;
            }
            else
            {
                var (before, step) = (_verticalOffset, MomentumY.Wheel(rows, nowMs));
                VerticalOffset = before + step;

                // At the top or the bottom already: nothing coasts on into it.
                if (step != 0 && _verticalOffset == before)
                    MomentumY.Stop();
                KeepTicking();
            }
        }

        FollowHeldDrag();
    }

    /// <summary>
    /// A wheel along the view by <paramref name="pixels"/> (positive: later) at <paramref name="nowMs"/>: Shift + the wheel (its notch
    /// negated) and the sideways wheel of a touchpad or a tilting wheel (MainWindow's own hook, as Windows sends it), with momentum; under
    /// a drag exactly as given, never coasting. Test seam.
    /// </summary>
    internal void PanWheelAt(double pixels, double nowMs)
    {
        if (IsDragging)
        {
            PanRequested?.Invoke(pixels);
            return;
        }

        var (before, step) = (ViewOffsetX, MomentumX.Wheel(pixels, nowMs));
        if (step != 0)
            PanRequested?.Invoke(step);

        // The view at an end, or fitted (it does not pan): nothing coasts on into it.
        if (step != 0 && ViewOffsetX == before)
            MomentumX.Stop();
        KeepTicking();
    }

    /// <summary>The middle button pressed at <paramref name="x"/> (pixels from the control's left): any coast stops and the pan begins. Test seam.</summary>
    internal void MiddlePanPress(double x, double nowMs)
    {
        _panFrom = x;
        _panTravel = 0;
        MomentumX.Press(nowMs);
        MomentumX.Sample(0, nowMs);
    }

    /// <summary>The middle-button drag at <paramref name="x"/>: the view pans with the pointer, one pixel for one. Test seam.</summary>
    internal void MiddlePanMove(double x, double nowMs)
    {
        if (_panFrom is not { } from)
            return;

        _panFrom = x;
        _panTravel += from - x;
        MomentumX.Sample(_panTravel, nowMs);
        PanRequested?.Invoke(from - x);
    }

    /// <summary>The middle button let go: a flick (250 DIP/s or more, still moving within the last 40 ms) coasts on. Test seam.</summary>
    internal void MiddlePanRelease(double nowMs)
    {
        _panFrom = null;
        if (MomentumX.Release(nowMs) != 0)
            KeepTicking();
    }

    // A coast begun: the control's own frame loop carries it, while the control is on screen (a test steps it itself).
    private void KeepTicking()
    {
        if (((_momentumX?.IsCoasting ?? false) || (_momentumY?.IsCoasting ?? false)) && IsVisible)
            StartTicking();
    }

    // Any press on the control (any button, touch and the pen too, before anything else of it): a coast stops where it is (5.4C).
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        StopMomentum();
        _wheelGlide?.Stop();
    }

    /// <summary>The view moved under a drag held still: what is dragged goes on from where the pointer is over the view now.</summary>
    private void FollowHeldDrag()
    {
        if (!IsDragging)
            return;

        var laid = Laid(Mouse.GetPosition(this));
        if (_splitDrag is { } split)
        {
            FollowSplit(split, laid);
        }
        else if (_fadeDrag is { } fade)
        {
            FollowFade(fade, laid);
        }
        else if (_crossfadeDrag is { } crossfade)
        {
            FollowCrossfade(crossfade, laid);
        }
        else if (_gainDrag is { } gain)
        {
            FollowGain(gain, laid);
        }
        else if (_drag is { } drag)
        {
            _lastPointer = laid;
            Follow(drag, laid, pointerMoved: false);
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && !IsDragging && CaptureMouse())
        {
            MiddlePanPress(e.GetPosition(this).X, MomentumScroller.Now());
            Cursor = Cursors.ScrollWE;
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panFrom is not null)
        {
            // Let go of the capture first (losing it mid-drag stops a coast), then the flick, if it was one, coasts on.
            var now = MomentumScroller.Now();
            _panFrom = null;
            ReleaseMouseCapture();
            MiddlePanRelease(now);
            e.Handled = true;
        }
    }
}
