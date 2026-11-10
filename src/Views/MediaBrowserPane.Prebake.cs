using System.IO;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.Views;

// ----- What a video dropped on the Timeline wants, made ahead (Pass 5.4.1-M5) -----
// A folder being indexed (IndexFolderAsync, after the posters on screen) and the watched capture folder (AppSettings.WatchedCaptureFolder,
// a while after start-up) have each video's Timeline work done ahead in quiet time (the Idle lane, Priority 3): the shared N-frame strip
// (the Timeline's filmstrip and hover take it up at once, MediaBrowserFrames), the waveform peaks of each audio stream (one reading of the
// file, Waveforms.BakeAsync) and the seek facts (seekindex.bin, MediaProbe). What the caches hold already is never made again. 5.4.2A: all of it
// to the disk caches only; no picture, peaks or strip of a pre-baked file is kept in memory (the Timeline reads them back when it shows the file).
public partial class MediaBrowserPane
{
    /// <summary>The waveform peaks and the seek facts of one video, made at Idle where the caches do not hold them yet.</summary>
    internal static async Task PrebakeTimelineAsync(string path, CancellationToken token)
    {
        var first = new WaveformRequest([0], 16, 16, "gray");
        if (MediaProbe.Recall(path) && PreviewCache.Timeline.Find(path, first.CacheKind, ".peaks") is not null)
            return;

        if (await MediaProbe.ProbeAsync(path, token, WorkLane.Idle) is not { } info)
            return;

        // The seek facts are written once the probe and the I-frames are both known (MediaProbe.KeepSeekFacts).
        if (info.Video is not null)
            await FfmpegRunner.GetIFramesAsync(path, token);

        var missing = Enumerable.Range(0, info.Audio.Count).Select(i => new WaveformRequest([i], 16, 16, "gray"))
            .Where(r => PreviewCache.Timeline.Find(path, r.CacheKind, ".peaks") is null).ToList();
        if (missing.Count > 0)
            await Waveforms.BakeAsync(path, missing, token);
    }

    /// <summary>
    /// The watched capture folder's videos prepared in quiet time (Pass 5.4.1-M5): their strips, waveforms and seek facts, newest first,
    /// at most Settings' Most Videos per Folder. Not on battery nor on a network drive unless Settings says so, as a folder shown is.
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

            // A video written to in the last minute is a recording still going on (the game being played): left alone.
            var settled = DateTime.UtcNow.AddMinutes(-1);
            var videos = await Task.Run(() => Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles().Where(f => MediaBrowserItem.KindOf(f.Name) == MediaBrowserKind.Video && f.LastWriteTimeUtc < settled)
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
            AppLog.Write($"Media browser: preparing the watched folder {folder}: {ex.Message}");
        }
    }
}
