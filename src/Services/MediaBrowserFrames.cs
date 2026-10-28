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
    public static async Task<(double Seconds, int Width, int Height)?> DescribeAsync(string path, CancellationToken token, WorkLane lane = WorkLane.Background)
    {
        if (ReadKept(path, "probe", ".txt") is { } kept && ParseProbe(kept) is { } known)
            return known;

        var info = await MediaProbe.ProbeAsync(path, token, lane);
        if (info is null)
            return null;

        var result = (Math.Max(info.DurationSeconds, 0), info.Video?.Width ?? 0, info.Video?.Height ?? 0);
        WriteKept(path, "probe", ".txt", string.Create(CultureInfo.InvariantCulture, $"{result.Item1:0.###}|{result.Item2}|{result.Item3}"));
        return result;
    }

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
    public static async Task<string?> StripAsync(string path, double seconds, int width, int height, int frames, int frameHeight, CancellationToken token, WorkLane lane = WorkLane.Background)
    {
        var shape = width > 0 && height > 0 ? (double)width / height : 16.0 / 9;
        var kind = StripKind(frames, frameHeight, shape);
        frameHeight = (int)Math.Min(frameHeight, 60000 / (frames * shape));
        if (FindKept(path, kind, ".jpg") is { } kept)
            return kept;

        var made = Path.Combine(SessionFolder, $"{Guid.NewGuid():N}.jpg");
        if (!await FfmpegRunner.GenerateFilmstripAsync(path, made, frames, seconds, token, frameHeight, lane))
            return null;

        if (PreviewCache.Browser.IsEnabled)
            return PreviewCache.Browser.Keep(path, kind, ".jpg", made);

        SessionFiles[Key(path, kind)] = made;
        return made;
    }

    /// <summary>The least height of a poster made on its own (4.2D): taller tiles get a taller one, in steps of this.</summary>
    public const int PosterOnlyHeight = 144;

    /// <summary>
    /// A video's poster on its own, before its strip (4.2D, the first of two passes): one frame at 30 % of its length (1 s
    /// when the length is not known), at least <see cref="PosterOnlyHeight"/> pixels tall, kept in the browser's cache as
    /// <c>posterh144.jpg</c> (or taller), or made now. The path of the JPEG; null when it could not be made.
    /// </summary>
    public static async Task<string?> PosterOnlyAsync(string path, double seconds, int height, CancellationToken token, WorkLane lane)
    {
        height = PosterHeightFor(height);
        var kind = string.Create(CultureInfo.InvariantCulture, $"posterh{height}");
        if (FindKept(path, kind, ".jpg") is { } kept)
            return kept;

        var made = Path.Combine(SessionFolder, $"{Guid.NewGuid():N}.jpg");
        if (!await FfmpegRunner.GeneratePosterFrameAsync(path, made, seconds > 0 ? seconds * 0.3 : 1.0, height, token, lane))
            return null;

        if (PreviewCache.Browser.IsEnabled)
            return PreviewCache.Browser.Keep(path, kind, ".jpg", made);

        SessionFiles[Key(path, kind)] = made;
        return made;
    }

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
    /// The frames of a strip, each a picture of its own at the given height. The strip's frames are told apart by the
    /// video's shape. Decoded small (JPEG can be decoded at a fraction of its size): it is for a tile. Frozen.
    /// </summary>
    public static IReadOnlyList<BitmapSource>? Frames(string strip, double shape, int height)
    {
        if (DecodeAt(strip, height) is not { } decoded)
            return null;

        var frameWidth = Math.Max((int)Math.Round(decoded.PixelHeight * (shape > 0 ? shape : 16.0 / 9)), 1);
        var count = Math.Max((int)Math.Round((double)decoded.PixelWidth / frameWidth), 1);
        frameWidth = decoded.PixelWidth / count;
        var frames = new List<BitmapSource>(count);
        for (var i = 0; i < count; i++)
            frames.Add(Cut(decoded, new Int32Rect(i * frameWidth, 0, frameWidth, decoded.PixelHeight)));

        return frames;
    }

    /// <summary>
    /// One frame of a strip as a picture standing on its own: a tile's poster. Only that frame is kept (the strip's size is
    /// read from its header, and the decode is cropped to the frame), never the whole strip decoded for it, which left a
    /// megabyte and a half of pixels per tile waiting to be collected. Frozen.
    /// </summary>
    public static BitmapSource? Poster(string strip, double shape, int height, double at)
    {
        try
        {
            int stripWidth, stripHeight;
            using (var stream = File.OpenRead(strip))
            {
                var header = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                (stripWidth, stripHeight) = (header.PixelWidth, header.PixelHeight);
            }

            // Where the frame is, in the strip's own pixels (the frames are told apart by the video's shape): the crop is
            // taken there, and what it cuts out is then decoded at the tile's height.
            var frameWidth = Math.Max((int)Math.Round(stripHeight * (shape > 0 ? shape : 16.0 / 9)), 1);
            var count = Math.Max((int)Math.Round((double)stripWidth / frameWidth), 1);
            frameWidth = Math.Max(stripWidth / count, 1);
            var index = Math.Clamp((int)(at * count), 0, count - 1);

            // No IgnoreImageCache with a stream: WPF's image cache is kept by address, and with none it throws.
            using var file = File.OpenRead(strip);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelHeight = Math.Max(height, 8);
            image.SourceRect = new Int32Rect(index * frameWidth, 0, frameWidth, stripHeight);
            image.StreamSource = file;
            image.EndInit();
            return Cut(image, new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UriFormatException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pixels of one rectangle of a picture, copied into a picture of their own whose first pixel is at (0, 0), at
    /// 96 DPI. A cut-out picture (a <see cref="CroppedBitmap"/>, or a <see cref="BitmapImage.SourceRect"/>) kept where it
    /// was cut from, and a tile scrubbed along the strip drew each frame further to the right inside its 16:9 box. Frozen.
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
