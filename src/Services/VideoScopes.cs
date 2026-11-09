namespace HandPegApp.Services;

/// <summary>The three scopes the Video Scopes pane draws (5.4C).</summary>
public enum VideoScopeKind { Histogram, LumaWaveform, RgbParade }

/// <summary>
/// The Video Scopes' maths (5.4C): pure, over a frame of BGRA8 bytes (its alpha never read), as the player composes it
/// (GpuPreviewEngine.CaptureThumbnailAsync, a small frame while paused). It counts the levels (the Histogram: red, green, blue
/// and the luma; the Luma Waveform: the luma of each column; the RGB Parade: each channel's levels per column, side by side)
/// and draws the counts into a picture of Bgr32 pixels, one row per level (level 255 at the top), on a dark ground with a
/// graticule every quarter. The luma is BT.709's (0.2126 R + 0.7152 G + 0.0722 B), in whole numbers that keep white 255.
/// Nothing here touches WPF: the pane writes the pixels into its one bitmap, reused.
/// </summary>
public static class VideoScopes
{
    /// <summary>BT.709's luma weights.</summary>
    public const double LumaRed = 0.2126, LumaGreen = 0.7152, LumaBlue = 0.0722;

    // The same weights in 65536ths, rounded so that they add up to 65536 exactly: white stays 255, black 0.
    private const int WeightRed = 13933, WeightGreen = 46871, WeightBlue = 4732;

    /// <summary>The rows of a scope's picture: one per level.</summary>
    public const int Levels = 256;

    /// <summary>The Luma Waveform's columns, and each channel's in the RGB Parade (with <see cref="ParadeGap"/> between them).</summary>
    public const int WaveformColumns = 384, ParadeColumns = 128, ParadeGap = 8;

    // The ground, and the graticule's lines at 0, 25, 50, 75 and 100 per cent.
    private const uint Ground = 0x00101014, Graticule = 0x00343440;

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
    /// The columns of the scope a column of the frame goes into: one when the scope has no more columns than the frame (several of the
    /// frame's share it), else the run of them it spans, so a narrow frame (a 9:16 one) leaves no empty columns.
    /// </summary>
    public static (int First, int End) ColumnsOf(int x, int width, int columns)
    {
        var first = (int)((long)x * columns / width);
        return (first, Math.Max((int)((long)(x + 1) * columns / width), first + 1));
    }

    /// <summary>How many of the frame's pixels a column of the scope holds: what makes a column's dot full.</summary>
    public static int PixelsPerColumn(int width, int height, int columns) => Math.Max(width * height / Math.Max(columns, 1), height);

    /// <summary>The luma of each column of the frame counted: [column, level], the frame's columns shared out over <paramref name="columns"/>.</summary>
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

    /// <summary>The picture of a scope of the frame: its Bgr32 pixels (0x00RRGGBB), row by row from the top, and its size.</summary>
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

    /// <summary>The RGB Parade's picture width: three channels of <see cref="ParadeColumns"/> with a gap between each.</summary>
    public const int ParadeWidth = 3 * ParadeColumns + 2 * ParadeGap;

    /// <summary>The row of the picture a level is drawn on: 255 at the top, 0 at the bottom.</summary>
    public static int RowOf(int level) => Levels - 1 - Math.Clamp(level, 0, Levels - 1);

    /// <summary>How bright a waveform's dot is for a count, 0 to 255: the share of the column's pixels at that level, brought up steeply so a few pixels still show.</summary>
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
    private static void Plot(uint[] pixels, int width, int x, int level, int count, int perColumn, (double R, double G, double B) tint)
    {
        if (count <= 0)
            return;

        var light = Brightness(count, perColumn);
        var at = RowOf(level) * width + x;
        var under = pixels[at];
        var r = Math.Max((int)(light * tint.R), (int)(under >> 16) & 0xFF);
        var g = Math.Max((int)(light * tint.G), (int)(under >> 8) & 0xFF);
        var b = Math.Max((int)(light * tint.B), (int)under & 0xFF);
        pixels[at] = (uint)((r << 16) | (g << 8) | b);
    }

    /// <summary>
    /// The Histogram drawn: a column per level, the luma a grey fill behind and the three channels over it, each lit where its bar
    /// reaches, so where they overlap is white. Scaled to the tallest level but black and white, which a letterbox or a white sky
    /// would leave everything else flat under.
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
