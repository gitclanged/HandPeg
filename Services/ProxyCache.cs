using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Proxies: small, quick-to-decode copies of the videos opened in Editor Mode, which the player shows in
/// place of the originals so that seeking is immediate however heavy the original is. A proxy is 720 lines
/// high, encoded as fast as x264 goes, with an I-frame every 15 frames, so no moment is more than a few
/// frames' decoding from one. They are for looking at only: every export reads the original file.
///
/// They live in one folder of the cache, named after the file they stand for (its path, size and date, so a
/// file that changes gets a new one). The folder is kept under a size the user sets by deleting the proxies
/// that were used longest ago.
/// </summary>
public static class ProxyCache
{
    public static string Folder => Path.Combine(AppPaths.Cache, "proxies");

    /// <summary>Where the proxy of a file is, or would be.</summary>
    /// <param name="low">The Low tier's proxy: 360 lines at half the bitrate, for hardware that needs it.</param>
    public static string PathFor(string media, bool low = false)
    {
        var info = new FileInfo(media);
        var key = $"{info.FullName.ToLowerInvariant()}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var name = string.Concat(Path.GetFileNameWithoutExtension(media).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(40));
        // "p3": the third make of proxy (capped bitrate, no sound). Earlier makes are not taken for these.
        return Path.Combine(Folder, $"{(name.Length > 0 ? name : "video")}_{hash}_{(low ? "p3low" : "p3")}.mp4");
    }

    /// <summary>The proxy of a file, when it has been made; it then counts as used just now. Null when there is none.</summary>
    public static string? Find(string media, bool low = false)
    {
        try
        {
            var path = PathFor(media, low);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return null;

            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Brings the folder under its limit: the proxies used longest ago go first. One is never deleted: the one about to be used.</summary>
    public static void Trim(double limitGb, string? keep = null)
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;

            var limit = (long)(Math.Clamp(limitGb, 0.5, 2000) * 1024 * 1024 * 1024);
            var files = new DirectoryInfo(Folder).GetFiles("*.mp4").OrderBy(f => f.LastAccessTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= limit)
                    break;
                if (string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase))
                    continue;

                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The proxy cache could not be trimmed", ex);
        }
    }

    /// <summary>
    /// Makes the proxy of a file, with FFmpeg, in the background. Room is made for it first. It is written
    /// under another name and given its own only when it is whole, so a half-made one is never mistaken for
    /// a proxy. Returns its path, or null when it could not be made.
    /// </summary>
    /// <param name="progress">How far along it is, 0 to 100.</param>
    /// <param name="encoders">The H.264 encoders to try, best first: the hardware ones this computer has (NVENC, QuickSync, AMF), then libx264.</param>
    public static async Task<string?> GenerateAsync(
        string media, bool low, double limitGb, IReadOnlyList<string> encoders, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var path = PathFor(media, low);
        var making = Path.Combine(Folder, $"making_{Guid.NewGuid():N}.part.mp4");
        try
        {
            Directory.CreateDirectory(Folder);
            Trim(limitGb, path);

            // FFmpeg says how long the file is once, at the start, and where it has got to from then on.
            var length = 0.0;
            var report = new Progress<FfmpegProgress>(p =>
            {
                if (p.InputDuration is { TotalSeconds: > 0 } whole)
                    length = whole.TotalSeconds;
                if (p.Position is { } position && length > 0)
                    progress.Report(Math.Clamp(position.TotalSeconds / length * 100, 0, 100));
            });

            // The graphics card encodes it where one can (and decodes the original on the way); each encoder that
            // will not is followed by the next, down to libx264, which always will. Whichever does it, the
            // bitrate is capped (4 Mbit/s, or 2 for the Low tier): a proxy is to be light before it is pretty.
            // A proxy has no sound at all (-an): what is heard in the player is always the original's.
            var order = encoders.Count > 0 ? encoders.ToList() : ["libx264"];
            for (var i = 0; i < order.Count; i++)
            {
                var encoder = order[i];
                var hardware = encoder != "libx264";
                var command = $"ffmpeg -hide_banner -y {(hardware ? "-hwaccel auto " : "")}-i \"{media}\" -map 0:v:0 -an -vf \"scale=-2:'min({(low ? 360 : 720)},ih)'\" "
                              + $"{EncoderArguments(encoder, low)} -maxrate {(low ? "2M -bufsize 4M" : "4M -bufsize 8M")} -g 15 -pix_fmt yuv420p -movflags +faststart \"{making}\"";
                try
                {
                    await FfmpegRunner.RunAsync(command, report, cancellationToken);
                    AppLog.Write($"Proxy: {Path.GetFileName(media)} encoded with {encoder}.");
                    break;
                }
                catch (InvalidOperationException ex) when (i + 1 < order.Count)
                {
                    AppLog.Write($"Proxy: {encoder} would not encode {Path.GetFileName(media)}, the next encoder is tried: {ex.Message}");
                }
            }

            File.Move(making, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"The proxy of {Path.GetFileName(media)} could not be made", ex);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(making))
                    File.Delete(making);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next clearing.
            }
        }
    }

    /// <summary>
    /// Clears out what nothing will use again, on a thread of its own, when HandPeg starts: proxies that have
    /// not been used for a week (their projects are forgotten, and they would only crowd the cache's limit),
    /// timeline proxies (each is of one session's timeline, and no later session can use it), and whatever a
    /// crash or a kill left half made. Nothing waits for it.
    /// </summary>
    public static void SweepInBackground(int staleDays = 7) => _ = Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;

            var (removed, freed, cutoff) = (0, 0L, DateTime.UtcNow.AddDays(-Math.Max(staleDays, 1)));
            foreach (var file in new DirectoryInfo(Folder).GetFiles())
            {
                var leftover = file.Name.StartsWith("timeline_", StringComparison.OrdinalIgnoreCase) || file.Name.StartsWith("making_", StringComparison.OrdinalIgnoreCase)
                               || file.Name.EndsWith(".render.mp4", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase);
                var stale = file.LastAccessTimeUtc < cutoff && file.LastWriteTimeUtc < cutoff;
                if (!leftover && !stale)
                    continue;

                try
                {
                    var size = file.Length;
                    file.Delete();
                    (removed, freed) = (removed + 1, freed + size);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by another HandPeg that is running: left for the next sweep.
                }
            }

            if (removed > 0)
                AppLog.Write($"Proxy cache: swept {removed} stale or leftover file{(removed == 1 ? "" : "s")}, {freed / 1048576.0:0.0} MB freed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The proxy cache could not be swept", ex);
        }
    });

    /// <summary>Each encoder's fastest sensible setting, at a quality of about CRF 28; the cap on the bitrate is added for all of them alike.</summary>
    private static string EncoderArguments(string encoder, bool low) => encoder switch
    {
        "h264_nvenc" => "-c:v h264_nvenc -preset p1 -rc vbr -cq 28 -b:v 0",
        // QuickSync's quality mode pays no heed to a cap on the bitrate, so it is given a bitrate to aim at instead.
        "h264_qsv" => $"-c:v h264_qsv -preset veryfast -b:v {(low ? "1500k" : "3M")}",
        "h264_amf" => $"-c:v h264_amf -quality speed -rc vbr_peak -b:v {(low ? "1500k" : "3M")}",
        _ => "-c:v libx264 -preset veryfast -tune fastdecode -crf 28",
    };

    /// <summary>
    /// Empties the cache without anything waiting for it: the deleting is handed to a command prompt of its
    /// own, which outlives HandPeg if HandPeg is closing. Closing is not held up by however many gigabytes there are.
    /// </summary>
    public static void ClearInBackground()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;

            Process.Start(new ProcessStartInfo("cmd.exe", string.Create(CultureInfo.InvariantCulture, $"/c rmdir /s /q \"{Folder}\""))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            AppLog.Write("The proxy cache could not be cleared", ex);
        }
    }
}
