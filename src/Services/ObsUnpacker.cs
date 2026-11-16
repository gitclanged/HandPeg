namespace HandPegApp.Services;

// ----- OBS side-by-side unpacker -----
//
// Splits a double-wide recording (e.g. 3840 x 1080 or 5120 x 1440: gameplay left, facecam right, both 16:9) into the clip cropped to
// its left half plus a child Cutout of the right half on the same clock, so both follow every split, trim and move. Wide Sequences:
// gameplay fills the frame, facecam as a corner inset. Tall (9:16): facecam band on top, gameplay fills the rest; side-cropped, never
// stretched.

/// <summary>Placed half: crop (picture fractions) and box (frame fractions; height derived from the crop).</summary>
public readonly record struct UnpackedPart(double CropLeft, double CropTop, double CropRight, double CropBottom, double X, double Y, double Width);

/// <summary>Where the two halves go: the gameplay (the clip itself) and the facecam (its Cutout).</summary>
public readonly record struct UnpackedCanvas(UnpackedPart Gameplay, UnpackedPart Facecam);

public static class ObsUnpacker
{
    /// <summary>Minimum aspect treated as side-by-side (two 16:9 = 3.56; a single 21:9 = 2.33).</summary>
    public const double SideBySideAspect = 3.0;

    /// <summary>Wide-frame facecam width and right/bottom margin, as fractions of frame width.</summary>
    public const double CornerWidth = 0.3, CornerMargin = 0.025;

    /// <summary>True when the aspect is at least <see cref="SideBySideAspect"/>.</summary>
    public static bool IsSideBySide(int width, int height) => width > 0 && height > 0 && (double)width / height >= SideBySideAspect - 1e-9;

    /// <summary>
    /// Layout of both halves of a <paramref name="pictureWidth"/> x <paramref name="pictureHeight"/> picture on a
    /// <paramref name="frameWidth"/> x <paramref name="frameHeight"/> frame.
    /// </summary>
    public static UnpackedCanvas Layout(int pictureWidth, int pictureHeight, int frameWidth, int frameHeight)
    {
        var half = pictureWidth / 2.0 / Math.Max(pictureHeight, 1);
        var frame = (double)Math.Max(frameWidth, 1) / Math.Max(frameHeight, 1);
        if (frame >= 1)
        {
            // Wide: the gameplay cut to the frame's shape and filling it; the facecam whole, in the bottom right corner.
            var gameplay = Fill(0, half, frame, 0, 0, 1);
            var height = CornerWidth / half * frame;
            var facecam = new UnpackedPart(0.5, 0, 0, 0, 1 - CornerMargin - CornerWidth, 1 - CornerMargin * frame - height, CornerWidth);
            return new UnpackedCanvas(gameplay, facecam);
        }

        // Tall: facecam band on top at full width (its natural height, max 40 %), gameplay below; both side-cropped to fit.
        var band = Math.Min(frame / half, 0.4);
        var face = Fill(0.5, half, frame / band, 0, 0, 1);
        var game = Fill(0, half, frame / (1 - band), 0, band, 1);
        return new UnpackedCanvas(game, face);
    }

    /// <summary>
    /// Centre-crops a half (left edge <paramref name="from"/>, aspect <paramref name="half"/>) to aspect <paramref name="shape"/> and places
    /// it at (<paramref name="x"/>, <paramref name="y"/>) with <paramref name="width"/> (frame fractions).
    /// </summary>
    private static UnpackedPart Fill(double from, double half, double shape, double x, double y, double width)
    {
        if (half > shape)
        {
            var kept = 0.5 * shape / half;
            var left = from + (0.5 - kept) / 2;
            return new UnpackedPart(left, 0, 1 - left - kept, 0, x, y, width);
        }

        var tall = half / shape;
        var top = (1 - tall) / 2;
        return new UnpackedPart(from, top, 1 - from - 0.5, 1 - top - tall, x, y, width);
    }
}
