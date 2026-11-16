using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Preview proxies: short-GOP 540p H.264 stand-ins for long-GOP videos the GPU preview cannot decode fast (no hardware decoder, CPU
/// preview), making seek/scrub cheap. Preview only: exports and audio always use the originals.
///
/// Created on demand (clip context menu "Optimize Long-GOP Clip for Scrubbing", shown when MainViewModel.OptimizeIsUseful), as serial
/// background work (WorkScheduler) on the background adapter, written beside its target then moved. Stored in PreviewCache.Proxies (or
/// Name.proxies beside the project, per Settings), keyed by source path/size/date, height and codec, and listed in the manifest.
/// </summary>
public static class ProxyCache
{
    // Proxy caches beside projects, by folder.
    private static readonly ConcurrentDictionary<string, PreviewCache> ProjectStores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Proxy cache location: the proxy cache, or Name.proxies beside the project when enabled and a project exists.</summary>
    public static PreviewCache Store(string? projectPath)
    {
        if (AppSettings.Current.ProxyLocation != AppSettings.ProxiesBesideProject || string.IsNullOrWhiteSpace(projectPath))
            return PreviewCache.Proxies;

        var folder = Path.Combine(Path.GetDirectoryName(projectPath) ?? "", Path.GetFileNameWithoutExtension(projectPath) + ".proxies");
        return ProjectStores.GetOrAdd(folder, PreviewCache.ProjectProxies);
    }

    private static bool Mjpeg => AppSettings.Current.ProxyCodec == AppSettings.ProxyCodecMjpeg;

    private static int Tier => AppSettings.ProxyTiers.Contains(AppSettings.Current.ProxyTier) ? AppSettings.Current.ProxyTier : 540;

    /// <summary>
    /// Cache kind: height, codec and timing revision "t2" (source timestamps preserved, <see cref="FrameTiming"/>), e.g. "proxy540h264t2".
    /// Different height, codec or revision means a distinct proxy.
    /// </summary>
    public static string Kind => string.Create(CultureInfo.InvariantCulture, $"proxy{Tier}{(Mjpeg ? "mjpeg" : "h264")}t2");

    private static string Extension => Mjpeg ? ".mkv" : ".mp4";

    /// <summary>The proxy of a file, when it has been made; null when it has not.</summary>
    public static string? Find(string media, string? projectPath) => Store(projectPath).Find(media, Kind, Extension);

    // ----- Making them -----

    /// <summary>
    /// H.264 encoder fallback order: Settings choice, or (Auto) background adapter, main adapter, then CPU; only probe-verified encoders,
    /// libx264 always last.
    /// </summary>
    public static List<string> Encoders(IReadOnlyCollection<string> working)
    {
        static string? For(GpuAdapter? card) => card is null ? null : card.IsNvidia ? "h264_nvenc" : card.IsIntel ? "h264_qsv" : card.IsAmd ? "h264_amf" : null;
        var order = AppSettings.Current.ProxyEncoder switch
        {
            "NVENC" => ["h264_nvenc"],
            "Quick Sync" => ["h264_qsv"],
            "AMF" => ["h264_amf"],
            AppSettings.ProxyEncoderProcessor => [],
            _ => new[] { For(GpuAdapters.BackgroundAdapter), For(GpuAdapters.All.FirstOrDefault()) }.OfType<string>().ToList(),
        };

        return [.. order.Distinct().Where(working.Contains), "libx264"];
    }

    /// <summary>
    /// Frame timing: keeps each frame with a strictly increasing timestamp, passed through on a 1/90000 timebase (exact for ms and
    /// 1/30000). Some downloads contain runs of duplicate timestamps; constant-rate output shifted them into later slots and never caught
    /// up, offsetting every subsequent proxy frame by one.
    /// </summary>
    private const string FrameTiming = "select='isnan(prev_selected_t)+gt(t,prev_selected_t)',";
    private const string FrameTimingOutput = "-fps_mode passthrough -enc_time_base 1/90000";

    /// <summary>Encoder arguments: fastest sensible preset, GOP 15, no B-frames.</summary>
    private static string EncodeArguments(string encoder) => Mjpeg ? "-c:v mjpeg -q:v 5 -pix_fmt yuvj420p" : encoder switch
    {
        "h264_nvenc" => "-c:v h264_nvenc -preset p1 -rc vbr -cq 28 -b:v 0 -maxrate 4M -bufsize 8M -g 15 -bf 0 -pix_fmt yuv420p",
        // QuickSync's quality mode pays no heed to a cap on the bitrate, so it is given a bitrate to aim at instead.
        "h264_qsv" => "-c:v h264_qsv -preset veryfast -b:v 3M -maxrate 4M -bufsize 8M -g 15 -bf 0 -pix_fmt nv12",
        "h264_amf" => "-c:v h264_amf -quality speed -rc vbr_peak -b:v 3M -maxrate 4M -bufsize 8M -g 15 -bf 0 -pix_fmt nv12",
        _ => "-c:v libx264 -preset veryfast -tune fastdecode -crf 28 -maxrate 4M -bufsize 8M -g 15 -bf 0 -pix_fmt yuv420p",
    };

    /// <summary>
    /// Builds a silent proxy at <see cref="Tier"/> lines (never upscaled), decoding on the background adapter and falling back through
    /// encoders to CPU. Returns the path or null. Runs inside background work.
    /// </summary>
    public static async Task<string?> MakeAsync(string media, string? projectPath, IReadOnlyCollection<string> workingEncoders, WorkContext work)
    {
        var store = Store(projectPath);
        if (store.Find(media, Kind, Extension) is { } ready)
            return ready;
        if (store.PrepareWrite(media, Kind, Extension) is not { } path)
            return null;

        var making = path + ".part" + Extension;
        try
        {
            var length = 0.0;
            var report = new Progress<FfmpegProgress>(p =>
            {
                if (p.InputDuration is { TotalSeconds: > 0 } whole)
                    length = whole.TotalSeconds;
                if (p.Position is { } position && length > 0)
                    work.Report(Math.Clamp(position.TotalSeconds / length, 0, 1));
            });

            var encoders = Mjpeg ? ["mjpeg"] : Encoders(workingEncoders);
            for (var i = 0; i < encoders.Count; i++)
            {
                var encoder = encoders[i];
                var decode = string.Join(" ", GpuAdapters.BackgroundDecodeArguments);
                var threads = string.Join(" ", GpuAdapters.BackgroundThreadArguments);
                var command = $"ffmpeg -hide_banner -y {threads} {decode} -i \"{media}\" -map 0:v:0 -an -sn -dn "
                              + $"-vf \"{FrameTiming}scale=-2:'min({Tier},ih)'\" {FrameTimingOutput} {EncodeArguments(encoder)} {(Mjpeg ? "" : "-movflags +faststart ")}\"{making}\"";
                try
                {
                    work.Report($"Encoding with {encoder}");
                    await FfmpegRunner.RunAsync(command, report, work.Token);
                    AppLog.Write($"Proxy: {Path.GetFileName(media)} made with {encoder} ({Kind}).");
                    break;
                }
                catch (InvalidOperationException ex) when (i + 1 < encoders.Count)
                {
                    AppLog.Write($"Proxy: {encoder} would not make the proxy of {Path.GetFileName(media)}; the next encoder is tried", ex);
                }
            }

            return store.Keep(media, Kind, Extension, making);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"The proxy of {Path.GetFileName(media)} could not be made", ex);
            return null;
        }
        finally
        {
            TryDelete(making);
        }
    }

    /// <summary>Deletes the proxies of the given files (a project being closed, when Settings says so).</summary>
    public static void Forget(IEnumerable<string> media, string? projectPath)
    {
        var store = Store(projectPath);
        var files = new HashSet<string>(media, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in store.ReadManifest().Where(e => files.Contains(e.Source) && e.Kind.StartsWith("proxy", StringComparison.Ordinal)))
            TryDelete(Path.Combine(store.Folder, entry.File));
        store.QueueTrim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            FileState.Forget(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next trim.
        }
    }
}
