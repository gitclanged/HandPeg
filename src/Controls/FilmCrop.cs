using System.Windows;

namespace HandPegApp;

/// <summary>
/// Pass 5.6.4A: the normalized source-frame region [<paramref name="UMin"/>, <paramref name="VMin"/>, <paramref name="UMax"/>,
/// <paramref name="VMax"/>] a Cutout's timeline filmstrip shows, cut from the parent file's shared strip (no extra decoding). A Cutout's is
/// its cropped source rect intersected with its drawn mask's outline bounds (user decision); <see cref="Full"/> for every other clip.
/// </summary>
public readonly record struct FilmCrop(double UMin, double VMin, double UMax, double VMax)
{
    /// <summary>The whole frame.</summary>
    public static readonly FilmCrop Full = new(0, 0, 1, 1);

    /// <summary>Least crop each way in strip pixels, so a tiny HUD cutout still shows a readable tile (user decision: 16 x 16).</summary>
    public const int MinStripPixels = 16;

    public double Width => UMax - UMin;

    public double Height => VMax - VMin;

    /// <summary>True when it covers (practically) the whole frame, or is empty or unreadable: draw the frames as they are.</summary>
    public bool IsFull => !(Width > 1e-6 && Height > 1e-6) || (UMin <= 1e-4 && VMin <= 1e-4 && UMax >= 1 - 1e-4 && VMax >= 1 - 1e-4);

    /// <summary>A rect (fractions), clamped to the frame.</summary>
    public static FilmCrop FromRect(double x, double y, double width, double height) => Clamped(x, y, x + width, y + height);

    /// <summary>
    /// Outline bounds mapped into the frame: each point (<paramref name="offsetX"/>/<paramref name="offsetY"/> added, fractions of the
    /// part) lies at part origin + point x part size. Null without a finite point.
    /// </summary>
    public static FilmCrop? Bounds(IEnumerable<IReadOnlyList<Point>> shapes, double partX, double partY, double partWidth, double partHeight,
        double offsetX = 0, double offsetY = 0)
    {
        var (uMin, vMin, uMax, vMax) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var shape in shapes)
        {
            foreach (var point in shape)
            {
                var (u, v) = (partX + (point.X + offsetX) * partWidth, partY + (point.Y + offsetY) * partHeight);
                if (!double.IsFinite(u) || !double.IsFinite(v))
                    continue;
                (uMin, vMin, uMax, vMax) = (Math.Min(uMin, u), Math.Min(vMin, v), Math.Max(uMax, u), Math.Max(vMax, v));
            }
        }

        return uMax >= uMin && vMax >= vMin ? Clamped(uMin, vMin, uMax, vMax) : null;
    }

    /// <summary>The overlap of two regions; this one when they do not overlap.</summary>
    public FilmCrop Intersect(FilmCrop other)
    {
        var both = new FilmCrop(Math.Max(UMin, other.UMin), Math.Max(VMin, other.VMin), Math.Min(UMax, other.UMax), Math.Min(VMax, other.VMax));
        return both.Width > 1e-6 && both.Height > 1e-6 ? both : this;
    }

    /// <summary>
    /// The region in a strip frame of <paramref name="frameWidth"/> x <paramref name="frameHeight"/> pixels: at least
    /// <see cref="MinStripPixels"/> each way (capped by the frame), grown about its centre and kept inside the frame.
    /// </summary>
    public Int32Rect ToPixels(int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
            return Int32Rect.Empty;
        var region = IsFull ? Full : this;
        var (x, width) = Span(region.UMin, region.UMax, frameWidth);
        var (y, height) = Span(region.VMin, region.VMax, frameHeight);
        return new Int32Rect(x, y, width, height);

        static (int Start, int Length) Span(double from, double to, int size)
        {
            var length = (int)Math.Clamp(Math.Round(Math.Max((to - from) * size, Math.Min(MinStripPixels, size))), 1, size);
            var start = (int)Math.Clamp(Math.Round((from + to) / 2 * size - length / 2.0), 0, size - length);
            return (start, length);
        }
    }

    private static FilmCrop Clamped(double uMin, double vMin, double uMax, double vMax)
    {
        static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        (uMin, vMin, uMax, vMax) = (Unit(uMin), Unit(vMin), Unit(uMax), Unit(vMax));
        return new FilmCrop(Math.Min(uMin, uMax), Math.Min(vMin, vMax), Math.Max(uMin, uMax), Math.Max(vMin, vMax));
    }
}
