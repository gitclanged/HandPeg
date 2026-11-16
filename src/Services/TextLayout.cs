using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Text layer layout at its font size: box size in output pixels and per-line baselines, measured from the font's own metrics. Lines are
/// centred with side margin to absorb small differences between text renderers.
/// </summary>
/// <param name="Width">Box width in output pixels at the set font size (even).</param>
/// <param name="LineHeight">Baseline-to-baseline distance.</param>
/// <param name="Baseline">Line top to baseline.</param>
/// <param name="FontFile">Font file path; empty when not a standalone first-face file.</param>
public sealed record TextLayout(int Width, int Height, double LineHeight, double Baseline, string FontFile, string FontName, IReadOnlyList<string> Lines)
{
    public const string DefaultFont = Models.TextDefaults.Font;
    public const double SmallestSize = Models.TextDefaults.SmallestSize, LargestSize = Models.TextDefaults.LargestSize, DefaultSize = Models.TextDefaults.Size;
    public const int LargestOutline = Models.TextDefaults.LargestOutline;

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
    /// Splits text into lines, dropping control characters and expanding tabs to four spaces.
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
    /// Font file path; empty for non-first faces of a .ttc collection (resolved by family name instead).
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
