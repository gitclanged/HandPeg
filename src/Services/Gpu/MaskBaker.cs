using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// What a layer's mask is baked from, as the graph builder makes it (FilterGraphBuilder.Video.cs BuildMaskSource), step
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
        var file = FileState.Get(Path);
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
        if (ReadGrey(recipe.Path, recipe.FromAlpha) is not { } picture)
            return null;

        var (grey, sourceWidth, sourceHeight) = picture;

        // The part under the layer (crop: x and y truncated, each side at least a pixel).
        var cutX = Math.Clamp((int)(sourceWidth * Math.Clamp(recipe.Cut.X, 0, 1)), 0, sourceWidth - 1);
        var cutY = Math.Clamp((int)(sourceHeight * Math.Clamp(recipe.Cut.Y, 0, 1)), 0, sourceHeight - 1);
        var cutWidth = Math.Clamp((int)(sourceWidth * Math.Clamp(recipe.Cut.Width, 0, 1)), 1, sourceWidth - cutX);
        var cutHeight = Math.Clamp((int)(sourceHeight * Math.Clamp(recipe.Cut.Height, 0, 1)), 1, sourceHeight - cutY);

        var mask = Resample(grey, sourceWidth, cutX, cutY, cutWidth, cutHeight, width, height);
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

        var pixels = new byte[width * height * 4];
        for (var i = 0; i < mask.Length; i++)
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
        for (var x = 0; x < toWidth; x++)
            Line(rows, x, toWidth, height, result, x, toWidth, toHeight);
        return result;
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
    /// comparisons a pixel whatever the radius.
    /// </summary>
    private static void Spread(float[] mask, int width, int height, int passes, bool grow)
    {
        var line = new float[Math.Max(width, height)];
        var output = new float[line.Length];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(mask, y * width, line, 0, width);
            Extreme(line, output, width, passes, grow);
            Array.Copy(output, 0, mask, y * width, width);
        }

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
                line[y] = mask[y * width + x];
            Extreme(line, output, height, passes, grow);
            for (var y = 0; y < height; y++)
                mask[y * width + x] = output[y];
        }
    }

    // The most (or least) of each window of 2r + 1 along a line, edges clamped.
    private static void Extreme(float[] line, float[] output, int count, int radius, bool most)
    {
        var size = 2 * radius + 1;
        var padded = count + 2 * radius;
        var block = ((padded + size - 1) / size) * size;
        var forward = new float[block];
        var backward = new float[block];
        float At(int i) => line[Math.Clamp(i - radius, 0, count - 1)];
        float Pick(float a, float b) => most ? MathF.Max(a, b) : MathF.Min(a, b);

        for (var i = 0; i < block; i++)
            forward[i] = i % size == 0 ? At(i) : Pick(forward[i - 1], At(i));
        for (var i = block - 1; i >= 0; i--)
            backward[i] = i % size == size - 1 || i == block - 1 ? At(i) : Pick(backward[i + 1], At(i));

        // The window of padded positions [i, i + size - 1] is the backward run from i and the forward run to its end.
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
    private static void BoxRows(float[] mask, int width, int height, int radius)
    {
        var line = new float[width];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(mask, y * width, line, 0, width);
            Box(line, width, radius, mask, y * width, 1);
        }
    }

    private static void BoxColumns(float[] mask, int width, int height, int radius)
    {
        var line = new float[height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
                line[y] = mask[y * width + x];
            Box(line, height, radius, mask, x, width);
        }
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
