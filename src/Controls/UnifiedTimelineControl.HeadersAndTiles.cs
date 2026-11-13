using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

// The unified timeline's drawing, continued: the headers, the overlay (the block being dragged, hover handles, the drop
// mark), the two lines that slide, the brushes and pens made once, and the waveform tiles (WaveTiles).
public sealed partial class UnifiedTimelineControl
{
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
            if (lane.Height < 0.5)
                continue;

            drawing.DrawRectangle(lane.Depth == 0 ? _card : _panel, null, new Rect(0, lane.Top, width, lane.Height));
            if (AccentOf(i) is { } accent)
                drawing.DrawRectangle(accent, null, new Rect(0, lane.Top, 3, lane.Height));

            // The deck: a line down from the parent's chevron to each lane of its stack. The sound's stack, under the
            // layers' when a row has both, hangs from the second chevron, its line passing beside the layers' lanes. The
            // lanes laid above their parent (in front of it, 5.0-Release) have it rising to them from the parent's top.
            if (lane.Depth > 0)
            {
                var x = DeckX(lane.Depth) + (lane.SecondDeck ? ChevronStep : 0) + 0.5;
                var middle = lane.Top + Math.Min(lane.Height / 2, 11);
                if (lane.Above)
                {
                    drawing.DrawLine(_deckPen, new Point(x, lane.FirstChild ? middle : lane.Top - LaneGap), new Point(x, lane.Bottom + LaneGap));
                }
                else
                {
                    var top = lane.SecondDeck && lane.FirstChild && lane.Parent >= 0 ? _lanes[lane.Parent].Bottom : lane.Top - LaneGap;
                    drawing.DrawLine(_deckPen, new Point(x, top), new Point(x, lane.LastChild ? middle : lane.Bottom + LaneGap));
                }

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

            var badge = BadgeRect(lane, pixelsPerDip);
            if (lane.Height >= 11)
            {
                var name = NameOf(row, lane.Detailed ? 12 : 9, lane.Mini ? _muted : _text, pixelsPerDip);
                var left = NameLeft(lane);
                var room = (lane.Detailed && !TwoLines(lane) ? badge.IsEmpty ? SwitchesLeft(lane) : badge.Left - 4 : width - 4) - left;
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

            // Its track's Game Preset (5.2A, a Track Preset Node): a pill with the preset's name, or a faint "+ Preset" that sets one.
            if (!badge.IsEmpty)
            {
                var set = row.Badge is { Length: > 0 };
                var words = BadgeWords(row, set ? _text : _muted, pixelsPerDip);
                if (set)
                    drawing.DrawRoundedRectangle(Translucent(_accent, 0.45), null, badge, badge.Height / 2, badge.Height / 2);
                else
                    drawing.DrawRoundedRectangle(null, BadgeOutline, badge, badge.Height / 2, badge.Height / 2);
                words.MaxTextWidth = Math.Max(badge.Width - 2 * BadgePadding, 1);
                drawing.DrawText(words, new Point(badge.Left + BadgePadding, badge.Top + (badge.Height - words.Height) / 2));
            }

            // The time area's ends beside it (the wells' lit edge, as the old bars had at theirs).
            if (_edgeWidth > 0 && lane.Height > 2 * _edgeWidth)
            {
                drawing.DrawLine(_edgePen, new Point(width + _edgeWidth / 2, lane.Top), new Point(width + _edgeWidth / 2, lane.Bottom));
                drawing.DrawLine(_lowerEdgePen, new Point(ActualWidth - _edgeWidth / 2, lane.Top), new Point(ActualWidth - _edgeWidth / 2, lane.Bottom));
            }
        }
    }

    // A header's preset badge (5.2A): its words' size, its padding, what it says with no preset, and the outline of that empty one.
    private const double BadgeTextSize = 10, BadgePadding = 6, BadgeHeight = 16;
    private const string NoBadgeText = "+ Preset";
    private static readonly Pen BadgeOutline = Frozen(new Pen(Solid(90, 255, 255, 255), 1) { DashStyle = Frozen(new DashStyle([3, 2], 0)) });

    /// <summary>
    /// Where a header's preset badge is (5.2A): on a tall lane in the switches' line, after the switches, as wide as its words (the room
    /// there at most); on a short one, left of the switches, and only for a preset that is set. Top-level rows drawn in full only; empty
    /// for none, or no room.
    /// </summary>
    private Rect BadgeRect(in Lane lane, double pixelsPerDip)
    {
        var row = lane.Row;
        var two = TwoLines(lane);
        if (!lane.Detailed || lane.Depth > 0 || (row.Badge is not { Length: > 0 } && !(row.OffersBadge && two)))
            return Rect.Empty;

        var natural = BadgeWords(row, _muted, pixelsPerDip, measure: true).WidthIncludingTrailingWhitespace + 2 * BadgePadding;
        double left, right, top;
        if (two)
        {
            left = NameLeft(lane) - 3 + SwitchCount(row) * SwitchSize + 6;
            right = Math.Min(_headerWidth - 6, left + natural);
            top = lane.Top + 23 + (SwitchSize - BadgeHeight) / 2;
        }
        else
        {
            right = SwitchesLeft(lane) - 4;
            left = Math.Max(NameLeft(lane) + 36, right - Math.Min(natural, 84));
            top = lane.Top + (lane.Height - BadgeHeight) / 2;
        }

        return right - left >= 28 ? new Rect(left, top, right - left, BadgeHeight) : Rect.Empty;
    }

    // A badge's words: the preset's name (or "+ Preset"), laid out once for its text and colour; drawn trimmed with an ellipsis to the pill, and
    // measured from a copy never trimmed.
    private FormattedText BadgeWords(TimelineRow row, Brush brush, double pixelsPerDip, bool measure = false)
    {
        var text = row.Badge is { Length: > 0 } name ? name : NoBadgeText;
        var key = (string.Concat(measure ? "\u0001measure:" : "\u0001badge:", text), brush, BadgeTextSize, false);
        if (!_words.TryGetValue(key, out var words))
        {
            words = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, BadgeTextSize, brush, pixelsPerDip)
            {
                MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            };
            _words[key] = words;
        }

        return words;
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
        _newTrackWords = null;
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
        // row, which shows where it would go there: blue where it may, red where it may not. The other blocks of a selected
        // group dragged with it (5.0) are drawn with what it carries along, each outlined as selected.
        if (_drag is { } drag && LaneOf(drag.Row) is >= 0 and var dragLane)
        {
            // What it carries along first, each in its own lane (a collapsed one draws it plainly), every move (4.4
            // BugHunt): moved with it, or trimmed with its edge (the file's frames staying put, as the parent's do); a child
            // the trim leaves outside it faint and dashed, coming loose; outlined red where the move cannot take it.
            var seamGrowth = drag.Seam is { } growing ? growing.Seconds * scale : 0;
            foreach (var child in _coupled)
            {
                // A caption its clip's trim has left outside it is not shown until it is let go (5.0).
                var childIndex = LaneOf(child.Row);
                if (childIndex < 0 || child.How is Follows.Within or Follows.SlipWithin && child.Block.Width < 1)
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

            // A slip slides its frames alone: its sound stack's lanes are not moved (a slip carries only its captions), so the sound
            // they play stays at the block's own left until the let-go. Half seen over another row, or at a seam (5.4A).
            var away = drag.Target is not null || drag.NewTrack is not null;
            if (away)
                drawing.PushOpacity(0.5);
            DrawBlock(drawing, lane, block, rect, contentLeft, null, from, to, dpi, drag.Kind == BlockEdit.Slip ? rect.Left : null);
            DrawLabel(drawing, block, rect, from, dpi.PixelsPerDip);
            if (block.CrossfadeIn > 0.01 && scale > 0)
                DrawCrossfade(drawing, lane, block, scale, _originX, dpi.PixelsPerDip);
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

            // At a seam (5.4A): the insertion bar where the new track goes, across the time area, in the accent (red where it may not
            // go there after all), and what it means.
            if (drag.NewTrack is { } seam)
                DrawNewTrackBar(drawing, seam.Y, drag.NewTrackFits && drag.FamilyFits, from, to, dpi.PixelsPerDip);
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

        // A rubber band being drawn over empty space (5.2A).
        if (_marquee is { } band)
        {
            var box = MarqueeBox(band);
            box.Offset(-_originX, 0);
            drawing.DrawRectangle(MarqueeFill, MarqueeEdge, box);
        }
    }

    private static readonly Brush MarqueeFill = Solid(40, 0x4F, 0xC3, 0xF7);
    private static readonly Pen MarqueeEdge = Frozen(new Pen(Solid(200, 0x4F, 0xC3, 0xF7), 1));

    // A J/L cut being dragged, in words.
    private static string SplitWords(bool lead, double seconds) => (lead, seconds >= 0) switch
    {
        (true, true) => string.Create(CultureInfo.InvariantCulture, $"J Cut: the sound starts {seconds:0.00} s before the picture"),
        (true, false) => string.Create(CultureInfo.InvariantCulture, $"The sound starts {-seconds:0.00} s after the picture"),
        (false, true) => string.Create(CultureInfo.InvariantCulture, $"L Cut: the sound lasts {seconds:0.00} s past the picture"),
        _ => string.Create(CultureInfo.InvariantCulture, $"The sound stops {-seconds:0.00} s before the picture ends"),
    };

    /// <summary>
    /// A new track's insertion bar (5.4A, a clip dragged to a seam): 3 DIPs across the time area at <paramref name="y"/> (laid out), in the
    /// accent where the clip may go (red where it may not), with "+ New Video Track" on it at the left. The words are laid out once.
    /// </summary>
    private void DrawNewTrackBar(DrawingContext drawing, double y, bool fits, double from, double to, double pixelsPerDip)
    {
        var bar = fits ? _accent : RefusedOutline.Brush;
        drawing.DrawRectangle(bar, null, new Rect(from, y - 1.5, to - from, 3));
        _newTrackWords ??= new FormattedText(NewTrackWords, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, pixelsPerDip);
        var text = _newTrackWords;
        var box = new Rect(from + 8, Math.Max(y - text.Height - 4, 0), text.Width + 10, text.Height + 2);
        drawing.DrawRoundedRectangle(bar, null, box, 3, 3);
        drawing.DrawText(text, new Point(box.Left + 5, box.Top + 1));
    }

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
    /// How many waveform tiles the timeline keeps at most (Pass 5.4D-TabPerf, the UI thread): 256, and more while project tabs are kept in
    /// memory (the window sets 256 + 128 for each, at most 1024, each tile some 32 KB), so a tab's sounds are not drawn again as it comes
    /// back. Made smaller, the next tile made trims the rest.
    /// </summary>
    public static int WaveTileCapacity
    {
        get => WaveTiles.CacheCap;
        set => WaveTiles.CacheCap = Math.Max(WaveTiles.LeastCap, Math.Min(value, WaveTiles.MostCap));
    }

    /// <summary>
    /// Waveforms as frozen tiles (4.4): a sound's peaks drawn once per zoom step (every doubling of how many pixels a second
    /// of the sound takes) in stretches 512 pixels long, at unit height, so a track's height, its gain and a mini-stack's
    /// thin lanes never make one again, and a pan or a drag only places them. At most 8 are made a render (crossing a zoom
    /// step never stalls a frame; the step below stands in meanwhile), and at most about 256 are kept (Pass 5.4D-TabPerf: more
    /// while project tabs are kept in memory, <see cref="WaveTileCapacity"/>), those of sounds no longer held going with them.
    /// While the zoom moves (5.4.4) the tiles of a step either side stand in, stretched, and the crisp ones are made once it settles,
    /// <see cref="SettleBuilds"/> in that one render. Used on the UI thread only.
    /// </summary>
    private static class WaveTiles
    {
        public const double TilePixels = 512;
        public const int LeastBucket = -12, MostBucket = 16;
        public const int LeastCap = 256, MostCap = 1024;
        public const int BuildsPerRender = 8, SettleBuilds = 32, StandInOctaves = 2;
        private const int MostColumns = 1024;

        /// <summary>How many tiles are kept at most (<see cref="WaveTileCapacity"/>).</summary>
        public static int CacheCap { get; set; } = LeastCap;

        /// <summary>Tiles made since HandPeg started (the golden wave-tiles-zoom counts them).</summary>
        public static int Built { get; private set; }

        /// <summary>Tiles held now, of every sound still held (the goldens).</summary>
        public static int Kept
        {
            get
            {
                var kept = 0;
                foreach (var (_, tiles) in Tiles)
                    kept += tiles.Count;
                return kept;
            }
        }

        private sealed class Tile(StreamGeometry geometry, long used)
        {
            public StreamGeometry Geometry { get; } = geometry;

            public long Used { get; set; } = used;
        }

        private static readonly ConditionalWeakTable<WaveformData, Dictionary<long, Tile>> Tiles = new();
        private static readonly List<long> Stale = [];
        private static int _count, _budget, _perRender = BuildsPerRender;
        private static long _stamp;

        /// <summary>A render of the rows begins: <paramref name="builds"/> tiles may be made in it (more in the one a settled zoom asks for, 5.4.4).</summary>
        public static void BeginRender(int builds = BuildsPerRender)
        {
            _stamp++;
            _budget = _perRender = builds;
        }

        /// <summary>More may be made (for the overlay, drawn on its own between the rows' renders), without ageing what was drawn.</summary>
        public static void Refill() => _budget = _perRender;

        /// <summary>Lets every tile go (the screen's DPI changed: a tile has a column for each device pixel), to be made again as drawn.</summary>
        public static void Clear()
        {
            foreach (var (_, tiles) in Tiles)
                tiles.Clear();
            _count = 0;
        }

        /// <summary>A tile already made (marked used in this render, 5.4.4: a stand-in drawn is never trimmed under it); null when it is not.</summary>
        public static StreamGeometry? Peek(WaveformData data, int bucket, long tile)
        {
            if (!Tiles.TryGetValue(data, out var tiles) || !tiles.TryGetValue(Key(bucket, tile), out var kept))
                return null;
            kept.Used = _stamp;
            return kept.Geometry;
        }

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
            Built++;
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
