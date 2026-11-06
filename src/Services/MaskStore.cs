using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Mask pictures: carried inside the files that have to stand on their own (a style preset, a project), and
/// drawn from shapes. A carried mask is kept under a name made from its content, so two different masks that
/// share a file name never take each other's place, and one mask used by several layers is carried once.
///
/// A drawn mask is a set of closed outlines, each a list of points given as fractions of the source frame,
/// written as text: "x,y x,y x,y" for an outline, outlines separated by ";". The picture is drawn from that
/// text, white inside the outlines and black outside, and the layer keeps the text, so the outlines can be
/// opened and changed again, and the picture drawn again when it is missing.
/// </summary>
public static class MaskStore
{
    // ----- Carried inside a file -----

    /// <summary>
    /// Adds the mask a layer uses to the set being written, and has the layer name it by its key. A layer
    /// without a mask file is left as it is.
    /// </summary>
    public static void Pack(LayerState layer, Dictionary<string, string> masks)
    {
        // A drawn mask is its outlines alone: nothing is carried but them (5.1-Release).
        if (IsDrawn(SourceOf(layer.MaskPath, layer.MaskShapes)))
        {
            layer.MaskPath = "";
            return;
        }

        var path = (layer.MaskPath ?? "").Trim().Trim('"');
        if (!layer.CustomMask || path.Length == 0 || !File.Exists(path))
            return;

        try
        {
            var bytes = File.ReadAllBytes(path);
            var key = KeyFor(path, bytes);
            masks.TryAdd(key, Convert.ToBase64String(bytes));
            layer.MaskPath = key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as a path: the file still works on this machine.
        }
    }

    /// <summary>
    /// Writes a carried mask into the masks folder and points the layer at it there. A file of that name that
    /// already holds the same picture is used as it is; one that holds another picture is left alone, and this
    /// one is written beside it under its content's name. A name the set does not carry is left as it is.
    /// </summary>
    /// <returns>Null, or why the mask could not be written.</returns>
    public static string? Unpack(LayerState layer, IReadOnlyDictionary<string, string> masks)
    {
        // A picture HandPeg once drew from the layer's own outlines (an older file's shape_*.png): the outlines stand in for it.
        if (IsDrawn(SourceOf(layer.MaskPath, layer.MaskShapes)))
        {
            layer.MaskPath = "";
            return null;
        }

        if (string.IsNullOrEmpty(layer.MaskPath) || !masks.TryGetValue(layer.MaskPath, out var encoded))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            Directory.CreateDirectory(AppPaths.Masks);
            var path = Path.Combine(AppPaths.Masks, Path.GetFileName(layer.MaskPath));
            if (File.Exists(path) && !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                path = Path.Combine(AppPaths.Masks, KeyFor(path, bytes));
            if (!File.Exists(path))
                File.WriteAllBytes(path, bytes);
            layer.MaskPath = path;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return $"The mask {layer.MaskPath} could not be unpacked: {ex.Message}";
        }
    }

    /// <summary>The name a mask is carried under: its file's name, with the first bytes of its content's hash.</summary>
    private static string KeyFor(string path, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 4).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.EndsWith("_" + hash, StringComparison.Ordinal))
            stem += "_" + hash;
        return stem + Path.GetExtension(path);
    }

    // ----- Read as a mask -----

    // Whether each picture has transparency, by its path, size and date: a picture is looked at once.
    private static readonly Dictionary<string, bool> AlphaKnown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a mask picture is to be read by its transparency rather than its brightness: it has an alpha
    /// channel, and some of it is actually see-through. Such a mask shows the layer where the picture is painted,
    /// in any colour, and cuts it away where the picture is transparent. A picture without transparency is read
    /// as black and white. False for a file that cannot be read.
    /// </summary>
    public static bool UsesAlpha(string path)
    {
        try
        {
            // Asked every time the graph is built: what is known of the file comes from memory (see FileState).
            var file = FileState.Get(path);
            if (!file.Exists)
                return false;

            var fullName = Path.GetFullPath(path.Trim().Trim('"'));
            var key = $"{fullName}|{file.Length}|{file.WrittenUtc.Ticks}";
            lock (AlphaKnown)
            {
                if (AlphaKnown.TryGetValue(key, out var known))
                    return known;
            }

            var found = HasTransparency(fullName);
            lock (AlphaKnown)
                AlphaKnown[key] = found;
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasTransparency(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        var format = frame.Format;
        if (format != PixelFormats.Bgra32 && format != PixelFormats.Pbgra32 && format != PixelFormats.Rgba64 && format != PixelFormats.Prgba64
            && format != PixelFormats.Rgba128Float && format != PixelFormats.Prgba128Float)
        {
            return false;
        }

        var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = pixels.PixelWidth * 4;
        var row = new byte[stride];
        for (var y = 0; y < pixels.PixelHeight; y++)
        {
            pixels.CopyPixels(new Int32Rect(0, y, pixels.PixelWidth, 1), row, stride, 0);
            for (var x = 3; x < stride; x += 4)
            {
                if (row[x] < 250)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A mask picture as grey levels, white where it shows, read as <see cref="UsesAlpha"/> says, and made small
    /// enough to look over quickly: at most so many pixels across. For finding the separate pieces in a mask.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height)? ReadGrey(string path, int largest = 960)
    {
        try
        {
            var alpha = UsesAlpha(path);
            using var stream = File.OpenRead(path);
            BitmapSource frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth > largest)
            {
                var scale = (double)largest / frame.PixelWidth;
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }

            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var (width, height) = (bgra.PixelWidth, bgra.PixelHeight);
            var raw = new byte[width * height * 4];
            bgra.CopyPixels(raw, width * 4, 0);
            var grey = new byte[width * height];
            for (int i = 0, j = 0; i < grey.Length; i++, j += 4)
                grey[i] = alpha ? raw[j + 3] : (byte)((raw[j] * 29 + raw[j + 1] * 150 + raw[j + 2] * 77) >> 8);
            return (grey, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    // ----- The separate pieces of a mask -----

    /// <summary>
    /// The islands of a mask of the whole frame: each patch that shows, apart from the rest, as the box round
    /// it in fractions of the frame. Specks of a few pixels are left out. Null when the picture cannot be read.
    /// </summary>
    public static List<Rect>? FindIslands(string path)
    {
        if (ReadGrey(path) is not var (pixels, width, height))
            return null;

        var seen = new bool[pixels.Length];
        var islands = new List<Rect>();
        var stack = new Stack<int>();
        var smallest = Math.Max(6, width * height / 50_000);
        for (var start = 0; start < pixels.Length; start++)
        {
            if (seen[start] || pixels[start] < 128)
                continue;

            // Everything that touches it, corners included, is the same island.
            var (left, top, right, bottom, count) = (int.MaxValue, int.MaxValue, -1, -1, 0);
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                var (x, y) = (at % width, at / width);
                (left, top, right, bottom, count) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y), count + 1);
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;

                        var next = ny * width + nx;
                        if (!seen[next] && pixels[next] >= 128)
                        {
                            seen[next] = true;
                            stack.Push(next);
                        }
                    }
                }
            }

            if (count >= smallest)
                islands.Add(new Rect((double)left / width, (double)top / height, (right - left + 1.0) / width, (bottom - top + 1.0) / height));
        }

        return islands;
    }

    /// <summary>
    /// Islands joined into pieces: two that come closer than the distance given (a fraction of the frame's width,
    /// across or down) are one piece, so that a row of separately traced ability icons is one layer, not one
    /// each. In reading order: top to bottom, then left to right. Each piece's box has a little room spare.
    /// </summary>
    public static List<Rect> GroupIslands(IReadOnlyList<Rect> islands, double join, double aspect = 16.0 / 9)
    {
        var (joinX, joinY) = (Math.Max(join, 0), Math.Max(join, 0) * aspect);
        var pieces = islands.ToList();
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < pieces.Count && !merged; i++)
            {
                for (var j = i + 1; j < pieces.Count; j++)
                {
                    var (a, b) = (pieces[i], pieces[j]);
                    var gapX = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
                    var gapY = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
                    if (gapX <= joinX && gapY <= joinY)
                    {
                        pieces[i] = Rect.Union(a, b);
                        pieces.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }
        }

        // Room for a soft edge: about 8 pixels of a 1440-line frame.
        const double roomX = 8 / 2560.0, roomY = 8 / 1440.0;
        return pieces
            .Select(r => new Rect(new Point(Math.Max(r.Left - roomX, 0), Math.Max(r.Top - roomY, 0)), new Point(Math.Min(r.Right + roomX, 1), Math.Min(r.Bottom + roomY, 1))))
            .OrderBy(r => Math.Round(r.Top * 8)).ThenBy(r => r.Left)
            .ToList();
    }

    /// <summary>A name for a piece from where it is on the frame: Top Left, Bottom Center...</summary>
    public static string PlaceName(Rect piece)
    {
        var (x, y) = (piece.X + piece.Width / 2, piece.Y + piece.Height / 2);
        var down = y < 1 / 3.0 ? "Top" : y < 2 / 3.0 ? "Middle" : "Bottom";
        var across = x < 1 / 3.0 ? "Left" : x < 2 / 3.0 ? "Center" : "Right";
        return $"{down} {across}";
    }

    // ----- Drawn from outlines -----

    /// <summary>The outlines in a mask's text, each with at least three points. Points that cannot be read are skipped.</summary>
    public static List<List<Point>> ParseShapes(string? text)
    {
        var shapes = new List<List<Point>>();
        foreach (var outline in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var points = new List<Point>();
            foreach (var pair in outline.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(',');
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && double.IsFinite(x) && double.IsFinite(y))
                {
                    points.Add(new Point(Math.Clamp(x, -1, 2), Math.Clamp(y, -1, 2)));
                }
            }

            if (points.Count >= 3)
                shapes.Add(points);
        }

        return shapes;
    }

    /// <summary>Outlines as a mask's text, each point to five places.</summary>
    public static string FormatShapes(IEnumerable<IReadOnlyList<Point>> shapes) =>
        string.Join(";", shapes.Where(s => s.Count >= 3).Select(s => string.Join(" ", s.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{Math.Round(p.X, 5)},{Math.Round(p.Y, 5)}")))));

    /// <summary>
    /// A Cutout's own drawn mask (not the frame's) moved from the part of the picture it was cut from (<paramref name="part"/>,
    /// fractions of the whole) onto the whole picture, as the mask of a Cutout of a file is (5.2-AuditB): its outlines go where
    /// they lay, so the crop cuts the same mask. A mask picture of its own is left as it is: it cannot be moved without a file.
    /// </summary>
    public static void SpreadOverPicture(LayerState state, (double X, double Y, double Width, double Height) part)
    {
        if (!state.CustomMask || state.MaskFrame || !IsDrawn(SourceOf(state.MaskPath, state.MaskShapes)))
            return;

        var moved = ParseShapes(state.MaskShapes).Select(s => (IReadOnlyList<Point>)s.Select(p => new Point(part.X + p.X * part.Width, part.Y + p.Y * part.Height)).ToList());
        (state.MaskPath, state.MaskShapes) = ("", FormatShapes(moved));
    }

    /// <summary>How large a drawn mask is made as a picture for a source of this size: at least 1920 wide and at most 3840, in the source's shape.</summary>
    public static (int Width, int Height) SizeFor(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return (2560, 1440);

        var width = Math.Clamp(sourceWidth, 1920, 3840);
        return (width, Math.Max((int)Math.Round(width * (double)sourceHeight / sourceWidth), 2));
    }

    // ----- Drawn masks: outlines only, never a file (5.1-Release, the user's call) -----

    /// <summary>
    /// The start of a drawn mask's key: a mask that is its layer's outlines alone. It is no file: the GPU engine and the hit test
    /// fill the outlines in memory at the size they need (<see cref="Rasterize"/>), styles and projects carry only the outlines, and
    /// only FFmpeg's fallback graph is given a picture, in the session's temp folder (<see cref="FileFor"/>).
    /// </summary>
    public const string DrawnPrefix = "drawn:";

    // Each drawn key's outlines (normalised text), so a key alone is enough to fill them: on any thread.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> DrawnShapes = new(StringComparer.Ordinal);

    /// <summary>The key of the mask some outlines make (the same outlines, the same key); "" when they have none.</summary>
    public static string DrawnKey(string? shapes)
    {
        var text = FormatShapes(ParseShapes(shapes));
        if (text.Length == 0)
            return "";

        var key = DrawnPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
        DrawnShapes.TryAdd(key, text);
        return key;
    }

    /// <summary>Whether a mask source is a drawn mask's key, not a file.</summary>
    public static bool IsDrawn(string? source) => source?.StartsWith(DrawnPrefix, StringComparison.Ordinal) == true;

    /// <summary>Whether a mask file is one HandPeg drew from outlines before 5.1-Release (shape_*.png): its layer's outlines stand in for it.</summary>
    public static bool IsGeneratedFile(string? path) =>
        Path.GetFileName((path ?? "").Trim().Trim('"')).StartsWith("shape_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a layer's mask is: its outlines (a drawn key) when it has some and names no picture of its own (or only one HandPeg
    /// drew from them), else its picture file, trimmed; "" for none.
    /// </summary>
    public static string SourceOf(string? path, string? shapes)
    {
        var file = (path ?? "").Trim().Trim('"');
        return (file.Length == 0 || IsGeneratedFile(file)) && DrawnKey(shapes) is { Length: > 0 } key ? key : file;
    }

    /// <summary>Whether a mask source is there to be shown: a drawn key whose outlines are known, or a file that exists.</summary>
    public static bool Exists(string? source) =>
        IsDrawn(source) ? DrawnShapes.ContainsKey(source!) : !string.IsNullOrEmpty(source) && FileState.Exists(source);

    /// <summary>
    /// A drawn mask filled in memory: the part of the frame <paramref name="cut"/> covers (fractions of the frame) at the size
    /// asked, each value the share of the pixel inside the outlines (nonzero winding: overlapping outlines add up), 0..1. Pure
    /// arithmetic, so on any thread. Null for a key whose outlines are not known.
    /// </summary>
    public static float[]? Rasterize(string key, (double X, double Y, double Width, double Height) cut, int width, int height)
    {
        if (!DrawnShapes.TryGetValue(key, out var text) || width <= 0 || height <= 0)
            return null;

        var (cutWidth, cutHeight) = (Math.Max(cut.Width, 1e-6), Math.Max(cut.Height, 1e-6));
        var edges = new List<(double X0, double Y0, double X1, double Y1, int Up)>();
        foreach (var shape in ParseShapes(text))
        {
            for (var i = 0; i < shape.Count; i++)
            {
                var (a, b) = (shape[i], shape[(i + 1) % shape.Count]);
                var (ax, ay, bx, by) = ((a.X - cut.X) / cutWidth * width, (a.Y - cut.Y) / cutHeight * height, (b.X - cut.X) / cutWidth * width, (b.Y - cut.Y) / cutHeight * height);
                if (ay != by)
                    edges.Add((ax, ay, bx, by, by > ay ? 1 : -1));
            }
        }

        // Four lines through each row of pixels; along each, the stretches inside are added exactly, to the fraction of a pixel.
        const int Lines = 4;
        var cover = new float[width * height];
        var crossings = new List<(double X, int Up)>();
        for (var y = 0; y < height; y++)
        {
            for (var line = 0; line < Lines; line++)
            {
                var at = y + (line + 0.5) / Lines;
                crossings.Clear();
                foreach (var (x0, y0, x1, y1, up) in edges)
                {
                    if (at < Math.Min(y0, y1) || at >= Math.Max(y0, y1))
                        continue;
                    crossings.Add((x0 + (at - y0) * (x1 - x0) / (y1 - y0), up));
                }

                if (crossings.Count < 2)
                    continue;
                crossings.Sort((p, q) => p.X.CompareTo(q.X));
                var (winding, from) = (0, 0.0);
                foreach (var (x, up) in crossings)
                {
                    var before = winding;
                    winding += up;
                    if (before == 0 && winding != 0)
                        from = x;
                    else if (before != 0 && winding == 0)
                        AddSpan(cover, y * width, width, from, x, 1f / Lines);
                }
            }
        }

        for (var i = 0; i < cover.Length; i++)
            cover[i] = Math.Min(cover[i], 1f);
        return cover;
    }

    // Adds a stretch of one row, from x = a to b (pixels, fractional), with its weight: whole pixels in full, the two ends in part.
    private static void AddSpan(float[] cover, int row, int width, double a, double b, float weight)
    {
        (a, b) = (Math.Clamp(a, 0, width), Math.Clamp(b, 0, width));
        if (b <= a)
            return;

        var (first, last) = ((int)a, (int)b);
        if (first == last)
        {
            cover[row + Math.Min(first, width - 1)] += (float)(b - a) * weight;
            return;
        }

        cover[row + first] += (float)(first + 1 - a) * weight;
        for (var x = first + 1; x < last; x++)
            cover[row + x] += weight;
        if (last < width)
            cover[row + last] += (float)(b - last) * weight;
    }

    /// <summary>
    /// A mask source as a file, for FFmpeg's fallback graph, which reads masks as pictures: a file as it is; a drawn mask filled
    /// at <paramref name="width"/> x <paramref name="height"/> into the session's temp folder (gone when it closes), never the
    /// Masks folder. "" when it cannot be made.
    /// </summary>
    public static string FileFor(string source, int width, int height)
    {
        if (!IsDrawn(source))
            return source;

        var path = Path.Combine(SessionPaths.Masks, $"{source[DrawnPrefix.Length..]}_{width}x{height}.png");
        try
        {
            if (File.Exists(path))
                return path;
            if (Rasterize(source, (0, 0, 1, 1), width, height) is not { } cover)
                return "";

            var grey = new byte[cover.Length];
            for (var i = 0; i < grey.Length; i++)
                grey[i] = (byte)Math.Clamp((int)MathF.Round(cover[i] * 255), 0, 255);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, grey, width)));
            Directory.CreateDirectory(SessionPaths.Masks);
            var part = path + ".part";
            using (var file = File.Create(part))
                encoder.Save(file);
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            AppLog.Write("A drawn mask could not be written for FFmpeg", ex);
            return "";
        }
    }

    // ----- Outlines for the built-in shapes, as fractions of a 16:9 frame -----

    // The frame the built-in outlines are measured on: the shapes are worked out in its pixels, so that a
    // circle is round and a corner is as round across as it is down, and then given as fractions of it.
    private const double FrameWidth = 1920, FrameHeight = 1080;

    /// <summary>A rectangle with rounded corners, the radius in pixels of a 1920 x 1080 frame.</summary>
    public static List<Point> RoundedRectangle(double x, double y, double width, double height, double radius = 8)
    {
        var (left, top, w, h) = (x * FrameWidth, y * FrameHeight, width * FrameWidth, height * FrameHeight);
        var r = Math.Clamp(radius, 0, Math.Min(w, h) / 2);
        var points = new List<Point>();
        void Corner(double cx, double cy, double from)
        {
            for (var i = 0; i <= 6; i++)
            {
                var angle = (from + i * 15) * Math.PI / 180;
                points.Add(Fraction(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
            }
        }

        Corner(left + w - r, top + r, 270);
        Corner(left + w - r, top + h - r, 0);
        Corner(left + r, top + h - r, 90);
        Corner(left + r, top + r, 180);
        return points;
    }

    /// <summary>An ellipse through the edges of a box given as fractions of the frame.</summary>
    public static List<Point> Ellipse(double x, double y, double width, double height, int steps = 48)
    {
        var (cx, cy) = ((x + width / 2) * FrameWidth, (y + height / 2) * FrameHeight);
        var (rx, ry) = (width / 2 * FrameWidth, height / 2 * FrameHeight);
        return Enumerable.Range(0, steps)
            .Select(i => Fraction(cx + rx * Math.Cos(2 * Math.PI * i / steps), cy + ry * Math.Sin(2 * Math.PI * i / steps)))
            .ToList();
    }

    /// <summary>A bar leaning to the right inside a box: its top edge starts a slant's width in, its bottom edge stops that far short.</summary>
    public static List<Point> Slant(double x, double y, double width, double height, double lean = 0.1) =>
    [
        new(x + width * lean, y), new(x + width, y), new(x + width * (1 - lean), y + height), new(x, y + height),
    ];

    /// <summary>A polygon through points given in pixels of a 1920 x 1080 frame.</summary>
    public static List<Point> Polygon(params (double X, double Y)[] pixels) => pixels.Select(p => Fraction(p.X, p.Y)).ToList();

    private static Point Fraction(double x, double y) => new(Math.Round(x / FrameWidth, 5), Math.Round(y / FrameHeight, 5));
}
