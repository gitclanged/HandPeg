using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

// ----- Baked project thumbnails (Pass 5.2D) -----
// Every save carries a small picture of the Sequence at the playhead: the composite as the player shows it (its Cutouts, Effects,
// text and captions; the fades left out, so a project that fades in is not black at its start), drawn small on the GPU engine
// (GpuPreviewEngine.CaptureThumbnailAsync, the short side 360 pixels) and kept as a JPEG. A .hproj carries it as Base64 right after
// its header (thumbnailJpeg), a .hpb as a stored thumbnail.jpg entry beside its project. The Project Manager and the launch window
// show it, and so does Windows Explorer through HandPeg's own thumbnail handler (src\ShellThumbnails, ExplorerThumbnails), which
// reads it the same quick way as ReadFromProject and ReadFromBundle do here.

/// <summary>Makes, reads and decodes a project's baked thumbnail: a JPEG of the Sequence at the playhead as it was saved.</summary>
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

    // How much of a project file is read at most to find the thumbnail (it lies in the first hundred kilobytes or so), and the first
    // step of that: read again four times as far while the thumbnail runs past what was read.
    private const int MostBytesRead = 4 << 20;
    private const int FirstRead = 128 << 10;

    /// <summary>
    /// A thumbnail's size for a canvas: its short side <paramref name="shortSide"/> (never more than the canvas's own), the long side in
    /// the canvas's shape, both even (1080 x 1920 is 360 x 640, 1920 x 1080 is 640 x 360). At least 2 x 2.
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
    /// A BGRA8 frame (its alpha not read) as a JPEG at <see cref="JpegQuality"/>; null when it cannot be made. Works on any thread (a save
    /// encodes it on the thread pool).
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
    /// A thumbnail decoded at <paramref name="decodeHeight"/> pixels tall (its shape kept), as the media browser decodes its pictures:
    /// frozen, so any thread may show it. Null when it cannot be read.
    /// </summary>
    public static BitmapSource? Decode(byte[] jpeg, int decodeHeight)
    {
        try
        {
            // From a stream, never a URI; no IgnoreImageCache with a stream (WPF keeps its cache by address, and throws with none).
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
    /// The thumbnail a project file carries, read from its first kilobytes (at most 4 MB) without reading the rest: its top level is
    /// walked as far as the property after "writtenBy". Null when it has none, or the file cannot be read.
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
    /// The thumbnail in a project's text (or the start of it): the top level's properties walked in order, objects stepped over, until
    /// "thumbnailJpeg" (its Base64 decoded) or the first property after "writtenBy" that is not it. <paramref name="cutOff"/> says the
    /// search ran past <paramref name="bytes"/> before it could tell (read further and ask again).
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
    /// The thumbnail a project bundle carries (its stored thumbnail.jpg entry): read through the archive's directory at its end, never
    /// the media. Null when it has none, or the file cannot be read.
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
