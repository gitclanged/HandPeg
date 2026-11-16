using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HandPegApp.Models;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// Sample caption preview for a style: font, primary/highlight/outline colours, outline width, and the animation state with one
/// word being spoken. An approximation of the exported captions for visual selection.
/// </summary>
public sealed class CaptionPreview : FrameworkElement
{
    private static readonly string[] Sample = ["NO", "WAY", "HE", "MISSED"];

    // Index of the sample word currently "spoken".
    private int _spokenWord = 2;

    /// <summary>Spoken sample word index (0 to 3); the Settings preview stage advances it to simulate playback.</summary>
    public int SpokenWord
    {
        get => _spokenWord;
        set
        {
            _spokenWord = value;
            InvalidateVisual();
        }
    }

    private CaptionStyle _style = new();

    /// <summary>Redraws with a snapshot of the style; later style mutations require another call.</summary>
    public void Show(CaptionStyle style)
    {
        _style = style.Clone();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRoundedRectangle(Backdrop, null, new Rect(RenderSize), 6, 6);

        var words = Sample.Take(Math.Clamp(_style.MaxWordsPerLine, 2, Sample.Length)).ToArray();
        var isPop = _style.Animation == CaptionStyle.TikTokPop;
        var primary = Brush(_style.PrimaryColor, Colors.White);
        var highlight = Brush(_style.HighlightColor, Colors.Yellow);

        // Fixed 0.42 scale so the largest style fits; smaller styles stay proportional.
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

        // Scale down to fit when the line exceeds the width.
        var fit = Math.Min(1, (ActualWidth - 16) / Math.Max(total, 1));
        drawing.PushTransform(new ScaleTransform(fit, fit, ActualWidth / 2, ActualHeight / 2));

        var x = (ActualWidth - total) / 2;
        for (var i = 0; i < texts.Count; i++)
        {
            // Karaoke: words before the spoken one are highlighted. Pop: only the spoken word.
            var isLit = isPop ? i == SpokenWord : i < SpokenWord;
            var geometry = texts[i].BuildGeometry(new Point(x, (ActualHeight - texts[i].Height) / 2));
            if (outline.Thickness > 0)
                drawing.DrawGeometry(null, outline, geometry);
            drawing.DrawGeometry(isLit ? highlight : primary, null, geometry);
            x += texts[i].Width + space;
        }

        drawing.Pop();
    }

    private static readonly SolidColorBrush Backdrop = Frozen(Color.FromRgb(0x3A, 0x4A, 0x5A));

    private static SolidColorBrush Brush(string color, Color fallback) => Frozen(TryParse(color, out var parsed) ? parsed : fallback);

    /// <summary>Parses #RRGGBB.</summary>
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
