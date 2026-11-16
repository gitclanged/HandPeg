using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

/// <summary>
/// Media Browser imagery: a per-video filmstrip (default 15 frames, 480 px tall) generated once (GPU, else FFmpeg; identical layout,
/// <see cref="FfmpegRunner.GenerateFilmstripAsync"/>), cached in <see cref="PreviewCache.Browser"/>, and sliced into tile-sized frames.
/// Probe data (duration, size) is cached too so reopened folders need no FFmpeg. With caching off, data lasts for the session only.
/// </summary>
public static class MediaBrowserFrames
{
    // With the cache off: what was made this session, by file and kind, so a folder opened twice is not made twice.
    private static readonly ConcurrentDictionary<string, string> SessionFiles = new(StringComparer.OrdinalIgnoreCase);

    private static string SessionFolder => Path.Combine(SessionPaths.Root, "browser");

    /// <summary>Video duration and size; null when unreadable.</summary>
    /// <param name="lane">Background for visible tiles; the folder indexer calls it inside its own Idle work.</param>
    /// <remarks>
    /// Like <see cref="StripAsync"/> and <see cref="PosterOnlyAsync"/>, cache I/O and tool launches run on the thread pool, never on the
    /// calling UI thread (previously tens of ms per call during drag-and-drop).
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

    /// <summary>
    /// File details for List columns and the Filmstrip subtitle: duration, size, frame rate, codec, audio stream count; null when unreadable.
    /// Cached as a separate "details" kind from the tiles' "probe". Runs on the pool like <see cref="DescribeAsync"/>.
    /// </summary>
    public static Task<MediaDetails?> DetailsAsync(string path, CancellationToken token, WorkLane lane = WorkLane.Background) =>
        Task.Run<MediaDetails?>(async () =>
        {
            if (ReadKept(path, "details", ".txt") is { } kept && ParseDetails(kept) is { } known)
                return known;

            var info = await MediaProbe.ProbeAsync(path, token, lane);
            if (info is null)
                return null;

            var details = new MediaDetails(Math.Max(info.DurationSeconds, 0), info.Video?.Width ?? 0, info.Video?.Height ?? 0, info.Video?.FrameRate ?? 0,
                info.Video?.Codec ?? info.Audio.FirstOrDefault()?.Codec ?? "", info.Audio.Count);
            WriteKept(path, "details", ".txt", FormatDetails(details));
            return details;
        }, token);

    /// <summary>"details" cache format: "1|seconds|width|height|fps|codec|audio streams" (invariant culture; codecs never contain "|").</summary>
    internal static string FormatDetails(MediaDetails details) => string.Create(CultureInfo.InvariantCulture,
        $"1|{details.Seconds:0.###}|{details.Width}|{details.Height}|{details.FrameRate:0.####}|{details.Codec.Replace('|', '_')}|{details.AudioStreams}");

    /// <summary>The details from their kept text; null for text of another version or shape.</summary>
    internal static MediaDetails? ParseDetails(string text)
    {
        var parts = text.Trim().Split('|');
        return parts.Length == 7 && parts[0] == "1"
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
               && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
               && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)
               && int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var audio)
            ? new MediaDetails(seconds, width, height, fps, parts[5], audio)
            : null;
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
    /// Tile from cache only (description, strip, derived poster) with no tool run or queue wait; null if either is missing. Off the UI thread.
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

    /// <summary>
    /// Cached browser strip decoded at <paramref name="height"/> with integer frame widths, reused as a Timeline block strip for files dropped
    /// from the browser (no tool run). Null when not cached. Off the UI thread. Frozen.
    /// </summary>
    public static BitmapSource? KeptStripAt(string path, int height)
    {
        try
        {
            if (ReadKept(path, "probe", ".txt") is not { } text || ParseProbe(text) is not { Width: > 0, Height: > 0 } video)
                return null;

            var settings = AppSettings.Current;
            var shape = (double)video.Width / video.Height;
            var kind = StripKind(Math.Clamp(settings.BrowserPreviewFrames, 2, 40), Math.Clamp(settings.BrowserPreviewHeight, 90, 1080), shape);
            if (FindKept(path, kind, ".jpg") is not { } strip || Layout(strip, shape) is not { } layout)
                return null;

            return Decode(strip, FrameWidthAt(layout, height) * layout.Count, height, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    // The cache's name for a strip of so many frames, at the height it is made (lowered for a strip too wide for a JPEG).
    private static string StripKind(int frames, int frameHeight, double shape) =>
        string.Create(CultureInfo.InvariantCulture, $"frames{frames}h{(int)Math.Min(frameHeight, 60000 / (frames * shape))}");

    /// <summary>
    /// Returns the cached strip or generates it; null on failure. Frame height is reduced if the strip would exceed JPEG's 65,535 px width.
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

    /// <summary>Minimum standalone poster height; taller tiles use multiples of it.</summary>
    public const int PosterOnlyHeight = 144;

    /// <summary>
    /// First-pass poster before the strip: one frame at 30 % of duration (1 s if unknown), at least <see cref="PosterOnlyHeight"/> px,
    /// cached as <c>posterh144.jpg</c> (or taller). Returns the JPEG path; null on failure.
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
    /// Tile from cached description and standalone poster while the strip is pending (second pass follows); null if either is missing. Off the UI thread.
    /// </summary>
    public static (double Seconds, int Width, int Height, BitmapSource? Poster)? KeptPosterOnly(string path, int height)
    {
        if (ReadKept(path, "probe", ".txt") is not { } text || ParseProbe(text) is not { Width: > 0, Height: > 0 } video)
            return null;

        if (FindKept(path, string.Create(CultureInfo.InvariantCulture, $"posterh{PosterHeightFor(height)}"), ".jpg") is not { } poster)
            return null;

        return (video.Seconds, video.Width, video.Height, DecodeAt(poster, height));
    }

    // Poster height for a tile height, quantized to PosterOnlyHeight steps so few sizes are cached.
    private static int PosterHeightFor(int height) =>
        Math.Max(PosterOnlyHeight, (height + PosterOnlyHeight - 1) / PosterOnlyHeight * PosterOnlyHeight);

    /// <summary>
    /// Slices a strip into equal-size frames at the given height, decoding at reduced JPEG scale for tiles. Frozen.
    /// </summary>
    /// <remarks>
    /// Decodes once at tile height and a width of N x integer frame width so each frame is cut exactly at its start. Decoding by height
    /// alone yields fractional frame widths whose cumulative rounding error shifts later frames (22 px by frame 25), visibly sliding the
    /// hover picture.
    /// </remarks>
    public static IReadOnlyList<BitmapSource>? Frames(string strip, double shape, int height)
    {
        try
        {
            if (Layout(strip, shape) is not { } layout)
                return null;

            var frameHeight = Math.Max(height, 8);
            var decoded = Decode(strip, FrameWidthAt(layout, frameHeight) * layout.Count, frameHeight, null);

            // Frame offsets: integer multiples of the frame width, or proportional (non-accumulating) when the decoder returned another width.
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
    /// Single strip frame as a tile poster, same size as <see cref="Frames"/> output. Reads the strip size from the header and decodes only
    /// the cropped frame region before scaling (avoids ~1.5 MB of garbage per tile). Frozen.
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
    /// Strip layout from the header only (size and frame count). Frames share one even width (scale=-2; GpuFilmstrips.FrameWidth), so the
    /// count is the divisor of the strip width nearest the probed aspect estimate (which may be a pixel or two off).
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

    // Frame start in strip pixels: index x frame width (one past the last gives the strip end).
    private static int FrameStart((int Width, int Height, int Count) layout, int index) =>
        (int)Math.Round(index * (double)layout.Width / layout.Count);

    // How wide one frame is drawn at a height: its shape in the strip, to a whole pixel.
    private static int FrameWidthAt((int Width, int Height, int Count) layout, int height) =>
        Math.Max((int)Math.Round((double)height * layout.Width / layout.Count / layout.Height), 1);

    // Decodes a JPEG stream at an exact size (both dimensions set to avoid WPF rounding), optionally cropping in source pixels before
    // scaling. No IgnoreImageCache with streams: WPF's cache is URI-keyed and throws without one.
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
    /// Copies a rectangle into a new 96-DPI bitmap (ignoring JPEG DPI) so WPF sizes all frames consistently. Frozen.
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

    /// <summary>Decodes an image file at the given height as its poster. Frozen; null when unreadable.</summary>
    public static BitmapSource? Picture(string path, int height) => DecodeAt(path, height);

    // Use a file stream, not a URI (URIs go through WPF's web request path: URL monikers, security zones). No IgnoreImageCache with streams.
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

/// <summary>
/// List view file details: duration (s), video size (0 for audio), frame rate, FFmpeg codec name (video, else first audio), audio stream count.
/// </summary>
public sealed record MediaDetails(double Seconds, int Width, int Height, double FrameRate, string Codec, int AudioStreams);
