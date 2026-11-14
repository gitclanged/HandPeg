using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

// The unified timeline's drawing: the rows' content (wells, blocks, frames, waveforms, fades, gain lines, keys, pills,
// seams, the end shade), the blocks' words, the headers, the overlay (the block being dragged, hover handles, the drop
// mark), and the two lines that slide (the playhead and the skim line). Everything made in code is frozen; what a block
// is drawn with is made once and kept while the block keeps its place (its outline, keys, fades, words), and the
// waveforms are frozen tiles kept per zoom step (WaveTiles), so drawing the rows again allocates next to nothing.
public sealed partial class UnifiedTimelineControl
{
    // Where the drawing in laid-out pixels starts, in steps of this (so that what is drawn again in the same place is made
    // of the same numbers, and the tiles' placements are found again rather than made).
    private const double OriginStep = 1024;

    // How far down a block a J/L cut's sound band starts, as a fraction of its height (every other sound is drawn across the block).
    private const double SoundBandTop = 0.6;

    // How see-through a waveform drawn across a block's pictures is (5.3-Stage2): its frames show through it.
    private const double WaveOverPictures = 0.5;

    // The width of a switch on a header, and the order the switches are laid in.
    private const double SwitchSize = 18;
    private static readonly RowSwitches[] SwitchOrder = [RowSwitches.Hidden, RowSwitches.Muted, RowSwitches.Soloed, RowSwitches.Voice, RowSwitches.Locked];
    private const RowSwitches AllSwitches = RowSwitches.Hidden | RowSwitches.Muted | RowSwitches.Soloed | RowSwitches.Voice | RowSwitches.Locked;

    // Segoe MDL2 Assets: ChevronRight (E76C), ChevronDown (E70D), View (E890), Hide (ED1A), Volume (E767), Mute (E74F),
    // Microphone (E720), Lock (E72E), Unlock (E785); Switch (E8AB) for the Hide pill's two arrows.
    private const string ChevronRight = "", ChevronDown = "";
    private const string SwitchGlyphText = "";

    // How far a header's second chevron (its sound's stack, beside its layers') is from its first.
    private const double ChevronStep = 16;

    private static readonly Typeface LabelFace = new("Segoe UI");
    private static readonly Typeface StrongFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface IconFace = new("Segoe MDL2 Assets");

    private static readonly Pen KeyOutline = Frozen(new Pen(Brushes.Black, 0.7));
    private static readonly Pen GainPen = Frozen(new Pen(Solid(230, 0xFF, 0xD5, 0x4F), 1.2));
    private static readonly Brush HandleFill = Solid(255, 0xFF, 0xD5, 0x4F);
    private static readonly Brush FadeShadeFill = Solid(120, 0, 0, 0);
    private static readonly Pen FadeCurve = Frozen(new Pen(Solid(235, 255, 255, 255), 1.3));
    private static readonly Brush FadeHandleFill = Solid(255, 0xF2, 0xF2, 0xF2);
    private static readonly Pen FadeHandleOutline = Frozen(new Pen(Solid(200, 0, 0, 0), 1));
    private static readonly Brush ReadoutFill = Solid(230, 24, 24, 24);
    private static readonly Brush CrossfadeFill = Solid(84, 0x4F, 0xC3, 0xF7);
    private static readonly Brush CrossfadeButton = Solid(210, 0x1B, 0x4F, 0x72);
    private static readonly Pen CrossfadeLine = Frozen(new Pen(Solid(235, 255, 255, 255), 1.2));
    private static readonly Pen SelectedOutline = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Brush LabelShade = Solid(150, 0, 0, 0);
    private static readonly Brush BadgeFill = Solid(200, 0, 0, 0);
    private static readonly Brush SoundShade = Solid(110, 0, 0, 0);
    private static readonly Brush DropMarkBrush = Solid(255, 0x4F, 0xC3, 0xF7);
    private static readonly Brush GhostFill = Solid(70, 0x4F, 0xC3, 0xF7);
    private static readonly Pen GhostOutline = Frozen(new Pen(DropMarkBrush, 1.5));
    private static readonly Brush RefusedFill = Solid(60, 0xE5, 0x39, 0x35);
    private static readonly Pen RefusedOutline = Frozen(new Pen(Solid(255, 0xE5, 0x39, 0x35), 1.5));
    private static readonly Brush GapFill = Solid(60, 255, 255, 255);
    private static readonly Pen GapOutline = Frozen(new Pen(Solid(110, 255, 255, 255), 1));
    private static readonly Pen GapArrow = Frozen(new Pen(Solid(220, 255, 255, 255), 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Brush SeamFill = Solid(235, 0xFF, 0xB7, 0x4D);
    private static readonly Pen SeamLine = Frozen(new Pen(Solid(200, 0xFF, 0xB7, 0x4D), 1) { DashStyle = Frozen(new DashStyle([3, 2], 0)) });
    private static readonly Pen SeamZigzag = Frozen(new Pen(Solid(230, 40, 26, 0), 1));
    private static readonly Brush RevealTint = Solid(30, 0xFF, 0xB7, 0x4D);
    private static readonly Brush RevealGhost = Solid(60, 0xFF, 0xB7, 0x4D);
    private static readonly Pen RevealEdge = Frozen(new Pen(Solid(120, 0xFF, 0xB7, 0x4D), 1) { DashStyle = Frozen(new DashStyle([2, 3], 0)) });
    private static readonly Brush RevealPillFill = Solid(235, 0xFF, 0xB7, 0x4D);
    private static readonly Brush RevealPillText = Solid(255, 40, 26, 0);
    private static readonly Pen DetachPen = Frozen(new Pen(Solid(230, 255, 255, 255), 1.2) { DashStyle = Frozen(new DashStyle([3, 2], 0)) });
    private static readonly Brush SoundChevron = Solid(255, 0x4C, 0xB8, 0x74);
    private static readonly Brush EmptyRowFill = Solid(18, 255, 255, 255);
    private static readonly Pen EmptyRowOutline = Frozen(new Pen(Solid(70, 255, 255, 255), 1) { DashStyle = Frozen(new DashStyle([4, 3], 0)) });
    private static readonly Brush SwitchOnFill = Solid(40, 255, 255, 255);
    private static readonly Brush PlayheadBrush = Brushes.OrangeRed;
    private static readonly Brush SkimBrush = Solid(130, 255, 255, 255);
    private static readonly Geometry PlayheadCap = FrozenTriangle();

    // What was drawn of the rows, in laid-out pixels, and where the drawing's own x = 0 is in them.
    private double _originX, _renderedFrom, _renderedTo = double.NegativeInfinity;

    // The heights the playhead's and the skim line's drawings were made for.
    private double _playheadHeight = -1, _skimHeight = -1;

    // Waveform tiles still to be made after a render that could not make them all: drawn at the next quiet moment.
    private bool _tilesPending;
    private Action? _drawPendingTiles;

    // The waveforms through a zoom (5.4.4): the scale the rows were last drawn at; whether it is moving (it changed, and less than
    // WaveSettleMs ago), so tiles of a step either side stand in for the ones not made; whether a stand-in (or nothing) was drawn while it
    // moved; and the timer that makes the crisp tiles once it rests (made at the first zoom), with the larger budget of that one render.
    private const int WaveSettleMs = 100;
    private double _waveScale;
    private bool _waveMoving, _waveOwed, _waveSettling;
    private DispatcherTimer? _waveSettle;

    // A step's finer tiles that make one tile, found (a stand-in, at most 2^StandInOctaves).
    private readonly StreamGeometry?[] _finer = new StreamGeometry?[1 << WaveTiles.StandInOctaves];

    // Where files dragged over the control would land (ShowDropMark).
    private sealed record DropMark(TimelineRow? Row, double Seconds, int Count, bool Stack);

    private DropMark? _dropMark;
    private FormattedText? _dropWords;
    private string? _dropWordsOf;

    // The words on a new track's insertion bar (5.4A), laid out once for the screen's DPI.
    private FormattedText? _newTrackWords;
    private const string NewTrackWords = "+ New Video Track";
    private FormattedText? _emptyHint;

    // Words laid out once: glyphs and short texts, by text, colour, size and face.
    private readonly Dictionary<(string Text, Brush Brush, double Size, bool Icon), FormattedText> _words = [];

    // The placements of the waveform tiles, frozen and found again while nothing moved (see OriginStep).
    private readonly Dictionary<(double, double, double, double), MatrixTransform> _placements = [];

    // The see-through versions of the colours blocks and sounds are drawn in, by colour and how see-through.
    private static readonly Dictionary<(Brush, int), Brush> TranslucentBrushes = [];

    protected override void OnRender(DrawingContext drawing)
    {
        // The panel behind everything: what shows between the lanes, and what makes the whole control answer the pointer.
        drawing.DrawRectangle(_panel, null, new Rect(RenderSize));

        var dpi = VisualTreeHelper.GetDpi(this);
        _tilesPending = false;
        if (_contentDirty)
            RenderContent(dpi);
        if (_labelsDirty)
            RenderLabels(dpi.PixelsPerDip);
        if (_headersDirty)
            RenderHeaders(dpi.PixelsPerDip);
        if (_overlayDirty)
            RenderOverlay(dpi);
        ApplyShift();
        PlaceLine(_playhead, _playheadSeconds, ref _playheadHeight, playhead: true);
        PlaceLine(_skim, _skimSeconds, ref _skimHeight, playhead: false);

        // Waveform tiles left to make: the next render makes them, once input and layout are done.
        if (_tilesPending)
        {
            _drawPendingTiles ??= () =>
            {
                _contentDirty = _overlayDirty = true;
                InvalidateVisual();
            };
            Dispatcher.BeginInvoke(DispatcherPriority.Background, _drawPendingTiles);
        }
    }

    // ----- The rows -----

    private void RenderContent(DpiScale dpi)
    {
        _contentDirty = false;
        var (width, scale, view) = (TimeAreaWidth, PixelsPerSecond, ViewOffsetX);

        // Half a view either side is drawn too: a pan that stays within it only slides what is drawn.
        var margin = Math.Max(width / 2, 256);
        (_renderedFrom, _renderedTo) = (view - margin, view + width + margin);
        var origin = Math.Floor(_renderedFrom / OriginStep) * OriginStep;
        if (origin != _originX)
        {
            // The overlay is drawn from the same origin: it is drawn again with the rows.
            _originX = origin;
            _overlayDirty = true;
        }

        // The scale changed since the rows were last drawn (both wheel glides, the zoom slider, a pinch, a splitter, a tab): moving, until
        // WaveSettleMs after the last change. The first render ever is not a move: it makes its tiles as always.
        if (_waveScale > 0 && scale != _waveScale)
            RestartWaveSettle();
        _waveScale = scale;
        _waveMoving = _waveSettle is { IsEnabled: true };
        _waveOwed = false;
        WaveTiles.BeginRender(_waveSettling && !_waveMoving ? WaveTiles.SettleBuilds : WaveTiles.BuildsPerRender);
        _waveSettling = false;

        using (var drawing = _content.RenderOpen())
        {
            // Under the last track: the same dark well, down to the toolbar, however few tracks there are.
            if (width > 0 && ActualHeight > _extent)
                drawing.DrawRectangle(_inset, null, new Rect(_renderedFrom - _originX, _extent, _renderedTo - _renderedFrom, ActualHeight - _extent));

            if (width > 0 && _lanes.Count > 0)
            {
                var (from, to) = (_renderedFrom - _originX, _renderedTo - _originX);

                // The lanes' wells, sunk by the theme's edge where it draws one (along their tops and feet; their ends are
                // drawn beside the headers, where they stay).
                foreach (var lane in _lanes)
                {
                    if (lane.Height < 0.5)
                        continue;

                    drawing.DrawRectangle(_inset, null, new Rect(from, lane.Top, to - from, lane.Height));
                    if (_edgeWidth > 0 && lane.Height > 2 * _edgeWidth)
                    {
                        drawing.DrawLine(_edgePen, new Point(from, lane.Top + _edgeWidth / 2), new Point(to, lane.Top + _edgeWidth / 2));
                        drawing.DrawLine(_lowerEdgePen, new Point(from, lane.Bottom - _edgeWidth / 2), new Point(to, lane.Bottom - _edgeWidth / 2));
                    }
                }

                // After the end of the sequence: the room a clip can be dropped or dragged into.
                if (double.IsFinite(_sequenceSeconds) && scale > 0)
                {
                    var end = _sequenceSeconds * scale - _originX;
                    if (end < to)
                    {
                        var shade = Math.Max(end, from);
                        drawing.DrawRectangle(TimelineMetrics.PastEndShade, null, new Rect(shade, 0, to - shade, _extent));
                        if (end >= from)
                            drawing.DrawLine(TimelineMetrics.EndLine, new Point(end + 0.5, 0), new Point(end + 0.5, _extent));
                    }
                }

                var shift = CurrentShift(scale);
                TranslateTransform? move = null;
                if (shift is { } moving)
                {
                    move = new TranslateTransform(moving.Dx, 0);
                    move.Freeze();
                }

                foreach (var lane in _lanes)
                    DrawLane(drawing, lane, from, to, dpi, shift is { } each && Applies(lane, each) ? each : (Shift?)null, move);
            }
        }
    }

    // ----- What slides: a fold put back or taken out, an edge dragged into a seam (4.4 BugHunt) -----

    // How what follows a moment is drawn while it slides, in laid-out pixels: the blocks (and seams, pills) starting at or
    // after At are drawn Dx along; for a fold put back, the blocks of the stretch that came back (starting from WipeFrom,
    // before At) are wiped in up to WipeTo; for one taken out, where it was is shaded from GhostFrom to GhostTo. On the rows
    // in Rows (by key; null: every row that is not locked).
    private readonly record struct Shift(double At, double Dx, double WipeFrom, double WipeTo, double GhostFrom, double GhostTo, HashSet<object>? Rows)
    {
        public bool Wipes => !double.IsNaN(WipeFrom);

        public bool Ghosts => !double.IsNaN(GhostFrom) && GhostTo - GhostFrom > 0.5;
    }

    /// <summary>What slides now: a fold sliding into place, or the later blocks of an edge dragged at a seam; null for nothing.</summary>
    private Shift? CurrentShift(double scale)
    {
        if (scale <= 0)
            return null;

        if (_slide is { } slide)
        {
            var (at, length) = (slide.At * scale, slide.Length * scale);
            var left = (1 - slide.Eased) * length;
            return slide.Opening
                ? new Shift(at + length - 0.5, -left, at, at + slide.Eased * length, double.NaN, double.NaN, slide.Rows)
                : new Shift(at - 0.5, left, double.NaN, double.NaN, at, at + left, slide.Rows);
        }

        if (_drag is { Seam: { } grip } && Math.Abs(grip.Seconds) > 1e-9)
            return new Shift(grip.Cut - 0.5, grip.Seconds * scale, double.NaN, double.NaN, double.NaN, double.NaN, grip.Rows);

        return null;
    }

    // Whether a lane slides with what is sliding: its row is among those named, or (none named) its track is not locked. The
    // blurred background never does (it lasts as long as the sequence), in a stack or not.
    private static bool Applies(in Lane lane, Shift shift) =>
        lane.Row.Kind != TimelineRowKind.Background
        && (shift.Rows is { } rows ? rows.Contains(lane.Row.Key) : (lane.Row.State & RowSwitches.Locked) == 0);

    // Where a block (or a pill, a stretch) starting at a laid-out x is while something slides: moved (Dx along), wiped in
    // (the stretch that came back), or where it is.
    private enum Slid { Still, Moved, Wiped }

    private static Slid SlidOf(Shift? shift, double left) =>
        shift is not { } s ? Slid.Still
        : left >= s.At ? Slid.Moved
        : s.Wipes && left >= s.WipeFrom - 0.5 ? Slid.Wiped
        : Slid.Still;

    private void DrawLane(DrawingContext drawing, in Lane lane, double from, double to, DpiScale dpi, Shift? shift, Transform? move)
    {
        // A stack hidden takes no height, and is nothing to draw.
        if (lane.Height < 0.5)
            return;

        var row = lane.Row;
        var scale = PixelsPerSecond;
        if (row.Kind == TimelineRowKind.Empty)
            drawing.DrawRectangle(EmptyRowFill, EmptyRowOutline, new Rect(from, lane.Top + 3, to - from, Math.Max(lane.Height - 6, 1)));

        // A fold being put back is wiped in, left to right, inside the stretch that came back.
        Geometry? wipe = null;
        if (shift is { Wipes: true } wiping)
        {
            var clip = new RectangleGeometry(new Rect(wiping.WipeFrom - _originX, lane.Top, Math.Max(wiping.WipeTo - wiping.WipeFrom, 0), lane.Height));
            clip.Freeze();
            wipe = clip;
        }

        var dx = shift?.Dx ?? 0;
        foreach (var block in row.Blocks)
        {
            if (IsDragged(block))
                continue;

            var rect = BlockRect(lane, block, _originX);
            var slid = SlidOf(shift, block.Left);
            if (slid == Slid.Moved && move is not null)
            {
                if (rect.Right + dx < from || rect.Left + dx > to)
                    continue;

                // Moved by one shared transform: what the block is drawn with (its outline, keys, fades) stays as it was made.
                drawing.PushTransform(move);
                DrawBlock(drawing, lane, block, rect, rect.Left, null, from - dx, to - dx, dpi);
                drawing.Pop();
                continue;
            }

            if (rect.Right < from || rect.Left > to)
                continue;

            if (slid == Slid.Wiped && wipe is not null)
            {
                drawing.PushClip(wipe);
                DrawBlock(drawing, lane, block, rect, rect.Left, null, from, to, dpi);
                drawing.Pop();
                continue;
            }

            DrawBlock(drawing, lane, block, rect, rect.Left, null, from, to, dpi);
        }

        // The one bar a closed sound stack is: every stream that is not muted, in one waveform. What a drag carries along is drawn on
        // the overlay with its bar, where it is now (Pass 5.5.1): these rows are drawn once as the drag begins and not again while it goes.
        if (lane.Summary && lane.Parent >= 0 && scale > 0)
        {
            foreach (var sibling in _lanes[lane.Parent].Row.Children)
            {
                if (!HeardInBar(sibling))
                    continue;

                foreach (var block in sibling.Blocks)
                {
                    if (_drag is null || !IsDragged(block))
                        DrawBarWave(drawing, lane, block, BlockRect(lane, block, _originX).Left, from, to, scale, dpi);
                }
            }
        }

        if (lane.Detailed && scale > 0)
        {
            // A crossfade across a cut: a box over the two blocks, as long as the crossfade and centred on the cut.
            foreach (var block in row.Blocks)
            {
                if (block.CrossfadeIn <= 0.01 || IsDragged(block))
                    continue;

                var slid = SlidOf(shift, block.Left);
                if (slid != Slid.Wiped)
                    DrawCrossfade(drawing, lane, block, scale, slid == Slid.Moved ? _originX - dx : _originX, dpi.PixelsPerDip);
            }
        }

        if (scale <= 0)
            return;

        // Folded dead air that was put back: shaded, edged, and on a lane in full its Hide pill, which folds it again.
        var reveals = row.Reveals;
        for (var i = 0; i < reveals.Count; i++)
        {
            var (left, right) = (reveals[i].From * scale, reveals[i].To * scale);
            var slid = SlidOf(shift, left);
            if (slid == Slid.Moved)
                (left, right) = (left + dx, right + dx);
            else if (slid == Slid.Wiped && shift is { } opening)
                right = Math.Min(right, opening.WipeTo);
            if (right - _originX < from || left - _originX > to)
                continue;
            DrawReveal(drawing, lane, left - _originX, right - _originX, dpi.PixelsPerDip, pill: slid != Slid.Wiped);
        }

        // Where a fold is being taken out: shaded, closing up.
        if (shift is { Ghosts: true } ghost && ghost.GhostTo - _originX >= from && ghost.GhostFrom - _originX <= to)
            drawing.DrawRectangle(RevealGhost, null, new Rect(ghost.GhostFrom - _originX, lane.Top + 1, ghost.GhostTo - ghost.GhostFrom, Math.Max(lane.Height - 2, 1)));

        var grip = _drag is { Seam: { } held } dragging && ReferenceEquals(dragging.Row, row) ? held : null;
        var seams = row.Seams;
        for (var i = 0; i < seams.Count; i++)
        {
            var x = seams[i].Seconds * scale;

            // The seam whose edge is dragged: at the earlier clip's end it moves with the edge (the fold's head shown again
            // or folded more), at the later clip's start it stays (its tail); gone once the fold is all shown again.
            if (grip is not null && Equals(seams[i].Fold, grip.Mark.Fold))
            {
                if (grip.Seconds >= grip.Mark.FoldedSeconds - 1e-6)
                    continue;
                x = grip.Cut + (grip.AtStart ? 0 : grip.Seconds * scale);
            }
            else if (SlidOf(shift, x) == Slid.Moved)
            {
                x += dx;
            }

            x -= _originX;
            if (x >= from - 8 && x <= to + 8)
                DrawSeam(drawing, lane, x);
        }
    }

    // Whether a row's sound is in its closed stack's one bar: a lane of the sound's stack, not muted.
    private static bool HeardInBar(TimelineRow row) => row.Stack == ChildStack.Audio && (row.State & RowSwitches.Muted) == 0;

    /// <summary>
    /// A block's sound in its closed stack's one bar (<paramref name="bar"/>, the Summary lane), cut to where the block is and laid from
    /// <paramref name="soundLeft"/> (where its file's stretch begins: its left edge, or where it was while a trim moves its start).
    /// </summary>
    private void DrawBarWave(DrawingContext drawing, in Lane bar, TimelineBlock block, double soundLeft, double from, double to, double scale, DpiScale dpi)
    {
        var rect = BlockRect(bar, block, _originX);
        if (rect.Right < from || rect.Left > to || (block.SoundWindow ?? WindowOf(block, rect.Width, scale)) is not { } sound)
            return;
        DrawWave(drawing, sound, soundLeft, new Rect(rect.Left, rect.Top + 1, rect.Width, Math.Max(rect.Height - 2, 1)), from, to, scale, 0.7, dpi.DpiScaleX);
    }

    /// <summary>
    /// The closed stack's one bar a row's sound is drawn in (Pass 5.5.1): the Summary lane under the row's parent, while the row is a lane
    /// of its sound stack that is heard; -1 for none (the stack open, the lane muted, a row of no sound stack).
    /// </summary>
    private int BarLaneOf(TimelineRow row)
    {
        if (!HeardInBar(row) || row.Parent is not { } parent)
            return -1;

        for (var i = 0; i < _lanes.Count; i++)
        {
            if (_lanes[i] is { Summary: true, Parent: >= 0 } lane && ReferenceEquals(_lanes[lane.Parent].Row, parent))
                return i;
        }

        return -1;
    }

    // A stretch of dead air that was put back: a faint shade over it, a dashed line at either end, and (on a lane in full,
    // wide enough) its Hide pill.
    private void DrawReveal(DrawingContext drawing, in Lane lane, double left, double right, double pixelsPerDip, bool pill)
    {
        if (right - left < 0.5)
            return;

        drawing.DrawRectangle(RevealTint, null, new Rect(left, lane.Top + 1, right - left, Math.Max(lane.Height - 2, 1)));
        drawing.DrawLine(RevealEdge, new Point(left + 0.5, lane.Top + 1), new Point(left + 0.5, lane.Bottom - 1));
        drawing.DrawLine(RevealEdge, new Point(right - 0.5, lane.Top + 1), new Point(right - 0.5, lane.Bottom - 1));
        if (!pill || RevealPill(lane, left, right, pixelsPerDip, out var words) is not { IsEmpty: false } box)
            return;

        drawing.DrawRoundedRectangle(RevealPillFill, null, box, box.Height / 2, box.Height / 2);
        var glyph = Word(SwitchGlyphText, RevealPillText, 9, icon: true, pixelsPerDip);
        var y = box.Top + (box.Height - glyph.Height) / 2;
        drawing.DrawText(glyph, new Point(box.Left + 4, y));
        if (words)
        {
            var hide = Word("Hide", RevealPillText, 9.5, icon: false, pixelsPerDip);
            drawing.DrawText(hide, new Point(box.Left + 4 + glyph.Width + 3, box.Top + (box.Height - hide.Height) / 2));
        }
    }

    /// <summary>
    /// Where a stretch's Hide pill is (from <paramref name="left"/> to <paramref name="right"/> along the lane, in the same
    /// x as they are given): centred on the stretch, under the band of the fade handles; the arrows and "Hide" where the
    /// stretch has room for both (<paramref name="words"/>), the arrows alone where it has room for them, none (Empty) below that.
    /// </summary>
    private Rect RevealPill(in Lane lane, double left, double right, double pixelsPerDip, out bool words)
    {
        words = false;
        if (!lane.Detailed || right - left < 14)
            return Rect.Empty;

        var glyph = Word(SwitchGlyphText, RevealPillText, 9, icon: true, pixelsPerDip);
        var hide = Word("Hide", RevealPillText, 9.5, icon: false, pixelsPerDip);
        var whole = 4 + glyph.Width + 3 + hide.Width + 5;
        var narrow = 4 + glyph.Width + 4;
        words = right - left >= whole + 6;
        var width = words ? whole : narrow;
        if (right - left < width + 2)
            return Rect.Empty;

        var height = Math.Min(14, lane.Height - 4);
        var top = Math.Max(Math.Min(lane.Top + FadeBand + 3, lane.Bottom - height - 2), lane.Top + 2);
        return new Rect((left + right - width) / 2, top, width, height);
    }

    /// <summary>
    /// Where a block is drawn, with its drawing's x = 0 at <paramref name="originX"/> of the laid-out rows: filling its lane
    /// when it gives no place of its own (Top and Height 0), else at its place in the row scaled to the lane's height.
    /// </summary>
    private static Rect BlockRect(in Lane lane, TimelineBlock block, double originX)
    {
        double top, height;
        if (block.Top == 0 && block.Height == 0)
        {
            var inset = lane.Detailed ? 2 : 1;
            (top, height) = (lane.Top + inset, lane.Height - 2 * inset);
        }
        else
        {
            var fraction = lane.Height / Math.Max(lane.Row.Height, 1);
            (top, height) = (lane.Top + block.Top * fraction, block.Height * fraction);
        }

        return new Rect(block.Left - originX, top, Math.Max(block.Width, 3), Math.Max(height, 1));
    }

    /// <summary>
    /// Draws a block: its colour, its pictures and its sound (both laid from <paramref name="contentLeft"/>, where its file's
    /// stretch begins: its left edge, or where it was while its start is trimmed, or slid by a slip), and, on a lane drawn
    /// in full, its gain line, fades, keys and badge; then its outline when it is selected. A J/L cut's sound reaches out
    /// past its edges in the lower part (<paramref name="splitNow"/>: the cut being dragged, in place of the block's). A stack
    /// sound is laid from <paramref name="stackLeft"/> when given (a slip: its lanes stay where they play), else from contentLeft.
    /// </summary>
    private void DrawBlock(DrawingContext drawing, in Lane lane, TimelineBlock block, Rect rect, double contentLeft, (double Lead, double Tail)? splitNow,
        double from, double to, DpiScale dpi, double? stackLeft = null)
    {
        var scale = PixelsPerSecond;
        var detailed = lane.Detailed;
        var (lead, tail) = splitNow ?? block.AudioSplit ?? (0.0, 0.0);
        // A video's block with a sound stack shows what its stack's audible lanes play, as one waveform (5.3-Stage2, the user's call:
        // StackSound, which the host combines), never its own sound beside it; one with no stack sound shows its own (a file of one
        // stream, the user's Q1, even beside a clip with a stack on the same track). A clip whose sound is in its stack has none of its
        // own on its block (unpacked, it is silent; unlinked, its sound is a block of its own: AddTrackBlocks), so nothing is drawn twice.
        // The closed stack's one bar draws its lanes itself (DrawLane).
        var stackSound = lane.Row.Kind == TimelineRowKind.Video && !lane.Summary ? block.StackSound : null;
        var wave = lane.Summary ? null : stackSound ?? block.SoundWindow ?? WindowOf(block, rect.Width, scale);
        var split = detailed && scale > 0 && (lead != 0 || tail != 0);

        // A J/L cut: where the sound reaches out past the picture, a tab of the block's colour under its lower part.
        var band = Rect.Empty;
        if (split)
        {
            band = new Rect(rect.Left - lead * scale, rect.Top + rect.Height * SoundBandTop, Math.Max(rect.Width + (lead + tail) * scale, 1), rect.Height * (1 - SoundBandTop));
            var reach = Translucent(block.Fill, 0.6);
            if (lead > 0)
                drawing.DrawRoundedRectangle(reach, null, new Rect(band.Left, band.Top, lead * scale + 3, band.Height), 3, 3);
            if (tail > 0)
                drawing.DrawRoundedRectangle(reach, null, new Rect(rect.Right - 3, band.Top, tail * scale + 3, band.Height), 3, 3);
        }

        var shape = block.ShapeAt(rect);
        drawing.DrawGeometry(block.Fill, null, shape);
        drawing.PushClip(shape);

        var pictures = false;
        if (detailed && scale > 0 && block.Film is { Frames.Count: > 0 } film)
        {
            DrawFilm(drawing, film, rect, contentLeft, from, to, scale);
            pictures = true;
        }
        else if (detailed && block.Frames is { } frames)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var (picture, left, width) = frames[i];
                var at = contentLeft + left;
                if (at <= to && at + width >= from)
                    drawing.DrawImage(picture, new Rect(at, rect.Top, width, rect.Height));
            }

            pictures = true;
        }

        // Its sound: across the whole block (5.3-Stage2, the user's call), see-through over its pictures, which show through it
        // (no shade under it); a J/L cut's still in its band below (after the clip). The stack's sound is laid on the block's own
        // file time from its left edge (where its lanes play: a slip moves its frames, never them).
        if (wave is { } sound && (!split || stackSound is not null) && scale > 0)
        {
            var soundRect = new Rect(rect.Left, rect.Top + 1, rect.Width, Math.Max(rect.Height - 2, 1));
            DrawWave(drawing, sound, stackSound is null ? contentLeft - lead * scale : stackLeft ?? contentLeft, soundRect, from, to, scale,
                pictures ? WaveOverPictures : 0.7, dpi.DpiScaleX);
        }

        if (detailed)
        {
            if (block.Gain is { } gain)
                DrawGain(drawing, rect, gain, scale);

            // Its fades: shaded above their curves, with a handle where each ends (at the corners while there is none,
            // drawn on the overlay while the pointer is over the block).
            if (block.Fades is { } fades && block.FadeEdit is not null)
                DrawFades(drawing, block, rect, fades, scale);

            if (block.Keys is { Count: > 0 } keys)
            {
                if (block.KeyShape is null || block.KeyShapeRect != rect)
                    (block.KeyShape, block.KeyShapeRect) = (Diamonds(keys, rect, 4.5), rect);
                drawing.DrawGeometry(ThemeManager.KeyFill, KeyOutline, block.KeyShape);
            }

            // The beats of its sound (5.2B, the beat grid): laid with its file's stretch, so they stay on their moments as its start is trimmed.
            if (block.Beats is { Count: > 0 } beats)
                DrawBeats(drawing, beats, rect, contentLeft, from, to);

            // Its own switches at its top right (5.2), its badge left of them.
            var switches = BlockSwitchBoxes(block, rect);
            var free = switches.Lock.IsEmpty ? switches.OnOff : switches.Lock;
            if (block.Badge is { Length: > 0 } badge)
                DrawBadge(drawing, block, free.IsEmpty ? rect : new Rect(rect.Left, rect.Top, Math.Max(free.Left - rect.Left, 0), rect.Height), badge, dpi.PixelsPerDip);
            DrawBlockSwitches(drawing, block, switches, dpi.PixelsPerDip);
        }

        drawing.Pop();

        if (split && stackSound is null && wave is { } splitSound)
        {
            if (block.SoundClip is null || block.SoundClipRect != band)
            {
                var clip = new RectangleGeometry(band, 3, 3);
                clip.Freeze();
                (block.SoundClip, block.SoundClipRect) = (clip, band);
            }

            drawing.PushClip(block.SoundClip);
            drawing.DrawRectangle(SoundShade, null, band);
            DrawWave(drawing, splitSound, contentLeft - lead * scale, band, from, to, scale, 0.9, dpi.DpiScaleX);
            drawing.Pop();
        }

        if (IsSelected?.Invoke(block.Clip) == true)
            drawing.DrawRoundedRectangle(null, SelectedOutline, new Rect(rect.Left + 0.75, rect.Top + 0.75, Math.Max(rect.Width - 1.5, 1), Math.Max(rect.Height - 1.5, 1)), 3, 3);
    }

    // The beat grid's tick (5.2B): faint, a pixel wide.
    private static readonly Pen BeatTick = Frozen(new Pen(Solid(140, 0xFF, 0xFF, 0xFF), 1));

    /// <summary>
    /// A block's beats: a short tick each along its foot, from <paramref name="contentLeft"/> (where its file's stretch is laid), only those
    /// on the block and in the stretch drawn (<paramref name="from"/> to <paramref name="to"/>). The beats are in order, so the first past
    /// the right ends it.
    /// </summary>
    private static void DrawBeats(DrawingContext drawing, IReadOnlyList<double> beats, Rect rect, double contentLeft, double from, double to)
    {
        var (left, right) = (Math.Max(rect.Left, from), Math.Min(rect.Right, to));
        var (top, bottom) = (rect.Bottom - Math.Clamp(rect.Height / 5, 3, 8), rect.Bottom - 1);
        for (var i = 0; i < beats.Count; i++)
        {
            var x = contentLeft + beats[i];
            if (x < left)
                continue;
            if (x > right)
                break;

            x = Math.Round(x) + 0.5;
            drawing.DrawLine(BeatTick, new Point(x, top), new Point(x, bottom));
        }
    }

    /// <summary>A block's sound given the old way (peaks by fraction, in pixels: TimelineBlock.Peaks), as a window by time.</summary>
    private static WaveWindow? WindowOf(TimelineBlock block, double width, double scale)
    {
        if (block.Peaks is not { } peaks || peaks.Data.Count == 0 || scale <= 0)
            return null;

        var span = peaks.Span > 0 ? peaks.Span : width;
        var seconds = peaks.Data.Seconds;
        if (span <= 0)
            return null;
        return new WaveWindow(peaks.Data, peaks.Offset / span * seconds, (peaks.Offset + width) / span * seconds, seconds * scale / span);
    }

    /// <summary>
    /// A block's pictures on its file's time grid: slots as wide as a picture is at the block's height, slot k showing the
    /// frame nearest the middle of its stretch of the file. Only the slots in view are drawn.
    /// </summary>
    private static void DrawFilm(DrawingContext drawing, FilmWindow film, Rect rect, double contentLeft, double from, double to, double scale)
    {
        var frames = film.Frames;
        var fileScale = scale / (film.Speed > 0 ? film.Speed : 1);
        if (film.SecondsPerFrame <= 0 || fileScale <= 0 || !double.IsFinite(fileScale))
            return;

        var first = frames[0];
        var aspect = first.Width > 0 && first.Height > 0 ? first.Width / first.Height : 16.0 / 9;
        var slot = Math.Max(rect.Height * aspect, 8);
        var slotSeconds = slot / fileScale;
        var (left, right) = (Math.Max(rect.Left, from), Math.Min(rect.Right, to));
        if (right <= left)
            return;

        // Where the file's start would be: the slots are counted from there, so they stay where they are in the file.
        var origin = contentLeft - film.MediaFrom * fileScale;
        var firstSlot = (long)Math.Floor((left - origin) / slot);
        var lastSlot = (long)Math.Floor((right - origin) / slot);
        for (var k = firstSlot; k <= lastSlot && k - firstSlot < 400; k++)
        {
            var index = (int)Math.Clamp(Math.Floor((k + 0.5) * slotSeconds / film.SecondsPerFrame), 0, frames.Count - 1);
            drawing.DrawImage(frames[index], new Rect(origin + k * slot, rect.Top, slot, rect.Height));
        }
    }

    /// <summary>
    /// A stretch of a sound, drawn from the frozen tiles of its zoom step (WaveTiles): each tile placed by one transform
    /// (stretched by less than two, and as tall as the band at the sound's gain), with a line through the middle so that
    /// silence still shows. Only what is in view of the band is drawn.
    /// </summary>
    private void DrawWave(DrawingContext drawing, WaveWindow wave, double soundLeft, Rect band, double from, double to, double scale, double opacity, double dpiScale)
    {
        var data = wave.Data;
        var fileScale = scale / (wave.Speed > 0 ? wave.Speed : 1);
        if (data.Count == 0 || fileScale <= 0 || !double.IsFinite(fileScale))
            return;

        var (left, right) = (Math.Max(band.Left, from), Math.Min(band.Right, to));
        if (right - left < 0.5)
            return;

        // The stretch of the sound in view, in its own seconds: never before its start or past its end (or the window's).
        var origin = soundLeft - wave.MediaFrom * fileScale;
        var start = Math.Max((left - origin) / fileScale, 0);
        var end = Math.Min(Math.Min((right - origin) / fileScale, data.Seconds), wave.MediaTo > wave.MediaFrom ? wave.MediaTo + 1 : double.MaxValue);
        if (end <= start)
            return;

        var fill = Translucent(data.Fill, opacity);
        var middle = band.Top + band.Height / 2;
        var height = band.Height / 2 * Math.Clamp(wave.GainScale, 0, 4);
        var bucket = (int)Math.Clamp(Math.Floor(Math.Log2(fileScale)), WaveTiles.LeastBucket, WaveTiles.MostBucket);
        var bucketScale = Math.ScaleB(1.0, bucket);
        var tileSeconds = WaveTiles.TilePixels / bucketScale;
        var stretch = fileScale / bucketScale;
        var moving = _waveMoving;

        // Whole tiles reach past the sound's own stretch (a tile's width at most, and the window's second of slack past its end): the wave is
        // cut to its band, from its first second shown to its last (5.4.4-Final, the user's: a closed sound stack's bar, which no block shape
        // clips, drew its wave past its clip's end again).
        drawing.PushClip(WaveClip(new Rect(Math.Max(left, origin + start * fileScale), band.Top,
            Math.Max(Math.Min(right, origin + end * fileScale) - Math.Max(left, origin + start * fileScale), 0), band.Height)));
        for (var tile = (long)Math.Floor(start / tileSeconds); tile * tileSeconds < end; tile++)
        {
            // While the zoom moves (5.4.4) only a tile made already is drawn as itself; settled, it is made now if this render may.
            var x = origin + tile * tileSeconds * fileScale;
            var geometry = moving ? WaveTiles.Peek(data, bucket, tile) : WaveTiles.Get(data, bucket, tile, dpiScale);
            if (geometry is null)
            {
                // Not made (only so many are made a render, so that crossing a zoom step never stalls one; none while the zoom moves):
                // a step either side stands in, stretched, until it is.
                if (DrawStandIn(drawing, data, bucket, tile, x, tileSeconds * fileScale, stretch, height, middle, band, fill, start, end, !moving))
                {
                    if (moving)
                        _waveOwed = true;
                    else
                        _tilesPending = true;
                    continue;
                }

                // Nothing to stand in (a sound come into view): made now, while this render may, even while the zoom moves.
                geometry = moving ? WaveTiles.Get(data, bucket, tile, dpiScale) : null;
                if (geometry is null)
                {
                    if (moving)
                        _waveOwed = true;
                    else
                        _tilesPending = true;
                    continue;
                }
            }

            drawing.PushTransform(Placed(stretch, height, x, middle, keep: !moving));
            drawing.DrawGeometry(fill, null, geometry);
            drawing.Pop();
        }

        drawing.Pop();

        // Silence is still a line: the sound is there, and quiet.
        var (lineFrom, lineTo) = (Math.Max(left, origin + start * fileScale), Math.Min(right, origin + end * fileScale));
        if (lineTo > lineFrom)
            drawing.DrawRectangle(fill, null, new Rect(lineFrom, middle - 0.5, lineTo - lineFrom, 1));
    }

    // The waves' clips (5.4.4-Final), frozen and kept while their rectangles are the same (a render of the same rows makes none).
    private readonly Dictionary<Rect, RectangleGeometry> _waveClips = [];

    /// <summary>A frozen clip of a wave's rectangle, the same one while the rectangle is the same.</summary>
    private RectangleGeometry WaveClip(Rect rect)
    {
        if (_waveClips.TryGetValue(rect, out var kept))
            return kept;
        var made = new RectangleGeometry(rect);
        made.Freeze();
        if (_waveClips.Count >= 1024)
            _waveClips.Clear();
        _waveClips[rect] = made;
        return made;
    }

    /// <summary>
    /// A tile's placement (stretch, height, where): frozen, and the same one while the numbers are the same. Not <paramref name="keep"/>
    /// (5.4.4: the zoom moving, whose numbers never come again), made and not kept.
    /// </summary>
    private MatrixTransform Placed(double scaleX, double scaleY, double x, double y, bool keep = true)
    {
        var key = (scaleX, scaleY, x, y);
        if (_placements.TryGetValue(key, out var kept))
            return kept;

        var made = new MatrixTransform(scaleX, 0, 0, scaleY, x, y);
        made.Freeze();
        if (!keep)
            return made;
        if (_placements.Count >= 2048)
            _placements.Clear();
        _placements[key] = made;
        return made;
    }

    /// <summary>
    /// A tile not made, stood in for by those of a zoom step either side (5.4.4), stretched to its place: first the finer ones that make it
    /// (2 or 4 at half or a quarter the stretch, every one in view), then a coarser one that holds it (cut to it), then the finer ones in
    /// part. False when none is made. <paramref name="start"/> and <paramref name="end"/> are the sound's seconds in view.
    /// </summary>
    private bool DrawStandIn(DrawingContext drawing, WaveformData data, int bucket, long tile, double x, double tileWidth, double stretch, double height,
        double middle, Rect band, Brush fill, double start, double end, bool keep)
    {
        var tileSeconds = WaveTiles.TilePixels / Math.ScaleB(1.0, bucket);
        var someFiner = 0;
        for (var k = 1; k <= WaveTiles.StandInOctaves && bucket + k <= WaveTiles.MostBucket; k++)
        {
            var (found, wanted) = FinerTiles(data, bucket + k, tile, k, tileSeconds, start, end);
            if (found > 0 && found == wanted)
            {
                DrawFiner(k);
                return true;
            }

            if (found > 0 && someFiner == 0)
                someFiner = k;
        }

        for (var k = 1; k <= WaveTiles.StandInOctaves && bucket - k >= WaveTiles.LeastBucket; k++)
        {
            if (WaveTiles.Peek(data, bucket - k, tile >> k) is not { } coarse)
                continue;
            var cut = new RectangleGeometry(new Rect(x, band.Top, tileWidth, band.Height));
            cut.Freeze();
            drawing.PushClip(cut);
            drawing.PushTransform(Placed(stretch * (1 << k), height, x - (tile & ((1L << k) - 1)) * tileWidth, middle, keep));
            drawing.DrawGeometry(fill, null, coarse);
            drawing.Pop();
            drawing.Pop();
            return true;
        }

        if (someFiner == 0)
            return false;
        FinerTiles(data, bucket + someFiner, tile, someFiner, tileSeconds, start, end);
        DrawFiner(someFiner);
        return true;

        void DrawFiner(int k)
        {
            var count = 1 << k;
            for (var j = 0; j < count; j++)
            {
                if (_finer[j] is not { } finer)
                    continue;
                drawing.PushTransform(Placed(stretch / count, height, x + j * tileWidth / count, middle, keep));
                drawing.DrawGeometry(fill, null, finer);
                drawing.Pop();
            }
        }
    }

    /// <summary>
    /// The tiles of a step <paramref name="k"/> octaves finer that make one tile, into _finer (null where not made or not in view): how many
    /// were found, and how many of them the sound's seconds in view (<paramref name="start"/> to <paramref name="end"/>) want.
    /// </summary>
    private (int Found, int Wanted) FinerTiles(WaveformData data, int finerBucket, long tile, int k, double tileSeconds, double start, double end)
    {
        var count = 1 << k;
        var (found, wanted) = (0, 0);
        var seconds = tileSeconds / count;
        for (var j = 0; j < count; j++)
        {
            var from = tile * tileSeconds + j * seconds;
            _finer[j] = null;
            if (from >= end || from + seconds <= start)
                continue;
            wanted++;
            if ((_finer[j] = WaveTiles.Peek(data, finerBucket, (tile << k) + j)) is not null)
                found++;
        }

        return (found, wanted);
    }

    /// <summary>The scale changed (5.4.4): the crisp tiles wait until it has rested WaveSettleMs (the timer is made at the first zoom, never before).</summary>
    private void RestartWaveSettle()
    {
        if (_waveSettle is null)
        {
            _waveSettle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(WaveSettleMs) };
            _waveSettle.Tick += (_, _) => SettleWaves();
        }

        _waveSettle.Stop();
        _waveSettle.Start();
    }

    /// <summary>
    /// The zoom has rested (5.4.4): the timer stops (nothing holds the control alive), and if a stand-in was drawn the rows are drawn once
    /// more with the crisp tiles, <see cref="WaveTiles.SettleBuilds"/> made in that render (any left follow, a few a render).
    /// </summary>
    private void SettleWaves()
    {
        _waveSettle?.Stop();
        _waveMoving = false;
        if (!_waveOwed)
            return;
        _waveOwed = false;
        _waveSettling = true;
        _contentDirty = _overlayDirty = true;
        InvalidateVisual();
    }

    /// <summary>Waveform tiles made since HandPeg started, and held now (the golden wave-tiles-zoom).</summary>
    internal static (int Built, int Kept) WaveTilesForTests => (WaveTiles.Built, WaveTiles.Kept);

    /// <summary>Whether the zoom counts as moving now: stand-ins drawn, nothing made (the golden wave-tiles-zoom).</summary>
    internal bool WaveZoomMoving => _waveMoving || _waveSettle is { IsEnabled: true };

    /// <summary>Does what the zoom's rest does, at once (the golden wave-tiles-zoom: no dispatcher runs there).</summary>
    internal void SettleWaveZoom() => SettleWaves();

    /// <summary>A colour, as see-through as asked (a colour that is not one plain colour is drawn as it is).</summary>
    private static Brush Translucent(Brush brush, double opacity)
    {
        if (brush is not SolidColorBrush solid || opacity >= 1)
            return brush;

        var key = (brush, (int)Math.Round(opacity * 100));
        if (TranslucentBrushes.TryGetValue(key, out var kept))
            return kept;

        if (TranslucentBrushes.Count >= 64)
            TranslucentBrushes.Clear();
        var color = solid.Color;
        var made = Solid((byte)Math.Round(color.A * solid.Opacity * opacity), color.R, color.G, color.B);
        TranslucentBrushes[key] = made;
        return made;
    }

    // ----- Gain lines, fades, keys, badges, pills and seams -----

    /// <summary>Where the gain line of a block lies, for a gain in dB: silence at its foot, +12 dB at its top.</summary>
    private static double GainY(Rect rect, double gainDb) =>
        rect.Top + 3 + (1 - (Math.Clamp(gainDb, TimelineMetrics.LowestGain, TimelineMetrics.HighestGain) - TimelineMetrics.LowestGain)
            / (TimelineMetrics.HighestGain - TimelineMetrics.LowestGain)) * Math.Max(rect.Height - 6, 1);

    /// <summary>The gain in dB at a height in a block: the line's place, the other way round.</summary>
    private static double GainAt(Rect rect, double y) =>
        Math.Round(TimelineMetrics.LowestGain + (1 - Math.Clamp((y - rect.Top - 3) / Math.Max(rect.Height - 6, 1), 0, 1))
            * (TimelineMetrics.HighestGain - TimelineMetrics.LowestGain), 1);

    /// <summary>How wide a block's fades are drawn, in pixels: their seconds at the scale, never more than the block.</summary>
    private static (double In, double Out) FadeSpan(Rect rect, double fadeInSeconds, double fadeOutSeconds, double scale)
    {
        scale = Math.Max(scale, 0);
        var fadeIn = Math.Clamp(fadeInSeconds * scale, 0, rect.Width);
        var fadeOut = Math.Clamp(fadeOutSeconds * scale, 0, rect.Width - fadeIn);
        return (fadeIn, fadeOut);
    }

    /// <summary>A clip's gain line: level across it at its gain, sloping down to its foot over its fades, with a grip in its middle.</summary>
    private static void DrawGain(DrawingContext drawing, Rect rect, (double GainDb, double FadeIn, double FadeOut) gain, double scale)
    {
        var y = GainY(rect, gain.GainDb);
        var (fadeIn, fadeOut) = FadeSpan(rect, gain.FadeIn, gain.FadeOut, scale);
        var (inX, outX) = (rect.Left + fadeIn, rect.Right - fadeOut);
        drawing.DrawLine(GainPen, new Point(rect.Left, fadeIn > 0.5 ? rect.Bottom : y), new Point(inX, y));
        drawing.DrawLine(GainPen, new Point(inX, y), new Point(outX, y));
        drawing.DrawLine(GainPen, new Point(outX, y), new Point(rect.Right, fadeOut > 0.5 ? rect.Bottom : y));
        drawing.DrawRectangle(HandleFill, null, new Rect((inX + outX) / 2 - 2.5, y - 2.5, 5, 5));
    }

    /// <summary>
    /// A block's fades: each shaded above its curve (from nothing at the block's foot to all of it at its top, or back, in
    /// the curve its transition style follows, EasingCurves.Evaluate), with a handle where each ends. The shapes are made
    /// once and kept while the block and its fades stay as they are.
    /// </summary>
    private static void DrawFades(DrawingContext drawing, TimelineBlock block, Rect rect,
        (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) fades, double scale)
    {
        var (fadeIn, fadeOut) = FadeSpan(rect, fades.In, fades.Out, scale);
        if (fadeIn <= 0.5 && fadeOut <= 0.5)
            return;

        var key = (rect, fadeIn, fadeOut, fades.InStyle, fades.OutStyle);
        if (block.FadeShade is null || block.FadeLine is null || block.FadeKey != key)
        {
            var (shade, line) = (new StreamGeometry(), new StreamGeometry());
            using (var shadePath = shade.Open())
            using (var linePath = line.Open())
            {
                if (fadeIn > 0.5)
                    Ramp(shadePath, linePath, rect, rect.Left, fadeIn, rising: true, fades.InStyle);
                if (fadeOut > 0.5)
                    Ramp(shadePath, linePath, rect, rect.Right - fadeOut, fadeOut, rising: false, fades.OutStyle);
            }

            shade.Freeze();
            line.Freeze();
            (block.FadeShade, block.FadeLine, block.FadeKey) = (shade, line, key);
        }

        drawing.DrawGeometry(FadeShadeFill, null, block.FadeShade);
        drawing.DrawGeometry(null, FadeCurve, block.FadeLine);
        if (fadeIn > 0.5)
            DrawFadeHandle(drawing, rect, rect.Left + fadeIn);
        if (fadeOut > 0.5)
            DrawFadeHandle(drawing, rect, rect.Right - fadeOut);
    }

    // One fade's ramp: its shade (the top edge across it, then back along its curve) and its curve.
    private static void Ramp(StreamGeometryContext shadePath, StreamGeometryContext linePath, Rect rect, double left, double width, bool rising, Models.TransitionStyle style)
    {
        var easing = new Models.Keyframe(0, 0, Models.TransitionStyles.Easing(style));
        var steps = Math.Clamp((int)(width / 3), 2, 32);
        Span<Point> points = stackalloc Point[33];
        for (var i = 0; i <= steps; i++)
        {
            var u = (double)i / steps;
            var shown = Models.EasingCurves.Evaluate(rising ? u : 1 - u, easing);
            points[i] = new Point(left + u * width, rect.Bottom - Math.Clamp(shown, -0.1, 1.15) * rect.Height);
        }

        shadePath.BeginFigure(new Point(left, rect.Top), isFilled: true, isClosed: true);
        shadePath.LineTo(new Point(left + width, rect.Top), false, false);
        for (var i = steps; i >= 0; i--)
            shadePath.LineTo(points[i], false, false);

        linePath.BeginFigure(points[0], isFilled: false, isClosed: false);
        for (var i = 1; i <= steps; i++)
            linePath.LineTo(points[i], true, true);
    }

    /// <summary>Where a fade handle is drawn: at its fade's end, kept inside the block.</summary>
    private static double HandleX(Rect rect, double x) => Math.Clamp(x, rect.Left + 4, Math.Max(rect.Right - 4, rect.Left + 4));

    private static void DrawFadeHandle(DrawingContext drawing, Rect rect, double x) =>
        drawing.DrawRoundedRectangle(FadeHandleFill, FadeHandleOutline, new Rect(HandleX(rect, x) - 3.5, rect.Top + 1.5, 7, 7), 1.5, 1.5);

    /// <summary>The fade being dragged, in words beside its handle ("Fade In: 0.75 s · Smooth"), inside what is seen.</summary>
    private void DrawFadeReadout(DrawingContext drawing, Rect rect, bool fadeIn,
        (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) fades, double from, double to, double pixelsPerDip)
    {
        var (inWidth, outWidth) = FadeSpan(rect, fades.In, fades.Out, PixelsPerSecond);
        var x = HandleX(rect, fadeIn ? rect.Left + inWidth : rect.Right - outWidth);
        DrawReadout(drawing, FadeWords(fadeIn, fadeIn ? fades.In : fades.Out, fadeIn ? fades.InStyle : fades.OutStyle), x, rect.Top + 1, toRight: fadeIn, from, to, pixelsPerDip);
    }

    private static string FadeWords(bool fadeIn, double seconds, Models.TransitionStyle style) =>
        string.Create(CultureInfo.InvariantCulture, $"Fade {(fadeIn ? "In" : "Out")}: {seconds:0.00} s · {Models.TransitionStyles.Label(style)}");

    /// <summary>Words in a dark box beside a point (to its right, or its left), kept inside what is seen.</summary>
    private static void DrawReadout(DrawingContext drawing, string words, double x, double top, bool toRight, double from, double to, double pixelsPerDip)
    {
        var text = new FormattedText(words, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, pixelsPerDip);
        var left = toRight ? x + 7 : x - 7 - text.Width - 8;
        left = Math.Clamp(left, from + 2, Math.Max(to - text.Width - 10, from + 2));
        var box = new Rect(left, top, text.Width + 8, text.Height + 2);
        drawing.DrawRoundedRectangle(ReadoutFill, null, box, 3, 3);
        drawing.DrawText(text, new Point(box.Left + 4, box.Top + 1));
    }

    /// <summary>Keyframes' diamonds as one frozen shape: one at each key (pixels from the block's left), near its foot.</summary>
    private static Geometry Diamonds(IReadOnlyList<double> keys, Rect rect, double radius)
    {
        var diamonds = new StreamGeometry();
        using (var path = diamonds.Open())
        {
            var y = rect.Bottom - 6;
            foreach (var key in keys)
            {
                var x = rect.Left + key;
                if (x < rect.Left - radius || x > rect.Right + radius)
                    continue;

                path.BeginFigure(new Point(x, y - radius), isFilled: true, isClosed: true);
                path.LineTo(new Point(x + radius, y), true, false);
                path.LineTo(new Point(x, y + radius), true, false);
                path.LineTo(new Point(x - radius, y), true, false);
            }
        }

        diamonds.Freeze();
        return diamonds;
    }

    // ----- A block's own switches (5.2) -----
    // Segoe MDL2 Assets: View (E890), Hide (ED1A), Volume (E767), Mute (E74F), Link (E71B), Unlock (E785).

    private const double BlockSwitchSize = 14, BlockSwitchGap = 2;
    private static readonly Brush SwitchOffFill = Solid(225, 0xC6, 0x28, 0x28);
    private static readonly Brush SwitchFreeFill = Solid(225, 0xEF, 0x8F, 0x00);

    /// <summary>Where a block's switches are (its On/Off rightmost, its Lock left of it), in its rect's space; none on a block too small for them.</summary>
    internal static (Rect OnOff, Rect Lock) BlockSwitchBoxes(TimelineBlock block, Rect rect)
    {
        if ((block.IsOn is null && block.IsLocked is null) || block.Switched is null || rect.Width < 3 * BlockSwitchSize + 16 || rect.Height < BlockSwitchSize + 4)
            return (Rect.Empty, Rect.Empty);

        var (top, right) = (rect.Top + 3, rect.Right - 4);
        var onOff = block.IsOn is null ? Rect.Empty : new Rect(right - BlockSwitchSize, top, BlockSwitchSize, BlockSwitchSize);
        if (!onOff.IsEmpty)
            right = onOff.Left - BlockSwitchGap;
        var lockBox = block.IsLocked is null ? Rect.Empty : new Rect(right - BlockSwitchSize, top, BlockSwitchSize, BlockSwitchSize);
        return (onOff, lockBox);
    }

    private void DrawBlockSwitches(DrawingContext drawing, TimelineBlock block, (Rect OnOff, Rect Lock) boxes, double pixelsPerDip)
    {
        if (!boxes.OnOff.IsEmpty && block.IsOn is { } on)
            DrawBlockSwitch(drawing, boxes.OnOff, on ? (block.SwitchIsSound ? "\uE767" : "\uE890") : (block.SwitchIsSound ? "\uE74F" : "\uED1A"), on ? BadgeFill : SwitchOffFill, pixelsPerDip);
        if (!boxes.Lock.IsEmpty && block.IsLocked is { } locked)
            DrawBlockSwitch(drawing, boxes.Lock, locked ? "\uE71B" : "\uE785", locked ? BadgeFill : SwitchFreeFill, pixelsPerDip);
    }

    private void DrawBlockSwitch(DrawingContext drawing, Rect box, string glyph, Brush fill, double pixelsPerDip)
    {
        drawing.DrawRoundedRectangle(fill, null, box, 3, 3);
        var text = Word(glyph, Brushes.White, 9, icon: true, pixelsPerDip);
        drawing.DrawText(text, new Point(box.Left + (box.Width - text.Width) / 2, box.Top + (box.Height - text.Height) / 2));
    }

    /// <summary>A block's badge: its words in a small dark tag at its top right, where the block has room for it.</summary>
    private static void DrawBadge(DrawingContext drawing, TimelineBlock block, Rect rect, string badge, double pixelsPerDip)
    {
        var text = block.BadgeText ??= new FormattedText(badge, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, StrongFace, 9.5, Brushes.White, pixelsPerDip);
        var box = new Rect(rect.Right - text.Width - 10, rect.Top + 3, text.Width + 6, text.Height + 1);
        if (box.Left < rect.Left + 4)
            return;

        drawing.DrawRoundedRectangle(BadgeFill, null, box, 3, 3);
        drawing.DrawText(text, new Point(box.Left + 3, box.Top + 0.5));
    }

    // A crossfade's box: from half of it before its cut to half after, the lane's whole height.
    private static Rect CrossfadeBox(in Lane lane, TimelineBlock block, double scale, double originX)
    {
        var rect = BlockRect(lane, block, originX);
        var half = block.CrossfadeIn * scale / 2;
        return new Rect(rect.Left - half, rect.Top, Math.Max(half * 2, 6), rect.Height);
    }

    // Its remove button: at the box's top middle, where the box has room for it.
    private static Rect CrossfadeRemove(Rect box) =>
        box.Width >= 18 && box.Height >= 22 ? new Rect(box.Left + box.Width / 2 - 7, box.Top + 2, 14, 14) : Rect.Empty;

    /// <summary>
    /// A crossfade as the industry draws it (the user's directive): a shaded box the full height of the track across the cut, its two edges
    /// grips, its two equal-power curves crossing in the middle, and a button to take it away.
    /// </summary>
    private void DrawCrossfade(DrawingContext drawing, in Lane lane, TimelineBlock block, double scale, double originX, double pixelsPerDip)
    {
        var box = CrossfadeBox(lane, block, scale, originX);
        var selected = ReferenceEquals(SelectedCrossfade, block.Clip);
        drawing.DrawRectangle(CrossfadeFill, selected ? SelectedOutline : null, box);
        drawing.DrawLine(CrossfadeLine, new Point(box.Left + 0.5, box.Top), new Point(box.Left + 0.5, box.Bottom));
        drawing.DrawLine(CrossfadeLine, new Point(box.Right - 0.5, box.Top), new Point(box.Right - 0.5, box.Bottom));

        const int steps = 14;
        var curves = new StreamGeometry();
        using (var context = curves.Open())
        {
            foreach (var rising in new[] { true, false })
            {
                for (var i = 0; i <= steps; i++)
                {
                    var through = (double)i / steps;
                    var level = rising ? Math.Sin(through * Math.PI / 2) : Math.Cos(through * Math.PI / 2);
                    var point = new Point(box.Left + through * box.Width, box.Bottom - 2 - level * (box.Height - 4));
                    if (i == 0)
                        context.BeginFigure(point, false, false);
                    else
                        context.LineTo(point, true, false);
                }
            }
        }

        curves.Freeze();
        drawing.DrawGeometry(null, CrossfadeLine, curves);

        if (CrossfadeRemove(box) is { IsEmpty: false } remove)
        {
            drawing.DrawEllipse(CrossfadeButton, null, new Point(remove.Left + 7, remove.Top + 7), 7, 7);
            drawing.DrawLine(CrossfadeLine, new Point(remove.Left + 4.5, remove.Top + 4.5), new Point(remove.Right - 4.5, remove.Bottom - 4.5));
            drawing.DrawLine(CrossfadeLine, new Point(remove.Left + 4.5, remove.Bottom - 4.5), new Point(remove.Right - 4.5, remove.Top + 4.5));
        }
    }

    // The pill a click closes a gap with: in the gap's middle, two arrows pointing in (what follows closes up).
    private static Rect GapPill(in Lane lane, double left, double right)
    {
        var (middle, y) = ((left + right) / 2, lane.Top + lane.Height / 2);
        return new Rect(middle - 12, y - 6, 24, 12);
    }

    private static void DrawGapPill(DrawingContext drawing, in Lane lane, double left, double right)
    {
        if (right - left < GapLeast)
            return;

        var pill = GapPill(lane, left, right);
        drawing.DrawRoundedRectangle(GapFill, GapOutline, pill, 6, 6);
        var (middle, y) = (pill.Left + pill.Width / 2, pill.Top + pill.Height / 2);
        drawing.DrawLine(GapArrow, new Point(pill.Left + 4, y), new Point(middle - 1.5, y));
        drawing.DrawLine(GapArrow, new Point(middle - 4, y - 2.5), new Point(middle - 1.5, y));
        drawing.DrawLine(GapArrow, new Point(middle - 4, y + 2.5), new Point(middle - 1.5, y));
        drawing.DrawLine(GapArrow, new Point(pill.Right - 4, y), new Point(middle + 1.5, y));
        drawing.DrawLine(GapArrow, new Point(middle + 4, y - 2.5), new Point(middle + 1.5, y));
        drawing.DrawLine(GapArrow, new Point(middle + 4, y + 2.5), new Point(middle + 1.5, y));
    }

    // A seam where dead air was folded out: a dashed line down the lane, and on a lane drawn in full a tab at its top with
    // a zigzag in it (the fold), which a click puts back.
    private static void DrawSeam(DrawingContext drawing, in Lane lane, double x)
    {
        drawing.DrawLine(SeamLine, new Point(x, lane.Top + 1), new Point(x, lane.Bottom - 1));
        if (!lane.Detailed)
            return;

        var tab = SeamTab(lane, x);
        drawing.DrawRoundedRectangle(SeamFill, null, tab, 2, 2);
        var (y, step) = (tab.Top + tab.Height / 2, tab.Width / 4);
        drawing.DrawLine(SeamZigzag, new Point(tab.Left + 1.5, y), new Point(tab.Left + step + 0.5, y - 2));
        drawing.DrawLine(SeamZigzag, new Point(tab.Left + step + 0.5, y - 2), new Point(tab.Left + 2 * step, y + 2));
        drawing.DrawLine(SeamZigzag, new Point(tab.Left + 2 * step, y + 2), new Point(tab.Right - step - 0.5, y - 2));
        drawing.DrawLine(SeamZigzag, new Point(tab.Right - step - 0.5, y - 2), new Point(tab.Right - 1.5, y));
    }

    // The tab sits under the band of the fade handles (the corners of the clips either side of the seam), as far as the
    // lane is tall enough for it; where it still meets a handle, the handle is the one pressed.
    private static Rect SeamTab(in Lane lane, double x) => new(x - 6, Math.Max(Math.Min(lane.Top + FadeBand + 5, lane.Bottom - 11), lane.Top + 2), 12, 9);

    // ----- The blocks' words -----

    /// <summary>
    /// The blocks' names, in the control's own x: each kept in view while its block is (from the view's left, never past
    /// the block's right), so a long clip scrolled half out of view still says what it is. Laid again on every pan, which
    /// only places words already laid out.
    /// </summary>
    private void RenderLabels(double pixelsPerDip)
    {
        _labelsDirty = false;
        using var drawing = _labels.RenderOpen();
        var (left, right) = (_headerWidth, _headerWidth + TimeAreaWidth);
        if (right <= left)
            return;

        var origin = ViewOffsetX - _headerWidth;
        var sliding = CurrentShift(PixelsPerSecond);
        foreach (var lane in _lanes)
        {
            if (lane.Mini || lane.Height < 14)
                continue;

            if (lane.Row.Kind == TimelineRowKind.Empty)
            {
                var hint = _emptyHint ??= new FormattedText("Drop media here for a new track", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11, _muted, pixelsPerDip);
                drawing.DrawText(hint, new Point(left + 10, lane.Top + (lane.Height - hint.Height) / 2));
                continue;
            }

            // While something slides, the words of what moves go with their blocks; those of a stretch being wiped in wait.
            var shift = sliding is { } each && Applies(lane, each) ? each : (Shift?)null;
            foreach (var block in lane.Row.Blocks)
            {
                if (block.Name.Length == 0 || IsDragged(block))
                    continue;

                var rect = BlockRect(lane, block, origin);
                switch (SlidOf(shift, block.Left))
                {
                    case Slid.Wiped:
                        continue;
                    case Slid.Moved:
                        rect.X += shift!.Value.Dx;
                        break;
                }

                if (rect.Right < left || rect.Left > right || rect.Width < 18)
                    continue;
                DrawLabel(drawing, block, rect, left, pixelsPerDip);
            }
        }
    }

    private static void DrawLabel(DrawingContext drawing, TimelineBlock block, Rect rect, double viewLeft, double pixelsPerDip)
    {
        var label = block.Label ??= new FormattedText(block.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, pixelsPerDip)
        {
            MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };

        // Set only when it changes: setting it lays the text out again.
        var widest = Math.Max(rect.Width - 10, 4);
        if (label.MaxTextWidth != widest)
            label.MaxTextWidth = widest;

        var x = Math.Max(rect.Left, viewLeft) + 5;
        x = Math.Max(Math.Min(x, rect.Right - 5 - label.Width), rect.Left + 5);
        var pictures = block.Film is not null || block.Frames is not null;
        var y = pictures && rect.Height >= 30 ? rect.Top + 3 : rect.Top + (rect.Height - label.Height) / 2;

        // Over frames, the words get a shade of their own to be read against.
        if (pictures)
            drawing.DrawRoundedRectangle(LabelShade, null, new Rect(x - 2, y, label.Width + 4, label.Height), 2, 2);
        drawing.DrawText(label, new Point(x, y));
    }
}
