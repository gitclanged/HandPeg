using System.Windows;

namespace HandPegApp;

// ----- Drag-out of blocks (e.g. onto the Personal Clip Library) -----
// While a clip move is outside the control, each move asks the host whether the point accepts a drop (the Media pane's Clip Library).
// While accepted, the block is parked at its origin with no target row or new track and no auto-scroll; releasing there hands it to
// the host with nothing committed (no edit, no Undo step). Back inside, or over a non-accepting target, the drag behaves normally.
// Trims, rolls, slips and seam-edge drags never query the host.
public sealed partial class UnifiedTimelineControl
{
    /// <summary>
    /// Host query on every move of a block move while the pointer is outside the control (screen point in physical pixels), and once with
    /// null when it re-enters or the drag ends. Return true to offer a drop there; the block stays parked at its origin while offered.
    /// </summary>
    public Func<TimelineRow, TimelineBlock, Point?, bool>? DragOutside { get; set; }

    /// <summary>Host drop handler for a release outside at an offered point; return true when taken (the control commits nothing).</summary>
    public Func<TimelineRow, TimelineBlock, Point, bool>? DropOutside { get; set; }

    // Drag currently outside the control (host was queried; it gets null on re-entry or end), last pointer position in control
    // coordinates, and whether the host offered a drop (block parked).
    private (TimelineRow Row, TimelineBlock Block)? _outside;
    private Point _outsideAt;
    private bool _dragParked;

    /// <summary>True while the dragged block is parked at its origin over an external drop target.</summary>
    internal bool IsDragParked => _dragParked;

    /// <summary>
    /// Advances a block drag to <paramref name="point"/> (control coordinates; <paramref name="laid"/> in row space) at <paramref name="nowMs"/>
    /// (drag clock): the block follows normally unless outside over an offered drop, which parks it. Returns false when parked (no
    /// auto-scroll). Every move also feeds the pointer-velocity sampler (Settle) in control coordinates.
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

    // Parks the block (and its family) at its origin with no target row or new track, and stops auto-scroll.
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

    // Pointer re-entered or the drag ended: notify the host with null and resume following the pointer.
    private void LeaveOutside()
    {
        _dragParked = false;
        if (_outside is not { } was)
            return;

        _outside = null;
        Raise(() => DragOutside?.Invoke(was.Row, was.Block, null));
    }

    /// <summary>
    /// Offers a parked block released at <paramref name="point"/> (control coordinates) to the host. If taken, the drag is restored and
    /// ended like an abort (DragEnded raised, capture released) with nothing committed, and true is returned.
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

    // Control point to screen physical pixels; returns the point unchanged when not in a window (tests).
    private Point ScreenOf(Point point) => PresentationSource.FromVisual(this) is null ? point : PointToScreen(point);
}
