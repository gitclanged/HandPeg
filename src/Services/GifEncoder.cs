using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// An animated GIF made in HandPeg (Pass 5.2-Audit: FFmpeg's palettegen and paletteuse graph is gone; the GPU export pipes its
/// frames here instead of to FFmpeg). As those two steps made it: one palette of 256 colours for the whole clip, from every
/// frame's colours (a histogram of 5 bits a channel, cut by median cut, the most-used colours weighing most), each frame
/// dithered onto it (Floyd-Steinberg), and only what changed from the frame before written (its bounding box), LZW-packed, the
/// clip looping for ever. The frames wait in a file until the palette is known: a GIF is short, and its frames are small.
/// </summary>
public sealed class GifEncoder : IDisposable
{
    private const int Levels = 32;

    private readonly int _width, _height;
    private readonly long[] _histogram = new long[Levels * Levels * Levels];
    private readonly string _framesPath;
    private readonly FileStream _frames;
    private int _count;
    private bool _disposed;

    /// <param name="width">The frames' width; <paramref name="height"/> their height (BGRA, rows packed).</param>
    public GifEncoder(int width, int height, string workFolder)
    {
        (_width, _height) = (Math.Clamp(width, 1, 65535), Math.Clamp(height, 1, 65535));
        Directory.CreateDirectory(workFolder);
        _framesPath = Path.Combine(workFolder, $"gif_frames_{Guid.NewGuid():N}.bgra");
        _frames = new FileStream(_framesPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose);
    }

    public long FrameBytes => (long)_width * _height * 4;

    public int FrameCount => _count;

    /// <summary>One frame, BGRA (alpha not read): counted into the palette and kept for <see cref="Write"/>.</summary>
    public void Add(ReadOnlySpan<byte> bgra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pixels = bgra[..(int)FrameBytes];
        for (var i = 0; i < pixels.Length; i += 4)
            _histogram[Bin(pixels[i + 2], pixels[i + 1], pixels[i])]++;
        _frames.Write(pixels);
        _count++;
    }

    /// <summary>
    /// The GIF of every frame added, each shown for its share of <paramref name="framesPerSecond"/> (GIF counts in hundredths of a
    /// second: the delays are rounded so they add up), looping for ever.
    /// </summary>
    public void Write(string path, double framesPerSecond, IProgress<double>? progress = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var palette = MedianCut(_histogram, 256);
        var nearest = NearestTable(palette);

        using var output = new BufferedStream(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), 1 << 20);
        WriteHeader(output, palette);

        var frame = new byte[FrameBytes];
        var (indices, previous) = (new byte[_width * _height], new byte[_width * _height]);
        var error = new int[2, (_width + 2) * 3];
        var shown = 0.0;
        _frames.Position = 0;
        for (var n = 0; n < _count; n++)
        {
            token.ThrowIfCancellationRequested();
            _frames.ReadExactly(frame);
            Dither(frame, palette, nearest, indices, error);

            // The delay that keeps the clip's clock: hundredths of a second since the start, less what was shown.
            var until = Math.Round((n + 1) * 100 / Math.Max(framesPerSecond, 0.1));
            var delay = (int)Math.Clamp(until - shown, 2, 65535);
            shown += delay;

            var (left, top, right, bottom) = n == 0 ? (0, 0, _width, _height) : Changed(indices, previous);
            if (right <= left)
                (left, top, right, bottom) = (0, 0, 1, 1);
            WriteFrame(output, indices, left, top, right - left, bottom - top, delay);
            (indices, previous) = (previous, indices);
            progress?.Report((n + 1) / (double)_count);
        }

        output.WriteByte(0x3B);
    }

    private static int Bin(byte r, byte g, byte b) => (r >> 3) * Levels * Levels + (g >> 3) * Levels + (b >> 3);

    // ----- The palette -----

    // Median cut over the histogram's bins: the box with the most pixels times its widest side split at its pixels' median.
    private static byte[] MedianCut(long[] histogram, int colours)
    {
        var bins = new List<int>();
        for (var i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] > 0)
                bins.Add(i);
        }

        var palette = new byte[256 * 3];
        if (bins.Count == 0)
            return palette;

        var boxes = new List<List<int>> { bins };
        while (boxes.Count < colours)
        {
            var (best, score) = (-1, 0.0);
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2)
                    continue;
                var (_, range) = WidestAxis(boxes[b]);
                var weight = boxes[b].Sum(i => histogram[i]);
                var s = range * Math.Sqrt(weight);
                if (s > score)
                    (best, score) = (b, s);
            }

            if (best < 0)
                break;

            var box = boxes[best];
            var (axis, _) = WidestAxis(box);
            box.Sort((a, c) => Channel(a, axis).CompareTo(Channel(c, axis)));
            var half = box.Sum(i => histogram[i]) / 2;
            var (running, cut) = (0L, 1);
            for (var k = 0; k < box.Count - 1; k++)
            {
                running += histogram[box[k]];
                if (running >= half)
                {
                    cut = k + 1;
                    break;
                }

                cut = k + 1;
            }

            boxes[best] = box[..cut];
            boxes.Add(box[cut..]);
        }

        for (var b = 0; b < boxes.Count; b++)
        {
            var (r, g, bl, total) = (0.0, 0.0, 0.0, 0.0);
            foreach (var i in boxes[b])
            {
                var w = histogram[i];
                (r, g, bl, total) = (r + Channel(i, 0) * w, g + Channel(i, 1) * w, bl + Channel(i, 2) * w, total + w);
            }

            palette[b * 3] = (byte)Math.Clamp(Math.Round(r / total * 255 / (Levels - 1)), 0, 255);
            palette[b * 3 + 1] = (byte)Math.Clamp(Math.Round(g / total * 255 / (Levels - 1)), 0, 255);
            palette[b * 3 + 2] = (byte)Math.Clamp(Math.Round(bl / total * 255 / (Levels - 1)), 0, 255);
        }

        return palette;
    }

    private static int Channel(int bin, int axis) => axis switch
    {
        0 => bin / (Levels * Levels),
        1 => bin / Levels % Levels,
        _ => bin % Levels,
    };

    private static (int Axis, int Range) WidestAxis(List<int> box)
    {
        var (axis, range) = (0, -1);
        for (var a = 0; a < 3; a++)
        {
            var (low, high) = (int.MaxValue, int.MinValue);
            foreach (var i in box)
            {
                var v = Channel(i, a);
                (low, high) = (Math.Min(low, v), Math.Max(high, v));
            }

            if (high - low > range)
                (axis, range) = (a, high - low);
        }

        return (axis, range);
    }

    // The palette entry nearest each bin (by squared distance, green weighing most), worked out once.
    private static byte[] NearestTable(byte[] palette)
    {
        var table = new byte[Levels * Levels * Levels];
        Parallel.For(0, Levels, r =>
        {
            for (var g = 0; g < Levels; g++)
            {
                for (var b = 0; b < Levels; b++)
                {
                    var (cr, cg, cb) = (r * 255 / (Levels - 1), g * 255 / (Levels - 1), b * 255 / (Levels - 1));
                    table[r * Levels * Levels + g * Levels + b] = Nearest(palette, cr, cg, cb);
                }
            }
        });
        return table;
    }

    private static byte Nearest(byte[] palette, int r, int g, int b)
    {
        var (best, distance) = (0, int.MaxValue);
        for (var i = 0; i < 256; i++)
        {
            var (dr, dg, db) = (palette[i * 3] - r, palette[i * 3 + 1] - g, palette[i * 3 + 2] - b);
            var d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (d < distance)
                (best, distance) = (i, d);
        }

        return (byte)best;
    }

    // ----- The frames -----

    // Floyd-Steinberg onto the palette: each pixel's error spread right and to the row below (7, 3, 5, 1 sixteenths).
    private void Dither(byte[] bgra, byte[] palette, byte[] nearest, byte[] indices, int[,] error)
    {
        Array.Clear(error);
        var (current, next) = (0, 1);
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var at = (y * _width + x) * 4;
                var e = (x + 1) * 3;
                var r = Math.Clamp(bgra[at + 2] + error[current, e] / 16, 0, 255);
                var g = Math.Clamp(bgra[at + 1] + error[current, e + 1] / 16, 0, 255);
                var b = Math.Clamp(bgra[at] + error[current, e + 2] / 16, 0, 255);
                var index = nearest[Bin((byte)r, (byte)g, (byte)b)];
                indices[y * _width + x] = index;
                Span<int> rest = [r - palette[index * 3], g - palette[index * 3 + 1], b - palette[index * 3 + 2]];
                for (var c = 0; c < 3; c++)
                {
                    error[current, e + 3 + c] += rest[c] * 7;
                    error[next, e - 3 + c] += rest[c] * 3;
                    error[next, e + c] += rest[c] * 5;
                    error[next, e + 3 + c] += rest[c];
                }
            }

            for (var i = 0; i < error.GetLength(1); i++)
                error[current, i] = 0;
            (current, next) = (next, current);
        }
    }

    // The bounding box of the pixels that differ from the frame before: left, top, right, bottom (exclusive).
    private (int Left, int Top, int Right, int Bottom) Changed(byte[] now, byte[] before)
    {
        var (left, top, right, bottom) = (_width, _height, 0, 0);
        for (var y = 0; y < _height; y++)
        {
            var row = y * _width;
            for (var x = 0; x < _width; x++)
            {
                if (now[row + x] == before[row + x])
                    continue;
                (left, right) = (Math.Min(left, x), Math.Max(right, x + 1));
                (top, bottom) = (Math.Min(top, y), Math.Max(bottom, y + 1));
            }
        }

        return (left, top, right, bottom);
    }

    // ----- The file -----

    private void WriteHeader(Stream output, byte[] palette)
    {
        output.Write("GIF89a"u8);
        WriteShort(output, _width);
        WriteShort(output, _height);
        output.WriteByte(0xF7); // a global colour table of 256 entries, 8 bits a colour
        output.WriteByte(0);
        output.WriteByte(0);
        output.Write(palette);

        // NETSCAPE2.0: loop for ever.
        output.Write([0x21, 0xFF, 0x0B]);
        output.Write("NETSCAPE2.0"u8);
        output.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    private void WriteFrame(Stream output, byte[] indices, int left, int top, int width, int height, int delay)
    {
        // The graphic control extension: its delay; each frame drawn over the last (the parts it leaves out stay).
        output.Write([0x21, 0xF9, 0x04, 0x04]);
        WriteShort(output, delay);
        output.Write([0x00, 0x00]);

        output.WriteByte(0x2C);
        WriteShort(output, left);
        WriteShort(output, top);
        WriteShort(output, width);
        WriteShort(output, height);
        output.WriteByte(0);

        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
            Array.Copy(indices, (top + y) * _width + left, pixels, y * width, width);
        Lzw(output, pixels);
    }

    private static void WriteShort(Stream output, int value)
    {
        output.WriteByte((byte)value);
        output.WriteByte((byte)(value >> 8));
    }

    // GIF's LZW of 8-bit indices: codes from 9 bits up to 12, a clear code when the table is full, in sub-blocks of 255 bytes.
    private static void Lzw(Stream output, byte[] pixels)
    {
        const int minimum = 8, clear = 1 << minimum, end = clear + 1, largest = 4096;
        output.WriteByte(minimum);
        var block = new byte[256];
        var (filled, bits, bitCount) = (0, 0L, 0);
        void Emit(int code, int size)
        {
            bits |= (long)code << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                block[++filled] = (byte)bits;
                bits >>= 8;
                bitCount -= 8;
                if (filled == 255)
                {
                    block[0] = 255;
                    output.Write(block, 0, 256);
                    filled = 0;
                }
            }
        }

        var table = new Dictionary<int, int>(largest);
        var (next, size) = (end + 1, minimum + 1);
        Emit(clear, size);
        if (pixels.Length > 0)
        {
            var prefix = (int)pixels[0];
            for (var i = 1; i < pixels.Length; i++)
            {
                var key = (prefix << 8) | pixels[i];
                if (table.TryGetValue(key, out var code))
                {
                    prefix = code;
                    continue;
                }

                Emit(prefix, size);
                if (next < largest)
                {
                    table[key] = next++;
                    if (next > 1 << size && size < 12)
                        size++;
                }
                else
                {
                    Emit(clear, size);
                    table.Clear();
                    (next, size) = (end + 1, minimum + 1);
                }

                prefix = pixels[i];
            }

            Emit(prefix, size);

            // The decoder takes its last entry from that code: when it fills this size, the end code is read a bit wider.
            if (next >= 1 << size && size < 12)
                size++;
        }

        Emit(end, size);
        if (bitCount > 0)
            Emit(0, 8 - bitCount);
        if (filled > 0)
        {
            block[0] = (byte)filled;
            output.Write(block, 0, filled + 1);
        }

        output.WriteByte(0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _frames.Dispose();
    }
}
