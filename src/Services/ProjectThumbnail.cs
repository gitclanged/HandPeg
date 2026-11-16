using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

// ----- Baked project thumbnails -----
// Each save embeds a JPEG of the Sequence composite at the playhead (Cutouts, Effects, text, captions; fades excluded so fade-ins are
// not black), rendered by GpuPreviewEngine.CaptureThumbnailAsync at a 360 px short side. .hproj stores it as Base64 after the header
// (thumbnailJpeg); .hpb as a stored thumbnail.jpg entry. Shown by Project Manager, the launch window and Explorer (src\ShellThumbnails,
// ExplorerThumbnails), which read it as quickly as ReadFromProject / ReadFromBundle.

/// <summary>Creates, reads and decodes a project's baked playhead thumbnail (JPEG).</summary>
public static class ProjectThumbnail
{
    /// <summary>The short side of a thumbnail, pixels: 360 x 640 for a 9:16 Sequence, 640 x 360 for a 16:9 one.</summary>
    public const int ShortSide = 360;

    /// <summary>The JPEG's quality: a 640 x 360 picture comes to some tens of kilobytes.</summary>
    public const int JpegQuality = 85;

    /// <summary>The thumbnail's entry in a project bundle (ProjectBundle), stored beside its project.hproj.</summary>
    public const string BundleEntry = "thumbnail.jpg";

    // The project file's property (ProjectFile.ThumbnailJpeg as written: camel case), and the header property it follows.
    private static ReadOnlySpan<byte> PropertyName => "thumbnailJpeg"u8;
    private static ReadOnlySpan<byte> WriterProperty => "writtenBy"u8;

    // Max bytes scanned for the thumbnail (usually within ~100 KB) and the initial read, growing 4x while the thumbnail is truncated.
    private const int MostBytesRead = 4 << 20;
    private const int FirstRead = 128 << 10;

    /// <summary>
    /// Thumbnail size: short side <paramref name="shortSide"/> (not above the canvas), long side by aspect, both even (1920 x 1080 to
    /// 640 x 360); minimum 2 x 2.
    /// </summary>
    public static (int Width, int Height) ThumbnailSize(int canvasWidth, int canvasHeight, int shortSide)
    {
        var (width, height) = (Math.Max(canvasWidth, 2), Math.Max(canvasHeight, 2));
        var shortest = Math.Min(width, height);
        var longest = Math.Max(width, height);
        var small = Math.Max(Math.Min(shortSide, shortest) & ~1, 2);
        var large = Math.Max((int)Math.Round((double)longest * small / shortest / 2, MidpointRounding.AwayFromZero) * 2, 2);
        return width <= height ? (small, large) : (large, small);
    }

    /// <summary>
    /// Encodes a BGRA8 frame (alpha ignored) as JPEG at <see cref="JpegQuality"/>; null on failure. Thread-safe (saves run it on the pool).
    /// </summary>
    public static byte[]? EncodeJpeg(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
            return null;

        try
        {
            var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
            picture.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(picture));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            AppLog.Write("Project thumbnail: the picture could not be made a JPEG", ex);
            return null;
        }
    }

    /// <summary>
    /// Decodes at <paramref name="decodeHeight"/> px (aspect kept), frozen for cross-thread use; null when unreadable.
    /// </summary>
    public static BitmapSource? Decode(byte[] jpeg, int decodeHeight)
    {
        try
        {
            // Stream source, never a URI; no IgnoreImageCache with streams (WPF's cache is URI-keyed and throws without one).
            using var stream = new MemoryStream(jpeg, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelHeight = Math.Max(decodeHeight, 8);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a project's thumbnail from its head (max 4 MB), walking top-level properties up to the one after "writtenBy". Null when absent
    /// or unreadable.
    /// </summary>
    public static byte[]? ReadFromProject(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length <= 0)
                return null;

            var buffer = new byte[(int)Math.Min(FirstRead, Math.Min(length, MostBytesRead))];
            var read = 0;
            while (true)
            {
                read += stream.ReadAtLeast(buffer.AsSpan(read), buffer.Length - read, throwOnEndOfStream: false);

                // The end of the file reached (or the file shorter than it said): the text is whole.
                var whole = read < buffer.Length || read >= length;
                var jpeg = ReadFromJson(buffer.AsSpan(0, read), whole, out var cutOff);
                if (jpeg is not null || !cutOff || whole || buffer.Length >= MostBytesRead)
                    return jpeg;

                Array.Resize(ref buffer, (int)Math.Min(Math.Min((long)buffer.Length * 4, MostBytesRead), length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Scans top-level properties (skipping objects) for "thumbnailJpeg" (Base64-decoded), stopping at the first other property after
    /// "writtenBy". <paramref name="cutOff"/> is set when <paramref name="bytes"/> ended first (read more and retry).
    /// </summary>
    internal static byte[]? ReadFromJson(ReadOnlySpan<byte> bytes, bool isFinalBlock, out bool cutOff)
    {
        cutOff = false;
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            bytes = bytes[3..];

        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock, state: default);
            if (!reader.Read())
            {
                cutOff = !isFinalBlock;
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
                return null;

            var pastWriter = false;
            while (true)
            {
                if (!reader.Read())
                {
                    cutOff = !isFinalBlock;
                    return null;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                    return null;

                var isThumbnail = reader.ValueTextEquals(PropertyName);
                if (pastWriter && !isThumbnail)
                    return null;

                var isWriter = reader.ValueTextEquals(WriterProperty);
                if (!reader.Read())
                {
                    cutOff = !isFinalBlock;
                    return null;
                }

                if (isThumbnail)
                    return reader.TokenType == JsonTokenType.String && reader.TryGetBytesFromBase64(out var jpeg) && jpeg.Length > 0 ? jpeg : null;

                if ((reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) && !reader.TrySkip())
                {
                    cutOff = !isFinalBlock;
                    return null;
                }

                pastWriter |= isWriter;
            }
        }
        catch (JsonException)
        {
            // Not JSON, or cut off in the middle of a value in a file read whole: no thumbnail.
            return null;
        }
    }

    /// <summary>
    /// Reads a bundle's stored thumbnail.jpg via the ZIP central directory (never the media). Null when absent or unreadable.
    /// </summary>
    public static byte[]? ReadFromBundle(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry(BundleEntry) is not { } entry || entry.Length is <= 0 or > MostBytesRead)
                return null;

            var jpeg = new byte[entry.Length];
            using var stream = entry.Open();
            stream.ReadExactly(jpeg);
            return jpeg;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
