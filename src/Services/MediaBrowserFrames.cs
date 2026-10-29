using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

/// <summary>
/// The media browser's pictures of a video: a strip of a few frames spread over its length (15 by default, 480
/// pixels tall), made once by FFmpeg and kept in the browser's cache (<see cref="PreviewCache.Browser"/>), and the
/// frames cut from it at the size a tile shows them. What a file is (its length and shape) is kept there too, so a
/// folder opened again needs no FFmpeg at all. With the cache off, both are kept for the session only.
/// </summary>
public static class MediaBrowserFrames
{
    // With the cache off: what was made this session, by file and kind, so a folder opened twice is not made twice.
    private static readonly ConcurrentDictionary<string, string> SessionFiles = new(StringComparer.OrdinalIgnoreCase);

    private static string SessionFolder => Path.Combine(SessionPaths.Root, "browser");

    /// <summary>What a video is: how long it plays and its picture's size; null when it cannot be read.</summary>
    /// <param name="lane">Background for the tiles on screen; the folder indexer runs it inside its own Idle work.</param>
    /// <remarks>
    /// Like <see cref="StripAsync"/> and <see cref="PosterOnlyAsync"/>, all of it runs on the thread pool (4.3E): the cache's
    /// reads and writes, and the start of the tool. Asked for on the interface's thread, as the browser asks, the work
    /// started there, and with it every ffprobe and FFmpeg the browser and the folder indexer ran, a few dozen
    /// milliseconds each, in the middle of a drag and drop.
    /// </remarks>
    public static Task<(double Seconds, int Width, int Height)?> DescribeAsync(string path, CancellationToken token, WorkLane lane = WorkLane.Background) =>
        Task.Run<(double Seconds, int Width, int Height)?>(async () =>
        {
            if (ReadKept(path, "probe", ".txt") is { } kept && ParseProbe(kept) is { } known)
                return known;

            var info = await MediaProbe.ProbeAsync(path, token, lane);
            if (info is null)
                return null;

            var result = (Math.Max(info.DurationSeconds, 0), info.Video?.Width ?? 0, info.Video?.Height ?? 0);
            WriteKept(path, "probe", ".txt", string.Create(CultureInfo.InvariantCulture, $"{result.Item1:0.###}|{result.Item2}|{result.Item3}"));
            return result;
        }, token);

    private static (double Seconds, int Width, int Height)? ParseProbe(string text)
    {
        var parts = text.Split('|');
        return parts.Length == 3
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
               && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            ? (seconds, width, height)
            : null;
    }

    /// <summary>
    /// A video's tile from what the cache already holds (4.1.1): its kept description and strip, and the poster cut from
    /// that strip, with no tool run and no wait for anything queued; null when either is not kept. Off the interface.
    /// </summary>
    public static (double Seconds, int Width, int Height, string Strip, BitmapSource? Poster)? Kept(string path, int frames, int frameHeight, int height, double at)
    {
        if (ReadKept(path, "probe", ".txt") is not { } text || ParseProbe(text) is not { Width: > 0, Height: > 0 } video)
            return null;

        var shape = (double)video.Width / video.Height;
        if (FindKept(path, StripKind(frames, frameHeight, shape), ".jpg") is not { } strip)
            return null;

        return (video.Seconds, video.Width, video.Height, strip, Poster(strip, shape, height, at));
    }

    // The cache's name for a strip of so many frames, at the height it is made (lowered for a strip too wide for a JPEG).
    private static string StripKind(int frames, int frameHeight, double shape) =>
        string.Create(CultureInfo.InvariantCulture, $"frames{frames}h{(int)Math.Min(frameHeight, 60000 / (frames * shape))}");

    /// <summary>
    /// The strip of a video's frames: kept, or made now. Null when it could not be made. Its frames are as tall as
    /// asked, unless that would make a strip too wide for a JPEG (65,535 pixels), when they are made smaller.
    /// </summary>
    public static Task<string?> StripAsync(string path, double seconds, int width, int height, int frames, int frameHeight, CancellationToken token, WorkLane lane = WorkLane.Background) =>
        Task.Run<string?>(async () =>
        {
            var shape = width > 0 && height > 0 ? (double)width / height : 16.0 / 9;
            var kind = StripKind(frames, frameHeight, shape);
            var madeHeight = (int)Math.Min(frameHeight, 60000 / (frames * shape));
            if (FindKept(path, kind, ".jpg") is { } kept)
                return kept;

            var made = Path.Combine(SessionFolder, $"{Guid.NewGuid():N}.jpg");
            if (!await FfmpegRunner.GenerateFilmstripAsync(path, made, frames, seconds, token, madeHeight, lane))
                return null;

            if (PreviewCache.Browser.IsEnabled)
                return PreviewCache.Browser.Keep(path, kind, ".jpg", made);

            SessionFiles[Key(path, kind)] = made;
            return made;
        }, token);

    /// <summary>The least height of a poster made on its own (4.2D): taller tiles get a taller one, in steps of this.</summary>
    public const int PosterOnlyHeight = 144;

    /// <summary>
    /// A video's poster on its own, before its strip (4.2D, the first of two passes): one frame at 30 % of its length (1 s
    /// when the length is not known), at least <see cref="PosterOnlyHeight"/> pixels tall, kept in the browser's cache as
    /// <c>posterh144.jpg</c> (or taller), or made now. The path of the JPEG; null when it could not be made.
    /// </summary>
    public static Task<string?> PosterOnlyAsync(string path, double seconds, int height, CancellationToken token, WorkLane lane) =>
        Task.Run<string?>(async () =>
        {
            var madeHeight = PosterHeightFor(height);
            var kind = string.Create(CultureInfo.InvariantCulture, $"posterh{madeHeight}");
            if (FindKept(path, kind, ".jpg") is { } kept)
                return kept;

            var made = Path.Combine(SessionFolder, $"{Guid.NewGuid():N}.jpg");
            if (!await FfmpegRunner.GeneratePosterFrameAsync(path, made, seconds > 0 ? seconds * 0.3 : 1.0, madeHeight, token, lane))
                return null;

            if (PreviewCache.Browser.IsEnabled)
                return PreviewCache.Browser.Keep(path, kind, ".jpg", made);

            SessionFiles[Key(path, kind)] = made;
            return made;
        }, token);

    /// <summary>
    /// A video's tile from a kept description and a kept poster made on its own (4.2D), when its strip is not kept yet: the
    /// poster at once, the strip after (the second pass). Null when either is not kept. Off the interface.
    /// </summary>
    public static (double Seconds, int Width, int Height, BitmapSource? Poster)? KeptPosterOnly(string path, int height)
    {
        if (ReadKept(path, "probe", ".txt") is not { } text || ParseProbe(text) is not { Width: > 0, Height: > 0 } video)
            return null;

        if (FindKept(path, string.Create(CultureInfo.InvariantCulture, $"posterh{PosterHeightFor(height)}"), ".jpg") is not { } poster)
            return null;

        return (video.Seconds, video.Width, video.Height, DecodeAt(poster, height));
    }

    // The height a poster is made at for a tile so many pixels tall: whole steps of PosterOnlyHeight, so a few sizes serve all.
    private static int PosterHeightFor(int height) =>
        Math.Max(PosterOnlyHeight, (height + PosterOnlyHeight - 1) / PosterOnlyHeight * PosterOnlyHeight);

    /// <summary>
    /// The frames of a strip, each a picture of its own at the given height, all the same size. Decoded small (JPEG can be
    /// decoded at a fraction of its size): it is for a tile. Frozen.
    /// </summary>
    /// <remarks>
    /// The strip is decoded once, at the tile's height and at a width of exactly as many frames of one whole number of
    /// pixels each, so every frame is cut exactly where it begins (4.3E). Decoded at the height alone, its frames came out
    /// a fraction of a pixel wide past a whole number (4,973 pixels for 25 frames: 198.9 each, a 420-pixel strip decoded
    /// 112 tall), they were cut a whole 198 apart, and each was cut further left of where it begins the further along the
    /// strip it is: 22 pixels by the 25th, a sliver of the frame before it at the left and the picture pushed right, more
    /// at every frame as the pointer crossed the tile. That was the hover picture sliding right.
    /// </remarks>
    public static IReadOnlyList<BitmapSource>? Frames(string strip, double shape, int height)
    {
        try
        {
            if (Layout(strip, shape) is not { } layout)
                return null;

            var frameHeight = Math.Max(height, 8);
            var decoded = Decode(strip, FrameWidthAt(layout, frameHeight) * layout.Count, frameHeight, null);

            // Where each frame begins in what was decoded: exactly a whole frame's width apart, unless the decoder gave
            // another width than asked; then where it falls, never a step that adds up along the strip.
            var across = (double)decoded.PixelWidth / layout.Count;
            var width = Math.Max((int)across, 1);
            var frames = new List<BitmapSource>(layout.Count);
            for (var i = 0; i < layout.Count; i++)
                frames.Add(Cut(decoded, new Int32Rect(Math.Clamp((int)Math.Round(i * across), 0, decoded.PixelWidth - width), 0, width, decoded.PixelHeight)));

            return frames;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or UriFormatException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// One frame of a strip as a picture standing on its own: a tile's poster, the same size as <see cref="Frames"/>' frames
    /// at that height, so the poster and the frames scrubbed through fill the tile's box alike. Only that frame is kept (the
    /// strip's size is read from its header, and the decode is cropped to the frame, in the strip's own pixels, before it
    /// is scaled), never the whole strip decoded for it, which left a megabyte and a half of pixels per tile waiting to be
    /// collected. Frozen.
    /// </summary>
    public static BitmapSource? Poster(string strip, double shape, int height, double at)
    {
        try
        {
            if (Layout(strip, shape) is not { } layout)
                return null;

            var index = Math.Clamp((int)(at * layout.Count), 0, layout.Count - 1);
            var (left, right) = (FrameStart(layout, index), FrameStart(layout, index + 1));
            var frameHeight = Math.Max(height, 8);
            var image = Decode(strip, FrameWidthAt(layout, frameHeight), frameHeight, new Int32Rect(left, 0, right - left, layout.Height));
            return Cut(image, new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or UriFormatException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// How a strip is laid out, from its header alone (no pixels decoded): its size in pixels and how many frames it holds.
    /// FFmpeg makes every frame of a strip the same width (scale=-2: the video's shape at the strip's height, to an even
    /// pixel) and sets them side by side, so the strip is exactly that many of them: the count is the one nearest what
    /// the video's shape gives that divides the strip's width (the shape as probed can be a pixel or two off the frames').
    /// </summary>
    private static (int Width, int Height, int Count)? Layout(string strip, double shape)
    {
        int width, height;
        using (var stream = File.OpenRead(strip))
        {
            var header = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            (width, height) = (header.PixelWidth, header.PixelHeight);
        }

        if (width <= 0 || height <= 0)
            return null;

        var guess = Math.Max((int)Math.Round(width / (height * (shape > 0 ? shape : 16.0 / 9))), 1);
        for (var step = 0; step <= Math.Max(guess / 4, 2); step++)
        {
            if (width % (guess + step) == 0)
                return (width, height, guess + step);
            if (guess - step >= 1 && width % (guess - step) == 0)
                return (width, height, guess - step);
        }

        return (width, height, guess);
    }

    // Where a frame of a strip begins, in the strip's own pixels: a whole number of frames' widths in (the last frame's end
    // for one past the last).
    private static int FrameStart((int Width, int Height, int Count) layout, int index) =>
        (int)Math.Round(index * (double)layout.Width / layout.Count);

    // How wide one frame is drawn at a height: its shape in the strip, to a whole pixel.
    private static int FrameWidthAt((int Width, int Height, int Count) layout, int height) =>
        Math.Max((int)Math.Round((double)height * layout.Width / layout.Count / layout.Height), 1);

    // A JPEG decoded at exactly the size given (both sides set: WPF then keeps to them, rather than working one out from the
    // other and rounding it), from a file stream; with a source rectangle, only that part of it, cut in its own pixels
    // before it is scaled. No IgnoreImageCache with a stream: WPF's image cache is kept by address, and with none it throws.
    private static BitmapSource Decode(string path, int width, int height, Int32Rect? source)
    {
        using var file = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = Math.Max(width, 1);
        image.DecodePixelHeight = Math.Max(height, 1);
        if (source is { } rect)
            image.SourceRect = rect;
        image.StreamSource = file;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// The pixels of one rectangle of a picture, copied into a picture of their own at 96 DPI (whatever the JPEG said), so
    /// that every frame and poster is sized alike by WPF. Frozen.
    /// </summary>
    private static BitmapSource Cut(BitmapSource source, Int32Rect rect)
    {
        var stride = (rect.Width * source.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * rect.Height];
        source.CopyPixels(rect, pixels, stride, 0);
        var frame = BitmapSource.Create(rect.Width, rect.Height, 96, 96, source.Format, source.Palette, pixels, stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>A picture file, decoded at the given height: an image's poster. Frozen; null when it cannot be read.</summary>
    public static BitmapSource? Picture(string path, int height) => DecodeAt(path, height);

    // Read from a file stream, not a URI: a URI goes through WPF's web request path (URL monikers and security zones,
    // loaded for a local file). No IgnoreImageCache with a stream: the image cache is kept by address, and with none it throws.
    private static BitmapSource? DecodeAt(string path, int height)
    {
        try
        {
            using var file = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelHeight = Math.Max(height, 8);
            image.StreamSource = file;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UriFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static string Key(string path, string kind) => $"{path}|{kind}";

    private static string? FindKept(string path, string kind, string extension) =>
        PreviewCache.Browser.IsEnabled
            ? PreviewCache.Browser.Find(path, kind, extension)
            : SessionFiles.TryGetValue(Key(path, kind), out var made) && File.Exists(made) ? made : null;

    private static string? ReadKept(string path, string kind, string extension)
    {
        try
        {
            return FindKept(path, kind, extension) is { } kept ? File.ReadAllText(kept) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteKept(string path, string kind, string extension, string text)
    {
        if (PreviewCache.Browser.IsEnabled)
        {
            PreviewCache.Browser.WriteBytes(path, kind, extension, System.Text.Encoding.UTF8.GetBytes(text));
            return;
        }

        try
        {
            Directory.CreateDirectory(SessionFolder);
            var made = Path.Combine(SessionFolder, $"{Guid.NewGuid():N}{extension}");
            File.WriteAllText(made, text);
            SessionFiles[Key(path, kind)] = made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
