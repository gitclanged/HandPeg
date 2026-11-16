using System.IO;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.Views;

// ----- Pre-baking Timeline assets for likely drops -----
// For a folder being indexed (IndexFolderAsync, after visible posters) and the watched capture folder (AppSettings.WatchedCaptureFolder,
// shortly after start-up), each video's Timeline work is done ahead on the Idle lane (Priority 3): the shared N-frame strip (used
// directly by Timeline filmstrips and hover, MediaBrowserFrames), per-stream waveform peaks (single file pass, Waveforms.BakeAsync) and
// seek facts (seekindex.bin, MediaProbe). Cached items are never rebuilt. Output goes to disk caches only; no pre-baked image, peaks or
// strip stays in memory (the Timeline reads them back when the file is shown).
public partial class MediaBrowserPane
{
    /// <summary>Bakes one video's missing waveform peaks and seek facts on the Idle lane.</summary>
    internal static async Task PrebakeTimelineAsync(string path, CancellationToken token)
    {
        var first = new WaveformRequest([0], 16, 16, "gray");
        if (MediaProbe.Recall(path) && PreviewCache.Timeline.Find(path, first.CacheKind, ".peaks") is not null)
            return;

        if (await MediaProbe.ProbeAsync(path, token, WorkLane.Idle) is not { } info)
            return;

        // Seek facts are persisted once both the probe and I-frames are known (MediaProbe.KeepSeekFacts).
        if (info.Video is not null)
            await FfmpegRunner.GetIFramesAsync(path, token);

        var missing = Enumerable.Range(0, info.Audio.Count).Select(i => new WaveformRequest([i], 16, 16, "gray"))
            .Where(r => PreviewCache.Timeline.Find(path, r.CacheKind, ".peaks") is null).ToList();
        if (missing.Count > 0)
            await Waveforms.BakeAsync(path, missing, token);
    }

    /// <summary>
    /// Pre-bakes the watched capture folder's videos at idle (strips, waveforms, seek facts), newest first, capped at Settings' Most Videos
    /// per Folder. Skipped on battery or network drives unless enabled in Settings, as for a browsed folder.
    /// </summary>
    public static async Task PrepareWatchedFolderAsync(CancellationToken token)
    {
        var settings = AppSettings.Current;
        var folder = settings.WatchedCaptureFolder.Trim().Trim('"');
        if (folder.Length == 0 || (!settings.BrowserIndexOnBattery && PowerStatus.OnBattery))
            return;

        try
        {
            if (!settings.BrowserIndexNetwork && await Task.Run(() => PowerStatus.IsNetworkFolder(folder), token))
                return;

            // Skip files written within the last minute: likely an in-progress recording.
            var settled = DateTime.UtcNow.AddMinutes(-1);
            var videos = await Task.Run(() => Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles().Where(f => MediaFiles.KindOf(f.Name) == MediaBrowserKind.Video && f.LastWriteTimeUtc < settled)
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(Math.Max(settings.BrowserIndexMaxFiles, 0)).ToList()
                : [], token);
            if (videos.Count == 0)
                return;

            var shown = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder;
            await WorkScheduler.Run(new WorkSpec($"Preparing {shown}", WorkLane.Idle) { Key = $"watch|{folder}" }, async work =>
            {
                for (var i = 0; i < videos.Count; i++)
                {
                    while (WorkScheduler.IsHeld)
                        await Task.Delay(250, work.Token);

                    work.Report((double)i / videos.Count, $"{i + 1} of {videos.Count} videos");
                    var item = new MediaBrowserItem(videos[i].FullName, MediaBrowserKind.Video, videos[i].Name);
                    await PrepareStripAsync(item, work.Token, WorkLane.Idle);
                    await PrebakeTimelineAsync(item.Path, work.Token);
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Media browser: preparing the watched folder {folder}", ex);
        }
    }
}
