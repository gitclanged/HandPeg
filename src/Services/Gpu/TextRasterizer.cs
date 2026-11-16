using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// Renders text with WPF into stills for the GPU engine (<see cref="GpuResourceKind.Still2D"/>, BGRA8 straight alpha), matching the
/// export: text clips laid out as FFmpeg drawtext does (TextLayout: same font, size, colours and outline, each line centred on its
/// baseline), drawn whole and cut by the crop; captions as libass renders the burned-in .ass (CaptionGenerator.WriteAss: bold, style
/// colours, outline and size at the caption box scale, centred on the bottom margin).
/// <para>Each caption cue is one still with all its states stacked (TikTok Pop's per-word highlight, Karaoke Sweep's unsung/sung), so one
/// resource serves every moment: the plan picks a state by crop and sweeps karaoke by cropping the sung state.</para>
/// <para>WPF renders on the UI thread: text clips at plan build (cached by content), captions near the playhead in
/// <see cref="PrepareAround"/> (time-budgeted, remainder next call). An undrawn caption is skipped for that frame; nothing waits. Data the
/// evaluating thread reads (<see cref="CueAt"/>) is published atomically and read lock-free.</para>
/// </summary>
public sealed class TextRasterizer
{
    private readonly GpuResourceBroker _broker;

    internal TextRasterizer(GpuResourceBroker broker) => _broker = broker;

    // ----- Text clips -----

    /// <summary>
    /// Renders a text layer on a transparent <paramref name="width"/> x <paramref name="height"/> canvas as drawtext does: layout lines scaled
    /// to the canvas, each centred horizontally at its layout baseline, with the outline drawn beneath the fill.
    /// </summary>
    internal GpuResourceRef TextStill(Layer layer, int width, int height)
    {
        (width, height) = (Math.Clamp(width, 2, 16384), Math.Clamp(height, 2, 16384));
        var layout = TextLayout.Measure(layer);
        var (letters, outline) = (Colour(layer.FontColor, Colors.White), Colour(layer.OutlineColor, Colors.Black));
        var hash = default(GpuHash).Add("text").Add(layer.TextContent).Add(layout.FontName).Add(layer.FontSize).Add(layer.OutlineThickness)
            .Add(letters.R << 16 | letters.G << 8 | letters.B).Add(outline.R << 16 | outline.G << 8 | outline.B).Add(width).Add(height).Value;
        return _broker.Still(hash, () => Draw(width, height, context =>
        {
            var scale = Math.Min((double)width / layout.Width, (double)height / layout.Height);
            var border = layer.OutlineThickness > 0 ? Math.Max((int)Math.Round(Math.Clamp(layer.OutlineThickness, 0, TextLayout.LargestOutline) * scale), 1) : 0;
            var typeface = new Typeface(new FontFamily(layout.FontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var size = Math.Max(layer.FontSize * scale, 1);
            var (fill, pen) = (Frozen(letters), border > 0 ? FrozenPen(outline, 2 * border) : null);

            var top = (height - layout.Lines.Count * layout.LineHeight * scale) / 2;
            for (var i = 0; i < layout.Lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(layout.Lines[i]))
                    continue;

                var text = new FormattedText(layout.Lines[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, fill, 1.0);
                var baseline = top + (i * layout.LineHeight + layout.Baseline) * scale;
                var geometry = text.BuildGeometry(new Point((width - text.Width) / 2, baseline - text.Baseline));
                if (pen is not null)
                    context.DrawGeometry(null, pen, geometry);
                context.DrawGeometry(fill, null, geometry);
            }
        }));
    }

    // ----- Captions -----

    /// <summary>One caption on the sequence (one .ass event, or a TikTok Pop line's events) and its still once drawn.</summary>
    internal sealed class CaptionCue
    {
        public required double From { get; init; }
        public required double To { get; init; }
        public required bool Pop { get; init; }
        public required string[] Words { get; init; }

        /// <summary>Per-word start times: a word's state (Pop) or sweep (Karaoke) runs until the next word starts.</summary>
        public required double[] WordStarts { get; init; }

        public required long Hash { get; init; }

        // Written once on the UI thread when drawn; read by the plan-evaluating thread.
        private CaptionRaster? _raster;

        public CaptionRaster? Raster
        {
            get => Volatile.Read(ref _raster);
            set => Volatile.Write(ref _raster, value);
        }

        /// <summary>Index of the word spoken at <paramref name="t"/> (0 before the second word starts).</summary>
        public int WordAt(double t)
        {
            var word = 0;
            while (word + 1 < WordStarts.Length && t >= WordStarts[word + 1])
                word++;
            return word;
        }

        /// <summary>End of a word's state or sweep: the next word's start, or the cue end for the last word.</summary>
        public double WordEnd(int word) => word + 1 < WordStarts.Length ? WordStarts[word + 1] : To;
    }

    /// <summary>
    /// A cue's still: <paramref name="States"/> images of the caption box stacked vertically, each <paramref name="Height"/> tall with
    /// <see cref="Gap"/> transparent rows between (no bleed when sampled), plus each word's left edge and the next word's start in box pixels
    /// (karaoke sweep).
    /// </summary>
    internal sealed record CaptionRaster(GpuResourceRef Still, int Width, int Height, int States, float[] Lefts, float[] Rights)
    {
        public const int Gap = 2;

        public int TotalHeight => States * Height + (States - 1) * Gap;

        /// <summary>A state's top and bottom rows as fractions of the still height.</summary>
        public (float Top, float Bottom) Rows(int state)
        {
            var top = state * (Height + Gap);
            return ((float)top / TotalHeight, (float)(top + Height) / TotalHeight);
        }
    }

    private CaptionCue[] _cues = [];
    private CaptionStyle _style = new();
    private double _textScale = 1;
    private (int Width, int Height) _box = (16, 16);

    /// <summary>
    /// Sets the captions (UI thread): per-line words in sequence seconds (the anchored captions lane projected onto the timeline, as the
    /// export's caption file is written), the style, and the factor applied to style sizes specified for a 1080-px frame (as
    /// WriteCaptionFile computes for the box). Null or empty clears the captions.
    /// </summary>
    public void SetCaptions(IReadOnlyList<IReadOnlyList<CaptionWord>>? lines, CaptionStyle? style, double textScale)
    {
        _style = style?.Clone() ?? new CaptionStyle();
        _textScale = double.IsFinite(textScale) && textScale > 0 ? textScale : 1;
        var pop = _style.Animation == CaptionStyle.TikTokPop;
        var cues = new List<CaptionCue>(lines?.Count ?? 0);
        for (var c = 0; c < (lines?.Count ?? 0); c++)
        {
            var line = lines![c];
            if (line is not { Count: > 0 })
                continue;

            // A cue ends with its last word unless the next follows within the silent gap (CaptionGenerator.WriteAss).
            var (start, end) = (line[0].Start, line[^1].End);
            if (c + 1 < lines.Count && lines[c + 1] is { Count: > 0 } next && next[0].Start - end <= CaptionGenerator.SilentGapSeconds)
                end = next[0].Start;
            end = Math.Max(end, start + 0.1);

            var words = new string[line.Count];
            var starts = new double[line.Count];
            for (var w = 0; w < line.Count; w++)
                (words[w], starts[w]) = (line[w].Text, line[w].Start);
            cues.Add(new CaptionCue { From = start, To = end, Pop = pop, Words = words, WordStarts = starts, Hash = CueHash(words, pop) });
        }

        cues.Sort(static (a, b) => a.From.CompareTo(b.From));

        // Carry already-drawn stills over by hash (words, style, scale, box): every edit resubmits the captions, and undrawn new cues would
        // blink off until PrepareAround redrew them (visible on every slider step, and as camera jumps in the 3D view).
        var before = Volatile.Read(ref _cues);
        if (before.Length > 0 && cues.Count > 0)
        {
            var drawn = new Dictionary<long, CaptionRaster>(before.Length);
            foreach (var cue in before)
            {
                if (cue.Raster is { } raster)
                    drawn.TryAdd(cue.Hash, raster);
            }

            foreach (var cue in cues)
            {
                if (drawn.TryGetValue(cue.Hash, out var raster))
                    cue.Raster = raster;
            }
        }

        Volatile.Write(ref _cues, [.. cues]);
    }

    /// <summary>Sets the caption box size in canvas pixels (from the plan, like the export's caption canvas); a new size redraws every caption.</summary>
    internal void UseCaptionBox(int width, int height)
    {
        var box = (Math.Max(width, 16), Math.Max(height, 16));
        if (box == _box)
            return;

        _box = box;
        var cues = Volatile.Read(ref _cues);
        var fresh = new CaptionCue[cues.Length];
        for (var i = 0; i < cues.Length; i++)
            fresh[i] = new CaptionCue { From = cues[i].From, To = cues[i].To, Pop = cues[i].Pop, Words = cues[i].Words, WordStarts = cues[i].WordStarts, Hash = CueHash(cues[i].Words, cues[i].Pop) };
        Volatile.Write(ref _cues, fresh);
    }

    /// <summary>
    /// Draws undrawn captions near <paramref name="sequenceSeconds"/> (UI thread, as the playhead moves): within
    /// <see cref="PrepareWindowSeconds"/>, nearest first, about <see cref="PrepareBudgetMs"/> per call, the rest next call. Evicted stills are redrawn.
    /// </summary>
    /// <returns>True when a caption was drawn (a new frame will show it).</returns>
    public bool PrepareAround(double sequenceSeconds)
    {
        var cues = Volatile.Read(ref _cues);
        if (cues.Length == 0)
            return false;

        var drew = false;
        var clock = Stopwatch.StartNew();
        var at = Math.Max(Find(cues, sequenceSeconds), 0);
        for (var step = 0; step < 2 * cues.Length; step++)
        {
            // Spiral outward from the playhead: at, at + 1, at - 1, at + 2, ...
            var i = at + (step % 2 == 0 ? step / 2 : -(step / 2 + 1));
            if (i < 0 || i >= cues.Length)
                continue;
            var cue = cues[i];
            if (cue.From > sequenceSeconds + PrepareWindowSeconds || cue.To < sequenceSeconds - PrepareWindowSeconds)
                continue;

            if (cue.Raster is { } drawn)
            {
                if (_broker.StateOf(drawn.Still) is GpuResourceState.Ready or GpuResourceState.Pending)
                {
                    _broker.Touch(drawn.Still, pinned: false);
                    continue;
                }

                cue.Raster = null;
            }

            cue.Raster = DrawCue(cue);
            drew = true;
            if (clock.Elapsed.TotalMilliseconds > PrepareBudgetMs)
                break;
        }

        return drew;
    }

    /// <summary>
    /// Export path (captions through the GPU stack, on the broker's STA thread): draws every undrawn caption between
    /// <paramref name="fromSeconds"/> and <paramref name="toSeconds"/> with no time budget and touches drawn ones (others are evicted as the
    /// table fills). True when the caption visible at <paramref name="fromSeconds"/>, if any, is drawn and uploaded.
    /// </summary>
    internal bool PrepareFor(double fromSeconds, double toSeconds)
    {
        var cues = Volatile.Read(ref _cues);
        for (var i = Math.Max(Find(cues, fromSeconds), 0); i < cues.Length && cues[i].From <= toSeconds; i++)
        {
            var cue = cues[i];
            if (cue.To < fromSeconds)
                continue;

            if (cue.Raster is { } drawn)
            {
                if (_broker.StateOf(drawn.Still) is GpuResourceState.Ready or GpuResourceState.Pending)
                {
                    _broker.Touch(drawn.Still, pinned: false);
                    continue;
                }

                cue.Raster = null;
            }

            cue.Raster = DrawCue(cue);
        }

        return CueAt(fromSeconds) is not { } now || now.Raster is { } raster && _broker.StateOf(raster.Still) == GpuResourceState.Ready;
    }

    /// <summary>Caption pre-draw window either side of the playhead, seconds.</summary>
    public const double PrepareWindowSeconds = 10;

    /// <summary>Approximate draw-time budget per <see cref="PrepareAround"/> call, ms.</summary>
    public const double PrepareBudgetMs = 2;

    /// <summary>Caption visible at <paramref name="t"/>, if any (lock-free; called on the plan-evaluating thread).</summary>
    internal CaptionCue? CueAt(double t)
    {
        var cues = Volatile.Read(ref _cues);
        var i = Find(cues, t);
        return i >= 0 && t < cues[i].To ? cues[i] : null;
    }

    /// <summary>Last cue starting at or before <paramref name="t"/> (binary search); -1 when none.</summary>
    private static int Find(CaptionCue[] cues, double t)
    {
        int low = 0, high = cues.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (cues[middle].From <= t)
                (found, low) = (middle, middle + 1);
            else
                high = middle - 1;
        }

        return found;
    }

    private long CueHash(string[] words, bool pop)
    {
        var hash = default(GpuHash).Add("caption").Add(pop).Add(_style.FontName).Add(_style.FontSize).Add(_style.OutlineThickness)
            .Add(_style.PrimaryColor).Add(_style.HighlightColor).Add(_style.OutlineColor).Add(_textScale).Add(_box.Width).Add(_box.Height);
        foreach (var word in words)
            hash = hash.Add(word);
        return hash.Value;
    }

    /// <summary>
    /// Renders a cue's states as libass renders its events: bold, style size x scale (libass treats the size as ascent + descent), outline
    /// beneath the fill, line centred horizontally on a bottom margin of 1/12 of the box. Karaoke: unsung (style colour), then sung
    /// (highlight). TikTok Pop: one state per word, that word highlighted and 15 % larger.
    /// </summary>
    private CaptionRaster? DrawCue(CaptionCue cue)
    {
        var (width, height) = _box;
        var states = cue.Pop ? cue.Words.Length : 2;
        var total = states * height + (states - 1) * CaptionRaster.Gap;
        if (total > 16384)
        {
            AppLog.Write("A caption with too many words for one still is drawn with its first states only.");
            states = (16384 + CaptionRaster.Gap) / (height + CaptionRaster.Gap);
            total = states * height + (states - 1) * CaptionRaster.Gap;
        }

        var fontSize = Math.Max((int)Math.Round(Math.Clamp(_style.FontSize, 8, 400) * _textScale), 6);
        var outline = Math.Round(Math.Clamp(_style.OutlineThickness, 0, 40) * _textScale, 1);
        var family = new FontFamily(string.IsNullOrWhiteSpace(_style.FontName) ? "Arial" : _style.FontName.Replace(",", " ").Trim());
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var (ascent, cell) = typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.Height > 0 ? (glyphs.Baseline, glyphs.Height) : (family.Baseline, family.LineSpacing);
        var em = fontSize / cell;
        var baseline = height - height / 12 - (cell - ascent) * em;
        var (primary, highlight, edge) = (Frozen(Colour(_style.PrimaryColor, Colors.White)), Frozen(Colour(_style.HighlightColor, Color.FromRgb(0xFF, 0xE6, 0x00))),
            Colour(_style.OutlineColor, Colors.Black));
        var pen = outline > 0 ? FrozenPen(edge, 2 * outline) : null;
        var space = new FormattedText(" ", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, em, primary, 1.0).WidthIncludingTrailingWhitespace;

        // Lay out every state first (the karaoke sweep needs word positions whether drawn now or found cached).
        var (lefts, rights) = (new float[cue.Words.Length], new float[cue.Words.Length]);
        var layouts = new (FormattedText Text, double X)[states][];
        for (var state = 0; state < states; state++)
        {
            var line = new (FormattedText Text, double X)[cue.Words.Length];
            var across = 0.0;
            for (var w = 0; w < line.Length; w++)
            {
                var big = cue.Pop && w == state;
                line[w].Text = new FormattedText(cue.Words[w], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, em * (big ? 1.15 : 1), primary, 1.0);
                across += line[w].Text.WidthIncludingTrailingWhitespace + (w + 1 < line.Length ? space : 0);
            }

            var x = (width - across) / 2;
            for (var w = 0; w < line.Length; w++)
            {
                // A karaoke word's sweep runs across it and the space after it (the .ass gives each \kf its word and its space).
                var advance = line[w].Text.WidthIncludingTrailingWhitespace + (w + 1 < line.Length ? space : 0);
                line[w].X = x;
                if (state == 0)
                    (lefts[w], rights[w]) = ((float)x, (float)(x + advance));
                x += advance;
            }

            layouts[state] = line;
        }

        var still = _broker.Still(cue.Hash, () => Draw(width, total, context =>
        {
            for (var state = 0; state < states; state++)
            {
                var top = state * (height + CaptionRaster.Gap);
                var line = layouts[state];
                for (var w = 0; w < line.Length; w++)
                {
                    var lit = cue.Pop ? w == state : state == 1;
                    var geometry = line[w].Text.BuildGeometry(new Point(line[w].X, top + baseline - line[w].Text.Baseline));
                    if (pen is not null)
                        context.DrawGeometry(null, pen, geometry);
                    context.DrawGeometry(lit ? highlight : primary, null, geometry);
                }
            }
        }), pinned: false);

        return still.IsNone ? null : new CaptionRaster(still, width, height, states, lefts, rights);
    }

    // ----- Drawing -----

    /// <summary>Renders into a transparent bitmap of the given size and returns BGRA8 with straight alpha (WPF renders premultiplied).</summary>
    private static (byte[] Pixels, int Width, int Height)? Draw(int width, int height, Action<DrawingContext> draw)
    {
        try
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                draw(context);

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a is 0 or 255)
                    continue;
                pixels[i] = (byte)Math.Min(pixels[i] * 255 / a, 255);
                pixels[i + 1] = (byte)Math.Min(pixels[i + 1] * 255 / a, 255);
                pixels[i + 2] = (byte)Math.Min(pixels[i + 2] * 255 / a, 255);
            }

            return (pixels, width, height);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Write("Words for the GPU engine could not be drawn", ex);
            return null;
        }
    }

    /// <summary>Parses #RRGGBB as drawtext and the caption file do; anything else yields the fallback.</summary>
    private static Color Colour(string? text, Color fallback)
    {
        var hex = (text ?? "").Trim().TrimStart('#');
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
