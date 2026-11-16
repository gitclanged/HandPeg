using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// Mask bake recipe, step for step as the export's mask chain: the picture read by luma or alpha, the region under the layer cropped,
/// scaled to the layer size, inverted, grown or shrunk by N passes, offset, and its outline softened, all in layer pixels.
/// </summary>
/// <param name="Cut">Region of the mask picture under the layer, as fractions: left, top, width, height.</param>
/// <param name="Grow">Passes of 3x3 dilation (positive) or erosion (negative).</param>
/// <param name="OffsetX">Mask offset inside the layer in its pixels; uncovered areas are hidden.</param>
/// <param name="BlurRadius">Radius of the box blur run twice over the outline (Soft Edges); 0 for none.</param>
public readonly record struct MaskRecipe(
    string Path, bool FromAlpha, int Width, int Height, (double X, double Y, double Width, double Height) Cut,
    bool Invert, int Grow, int OffsetX, int OffsetY, int BlurRadius)
{
    /// <summary>Content hash of the recipe including the file's size and last write (a redrawn mask is a new mask).</summary>
    public long ContentHash()
    {
        // Drawn masks are already keyed by their outline hash: no file to stat.
        var file = MaskStore.IsDrawn(Path) ? default : FileState.Get(Path);
        return default(GpuHash).Add("mask").Add(Path).Add(file.Length).Add(file.WrittenUtc.Ticks).Add(FromAlpha).Add(Width).Add(Height)
            .Add(Cut.X).Add(Cut.Y).Add(Cut.Width).Add(Cut.Height).Add(Invert).Add(Grow).Add(OffsetX).Add(OffsetY).Add(BlurRadius).Value;
    }

    /// <summary>Hash of the recipe without its size and pixel-measured steps: an older bake of the same family stands in while a new one is made.</summary>
    public long FamilyHash() =>
        default(GpuHash).Add("mask-family").Add(Path).Add(FromAlpha).Add(Cut.X).Add(Cut.Y).Add(Cut.Width).Add(Cut.Height).Add(Invert).Value;
}

/// <summary>
/// Bakes masks and decodes stills for the GPU engine on the CPU: what PSLayerMask's single pass cannot match the export on (above all the
/// soft edge, a box blur run twice) is done here once and uploaded as a <see cref="GpuResourceKind.Mask2D"/>, then drawn with zero grow,
/// invert and offset. Grow and blur are separable and radius-independent (van Herk / Gil-Werman for the square max, running sums for the
/// box). Thread-safe: every call uses its own arrays (runs on a background lane).
/// </summary>
public static class MaskBaker
{
    /// <summary>Longest side kept for stills read from files (the worker scales them to their box).</summary>
    public const int LargestStillSide = 4096;

    /// <summary>Bakes the mask as BGRA8 (value in every channel, opaque) at the recipe's size; null when the picture cannot be read.</summary>
    public static byte[]? Bake(in MaskRecipe recipe)
    {
        var (width, height) = (Math.Clamp(recipe.Width, 1, 16384), Math.Clamp(recipe.Height, 1, 16384));
        float[] mask;
        if (MaskStore.IsDrawn(recipe.Path))
        {
            // Drawn mask: outlines rasterized in memory, the region under the layer at exactly the layer size.
            if (MaskStore.Rasterize(recipe.Path, recipe.Cut, width, height) is not { } drawn)
                return null;
            mask = drawn;
        }
        else
        {
            if (ReadGrey(recipe.Path, recipe.FromAlpha) is not { } picture)
                return null;

            var (grey, sourceWidth, sourceHeight) = picture;

            // Region under the layer (crop: x and y truncated, each side at least 1 px).
            var cutX = Math.Clamp((int)(sourceWidth * Math.Clamp(recipe.Cut.X, 0, 1)), 0, sourceWidth - 1);
            var cutY = Math.Clamp((int)(sourceHeight * Math.Clamp(recipe.Cut.Y, 0, 1)), 0, sourceHeight - 1);
            var cutWidth = Math.Clamp((int)(sourceWidth * Math.Clamp(recipe.Cut.Width, 0, 1)), 1, sourceWidth - cutX);
            var cutHeight = Math.Clamp((int)(sourceHeight * Math.Clamp(recipe.Cut.Height, 0, 1)), 1, sourceHeight - cutY);

            mask = Resample(grey, sourceWidth, cutX, cutY, cutWidth, cutHeight, width, height);
        }

        if (recipe.Invert)
        {
            for (var i = 0; i < mask.Length; i++)
                mask[i] = 1 - mask[i];
        }

        if (recipe.Grow != 0)
            Spread(mask, width, height, Math.Abs(recipe.Grow), recipe.Grow > 0);
        if (recipe.OffsetX != 0 || recipe.OffsetY != 0)
            mask = Shift(mask, width, height, recipe.OffsetX, recipe.OffsetY);
        if (recipe.BlurRadius > 0)
        {
            var radius = Math.Min(recipe.BlurRadius, Math.Max(Math.Min(width, height) / 2 - 1, 0));
            for (var pass = 0; pass < 2 && radius > 0; pass++)
            {
                BoxRows(mask, width, height, radius);
                BoxColumns(mask, width, height, radius);
            }
        }

        return Pack(mask);
    }

    /// <summary>
    /// Packs the mask as BGRA8: value x 255, rounded to even (as MathF.Round), clamped to 0..255, written to every channel, opaque.
    /// Vectorized with identical per-lane arithmetic, then scalar for the tail.
    /// </summary>
    private static byte[] Pack(float[] mask)
    {
        var pixels = new byte[mask.Length * 4];
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var values = MemoryMarshal.Cast<float, Vector<float>>(mask.AsSpan());
            var packed = MemoryMarshal.Cast<byte, Vector<int>>(pixels.AsSpan());
            var (scale, most, grey, solid) = (new Vector<float>(255), new Vector<int>(255), new Vector<int>(0x010101), new Vector<int>(unchecked((int)0xFF000000)));
            for (var v = 0; v < values.Length; v++)
                packed[v] = Vector.Min(Vector.Max(Vector.ConvertToInt32(Vector.Round(values[v] * scale)), Vector<int>.Zero), most) * grey | solid;
            i = values.Length * Vector<float>.Count;
        }

        for (; i < mask.Length; i++)
        {
            var v = (byte)Math.Clamp((int)MathF.Round(mask[i] * 255), 0, 255);
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (v, v, v, 255);
        }

        return pixels;
    }

    /// <summary>Decodes a picture file as BGRA8 straight alpha at native size (longest side at most <see cref="LargestStillSide"/>); null when unreadable.</summary>
    public static (byte[] Pixels, int Width, int Height)? ReadStill(string path)
    {
        if (Decode(path, LargestStillSide) is not { } bitmap)
            return null;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return (pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    // ----- Reading -----

    /// <summary>Picture as grey 0..1: its alpha, or its luma (FFmpeg format=gray: BT.601 weights).</summary>
    private static (float[] Grey, int Width, int Height)? ReadGrey(string path, bool fromAlpha)
    {
        if (Decode(path, 0) is not { } bitmap)
            return null;

        var (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var grey = new float[width * height];
        for (var i = 0; i < grey.Length; i++)
        {
            var (b, g, r, a) = (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]);
            grey[i] = fromAlpha ? a / 255f : (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
        }

        return (grey, width, height);
    }

    /// <summary>Decodes a picture file from a stream (never a URI, never cached) as frozen straight BGRA; null on failure.</summary>
    private static BitmapSource? Decode(string path, int largestSide)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (largestSide > 0 && Math.Max(frame.PixelWidth, frame.PixelHeight) > largestSide)
            {
                var scale = (double)largestSide / Math.Max(frame.PixelWidth, frame.PixelHeight);
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }

            var bgra = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
            return bgra.PixelWidth > 0 && bgra.PixelHeight > 0 ? bgra : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            AppLog.Write($"A picture for the GPU engine could not be read ({Path.GetFileName(path)})", ex);
            return null;
        }
    }

    // ----- Bake steps -----

    /// <summary>
    /// Scales a region of a grey picture: box average over the footprint when shrinking, linear interpolation when enlarging; horizontal,
    /// then vertical.
    /// </summary>
    private static float[] Resample(float[] source, int stride, int x0, int y0, int width, int height, int toWidth, int toHeight)
    {
        var rows = new float[height * toWidth];
        for (var y = 0; y < height; y++)
            Line(source, (y0 + y) * stride + x0, 1, width, rows, y * toWidth, 1, toWidth);

        var result = new float[toWidth * toHeight];
        Down(rows, toWidth, height, result, toHeight);
        return result;
    }

    // Vertical pass over all columns at once, row by row: each output row uses the same source rows and weights in every column, so columns
    // are processed side by side in memory order (a lone column walk misses cache per pixel), with exactly the arithmetic Line applies.
    private static void Down(float[] source, int width, int count, float[] target, int size)
    {
        var scale = (double)count / size;
        var sums = new double[scale > 1 ? width : 0];
        for (var i = 0; i < size; i++)
        {
            var to = target.AsSpan(i * width, width);
            if (scale > 1)
            {
                var (start, end) = (i * scale, (i + 1) * scale);
                var weight = 0.0;
                Array.Clear(sums);
                for (var k = (int)start; k < Math.Min((int)Math.Ceiling(end), count); k++)
                {
                    var w = Math.Min(end, k + 1) - Math.Max(start, k);
                    var row = source.AsSpan(k * width, width);
                    for (var x = 0; x < width; x++)
                        sums[x] += row[x] * w;
                    weight += w;
                }

                for (var x = 0; x < width; x++)
                    to[x] = (float)(weight > 0 ? sums[x] / weight : 0);
            }
            else
            {
                var at = Math.Clamp((i + 0.5) * scale - 0.5, 0, count - 1);
                var k = (int)at;
                var f = at - k;
                var row = source.AsSpan(k * width, width);
                var next = source.AsSpan(Math.Min(k + 1, count - 1) * width, width);
                for (var x = 0; x < width; x++)
                    to[x] = (float)(row[x] * (1 - f) + next[x] * f);
            }
        }
    }

    // One Resample line: count samples from `from` (stride `step`) into `size` samples at `to` (stride `toStep`).
    private static void Line(float[] source, int from, int step, int count, float[] target, int to, int toStep, int size)
    {
        var scale = (double)count / size;
        for (var i = 0; i < size; i++)
        {
            double value;
            if (scale > 1)
            {
                // Footprint [i, i + 1) * scale, each source pixel weighted by its overlap.
                var (start, end) = (i * scale, (i + 1) * scale);
                var (sum, weight) = (0.0, 0.0);
                for (var k = (int)start; k < Math.Min((int)Math.Ceiling(end), count); k++)
                {
                    var w = Math.Min(end, k + 1) - Math.Max(start, k);
                    sum += source[from + k * step] * w;
                    weight += w;
                }

                value = weight > 0 ? sum / weight : 0;
            }
            else
            {
                var at = Math.Clamp((i + 0.5) * scale - 0.5, 0, count - 1);
                var k = (int)at;
                var f = at - k;
                var next = Math.Min(k + 1, count - 1);
                value = source[from + k * step] * (1 - f) + source[from + next * step] * f;
            }

            target[to + i * toStep] = (float)value;
        }
    }

    /// <summary>
    /// N passes of 3x3 dilation (or erosion): max (min) within a square of radius <paramref name="passes"/>, edges clamped, matching repeated
    /// FFmpeg dilation/erosion. Separable van Herk / Gil-Werman: three comparisons per pixel at any radius. Columns are processed a vector
    /// width at a time (one column per lane, identical comparisons), reading runs of a row rather than a cache line per pixel.
    /// </summary>
    private static void Spread(float[] mask, int width, int height, int passes, bool grow)
    {
        var line = new float[Math.Max(width, height)];
        var output = new float[line.Length];
        var size = 2 * passes + 1;
        var block = (line.Length + 2 * passes + size - 1) / size * size;
        var (forward, backward) = (new float[block], new float[block]);
        for (var y = 0; y < height; y++)
        {
            Array.Copy(mask, y * width, line, 0, width);
            Extreme(line, output, width, passes, grow, forward, backward);
            Array.Copy(output, 0, mask, y * width, width);
        }

        var x = 0;
        if (Vector.IsHardwareAccelerated && width >= Vector<float>.Count)
        {
            var lanes = Vector<float>.Count;
            var (columns, extremes) = (new Vector<float>[height], new Vector<float>[height]);
            var (forwards, backwards) = (new Vector<float>[block], new Vector<float>[block]);
            for (; x <= width - lanes; x += lanes)
            {
                for (var y = 0; y < height; y++)
                    columns[y] = new Vector<float>(mask.AsSpan(y * width + x, lanes));
                Extreme(columns, extremes, height, passes, grow, forwards, backwards);
                for (var y = 0; y < height; y++)
                    extremes[y].CopyTo(mask.AsSpan(y * width + x, lanes));
            }
        }

        for (; x < width; x++)
        {
            for (var y = 0; y < height; y++)
                line[y] = mask[y * width + x];
            Extreme(line, output, height, passes, grow, forward, backward);
            for (var y = 0; y < height; y++)
                mask[y * width + x] = output[y];
        }
    }

    // Max (or min) of each 2r + 1 window along a line, edges clamped. The run buffers are the caller's, reused per line.
    private static void Extreme(float[] line, float[] output, int count, int radius, bool most, float[] forward, float[] backward)
    {
        var size = 2 * radius + 1;
        var padded = count + 2 * radius;
        var block = ((padded + size - 1) / size) * size;
        float At(int i) => line[Math.Clamp(i - radius, 0, count - 1)];
        float Pick(float a, float b) => most ? MathF.Max(a, b) : MathF.Min(a, b);

        // Per window (the block holds whole windows): a forward run from each start and a backward run from each end, carried in a
        // local rather than read back from the buffer.
        for (var start = 0; start < block; start += size)
        {
            var run = forward[start] = At(start);
            for (var i = start + 1; i < start + size; i++)
                run = forward[i] = Pick(run, At(i));
            run = backward[start + size - 1] = At(start + size - 1);
            for (var i = start + size - 2; i >= start; i--)
                run = backward[i] = Pick(run, At(i));
        }

        // Window [i, i + size - 1] of padded positions = backward run from i combined with the forward run to its end.
        for (var i = 0; i < count; i++)
            output[i] = Pick(backward[i], forward[Math.Min(i + size - 1, block - 1)]);
    }

    // Vector variant: each lane is its own line (Vector.Max/Min match MathF.Max/Min per lane).
    private static void Extreme(Vector<float>[] line, Vector<float>[] output, int count, int radius, bool most, Vector<float>[] forward, Vector<float>[] backward)
    {
        var size = 2 * radius + 1;
        var padded = count + 2 * radius;
        var block = ((padded + size - 1) / size) * size;
        Vector<float> At(int i) => line[Math.Clamp(i - radius, 0, count - 1)];
        Vector<float> Pick(Vector<float> a, Vector<float> b) => most ? Vector.Max(a, b) : Vector.Min(a, b);

        // Per window: forward and backward runs, as in the scalar variant.
        for (var start = 0; start < block; start += size)
        {
            var run = forward[start] = At(start);
            for (var i = start + 1; i < start + size; i++)
                run = forward[i] = Pick(run, At(i));
            run = backward[start + size - 1] = At(start + size - 1);
            for (var i = start + size - 2; i >= start; i--)
                run = backward[i] = Pick(run, At(i));
        }

        for (var i = 0; i < count; i++)
            output[i] = Pick(backward[i], forward[Math.Min(i + size - 1, block - 1)]);
    }

    /// <summary>Shifts the mask by (dx, dy) within its own size; uncovered areas are hidden (FFmpeg geq with between()).</summary>
    private static float[] Shift(float[] mask, int width, int height, int dx, int dy)
    {
        var moved = new float[mask.Length];
        for (var y = 0; y < height; y++)
        {
            var from = y - dy;
            if (from < 0 || from >= height)
                continue;
            for (var x = 0; x < width; x++)
            {
                var source = x - dx;
                if (source >= 0 && source < width)
                    moved[y * width + x] = mask[from * width + source];
            }
        }

        return moved;
    }

    // Box blur of radius r along each row (running sums, edges clamped), as boxblur's horizontal pass. Four rows at a time with
    // independent sums hide the add latency. Each row is copied with its end values repeated r + 1 times beyond each end (no clamping),
    // and sums the same values in the same order as Box, so results are bit-identical.
    private static void BoxRows(float[] mask, int width, int height, int radius)
    {
        var (pad, size) = (radius + 1, 2 * radius + 1);
        var stride = width + 2 * pad;
        var lines = new float[4 * stride];
        var y = 0;
        for (; y + 4 <= height; y += 4)
        {
            for (var n = 0; n < 4; n++)
            {
                var line = lines.AsSpan(n * stride, stride);
                line[..pad].Fill(mask[(y + n) * width]);
                mask.AsSpan((y + n) * width, width).CopyTo(line[pad..]);
                line[(pad + width)..].Fill(mask[(y + n) * width + width - 1]);
            }

            var (a, b, c, d) = (0.0, 0.0, 0.0, 0.0);
            for (var k = pad - radius; k <= pad + radius; k++)
                (a, b, c, d) = (a + lines[k], b + lines[stride + k], c + lines[2 * stride + k], d + lines[3 * stride + k]);

            var (at, take) = (y * width, pad - radius);
            for (var i = 0; i < width; i++, take++)
            {
                mask[at + i] = (float)(a / size);
                mask[at + width + i] = (float)(b / size);
                mask[at + 2 * width + i] = (float)(c / size);
                mask[at + 3 * width + i] = (float)(d / size);
                var add = take + size;
                a += lines[add] - lines[take];
                b += lines[stride + add] - lines[stride + take];
                c += lines[2 * stride + add] - lines[2 * stride + take];
                d += lines[3 * stride + add] - lines[3 * stride + take];
            }
        }

        for (; y < height; y++)
        {
            Array.Copy(mask, y * width, lines, 0, width);
            Box(lines, width, radius, mask, y * width, 1);
        }
    }

    // Vertical box pass (boxblur's vertical pass): all column sums kept side by side and advanced a row at a time, reading in memory order.
    // Each column sums the same values in the same order as Box (differences in float, then added), so results are bit-identical. The r + 1
    // rows a sum must still subtract are saved, round-robin, before being overwritten.
    private static void BoxColumns(float[] mask, int width, int height, int radius)
    {
        var (sums, kept, size) = (new double[width], new float[(radius + 1) * width], 2 * radius + 1);
        for (var k = -radius; k <= radius; k++)
            AddRow(sums, mask.AsSpan(Math.Clamp(k, 0, height - 1) * width, width));

        for (var y = 0; y < height; y++)
        {
            var row = mask.AsSpan(y * width, width);
            row.CopyTo(kept.AsSpan(y % (radius + 1) * width, width));
            WriteRow(sums, size, row);
            if (y < height - 1)
                MoveRow(sums, mask.AsSpan(Math.Min(y + radius + 1, height - 1) * width, width), kept.AsSpan(Math.Max(y - radius, 0) % (radius + 1) * width, width));
        }
    }

    // sums[x] += row[x], widened to double first (as Box's initial sum).
    private static void AddRow(Span<double> sums, ReadOnlySpan<float> row)
    {
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x <= row.Length - Vector<float>.Count; x += Vector<float>.Count)
            {
                Vector.Widen(new Vector<float>(row[x..]), out var low, out var high);
                var into = MemoryMarshal.Cast<double, Vector<double>>(sums.Slice(x, Vector<float>.Count));
                (into[0], into[1]) = (into[0] + low, into[1] + high);
            }
        }

        for (; x < row.Length; x++)
            sums[x] += row[x];
    }

    // row[x] = (float)(sums[x] / size), as Box writes a value.
    private static void WriteRow(ReadOnlySpan<double> sums, int size, Span<float> row)
    {
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var by = new Vector<double>(size);
            for (; x <= row.Length - Vector<float>.Count; x += Vector<float>.Count)
            {
                var from = MemoryMarshal.Cast<double, Vector<double>>(sums.Slice(x, Vector<float>.Count));
                Vector.Narrow(from[0] / by, from[1] / by).CopyTo(row[x..]);
            }
        }

        for (; x < row.Length; x++)
            row[x] = (float)(sums[x] / size);
    }

    // sums[x] += add[x] - take[x], difference in float then widened, as Box advances its sum.
    private static void MoveRow(Span<double> sums, ReadOnlySpan<float> add, ReadOnlySpan<float> take)
    {
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x <= add.Length - Vector<float>.Count; x += Vector<float>.Count)
            {
                Vector.Widen(new Vector<float>(add[x..]) - new Vector<float>(take[x..]), out var low, out var high);
                var into = MemoryMarshal.Cast<double, Vector<double>>(sums.Slice(x, Vector<float>.Count));
                (into[0], into[1]) = (into[0] + low, into[1] + high);
            }
        }

        for (; x < add.Length; x++)
            sums[x] += add[x] - take[x];
    }

    private static void Box(float[] line, int count, int radius, float[] target, int to, int step)
    {
        float At(int i) => line[Math.Clamp(i, 0, count - 1)];
        var sum = 0.0;
        for (var k = -radius; k <= radius; k++)
            sum += At(k);
        var size = 2 * radius + 1;
        for (var i = 0; i < count; i++)
        {
            target[to + i * step] = (float)(sum / size);
            sum += At(i + radius + 1) - At(i - radius);
        }
    }
}
