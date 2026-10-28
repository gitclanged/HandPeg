using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// How a text layer's words lie in its box, at the size its font is set to: the box, in pixels, and where each
/// line's baseline is in it. The box is what the layer is on the frame, so it is measured here, with the
/// font's own figures, and FFmpeg's drawtext is then told where to put each line; a line is centred in the
/// box, which has room to spare at the sides for the little that two text engines differ by.
/// </summary>
/// <param name="Width">The box, in pixels of the output frame at the font's set size. Even.</param>
/// <param name="LineHeight">From one line's baseline to the next.</param>
/// <param name="Baseline">From the top of a line to its baseline.</param>
/// <param name="FontFile">The font's file, for drawtext's fontfile; empty when it is not a file drawtext can open by itself.</param>
public sealed record TextLayout(int Width, int Height, double LineHeight, double Baseline, string FontFile, string FontName, IReadOnlyList<string> Lines)
{
    public const string DefaultFont = "Segoe UI";
    public const double SmallestSize = 8, LargestSize = 400, DefaultSize = 48;
    public const int LargestOutline = 20;

    /// <summary>The fonts installed on this computer, by name, for the list a text layer's font is chosen from.</summary>
    public static IReadOnlyList<string> SystemFonts => _systemFonts.Value;

    private static readonly Lazy<IReadOnlyList<string>> _systemFonts = new(() =>
        Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => n.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList());

    private static readonly LruCache<string, TextLayout> Cache = new(64);

    /// <summary>The top of the first line in a box of the layout's height: the lines are in the middle of it.</summary>
    public double Top => (Height - Lines.Count * LineHeight) / 2;

    public static TextLayout Measure(Layer layer) =>
        Measure(layer.TextContent, layer.FontFamily, layer.FontSize, layer.OutlineThickness);

    public static TextLayout Measure(string? content, string? family, double size, int outline)
    {
        family = string.IsNullOrWhiteSpace(family) ? DefaultFont : family.Trim();
        size = double.IsFinite(size) ? Math.Clamp(size, SmallestSize, LargestSize) : DefaultSize;
        outline = Math.Clamp(outline, 0, LargestOutline);

        var lines = SplitLines(content);
        var key = string.Join('\u001F', [family, size.ToString("R", CultureInfo.InvariantCulture), outline.ToString(CultureInfo.InvariantCulture), .. lines]);
        if (Cache.TryGetValue(key, out var kept))
            return kept;

        var fontFamily = new FontFamily(family);
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var widest = 0.0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var text = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.0);
            widest = Math.Max(widest, text.WidthIncludingTrailingWhitespace);
        }

        // Room around the words: the outline, and a margin for strokes that reach past their own advance.
        var (padX, padY) = (Math.Ceiling(size * 0.25) + outline, Math.Ceiling(size * 0.15) + outline);
        var lineHeight = fontFamily.LineSpacing * size;
        var layout = new TextLayout(
            Even(Math.Max(widest, size / 2) + 2 * padX), Even(lines.Count * lineHeight + 2 * padY),
            lineHeight, fontFamily.Baseline * size, FindFontFile(typeface), family, lines);

        Cache[key] = layout;
        return layout;
    }

    /// <summary>
    /// The text as lines. Each line is drawn by a drawtext of its own, so no line break ever has to be written
    /// into a filter graph. What cannot be drawn (control characters) is left out, and a tab is four spaces.
    /// </summary>
    private static List<string> SplitLines(string? content)
    {
        var lines = (content ?? "").ReplaceLineEndings("\n").Split('\n')
            .Select(line => new string(line.Replace("\t", "    ").Where(c => !char.IsControl(c)).ToArray()).TrimEnd())
            .ToList();
        while (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// The file the font is read from. drawtext opens the first face of a file, so a font that is a later face
    /// of a collection (.ttc) is left to be found by its name instead.
    /// </summary>
    private static string FindFontFile(Typeface typeface)
    {
        try
        {
            if (typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.FontUri is { IsFile: true } uri
                && uri.Fragment.TrimStart('#') is "" or "0" && File.Exists(uri.LocalPath))
            {
                return uri.LocalPath;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException)
        {
            // Found by name, then.
        }

        return "";
    }

    private static int Even(double value) => Math.Max((int)Math.Ceiling(value / 2) * 2, 2);
}
