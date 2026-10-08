using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Draws a sample caption in a given style: the font, the three colours, the outline, and the moment of
/// the animation where one word is being spoken. An imitation of what FFmpeg will draw, for choosing by eye.
/// </summary>
public sealed class CaptionPreview : FrameworkElement
{
    private static readonly string[] Sample = ["NO", "WAY", "HE", "MISSED"];

    // Which sample word is "being spoken" in the picture.
    private const int SpokenWord = 2;

    private CaptionStyle _style = new();

    /// <summary>Redraws with a style. The style is read at once; later changes to it need another call.</summary>
    public void Show(CaptionStyle style)
    {
        _style = style.Clone();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5A)), null, new Rect(RenderSize), 6, 6);

        var words = Sample.Take(Math.Clamp(_style.MaxWordsPerLine, 2, Sample.Length)).ToArray();
        var isPop = _style.Animation == CaptionStyle.TikTokPop;
        var primary = Brush(_style.PrimaryColor, Colors.White);
        var highlight = Brush(_style.HighlightColor, Colors.Yellow);

        // Sized so that the largest style still fits the box; smaller styles are shown in proportion.
        var size = Math.Clamp(_style.FontSize, 8, 400) * 0.42;
        var outline = new Pen(Brush(_style.OutlineColor, Colors.Black), Math.Clamp(_style.OutlineThickness, 0, 40) * 0.42 * 2) { LineJoin = PenLineJoin.Round };
        var typeface = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(_style.FontName) ? "Arial" : _style.FontName),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string word, double scale) =>
            new(word, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size * scale, Brushes.White, pixelsPerDip);

        var space = Text(" ", 1).WidthIncludingTrailingWhitespace;
        var texts = words.Select((word, index) => Text(word, isPop && index == SpokenWord ? 1.15 : 1)).ToList();
        var total = texts.Sum(t => t.Width) + space * (texts.Count - 1);

        // Shrunk to fit when the line is wider than the box.
        var fit = Math.Min(1, (ActualWidth - 16) / Math.Max(total, 1));
        drawing.PushTransform(new ScaleTransform(fit, fit, ActualWidth / 2, ActualHeight / 2));

        var x = (ActualWidth - total) / 2;
        for (var i = 0; i < texts.Count; i++)
        {
            // Karaoke: the words up to the one being spoken have already turned. Pop: only the spoken one.
            var isLit = isPop ? i == SpokenWord : i < SpokenWord;
            var geometry = texts[i].BuildGeometry(new Point(x, (ActualHeight - texts[i].Height) / 2));
            if (outline.Thickness > 0)
                drawing.DrawGeometry(null, outline, geometry);
            drawing.DrawGeometry(isLit ? highlight : primary, null, geometry);
            x += texts[i].Width + space;
        }

        drawing.Pop();
    }

    private static SolidColorBrush Brush(string color, Color fallback) =>
        new(TryParse(color, out var parsed) ? parsed : fallback);

    /// <summary>Reads #RRGGBB.</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        var hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
            return false;

        color = Color.FromRgb(Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        return true;
    }
}
