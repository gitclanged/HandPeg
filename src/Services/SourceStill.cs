using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

/// <summary>One frame of the source, as it is in the file: what HUD areas and masks are measured on.</summary>
public static class SourceStill
{
    /// <summary>Writes the frame at a moment of a video to a picture file and reads it back, frozen for any thread.</summary>
    public static async Task<BitmapImage> TakeAsync(string source, double seconds, string pngPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? $"\"{DependencyUpdater.FfmpegPath}\"" : "ffmpeg";
        var at = Math.Max(seconds, 0).ToString("0.###", CultureInfo.InvariantCulture);
        await FfmpegRunner.RunAsync($"{ffmpeg} -hide_banner -y -ss {at} -i \"{source}\" -frames:v 1 -update 1 \"{pngPath}\"",
            new Progress<FfmpegProgress>(), cancellationToken);

        var still = new BitmapImage();
        still.BeginInit();
        still.CacheOption = BitmapCacheOption.OnLoad;
        still.UriSource = new Uri(pngPath);
        still.EndInit();
        still.Freeze();
        return still;
    }

    /// <summary>Deletes a still that is no longer shown; one that cannot be deleted is left for the session's clean-up.</summary>
    public static void Delete(string pngPath)
    {
        try
        {
            File.Delete(pngPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
