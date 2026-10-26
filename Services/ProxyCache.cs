using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using CliWrap;

namespace HandPegApp.Services;

/// <summary>
/// Proxies: small copies of heavy videos (4K, AV1, 10-bit HEVC, long I-frame intervals...) that Live Preview plays in
/// their place, so that seeking and playing a layer of one costs what a 540-line H.264 file costs. They are for
/// looking at only: exports always read the original files, and the sound always comes from the original.
///
/// A proxy is made in the background (see WorkScheduler) on the background graphics card where there is one, after
/// HandPeg has been left alone for a while, one at a time; written beside its place and moved there when whole. It
/// is kept in the proxy cache (PreviewCache.Proxies, or a folder beside the project: Settings), named after the file
/// it stands for (its path, size and date), its height and its codec, and listed in that cache's manifest.
///
/// The composed timeline proxy (the whole sequence as one video) is made here too: see <see cref="MakeTimelineAsync"/>.
/// </summary>
public static class ProxyCache
{
    // Proxy caches beside projects, by folder.
    private static readonly ConcurrentDictionary<string, PreviewCache> ProjectStores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cache proxies are kept in: the proxy cache, or a folder beside the project (Name.proxies) when Settings says so and there is a project.</summary>
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
    /// What a proxy is kept under in its cache: its height and codec, and "t2" since its frames keep the file's own time
    /// stamps ("proxy540h264t2", <see cref="FrameTiming"/>). A proxy of another height or codec, or one made before, is another proxy.
    /// </summary>
    public static string Kind => string.Create(CultureInfo.InvariantCulture, $"proxy{Tier}{(Mjpeg ? "mjpeg" : "h264")}t2");

    private static string Extension => Mjpeg ? ".mkv" : ".mp4";

    /// <summary>The proxy of a file, when it has been made; null when it has not.</summary>
    public static string? Find(string media, string? projectPath) => Store(projectPath).Find(media, Kind, Extension);

    // ----- Which files want one -----

    /// <summary>
    /// Whether a video is heavy enough to want a proxy: larger than Settings says (its shorter side over 1080 by default,
    /// so a vertical 1080x1920 clip is 1080p as a landscape one is), in a codec that is
    /// slow to decode in software (AV1, VP9, ProRes, 10-bit HEVC), or with I-frames further apart than Settings says
    /// (2 s by default: every seek decodes that far). <paramref name="iFrameSeconds"/> is the measured interval, when known.
    /// </summary>
    public static bool IsHeavy(MediaInfo info, double? iFrameSeconds)
    {
        if (info.Video is not { } video)
            return false;

        var settings = AppSettings.Current;
        var tenBit = video.PixelFormat.Contains("10", StringComparison.Ordinal) || video.PixelFormat.Contains("12", StringComparison.Ordinal);
        return Math.Min(video.Width, video.Height) > Math.Max(settings.ProxyHeavyHeight, 240)
               || video.Codec is "av1" or "vp9" or "prores"
               || (video.Codec == "hevc" && tenBit)
               || iFrameSeconds > Math.Max(settings.ProxyHeavyIFrameSeconds, 0.5);
    }

    // The I-frame interval of each file looked at this session.
    private static readonly ConcurrentDictionary<string, double> IFrameIntervals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How far apart a file's I-frames are, on average, over its first thirty seconds: from the packets' flags,
    /// nothing decoded. Null when it could not be read.
    /// </summary>
    public static async Task<double?> IFrameIntervalAsync(string media, CancellationToken token)
    {
        if (IFrameIntervals.TryGetValue(media, out var known))
            return known;
        if (!File.Exists(DependencyUpdater.FfprobePath))
            return null;

        var times = new List<double>();
        await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfprobePath)
            .WithArguments(["-v", "error", "-select_streams", "v:0", "-read_intervals", "%+30", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", "-i", media])
            .WithStandardOutputPipe(ProcessPipes.Lines(line =>
            {
                var comma = line.IndexOf(',');
                if (comma > 0 && comma + 1 < line.Length && line[comma + 1] == 'K'
                    && double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    times.Add(seconds);
                }
            }))
            .WithValidation(CommandResultValidation.None), token);

        if (times.Count < 2)
            return null;

        times.Sort();
        var interval = (times[^1] - times[0]) / (times.Count - 1);
        IFrameIntervals[media] = interval;
        return interval;
    }

    // ----- Making them -----

    /// <summary>
    /// The H.264 encoders to try for a proxy, best first: as Settings says, or (Auto) the background card's, then the
    /// main card's, then the processor's. Only those the encoder test found working; libx264 always last.
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
    /// What a proxy keeps of the file's frames, and when: every frame whose time stamp is later than the last one kept,
    /// at exactly its own time stamp (passed through, on a 1/90000 clock that holds milliseconds and 1/30000 alike).
    /// Some downloads have runs of frames sharing one time stamp (72 at 511.752 s in a YouTube webm, the stream going
    /// on 14 ms later): the default constant-rate output put them in the slots that followed and, within its tolerance
    /// of 1.1 frames, never caught up, so every later frame of the proxy was a frame off the original's (Pass 4.1B).
    /// </summary>
    private const string FrameTiming = "select='isnan(prev_selected_t)+gt(t,prev_selected_t)',";
    private const string FrameTimingOutput = "-fps_mode passthrough -enc_time_base 1/90000";

    /// <summary>What follows the picture in a proxy's command: the encoder at its fastest sensible setting, an I-frame every 15 frames and no B-frames.</summary>
    private static string EncodeArguments(string encoder) => Mjpeg ? "-c:v mjpeg -q:v 5 -pix_fmt yuvj420p" : encoder switch
    {
        "h264_nvenc" => "-c:v h264_nvenc -preset p1 -rc vbr -cq 28 -b:v 0 -maxrate 4M -bufsize 8M -g 15 -bf 0 -pix_fmt yuv420p",
        // QuickSync's quality mode pays no heed to a cap on the bitrate, so it is given a bitrate to aim at instead.
        "h264_qsv" => "-c:v h264_qsv -preset veryfast -b:v 3M -maxrate 4M -bufsize 8M -g 15 -bf 0 -pix_fmt nv12",
        "h264_amf" => "-c:v h264_amf -quality speed -rc vbr_peak -b:v 3M -maxrate 4M -bufsize 8M -g 15 -bf 0 -pix_fmt nv12",
        _ => "-c:v libx264 -preset veryfast -tune fastdecode -crf 28 -maxrate 4M -bufsize 8M -g 15 -bf 0 -pix_fmt yuv420p",
    };

    /// <summary>
    /// Makes the proxy of a file: silent, <see cref="Tier"/> lines high (never larger than the original), decoded on
    /// the background card where there is one. Each encoder that will not do it is followed by the next, down to the
    /// processor. Returns its path, or null when it could not be made. Called inside a piece of background work.
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
                    AppLog.Write($"Proxy: {encoder} would not make the proxy of {Path.GetFileName(media)}, the next encoder is tried: {ex.Message}");
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

    /// <summary>
    /// Makes the composed timeline proxy: the whole sequence, as Render Preview would make it (the export's own
    /// graph, from the original files, without captions or sound), at the proxy height, by the proxies' encoders.
    /// Kept in the proxy cache under the timeline's fingerprint, against the main video's file.
    /// </summary>
    /// <param name="previewCommand">Makes the Render Preview command for an output file and a scale in percent.</param>
    public static async Task<string?> MakeTimelineAsync(string mainMedia, string fingerprint, int percent, Func<string, int, string, string> previewCommand,
        IReadOnlyCollection<string> workingEncoders, double seconds, string? projectPath, WorkContext work)
    {
        var store = Store(projectPath);
        var kind = $"timeline{fingerprint}";
        if (store.Find(mainMedia, kind, ".mp4") is { } ready)
            return ready;
        if (store.PrepareWrite(mainMedia, kind, ".mp4") is not { } path)
            return null;

        var making = path + ".part.mp4";
        var report = new Progress<FfmpegProgress>(p =>
        {
            if (p.Position is { } position && seconds > 0)
                work.Report(Math.Clamp(position.TotalSeconds / seconds, 0, 1));
        });

        try
        {
            var encoders = Encoders(workingEncoders);
            for (var i = 0; i < encoders.Count; i++)
            {
                try
                {
                    await FfmpegRunner.RunAsync(previewCommand(making, percent, EncodeArguments(encoders[i])), report, work.Token, graphInFile: true);
                    break;
                }
                catch (InvalidOperationException ex) when (ex is not FfmpegCommandException && i + 1 < encoders.Count)
                {
                    AppLog.Write($"Timeline proxy: {encoders[i]} would not make it, the next encoder is tried: {ex.Message}");
                }
            }

            return store.Keep(mainMedia, kind, ".mp4", making);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The timeline proxy could not be made", ex);
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
