using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HandPegApp.Models;
using HandPegApp.Services;
using static HandPegApp.UiBrushes;

namespace HandPegApp.Views;

/// <summary>
/// Standalone subtitle preview: a black frame shaped like the caption box (or the output frame for picture tracks) showing the cue
/// active at <see cref="Time"/>, rendered in WPF to match the GPU TextRasterizer and the export's libass output: bold, style colours,
/// outline and size at box scale, centred with its descender line a twelfth of the box above the bottom; TikTok Pop shows the spoken word
/// highlighted and 15% larger, Karaoke Sweep sweeps the highlight across each word while it is spoken. No playback or decoding; it
/// redraws only when the visible state changes.
/// <para>Picture-track subtitles need a decoder to render, so an active cue is shown as a numbered placeholder bar in the subtitle
/// area, enough to see retimed cues appear and disappear.</para>
/// <para>Currently unused: the Subtitle Studio is a dock pane without its own picture, since the main player renders captions live.
/// Kept (with MainViewModel.SubtitleEditorCaptions and SubtitleFrameSize) for a dedicated caption preview, e.g. in a torn-off side
/// window.</para>
/// </summary>
public sealed class SubtitlePreview : FrameworkElement
{
    // Text-track lines on the timeline clock with display intervals (ends extended to the next line across short gaps, as
    // CaptionGenerator.WriteAss does). Picture-track cues carry only their times.
    private IReadOnlyList<List<CaptionWord>> _lines = [];
    private double[] _from = [], _to = [];
    private bool _pictures;
    private CaptionStyle _style = new();
    private double _textScale = 1;
    private (int Width, int Height) _box = (1920, 1080);
    private double _time;

    // Last rendered (line, word, sweep) state; unchanged states skip redraw.
    private (int Line, int Word, int Sweep) _shown = (-1, -1, -1);

    /// <summary>Editor playhead in timeline seconds; the cue active at this time is drawn.</summary>
    public double Time
    {
        get => _time;
        set
        {
            _time = double.IsFinite(value) ? value : 0;
            if (Showing() != _shown)
                InvalidateVisual();
        }
    }

    /// <summary>
    /// Shows a text track: lines (each a cue's timed words on the timeline clock), the caption style, the scale applied to style sizes
    /// (authored for a 1080-pixel frame) for this caption box, and the box size in pixels.
    /// </summary>
    public void ShowCaptions(IReadOnlyList<List<CaptionWord>> lines, CaptionStyle style, double textScale, (int Width, int Height) box)
    {
        _pictures = false;
        _lines = lines;
        (_style, _textScale) = (style.Clone(), double.IsFinite(textScale) && textScale > 0 ? textScale : 1);
        (_from, _to) = (new double[lines.Count], new double[lines.Count]);
        for (var i = 0; i < lines.Count; i++)
        {
            var (start, end) = lines[i].Count > 0 ? (lines[i][0].Start, lines[i][^1].End) : (double.MaxValue, double.MaxValue);
            if (i + 1 < lines.Count && lines[i + 1].Count > 0 && lines[i + 1][0].Start - end <= CaptionGenerator.SilentGapSeconds)
                end = lines[i + 1][0].Start;
            (_from[i], _to[i]) = (start, Math.Max(end, start + 0.1));
        }

        Use(box);
    }

    /// <summary>Shows a picture track: cue intervals on the timeline clock, within a frame of size <paramref name="box"/>.</summary>
    public void ShowPictures(IReadOnlyList<(double Start, double End)> cues, (int Width, int Height) box)
    {
        _pictures = true;
        _lines = [];
        (_from, _to) = (cues.Select(c => c.Start).ToArray(), cues.Select(c => c.End).ToArray());
        Use(box);
    }

    private void Use((int Width, int Height) box)
    {
        _box = (Math.Max(box.Width, 16), Math.Max(box.Height, 16));
        _shown = (-1, -1, -1);
        InvalidateVisual();
    }

    // Active state at the playhead: line (latest start at or before Time, while unexpired), spoken word, and karaoke sweep
    // progress through that word (0..100, percent of the word's advance).
    private (int Line, int Word, int Sweep) Showing()
    {
        var line = -1;
        for (int low = 0, high = _from.Length - 1; low <= high;)
        {
            var middle = (low + high) >>> 1;
            if (_from[middle] <= _time)
                (line, low) = (middle, middle + 1);
            else
                high = middle - 1;
        }

        if (line < 0 || _time >= _to[line])
            return (-1, -1, -1);
        if (_pictures)
            return (line, 0, 0);

        var words = _lines[line];
        var word = 0;
        while (word + 1 < words.Count && _time >= words[word + 1].Start)
            word++;
        if (_style.Animation == CaptionStyle.TikTokPop)
            return (line, word, 0);

        var end = word + 1 < words.Count ? words[word + 1].Start : _to[line];
        var span = end - words[word].Start;
        return (line, word, (int)Math.Round(100 * (span > 1e-6 ? Math.Clamp((_time - words[word].Start) / span, 0, 1) : 1)));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        _shown = Showing();

        // Fit the box centred and draw in box-pixel coordinates via a scale transform.
        var (boxWidth, boxHeight) = _box;
        var scale = Math.Min(ActualWidth / boxWidth, ActualHeight / boxHeight);
        if (_shown.Line < 0 || !(scale > 0))
            return;

        var frame = new Rect((ActualWidth - boxWidth * scale) / 2, (ActualHeight - boxHeight * scale) / 2, boxWidth * scale, boxHeight * scale);
        drawing.PushClip(new RectangleGeometry(frame));
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, frame.X, frame.Y));
        if (_pictures)
            DrawPicture(drawing, _shown.Line);
        else
            DrawLine(drawing, _lines[_shown.Line], _shown.Word, _shown.Sweep / 100.0);
        drawing.Pop();
        drawing.Pop();
    }

    private void DrawLine(DrawingContext drawing, List<CaptionWord> words, int spoken, double through)
    {
        var (width, height) = _box;
        var pop = _style.Animation == CaptionStyle.TikTokPop;
        var fontSize = Math.Max((int)Math.Round(Math.Clamp(_style.FontSize, 8, 400) * _textScale), 6);
        var outline = Math.Round(Math.Clamp(_style.OutlineThickness, 0, 40) * _textScale, 1);
        var family = new FontFamily(string.IsNullOrWhiteSpace(_style.FontName) ? "Arial" : _style.FontName.Replace(",", " ").Trim());
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var (ascent, cell) = typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.Height > 0 ? (glyphs.Baseline, glyphs.Height) : (family.Baseline, family.LineSpacing);
        var em = fontSize / cell;
        var baseline = height - height / 12.0 - (cell - ascent) * em;
        var (primary, highlight) = (Frozen(Colour(_style.PrimaryColor, Colors.White)), Frozen(Colour(_style.HighlightColor, Color.FromRgb(0xFF, 0xE6, 0x00))));
        var pen = outline > 0 ? FrozenPen(Colour(_style.OutlineColor, Colors.Black), 2 * outline) : null;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string word, double size) => new(word, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, primary, pixelsPerDip);

        var space = Text(" ", em).WidthIncludingTrailingWhitespace;
        var texts = new FormattedText[words.Count];
        var across = 0.0;
        for (var w = 0; w < texts.Length; w++)
        {
            texts[w] = Text(words[w].Text, em * (pop && w == spoken ? 1.15 : 1));
            across += texts[w].WidthIncludingTrailingWhitespace + (w + 1 < texts.Length ? space : 0);
        }

        // Karaoke: unsung line, then the highlighted line clipped at the sweep, which crosses each word plus its trailing space
        // while spoken. Pop: single pass with the spoken word highlighted.
        var geometries = new Geometry[texts.Length];
        var (x, sweep) = ((width - across) / 2, 0.0);
        for (var w = 0; w < texts.Length; w++)
        {
            var advance = texts[w].WidthIncludingTrailingWhitespace + (w + 1 < texts.Length ? space : 0);
            geometries[w] = texts[w].BuildGeometry(new Point(x, baseline - texts[w].Baseline));
            if (w == spoken)
                sweep = x + through * advance;
            x += advance;
        }

        void Draw(Func<int, Brush> fill)
        {
            for (var w = 0; w < geometries.Length; w++)
            {
                if (pen is not null)
                    drawing.DrawGeometry(null, pen, geometries[w]);
                drawing.DrawGeometry(fill(w), null, geometries[w]);
            }
        }

        if (pop)
        {
            Draw(w => w == spoken ? highlight : primary);
            return;
        }

        Draw(_ => primary);
        if (sweep <= 0)
            return;

        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Min(sweep, width), height)));
        Draw(_ => highlight);
        drawing.Pop();
    }

    // Active picture subtitle: numbered placeholder bar in the subtitle area.
    private void DrawPicture(DrawingContext drawing, int cue)
    {
        var (width, height) = _box;
        var bar = new Rect(width * 0.3, height - height / 12.0 - height * 0.09, width * 0.4, height * 0.09);
        drawing.DrawRoundedRectangle(PictureBar, PictureEdge, bar, bar.Height / 4, bar.Height / 4);
        var text = new FormattedText($"Picture subtitle {(cue + 1).ToString(CultureInfo.InvariantCulture)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), bar.Height * 0.45, PictureText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(text, new Point(bar.X + (bar.Width - text.Width) / 2, bar.Y + (bar.Height - text.Height) / 2));
    }

    private static readonly SolidColorBrush PictureBar = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush PictureText = Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
    private static readonly Pen PictureEdge = FrozenPen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 2);

    private static Color Colour(string? text, Color fallback)
    {
        var hex = (text ?? "").Trim().TrimStart('#');
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
