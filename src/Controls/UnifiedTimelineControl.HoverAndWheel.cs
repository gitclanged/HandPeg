using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// ----- Unified timeline mouse: hit-testing, move handling, track height, wheel and middle button -----
public sealed partial class UnifiedTimelineControl
{
    // ----- Hit-testing -----

    /// <summary>Block at a layout point in a lane, topmost (last drawn) first; includes J/L audio extensions.</summary>
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

    // J/L audio band rect: the block's lower part extended past its edges.
    private Rect? SoundReach(TimelineBlock block, Rect rect)
    {
        var scale = PixelsPerSecond;
        if (block.AudioSplit is not { } split || (split.LeadIn == 0 && split.TailOut == 0) || scale <= 0)
            return null;

        return new Rect(rect.Left - split.LeadIn * scale, rect.Top + rect.Height * SoundBandTop,
            Math.Max(rect.Width + (split.LeadIn + split.TailOut) * scale, 1), rect.Height * (1 - SoundBandTop));
    }

    /// <summary>J/L audio edge hit in the lower band: true = start, false = end, null = none.</summary>
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

    /// <summary>Fade handle hit: true = fade in, false = fade out, null = none.</summary>
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

    /// <summary>Gain line hit (excluding the ends, which trim).</summary>
    private static bool OnGainLine(in Lane lane, TimelineBlock block, Point laid)
    {
        if (block.Gain is not { } gain || block.GainEdit is null)
            return false;

        var rect = BlockRect(lane, block, 0);
        return Math.Abs(laid.Y - GainY(rect, gain.GainDb)) <= GainReach + 1
            && laid.X > rect.Left + TimelineMetrics.EdgePixels && laid.X < rect.Right - TimelineMetrics.EdgePixels;
    }

    /// <summary>True on a fade handle of the block beneath; takes precedence over a seam tab.</summary>
    private bool OnFadeHandle(int index, Point laid) =>
        _lanes[index].Detailed && BlockAt(index, laid) is { } block && FadeHandleAt(_lanes[index], block, laid) is not null;

    /// <summary>Index of the seam tab hit in the row's seams; -1 for none.</summary>
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

    /// <summary>Index of the gap pill hit (only the hovered gap's pill is shown and hit-testable); -1 for none.</summary>
    private int GapAt(in Lane lane, Point laid)
    {
        if (!lane.Detailed || IsDragging || _hoveredGap is not { } hovered || !ReferenceEquals(hovered.Row, lane.Row) || hovered.Index >= lane.Row.Gaps.Count)
            return -1;

        var (left, right) = lane.Row.Gaps[hovered.Index];
        return right - left >= GapLeast && Rect.Inflate(GapPill(lane, left, right), 2, 2).Contains(laid) ? hovered.Index : -1;
    }

    /// <summary>Index of the gap containing a point on a full-size lane (its pill shows); -1 for none.</summary>
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

    /// <summary>Chevron hit (x in control space, y in row space): the stack it toggles; null for none.</summary>
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

    // Chevron tooltip: names the stack (or which of two). An effect-lanes-only stack (e.g. a Cutout with its own effect) shows/hides them
    // (no thin bars); a mixed stack mentions them.
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

    /// <summary>Index of the unfolded range whose Hide pill is hit; -1 for none.</summary>
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

    /// <summary>Header switch hit (x in control space, y in row space); null for none.</summary>
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

    /// <summary>True on a header preset badge (x in control space, y in row space).</summary>
    private bool BadgeAt(in Lane lane, Point at) =>
        (lane.Row.Badge is { Length: > 0 } || lane.Row.OffersBadge) && BadgeRect(lane, VisualTreeHelper.GetDpi(this).PixelsPerDip) is { IsEmpty: false } badge
        && badge.Contains(at);

    // ----- Pointer move -----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        // Spring-Zoom rest and Ghost Playhead while dragging, the ducking drag, hover strips (gestures, Parenting.cs).
        if (TakeMove(point))
            return;
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

        // Header press/drag; require the button held (CaptureMouse raises a synthetic move at the pointer).
        if (_rowDrag is not null)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                FollowRowDrag(point, Now());
            return;
        }

        if (_drag is not { } drag)
        {
            HoverAt(point, laid);
            return;
        }

        // Over an external drop target the block parks at its origin and the view stays still. Refresh modifiers (Alt mid-move tethers,
        // Shift keeps time) and restart the dwell re-check.
        _lastPointer = laid;
        drag.Keys = Keyboard.Modifiers;
        if (!FollowPointer(drag, point, laid, Now()))
            return;
        if (drag is { Kind: BlockEdit.Move, Group: not { Count: > 0 } })
            RestSoon();

        // Edge auto-scroll while the pointer stays near either end of the time area.
        var seen = point.X - _headerWidth;
        var nearEdge = seen < AutoScrollZone || seen > TimeAreaWidth - AutoScrollZone;
        if (nearEdge && !_autoScroll.IsEnabled)
            _autoScroll.Start();
        else if (!nearEdge)
            _autoScroll.Stop();
    }

    /// <summary>
    /// Positions the dragged block (and roll partner) for the layout-space pointer. <paramref name="pointerMoved"/>: pointer moved or the
    /// dwell elapsed (SettleTick), so drop targets are re-evaluated; otherwise the view moved under a still pointer and targets are re-queried.
    /// <paramref name="nowMs"/>: drag clock for velocity-gated drop zones (DragSettle).
    /// </summary>
    private void Follow(BlockDrag drag, Point point, bool pointerMoved, double nowMs)
    {
        // Clips may move or extend past the sequence end (duration = furthest clip end); rows re-lay to the new length on release.
        var moved = point.X - drag.PointerX;
        if (pointerMoved)
            drag.Travel = Math.Max(drag.Travel, Math.Abs(moved));

        switch (drag.Kind)
        {
            // Seam edge: outward unfolds, inward folds more, within fold and media limits (seconds; Shift = whole frames). The clip
            // resizes at that edge with its start pinned to the cut; later content is drawn shifted by the same amount (CurrentShift).
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
                // Shift on a move that left its lane (over another row, a "+ New Track" seam or a tether): keep the exact original time
                // while held. Within its own lane (inside the deadzone) Shift stays Precision Unsnapped (frame steps). Never for groups,
                // which cannot change rows.
                var shift = drag.Keys.HasFlag(ModifierKeys.Shift) && drag.Group is not { Count: > 0 };
                drag.TimeLocked &= shift;
                PlaceMoved(drag, moved);
                if (drag.Group is { Count: > 0 })
                {
                    (drag.Target, drag.NewTrack, drag.Attach) = (null, null, null);
                }
                else if (pointerMoved)
                {
                    LookUnder(drag, point, nowMs);
                    if (shift && !drag.TimeLocked && (drag.Target is not null || drag.NewTrack is not null || drag.Attach is not null))
                    {
                        // Off-lane with Shift: snap back to its own time and re-evaluate targets there.
                        drag.TimeLocked = true;
                        PlaceMoved(drag, moved);
                        LookUnder(drag, point, nowMs);
                    }
                }
                else if (drag.Attach is { } held)
                {
                    // View moved under a tether: re-check that the tether still holds at the new position.
                    var (name, at) = ((string?)null, drag.Block.Left);
                    if (drag.Block.CanAttachTo is not { } canAttach || !Raise(() => name = canAttach(held.Block, at)) || name is null)
                        drag.Attach = null;
                    else if (name != held.Name)
                        drag.Attach = held with { Name = name };
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

        // Update carried blocks (children, unlinked audio, audio Sub-Lanes) on every move.
        FollowFamily(drag);
        _overlayDirty = true;
        InvalidateVisual();
    }

    // Moved block x: its original time while Shift time-lock applies, else pointer-relative with whichever edge snaps nearer (using the
    // drag's own modifiers; Shift = frame steps). Groups move together along their rows, clamped so no member crosses time 0.
    private void PlaceMoved(BlockDrag drag, double moved)
    {
        if (drag.TimeLocked)
        {
            drag.Block.Left = drag.Left;
            return;
        }

        var left = drag.Left + moved;
        var (toLeft, toRight) = (Snap(left, drag.Keys) - left, Snap(left + drag.Width, drag.Keys) - (left + drag.Width));
        left += Math.Abs(toLeft) <= Math.Abs(toRight) ? toLeft : toRight;
        drag.Block.Left = drag.Group is { Count: > 0 } ? Math.Max(left, drag.GroupFloor) : drag.Block.MayStartBeforeZero ? left : Math.Max(left, 0);
    }

    // Drop target for a moved single block at its drawn time, in priority order: tether (bottom edge of an overlapped clip on another
    // lane once settled; anywhere on it immediately with Alt), then a new-track seam (if the host allows; active only after the pointer
    // settles, or still active and not swept past), else the row under the pointer (beyond the deadzone).
    // DragSettle gates the first two by pointer velocity.
    private void LookUnder(BlockDrag drag, Point point, double nowMs)
    {
        BuildSeamGroups();
        if (AttachAt(drag, point, nowMs) is { } attach)
        {
            (drag.Target, drag.NewTrack, drag.Attach) = (null, null, attach);
        }
        else if (NewTrackSeamAt(point, drag) is { } seam
                 && Settle.Awake(drag.NewTrack is { } was && ReferenceEquals(was.Beside, seam.Beside) && was.Above == seam.Above, nowMs)
                 && OffersNewTrack(drag, seam))
        {
            (drag.Target, drag.Attach) = (null, null);
        }
        else
        {
            (drag.NewTrack, drag.Attach) = (null, null);
            TargetUnder(drag, point);
        }
    }

    /// <summary>
    /// New-track seam under a moved block (above the top video track, below the bottom, or in the band between two): the top-level video
    /// row to insert beside, above/below, and the insertion bar y (layout). A between-rows seam is expressed as "above the lower row". Each
    /// side's band is the outer min(14 DIPs, quarter lane) of the top-level row's own lane, so minimum-height rows keep a landing middle.
    /// Only for single blocks that can move to a new track, after 2x the system drag distance vertically from the press (so edge-adjacent
    /// horizontal moves never trigger); at the block's own track edges only the seam in its direction counts. Null for none. Groups come
    /// from the caller (<see cref="BuildSeamGroups"/>, once per move).
    /// </summary>
    private (TimelineRow Beside, bool Above, double Y)? NewTrackSeamAt(Point point, BlockDrag drag)
    {
        if (drag.Group is { Count: > 0 } || drag.Block.MoveToNewTrack is null || drag.Block.CanMoveToNewTrack is null
            || Math.Abs(point.Y - drag.PointerY) <= 2 * SystemParameters.MinimumVerticalDragDistance || _seamGroups.Count == 0)
        {
            return null;
        }

        var y = point.Y;
        var own = -1;
        for (var g = 0; g < _seamGroups.Count && own < 0; g++)
        {
            if (ReferenceEquals(_lanes[_seamGroups[g].Root].Row, drag.Row))
                own = g;
        }

        // Groups above and below the seam (-1 = stack end), the adjacent row for insertion, and the bar position.
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

        // At its own track's edges, accept only the seam in the movement direction (up = above, down = below).
        if (own >= 0 && ((seam.Lower == own && y >= drag.PointerY) || (seam.Upper == own && y <= drag.PointerY)))
            return null;
        return (seam.Beside, seam.Above, seam.Y);
    }

    /// <summary>True when a y below the video rows counts as "below" for a new track (empty space or the Background row).</summary>
    private bool BelowPictures(double y)
    {
        var under = LaneAt(y);
        return under < 0 || _lanes[under] is { Depth: 0, Row.Kind: TimelineRowKind.Background };
    }

    // Max seam band depth per side (DIPs of the row's lane), capped at a quarter of the lane.
    private const double SeamBand = 14;

    // Lane run of one top-level video row plus its stacks (NewTrackSeamAt): root, first and last lane, extent and band.
    private readonly record struct SeamGroup(int Root, int First, int Last, double Top, double Bottom, double Band);

    private readonly List<SeamGroup> _seamGroups = [];

    /// <summary>
    /// Video groups top to bottom, built once per move for seams, tether bands and header seams: each top-level Video (or empty) row's lane
    /// run (InFront lanes above, stacks below) with its band.
    /// </summary>
    private void BuildSeamGroups()
    {
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
    }

    /// <summary>Queries the host (CanMoveToNewTrack) for this seam; on success targets it and returns true, else leaves state unchanged.</summary>
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

    // Target row for a moved block (CanMoveTo decides validity), or none. Hysteresis: the current row holds until the pointer passes its
    // edges by more than the deadzone.
    private void TargetUnder(BlockDrag drag, Point point)
    {
        var index = StickyLaneAt(drag, point.Y);
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

    /// <summary>Gain line drag: updates the gain.</summary>
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
    /// Fade handle drag: fade length from the block edge to the handle, snapped (other clip edges, playhead) and capped by the space left
    /// by the other fade. The gain line slope follows.
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
    /// J/L drag: the audio start (or end) follows the pointer with edge snapping while the video stays fixed; audio keeps at least one frame.
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

    // Hover (no button): update cursor, tooltip and fade handles for the target, and move the skim line over the time area.
    private void HoverAt(Point point, Point laid)
    {
        var index = LaneAt(laid.Y);

        // Show the close-gap pill for the hovered gap.
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
            {
                ToolTip = left <= 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Close the {right / scale:0.00} s gap before the first clip")
                    : string.Create(CultureInfo.InvariantCulture, $"Close the {(right - left) / scale:0.00} s gap");
            }
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

        // Cursor zones: edges trim; a shared cut rolls; Alt slips (or, on the lower part of an edge with linked audio, J/L cuts); top
        // corners (or fade ends) are fade handles.
        var rect = BlockRect(lane, block, 0);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var fadeHandle = FadeHandleAt(lane, block, laid);
        var edge = TimelineMetrics.EdgePixels;

        // Seam edge: drag unfolds or folds more (never a roll).
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

    /// <summary>Hover target changed: hide the previous block's corner handles, show the new one's.</summary>
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

    /// <summary>Hovered gap changed (-1 = none): swap which pill is shown.</summary>
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

    // True when the tooltip target changed (tooltip set once per target).
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

    /// <summary>Fade handle tooltip; none elsewhere on the block.</summary>
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
    /// Raises <see cref="PointerSecondsChanged"/> for Instant Seek: sequence seconds (>= 0) in the time area; none over headers or outside.
    /// </summary>
    private void NotePointer(Point point) =>
        PointerSeconds(point.X >= _headerWidth && point.X <= ActualWidth && point.Y >= 0 && point.Y <= ActualHeight ? Math.Max(TimeAt(point.X), 0) : double.NaN);

    /// <summary>
    /// Host relay of the ruler's pointer time (TimelineRuler.PointerSecondsChanged; null on leave) so <see cref="PointerSecondsChanged"/>
    /// covers both ruler and rows.
    /// </summary>
    public void NotePointerOverRuler(double? seconds)
    {
        // Over the ruler (hovering or scrubbing) only the playhead line shows: no skim line beside it.
        if (seconds is not null)
            Skim(double.NaN);
        PointerSeconds(seconds is { } at ? Math.Max(at, 0) : double.NaN);
    }

    // Raises the pointer time only on change (NaN = none, raised once).
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
            // Hint failures are non-fatal: log once and continue.
            Services.AppLog.Write("Timeline: telling of the moment under the pointer failed", ex);
        }
    }

    /// <summary>
    /// Shift+click range: blocks on the two endpoint lanes and full-size lanes between them that overlap [earlier start, later end], top
    /// to bottom (endpoints resolved by model object). Empty if either endpoint is missing.
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

    // First lane/block (top to bottom) representing a model object; null if none.
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

    /// <summary>Moves the skim line (NaN hides it) and raises the skim event.</summary>
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

        // Pending right-clicks are uncaptured: if released elsewhere, no release arrives here, so cancel the menu and stop monitoring
        // window keys now.
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
        if (_drag is not { } drag || _dragParked || Mouse.LeftButton != MouseButtonState.Pressed || width <= 0)
        {
            _autoScroll.Stop();
            return;
        }

        // Speed scales with depth into the edge zone, up to ~3% of the time area per tick.
        var seen = _lastPointer.X - ViewOffsetX;
        var depth = seen < AutoScrollZone ? seen - AutoScrollZone : seen > width - AutoScrollZone ? seen - (width - AutoScrollZone) : 0;
        var pixels = Math.Clamp(depth / AutoScrollZone, -1.5, 1.5) * width * 0.03;
        if (Math.Abs(pixels) < 0.5)
            return;

        // The host pans (SetView) while the pointer stays put on screen, so it advances in layout space; re-place with current modifiers
        // (Shift may change during auto-scroll, group moves included).
        var before = ViewOffsetX;
        PanRequested?.Invoke(pixels);
        _lastPointer.X += ViewOffsetX - before;
        drag.Keys = Keyboard.Modifiers;
        Follow(drag, _lastPointer, pointerMoved: false, Now());
    }

    /// <summary>Frame-step rate while Shift is held (Precision Unsnapped); 0 without Shift or a known rate.</summary>
    private double PrecisionRate() => PrecisionRate(Keyboard.Modifiers);

    // PrecisionRate for explicit modifiers (a move's BlockDrag.Keys).
    private double PrecisionRate(ModifierKeys keys) =>
        keys.HasFlag(ModifierKeys.Shift) && FrameRate?.Invoke() is > 0 and var rate ? rate : 0;

    /// <summary>
    /// Snaps a layout x to the nearest snap point within range, else returns it unchanged. Shift (Precision Unsnapped): no snapping, round
    /// to the nearest sequence frame (frame-accurate trims). Alt: no snapping (Alt-drag slips).
    /// </summary>
    private double Snap(double x) => Snap(x, Keyboard.Modifiers);

    // Snap(x) for explicit modifiers (a move's BlockDrag.Keys, refreshed on every move and dwell tick).
    private double Snap(double x, ModifierKeys keys)
    {
        var scale = PixelsPerSecond;
        if (scale <= 0)
            return x;

        if (keys.HasFlag(ModifierKeys.Shift))
            return PrecisionRate(keys) is var rate and > 0 ? Math.Floor(x / scale * rate + 0.5) / rate * scale : x;

        if (_snapTimes.Count == 0 || keys.HasFlag(ModifierKeys.Alt))
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

    // ----- Track height -----

    /// <summary>Full-size video/audio lane whose lower edge is within <see cref="HeightEdgeReach"/> of <paramref name="laidY"/>; -1 for none.</summary>
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

    /// <summary>Count of Track-Height-scaled rows from the top through lane <paramref name="index"/> (inclusive).</summary>
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

    // ----- Wheel and middle button -----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Always handled here, including with the left button held and during drags.
        e.Handled = true;
        var at = e.GetPosition(this);
        WheelAt(e.Delta, Keyboard.Modifiers, MomentumScroller.Now(), Math.Max(at.X - _headerWidth, 0), overTime: at.X >= _headerWidth, at.Y + _verticalOffset);
    }

    // WheelGlide for plain-wheel pan over clips and Ctrl+wheel zoom (created on first use).
    private WheelGlide? _wheelGlide;

    private WheelGlide Glide => _wheelGlide ??= new(pixels => PanRequested?.Invoke(pixels), (factor, at) => ZoomRequested?.Invoke(factor, at), () => IsVisible);

    /// <summary>Fraction of the view width panned per plain-wheel notch over clips.</summary>
    internal const double WheelPanShare = 0.10;

    /// <summary>
    /// Test seam (called by <see cref="OnMouseWheel"/>): wheel <paramref name="delta"/> (120/notch, up positive) at <paramref name="nowMs"/>.
    /// Over clips (<paramref name="overTime"/>) plain wheel glides a pan of 1/10 view per notch (down = later); over headers or with Alt it
    /// scrolls rows with momentum; Alt over a track that sizes alone (layout <paramref name="laidY"/>) steps its height; Alt+Shift steps
    /// every track's height; Ctrl glides a zoom about <paramref name="zoomAt"/> (pixels from the time area's left) with the pointer time
    /// pinned; Shift pans with momentum (fast streams coast, an opposing tick stops). During drags each notch applies exactly and
    /// immediately, without coasting.
    /// </summary>
    internal void WheelAt(int delta, ModifierKeys modifiers, double nowMs, double zoomAt = 0, bool overTime = false, double laidY = double.NaN)
    {
        // Alt+wheel over a sizable track: that track taller (up) or shorter (elsewhere Alt alone scrolls rows).
        if (modifiers.HasFlag(ModifierKeys.Alt) && StepRowHeight(delta, modifiers, laidY))
            return;

        // Alt+Shift+wheel: all tracks taller (up) or shorter.
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
            // Plain wheel over clips: glide-pan 1/10 view per notch (stops any row or view coast).
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

                // Already at top/bottom: do not start a coast into the limit.
                if (step != 0 && _verticalOffset == before)
                    MomentumY.Stop();
                KeepTicking();
            }
        }

        FollowHeldDrag();
    }

    /// <summary>
    /// Test seam: horizontal pan by <paramref name="pixels"/> (positive = later) at <paramref name="nowMs"/> from Shift+wheel (negated) or
    /// touchpad/tilt horizontal wheel (MainWindow hook), with momentum; during drags applied exactly, no coast.
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

        // View at a limit or fitted (cannot pan): no coast.
        if (step != 0 && ViewOffsetX == before)
            MomentumX.Stop();
        KeepTicking();
    }

    /// <summary>Test seam: middle press at <paramref name="x"/> (pixels from the control's left) stops coasts and starts a pan.</summary>
    internal void MiddlePanPress(double x, double nowMs)
    {
        _panFrom = x;
        _panTravel = 0;
        MomentumX.Press(nowMs);
        MomentumX.Sample(0, nowMs);
    }

    /// <summary>Test seam: middle-drag to <paramref name="x"/> pans 1:1 with the pointer.</summary>
    internal void MiddlePanMove(double x, double nowMs)
    {
        if (_panFrom is not { } from)
            return;

        _panFrom = x;
        _panTravel += from - x;
        MomentumX.Sample(_panTravel, nowMs);
        PanRequested?.Invoke(from - x);
    }

    /// <summary>Test seam: middle release; a flick (>= 250 DIP/s, moving within the last 40 ms) starts a coast.</summary>
    internal void MiddlePanRelease(double nowMs)
    {
        _panFrom = null;
        if (MomentumX.Release(nowMs) != 0)
            KeepTicking();
    }

    // Coast started: driven by the control's frame loop while visible (tests step it directly).
    private void KeepTicking()
    {
        if (((_momentumX?.IsCoasting ?? false) || (_momentumY?.IsCoasting ?? false)) && IsVisible)
            StartTicking();
    }

    // Any press (any button, touch or pen) stops coasts first, before other handling.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        StopMomentum();
        _wheelGlide?.Stop();
    }

    /// <summary>View moved under a stationary drag: re-place the dragged item for the pointer's new layout position.</summary>
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
        else if (_drag is { } drag && !_dragParked)
        {
            // A block parked over an external target stays at its origin regardless of view movement. Uses current modifiers, as a pointer
            // move would.
            _lastPointer = laid;
            drag.Keys = Keyboard.Modifiers;
            Follow(drag, laid, pointerMoved: false, Now());
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
            // Release capture first (capture loss stops coasts), then start the flick coast if any.
            var now = MomentumScroller.Now();
            _panFrom = null;
            ReleaseMouseCapture();
            MiddlePanRelease(now);
            e.Handled = true;
        }
    }
}
