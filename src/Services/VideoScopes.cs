namespace HandPegApp.Services;

/// <summary>Video Scopes pane scope kinds (append-only so existing values keep their numbers).</summary>
public enum VideoScopeKind { Histogram, LumaWaveform, RgbParade, Vectorscope }

/// <summary>
/// Pure Video Scopes math over BGRA8 frames (alpha ignored) from GpuPreviewEngine.CaptureThumbnailAsync (small paused frame). Builds
/// Histogram (R, G, B, luma), Luma Waveform (per-column luma) and RGB Parade (per-channel columns) as Bgr32 images, one row per level
/// (255 at top) on a dark ground with quarter graticules. Luma is integer BT.709 (0.2126 R + 0.7152 G + 0.0722 B, white = 255).
/// Vectorscope: BT.709 Cb (x) / Cr (y) density with range graticule, 75 % circle, crosshair, 75/100 % primary targets and the skin-tone
/// I-bar through the centre. WPF-free; the pane copies pixels into one reused bitmap (the Histogram it draws as the YRGB overlay over
/// <see cref="Histogram"/>'s counts, YrgbHistogram; <see cref="Render"/>'s Histogram bitmap stays for other callers).
/// </summary>
public static class VideoScopes
{
    /// <summary>BT.709's luma weights.</summary>
    public const double LumaRed = 0.2126, LumaGreen = 0.7152, LumaBlue = 0.0722;

    // The same weights in 65536ths, rounded so that they add up to 65536 exactly: white stays 255, black 0.
    private const int WeightRed = 13933, WeightGreen = 46871, WeightBlue = 4732;

    /// <summary>The rows of a scope's picture: one per level.</summary>
    public const int Levels = 256;

    /// <summary>Column counts for the Luma Waveform and each RGB Parade channel (separated by <see cref="ParadeGap"/>).</summary>
    public const int WaveformColumns = 384, ParadeColumns = 128, ParadeGap = 8;

    // The ground, and the graticule's lines at 0, 25, 50, 75 and 100 per cent.
    private const uint Ground = 0x00101014, Graticule = 0x00343440;

    /// <summary>Vectorscope side length; chroma range 0.5 maps to <see cref="VectorscopeMargin"/> inside the edges.</summary>
    public const int VectorscopeSize = 256, VectorscopeMargin = 8;

    /// <summary>BT.709 chroma scales: Cb = (B - Y) / 1.8556, Cr = (R - Y) / 1.5748, each -0.5..0.5 (matches PSHueSaturation).</summary>
    public const double CbScale = 1.8556, CrScale = 1.5748;

    /// <summary>
    /// Skin-tone line angle, degrees CCW from +Cb (between red 103 and yellow 175): the I-bar's skin side; the bar runs through the centre
    /// to <see cref="SkinToneDegrees"/> + 180 as well.
    /// </summary>
    public const double SkinToneDegrees = 123;

    // The skin-tone I-bar's colour, and the targets' (brighter than the graticule).
    private const uint SkinTone = 0x00E8A27C, TargetBox = 0x00707084;

    // The I-bar's blend over the picture: its skin side, and the fainter opposite side.
    private const double SkinToneBlend = 0.55, SkinToneOppositeBlend = 0.4;

    /// <summary>A pixel's BT.709 luma, 0 to 255, rounded to the nearest level.</summary>
    public static int Luma(int red, int green, int blue) => (WeightRed * red + WeightGreen * green + WeightBlue * blue + 32768) >> 16;

    /// <summary>The levels of a frame counted: [channel, level], red, green, blue, then the luma.</summary>
    public static int[,] Histogram(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var counts = new int[4, Levels];
        var pixels = Math.Min(width * height, bgra.Length / 4);
        for (var i = 0; i < pixels; i++)
        {
            var (b, g, r) = (bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2]);
            counts[0, r]++;
            counts[1, g]++;
            counts[2, b]++;
            counts[3, Luma(r, g, b)]++;
        }

        return counts;
    }

    /// <summary>
    /// The levels of a frame after a colour transform (sRGB 0..1 in and out, held within it), counted as <see cref="Histogram"/> counts
    /// them over every <paramref name="step"/>-th pixel of every <paramref name="step"/>-th row: a live post-grade histogram from the
    /// pre-grade still while a drag holds the GPU readback back (the still through the grade's own model, ColorMatch.Graded; a LUT is not
    /// modelled, so the next paused capture replaces it). Allocation-free per pixel; run it off the UI thread.
    /// </summary>
    public static int[,] HistogramOf(ReadOnlySpan<byte> bgra, int width, int height, Func<(double R, double G, double B), (double R, double G, double B)> transform, int step = 1)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var counts = new int[4, Levels];
        step = Math.Max(step, 1);
        var rows = width > 0 ? Math.Min(height, bgra.Length / 4 / width) : 0;
        for (var y = 0; y < rows; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var i = (y * width + x) * 4;
                var (r, g, b) = transform((bgra[i + 2] / 255.0, bgra[i + 1] / 255.0, bgra[i] / 255.0));
                var (red, green, blue) = (LevelOf(r), LevelOf(g), LevelOf(b));
                counts[0, red]++;
                counts[1, green]++;
                counts[2, blue]++;
                counts[3, Luma(red, green, blue)]++;
            }
        }

        return counts;
    }

    // A 0..1 value as its nearest of the 256 levels (non-finite reads 0).
    private static int LevelOf(double value) => double.IsFinite(value) ? (int)Math.Round(Math.Clamp(value, 0, 1) * (Levels - 1)) : 0;

    /// <summary>
    /// Scope column range for a frame column: one when downsampling, else the spanned run so narrow (9:16) frames leave no gaps.
    /// </summary>
    public static (int First, int End) ColumnsOf(int x, int width, int columns)
    {
        var first = (int)((long)x * columns / width);
        return (first, Math.Max((int)((long)(x + 1) * columns / width), first + 1));
    }

    /// <summary>How many of the frame's pixels a column of the scope holds: what makes a column's dot full.</summary>
    public static int PixelsPerColumn(int width, int height, int columns) => Math.Max(width * height / Math.Max(columns, 1), height);

    /// <summary>Per-column luma counts [column, level], frame columns distributed over <paramref name="columns"/>.</summary>
    public static int[,] LumaWaveform(ReadOnlySpan<byte> bgra, int width, int height, int columns)
    {
        var counts = new int[columns, Levels];
        if (width <= 0 || columns <= 0)
            return counts;

        var rows = Math.Min(height, bgra.Length / 4 / width);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                var level = Luma(bgra[at + 2], bgra[at + 1], bgra[at]);
                var (first, end) = ColumnsOf(x, width, columns);
                for (var column = first; column < end; column++)
                    counts[column, level]++;
            }
        }

        return counts;
    }

    /// <summary>Each channel's levels per column counted: [channel (red, green, blue), column, level].</summary>
    public static int[,,] RgbParade(ReadOnlySpan<byte> bgra, int width, int height, int columns)
    {
        var counts = new int[3, columns, Levels];
        if (width <= 0 || columns <= 0)
            return counts;

        var rows = Math.Min(height, bgra.Length / 4 / width);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                var (first, end) = ColumnsOf(x, width, columns);
                for (var column = first; column < end; column++)
                {
                    counts[0, column, bgra[at + 2]]++;
                    counts[1, column, bgra[at + 1]]++;
                    counts[2, column, bgra[at]]++;
                }
            }
        }

        return counts;
    }

    /// <summary>Renders a scope: Bgr32 pixels (0x00RRGGBB), top-down rows, plus size.</summary>
    public static (uint[] Pixels, int Width, int Height) Render(VideoScopeKind kind, ReadOnlySpan<byte> bgra, int width, int height)
    {
        switch (kind)
        {
            case VideoScopeKind.Histogram:
            {
                var pixels = NewPicture(Levels);
                DrawHistogram(Histogram(bgra, width, height), pixels);
                return (pixels, Levels, Levels);
            }

            case VideoScopeKind.RgbParade:
            {
                var wide = ParadeWidth;
                var pixels = NewPicture(wide);
                var counts = RgbParade(bgra, width, height, ParadeColumns);
                var perColumn = PixelsPerColumn(width, height, ParadeColumns);
                for (var channel = 0; channel < 3; channel++)
                {
                    var tint = channel switch { 0 => (1.0, 0.25, 0.25), 1 => (0.25, 1.0, 0.25), _ => (0.35, 0.45, 1.0) };
                    var left = channel * (ParadeColumns + ParadeGap);
                    for (var column = 0; column < ParadeColumns; column++)
                    {
                        for (var level = 0; level < Levels; level++)
                            Plot(pixels, wide, left + column, level, counts[channel, column, level], perColumn, tint);
                    }
                }

                return (pixels, wide, Levels);
            }

            case VideoScopeKind.Vectorscope:
                return (DrawVectorscope(bgra, width, height), VectorscopeSize, VectorscopeSize);

            default:
            {
                var pixels = NewPicture(WaveformColumns);
                var counts = LumaWaveform(bgra, width, height, WaveformColumns);
                var perColumn = PixelsPerColumn(width, height, WaveformColumns);
                for (var column = 0; column < WaveformColumns; column++)
                {
                    for (var level = 0; level < Levels; level++)
                        Plot(pixels, WaveformColumns, column, level, counts[column, level], perColumn, (0.8, 1.0, 0.85));
                }

                return (pixels, WaveformColumns, Levels);
            }
        }
    }

    /// <summary>A colour's BT.709 colour difference (each channel 0 to 255): Cb and Cr, each -0.5 to 0.5.</summary>
    public static (double Cb, double Cr) Chroma(int red, int green, int blue)
    {
        var y = (LumaRed * red + LumaGreen * green + LumaBlue * blue) / 255.0;
        return ((blue / 255.0 - y) / CbScale, (red / 255.0 - y) / CrScale);
    }

    /// <summary>Vectorscope cell for (Cb, Cr): +Cb right, +Cr up, neutral at centre, clamped to the square.</summary>
    public static (int X, int Y) VectorscopeCell(double cb, double cr, int size = VectorscopeSize)
    {
        var (centre, reach) = (size / 2.0, size / 2.0 - VectorscopeMargin);
        var x = (int)Math.Floor(centre + cb / 0.5 * reach);
        var y = (int)Math.Floor(centre - cr / 0.5 * reach);
        return (Math.Max(0, Math.Min(x, size - 1)), Math.Max(0, Math.Min(y, size - 1)));
    }

    /// <summary>Chroma density counts [x, y] over Vectorscope cells (<see cref="VectorscopeCell"/>).</summary>
    public static int[,] Vectorscope(ReadOnlySpan<byte> bgra, int width, int height, int size = VectorscopeSize)
    {
        var counts = new int[size, size];
        var pixels = Math.Min(width * height, bgra.Length / 4);
        for (var i = 0; i < pixels; i++)
        {
            var (cb, cr) = Chroma(bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);
            var (x, y) = VectorscopeCell(cb, cr, size);
            counts[x, y]++;
        }

        return counts;
    }

    /// <summary>Chroma targets for R, Mg, B, Cy, G, Yl at a level (1 = 100 %, 0.75 = 75 %).</summary>
    public static (string Name, double Cb, double Cr)[] Targets(double level)
    {
        var full = (int)Math.Round(255 * level);
        (string, double, double) Of(string name, int r, int g, int b)
        {
            var (cb, cr) = Chroma(r, g, b);
            return (name, cb, cr);
        }

        return [Of("R", full, 0, 0), Of("Mg", full, 0, full), Of("B", 0, 0, full), Of("Cy", 0, full, full), Of("G", 0, full, 0), Of("Yl", full, full, 0)];
    }

    /// <summary>Chroma angle in degrees CCW from +Cb, 0-360 (skin-tone line at <see cref="SkinToneDegrees"/>).</summary>
    public static double HueDegrees(double cb, double cr)
    {
        var degrees = Math.Atan2(cr, cb) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    /// <summary>The Vectorscope's picture: the graticule, then the counts as dots never darker than it.</summary>
    private static uint[] DrawVectorscope(ReadOnlySpan<byte> bgra, int width, int height)
    {
        const int size = VectorscopeSize;
        var pixels = new uint[size * size];
        Array.Fill(pixels, Ground);
        var (centre, reach) = (size / 2.0, size / 2.0 - VectorscopeMargin);

        // The crosshair, the colour difference's reach and the 75 % circle.
        for (var i = VectorscopeMargin; i < size - VectorscopeMargin; i++)
        {
            pixels[size / 2 * size + i] = Graticule;
            pixels[i * size + size / 2] = Graticule;
        }

        foreach (var radius in new[] { reach, reach * 0.75 })
        {
            var steps = (int)(4 * Math.PI * radius);
            for (var k = 0; k < steps; k++)
            {
                var angle = 2 * Math.PI * k / steps;
                SetPixel(pixels, size, (int)Math.Round(centre + radius * Math.Cos(angle)), (int)Math.Round(centre - radius * Math.Sin(angle)), Graticule);
            }
        }

        // The skin-tone I-bar: the in-phase axis through the centre, reach to reach, two pixels wide, blended over what is under it; the
        // skin side (SkinToneDegrees) warm, the opposite side (its -I half, toward cyan-blue) fainter. The skin half draws as it always has.
        var (cos, sin) = (Math.Cos(SkinToneDegrees * Math.PI / 180), Math.Sin(SkinToneDegrees * Math.PI / 180));
        for (var t = -reach; t <= reach; t += 0.5)
        {
            var amount = t >= 0 ? SkinToneBlend : SkinToneOppositeBlend;
            var (x, y) = (centre + t * cos, centre - t * sin);
            Blend(pixels, size, (int)Math.Round(x), (int)Math.Round(y), SkinTone, amount);
            Blend(pixels, size, (int)Math.Round(x - sin * 0.8), (int)Math.Round(y - cos * 0.8), SkinTone, amount);
        }

        // The targets: small boxes at 100 %, smaller at 75 %.
        foreach (var (level, half) in new[] { (1.0, 4), (0.75, 3) })
        {
            foreach (var (_, cb, cr) in Targets(level))
            {
                var (x, y) = VectorscopeCell(cb, cr, size);
                for (var d = -half; d <= half; d++)
                {
                    SetPixel(pixels, size, x + d, y - half, TargetBox);
                    SetPixel(pixels, size, x + d, y + half, TargetBox);
                    SetPixel(pixels, size, x - half, y + d, TargetBox);
                    SetPixel(pixels, size, x + half, y + d, TargetBox);
                }
            }
        }

        // Dot brightness scales with frame share (1/64 is bright), never darker than the graticule.
        var counts = Vectorscope(bgra, width, height, size);
        var perCell = Math.Max(Math.Min(width * height, bgra.Length / 4) / 64, 1);
        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
                PlotAt(pixels, size, x, y, counts[x, y], perCell, (0.8, 1.0, 0.85));
        }

        return pixels;
    }

    private static void SetPixel(uint[] pixels, int size, int x, int y, uint colour)
    {
        if (x >= 0 && x < size && y >= 0 && y < size)
            pixels[y * size + x] = colour;
    }

    private static void Blend(uint[] pixels, int size, int x, int y, uint colour, double amount)
    {
        if (x < 0 || x >= size || y < 0 || y >= size)
            return;
        var under = pixels[y * size + x];
        int Mix(int shift) => (int)Math.Round(((under >> shift) & 0xFF) * (1 - amount) + ((colour >> shift) & 0xFF) * amount);
        pixels[y * size + x] = (uint)((Mix(16) << 16) | (Mix(8) << 8) | Mix(0));
    }

    /// <summary>RGB Parade width: three <see cref="ParadeColumns"/> channels plus gaps.</summary>
    public const int ParadeWidth = 3 * ParadeColumns + 2 * ParadeGap;

    /// <summary>The row of the picture a level is drawn on: 255 at the top, 0 at the bottom.</summary>
    public static int RowOf(int level) => Levels - 1 - Math.Clamp(level, 0, Levels - 1);

    /// <summary>Waveform dot brightness 0-255 from the column share at a level, with a steep curve so sparse pixels remain visible.</summary>
    public static int Brightness(int count, int perColumn) =>
        count <= 0 ? 0 : (int)Math.Round(255 * (1 - Math.Exp(-48.0 * count / Math.Max(perColumn, 1))));

    // A dark picture with the graticule drawn across it.
    private static uint[] NewPicture(int width)
    {
        var pixels = new uint[width * Levels];
        Array.Fill(pixels, Ground);
        foreach (var level in new[] { 0, 64, 128, 191, 255 })
            Array.Fill(pixels, Graticule, RowOf(level) * width, width);
        return pixels;
    }

    // A waveform's dot: brighter than the ground or the graticule under it, never darker.
    private static void Plot(uint[] pixels, int width, int x, int level, int count, int perColumn, (double R, double G, double B) tint) =>
        PlotAt(pixels, width, x, RowOf(level), count, perColumn, tint);

    // Plots a dot (waveform level or Vectorscope cell) using max blending with the existing pixel.
    private static void PlotAt(uint[] pixels, int width, int x, int row, int count, int perColumn, (double R, double G, double B) tint)
    {
        if (count <= 0)
            return;

        var light = Brightness(count, perColumn);
        var at = row * width + x;
        var under = pixels[at];
        var r = Math.Max((int)(light * tint.R), (int)(under >> 16) & 0xFF);
        var g = Math.Max((int)(light * tint.G), (int)(under >> 8) & 0xFF);
        var b = Math.Max((int)(light * tint.B), (int)under & 0xFF);
        pixels[at] = (uint)((r << 16) | (g << 8) | b);
    }

    /// <summary>
    /// Draws the Histogram: one column per level, grey luma fill behind additive R/G/B bars (overlap = white). Scaled to the tallest
    /// level excluding pure black and white, so letterboxes or blown skies do not flatten the rest.
    /// </summary>
    private static void DrawHistogram(int[,] counts, uint[] pixels)
    {
        var tallest = 1;
        for (var channel = 0; channel < 4; channel++)
        {
            for (var level = 1; level < Levels - 1; level++)
                tallest = Math.Max(tallest, counts[channel, level]);
        }

        Span<int> bars = stackalloc int[4];
        for (var x = 0; x < Levels; x++)
        {
            for (var channel = 0; channel < 4; channel++)
                bars[channel] = (int)Math.Min((long)counts[channel, x] * Levels / tallest, Levels);

            for (var up = 0; up < Levels; up++)
            {
                var at = (Levels - 1 - up) * Levels + x;
                var grey = up < bars[3] ? 0x40 : 0;
                var (r, g, b) = (up < bars[0] ? 0xE6 : grey, up < bars[1] ? 0xE6 : grey, up < bars[2] ? 0xE6 : grey);
                if (r + g + b > 0)
                    pixels[at] = (uint)((r << 16) | (g << 8) | b);
            }
        }
    }
}
