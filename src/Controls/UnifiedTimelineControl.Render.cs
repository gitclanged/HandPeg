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

    // How far down a block its sound band starts, under its pictures (or a J/L cut's sound), as a fraction of its height.
    private const double SoundBandTop = 0.6;

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
    private static readonly Brush CrossfadeFill = Solid(170, 0x4F, 0xC3, 0xF7);
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

    // Where files dragged over the control would land (ShowDropMark).
    private sealed record DropMark(TimelineRow? Row, double Seconds, int Count, bool Stack);

    private DropMark? _dropMark;
    private FormattedText? _dropWords;
    private string? _dropWordsOf;
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

        WaveTiles.BeginRender();

        using (var drawing = _content.RenderOpen())
        {
            if (width > 0 && _lanes.Count > 0)
            {
                var (from, to) = (_renderedFrom - _originX, _renderedTo - _originX);

                // The lanes' wells, sunk by the theme's edge where it draws one (along their tops and feet; their ends are
                // drawn beside the headers, where they stay).
                foreach (var lane in _lanes)
                {
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
                        drawing.DrawRectangle(TimelineTrack.PastEndShade, null, new Rect(shade, 0, to - shade, _extent));
                        if (end >= from)
                            drawing.DrawLine(TimelineTrack.EndLine, new Point(end + 0.5, 0), new Point(end + 0.5, _extent));
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

    // Whether a lane slides with what is sliding: its row is among those named, or (none named) its track is not locked.
    private static bool Applies(in Lane lane, Shift shift) =>
        shift.Rows is { } rows ? rows.Contains(lane.Row.Key) : (lane.Row.State & RowSwitches.Locked) == 0 && lane.Row.Kind != TimelineRowKind.Background;

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

        if (lane.Detailed && scale > 0)
        {
            // A crossfade across a cut: a pill over the two blocks, as long as the crossfade and centred on the cut.
            foreach (var block in row.Blocks)
            {
                if (block.CrossfadeIn <= 0.01 || IsDragged(block))
                    continue;

                var slid = SlidOf(shift, block.Left);
                if (slid != Slid.Wiped)
                    DrawCrossfade(drawing, lane, block, scale, slid == Slid.Moved ? _originX - dx : _originX);
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
    /// past its edges in the lower part (<paramref name="splitNow"/>: the cut being dragged, in place of the block's).
    /// </summary>
    private void DrawBlock(DrawingContext drawing, in Lane lane, TimelineBlock block, Rect rect, double contentLeft, (double Lead, double Tail)? splitNow,
        double from, double to, DpiScale dpi)
    {
        var scale = PixelsPerSecond;
        var detailed = lane.Detailed;
        var (lead, tail) = splitNow ?? block.AudioSplit ?? (0.0, 0.0);
        var wave = block.SoundWindow ?? WindowOf(block, rect.Width, scale);
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

        // Its sound: over its pictures, in their lower part on a shade of its own (never a slab over the frames); alone,
        // across the block.
        if (wave is { } sound && !split && scale > 0)
        {
            var soundRect = pictures
                ? new Rect(rect.Left, rect.Top + rect.Height * SoundBandTop, rect.Width, rect.Height * (1 - SoundBandTop))
                : new Rect(rect.Left, rect.Top + 1, rect.Width, Math.Max(rect.Height - 2, 1));
            if (pictures)
                drawing.DrawRectangle(SoundShade, null, soundRect);
            DrawWave(drawing, sound, contentLeft - lead * scale, soundRect, from, to, scale, pictures ? 0.9 : 0.7, dpi.DpiScaleX);
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

            if (block.Badge is { Length: > 0 } badge)
                DrawBadge(drawing, block, rect, badge, dpi.PixelsPerDip);
        }

        drawing.Pop();

        if (split && wave is { } splitSound)
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
        for (var tile = (long)Math.Floor(start / tileSeconds); tile * tileSeconds < end; tile++)
        {
            var x = origin + tile * tileSeconds * fileScale;
            if (WaveTiles.Get(data, bucket, tile, dpiScale) is { } geometry)
            {
                drawing.PushTransform(Placed(stretch, height, x, middle));
                drawing.DrawGeometry(fill, null, geometry);
                drawing.Pop();
                continue;
            }

            // Not made yet (only so many are made a render, so that crossing a zoom step never stalls one): the step
            // below's tile, cut to this one's stretch, stands in until the next render makes it.
            _tilesPending = true;
            if (bucket - 1 >= WaveTiles.LeastBucket && WaveTiles.Peek(data, bucket - 1, tile / 2) is { } coarse)
            {
                var cut = new RectangleGeometry(new Rect(x, band.Top, tileSeconds * fileScale, band.Height));
                cut.Freeze();
                drawing.PushClip(cut);
                drawing.PushTransform(Placed(stretch * 2, height, origin + tile / 2 * 2 * tileSeconds * fileScale, middle));
                drawing.DrawGeometry(fill, null, coarse);
                drawing.Pop();
                drawing.Pop();
            }
        }

        // Silence is still a line: the sound is there, and quiet.
        var (lineFrom, lineTo) = (Math.Max(left, origin + start * fileScale), Math.Min(right, origin + end * fileScale));
        if (lineTo > lineFrom)
            drawing.DrawRectangle(fill, null, new Rect(lineFrom, middle - 0.5, lineTo - lineFrom, 1));
    }

    /// <summary>A tile's placement (stretch, height, where): frozen, and the same one while the numbers are the same.</summary>
    private MatrixTransform Placed(double scaleX, double scaleY, double x, double y)
    {
        var key = (scaleX, scaleY, x, y);
        if (_placements.TryGetValue(key, out var kept))
            return kept;

        if (_placements.Count >= 2048)
            _placements.Clear();
        var made = new MatrixTransform(scaleX, 0, 0, scaleY, x, y);
        made.Freeze();
        _placements[key] = made;
        return made;
    }

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
        rect.Top + 3 + (1 - (Math.Clamp(gainDb, TimelineTrack.LowestGain, TimelineTrack.HighestGain) - TimelineTrack.LowestGain)
            / (TimelineTrack.HighestGain - TimelineTrack.LowestGain)) * Math.Max(rect.Height - 6, 1);

    /// <summary>The gain in dB at a height in a block: the line's place, the other way round.</summary>
    private static double GainAt(Rect rect, double y) =>
        Math.Round(TimelineTrack.LowestGain + (1 - Math.Clamp((y - rect.Top - 3) / Math.Max(rect.Height - 6, 1), 0, 1))
            * (TimelineTrack.HighestGain - TimelineTrack.LowestGain), 1);

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

    /// <summary>A pill across the cut a crossfade spans, with a cross in it: as long as the crossfade, centred on the cut.</summary>
    private static void DrawCrossfade(DrawingContext drawing, in Lane lane, TimelineBlock block, double scale, double originX)
    {
        var rect = BlockRect(lane, block, originX);
        var half = block.CrossfadeIn * scale / 2;
        var height = Math.Min(Math.Max(rect.Height * 0.42, 8), 16);
        var pill = new Rect(rect.Left - half, rect.Top + (rect.Height - height) / 2, Math.Max(half * 2, 6), height);
        drawing.DrawRoundedRectangle(CrossfadeFill, null, pill, height / 2, height / 2);
        var inset = Math.Min(height / 2, pill.Width / 2);
        drawing.DrawLine(CrossfadeLine, new Point(pill.Left + inset, pill.Top + 2), new Point(pill.Right - inset, pill.Bottom - 2));
        drawing.DrawLine(CrossfadeLine, new Point(pill.Left + inset, pill.Bottom - 2), new Point(pill.Right - inset, pill.Top + 2));
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

    // ----- The headers -----

    private static bool TwoLines(in Lane lane) => lane.Height >= 44;

    private static double DeckX(int depth) => 12 + (depth - 1) * DeckIndent;

    // A header's chevrons: its layers' stack's first (slot 0), its sound's beside it when it has both (4.4 BugHunt).
    private static Rect ChevronRect(in Lane lane, int slot) =>
        new(4 + lane.Depth * DeckIndent + slot * ChevronStep, TwoLines(lane) ? lane.Top + 4 : lane.Top + (lane.Height - 16) / 2, 16, 16);

    private static double NameLeft(in Lane lane) => 22 + lane.Depth * DeckIndent + (lane.ChevronCount > 1 ? ChevronStep : 0);

    private static int SwitchCount(TimelineRow row) => System.Numerics.BitOperations.PopCount((uint)(row.Offered & AllSwitches));

    private double SwitchesLeft(in Lane lane) => _headerWidth - 4 - SwitchCount(lane.Row) * SwitchSize;

    /// <summary>Where a header's switch is: in a row of its own under the name on a tall lane, else at the right of the name's line.</summary>
    private Rect SwitchRect(in Lane lane, int slot)
    {
        if (TwoLines(lane))
            return new Rect(NameLeft(lane) - 3 + slot * SwitchSize, lane.Top + 23, SwitchSize, SwitchSize);

        var count = SwitchCount(lane.Row);
        return new Rect(_headerWidth - 4 - (count - slot) * SwitchSize, lane.Top + (lane.Height - SwitchSize) / 2, SwitchSize, SwitchSize);
    }

    private void RenderHeaders(double pixelsPerDip)
    {
        _headersDirty = false;
        using var drawing = _headers.RenderOpen();
        var width = _headerWidth;
        if (width <= 0)
            return;

        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            var row = lane.Row;
            drawing.DrawRectangle(lane.Depth == 0 ? _card : _panel, null, new Rect(0, lane.Top, width, lane.Height));
            if (AccentOf(i) is { } accent)
                drawing.DrawRectangle(accent, null, new Rect(0, lane.Top, 3, lane.Height));

            // The deck: a line down from the parent's chevron to each lane of its stack. The sound's stack, under the
            // layers' when a row has both, hangs from the second chevron, its line passing beside the layers' lanes.
            if (lane.Depth > 0)
            {
                var x = DeckX(lane.Depth) + (lane.SecondDeck ? ChevronStep : 0) + 0.5;
                var middle = lane.Top + Math.Min(lane.Height / 2, 11);
                var top = lane.SecondDeck && lane.FirstChild && lane.Parent >= 0 ? _lanes[lane.Parent].Bottom : lane.Top - LaneGap;
                drawing.DrawLine(_deckPen, new Point(x, top), new Point(x, lane.LastChild ? middle : lane.Bottom + LaneGap));
                drawing.DrawLine(_deckPen, new Point(x, middle), new Point(x + 7, middle));
            }

            if (lane.Stacks != 0 && lane.Height >= 12)
            {
                // A chevron for each stack the row has; the sound's in its own colour when there are two.
                var slot = 0;
                foreach (var stack in StackOrder)
                {
                    if (!lane.Has(stack))
                        continue;

                    var tint = stack == ChildStack.Audio && lane.ChevronCount > 1 ? SoundChevron : _muted;
                    var chevron = Word(IsExpanded(row, stack) ? ChevronDown : ChevronRight, tint, 9, icon: true, pixelsPerDip);
                    var at = ChevronRect(lane, slot++);
                    drawing.DrawText(chevron, new Point(at.Left + (at.Width - chevron.Width) / 2, at.Top + (at.Height - chevron.Height) / 2));
                }
            }

            if (lane.Height >= 11)
            {
                var name = NameOf(row, lane.Detailed ? 12 : 9, lane.Mini ? _muted : _text, pixelsPerDip);
                var left = NameLeft(lane);
                var room = (lane.Detailed && !TwoLines(lane) ? SwitchesLeft(lane) : width - 4) - left;
                if (room > 8)
                {
                    if (name.MaxTextWidth != room)
                        name.MaxTextWidth = room;
                    var y = TwoLines(lane) ? lane.Top + 5 : lane.Top + (lane.Height - name.Height) / 2;
                    drawing.DrawText(name, new Point(left, y));
                }
            }

            if (lane.Detailed)
            {
                var slot = 0;
                foreach (var flag in SwitchOrder)
                {
                    if ((row.Offered & flag) == 0)
                        continue;

                    var at = SwitchRect(lane, slot++);
                    var on = (row.State & flag) != 0;
                    var glyph = SwitchGlyph(flag, on, pixelsPerDip);
                    if (on)
                        drawing.DrawRoundedRectangle(SwitchOnFill, null, at, 3, 3);
                    drawing.DrawText(glyph, new Point(at.Left + (at.Width - glyph.Width) / 2, at.Top + (at.Height - glyph.Height) / 2));
                }
            }

            // The time area's ends beside it (the wells' lit edge, as the old bars had at theirs).
            if (_edgeWidth > 0 && lane.Height > 2 * _edgeWidth)
            {
                drawing.DrawLine(_edgePen, new Point(width + _edgeWidth / 2, lane.Top), new Point(width + _edgeWidth / 2, lane.Bottom));
                drawing.DrawLine(_lowerEdgePen, new Point(ActualWidth - _edgeWidth / 2, lane.Top), new Point(ActualWidth - _edgeWidth / 2, lane.Bottom));
            }
        }
    }

    // A lane's stripe: its row's accent, else the nearest parent's.
    private Brush? AccentOf(int lane)
    {
        for (var i = lane; i >= 0; i = _lanes[i].Parent)
        {
            if (_lanes[i].Row.Accent is { } accent)
                return accent;
        }

        return null;
    }

    private static FormattedText NameOf(TimelineRow row, double size, Brush brush, double pixelsPerDip)
    {
        var name = row.NameText;
        if (name is null || row.NameTextOf != row.Name || row.NameTextSize != size || !ReferenceEquals(row.NameTextBrush, brush))
        {
            name = new FormattedText(row.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, size, brush, pixelsPerDip)
            {
                MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            };
            (row.NameText, row.NameTextOf, row.NameTextSize, row.NameTextBrush) = (name, row.Name, size, brush);
        }

        return name;
    }

    private FormattedText SwitchGlyph(RowSwitches flag, bool on, double pixelsPerDip)
    {
        var text = flag switch
        {
            RowSwitches.Hidden => on ? "" : "",
            RowSwitches.Muted => on ? "" : "",
            RowSwitches.Locked => on ? "" : "",
            RowSwitches.Voice => "",
            _ => "S",
        };
        var brush = !on ? _muted
            : flag is RowSwitches.Hidden or RowSwitches.Muted or RowSwitches.Locked ? _warning
            : flag == RowSwitches.Soloed ? _gold
            : _accent;
        return flag == RowSwitches.Soloed ? Word(text, brush, 11, icon: false, pixelsPerDip) : Word(text, brush, 11, icon: true, pixelsPerDip);
    }

    /// <summary>A glyph or a short word, laid out once for its colour and size.</summary>
    private FormattedText Word(string text, Brush brush, double size, bool icon, double pixelsPerDip)
    {
        var key = (text, brush, size, icon);
        if (!_words.TryGetValue(key, out var word))
        {
            word = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, icon ? IconFace : StrongFace, size, brush, pixelsPerDip);
            _words[key] = word;
        }

        return word;
    }

    /// <summary>Forgets every word laid out (the theme's colours or the screen's DPI changed): they are laid out again as drawn.</summary>
    private void ForgetWords()
    {
        _words.Clear();
        _emptyHint = null;
        _dropWords = null;
        _dropWordsOf = null;
        foreach (var lane in _lanes)
        {
            lane.Row.NameText = null;
            foreach (var block in lane.Row.Blocks)
            {
                block.Label = null;
                block.BadgeText = null;
            }
        }
    }

    // ----- The overlay: what is being dragged, hover handles, the drop mark -----

    private void RenderOverlay(DpiScale dpi)
    {
        _overlayDirty = false;
        WaveTiles.Refill();
        using var drawing = _overlay.RenderOpen();
        var scale = PixelsPerSecond;
        var (from, to) = (ViewOffsetX - _originX, ViewOffsetX + TimeAreaWidth - _originX);
        if (to <= from)
            return;

        // The block being dragged (and, rolled, the one after it), drawn as it is now; half seen while it is over another
        // row, which shows where it would go there: blue where it may, red where it may not.
        if (_drag is { } drag && LaneOf(drag.Row) is >= 0 and var dragLane)
        {
            // What it carries along first, each in its own lane (a collapsed one draws it plainly), every move (4.4
            // BugHunt): moved with it, or trimmed with its edge (the file's frames staying put, as the parent's do); a child
            // the trim leaves outside it faint and dashed, coming loose; outlined red where the move cannot take it.
            var seamGrowth = drag.Seam is { } growing ? growing.Seconds * scale : 0;
            foreach (var child in _coupled)
            {
                var childIndex = LaneOf(child.Row);
                if (childIndex < 0)
                    continue;

                var childLane = _lanes[childIndex];
                var childRect = BlockRect(childLane, child.Block, _originX);
                if (childRect.Right < from || childRect.Left > to)
                    continue;

                var childContent = child.How switch
                {
                    Follows.Start when !child.Detaching => child.Left0 - _originX,
                    Follows.SeamEdge when drag.Seam is { AtStart: true } => child.Left0 - seamGrowth - _originX,
                    _ => childRect.Left,
                };
                if (child.Detaching)
                    drawing.PushOpacity(0.6);
                DrawBlock(drawing, childLane, child.Block, childRect, childContent, null, from, to, dpi);
                if (childLane.Detailed)
                    DrawLabel(drawing, child.Block, childRect, from, dpi.PixelsPerDip);
                if (child.Detaching)
                {
                    drawing.Pop();
                    drawing.DrawRoundedRectangle(null, DetachPen, new Rect(childRect.Left + 0.5, childRect.Top + 0.5, Math.Max(childRect.Width - 1, 1), Math.Max(childRect.Height - 1, 1)), 3, 3);
                }
                else if (!drag.FamilyFits)
                {
                    drawing.DrawRoundedRectangle(null, RefusedOutline, childRect, 3, 3);
                }
            }

            var lane = _lanes[dragLane];
            var block = drag.Block;
            var rect = BlockRect(lane, block, _originX);

            // At a seam's start edge the clip's start stays at the cut and its file slides in behind it (shown again) or
            // out (folded more).
            var contentLeft = drag.Seam is { AtStart: true } ? drag.Left - seamGrowth - _originX : drag.Kind switch
            {
                BlockEdit.TrimStart when drag.Seam is null => drag.Left - _originX,
                BlockEdit.Slip => rect.Left + drag.Slip,
                _ => rect.Left,
            };

            var away = drag.Target is not null;
            if (away)
                drawing.PushOpacity(0.5);
            DrawBlock(drawing, lane, block, rect, contentLeft, null, from, to, dpi);
            DrawLabel(drawing, block, rect, from, dpi.PixelsPerDip);
            if (block.CrossfadeIn > 0.01 && scale > 0)
                DrawCrossfade(drawing, lane, block, scale, _originX);
            if (away)
                drawing.Pop();
            if (!drag.FamilyFits)
                drawing.DrawRoundedRectangle(null, RefusedOutline, rect, 3, 3);

            // An edge at a seam: how much of the fold it shows again, or folds in, and how much stays folded.
            if (drag.Seam is { } grip && Math.Abs(grip.Seconds) > 1e-4)
            {
                var folded = Math.Max(grip.Mark.FoldedSeconds - grip.Seconds, 0);
                var words = grip.Seconds > 0
                    ? folded < 0.005
                        ? string.Create(CultureInfo.InvariantCulture, $"Shows all {grip.Mark.FoldedSeconds:0.00} s of the folded silence again")
                        : string.Create(CultureInfo.InvariantCulture, $"Shows {grip.Seconds:0.00} s of the folded silence again ({folded:0.00} s stay folded)")
                    : string.Create(CultureInfo.InvariantCulture, $"Folds {-grip.Seconds:0.00} s more of the clip in ({folded:0.00} s folded)");
                DrawReadout(drawing, words, grip.AtStart ? rect.Left : rect.Right, rect.Top + 1, toRight: !grip.AtStart, from, to, dpi.PixelsPerDip);
            }

            if (drag.Neighbor is { } next)
            {
                var nextRect = BlockRect(lane, next, _originX);
                DrawBlock(drawing, lane, next, nextRect, drag.NeighborLeft - _originX, null, from, to, dpi);
                DrawLabel(drawing, next, nextRect, from, dpi.PixelsPerDip);
            }

            if (drag.Target is { } target && LaneOf(target) is >= 0 and var targetLane)
            {
                var over = _lanes[targetLane];
                var ghost = new Rect(block.Left - _originX, over.Top + 1, Math.Max(block.Width, 3), Math.Max(over.Height - 2, 1));
                drawing.DrawRoundedRectangle(drag.Fits ? GhostFill : RefusedFill, drag.Fits ? GhostOutline : RefusedOutline, ghost, 3, 3);
            }
        }

        if (_fadeDrag is { } fadeDrag && LaneOf(fadeDrag.Row) is >= 0 and var fadeLane)
        {
            var rect = BlockRect(_lanes[fadeLane], fadeDrag.Block, _originX);
            DrawBlock(drawing, _lanes[fadeLane], fadeDrag.Block, rect, rect.Left, null, from, to, dpi);
            DrawLabel(drawing, fadeDrag.Block, rect, from, dpi.PixelsPerDip);
            if (fadeDrag.Block.Fades is { } fades)
                DrawFadeReadout(drawing, rect, fadeDrag.In, fades, from, to, dpi.PixelsPerDip);
        }

        if (_gainDrag is { } gainDrag && LaneOf(gainDrag.Row) is >= 0 and var gainLane)
        {
            var rect = BlockRect(_lanes[gainLane], gainDrag.Block, _originX);
            DrawBlock(drawing, _lanes[gainLane], gainDrag.Block, rect, rect.Left, null, from, to, dpi);
            DrawLabel(drawing, gainDrag.Block, rect, from, dpi.PixelsPerDip);
            if (gainDrag.Block.Gain is { } gain)
            {
                var words = string.Create(CultureInfo.InvariantCulture, $"Gain: {gain.GainDb:+0.0;-0.0;0.0} dB");
                var middle = Math.Clamp((rect.Left + rect.Right) / 2, from, to);
                DrawReadout(drawing, words, middle, GainY(rect, gain.GainDb) - 20, toRight: true, from, to, dpi.PixelsPerDip);
            }
        }

        if (_splitDrag is { } split && LaneOf(split.Row) is >= 0 and var splitLane)
        {
            var rect = BlockRect(_lanes[splitLane], split.Block, _originX);
            DrawBlock(drawing, _lanes[splitLane], split.Block, rect, rect.Left, (split.Lead, split.Tail), from, to, dpi);
            DrawLabel(drawing, split.Block, rect, from, dpi.PixelsPerDip);
            var edge = split.AtLead ? rect.Left - split.Lead * scale : rect.Right + split.Tail * scale;
            DrawReadout(drawing, SplitWords(split.AtLead, split.AtLead ? split.Lead : split.Tail), edge, rect.Top + rect.Height * SoundBandTop - 18, toRight: !split.AtLead,
                from, to, dpi.PixelsPerDip);
        }

        // The fade handles of the block the pointer is over, at its corners where it has no fade yet.
        if (!IsDragging && _hovered is { } hovered && hovered.Block is { Fades: { } hoverFades, FadeEdit: not null } && LaneOf(hovered.Row) is >= 0 and var hoverLane
            && _lanes[hoverLane].Detailed)
        {
            var rect = BlockRect(_lanes[hoverLane], hovered.Block, _originX);
            var (fadeIn, fadeOut) = FadeSpan(rect, hoverFades.In, hoverFades.Out, scale);
            if (fadeIn <= 0.5)
                DrawFadeHandle(drawing, rect, rect.Left);
            if (fadeOut <= 0.5)
                DrawFadeHandle(drawing, rect, rect.Right);
        }

        // The pill that closes the gap the pointer is in (not while something is dragged: the gaps are where the blocks were).
        if (!IsDragging && _hoveredGap is { } hoveredGap && LaneOf(hoveredGap.Row) is >= 0 and var gapLane && _lanes[gapLane].Detailed
            && hoveredGap.Index < hoveredGap.Row.Gaps.Count)
        {
            var (left, right) = hoveredGap.Row.Gaps[hoveredGap.Index];
            DrawGapPill(drawing, _lanes[gapLane], left - _originX, right - _originX);
        }

        if (_dropMark is { } mark && scale > 0)
            DrawDropMark(drawing, mark, scale, from, to, dpi.PixelsPerDip);
    }

    // A J/L cut being dragged, in words.
    private static string SplitWords(bool lead, double seconds) => (lead, seconds >= 0) switch
    {
        (true, true) => string.Create(CultureInfo.InvariantCulture, $"J Cut: the sound starts {seconds:0.00} s before the picture"),
        (true, false) => string.Create(CultureInfo.InvariantCulture, $"The sound starts {-seconds:0.00} s after the picture"),
        (false, true) => string.Create(CultureInfo.InvariantCulture, $"L Cut: the sound lasts {seconds:0.00} s past the picture"),
        _ => string.Create(CultureInfo.InvariantCulture, $"The sound stops {-seconds:0.00} s before the picture ends"),
    };

    /// <summary>Where dropped files would land: a bar at the moment on the row (or a band for a new row under them all), and how many.</summary>
    private void DrawDropMark(DrawingContext drawing, DropMark mark, double scale, double from, double to, double pixelsPerDip)
    {
        var x = mark.Seconds * scale - _originX;
        double top, height;
        if (mark.Row is { } row && LaneOf(row) is >= 0 and var index)
        {
            (top, height) = (_lanes[index].Top, _lanes[index].Height);
        }
        else
        {
            (top, height) = (_extent, 28);
            drawing.DrawRoundedRectangle(GhostFill, GhostOutline, new Rect(from + 2, top + 1, Math.Max(to - from - 4, 1), height - 2), 3, 3);
        }

        drawing.DrawRectangle(DropMarkBrush, null, new Rect(x - 1.5, top, 3, height));
        if (mark.Count <= 1 && !mark.Stack)
            return;

        var words = mark.Stack
            ? string.Create(CultureInfo.InvariantCulture, $"{mark.Count} on tracks of their own")
            : string.Create(CultureInfo.InvariantCulture, $"{mark.Count} one after another");
        var text = _dropWords;
        if (text is null || _dropWordsOf != words)
        {
            text = new FormattedText(words, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, pixelsPerDip);
            (_dropWords, _dropWordsOf) = (text, words);
        }

        var box = new Rect(Math.Clamp(x + 6, from + 2, Math.Max(to - text.Width - 10, from + 2)), top + 2, text.Width + 8, text.Height + 2);
        drawing.DrawRoundedRectangle(ReadoutFill, null, box, 3, 3);
        drawing.DrawText(text, new Point(box.Left + 4, box.Top + 1));
    }

    // ----- The lines that slide -----

    /// <summary>
    /// Places the playhead or the skim line at a moment (NaN: none): its visual slides, and is drawn again only when the
    /// control's height changed. Hidden while the moment is outside the view.
    /// </summary>
    private void PlaceLine(DrawingVisual line, double seconds, ref double drawnHeight, bool playhead)
    {
        var scale = PixelsPerSecond;
        var x = _headerWidth + seconds * scale - ViewOffsetX;
        var shown = double.IsFinite(seconds) && scale > 0 && x >= _headerWidth - 6 && x <= ActualWidth + 6;
        var height = Math.Max(ActualHeight, 4);
        if (shown && height != drawnHeight)
        {
            drawnHeight = height;
            using var drawing = line.RenderOpen();
            if (playhead)
            {
                drawing.DrawRectangle(PlayheadBrush, null, new Rect(-0.75, 0, 1.5, height));
                drawing.DrawGeometry(PlayheadBrush, null, PlayheadCap);
            }
            else
            {
                drawing.DrawRectangle(SkimBrush, null, new Rect(-0.5, 0, 1, height));
            }
        }

        line.Opacity = shown ? 1 : 0;
        if (shown)
            line.Offset = new Vector(x, 0);
    }

    // ----- Made once -----

    private static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue) => FrozenBrush(Color.FromArgb(alpha, red, green, blue));

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private static DashStyle Frozen(DashStyle dashes)
    {
        dashes.Freeze();
        return dashes;
    }

    private static Geometry FrozenTriangle()
    {
        var cap = new StreamGeometry();
        using (var path = cap.Open())
        {
            path.BeginFigure(new Point(-5, 0), isFilled: true, isClosed: true);
            path.LineTo(new Point(5, 0), false, false);
            path.LineTo(new Point(0, 6), false, false);
        }

        cap.Freeze();
        return cap;
    }

    /// <summary>
    /// Waveforms as frozen tiles (4.4): a sound's peaks drawn once per zoom step (every doubling of how many pixels a second
    /// of the sound takes) in stretches 512 pixels long, at unit height, so a track's height, its gain and a mini-stack's
    /// thin lanes never make one again, and a pan or a drag only places them. At most 8 are made a render (crossing a zoom
    /// step never stalls a frame; the step below stands in meanwhile), and at most about 256 are kept, those of sounds no
    /// longer held going with them. Used on the UI thread only.
    /// </summary>
    private static class WaveTiles
    {
        public const double TilePixels = 512;
        public const int LeastBucket = -12, MostBucket = 16;
        private const int CacheCap = 256, BuildsPerRender = 8, MostColumns = 1024;

        private sealed class Tile(StreamGeometry geometry, long used)
        {
            public StreamGeometry Geometry { get; } = geometry;

            public long Used { get; set; } = used;
        }

        private static readonly ConditionalWeakTable<WaveformData, Dictionary<long, Tile>> Tiles = new();
        private static readonly List<long> Stale = [];
        private static int _count, _budget;
        private static long _stamp;

        public static void BeginRender()
        {
            _stamp++;
            _budget = BuildsPerRender;
        }

        /// <summary>More may be made (for the overlay, drawn on its own between the rows' renders), without ageing what was drawn.</summary>
        public static void Refill() => _budget = BuildsPerRender;

        /// <summary>Lets every tile go (the screen's DPI changed: a tile has a column for each device pixel), to be made again as drawn.</summary>
        public static void Clear()
        {
            foreach (var (_, tiles) in Tiles)
                tiles.Clear();
            _count = 0;
        }

        /// <summary>A tile already made; null when it is not.</summary>
        public static StreamGeometry? Peek(WaveformData data, int bucket, long tile) =>
            Tiles.TryGetValue(data, out var tiles) && tiles.TryGetValue(Key(bucket, tile), out var kept) ? kept.Geometry : null;

        /// <summary>A tile: the one made before, or made now while this render may make more; null when it may not.</summary>
        public static StreamGeometry? Get(WaveformData data, int bucket, long tile, double dpiScale)
        {
            var tiles = Tiles.GetOrCreateValue(data);
            var key = Key(bucket, tile);
            if (tiles.TryGetValue(key, out var kept))
            {
                kept.Used = _stamp;
                return kept.Geometry;
            }

            if (_budget <= 0)
                return null;

            _budget--;
            var made = new Tile(Build(data, bucket, tile, dpiScale), _stamp);
            tiles[key] = made;
            if (++_count > CacheCap)
                Trim();
            return made.Geometry;
        }

        private static long Key(int bucket, long tile) => ((long)bucket << 40) ^ tile;

        // Over the cap: the tiles drawn neither in this render nor the one before go.
        private static void Trim()
        {
            _count = 0;
            foreach (var (_, tiles) in Tiles)
            {
                Stale.Clear();
                foreach (var (key, tile) in tiles)
                {
                    if (tile.Used < _stamp - 1)
                        Stale.Add(key);
                }

                foreach (var key in Stale)
                    tiles.Remove(key);
                _count += tiles.Count;
            }
        }

        // One tile: the sound's seconds [tile, tile + 1) × 512 / 2^bucket as one filled outline, x in pixels at 2^bucket a
        // second, y from -1 (loudest up) to 1, a column for each device pixel (or each pair, zoomed in).
        private static StreamGeometry Build(WaveformData data, int bucket, long tile, double dpiScale)
        {
            var pixelsPerSecond = Math.ScaleB(1.0, bucket);
            var tileSeconds = TilePixels / pixelsPerSecond;
            var from = tile * tileSeconds;
            var to = Math.Min(from + tileSeconds, data.Seconds);
            var width = (to - from) * pixelsPerSecond;
            var geometry = new StreamGeometry();
            if (width > 0)
            {
                var columns = (int)Math.Clamp(Math.Ceiling(Math.Min(width * Math.Max(dpiScale, 1), (to - from) * data.PairsPerSecond)), 1, MostColumns);
                Span<float> lows = stackalloc float[columns];
                Span<float> highs = stackalloc float[columns];
                data.Envelope(from, to, lows, highs);
                var step = width / columns;
                using var path = geometry.Open();
                path.BeginFigure(new Point(0, -highs[0]), isFilled: true, isClosed: true);
                for (var c = 0; c < columns; c++)
                    path.LineTo(new Point((c + 0.5) * step, -highs[c]), false, false);
                path.LineTo(new Point(width, -highs[columns - 1]), false, false);
                path.LineTo(new Point(width, -lows[columns - 1]), false, false);
                for (var c = columns - 1; c >= 0; c--)
                    path.LineTo(new Point((c + 0.5) * step, -lows[c]), false, false);
                path.LineTo(new Point(0, -lows[0]), false, false);
            }

            geometry.Freeze();
            return geometry;
        }
    }
}
