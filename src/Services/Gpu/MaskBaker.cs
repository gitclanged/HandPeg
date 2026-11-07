using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// What a layer's mask is baked from, as the export's graph made it before 5.2-Audit (BuildMaskSource), step
/// for step: the picture read by its brightness or its transparency, the part of it under the layer cut out, scaled to the
/// layer's size, turned inside out, grown or shrunk by so many passes, nudged, and its outline softened. All in the
/// layer's own pixels, as the export builds it.
/// </summary>
/// <param name="Cut">The part of the mask picture that lies under the layer, as fractions of it: left, top, width, height.</param>
/// <param name="Grow">Passes of a 3 x 3 dilation (above 0) or erosion (below 0).</param>
/// <param name="OffsetX">How far the mask is moved inside the layer, in its pixels; what it uncovers is hidden.</param>
/// <param name="BlurRadius">The radius of the box blur run twice over its outline (Soft Edges); 0 for none.</param>
public readonly record struct MaskRecipe(
    string Path, bool FromAlpha, int Width, int Height, (double X, double Y, double Width, double Height) Cut,
    bool Invert, int Grow, int OffsetX, int OffsetY, int BlurRadius)
{
    /// <summary>The recipe's content hash, with the file's size and last write (a mask drawn again is a new mask).</summary>
    public long ContentHash()
    {
        // A drawn mask's key is its outlines' hash already: there is no file to ask about.
        var file = MaskStore.IsDrawn(Path) ? default : FileState.Get(Path);
        return default(GpuHash).Add("mask").Add(Path).Add(file.Length).Add(file.WrittenUtc.Ticks).Add(FromAlpha).Add(Width).Add(Height)
            .Add(Cut.X).Add(Cut.Y).Add(Cut.Width).Add(Cut.Height).Add(Invert).Add(Grow).Add(OffsetX).Add(OffsetY).Add(BlurRadius).Value;
    }

    /// <summary>What the mask is apart from its size and the steps measured in the layer's pixels: an older bake of the same family stands in while a new one is made.</summary>
    public long FamilyHash() =>
        default(GpuHash).Add("mask-family").Add(Path).Add(FromAlpha).Add(Cut.X).Add(Cut.Y).Add(Cut.Width).Add(Cut.Height).Add(Invert).Value;
}

/// <summary>
/// Bakes masks and decodes stills for the GPU engine, on the processor: everything a mask needs that the compositor's one
/// pass (PSLayerMask) cannot do as the export does, above all the soft edge (a box blur run twice), is done here once and
/// handed over as a <see cref="GpuResourceKind.Mask2D"/> (then sent with its grow, invert and offset at nothing). Grow and
/// blur are separable and cost the same whatever their radius (van Herk / Gil-Werman for the square's maximum, running sums
/// for the box). Thread-safe: every call works on its own arrays (called on a background lane).
/// </summary>
public static class MaskBaker
{
    /// <summary>The longest side a still read from a file is kept at (the worker scales it to its box).</summary>
    public const int LargestStillSide = 4096;

    /// <summary>The mask as BGRA8 (its value in every colour, solid), its recipe's size; null when the picture cannot be read.</summary>
    public static byte[]? Bake(in MaskRecipe recipe)
    {
        var (width, height) = (Math.Clamp(recipe.Width, 1, 16384), Math.Clamp(recipe.Height, 1, 16384));
        float[] mask;
        if (MaskStore.IsDrawn(recipe.Path))
        {
            // A drawn mask (5.1-Release): its outlines filled in memory, the part under the layer at exactly the layer's size.
            if (MaskStore.Rasterize(recipe.Path, recipe.Cut, width, height) is not { } drawn)
                return null;
            mask = drawn;
        }
        else
        {
            if (ReadGrey(recipe.Path, recipe.FromAlpha) is not { } picture)
                return null;

            var (grey, sourceWidth, sourceHeight) = picture;

            // The part under the layer (crop: x and y truncated, each side at least a pixel).
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
    /// The mask as BGRA8: each value times 255, rounded (to even, as MathF.Round), held to 0..255 and written in every colour,
    /// solid. A whole vector of values at a time (the same multiply and rounding in each lane), then one at a time for the rest.
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

    /// <summary>A picture file as BGRA8, straight alpha, at its own size (at most <see cref="LargestStillSide"/> on its longer side); null when it cannot be read.</summary>
    public static (byte[] Pixels, int Width, int Height)? ReadStill(string path)
    {
        if (Decode(path, LargestStillSide) is not { } bitmap)
            return null;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return (pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    // ----- Reading -----

    /// <summary>A picture as grey values 0..1: its transparency, or its brightness (FFmpeg's format=gray: BT.601 weights).</summary>
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

    /// <summary>A picture file decoded from a stream (never a URI, never cached), as straight BGRA, frozen; null when it cannot be.</summary>
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
            AppLog.Write($"A picture for the GPU engine could not be read ({Path.GetFileName(path)}): {ex.GetType().Name}");
            return null;
        }
    }

    // ----- The steps -----

    /// <summary>
    /// A part of a grey picture scaled to a new size: each output pixel the mean of the source it covers where it shrinks
    /// (a box of the footprint), linear between source pixels where it grows; across, then down.
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

    // Resample's pass down, every column at once, a row at a time: an output row takes the same source rows with the same
    // weights in every column, so the columns go side by side, in rows as the picture is laid out (one walked alone reads a
    // cache line for every pixel), each through the very operations Line gives a column alone.
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

    // One line of Resample: count samples from `from` (every `step`) into `size` samples at `to` (every `toStep`).
    private static void Line(float[] source, int from, int step, int count, float[] target, int to, int toStep, int size)
    {
        var scale = (double)count / size;
        for (var i = 0; i < size; i++)
        {
            double value;
            if (scale > 1)
            {
                // The footprint [i, i + 1) * scale, each source pixel weighed by how much of it lies inside.
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
    /// Passes of a 3 x 3 dilation (or erosion): the most (least) within a square of radius <paramref name="passes"/>, edges
    /// clamped, as many passes of FFmpeg's dilation and erosion give. Separable, by van Herk / Gil-Werman: three
    /// comparisons a pixel whatever the radius. The columns go a vector's width at a time, side by side (each lane a column,
    /// through the same comparisons as one alone), so the picture is read a run of pixels a row, not a cache line a pixel.
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

    // The most (or least) of each window of 2r + 1 along a line, edges clamped. (Its runs are the caller's, used again line after line.)
    private static void Extreme(float[] line, float[] output, int count, int radius, bool most, float[] forward, float[] backward)
    {
        var size = 2 * radius + 1;
        var padded = count + 2 * radius;
        var block = ((padded + size - 1) / size) * size;
        float At(int i) => line[Math.Clamp(i - radius, 0, count - 1)];
        float Pick(float a, float b) => most ? MathF.Max(a, b) : MathF.Min(a, b);

        // Window by window (the block is a whole number of them): a run forward from each one's start, and back from its end,
        // each carried along in a local rather than read back from the run it is written to.
        for (var start = 0; start < block; start += size)
        {
            var run = forward[start] = At(start);
            for (var i = start + 1; i < start + size; i++)
                run = forward[i] = Pick(run, At(i));
            run = backward[start + size - 1] = At(start + size - 1);
            for (var i = start + size - 2; i >= start; i--)
                run = backward[i] = Pick(run, At(i));
        }

        // The window of padded positions [i, i + size - 1] is the backward run from i and the forward run to its end.
        for (var i = 0; i < count; i++)
            output[i] = Pick(backward[i], forward[Math.Min(i + size - 1, block - 1)]);
    }

    // The same along lines side by side, each of a vector's lanes a line of its own (Vector.Max and Min are MathF.Max and Min, lane by lane).
    private static void Extreme(Vector<float>[] line, Vector<float>[] output, int count, int radius, bool most, Vector<float>[] forward, Vector<float>[] backward)
    {
        var size = 2 * radius + 1;
        var padded = count + 2 * radius;
        var block = ((padded + size - 1) / size) * size;
        Vector<float> At(int i) => line[Math.Clamp(i - radius, 0, count - 1)];
        Vector<float> Pick(Vector<float> a, Vector<float> b) => most ? Vector.Max(a, b) : Vector.Min(a, b);

        // Window by window (the block is a whole number of them): a run forward from each one's start, and back from its end,
        // each carried along in a local rather than read back from the run it is written to.
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

    /// <summary>The mask moved by (dx, dy) inside its own size: what it uncovers is hidden (FFmpeg's geq with its between()s).</summary>
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

    // A box of radius r across each row (running sums, edges clamped), as boxblur's one pass across.
    // Four rows at a time, each with its own sum: a sum waits on its last step, so four of them side by side keep the
    // processor busy. Each row is copied with its first and last values repeated r + 1 times beyond its ends (no position to
    // clamp), and takes the same values in the same order as Box, so the result is the same to the bit.
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

    // The same down each column, as boxblur's pass down: every column's running sum kept side by side and moved a row at a
    // time, so the picture is read in rows (a column walked alone reads a cache line for every pixel). Each column takes the
    // same values in the same order as Box along one line (the difference made in float, then added), so the result is the
    // same to the bit. The rows a sum has still to take away are kept as they were before being written over: r + 1, in turn.
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

    // sums[x] += row[x], each value widened to double first (as Box's first sum).
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

    // sums[x] += add[x] - take[x], the difference made in float and then widened, as Box moves its sum along.
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
