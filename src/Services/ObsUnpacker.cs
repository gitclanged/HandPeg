namespace HandPegApp.Services;

// ----- The OBS side-by-side unpacker (Pass 5.2C) -----
//
// A streamer's recording that holds two pictures side by side on one double-wide canvas (OBS's 3840 x 1080 or 5120 x 1440: the
// gameplay on the left, the facecam on the right, each a 16:9 picture) is one clip of one file. Unpacked, the clip shows its left
// half (cropped to it) and a Cutout of the same file on the same clock shows its right half, so the two stay in step through every
// split, trim and move (the Cutout is the clip's child). Where they go depends on the Sequence's shape: on a wide frame the gameplay
// fills it and the facecam is a corner picture; on a tall frame (9:16) the facecam is a band across the top and the gameplay fills
// the rest, each cut at its sides to the place it fills, never stretched.

/// <summary>One half of the canvas as placed: its crop (fractions of the whole picture) and its box (fractions of the frame; its height follows from the crop).</summary>
public readonly record struct UnpackedPart(double CropLeft, double CropTop, double CropRight, double CropBottom, double X, double Y, double Width);

/// <summary>Where the two halves go: the gameplay (the clip itself) and the facecam (its Cutout).</summary>
public readonly record struct UnpackedCanvas(UnpackedPart Gameplay, UnpackedPart Facecam);

public static class ObsUnpacker
{
    /// <summary>The least width over height of a picture taken for two side by side (two 16:9 pictures are 3.56; one 21:9 picture is 2.33).</summary>
    public const double SideBySideAspect = 3.0;

    /// <summary>The facecam's width as a fraction of a wide frame's, and its margin from the frame's right and bottom edges (of the frame's width).</summary>
    public const double CornerWidth = 0.3, CornerMargin = 0.025;

    /// <summary>Whether a picture of this size is two pictures side by side (<see cref="SideBySideAspect"/> or wider).</summary>
    public static bool IsSideBySide(int width, int height) => width > 0 && height > 0 && (double)width / height >= SideBySideAspect - 1e-9;

    /// <summary>
    /// Where the halves of a side-by-side picture (<paramref name="pictureWidth"/> x <paramref name="pictureHeight"/>) go on a frame of
    /// <paramref name="frameWidth"/> x <paramref name="frameHeight"/> (see the top of this file).
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

        // Tall: the facecam a band across the top, as tall as its own shape makes it at the frame's width (at most 40 % of the frame), cut at
        // its sides to that band; the gameplay fills the rest, cut at its sides to it.
        var band = Math.Min(frame / half, 0.4);
        var face = Fill(0.5, half, frame / band, 0, 0, 1);
        var game = Fill(0, half, frame / (1 - band), 0, band, 1);
        return new UnpackedCanvas(game, face);
    }

    /// <summary>
    /// One half (its left edge at <paramref name="from"/> of the whole picture, <paramref name="half"/> its width over height) cut about its
    /// middle to <paramref name="shape"/> (width over height), at its sides when it is wider, top and bottom when it is taller; boxed at
    /// (<paramref name="x"/>, <paramref name="y"/>) and <paramref name="width"/> of the frame.
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
