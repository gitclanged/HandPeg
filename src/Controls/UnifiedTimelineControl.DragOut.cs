using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

// ----- Cut seams, menu regions, drag-over crossfades, trim readouts and Ctrl+drag clones (Pass 5.7.1 L1) -----
// A cut seam (two blocks meeting on a track) is a target of its own: a click on it (the roll grip, released in place) selects it for Ctrl+T
// (default crossfade), Delete (heal a through-edit) and comma/period (nudge a frame), handled by the host (MainWindow.TimelineView.cs).
// Right-clicks name the region hit (fade wedge or handle, seam, gain line or waveform, body) in TimelineMenuRequest. A move dragged over
// its neighbour on the same track shows the X-curve crossfade the document makes on release (abut + TransitionIn); trims show the live
// delta and length beside the Ghost Trim Tails; Ctrl+drag on a body clones the clip where it is dropped (one Undo step, the original stays).
public sealed partial class UnifiedTimelineControl
{
    // ----- Cut seams -----

    /// <summary>Host query: two blocks meeting at a cut are one take cut in two (Delete heals them). Null: never.</summary>
    public Func<TimelineBlock, TimelineBlock, bool>? ThroughEdit { get; set; }

    /// <summary>The selected cut seam; null for none. Any other press clears it; a row rebuild finds it again by its two clips.</summary>
    public TimelineSeamTarget? SelectedSeam { get; private set; }

    /// <summary>The selected seam changed (null: cleared).</summary>
    public event Action<TimelineSeamTarget?>? SeamSelected;

    /// <summary>Ctrl+drag released: duplicate the block's clip at these Sequence seconds (on the row's track; another row's data when over one).</summary>
    public event Action<TimelineRow, TimelineBlock, object?, double>? CloneRequested;

    /// <summary>Selects a cut seam (null clears it) and redraws its mark.</summary>
    public void SelectSeam(TimelineSeamTarget? seam)
    {
        if (seam is null && SelectedSeam is null)
            return;

        SelectedSeam = seam;
        _overlayDirty = true;
        InvalidateVisual();
        Raise(() => SeamSelected?.Invoke(seam));
    }

    /// <summary>
    /// The cut seam at a layout point on a full-size lane: a block and the one ending where it starts (same band, within a pixel), the point
    /// within the edge reach of the cut or inside the crossfade across it. Null for none.
    /// </summary>
    private TimelineSeamTarget? SeamTargetAt(in Lane lane, Point laid)
    {
        var scale = PixelsPerSecond;
        if (!lane.Detailed || scale <= 0)
            return null;

        foreach (var right in lane.Row.Blocks)
        {
            if (right.Clip is null || NextTo(lane.Row, right, after: false) is not { Clip: not null } left)
                continue;

            var rect = BlockRect(lane, right, 0);
            var reach = Math.Max(TimelineMetrics.EdgePixels, right.CrossfadeIn * scale / 2);
            if (laid.Y >= rect.Top - 1 && laid.Y <= rect.Bottom + 1 && Math.Abs(laid.X - right.Left) <= reach)
                return SeamOf(lane.Row, left, right);
        }

        return null;
    }

    /// <summary>The seam where <paramref name="left"/> meets <paramref name="right"/> on <paramref name="row"/>, asking the host whether it is a through-edit.</summary>
    private TimelineSeamTarget SeamOf(TimelineRow row, TimelineBlock left, TimelineBlock right)
    {
        var scale = PixelsPerSecond;
        var through = false;
        if (ThroughEdit is { } query && right.CrossfadeIn <= 0.01)
            Raise(() => through = query(left, right));
        return new TimelineSeamTarget(row, left, right, scale > 0 ? right.Left / scale : 0) { CrossfadeSeconds = right.CrossfadeIn, IsThroughEdit = through };
    }

    /// <summary>
    /// A block drag released in place on a cut (a roll, or a trim grip where a neighbour meets the edge): the seam it selects; null when the
    /// press was not on a cut.
    /// </summary>
    private TimelineSeamTarget? SeamClickOf(BlockDrag drag)
    {
        var edge = TimelineMetrics.EdgePixels;
        return drag switch
        {
            { Kind: BlockEdit.Roll, Neighbor: { } after } => SeamOf(drag.Row, drag.Block, after),
            { Kind: BlockEdit.TrimEnd, Seam: null } when Math.Abs(drag.PointerX - (drag.Left + drag.Width)) <= edge && NextTo(drag.Row, drag.Block, after: true) is { Clip: not null } after
                => SeamOf(drag.Row, drag.Block, after),
            { Kind: BlockEdit.TrimStart, Seam: null } when Math.Abs(drag.PointerX - drag.Left) <= edge && NextTo(drag.Row, drag.Block, after: false) is { Clip: not null } before
                => SeamOf(drag.Row, before, drag.Block),
            _ => null,
        };
    }

    /// <summary>After a row rebuild: the selected seam on the rebuilt blocks (same two clips, still meeting), else cleared.</summary>
    private void FindSeamAgain()
    {
        if (SelectedSeam is not { } seam)
            return;

        if (Again(seam.Left, seam.Row) is { } left && Again(seam.Right, seam.Row) is { } right && ReferenceEquals(left.Row, right.Row)
            && ReferenceEquals(NextTo(right.Row, right.Block, after: false), left.Block))
        {
            SelectedSeam = SeamOf(right.Row, left.Block, right.Block);
            _overlayDirty = true;
            return;
        }

        SelectSeam(null);
    }

    /// <summary>Test seam: the cut seam at a control point (as a right-click or a click would find it); null for none.</summary>
    internal TimelineSeamTarget? SeamAtPoint(Point point)
    {
        var laid = Laid(point);
        return LaneAt(laid.Y) is >= 0 and var index ? SeamTargetAt(_lanes[index], laid) : null;
    }

    // ----- Menu regions -----

    /// <summary>
    /// The part of <paramref name="block"/> a right-click at <paramref name="laid"/> asks about: a fade handle, then a cut seam, then the
    /// shaded fade wedge, then the gain line (or, on an audio track, the waveform's middle band), else the body. Collapsed lanes: the body.
    /// </summary>
    private (ClipMenuRegion Region, TimelineSeamTarget? Seam) RegionAt(int index, TimelineBlock block, Point laid)
    {
        var lane = _lanes[index];
        if (!lane.Detailed)
            return (ClipMenuRegion.Body, null);
        if (FadeHandleAt(lane, block, laid) is { } handle)
            return (handle ? ClipMenuRegion.FadeIn : ClipMenuRegion.FadeOut, null);
        if (SeamTargetAt(lane, laid) is { } seam)
            return (ClipMenuRegion.Seam, seam);
        if (FadeWedgeAt(lane, block, laid) is { } wedge)
            return (wedge ? ClipMenuRegion.FadeIn : ClipMenuRegion.FadeOut, null);
        if (OnGainLine(lane, block, laid) || OnWave(lane, block, laid))
            return (ClipMenuRegion.Gain, null);
        return (ClipMenuRegion.Body, null);
    }

    /// <summary>Inside a fade's shaded wedge (above its line): true the fade in, false the fade out, null neither.</summary>
    private bool? FadeWedgeAt(in Lane lane, TimelineBlock block, Point laid)
    {
        var scale = PixelsPerSecond;
        if (block.Fades is not { } fades || scale <= 0)
            return null;

        var rect = BlockRect(lane, block, 0);
        if (!rect.Contains(laid))
            return null;

        var (fadeIn, fadeOut) = FadeSpan(rect, fades.In, fades.Out, scale);
        var depth = (laid.Y - rect.Top) / Math.Max(rect.Height, 1);
        if (fadeIn > 1 && laid.X <= rect.Left + fadeIn && depth <= 1 - (laid.X - rect.Left) / fadeIn)
            return true;
        if (fadeOut > 1 && laid.X >= rect.Right - fadeOut && depth <= 1 - (rect.Right - laid.X) / fadeOut)
            return false;
        return null;
    }

    /// <summary>On an audio track's waveform: the middle band (half the height) of an audible block, clear of its trim edges.</summary>
    private static bool OnWave(in Lane lane, TimelineBlock block, Point laid)
    {
        if (lane.Row.Kind != TimelineRowKind.Audio || block.Gain is null || block.GainEdit is null)
            return false;

        var rect = BlockRect(lane, block, 0);
        return Math.Abs(laid.Y - (rect.Top + rect.Height / 2)) <= rect.Height / 4
            && laid.X > rect.Left + TimelineMetrics.EdgePixels && laid.X < rect.Right - TimelineMetrics.EdgePixels;
    }

    // The pending right-click's region, seam and Sequence seconds (set with ArmMenu, cleared by DisarmMenu).
    private ClipMenuRegion _menuRegion;
    private TimelineSeamTarget? _menuSeam;
    private double _menuSeconds = double.NaN;

    /// <summary>The menu request for the pending right-click (region, seam and pointer time included).</summary>
    private TimelineMenuRequest MenuRequestOf(TimelineRow? row, TimelineBlock? block, bool onHeader, bool? fadeIn, ContextMenu menu) =>
        new(row, block, onHeader, fadeIn, menu) { Region = _menuRegion, Seam = _menuSeam, Seconds = _menuSeconds };

    /// <summary>Test seam: the region and seam a right-click at a control point would ask about (Body with no block); null over no lane.</summary>
    internal (ClipMenuRegion Region, TimelineSeamTarget? Seam)? RegionAtPoint(Point point)
    {
        var laid = Laid(point);
        if (point.X < _headerWidth || LaneAt(laid.Y) is not (>= 0 and var index))
            return null;
        return BlockAt(index, laid) is { } block ? RegionAt(index, block, laid) : (ClipMenuRegion.Body, null);
    }

    // ----- Drag-over crossfade -----

    /// <summary>
    /// The crossfade a move would make on release: its own row's block it overlaps by a frame to TimelineDocument.LongestDragCrossfade
    /// (and half of either clip), with the overlap in seconds and layout pixels; null for none (a deeper overlap is refused on release).
    /// </summary>
    private (TimelineBlock Other, double Seconds, double From, double To)? DragOverlap(BlockDrag drag)
    {
        var scale = PixelsPerSecond;
        if (drag is not { Kind: BlockEdit.Move, Target: null, NewTrack: null, Attach: null, Clone: false, TimeLocked: false } || drag.Group is { Count: > 0 } || scale <= 0)
            return null;

        var block = drag.Block;
        var (left, right) = (block.Left, block.Left + block.Width);
        var frame = 1 / Math.Max(FrameRate?.Invoke() ?? 30, 1);
        foreach (var other in drag.Row.Blocks)
        {
            if (ReferenceEquals(other, block) || IsDragged(other) || Math.Abs(other.Top - block.Top) > 0.5)
                continue;

            var (from, to) = (Math.Max(left, other.Left), Math.Min(right, other.Left + other.Width));
            if (to - from <= 0.5)
                continue;

            var seconds = (to - from) / scale;
            var most = Math.Min(Models.Timeline.TimelineDocument.LongestDragCrossfade, Math.Min(block.Width, other.Width) / scale / 2);
            return seconds >= frame - 1e-6 && seconds <= most + 1e-3 ? (other, seconds, from, to) : null;
        }

        return null;
    }

    /// <summary>Test seam: the live drag-over crossfade (the overlapped clip and its length in seconds); null for none.</summary>
    internal (object? Clip, double Seconds)? LiveCrossfade => _drag is { } drag && DragOverlap(drag) is { } overlap ? (overlap.Other.Clip, overlap.Seconds) : null;

    // ----- Trim readout -----

    /// <summary>
    /// The live readout of a trim or roll: the edge's travel and the clip's length now ("+0.42 s · 3.20 s"); null for none. Seam-edge drags
    /// have their own readout.
    /// </summary>
    private string? TrimWords(BlockDrag drag)
    {
        var scale = PixelsPerSecond;
        if (drag is not { Kind: BlockEdit.TrimStart or BlockEdit.TrimEnd or BlockEdit.Roll, Seam: null } || scale <= 0)
            return null;

        var block = drag.Block;
        var delta = drag.Kind == BlockEdit.TrimStart ? (drag.Left - block.Left) / scale : (block.Left + block.Width - (drag.Left + drag.Width)) / scale;
        return TrimText(delta, block.Width / scale);
    }

    /// <summary>A trim readout: signed change and length in seconds (pure; goldens).</summary>
    internal static string TrimText(double delta, double length) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(Math.Abs(delta) < 0.005 ? 0 : delta):+0.00;-0.00;0.00} s · {length:0.00} s");

    /// <summary>Test seam: the live trim readout; null for none.</summary>
    internal string? TrimReadout => _drag is { } drag ? TrimWords(drag) : null;

    // ----- Overlay drawing -----

    private static readonly Pen SeamMarkPen = Ice(new Pen(Brushes.White, 2) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat });
    private static readonly Pen CrossCurvePen = Ice(new Pen(Tint(235, 0xFF, 0xFF, 0xFF), 1.4));
    private static readonly Brush CrossShade = Tint(70, 0x00, 0x00, 0x00);

    /// <summary>Selected seam mark, the drag-over crossfade, the trim readout and the clone's original (RenderOverlay; x = 0 at _originX).</summary>
    private void DrawEditAids(DrawingContext drawing, double scale, double from, double to, DpiScale dpi)
    {
        if (scale <= 0)
            return;

        if (SelectedSeam is { } seam && !IsDragging && LaneOf(seam.Row) is >= 0 and var seamLane && _lanes[seamLane].Detailed)
            DrawSeamMark(drawing, _lanes[seamLane], seam.Right.Left - _originX, from, to);

        if (_drag is not { } drag || LaneOf(drag.Row) is not (>= 0 and var index))
            return;

        var lane = _lanes[index];
        var rect = BlockRect(lane, drag.Block, _originX);
        if (DragOverlap(drag) is { } overlap)
        {
            var box = new Rect(overlap.From - _originX, rect.Top, Math.Max(overlap.To - overlap.From, 1), rect.Height);
            DrawCrossCurve(drawing, box);
            var words = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Crossfade {overlap.Seconds:0.00} s");
            DrawReadout(drawing, words, (box.Left + box.Right) / 2, rect.Top + 1, toRight: true, from, to, dpi.PixelsPerDip);
        }

        if (TrimWords(drag) is { } trim)
        {
            var atStart = drag.Kind == BlockEdit.TrimStart;
            DrawReadout(drawing, trim, atStart ? rect.Left : rect.Right, rect.Bottom - 18, toRight: !atStart, from, to, dpi.PixelsPerDip);
        }

        if (drag is { Clone: true } && Math.Abs(drag.Block.Left - drag.Left) > 0.5)
        {
            // The original stays: drawn faint where it was, with a "+" beside the copy.
            var origin = new Rect(drag.Left - _originX, rect.Top, Math.Max(drag.Width, 3), rect.Height);
            drawing.PushOpacity(0.55);
            drawing.DrawRoundedRectangle(drag.Block.Fill, null, origin, 3, 3);
            drawing.Pop();
            DrawReadout(drawing, "+ Duplicate", rect.Left, rect.Top + 1, toRight: true, from, to, dpi.PixelsPerDip);
        }
    }

    // Selected seam: a white bar across the lane at the cut with bracket feet.
    private static void DrawSeamMark(DrawingContext drawing, in Lane lane, double x, double from, double to)
    {
        if (x < from - 6 || x > to + 6)
            return;

        var (top, bottom) = (lane.Top + 1, lane.Bottom - 1);
        drawing.DrawLine(SeamMarkPen, new Point(x, top), new Point(x, bottom));
        drawing.DrawLine(SeamMarkPen, new Point(x - 4, top + 1), new Point(x + 4, top + 1));
        drawing.DrawLine(SeamMarkPen, new Point(x - 4, bottom - 1), new Point(x + 4, bottom - 1));
    }

    // X-curve over an overlap: the outgoing gain falling and the incoming rising (equal power, the default curve), over a light shade.
    private static void DrawCrossCurve(DrawingContext drawing, Rect box)
    {
        drawing.DrawRectangle(CrossShade, null, box);
        const int Steps = 16;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var curve = 0; curve < 2; curve++)
            {
                for (var i = 0; i <= Steps; i++)
                {
                    var (gainOut, gainIn) = Models.CrossfadeCurves.Gains(Models.CrossfadeCurve.EqualPower, i / (double)Steps);
                    var point = new Point(box.Left + box.Width * i / Steps, box.Bottom - (curve == 0 ? gainOut : gainIn) * box.Height);
                    if (i == 0)
                        context.BeginFigure(point, isFilled: false, isClosed: false);
                    else
                        context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();
        drawing.DrawGeometry(null, CrossCurvePen, geometry);
    }
}
