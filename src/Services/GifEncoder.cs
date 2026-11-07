using System.IO;
using System.Numerics;

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
        var (indices, previous, box) = (new byte[_width * _height], new byte[_width * _height], new byte[_width * _height]);
        var (error, below) = (new int[(_width + 2) * 3], new int[(_width + 2) * 3]);
        var codes = new LzwTable();
        var shown = 0.0;
        _frames.Position = 0;
        for (var n = 0; n < _count; n++)
        {
            token.ThrowIfCancellationRequested();
            _frames.ReadExactly(frame);
            Dither(frame, nearest, indices, error, below);

            // The delay that keeps the clip's clock: hundredths of a second since the start, less what was shown.
            var until = Math.Round((n + 1) * 100 / Math.Max(framesPerSecond, 0.1));
            var delay = (int)Math.Clamp(until - shown, 2, 65535);
            shown += delay;

            var (left, top, right, bottom) = n == 0 ? (0, 0, _width, _height) : Changed(indices, previous);
            if (right <= left)
                (left, top, right, bottom) = (0, 0, 1, 1);
            WriteFrame(output, indices, box, codes, left, top, right - left, bottom - top, delay);
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

        // Each box's score (its widest side times the root of its weight) is worked out once, as it is made: only the two
        // halves of the box just cut are new, and every other box scores as it did.
        var boxes = new List<List<int>> { bins };
        var scores = new List<double> { Score(bins) };
        double Score(List<int> box)
        {
            if (box.Count < 2)
                return 0;
            var (_, range) = WidestAxis(box);
            var weight = box.Sum(i => histogram[i]);
            return range * Math.Sqrt(weight);
        }

        while (boxes.Count < colours)
        {
            var (best, score) = (-1, 0.0);
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count >= 2 && scores[b] > score)
                    (best, score) = (b, scores[b]);
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
            scores[best] = Score(boxes[best]);
            scores.Add(Score(boxes[^1]));
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

    // The palette entry nearest each bin (by squared distance, green weighing most), worked out once, with its colour beside
    // it (entry | red << 8 | green << 16 | blue << 24), so that dithering finds both at once.
    private static uint[] NearestTable(byte[] palette)
    {
        // The palette by channel, as whole numbers, so that a vector's width of entries is measured at once.
        var (reds, greens, blues) = (new int[256], new int[256], new int[256]);
        for (var i = 0; i < 256; i++)
            (reds[i], greens[i], blues[i]) = (palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]);

        var table = new uint[Levels * Levels * Levels];
        Parallel.For(0, Levels, r =>
        {
            for (var g = 0; g < Levels; g++)
            {
                for (var b = 0; b < Levels; b++)
                {
                    var (cr, cg, cb) = (r * 255 / (Levels - 1), g * 255 / (Levels - 1), b * 255 / (Levels - 1));
                    var entry = Nearest(reds, greens, blues, cr, cg, cb);
                    table[r * Levels * Levels + g * Levels + b] = entry | (uint)palette[entry * 3] << 8 | (uint)palette[entry * 3 + 1] << 16 | (uint)palette[entry * 3 + 2] << 24;
                }
            }
        });
        return table;
    }

    // The first of the nearest entries. Each lane keeps the first nearest of its own entries (a strictly nearer one replaces
    // it), and of the lanes the nearest wins, the lowest entry among equals: the first nearest of all, as one at a time finds it.
    private static byte Nearest(int[] reds, int[] greens, int[] blues, int r, int g, int b)
    {
        var (best, distance, i) = (0, int.MaxValue, 0);
        if (Vector.IsHardwareAccelerated)
        {
            var (red, green, blue) = (new Vector<int>(r), new Vector<int>(g), new Vector<int>(b));
            var (least, at, index, step) = (new Vector<int>(int.MaxValue), Vector<int>.Zero, Vector<int>.Indices, new Vector<int>(Vector<int>.Count));
            for (; i <= 256 - Vector<int>.Count; i += Vector<int>.Count)
            {
                var (dr, dg, db) = (new Vector<int>(reds, i) - red, new Vector<int>(greens, i) - green, new Vector<int>(blues, i) - blue);
                var d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
                var nearer = Vector.LessThan(d, least);
                (least, at) = (Vector.ConditionalSelect(nearer, d, least), Vector.ConditionalSelect(nearer, index, at));
                index += step;
            }

            for (var lane = 0; lane < Vector<int>.Count; lane++)
            {
                if (least[lane] < distance || least[lane] == distance && at[lane] < best)
                    (best, distance) = (at[lane], least[lane]);
            }
        }

        for (; i < 256; i++)
        {
            var (dr, dg, db) = (reds[i] - r, greens[i] - g, blues[i] - b);
            var d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (d < distance)
                (best, distance) = (i, d);
        }

        return (byte)best;
    }

    // ----- The frames -----

    // Floyd-Steinberg onto the palette: each pixel's error spread right and to the row below (7, 3, 5, 1 sixteenths). The
    // errors the row above left (this row's) and those for the row below are two arrays, a pixel's three side by side,
    // swapped as a row is done. The errors of the last two pixels are kept in hand: the one on the left gives this pixel
    // seven sixteenths of its own, and a pixel of the row below takes one, five and three of the three above it, so each of
    // its sums is written once, when the third is known (the same whole numbers, added in another order).
    private void Dither(byte[] bgra, uint[] nearest, byte[] indices, int[] current, int[] next)
    {
        Array.Clear(current);
        for (var y = 0; y < _height; y++)
        {
            var (r1, g1, b1, r2, g2, b2) = (0, 0, 0, 0, 0, 0);
            for (var x = 0; x < _width; x++)
            {
                var at = (y * _width + x) * 4;
                var e = (x + 1) * 3;
                var r = Math.Clamp(bgra[at + 2] + (current[e] + r1 * 7) / 16, 0, 255);
                var g = Math.Clamp(bgra[at + 1] + (current[e + 1] + g1 * 7) / 16, 0, 255);
                var b = Math.Clamp(bgra[at] + (current[e + 2] + b1 * 7) / 16, 0, 255);
                var found = nearest[Bin((byte)r, (byte)g, (byte)b)];
                indices[y * _width + x] = (byte)found;
                var (er, eg, eb) = (r - (int)(found >> 8 & 0xFF), g - (int)(found >> 16 & 0xFF), b - (int)(found >> 24));
                if (x > 0)
                    (next[e - 3], next[e - 2], next[e - 1]) = (r2 + r1 * 5 + er * 3, g2 + g1 * 5 + eg * 3, b2 + b1 * 5 + eb * 3);
                (r2, g2, b2, r1, g1, b1) = (r1, g1, b1, er, eg, eb);
            }

            var last = _width * 3;
            (next[last], next[last + 1], next[last + 2]) = (r2 + r1 * 5, g2 + g1 * 5, b2 + b1 * 5);
            (current, next) = (next, current);
        }
    }

    // The bounding box of the pixels that differ from the frame before: left, top, right, bottom (exclusive). A row is
    // compared whole (a vector at a time); in one that changed, its first difference is its run of equal pixels from the
    // left, and its last is found from the right, a vector at a time and then pixel by pixel.
    private (int Left, int Top, int Right, int Bottom) Changed(byte[] now, byte[] before)
    {
        var (left, top, right, bottom) = (_width, _height, 0, 0);
        for (var y = 0; y < _height; y++)
        {
            var row = now.AsSpan(y * _width, _width);
            var was = before.AsSpan(y * _width, _width);
            var first = row.CommonPrefixLength(was);
            if (first == _width)
                continue;

            var last = _width;
            while (last - Vector<byte>.Count > first && new Vector<byte>(row[(last - Vector<byte>.Count)..]) == new Vector<byte>(was[(last - Vector<byte>.Count)..]))
                last -= Vector<byte>.Count;
            do
                last--;
            while (row[last] == was[last]);

            (left, right) = (Math.Min(left, first), Math.Max(right, last + 1));
            (top, bottom) = (Math.Min(top, y), Math.Max(bottom, y + 1));
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

    private void WriteFrame(Stream output, byte[] indices, byte[] box, LzwTable codes, int left, int top, int width, int height, int delay)
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

        // The box's rows, one after another: in place when it is as wide as the frame, else copied together.
        if (width == _width)
        {
            Lzw(output, indices.AsSpan(top * _width, width * height), codes);
            return;
        }

        for (var y = 0; y < height; y++)
            Array.Copy(indices, (top + y) * _width + left, box, y * width, width);
        Lzw(output, box.AsSpan(0, width * height), codes);
    }

    private static void WriteShort(Stream output, int value)
    {
        output.WriteByte((byte)value);
        output.WriteByte((byte)(value >> 8));
    }

    // GIF's LZW of 8-bit indices: codes from 9 bits up to 12, a clear code when the table is full, in sub-blocks of 255 bytes.
    private static void Lzw(Stream output, ReadOnlySpan<byte> pixels, LzwTable table)
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

        table.Clear();
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
                    table.Add(key, next++);
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

    /// <summary>
    /// LZW's table of strings, by the code of a string less its last index and that index (code &lt;&lt; 8 | index, under
    /// 2^20): one slot each, the code of the string or 0 for none (no string's code is 0), and the slots in use, so that
    /// clearing the table clears only those. One for a whole file, made once.
    /// </summary>
    private sealed class LzwTable
    {
        private readonly short[] _codes = new short[1 << 20];
        private readonly int[] _used = new int[4096];
        private int _count;

        public bool TryGetValue(int key, out int code) => (code = _codes[key]) != 0;

        public void Add(int key, int code)
        {
            _codes[key] = (short)code;
            _used[_count++] = key;
        }

        public void Clear()
        {
            for (var i = 0; i < _count; i++)
                _codes[_used[i]] = 0;
            _count = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _frames.Dispose();
    }
}
