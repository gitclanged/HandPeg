namespace HandPegApp.Services;

/// <summary>The scopes the Video Scopes pane draws (5.4.0; the Vectorscope 5.6.0, appended so the others keep their numbers).</summary>
public enum VideoScopeKind { Histogram, LumaWaveform, RgbParade, Vectorscope }

/// <summary>
/// The Video Scopes' maths (5.4.0): pure, over a frame of BGRA8 bytes (its alpha never read), as the player composes it
/// (GpuPreviewEngine.CaptureThumbnailAsync, a small frame while paused). It counts the levels (the Histogram: red, green, blue
/// and the luma; the Luma Waveform: the luma of each column; the RGB Parade: each channel's levels per column, side by side)
/// and draws the counts into a picture of Bgr32 pixels, one row per level (level 255 at the top), on a dark ground with a
/// graticule every quarter. The luma is BT.709's (0.2126 R + 0.7152 G + 0.0722 B), in whole numbers that keep white 255.
/// 5.6.0, the Vectorscope: each pixel's BT.709 colour difference (Cb across, Cr up) counted into a square, over a graticule of the
/// reach of the colour difference, the 75 % circle, a crosshair, the primaries' targets at 75 % and 100 %, and the skin-tone line.
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

    /// <summary>The Vectorscope's picture: a square of this side, the colour difference's reach (0.5) <see cref="VectorscopeMargin"/> in from its edges.</summary>
    public const int VectorscopeSize = 256, VectorscopeMargin = 8;

    /// <summary>BT.709's colour-difference scales: Cb = (B - Y) / 1.8556, Cr = (R - Y) / 1.5748, each -0.5 to 0.5 (as PSHueSaturation's).</summary>
    public const double CbScale = 1.8556, CrScale = 1.5748;

    /// <summary>The skin-tone line's angle, degrees counter-clockwise from +Cb (between red's 103 and yellow's 175): most skin falls near it.</summary>
    public const double SkinToneDegrees = 123;

    // The skin-tone line's colour, and the targets' (brighter than the graticule).
    private const uint SkinTone = 0x00E8A27C, TargetBox = 0x00707084;

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

    /// <summary>The Vectorscope's cell of a colour difference: +Cb to the right, +Cr up, grey at the centre; kept inside the square.</summary>
    public static (int X, int Y) VectorscopeCell(double cb, double cr, int size = VectorscopeSize)
    {
        var (centre, reach) = (size / 2.0, size / 2.0 - VectorscopeMargin);
        var x = (int)Math.Floor(centre + cb / 0.5 * reach);
        var y = (int)Math.Floor(centre - cr / 0.5 * reach);
        return (Math.Max(0, Math.Min(x, size - 1)), Math.Max(0, Math.Min(y, size - 1)));
    }

    /// <summary>The colour difference of a frame counted: [x, y] cells of the Vectorscope's square (<see cref="VectorscopeCell"/>).</summary>
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

    /// <summary>The primaries' targets (red, magenta, blue, cyan, green, yellow) at a level (1 for 100 %, 0.75 for 75 %): their colour differences.</summary>
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

    /// <summary>A colour difference's angle, degrees counter-clockwise from +Cb, 0 to 360 (the skin-tone line is at <see cref="SkinToneDegrees"/>).</summary>
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

        // The skin-tone line, from the centre to the reach, two pixels wide, blended over what is under it.
        var (cos, sin) = (Math.Cos(SkinToneDegrees * Math.PI / 180), Math.Sin(SkinToneDegrees * Math.PI / 180));
        for (var t = 0.0; t <= reach; t += 0.5)
        {
            var (x, y) = (centre + t * cos, centre - t * sin);
            Blend(pixels, size, (int)Math.Round(x), (int)Math.Round(y), SkinTone, 0.55);
            Blend(pixels, size, (int)Math.Round(x - sin * 0.8), (int)Math.Round(y - cos * 0.8), SkinTone, 0.55);
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

        // The dots: a cell's brightness grows with its share of the frame (a 64th of it is bright), never darker than the graticule.
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
    private static void Plot(uint[] pixels, int width, int x, int level, int count, int perColumn, (double R, double G, double B) tint) =>
        PlotAt(pixels, width, x, RowOf(level), count, perColumn, tint);

    // A dot at a row and column of the picture (the Vectorscope's cells as the waveforms' levels): never darker than what is under it.
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
