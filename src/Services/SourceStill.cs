using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

/// <summary>One frame of the source, as it is in the file: what HUD areas and masks are measured on.</summary>
public static class SourceStill
{
    /// <summary>
    /// Renders an exact frame at native size to an image file via the thumbnail compositor (FFmpeg only as fallback) and returns it frozen.
    /// </summary>
    public static async Task<BitmapImage> TakeAsync(string source, double seconds, string pngPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
        if (!await GpuFilmstrips.TryStillAsync(source, pngPath, Math.Max(seconds, 0), null, onAKeyFrame: false, cancellationToken))
        {
            var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? $"\"{DependencyUpdater.FfmpegPath}\"" : "ffmpeg";
            var at = Math.Max(seconds, 0).ToString("0.###", CultureInfo.InvariantCulture);
            await FfmpegRunner.RunAsync($"{ffmpeg} -hide_banner -y -ss {at} -i \"{source}\" -frames:v 1 -update 1 \"{pngPath}\"",
                new Progress<FfmpegProgress>(), cancellationToken);
        }

        var still = new BitmapImage();
        still.BeginInit();
        still.CacheOption = BitmapCacheOption.OnLoad;
        still.UriSource = new Uri(pngPath);
        still.EndInit();
        still.Freeze();
        return still;
    }

    /// <summary>Deletes an unused still; locked files are left for session cleanup.</summary>
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
