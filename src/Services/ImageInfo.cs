using System.IO;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

public static class ImageInfo
{
    /// <summary>The pixel size of a picture file, read from its header; null when it is not a readable image.</summary>
    public static (int Width, int Height)? GetSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0 ? (frame.PixelWidth, frame.PixelHeight) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or FileFormatException)
        {
            return null;
        }
    }
}
