using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

// Unified timeline rendering: row content (wells, blocks, thumbnails, waveforms, fades, gain lines, keys, pills, seams, end shade),
// block labels, headers, overlay (dragged block, hover handles, drop mark), and the translated playhead and skim line. All code-built
// resources are frozen; per-block geometry is cached while the block is unchanged, and waveforms are frozen per-zoom-step tiles
// (WaveTiles), so redraws allocate almost nothing.
public sealed partial class UnifiedTimelineControl
{
    // Drawing origin quantum in layout pixels: redraws at the same place reuse identical numbers, so cached tile placements hit.
    private const double OriginStep = 1024;

    // J/L audio band top as a fraction of block height (other audio spans the full block).
    private const double SoundBandTop = 0.6;

    // Waveform opacity over thumbnails (frames show through).
    private const double WaveOverPictures = 0.5;

    // Header switch width and layout order.
    private const double SwitchSize = 18;
    private static readonly RowSwitches[] SwitchOrder = [RowSwitches.Hidden, RowSwitches.Muted, RowSwitches.Soloed, RowSwitches.Voice, RowSwitches.Locked];
    private const RowSwitches AllSwitches = RowSwitches.Hidden | RowSwitches.Muted | RowSwitches.Soloed | RowSwitches.Voice | RowSwitches.Locked;

    // Segoe MDL2 Assets: ChevronRight (E76C), ChevronDown (E70D), View (E890), Hide (ED1A), Volume (E767), Mute (E74F),
    // Microphone (E720), Lock (E72E), Unlock (E785); Switch (E8AB) for the Hide pill's two arrows.
    private const string ChevronRight = Glyphs.ChevronRight, ChevronDown = Glyphs.ChevronDown;
    private const string SwitchGlyphText = Glyphs.Switch;

    // Offset of the audio-stack chevron from the visual-stack chevron.
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

    // Drawn layout-pixel range and the drawing's x = 0 within it.
    private double _originX, _renderedFrom, _renderedTo = double.NegativeInfinity;

    // Heights the playhead and skim line drawings were built for.
    private double _playheadHeight = -1, _skimHeight = -1;

    // Waveform tiles deferred by a budget-limited render; built at the next idle render.
    private bool _tilesPending;
    private Action? _drawPendingTiles;

    // Zoom-time waveform state: last drawn scale; moving flag (changed < WaveSettleMs ago, so adjacent-step tiles stand in); whether a
    // stand-in or gap was drawn; and the settle timer (created on first zoom) that redraws crisp tiles with a larger one-render budget.
    private const int WaveSettleMs = 100;
    private double _waveScale;
    private bool _waveMoving, _waveOwed, _waveSettling;
    private DispatcherTimer? _waveSettle;

    // Scratch buffer of finer-step tiles composing one stand-in (at most 2^StandInOctaves).
    private readonly StreamGeometry?[] _finer = new StreamGeometry?[1 << WaveTiles.StandInOctaves];

    // File-drop preview target (ShowDropMark).
    private sealed record DropMark(TimelineRow? Row, double Seconds, int Count, bool Stack);

    private DropMark? _dropMark;
    private FormattedText? _dropWords;
    private string? _dropWordsOf;

    // New-track insertion bar label, formatted once per DPI.
    private FormattedText? _newTrackWords;
    private const string NewTrackWords = "+ New Video Track";
    private FormattedText? _emptyHint;

    // FormattedText cache keyed by text, colour, size and typeface.
    private readonly Dictionary<(string Text, Brush Brush, double Size, bool Icon), FormattedText> _words = [];

    // Frozen waveform tile transforms, reused while unchanged (see OriginStep).
    private readonly Dictionary<(double, double, double, double), MatrixTransform> _placements = [];

    // Frozen translucent brush cache keyed by colour and opacity.
    private static readonly Dictionary<(Brush, int), Brush> TranslucentBrushes = [];

    protected override void OnRender(DrawingContext drawing)
    {
        // Background panel: visible between lanes and makes the whole control hit-testable.
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

        // Deferred tiles: schedule another render after input and layout.
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

    // ----- Rows -----

    private void RenderContent(DpiScale dpi)
    {
        _contentDirty = false;
        var (width, scale, view) = (TimeAreaWidth, PixelsPerSecond, ViewOffsetX);

        // Over-draw half a view on each side so pans within it only translate.
        var margin = Math.Max(width / 2, 256);
        (_renderedFrom, _renderedTo) = (view - margin, view + width + margin);
        var origin = Math.Floor(_renderedFrom / OriginStep) * OriginStep;
        if (origin != _originX)
        {
            // The overlay shares the origin, so redraw it with the rows.
            _originX = origin;
            _overlayDirty = true;
        }

        // Scale changed since last draw (wheel glide, zoom slider, pinch, splitter, tab): moving until WaveSettleMs after the last change.
        // The first render is never a move.
        if (_waveScale > 0 && scale != _waveScale)
            RestartWaveSettle();
        _waveScale = scale;
        _waveMoving = _waveSettle is { IsEnabled: true };
        _waveOwed = false;
        WaveTiles.BeginRender(_waveSettling && !_waveMoving ? WaveTiles.SettleBuilds : WaveTiles.BuildsPerRender);
        _waveSettling = false;

        using (var drawing = _content.RenderOpen())
        {
            // Fill below the last track with the well colour down to the toolbar.
            if (width > 0 && ActualHeight > _extent)
                drawing.DrawRectangle(_inset, null, new Rect(_renderedFrom - _originX, _extent, _renderedTo - _renderedFrom, ActualHeight - _extent));

            if (width > 0 && _lanes.Count > 0)
            {
                var (from, to) = (_renderedFrom - _originX, _renderedTo - _originX);

                // Lane wells with the theme's sunken edge along top and bottom (ends are drawn beside the static headers).
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

                // Past-end shade (drop/drag target area).
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

    // ----- Slide rendering: unfold/refold and seam-edge drags -----

    // Slide parameters in layout pixels: content starting at or after At is offset by Dx; on unfold, the restored range (from WipeFrom,
    // before At) is wiped in up to WipeTo; on refold, GhostFrom..GhostTo is shaded. Applies to Rows (keys; null = all unlocked).
    private readonly record struct Shift(double At, double Dx, double WipeFrom, double WipeTo, double GhostFrom, double GhostTo, HashSet<object>? Rows)
    {
        public bool Wipes => !double.IsNaN(WipeFrom);

        public bool Ghosts => !double.IsNaN(GhostFrom) && GhostTo - GhostFrom > 0.5;
    }

    /// <summary>Active slide (fold animation or seam-edge drag), or null.</summary>
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

    // Lane participates if its row is listed, or (none listed) its track is unlocked; the blur background never slides.
    private static bool Applies(in Lane lane, Shift shift) =>
        lane.Row.Kind != TimelineRowKind.Background
        && (shift.Rows is { } rows ? rows.Contains(lane.Row.Key) : (lane.Row.State & RowSwitches.Locked) == 0);

    // Slide disposition for content at layout x: offset by Dx, wiped in (restored range), or unchanged.
    private enum Slid { Still, Moved, Wiped }

    private static Slid SlidOf(Shift? shift, double left) =>
        shift is not { } s ? Slid.Still
        : left >= s.At ? Slid.Moved
        : s.Wipes && left >= s.WipeFrom - 0.5 ? Slid.Wiped
        : Slid.Still;

    private void DrawLane(DrawingContext drawing, in Lane lane, double from, double to, DpiScale dpi, Shift? shift, Transform? move)
    {
        // Zero-height (hidden) lanes draw nothing.
        if (lane.Height < 0.5)
            return;

        var row = lane.Row;
        var scale = PixelsPerSecond;
        if (row.Kind == TimelineRowKind.Empty)
            drawing.DrawRectangle(EmptyRowFill, EmptyRowOutline, new Rect(from, lane.Top + 3, to - from, Math.Max(lane.Height - 6, 1)));

        // Unfold: wipe in left to right within the restored range.
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

                // Offset via one shared transform so cached block geometry (outline, keys, fades) is reused.
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

        // Collapsed audio stack summary bar: all unmuted streams in one waveform. Dragged content draws its bar on the overlay at its live
        // position, since rows are drawn once at drag start and not during the drag.
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
            // Crossfade box centred on the cut, as long as the crossfade.
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

        // Unfolded dead-air ranges: shaded, edged, with a Hide pill on full-size lanes.
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

        // Refold in progress: shade the closing range.
        if (shift is { Ghosts: true } ghost && ghost.GhostTo - _originX >= from && ghost.GhostFrom - _originX <= to)
            drawing.DrawRectangle(RevealGhost, null, new Rect(ghost.GhostFrom - _originX, lane.Top + 1, ghost.GhostTo - ghost.GhostFrom, Math.Max(lane.Height - 2, 1)));

        var grip = _drag is { Seam: { } held } dragging && ReferenceEquals(dragging.Row, row) ? held : null;
        var seams = row.Seams;
        for (var i = 0; i < seams.Count; i++)
        {
            var x = seams[i].Seconds * scale;

            // Seam under an edge drag: follows the earlier clip's end (fold head), stays at the later clip's start (fold tail); hidden
            // once fully unfolded.
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

    /// <summary>
    /// Whether a lane draws thumbnails and labels: full-detail lanes, and mini-stack lanes once their visible height reaches the full-detail
    /// height (22 DIPs), so expanding or collapsing stacks reveal pictures smoothly; never the collapsed audio summary bar.
    /// </summary>
    private static bool ShowsPictures(in Lane lane) => !lane.Summary && lane.Height >= FullDetailHeight;

    /// <summary>Test seam: the time area's transform (the rows are only ever shifted by it, never stretched).</summary>
    internal Transform? RowsTransform => _scroller.Transform;

    /// <summary>Test seam: a row's stack openness set mid-animation (heading open) and laid out at once, as a frame of the animation would.</summary>
    internal void SetOpennessForTests(object rowKey, ChildStack stack, double current)
    {
        _expansion[(rowKey, stack)] = new Expansion { Current = current, Target = 1.0 };
        Relayout();
    }

    /// <summary>Test seam: whether the lane of the row with this key draws thumbnails and labels now (ShowsPictures); null when not laid.</summary>
    internal bool? LaneShowsPictures(object key)
    {
        foreach (var lane in _lanes)
        {
            if (Equals(lane.Row.Key, key))
                return ShowsPictures(lane);
        }

        return null;
    }

    /// <summary>Test seam: the laid height of the lane of the row with this key; NaN when not laid.</summary>
    internal double LaneHeightOf(object key)
    {
        foreach (var lane in _lanes)
        {
            if (Equals(lane.Row.Key, key))
                return lane.Height;
        }

        return double.NaN;
    }

    // True if a row's audio feeds its collapsed stack's summary bar (unmuted audio-stack lane).
    private static bool HeardInBar(TimelineRow row) => row.Stack == ChildStack.Audio && (row.State & RowSwitches.Muted) == 0;

    /// <summary>
    /// Draws a block's audio into its collapsed stack's summary lane (<paramref name="bar"/>), clipped to the block and laid from
    /// <paramref name="soundLeft"/> (media range start: left edge, or the pre-trim position during a start trim).
    /// </summary>
    private void DrawBarWave(DrawingContext drawing, in Lane bar, TimelineBlock block, double soundLeft, double from, double to, double scale, DpiScale dpi)
    {
        var rect = BlockRect(bar, block, _originX);
        if (rect.Right < from || rect.Left > to || (block.SoundWindow ?? WindowOf(block, rect.Width, scale)) is not { } sound)
            return;
        DrawWave(drawing, sound, soundLeft, new Rect(rect.Left, rect.Top + 1, rect.Width, Math.Max(rect.Height - 2, 1)), from, to, scale, 0.7, dpi.DpiScaleX);
    }

    /// <summary>
    /// Summary-lane index under the row's parent for an audible audio-stack lane; -1 when the stack is open, the lane is muted, or there
    /// is no audio stack.
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

    // Unfolded range: faint shade, dashed lines at both ends, Hide pill on full-size lanes when wide enough.
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
    /// Hide pill rect for a range [<paramref name="left"/>, <paramref name="right"/>] (same x space): centred, below the fade-handle band;
    /// arrows plus "Hide" when wide enough (<paramref name="words"/>), arrows only when narrower, Empty below that.
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
    /// Block rect with drawing x = 0 at layout <paramref name="originX"/>: fills the lane when Top and Height are 0, else its Top/Height
    /// scaled to the lane height.
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
    /// Draws a block: fill, thumbnails and audio laid from <paramref name="contentLeft"/> (media range start: left edge, pre-trim position
    /// during a start trim, or slipped), and on full-size lanes gain line, fades, keys and badge; outline when selected. J/L audio extends
    /// past the edges in the lower band (<paramref name="splitNow"/> overrides during a J/L drag). Stack audio is laid from
    /// <paramref name="stackLeft"/> when given (slips do not move lanes), else contentLeft.
    /// </summary>
    private void DrawBlock(DrawingContext drawing, in Lane lane, TimelineBlock block, Rect rect, double contentLeft, (double Lead, double Tail)? splitNow,
        double from, double to, DpiScale dpi, double? stackLeft = null)
    {
        var scale = PixelsPerSecond;
        var detailed = lane.Detailed;
        var (lead, tail) = splitNow ?? block.AudioSplit ?? (0.0, 0.0);
        // A video block with StackSound (host-combined audible lanes) draws only that; without it, its own audio (e.g. single-stream files,
        // even beside stacked clips). Clips whose audio lives in the stack carry none on the block (unpacked = silent; unlinked = separate
        // block, AddTrackBlocks), so nothing draws twice. The collapsed summary bar draws its lanes itself (DrawLane).
        var stackSound = lane.Row.Kind == TimelineRowKind.Video && !lane.Summary ? block.StackSound : null;
        var wave = lane.Summary ? null : stackSound ?? block.SoundWindow ?? WindowOf(block, rect.Width, scale);
        var split = detailed && scale > 0 && (lead != 0 || tail != 0);

        // J/L extension: block-coloured tab under the lower band where audio extends past the video.
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

        // Thumbnails by visible height (Pass 5.7.1), not by openness: an opening or closing mini-stack shows its pictures as soon as its
        // lanes are tall enough, never solid bars that flash into thumbnails at the end.
        var pictures = false;
        var showsPictures = detailed || ShowsPictures(lane);
        if (showsPictures && scale > 0 && block.Film is { Frames.Count: > 0 } film)
        {
            DrawFilm(drawing, film, rect, contentLeft, from, to, scale);
            pictures = true;
        }
        else if (showsPictures && block.Frames is { } frames)
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

        // Audio spans the whole block, translucent over thumbnails (no backing shade); J/L audio stays in its lower band. Stack audio is in
        // the block's file time from its left edge (a slip moves thumbnails, not the lanes).
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

            // Fades shaded above their curves with end handles (corner handles when zero length are drawn on the overlay on hover).
            if (block.Fades is { } fades && block.FadeEdit is not null)
                DrawFades(drawing, block, rect, fades, scale);

            if (block.Keys is { Count: > 0 } keys)
            {
                if (block.KeyShape is null || block.KeyShapeRect != rect)
                    (block.KeyShape, block.KeyShapeRect) = (Diamonds(keys, rect, 4.5), rect);
                drawing.DrawGeometry(ThemeManager.KeyFill, KeyOutline, block.KeyShape);
            }

            // Beat grid, laid in media time so beats stay put under start trims.
            if (block.Beats is { Count: > 0 } beats)
                DrawBeats(drawing, beats, rect, contentLeft, from, to);

            // Block switches top-right, badge to their left.
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

    // Beat tick pen: faint, 1 px.
    private static readonly Pen BeatTick = Frozen(new Pen(Solid(140, 0xFF, 0xFF, 0xFF), 1));

    /// <summary>
    /// Draws beat ticks along the block bottom from <paramref name="contentLeft"/>, only within the block and [<paramref name="from"/>,
    /// <paramref name="to"/>]. Beats are sorted, so iteration stops at the first past the right edge.
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

    /// <summary>Converts legacy fraction/pixel peaks (TimelineBlock.Peaks) to a time-based waveform window.</summary>
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

    /// <summary>Thumbnail slot aspect (width / height) bounds: a Cutout's cut frames may be slivers (a killfeed line, a health bar).</summary>
    internal static readonly (double Least, double Most) FilmAspectRange = (0.25, 8.0);

    /// <summary>
    /// Draws thumbnails on the file's time grid: slots one frame-width wide at block height, slot k showing the frame nearest its
    /// centre time. Only visible slots are drawn.
    /// </summary>
    private static void DrawFilm(DrawingContext drawing, FilmWindow film, Rect rect, double contentLeft, double from, double to, double scale)
    {
        var frames = film.Frames;
        var fileScale = scale / (film.Speed > 0 ? film.Speed : 1);
        if (film.SecondsPerFrame <= 0 || fileScale <= 0 || !double.IsFinite(fileScale))
            return;

        // A Cutout's frames come cut to its region (MainWindow.FilmOf, FilmCrop: at least 16 x 16 strip pixels), so the slot takes the
        // region's shape; extreme slivers are held to FilmAspectRange so a slot is never a hairline or a whole row.
        var first = frames[0];
        var shape = first.Width > 0 && first.Height > 0 ? first.Width / first.Height : 16.0 / 9;
        var aspect = Math.Clamp(shape, FilmAspectRange.Least, FilmAspectRange.Most);
        var slot = Math.Max(rect.Height * aspect, 8);

        // Each picture fits its slot at its own shape, centred: a sliver held by FilmAspectRange is letterboxed, never stretched.
        var (pictureWidth, pictureHeight) = rect.Height > 0 && shape >= slot / rect.Height ? (slot, Math.Min(slot / shape, rect.Height)) : (rect.Height * shape, rect.Height);
        var slotSeconds = slot / fileScale;
        var (left, right) = (Math.Max(rect.Left, from), Math.Min(rect.Right, to));
        if (right <= left)
            return;

        // Slots are indexed from the file start's x so they stay fixed in media time.
        var origin = contentLeft - film.MediaFrom * fileScale;
        var firstSlot = (long)Math.Floor((left - origin) / slot);
        var lastSlot = (long)Math.Floor((right - origin) / slot);
        for (var k = firstSlot; k <= lastSlot && k - firstSlot < 400; k++)
        {
            var index = (int)Math.Clamp(Math.Floor((k + 0.5) * slotSeconds / film.SecondsPerFrame), 0, frames.Count - 1);
            drawing.DrawImage(frames[index], new Rect(origin + k * slot + (slot - pictureWidth) / 2, rect.Top + (rect.Height - pictureHeight) / 2, pictureWidth, pictureHeight));
        }
    }

    /// <summary>
    /// Draws a waveform range from frozen zoom-step tiles (WaveTiles), each placed by one transform (horizontal stretch &lt; 2x, height =
    /// band x gain), plus a centre line so silence is visible. Only the visible part of the band is drawn.
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

        // Visible media-time range, clamped to the audio and window bounds.
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

        // Whole tiles overshoot the range (up to a tile width, plus the window's 1 s slack): clip to the band's visible range, since the
        // collapsed summary bar has no block shape to clip it.
        drawing.PushClip(WaveClip(new Rect(Math.Max(left, origin + start * fileScale), band.Top,
            Math.Max(Math.Min(right, origin + end * fileScale) - Math.Max(left, origin + start * fileScale), 0), band.Height)));
        for (var tile = (long)Math.Floor(start / tileSeconds); tile * tileSeconds < end; tile++)
        {
            // While zooming, only existing tiles draw as themselves; when settled, missing tiles are built within the render budget.
            var x = origin + tile * tileSeconds * fileScale;
            var geometry = moving ? WaveTiles.Peek(data, bucket, tile) : WaveTiles.Get(data, bucket, tile, dpiScale);
            if (geometry is null)
            {
                // Not built (per-render budget avoids stalls at step crossings; none while zooming): stretch an adjacent step's tiles.
                if (DrawStandIn(drawing, data, bucket, tile, x, tileSeconds * fileScale, stretch, height, middle, band, fill, start, end, !moving))
                {
                    if (moving)
                        _waveOwed = true;
                    else
                        _tilesPending = true;
                    continue;
                }

                // No stand-in available (newly visible audio): build now within budget, even while zooming.
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

        // Centre line keeps silent audio visible.
        var (lineFrom, lineTo) = (Math.Max(left, origin + start * fileScale), Math.Min(right, origin + end * fileScale));
        if (lineTo > lineFrom)
            drawing.DrawRectangle(fill, null, new Rect(lineFrom, middle - 0.5, lineTo - lineFrom, 1));
    }

    // Frozen waveform clip geometries, reused while the rect is unchanged (identical rows allocate none).
    private readonly Dictionary<Rect, RectangleGeometry> _waveClips = [];

    /// <summary>Cached frozen RectangleGeometry for a waveform clip rect.</summary>
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
    /// Frozen tile transform (stretch, height, offset), cached by value; with <paramref name="keep"/> false (zooming: values never recur)
    /// it is created without caching.
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
    /// Draws a missing tile from adjacent zoom steps: all visible finer tiles composing it (2 or 4), else a coarser containing tile
    /// (clipped), else whichever finer tiles exist. Returns false if none exist. <paramref name="start"/>/<paramref name="end"/> = visible media seconds.
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
    /// Fills _finer with the tiles <paramref name="k"/> octaves finer that compose one tile (null where missing or not visible); returns
    /// (found, needed for [<paramref name="start"/>, <paramref name="end"/>]).
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

    /// <summary>Scale changed: (re)starts the WaveSettleMs settle timer (created lazily on first zoom).</summary>
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
    /// Zoom settled: stops the timer (so it does not root the control) and, if stand-ins were drawn, redraws with up to
    /// <see cref="WaveTiles.SettleBuilds"/> crisp tiles built that render (the rest follow a few per render).
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

    /// <summary>Tiles built since start and currently held (golden wave-tiles-zoom).</summary>
    internal static (int Built, int Kept) WaveTilesForTests => (WaveTiles.Built, WaveTiles.Kept);

    /// <summary>True while zooming (stand-ins drawn, no builds) (golden wave-tiles-zoom).</summary>
    internal bool WaveZoomMoving => _waveMoving || _waveSettle is { IsEnabled: true };

    /// <summary>Runs the settle logic synchronously (golden wave-tiles-zoom has no running dispatcher).</summary>
    internal void SettleWaveZoom() => SettleWaves();

    /// <summary>Translucent variant of a SolidColorBrush (non-solid brushes are returned unchanged).</summary>
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

    /// <summary>Gain line y for a dB value: silence at the bottom, +12 dB at the top.</summary>
    private static double GainY(Rect rect, double gainDb) =>
        rect.Top + 3 + (1 - (Math.Clamp(gainDb, TimelineMetrics.LowestGain, TimelineMetrics.HighestGain) - TimelineMetrics.LowestGain)
            / (TimelineMetrics.HighestGain - TimelineMetrics.LowestGain)) * Math.Max(rect.Height - 6, 1);

    /// <summary>Inverse of the gain line mapping: dB at a y.</summary>
    private static double GainAt(Rect rect, double y) =>
        Math.Round(TimelineMetrics.LowestGain + (1 - Math.Clamp((y - rect.Top - 3) / Math.Max(rect.Height - 6, 1), 0, 1))
            * (TimelineMetrics.HighestGain - TimelineMetrics.LowestGain), 1);

    /// <summary>Fade widths in pixels at the current scale, clamped to the block width.</summary>
    private static (double In, double Out) FadeSpan(Rect rect, double fadeInSeconds, double fadeOutSeconds, double scale)
    {
        scale = Math.Max(scale, 0);
        var fadeIn = Math.Clamp(fadeInSeconds * scale, 0, rect.Width);
        var fadeOut = Math.Clamp(fadeOutSeconds * scale, 0, rect.Width - fadeIn);
        return (fadeIn, fadeOut);
    }

    /// <summary>Draws the gain line: level at the gain, sloping to the bottom across fades, with a centre grip.</summary>
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
    /// Draws fades shaded above their EasingCurves.Evaluate curve (bottom to top, or reverse) with end handles; geometry is cached while
    /// block and fades are unchanged.
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

    // A fade's easing and its drawn level (0 silent .. 1 full, as Ramp draws it) at u of its length: shared with the right-click hit test
    // (FadeWedgeAt), so a click on the shaded part of an eased fade finds the fade (review F18).
    private static Models.Keyframe FadeEasing(Models.TransitionStyle style) => new(0, 0, Models.TransitionStyles.Easing(style));

    private static double FadeLevel(double u, Models.Keyframe easing) => Math.Clamp(Models.EasingCurves.Evaluate(u, easing), -0.1, 1.15);

    // One fade: shade polygon (top edge, then back along the curve) and the curve itself.
    private static void Ramp(StreamGeometryContext shadePath, StreamGeometryContext linePath, Rect rect, double left, double width, bool rising, Models.TransitionStyle style)
    {
        var easing = FadeEasing(style);
        var steps = Math.Clamp((int)(width / 3), 2, 32);
        Span<Point> points = stackalloc Point[33];
        for (var i = 0; i <= steps; i++)
        {
            var u = (double)i / steps;
            points[i] = new Point(left + u * width, rect.Bottom - FadeLevel(rising ? u : 1 - u, easing) * rect.Height);
        }

        shadePath.BeginFigure(new Point(left, rect.Top), isFilled: true, isClosed: true);
        shadePath.LineTo(new Point(left + width, rect.Top), false, false);
        for (var i = steps; i >= 0; i--)
            shadePath.LineTo(points[i], false, false);

        linePath.BeginFigure(points[0], isFilled: false, isClosed: false);
        for (var i = 1; i <= steps; i++)
            linePath.LineTo(points[i], true, true);
    }

    /// <summary>Fade handle position at the fade end, clamped inside the block.</summary>
    private static double HandleX(Rect rect, double x) => Math.Clamp(x, rect.Left + 4, Math.Max(rect.Right - 4, rect.Left + 4));

    private static void DrawFadeHandle(DrawingContext drawing, Rect rect, double x) =>
        drawing.DrawRoundedRectangle(FadeHandleFill, FadeHandleOutline, new Rect(HandleX(rect, x) - 3.5, rect.Top + 1.5, 7, 7), 1.5, 1.5);

    /// <summary>Fade drag readout beside the handle ("Fade In: 0.75 s · Smooth"), kept on-screen.</summary>
    private void DrawFadeReadout(DrawingContext drawing, Rect rect, bool fadeIn,
        (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) fades, double from, double to, double pixelsPerDip)
    {
        var (inWidth, outWidth) = FadeSpan(rect, fades.In, fades.Out, PixelsPerSecond);
        var x = HandleX(rect, fadeIn ? rect.Left + inWidth : rect.Right - outWidth);
        DrawReadout(drawing, FadeWords(fadeIn, fadeIn ? fades.In : fades.Out, fadeIn ? fades.InStyle : fades.OutStyle), x, rect.Top + 1, toRight: fadeIn, from, to, pixelsPerDip);
    }

    private static string FadeWords(bool fadeIn, double seconds, Models.TransitionStyle style) =>
        string.Create(CultureInfo.InvariantCulture, $"Fade {(fadeIn ? "In" : "Out")}: {seconds:0.00} s · {Models.TransitionStyles.Label(style)}");

    /// <summary>Draws a dark label box right (or left) of a point, kept on-screen.</summary>
    private static void DrawReadout(DrawingContext drawing, string words, double x, double top, bool toRight, double from, double to, double pixelsPerDip)
    {
        var text = new FormattedText(words, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, pixelsPerDip);
        var left = toRight ? x + 7 : x - 7 - text.Width - 8;
        left = Math.Clamp(left, from + 2, Math.Max(to - text.Width - 10, from + 2));
        var box = new Rect(left, top, text.Width + 8, text.Height + 2);
        drawing.DrawRoundedRectangle(ReadoutFill, null, box, 3, 3);
        drawing.DrawText(text, new Point(box.Left + 4, box.Top + 1));
    }

    /// <summary>Single frozen diamond geometry for all keys (x from block left), near the bottom.</summary>
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

    // ----- Block switches -----
    // Segoe MDL2 Assets: View (E890), Hide (ED1A), Volume (E767), Mute (E74F), Link (E71B), Unlock (E785).

    private const double BlockSwitchSize = 14, BlockSwitchGap = 2;
    private static readonly Brush SwitchOffFill = Solid(225, 0xC6, 0x28, 0x28);
    private static readonly Brush SwitchFreeFill = Solid(225, 0xEF, 0x8F, 0x00);

    /// <summary>Switch rects in block space (On/Off rightmost, Lock to its left); none when the block is too small.</summary>
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

    /// <summary>Draws the badge text in a small dark tag at the top right when it fits.</summary>
    private static void DrawBadge(DrawingContext drawing, TimelineBlock block, Rect rect, string badge, double pixelsPerDip)
    {
        var text = block.BadgeText ??= new FormattedText(badge, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, StrongFace, 9.5, Brushes.White, pixelsPerDip);
        var box = new Rect(rect.Right - text.Width - 10, rect.Top + 3, text.Width + 6, text.Height + 1);
        if (box.Left < rect.Left + 4)
            return;

        drawing.DrawRoundedRectangle(BadgeFill, null, box, 3, 3);
        drawing.DrawText(text, new Point(box.Left + 3, box.Top + 0.5));
    }

    // Crossfade box: centred on the cut, full lane height.
    private static Rect CrossfadeBox(in Lane lane, TimelineBlock block, double scale, double originX)
    {
        var rect = BlockRect(lane, block, originX);
        var half = block.CrossfadeIn * scale / 2;
        return new Rect(rect.Left - half, rect.Top, Math.Max(half * 2, 6), rect.Height);
    }

    // Remove button at the box's top centre when it fits.
    private static Rect CrossfadeRemove(Rect box) =>
        box.Width >= 18 && box.Height >= 22 ? new Rect(box.Left + box.Width / 2 - 7, box.Top + 2, 14, 14) : Rect.Empty;

    /// <summary>
    /// Draws a crossfade in the standard NLE style: full-height shaded box across the cut, grip edges, two crossing equal-power curves,
    /// and a remove button.
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

    // Close-gap pill centred in the gap: two inward arrows.
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

    // Dead-air seam: dashed vertical line; on full-size lanes a top tab with a zigzag, clickable to unfold.
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

    // Tab sits below the fade-handle band when the lane is tall enough; where it still overlaps a handle, the handle wins hit-testing.
    private static Rect SeamTab(in Lane lane, double x) => new(x - 6, Math.Max(Math.Min(lane.Top + FadeBand + 5, lane.Bottom - 11), lane.Top + 2), 12, 9);

    // ----- Block labels -----

    /// <summary>
    /// Positions block labels in control x, pinned to the view's left while the block is visible (never past its right edge), so
    /// partially scrolled clips stay labelled. Re-run on every pan; only repositions existing text.
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
            // A mini-stack lane is labelled once tall enough (ShowsPictures), so labels grow in with the stack too.
            if ((lane.Mini && !ShowsPictures(lane)) || lane.Height < 14)
                continue;

            if (lane.Row.Kind == TimelineRowKind.Empty)
            {
                var hint = _emptyHint ??= new FormattedText("Drop media here for a new track", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11, _muted, pixelsPerDip);
                drawing.DrawText(hint, new Point(left + 10, lane.Top + (lane.Height - hint.Height) / 2));
                continue;
            }

            // During a slide, labels follow moving blocks; labels in a range being wiped in are deferred.
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

        // Assign only on change: setting text forces relayout.
        var widest = Math.Max(rect.Width - 10, 4);
        if (label.MaxTextWidth != widest)
            label.MaxTextWidth = widest;

        var x = Math.Max(rect.Left, viewLeft) + 5;
        x = Math.Max(Math.Min(x, rect.Right - 5 - label.Width), rect.Left + 5);
        var pictures = block.Film is not null || block.Frames is not null;
        var y = pictures && rect.Height >= 30 ? rect.Top + 3 : rect.Top + (rect.Height - label.Height) / 2;

        // Over thumbnails, add a backing shade for legibility.
        if (pictures)
            drawing.DrawRoundedRectangle(LabelShade, null, new Rect(x - 2, y, label.Width + 4, label.Height), 2, 2);
        drawing.DrawText(label, new Point(x, y));
    }
}
