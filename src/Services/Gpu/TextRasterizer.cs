using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// Words drawn by WPF into stills for the GPU engine (<see cref="GpuResourceKind.Still2D"/>, BGRA8 straight alpha), as the
/// export draws them: a text clip as FFmpeg's drawtext writes it (FilterGraphBuilder.Video.cs AppendTextSource: the same
/// layout, TextLayout, the same font, size, colours and outline, each line centred on its baseline), drawn whole and cut
/// by its crop; and the captions as libass draws the .ass file the export burns in (CaptionGenerator.WriteAss: bold, the
/// style's colours, outline and size at the caption box's scale, centred at the box's bottom margin).
/// <para>A caption is one still per cue, every state of it stacked in it (a caption's words while TikTok Pop has each one
/// of them up, or Karaoke Sweep's words before and after they are sung), so one resource is enough for all its moments:
/// the plan picks a state by its crop, and sweeps a karaoke line by cutting the sung state at the sweep.</para>
/// <para>WPF draws on the UI thread: text clips when a plan is made (cached by their content), captions near the playhead
/// in <see cref="PrepareAround"/> (a budget per call, the rest next call). A caption not drawn yet is not shown for that
/// frame: nothing ever waits for one. What the thread that evaluates plans reads (<see cref="CueAt"/>) is published whole and
/// read lock-free.</para>
/// </summary>
public sealed class TextRasterizer
{
    private readonly GpuResourceBroker _broker;

    internal TextRasterizer(GpuResourceBroker broker) => _broker = broker;

    // ----- Text clips -----

    /// <summary>
    /// A text layer's words drawn on a see-through canvas of <paramref name="width"/> x <paramref name="height"/> pixels,
    /// as drawtext draws them there: the layout's lines at its scale to the canvas, each centred across, its baseline where
    /// the layout puts it; the outline round the letters, under them.
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

    /// <summary>One caption on the sequence (one .ass event, or a TikTok Pop line's events), and its still once drawn.</summary>
    internal sealed class CaptionCue
    {
        public required double From { get; init; }
        public required double To { get; init; }
        public required bool Pop { get; init; }
        public required string[] Words { get; init; }

        /// <summary>When each word is spoken: its state (Pop) or its sweep (Karaoke) runs from its start to the next word's.</summary>
        public required double[] WordStarts { get; init; }

        public required long Hash { get; init; }

        // Set once on the UI thread when drawn; read by the thread that evaluates plans.
        private CaptionRaster? _raster;

        public CaptionRaster? Raster
        {
            get => Volatile.Read(ref _raster);
            set => Volatile.Write(ref _raster, value);
        }

        /// <summary>Which word is being said at a moment of the cue (0 before the second starts).</summary>
        public int WordAt(double t)
        {
            var word = 0;
            while (word + 1 < WordStarts.Length && t >= WordStarts[word + 1])
                word++;
            return word;
        }

        /// <summary>When the word's state (or sweep) ends: the next word's start, the caption's end for the last.</summary>
        public double WordEnd(int word) => word + 1 < WordStarts.Length ? WordStarts[word + 1] : To;
    }

    /// <summary>
    /// A cue's still: <paramref name="States"/> pictures of the caption box stacked down it, each <paramref name="Height"/>
    /// tall with <see cref="Gap"/> see-through rows between (so no state bleeds into the next when it is sampled), and each
    /// word's left edge and where the next begins, in the box's pixels (a karaoke line's sweep).
    /// </summary>
    internal sealed record CaptionRaster(GpuResourceRef Still, int Width, int Height, int States, float[] Lefts, float[] Rights)
    {
        public const int Gap = 2;

        public int TotalHeight => States * Height + (States - 1) * Gap;

        /// <summary>The state's rows as fractions of the still: top and bottom.</summary>
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
    /// The captions to draw (on the UI thread): each line's words in sequence seconds (the anchored captions lane projected
    /// onto the timeline: what the export's caption file is written from), the style, and what the style's sizes, given for a
    /// 1080-pixel frame, are multiplied by (as WriteCaptionFile works it out for the box). Null or none for no captions.
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

            // Gone when its last word ends, unless the next caption follows within a breath (CaptionGenerator.WriteAss).
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

        // A caption drawn already keeps its still (5.0-Release): every edit hands the captions over again, and a cue made
        // anew undrawn left the caption off the frames drawn until PrepareAround drew it again (a blink on every step of a
        // slider dragged; in the 3D view the stack's camera swung with it). Its words, style, scale and box are its hash.
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

    /// <summary>The caption box's size in the canvas's pixels (from the plan, as the export's caption canvas is): a new size draws every caption again.</summary>
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
    /// Draws the captions around a moment of the sequence that are not drawn yet (the UI thread, as the playhead moves):
    /// those within <see cref="PrepareWindowSeconds"/> of it, nearest first, for about <see cref="PrepareBudgetMs"/> per
    /// call; the rest on the next call. A caption whose still was let go is drawn again.
    /// </summary>
    /// <returns>Whether a caption was drawn (a new frame shows it).</returns>
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
            // Outwards from the playhead: at, at + 1, at - 1, at + 2...
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

    /// <summary>How far either side of the playhead captions are drawn ahead, in seconds.</summary>
    public const double PrepareWindowSeconds = 10;

    /// <summary>About how long one <see cref="PrepareAround"/> may draw for, in milliseconds.</summary>
    public const double PrepareBudgetMs = 2;

    /// <summary>The caption shown at a moment, if there is one (lock-free: the thread that evaluates plans).</summary>
    internal CaptionCue? CueAt(double t)
    {
        var cues = Volatile.Read(ref _cues);
        var i = Find(cues, t);
        return i >= 0 && t < cues[i].To ? cues[i] : null;
    }

    /// <summary>The last cue that starts at or before a moment; -1 for none.</summary>
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
    /// A cue's states drawn as libass draws its events: bold, the font at the style's size times the scale (a size libass
    /// takes as the line's ascent and descent together), the outline round the letters, the line centred across between
    /// the margins (a 40th of the box at each side) and standing on the bottom margin (a 12th of it). Karaoke: its words
    /// before they are sung (the style's colour), then sung (the highlight). TikTok Pop: one state per word, that word in
    /// the highlight and a seventh larger.
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

        // Each state's words laid out first (a karaoke line's sweep needs where its words are, drawn now or found drawn).
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

    /// <summary>Draws into a see-through picture of the given size and gives it back as BGRA8 with straight alpha (WPF draws premultiplied).</summary>
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

    /// <summary>#RRGGBB as drawtext and the caption file read it; anything else is the fallback.</summary>
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
