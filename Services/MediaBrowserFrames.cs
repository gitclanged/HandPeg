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
    public static async Task<(double Seconds, int Width, int Height)?> DescribeAsync(string path, CancellationToken token)
    {
        if (ReadKept(path, "probe", ".txt") is { } kept && Parse(kept) is { } known)
            return known;

        var info = await MediaProbe.ProbeAsync(path, token);
        if (info is null)
            return null;

        var result = (Math.Max(info.DurationSeconds, 0), info.Video?.Width ?? 0, info.Video?.Height ?? 0);
        WriteKept(path, "probe", ".txt", string.Create(CultureInfo.InvariantCulture, $"{result.Item1:0.###}|{result.Item2}|{result.Item3}"));
        return result;

        static (double, int, int)? Parse(string text)
        {
            var parts = text.Split('|');
            return parts.Length == 3
                   && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                   && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                   && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                ? (seconds, width, height)
                : null;
        }
    }

    /// <summary>
    /// The strip of a video's frames: kept, or made now. Null when it could not be made. Its frames are as tall as
    /// asked, unless that would make a strip too wide for a JPEG (65,535 pixels), when they are made smaller.
    /// </summary>
    public static async Task<string?> StripAsync(string path, double seconds, int width, int height, int frames, int frameHeight, CancellationToken token)
    {
        var shape = width > 0 && height > 0 ? (double)width / height : 16.0 / 9;
        frameHeight = (int)Math.Min(frameHeight, 60000 / (frames * shape));
        var kind = string.Create(CultureInfo.InvariantCulture, $"frames{frames}h{frameHeight}");
        if (FindKept(path, kind, ".jpg") is { } kept)
            return kept;

        var made = Path.Combine(SessionFolder, $"{Guid.NewGuid():N}.jpg");
        if (!await FfmpegRunner.GenerateFilmstripAsync(path, made, frames, seconds, token, frameHeight))
            return null;

        if (PreviewCache.Browser.IsEnabled)
            return PreviewCache.Browser.Keep(path, kind, ".jpg", made);

        SessionFiles[Key(path, kind)] = made;
        return made;
    }

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
        {
            var frame = new CroppedBitmap(decoded, new Int32Rect(i * frameWidth, 0, frameWidth, decoded.PixelHeight));
            frame.Freeze();
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>One frame of a strip as a picture standing on its own (the strip is not held): a tile's poster. Frozen.</summary>
    public static BitmapSource? Poster(string strip, double shape, int height, double at)
    {
        if (Frames(strip, shape, height) is not { Count: > 0 } frames)
            return null;

        var copy = new WriteableBitmap(frames[Math.Clamp((int)(at * frames.Count), 0, frames.Count - 1)]);
        copy.Freeze();
        return copy;
    }

    /// <summary>A picture file, decoded at the given height: an image's poster. Frozen; null when it cannot be read.</summary>
    public static BitmapSource? Picture(string path, int height) => DecodeAt(path, height);

    private static BitmapSource? DecodeAt(string path, int height)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelHeight = Math.Max(height, 8);
            image.UriSource = new Uri(path);
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
