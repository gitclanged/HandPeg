using System.Windows;

namespace HandPegApp;

// ----- A block dragged out of the control (5.4.4: onto the Personal Clip Library) -----
// A clip's move that leaves the control asks the host, every move, whether the place under the pointer takes it (the Media pane's
// Clip Library). While the host says yes the block is drawn back where it began, the drag goes onto no row and no new track, and
// the view does not scroll by itself; let go there, the host takes it and nothing is committed (no edit, no step of Undo). Back
// inside, or over a place that takes nothing, the drag is the same as before. A trim, a roll, a slip or a seam's edge never asks.
public sealed partial class UnifiedTimelineControl
{
    /// <summary>
    /// Each move of a block's move while the pointer is outside the control (the point on the screen, in its real pixels), and once
    /// with null when it comes back inside or the drag ends (5.4.4). True: the host offers a drop there, and the block is drawn back
    /// where it began while it does.
    /// </summary>
    public Func<TimelineRow, TimelineBlock, Point?, bool>? DragOutside { get; set; }

    /// <summary>A block's move let go outside the control where the host offered a drop (5.4.4): true when it takes the block; nothing is committed then.</summary>
    public Func<TimelineRow, TimelineBlock, Point, bool>? DropOutside { get; set; }

    // The drag whose pointer is outside the control (the host has been asked about it, and is told null when it comes back or the drag
    // ends), where the pointer last was (the control's own coordinates), and whether the host offered a drop there (the block parked).
    private (TimelineRow Row, TimelineBlock Block)? _outside;
    private Point _outsideAt;
    private bool _dragParked;

    /// <summary>Whether the block of the drag under way is held back where it began over a place outside that takes it (5.4.4).</summary>
    internal bool IsDragParked => _dragParked;

    /// <summary>
    /// The pointer of a block's drag at <paramref name="point"/> on the control (<paramref name="laid"/> on the rows) at <paramref name="nowMs"/>
    /// (the drags' clock): the block follows it as before, unless it is outside and the host offers a drop there (5.4.4), which parks it.
    /// False when parked (no auto-scroll then). 5.5A: every move is a sample of the pointer's speed, in the control's own coordinates.
    /// </summary>
    private bool FollowPointer(BlockDrag drag, Point point, Point laid, double nowMs)
    {
        Settle.Sample(point, nowMs);
        if (DragOutside is { } outside && drag is { Kind: BlockEdit.Move, Seam: null } && !new Rect(RenderSize).Contains(point))
        {
            var (row, block, screen, offered) = (drag.Row, drag.Block, ScreenOf(point), false);
            (_outside, _outsideAt) = ((row, block), point);
            Raise(() => offered = outside(row, block, screen));
            if (offered)
            {
                Park(drag);
                return false;
            }

            _dragParked = false;
        }
        else if (_outside is not null)
        {
            LeaveOutside();
        }

        Follow(drag, laid, pointerMoved: true, nowMs);
        return true;
    }

    // The block (and what it carries along) back where it began, onto no row and no new track, the view still.
    private void Park(BlockDrag drag)
    {
        _autoScroll.Stop();
        if (_dragParked)
            return;

        _dragParked = true;
        PutBack(drag);
        (drag.Target, drag.NewTrack) = (null, null);
        _overlayDirty = true;
        InvalidateVisual();
    }

    // The pointer is back inside, or the drag ended: the host is told (null), and the block follows the pointer again.
    private void LeaveOutside()
    {
        _dragParked = false;
        if (_outside is not { } was)
            return;

        _outside = null;
        Raise(() => DragOutside?.Invoke(was.Row, was.Block, null));
    }

    /// <summary>
    /// A parked block let go at <paramref name="point"/> (the control's own coordinates): the host is asked to take it. Taken, the drag
    /// is put back and ended as an abort is (DragEnded told, the mouse let go) and nothing is committed; true then.
    /// </summary>
    private bool TryDropOutside(BlockDrag drag, Point point)
    {
        if (!_dragParked || DropOutside is not { } drop)
            return false;

        var (row, block, screen, taken) = (drag.Row, drag.Block, ScreenOf(point), false);
        Raise(() => taken = drop(row, block, screen));
        if (!taken)
            return false;

        AbortDrag();
        return true;
    }

    // A point on the control on the screen, in its real pixels; the point itself while the control is in no window (a test).
    private Point ScreenOf(Point point) => PresentationSource.FromVisual(this) is null ? point : PointToScreen(point);
}
